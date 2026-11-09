#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Formats.Tar;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;
using PMKUnlocker.Qualcomm;
using PMKUnlocker.Spreadtrum;

namespace PMKUnlocker
{
    public partial class Form1
    {
        // su probe — KSU/Magisk/Temp-root ပထမဆုံးခေါ်မှာ daemon start + permission prompt ကြောင့်
        // 15s ထက်ကြာနိုင်လို့ 20s + တစ်ကြိမ် retry (မဟုတ်ရင် false "su not available" ဖြစ်တယ်)
        private async Task<string> ProbeSuAsync(int timeoutMs = 20000, int retries = 1)
        {
            for (int i = 0; ; i++)
            {
                string id = (await ExecuteCommandQuickAsync("adb.exe", "shell su -c id", timeoutMs)).Trim();
                if (!string.IsNullOrWhiteSpace(id) || i >= retries) return id;
                await Task.Delay(700);
            }
        }

        private async Task<string> ExecuteCommandQuickAsync(string fileName, string arguments, int timeoutMs = 15000)
        {
            try { arguments = await BindDeviceArgumentsAsync(fileName, arguments); }
            catch (Exception ex)
            {
                if (flashWorkflowContext.Value) workflowFailed = true;
                if (rebootWaitActive) return "";
                // Temp Root flow: device ပျောက် → reconnect တစ်ကြိမ် စောင့်၊ မရရင် abort (silent)
                if (tempRootFlowActive && ex.Message.IndexOf("not connected", StringComparison.Ordinal) >= 0)
                {
                    if (!await TryRecoverTempRootDeviceAsync()) return "";
                    try { arguments = await BindDeviceArgumentsAsync(fileName, arguments); }
                    catch (Exception ex2)
                    {
                        tempRootAborted = true;
                        Log("[!] " + ex2.Message, Color.Orange);
                        return "";
                    }
                }
                else
                {
                    Log("[!] " + ex.Message, Color.Orange);
                    return "";
                }
            }
            // Normal ROM dump လို QuickAsync သုံးတဲ့ op တွေအတွက်လည်း auto-reboot detect လုပ်
            if (flashWorkflowContext.Value) workflowDidOp = true;
            string result = await ReviewSafety.RunQuickAsync(ResolveToolPath(fileName), arguments, timeoutMs);
            if (flashWorkflowContext.Value && string.IsNullOrWhiteSpace(result)) workflowFailed = true;
            return result;
        }

        // Temp Root flow မှာ payload/exploit run အတွင်း device ပျောက်ရင် တစ်ကြိမ်ပဲ
        // wait-for-device နဲ့ ပြန်စောင့် (kernel panic → reboot ပြန်တက်ချိန် ~60s)။
        // ဒုတိယကြိမ် မပြန်ရင် flow ကို abort — ဆက်ပြီး command spam မလုပ်တော့ဘူး။
        private async Task<bool> TryRecoverTempRootDeviceAsync()
        {
            if (tempRootAborted) return false;
            if (tempRootRecoveryDone)
            {
                tempRootAborted = true;
                Log("[!] Device did not reconnect — Temp Root stopped.", Color.OrangeRed);
                Log("[i] Check cable / USB port / phone power, then run again once the phone responds.", Color.Orange);
                return false;
            }
            tempRootRecoveryDone = true;
            rebootWaitActive = true;
            try
            {
                if (!tempRootReconnectLogged)
                {
                    tempRootReconnectLogged = true;
                    Log("[!] ADB device lost — waiting up to 180s to reconnect (possible reboot)...", Color.Orange);
                }
                await ReviewSafety.RunQuickAsync(ResolveToolPath("adb.exe"), "wait-for-device", 180000);
                string devOut = await ReviewSafety.RunQuickAsync(ResolveToolPath("adb.exe"), "devices", 15000);
                string[] serials = ShopServices.DeviceSerials(devOut, true);
                bool back = serials.Contains(selectedAdbSerial, StringComparer.Ordinal) ||
                            (selectedAdbSerial.Length == 0 && serials.Length > 0);
                if (!back)
                {
                    tempRootAborted = true;
                    Log("[!] Device did not come back after 180s — Temp Root stopped.", Color.OrangeRed);
                    Log("[i] Check cable / USB port / phone power, then run again once the phone responds.", Color.Orange);
                    return false;
                }
                for (int i = 0; i < 30; i++)
                {
                    string bc = (await ExecuteCommandQuickAsync("adb.exe", "shell getprop sys.boot_completed", 5000)).Trim();
                    if (bc.StartsWith("1")) break;
                    await Task.Delay(2000);
                }
                if (tempRootReconnectLogged)
                {
                    tempRootReconnectLogged = false;
                    Log("[i] Device reconnected — continuing.", Color.Cyan);
                }
                return true;
            }
            finally { rebootWaitActive = false; }
        }

        // Main flow (push / E2 verify) ကိုယ်တိုင် device ပြန်ချိတ်ပြီးမှ ဆက်အောင် gate။
        // exception-path recovery နဲ့ မတူဘဲ single-shot flag မသုံး — background recovery
        // ကြားနေရင်လည်း တူညီ wait နဲ့ စောင့်လို့ရတယ် (wait-for-device idempotent)။
        private async Task<bool> EnsureTempRootDeviceConnectedAsync(int waitSec = 180)
        {
            if (tempRootAborted) return false;
            string devOut = await ReviewSafety.RunQuickAsync(ResolveToolPath("adb.exe"), "devices", 15000);
            string[] serials = ShopServices.DeviceSerials(devOut, true);
            if ((selectedAdbSerial.Length > 0 && serials.Contains(selectedAdbSerial, StringComparer.Ordinal)) ||
                (selectedAdbSerial.Length == 0 && serials.Length > 0))
                return true;

            bool prevWait = rebootWaitActive;
            rebootWaitActive = true;
            try
            {
                if (!tempRootReconnectLogged)
                {
                    tempRootReconnectLogged = true;
                    Log("[!] Device not connected — waiting to reconnect (" + waitSec + "s)...", Color.Orange);
                }
                await ReviewSafety.RunQuickAsync(ResolveToolPath("adb.exe"), "wait-for-device", waitSec * 1000);
                string dev2 = await ReviewSafety.RunQuickAsync(ResolveToolPath("adb.exe"), "devices", 15000);
                string[] s2 = ShopServices.DeviceSerials(dev2, true);
                bool back = (selectedAdbSerial.Length > 0 && s2.Contains(selectedAdbSerial, StringComparer.Ordinal)) ||
                            (selectedAdbSerial.Length == 0 && s2.Length > 0);
                if (!back)
                {
                    tempRootAborted = true;
                    Log("[!] Device did not come back after " + waitSec + "s — Temp Root stopped.", Color.OrangeRed);
                    Log("[i] Check cable / USB port / phone power, then run again once the phone responds.", Color.Orange);
                    return false;
                }
                for (int i = 0; i < 30; i++)
                {
                    string bc = (await ExecuteCommandQuickAsync("adb.exe", "shell getprop sys.boot_completed", 5000)).Trim();
                    if (bc.StartsWith("1")) break;
                    await Task.Delay(2000);
                }
                if (tempRootReconnectLogged)
                {
                    tempRootReconnectLogged = false;
                    Log("[i] Device reconnected — continuing.", Color.Cyan);
                }
                return true;
            }
            finally { rebootWaitActive = prevWait; }
        }

        // --- ADB Handlers ---
        private async void BtnAdbInfo_Click(object sender, EventArgs e)
        {
            Log("📱 Reading Device Details...", Color.FromArgb(0, 230, 255));
            string devices = await ExecuteCommandQuickAsync("adb.exe", "devices");

            if (devices.Contains("\tdevice"))
            {
                await LoadDevicePropsAsync();
                string brand = Prop(PropBrandKeys);
                string model = Prop(PropModelKeys);
                string marketName = Prop("ro.product.marketname", "ro.product.odm.marketname", "ro.product.vendor.marketname",
                                         "ro.miui.build.marketname", "ro.product.bootimage.marketname");
                string codename = Prop(PropDeviceKeys);
                string android = Prop("ro.build.version.release", "ro.product.build.version.release");
                string sdk = Prop("ro.build.version.sdk", "ro.product.build.version.sdk");
                string patch = Prop("ro.build.version.security_patch", "ro.product.build.version.security_patch");
                string serial = await ExecuteCommandQuickAsync("adb.exe", "get-serialno");
                string cpuAbi = Prop("ro.product.cpu.abi");
                string soc = Prop(PropSocKeys);
                string board = Prop("ro.board.platform", "ro.hardware");
                string buildId = Prop("ro.build.id", "ro.product.build.id");
                string incremental = Prop("ro.build.version.incremental", "ro.product.build.version.incremental");
                string buildType = Prop("ro.build.type") + (string.IsNullOrWhiteSpace(Prop("ro.build.tags")) ? "" : " (" + Prop("ro.build.tags") + ")");
                string miuiVer = Prop("ro.miui.ui.version.name", "ro.hyperos.ui.version.name", "ro.miui.ui.version.code");
                string region = Prop("ro.miui.region", "ro.miui.build.region", "ro.csc.country_code", "ro.csc.sales_code");
                string locale = Prop("persist.sys.locale", "ro.product.locale");
                string slotSuffix = Prop("ro.boot.slot_suffix", "ro.product.ab_ota_partitions");
                string flashLocked = Prop("ro.boot.flash.locked");
                string vbState = Prop("ro.boot.verifiedbootstate");
                string oemAllowed = Prop("sys.oem_unlock_allowed");
                string oemSupported = Prop("ro.oem_unlock_supported");
                string verity = Prop("ro.boot.veritymode", "ro.boot.veritymode.managed");
                string selinux = Prop("ro.boot.selinux");
                string secureVal = Prop("ro.secure");
                string debuggable = Prop("ro.debuggable");
                string deviceName = (await ExecuteCommandQuickAsync("adb.exe", "shell settings get global device_name")).Trim();

                string displaySize = (await ExecuteCommandQuickAsync("adb.exe", "shell wm size")).Replace("Physical size:", "").Trim();
                string density = (await ExecuteCommandQuickAsync("adb.exe", "shell wm density")).Replace("Physical density:", "").Trim();
                string dfOut = await ExecuteCommandQuickAsync("adb.exe", "shell df -h /data");
                string memOut = await ExecuteCommandQuickAsync("adb.exe", "shell cat /proc/meminfo");
                string suOut = (await ExecuteCommandQuickAsync("adb.exe", "shell which su")).Trim();
                if (string.IsNullOrWhiteSpace(suOut))
                {
                    string tmpSu = (await ExecuteCommandQuickAsync("adb.exe", "shell ls /data/local/tmp/su")).Trim();
                    if (tmpSu.Contains("/data/local/tmp/su")) suOut = "/data/local/tmp/su";
                }
                string enforce = (await ExecuteCommandQuickAsync("adb.exe", "shell getenforce")).Trim();

                // RAM (MemTotal kB → GB)
                string ram = "";
                Match mm = Regex.Match(memOut, @"MemTotal:\s*(\d+)\s*kB");
                if (mm.Success)
                {
                    double gb = double.Parse(mm.Groups[1].Value) / 1048576.0;
                    ram = gb.ToString("F1") + " GB";
                }

                // /data storage (df ရဲ့ ဒုတိယ လိုင်း: total used avail use%)
                string storage = "";
                foreach (string ln in dfOut.Split('\n'))
                {
                    Match dm = Regex.Match(ln, @"^(\S+)\s+(\S+)\s+(\S+)\s+(\S+)\s+(\d+)%");
                    if (dm.Success) { storage = dm.Groups[2].Value + " total, " + dm.Groups[4].Value + " free (" + dm.Groups[5].Value + "% used)"; break; }
                }

                string bootState = "";
                if (!string.IsNullOrWhiteSpace(flashLocked))
                    bootState += "flash.locked=" + flashLocked +
                                 (flashLocked == "0" ? " (bootloader UNLOCKED)" : " (bootloader locked)");
                if (!string.IsNullOrWhiteSpace(vbState)) bootState += (bootState.Length > 0 ? ", " : "") + "verifiedboot=" + vbState;
                if (!string.IsNullOrWhiteSpace(oemAllowed)) bootState += (bootState.Length > 0 ? ", " : "") + "oem_unlock_allowed=" + oemAllowed;
                if (!string.IsNullOrWhiteSpace(oemSupported)) bootState += (bootState.Length > 0 ? ", " : "") + "oem_unlock_supported=" + oemSupported;
                if (!string.IsNullOrWhiteSpace(verity)) bootState += (bootState.Length > 0 ? ", " : "") + "verity=" + verity;

                string modelDisplay = !string.IsNullOrWhiteSpace(marketName) ? model + " (" + marketName + ")" : model;

                Color lbl = Color.FromArgb(130, 145, 170);
                Color idCol = Color.FromArgb(95, 255, 175);
                Color sysCol = Color.FromArgb(85, 205, 255);
                Color hwCol = Color.FromArgb(255, 205, 90);
                Color uniCol = Color.FromArgb(215, 155, 255);
                Color okCol = Color.FromArgb(95, 255, 175);
                Color badCol = Color.OrangeRed;
                Color warnCol = Color.FromArgb(255, 180, 70);

                string selinuxText = string.IsNullOrWhiteSpace(enforce) ? (string.IsNullOrWhiteSpace(selinux) ? "?" : selinux) : enforce;
                string selinuxFull = selinuxText +
                    "  (secure=" + (string.IsNullOrWhiteSpace(secureVal) ? "?" : secureVal) + ", debuggable=" + (string.IsNullOrWhiteSpace(debuggable) ? "?" : debuggable) + ")";
                string rootText = string.IsNullOrWhiteSpace(suOut) ? "not detected (no su)" : "su found: " + suOut;
                bool bootBad = bootState.Contains("UNLOCKED") || bootState.Contains("verifiedboot=orange") || bootState.Contains("verifiedboot=red");

                Log("", Color.FromArgb(0, 110, 170));   // header နဲ့ အောက်စာကြောင်းတွေ ခြားရန်
                LogPair("Brand            : ", brand, lbl, idCol);
                LogPair("Model            : ", modelDisplay, lbl, idCol);
                if (!string.IsNullOrWhiteSpace(deviceName)) LogPair("Device Name      : ", deviceName, lbl, idCol);
                LogPair("Codename / Board : ", codename + " / " + board, lbl, idCol);
                LogPair("Android Ver      : ", "Android " + android + (string.IsNullOrWhiteSpace(sdk) ? "" : " (SDK " + sdk + ")") +
                    (string.IsNullOrWhiteSpace(patch) ? "" : ", patch " + patch), lbl, sysCol);
                if (!string.IsNullOrWhiteSpace(miuiVer) || !string.IsNullOrWhiteSpace(incremental))
                    LogPair("MIUI / Build     : ", (string.IsNullOrWhiteSpace(miuiVer) ? "-" : miuiVer) + "  " + incremental, lbl, sysCol);
                LogPair("Build ID / Type  : ", buildId + " (" + buildType + ")", lbl, sysCol);
                LogPair("SoC / CPU Arch   : ", soc + " / " + cpuAbi, lbl, sysCol);
                // MTK ဆို ဒီ SoC token ကနေ DA catalog ကို ကြိုတင် ချိတ် — BROM session မှာ CPU: မဖတ်ရတော့ဘူး
                NoteMediaTekChipFromAdb(soc, board);
                LogPair("Region / Locale  : ", (string.IsNullOrWhiteSpace(region) ? "-" : region) + " / " + (string.IsNullOrWhiteSpace(locale) ? "-" : locale), lbl, sysCol);
                LogPair("RAM              : ", string.IsNullOrWhiteSpace(ram) ? "-" : ram, lbl, hwCol);
                LogPair("Storage /data    : ", string.IsNullOrWhiteSpace(storage) ? "-" : storage, lbl, hwCol);
                LogPair("Screen / Density : ", displaySize + (string.IsNullOrWhiteSpace(density) ? "" : " @ " + density + "dpi"), lbl, hwCol);
                LogPair("Serial Number    : ", serial, lbl, uniCol);
                if (!string.IsNullOrWhiteSpace(slotSuffix)) LogPair("A/B / Slots      : ", slotSuffix, lbl, uniCol);
                if (bootState.Length > 0) LogPair("Bootloader State : ", bootState, lbl, bootBad ? badCol : okCol);
                LogPair("SELinux          : ", selinuxFull, lbl, selinuxText == "?" ? Color.Gray : (selinuxText.Contains("Permissive") ? badCol : okCol));
                LogPair("Root             : ", rootText, lbl, string.IsNullOrWhiteSpace(suOut) ? warnCol : okCol);
                Log("");   // နောက်လာမယ့် output နဲ့ ခြားရန်
            }
            else
            {
                Log("Device not found or unauthorized!", Color.Red);
            }
        }

        private async void BtnAdbBatteryInfo_Click(object sender, EventArgs e)
        {
            await ExecuteCommandCleanAsync("adb.exe", "shell dumpsys battery", "Battery Info", false, true);
        }

        // Header battery badge — ADB ရှိရင် level % ကို poll နဲ့ ပြ
        private async Task RefreshBatteryHeaderAsync(bool connected)
        {
            if (lblBattery == null) return;
            if (!connected)
            {
                lblBattery.Text = "🔋 —";
                lblBattery.ForeColor = Color.FromArgb(180, 195, 215);
                return;
            }
            try
            {
                string dump = await ExecuteCommandQuickAsync("adb.exe", "shell dumpsys battery");
                Match m = Regex.Match(dump ?? "", @"level:\s*(\d+)");
                if (!m.Success)
                {
                    lblBattery.Text = "🔋 —";
                    lblBattery.ForeColor = Color.FromArgb(180, 195, 215);
                    return;
                }
                int level = int.Parse(m.Groups[1].Value);
                lblBattery.Text = "🔋 " + level + "%";
                if (level <= 15) lblBattery.ForeColor = Color.OrangeRed;
                else if (level <= 30) lblBattery.ForeColor = Color.Orange;
                else lblBattery.ForeColor = Color.LightGreen;
            }
            catch
            {
                lblBattery.Text = "🔋 —";
            }
        }

        // Pattern / PIN / password ဖျက် — root ရှိရင် locksettings + gatekeeper files ဖျက်။
        // root မရှိရင် dismiss-keyguard / locksettings စမ်းပြီး မရရင် Factory Reset / MTK Userlock လမ်း ညွှန်။
        private async void BtnAdbRemoveLock_Click(object sender, EventArgs e)
        {
            string dv = await ExecuteCommandQuickAsync("adb.exe", "devices");
            if (string.IsNullOrWhiteSpace(dv) || !dv.Contains("\tdevice"))
            {
                MessageBox.Show("ADB device not found." + Environment.NewLine + Environment.NewLine
                                + "- USB debugging ဖွင့်ပြီး cable ချိတ်ပါ" + Environment.NewLine
                                + "- RSA prompt ကို Allow ပါ",
                    "Remove Pattern/PIN", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (MessageBox.Show(
                    "Remove screen lock (pattern / PIN / password)?" + Environment.NewLine + Environment.NewLine +
                    "• ROOT ရှိရင် — locksettings + gatekeeper files ဖျက် (data မပျက်)" + Environment.NewLine +
                    "• ROOT မရှိရင် — best-effort ADB commands စမ်းမယ်" + Environment.NewLine +
                    "• မရရင် — Factory Reset (ADB) / MTK Userlock Reset သုံးပါ" + Environment.NewLine + Environment.NewLine +
                    "ဖုန်းပိုင်ရှင် ခွင့်ပြုချက် ရှိမှ လုပ်ပါ",
                    "Confirm Remove Lock", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("   REMOVE PATTERN / PIN / PASSWORD", Color.White);
            Log("========================================================", Color.FromArgb(0, 180, 255));

            string id = (await ProbeSuAsync()).Trim();
            bool hasRoot = id.Contains("uid=0");

            if (hasRoot)
            {
                Log("[*] Root detected — clearing lock settings files...", Color.Cyan);
                string[] rmCmds =
                {
                    "rm -f /data/system/locksettings.db /data/system/locksettings.db-wal /data/system/locksettings.db-shm",
                    "rm -f /data/system/users/0/locksettings.db /data/system/users/0/locksettings.db-wal /data/system/users/0/locksettings.db-shm",
                    "rm -f /data/system/gatekeeper.password.key /data/system/gatekeeper.pattern.key",
                    "rm -f /data/system/password.key /data/system/gesture.key /data/system/locksettings.dat",
                    "rm -rf /data/system/locksettings*"
                };
                int ok = 0;
                foreach (string c in rmCmds)
                {
                    if (await ExecuteCommandCleanAsync("adb.exe", "shell su -c \"" + c + "\"", "Remove lock file", false, true))
                        ok++;
                }
                await ExecuteCommandQuickAsync("adb.exe", "shell su -c \"setprop persist.sys.usb.config mtp,adb\"");
                Log("[OK] Root lock clear: " + ok + " step(s). Reboot the phone and check the screen lock.", Color.LightGreen);
                if (MessageBox.Show("Reboot now to apply?", "Reboot", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    await ExecuteCommandCleanAsync("adb.exe", "reboot", "Reboot after lock clear");
                return;
            }

            Log("[i] No root — trying non-root lock commands (may fail on modern Android)...", Color.Orange);
            // Android 8-10 best-effort (usually needs root on newer builds)
            await ExecuteCommandQuickAsync("adb.exe", "shell locksettings clear --old \"\"");
            await ExecuteCommandQuickAsync("adb.exe", "shell wm dismiss-keyguard");
            await ExecuteCommandQuickAsync("adb.exe", "shell input keyevent 82");

            string check = await ExecuteCommandQuickAsync("adb.exe", "shell locksettings get-disabled");
            Log("[i] locksettings get-disabled: " + (string.IsNullOrWhiteSpace(check) ? "(empty)" : check.Trim()), Color.Gray);
            Log("[!] Non-root remove is not reliable on Android 11+.", Color.Orange);
            Log("[i] Fallback: use 🗑 Factory Reset (ADB) or MTK tab → 🔑 Userlock Reset (wipes data).", Color.Orange);
        }

        // ROOT partition list + RW remount — root ရှိရင် by-name list ပြ၊ ရွေးတဲ့ partition ကို mount -o remount,rw
        private async void BtnAdbPartitions_Click(object sender, EventArgs e)
        {
            string dv = await ExecuteCommandQuickAsync("adb.exe", "devices");
            if (string.IsNullOrWhiteSpace(dv) || !dv.Contains("\tdevice"))
            {
                MessageBox.Show("ADB device not found." + Environment.NewLine + Environment.NewLine
                                + "- USB debugging ဖွင့်ပြီး cable ချိတ်ပါ" + Environment.NewLine
                                + "- RSA prompt ကို Allow ပါ",
                    "Partitions (ROOT)", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string id = (await ProbeSuAsync()).Trim();
            if (!id.Contains("uid=0"))
            {
                MessageBox.Show("Root (su) မရပါ။" + Environment.NewLine + Environment.NewLine
                                + "• Xiaomi Temp Root / Magisk / KernelSU ရအောင်လုပ်ပါ" + Environment.NewLine
                                + "• ဖုန်း screen က su prompt Allow မလုပ်ရသေးရင် လုပ်ပါ",
                    "Partitions (ROOT)", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Log("[!] Partitions: su not available (" + (string.IsNullOrWhiteSpace(id) ? "no output" : id) + ")", Color.OrangeRed);
                return;
            }

            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("   ROOT PARTITIONS (by-name)", Color.White);
            Log("========================================================", Color.FromArgb(0, 180, 255));

            // One push + one run — names/sizes/mounts/dm (was: N+ adb calls per dm-* → slow)
            // size ယူရာမှာ blockdev/basename/readlink ကို partition တိုင်းမှာ fork လုပ် → 18s+
            // ကြာခဲ့တယ်; ls -l + shell builtin read/sysfs နဲ့ ဖြစ်လို့ fork မလုပ်တော့ဘူး (<1s)
            string sizeSh =
                "echo '##NAMES'\n" +
                "ls -1 /dev/block/by-name 2>/dev/null\n" +
                "echo '##SIZES'\n" +
                "ls -l /dev/block/by-name 2>/dev/null | while read -r line; do\n" +
                "  case \"$line\" in *' -> '*) ;; *) continue ;; esac\n" +
                "  lhs=${line% -> *}\n" +
                "  name=${lhs##* }\n" +   // ls -l <dir> = path မပါ → နောက်ဆုံး token က partition name
                "  rhs=${line#* -> }\n" +
                "  tgt=${rhs##*/}\n" +
                "  sz=0\n" +
                "  if [ -r \"/sys/class/block/$tgt/size\" ]; then\n" +
                "    read sec < \"/sys/class/block/$tgt/size\"\n" +
                "    sz=$((sec * 512))\n" +
                "  fi\n" +
                "  if [ \"$sz\" -eq 0 ]; then\n" +
                "    b=$(blockdev --getsize64 \"/dev/block/by-name/$name\" 2>/dev/null)\n" +
                "    case \"$b\" in ''|*[!0-9]*) sz=0 ;; *) sz=$b ;; esac\n" +
                "  fi\n" +
                "  printf '%s|%s\\n' \"$name\" \"$sz\"\n" +
                "done\n" +
                "echo '##MOUNTS'\n" +
                "cat /proc/mounts\n" +
                "echo '##DM'\n" +
                "for b in /sys/block/dm-*; do\n" +
                "  [ -d \"$b\" ] || continue\n" +
                "  dm=${b##*/}\n" +
                "  nm=''\n" +
                "  [ -r \"$b/dm/name\" ] && read nm < \"$b/dm/name\"\n" +
                "  sec=0\n" +
                "  [ -r \"$b/size\" ] && read sec < \"$b/size\"\n" +
                "  sz=$((sec * 512))\n" +
                "  printf '%s|%s|%s\\n' \"$dm\" \"$nm\" \"$sz\"\n" +
                "done\n";
            string tmpSh = Path.Combine(Path.GetTempPath(), "pmk_sizes.sh");
            File.WriteAllText(tmpSh, sizeSh.Replace("\r\n", "\n"));
            await ExecuteCommandQuickAsync("adb.exe", "push \"" + tmpSh + "\" /data/local/tmp/pmk_sizes.sh");
            // script = ~16s (blockdev 150+ partition) — default 15s timeout ထိ → blob ကွက်
            string blob = (await ExecuteCommandQuickAsync("adb.exe",
                "shell su -c \"sh /data/local/tmp/pmk_sizes.sh\"", 60000)).Trim();

            var namesList = new List<string>();
            var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var mountByDev = new Dictionary<string, (string Mp, bool Ro)>(StringComparer.OrdinalIgnoreCase);
            var mountByName = new Dictionary<string, (string Mp, bool Ro)>(StringComparer.OrdinalIgnoreCase);
            var mountEntries = new List<(string Dev, string Mp, bool Ro)>();
            var dmByName = new Dictionary<string, (string Dev, string Name, long Sz)>(StringComparer.OrdinalIgnoreCase);
            var dmByDevBn = new Dictionary<string, (string Name, long Sz)>(StringComparer.OrdinalIgnoreCase);

            string section = "";
            foreach (string rawLine in blob.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.TrimEnd('\r');
                if (line.StartsWith("##", StringComparison.Ordinal))
                {
                    section = line.TrimStart('#');
                    continue;
                }
                if (section == "NAMES")
                {
                    if (!string.IsNullOrWhiteSpace(line) && !line.StartsWith("total ", StringComparison.OrdinalIgnoreCase))
                        namesList.Add(line.Trim());
                }
                else if (section == "SIZES")
                {
                    int bar = line.LastIndexOf('|');
                    if (bar <= 0) continue;
                    string n = line.Substring(0, bar).Trim();
                    long.TryParse(line.Substring(bar + 1).Trim(), out long b);
                    if (!string.IsNullOrEmpty(n)) sizes[n] = b;
                }
                else if (section == "MOUNTS")
                {
                    string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 4) continue;
                    string dev = parts[0];
                    string mp = parts[1];
                    string opts = parts[3];
                    bool ro = opts.Split(',').Any(o => o.Equals("ro", StringComparison.OrdinalIgnoreCase));
                    mountByDev[dev] = (mp, ro);
                    mountEntries.Add((dev, mp, ro));
                    string bn = Path.GetFileName(dev.TrimEnd('/'));
                    if (!string.IsNullOrEmpty(bn)) mountByName[bn] = (mp, ro);
                    if (mp != "/")
                    {
                        string mpn = Path.GetFileName(mp.TrimEnd('/'));
                        if (!string.IsNullOrEmpty(mpn) && !mountByName.ContainsKey(mpn))
                            mountByName[mpn] = (mp, ro);
                    }
                }
                else if (section == "DM")
                {
                    // dm-16|system-verity|bytes
                    string[] pp = line.Split('|');
                    if (pp.Length < 3) continue;
                    string dmBn = pp[0].Trim();
                    string dmName = pp[1].Trim();
                    long.TryParse(pp[2].Trim(), out long dmSz);
                    if (string.IsNullOrEmpty(dmName)) continue;
                    string key = dmName;
                    if (key.EndsWith("-verity", StringComparison.OrdinalIgnoreCase))
                        key = key.Substring(0, key.Length - "-verity".Length);
                    if (!string.IsNullOrEmpty(key))
                    {
                        dmByDevBn[dmBn] = (key, dmSz);
                        if (!dmByName.ContainsKey(key))
                            dmByName[key] = (dmBn, dmName, dmSz);
                        if (!mountByName.ContainsKey(key) && mountByDev.TryGetValue("/dev/block/" + dmBn, out var mOnDm))
                            mountByName[key] = mOnDm;
                        // mp basename already added; ensure key maps mount
                        foreach (var (dev, mp, ro) in mountEntries)
                        {
                            if (Path.GetFileName(dev.TrimEnd('/')).Equals(dmBn, StringComparison.OrdinalIgnoreCase))
                            {
                                mountByName[key] = (mp, ro);
                                break;
                            }
                        }
                    }
                }
            }

            string[] names = namesList.ToArray();
            if (names.Length == 0)
            {
                Log("[i] script blob bytes=" + blob.Length +
                    (blob.Length > 0 ? "  head=" + blob.Substring(0, Math.Min(140, blob.Length)).Replace("\n", " ") : "  (empty — timeout/exit≠0/adb error)"), Color.Gray);
                Log("[!] /dev/block/by-name not found — device may not expose by-name symlinks.", Color.Orange);
                MessageBox.Show("/dev/block/by-name မရှိပါ (by-name symlink မပေးတဲ့ device)။",
                    "Partitions (ROOT)", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var byNameSet = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            var extraMounts = new List<(string Name, string Dev, string Mp, bool Ro, long Sz)>();
            foreach (var (dev, mp, ro) in mountEntries)
            {
                if (mp == "/" || mp.StartsWith("/dev", StringComparison.Ordinal) ||
                    mp.StartsWith("/sys", StringComparison.Ordinal) || mp.StartsWith("/proc", StringComparison.Ordinal) ||
                    mp.StartsWith("/apex", StringComparison.Ordinal) || mp.StartsWith("/mnt/installer", StringComparison.Ordinal) ||
                    mp.StartsWith("/linkerconfig", StringComparison.Ordinal) ||
                    mp.StartsWith("/system_dlkm", StringComparison.Ordinal))
                    continue;
                string bn = Path.GetFileName(dev.TrimEnd('/'));
                string mName = "";
                long bytes = 0;
                if (dev.StartsWith("/dev/block/by-name/", StringComparison.Ordinal))
                {
                    mName = Path.GetFileName(dev);
                    if (byNameSet.Contains(mName)) continue;
                }
                else if (bn.StartsWith("dm-", StringComparison.OrdinalIgnoreCase))
                {
                    if (dmByDevBn.TryGetValue(bn, out var dmInfo)) mName = dmInfo.Name;
                    if (string.IsNullOrEmpty(mName)) mName = Path.GetFileName(mp.TrimEnd('/'));
                    if (byNameSet.Contains(mName)) continue;
                    bytes = dmByDevBn.TryGetValue(bn, out var dmSz2) ? dmSz2.Sz : 0;
                }
                else
                {
                    continue;
                }
                if (string.IsNullOrEmpty(mName)) continue;
                if (extraMounts.Any(x => x.Name.Equals(mName, StringComparison.OrdinalIgnoreCase))) continue;
                extraMounts.Add((mName, dev, mp, ro, bytes));
                if (bytes > 0 && !sizes.ContainsKey(mName)) sizes[mName] = bytes;
            }

            Log("[i] " + names.Length + " partition(s) in /dev/block/by-name", Color.Cyan);
            if (extraMounts.Count > 0)
                Log("[i] +" + extraMounts.Count + " mounted fs not in by-name (dm/super)", Color.Cyan);

            // Fill MAIN partition grid (not a dialog) — right-click → Mount RW via ctxPartitionMenu
            if (tabControl.SelectedTab != tabAdb) tabControl.SelectedTab = tabAdb;
            ClearPartitionData();
            foreach (string name in names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                long bytes = sizes.TryGetValue(name, out long b) ? b : 0;
                string devPath = "/dev/block/by-name/" + name;
                string mp = "";
                string mode = "—";
                if (mountByName.TryGetValue(name, out var mnt))
                {
                    mp = mnt.Mp;
                    mode = mnt.Ro ? "ro" : "rw";
                }
                else if (mountByDev.TryGetValue(devPath, out var mnt2))
                {
                    mp = mnt2.Mp;
                    mode = mnt2.Ro ? "ro" : "rw";
                }
                dgvPartitions.Rows.Add(false, name, FormatBytesLong(bytes), devPath, mp, mode);
            }
            foreach (var ex in extraMounts.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                long bytes = sizes.TryGetValue(ex.Name, out long b) ? b : ex.Sz;
                dgvPartitions.Rows.Add(false, ex.Name, FormatBytesLong(bytes), ex.Dev, ex.Mp, ex.Ro ? "ro" : "rw");
            }
            SyncPartitionPanel(false);   // ADB ROOT grid ပြည့်ပြီ — partition row အလိုအလျောက် ချိန်
            Log("[OK] " + dgvPartitions.RowCount + " partition(s) in grid — right-click → Mount RW", Color.LightGreen);
            Log("[i] Right-click mounted rows (system/vendor…) → 🔓 Mount RW", Color.Gray);
        }

        // Factory Reset — ADB ကန် recovery wipe / MASTER_CLEAR ကြိုးစား။ မရရင် recovery ပို့ပြီး လက်နှိပ်ရန် ညွှန်။
        private async void BtnAdbFactoryReset_Click(object sender, EventArgs e)
        {
            string dv = await ExecuteCommandQuickAsync("adb.exe", "devices");
            if (string.IsNullOrWhiteSpace(dv) || !dv.Contains("\tdevice"))
            {
                MessageBox.Show("ADB device not found." + Environment.NewLine + Environment.NewLine
                                + "- USB debugging ဖွင့်ပြီး cable ချိတ်ပါ" + Environment.NewLine
                                + "- RSA prompt ကို Allow ပါ",
                    "Factory Reset", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (MessageBox.Show(
                    "Factory Reset via ADB?" + Environment.NewLine + Environment.NewLine +
                    "• ဖုန်းထဲက data / ဓာတ်ပုံ / အက်ပ် အားလုံး ဖျက်ခံရမယ်" + Environment.NewLine +
                    "• Pattern/PIN နဲ့ Google account (FRP) ပါ ပျက်သွားနိုင်တယ်" + Environment.NewLine +
                    "• ဖုန်းပိုင်ရှင် ခွင့်ပြုချက် ရှိမှ လုပ်ပါ" + Environment.NewLine + Environment.NewLine +
                    "ဆက်လုပ်မလား?",
                    "Confirm Factory Reset", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("   FACTORY RESET (ADB)", Color.White);
            Log("========================================================", Color.FromArgb(0, 180, 255));

            await RunAdbFactoryResetCoreAsync();
        }

        // ADB device ချိတ်ထားပြီးသားလို့ ယူဆပြီး wipe လုပ် — MTP Factory Reset ကနေလည်း ပြန်ခေါ်သည်
        private async Task RunAdbFactoryResetCoreAsync()
        {
            // 1) root → wipe data via recovery command / rm (စမ်း)
            string id = (await ProbeSuAsync()).Trim();
            if (id.Contains("uid=0"))
            {
                Log("[*] Root detected — attempting data wipe...", Color.Cyan);
                // Common root paths used by tech tools (not all devices expose these)
                await ExecuteCommandQuickAsync("adb.exe", "shell su -c \"recovery --wipe_data\"");
                await ExecuteCommandQuickAsync("adb.exe", "shell su -c \"am broadcast -a android.intent.action.MASTER_CLEAR\"");
            }

            // 2) non-root — recovery --wipe_data / masterclear (device-dependent)
            Log("[*] Trying recovery --wipe_data ...", Color.Cyan);
            string wipe = await ExecuteCommandQuickAsync("adb.exe", "shell recovery --wipe_data");
            if (!string.IsNullOrWhiteSpace(wipe) &&
                (wipe.IndexOf("OK", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 wipe.IndexOf("Success", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                Log("[OK] recovery --wipe_data accepted — phone should wipe and reboot.", Color.LightGreen);
                return;
            }

            Log("[*] Trying MASTER_CLEAR broadcast ...", Color.Cyan);
            await ExecuteCommandQuickAsync("adb.exe",
                "shell am broadcast -a android.intent.action.MASTER_CLEAR -f 0x01000000");

            // 3) fallback — reboot recovery + ညွှန်ကြားချက်
            Log("[i] Direct wipe not confirmed — rebooting to recovery for manual wipe...", Color.Orange);
            await ExecuteCommandCleanAsync("adb.exe", "reboot recovery", "Reboot Recovery (manual wipe)");
            MessageBox.Show(
                "Phone ကို recovery mode ပို့ပြီးပါပြီ။" + Environment.NewLine + Environment.NewLine +
                "Recovery ထဲမှာ:" + Environment.NewLine +
                "  1. Wipe data / Factory reset ရွေးပါ" + Environment.NewLine +
                "  2. Cache partition wipe ရွေးပါ (optional)" + Environment.NewLine +
                "  3. Reboot system now" + Environment.NewLine + Environment.NewLine +
                "Menu မပေါ်ရင်: Vol+ / Vol- နဲ့ ရွေး၊ Power နဲ့ အတည်ပြုပါ။",
                "Factory Reset — Recovery", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Log("[i] Manual recovery steps shown in dialog.", Color.Cyan);
        }

        private async void BtnAdbScreenshot_Click(object sender, EventArgs e)
        {
            Log("Capturing Screenshot...", Color.Cyan);
            string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string filePath = Path.Combine(desktopPath, "screenshot_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");

            await ExecuteCommandQuickAsync("adb.exe", "shell screencap -p /sdcard/temp_screen.png");
            await ExecuteCommandQuickAsync("adb.exe", "pull /sdcard/temp_screen.png \"" + filePath + "\"");
            await ExecuteCommandQuickAsync("adb.exe", "shell rm /sdcard/temp_screen.png");

            if (File.Exists(filePath))
            {
                Log("[✔] Screenshot saved: " + filePath, Color.LightGreen);
                Process.Start(new ProcessStartInfo { FileName = filePath, UseShellExecute = true });
            }
        }

        private async void BtnAdbListApps_Click(object sender, EventArgs e)
        {
            await ExecuteCommandCleanAsync("adb.exe", "shell pm list packages -3", "List 3rd-Party Apps", false, true);
        }

        private async void BtnAdbInstallApk_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog ofd = new OpenFileDialog { Filter = "Android App Package (*.apk)|*.apk" })
            {
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    await ExecuteCommandCleanAsync("adb.exe", "install -r \"" + ofd.FileName + "\"", "Install " + Path.GetFileName(ofd.FileName));
                }
            }
        }

        private async void BtnAdbRebootExecute_Click(object sender, EventArgs e)
        {
            switch (cmbAdbReboot.SelectedIndex)
            {
                case 0: Log("Rebooting System...", Color.Cyan); await ExecuteCommandQuickAsync("adb.exe", "reboot"); break;
                case 1: Log("Rebooting Recovery...", Color.Cyan); await ExecuteCommandQuickAsync("adb.exe", "reboot recovery"); break;
                case 2: Log("Rebooting Bootloader...", Color.Cyan); await ExecuteCommandQuickAsync("adb.exe", "reboot bootloader"); break;
                case 3: Log("Rebooting EDL...", Color.Cyan); await ExecuteCommandQuickAsync("adb.exe", "reboot edl"); break;
                case 4: Log("Rebooting Safe Mode...", Color.Cyan); await ExecuteCommandQuickAsync("adb.exe", "reboot safe_mode"); break;
                case 5: Log("Powering Off...", Color.Cyan); await ExecuteCommandQuickAsync("adb.exe", "shell reboot -p"); break;
            }
            Log("Done.", Color.LightGreen);
        }

        // --- Fastboot Handlers ---

    }
}
