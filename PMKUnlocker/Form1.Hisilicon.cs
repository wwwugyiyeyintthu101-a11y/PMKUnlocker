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
        private string HuaweiDriversRoot =>
            Path.Combine(Application.StartupPath, "huawei-drivers");

        private string HuaweiDriverInfDir => Path.Combine(HuaweiDriversRoot, "Driver");

        private void BtnHisiInstallDrivers_Click(object sender, EventArgs e)
        {
            RunHuaweiDriverPnpAsync(install: true, "Install HUAWEI USB COM / ADB / VCOM drivers");
        }

        private void BtnHisiUninstallDrivers_Click(object sender, EventArgs e)
        {
            RunHuaweiDriverPnpAsync(install: false, "Remove previously installed HUAWEI driver packages");
        }

        private async void RunHuaweiDriverPnpAsync(bool install, string taskTitle)
        {
            string infDir = HuaweiDriverInfDir;
            if (!Directory.Exists(infDir))
            {
                Log("[FAIL] huawei-drivers pack not found: " + infDir, Color.Red);
                return;
            }

            int infCount;
            try { infCount = Directory.GetFiles(infDir, "*.inf", SearchOption.AllDirectories).Length; }
            catch { infCount = 0; }
            if (infCount == 0)
            {
                Log("[FAIL] No .inf files under " + infDir, Color.Red);
                return;
            }

            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("   " + taskTitle.ToUpper(), Color.White);
            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("[*] Pack: " + infDir + "  (" + infCount + " INF, arch X64+X86)", Color.Orange);

            // DriverSetup.exe (Huawei vendor) self-elevates + reads install.xml; fall back to pnputil
            string setupExe = Path.Combine(HuaweiDriversRoot, install ? "DriverSetup.exe" : "DriverUninstall.exe");
            ToolIntegrity.Verify(setupExe);
            bool usedVendor = false;
            if (File.Exists(setupExe))
            {
                try
                {
                    var vendor = new ProcessStartInfo
                    {
                        FileName = setupExe,
                        WorkingDirectory = HuaweiDriversRoot,
                        UseShellExecute = true
                    };
                    using var vp = Process.Start(vendor);
                    if (vp != null)
                    {
                        // vendor setup hang ရင် 600s ပြီး kill (မဟုတ်ရင် fallback pnputil ကို စောင်းနေမယ်)
                        bool vendorDone = true;
                        using (var vCts = new CancellationTokenSource(TimeSpan.FromSeconds(600)))
                        {
                            try { await vp.WaitForExitAsync(vCts.Token); }
                            catch (OperationCanceledException)
                            {
                                vendorDone = false;
                                try { vp.Kill(entireProcessTree: true); } catch { }
                                Log("[!] " + Path.GetFileName(setupExe) + " — 600s timeout, killed — pnputil fallback", Color.Orange);
                            }
                        }
                        if (vendorDone)
                        {
                            usedVendor = true;
                            Log("[i] " + Path.GetFileName(setupExe) + " exit=" + vp.ExitCode, Color.Gray);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log("[!] " + Path.GetFileName(setupExe) + ": " + ex.Message + " — pnputil fallback", Color.Orange);
                }
            }

            // Always also push INFs via pnputil (covers cases where vendor EXE needs UI/clicks)
            string args = install
                ? "/add-driver \"" + Path.Combine(infDir, "*.inf") + "\" /subdirs /install"
                : "/delete-driver oem*.inf /uninstall /force";
            if (!install)
            {
                // /delete-driver needs published name (oemNN.inf) — list Huawei packages first via PowerShell
                args = "-NoProfile -Command \"" +
                    "Get-WindowsDriver -Online -ErrorAction SilentlyContinue | " +
                    "Where-Object { $_.ProviderName -match 'Huawei|HiSilicon|HUAWEI' } | " +
                    "ForEach-Object { pnputil /delete-driver $_.Driver /uninstall /force }\"";
                await ExecuteCommandCleanAsync("powershell.exe", args, taskTitle, false, true);
            }
            else
            {
                // ResolveToolPath PATH search က System32 တစ်ခါတည်း မရှာနိုင် — full path သုံး
                string pnp = Path.Combine(Environment.SystemDirectory, "pnputil.exe");
                if (!File.Exists(pnp)) pnp = "pnputil.exe";
                await ExecuteCommandCleanAsync(pnp, args, taskTitle, false, true);
            }

            if (usedVendor)
                Log("[OK] Driver pack processed — replug phone (HUAWEI USB COM 1.0) then refresh COM ports.", Color.LightGreen);
            RefreshHisiPorts();
        }

        // OPEN HUAWEI 2018 `open hw.BAT` port: adb restart + user_setup_complete content insert
        private async void BtnHisiEnableAdb_Click(object sender, EventArgs e)
        {
            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("   HISILICON - ENABLE ADB (OPEN HUAWEI SEQUENCE)", Color.White);
            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("[*] Step 1/4: adb kill-server", Color.Orange);
            await ExecuteCommandQuickAsync("adb.exe", "kill-server");

            Log("[*] Step 2/4: adb start-server + wait-for-device", Color.Orange);
            await ExecuteCommandCleanAsync("adb.exe", "start-server", "HiSilicon - Enable ADB", false, true, quiet: true);
            await ExecuteCommandCleanAsync("adb.exe", "wait-for-device", "HiSilicon - Enable ADB", false, true, quiet: true, timeoutSec: 180);

            string devices = await ExecuteCommandQuickAsync("adb.exe", "devices");
            if (!(devices ?? "").Contains("\tdevice"))
            {
                Log("[FAIL] No ADB device. Install drivers first, enable USB debugging / use testpoint, then retry.", Color.Red);
                Log("       adb devices →", Color.Gray);
                foreach (string ln in (devices ?? "").Split('\n'))
                    if (ln.Trim().Length > 0) Log("       " + ln.TrimEnd(), Color.Gray);
                return;
            }

            Log("[*] Step 3/4: diagnostics (cpuinfo / platform / lsmod)", Color.Orange);
            string cpu = await ExecuteCommandQuickAsync("adb.exe", "shell cat /proc/cpuinfo");
            foreach (string ln in (cpu ?? "").Split('\n').Take(6))
                if (ln.Trim().Length > 0) Log("       " + ln.TrimEnd(), Color.Gray);
            string manuf = (await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.product.manufacturer")).Trim();
            string board = (await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.board.platform")).Trim();
            string hw = (await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.hardware")).Trim();
            Log("[i] manufacturer=" + (manuf == "" ? "?" : manuf) +
                "  board=" + (board == "" ? "?" : board) +
                "  hw=" + (hw == "" ? "?" : hw),
                (manuf.IndexOf("huawei", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 board.IndexOf("kirin", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 hw.IndexOf("kirin", StringComparison.OrdinalIgnoreCase) >= 0)
                    ? Color.LightGreen : Color.Orange);
            if (manuf.IndexOf("huawei", StringComparison.OrdinalIgnoreCase) < 0 &&
                board.IndexOf("kirin", StringComparison.OrdinalIgnoreCase) < 0)
                Log("[!] Not a Kirin/Huawei board — sequence still runs (generic ADB path).", Color.Orange);
            string lsmod = await ExecuteCommandQuickAsync("adb.exe", "shell lsmod");
            foreach (string ln in (lsmod ?? "").Split('\n').Take(8))
                if (ln.Trim().Length > 0) Log("       " + ln.TrimEnd(), Color.Gray);
            string nand = await ExecuteCommandQuickAsync("adb.exe", "shell ls /dev/block/nand*");
            foreach (string ln in (nand ?? "").Split('\n').Take(8))
                if (ln.Trim().Length > 0) Log("       " + ln.TrimEnd(), Color.Gray);

            Log("[*] Step 4/4: content insert user_setup_complete=1", Color.Orange);
            // content insert က WRITE_SECURE_SETTINGS လို — shell မှာ မရှိရင် SecurityException
            // (Android 7+ မှာ ပုံမှန်)။ settings put က user 0 shell နဲ့ ရတယ် / ရှိပြီးသားဆို 1 ပြတယ်။
            string insertOut = await ExecuteCommandQuickAsync("adb.exe",
                "shell content insert --uri content://settings/secure --bind name:s:user_setup_complete --bind value:s:1");
            bool insertBlocked = (insertOut ?? "").IndexOf("SecurityException", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 (insertOut ?? "").IndexOf("WRITE_SECURE_SETTINGS", StringComparison.OrdinalIgnoreCase) >= 0;
            if (insertBlocked)
                Log("[i] content insert blocked (WRITE_SECURE_SETTINGS) — normal on Android 7+, using settings put.", Color.Gray);

            // Fallback / primary path (open-hw + FRP)
            await ExecuteCommandQuickAsync("adb.exe", "shell settings put secure user_setup_complete 1");
            await ExecuteCommandQuickAsync("adb.exe", "shell settings put global device_provisioned 1");

            string verify = (await ExecuteCommandQuickAsync("adb.exe", "shell settings get secure user_setup_complete")).Trim();
            string prov = (await ExecuteCommandQuickAsync("adb.exe", "shell settings get global device_provisioned")).Trim();
            if (verify == "1")
                Log("[OK] user_setup_complete=1 (verify) · device_provisioned=" + (prov == "" ? "?" : prov) +
                    ". Continue to FRP Reset / setup wizard skip.", Color.LightGreen);
            else if (!insertBlocked && insertOut != null && insertOut.Trim().Length == 0)
                Log("[OK] content insert accepted (no error). Continue to FRP Reset.", Color.LightGreen);
            else
                Log("[!] user_setup_complete verify='" + verify + "' — enable USB debugging auth on phone, then retry.", Color.Orange);

            RefreshHisiPorts();
        }

        // ================= Samsung MTP (File Transfer mode — USB debugging မဖွင့်) =================
        // PnP/registry ကနေ VID_04E8 device တွေ ရှာပြီး MTP-related PID/service စစ်တယ်။
        // MTP FRP က *#0*# test mode ကတဆင့် ADB enable ဖြစ်လာအောင် စောင့်ပြီး ADB FRP ဆက်တယ်။
        private async void BtnHisiOemUnlock_Click(object sender, EventArgs e)
        {
            string code = PromptInput("Huawei OEM Unlock",
                "PotatoNV က ရလာတဲ့ unlock code ကို paste ပါ။\n(ဖုန်းက fastboot mode မှာ ရှိရမယ် — ⚡ Reboot Fastboot နဲ့ ရောက်နိုင်တယ်)");
            if (string.IsNullOrWhiteSpace(code)) return;

            code = code.Trim();
            if (!IsValidPartitionName(code))
            {
                Log("[x] Unlock code contains invalid characters - allowed: a-z, 0-9, _ , -", Color.Red);
                return;
            }
            if (MessageBox.Show(
                    "fastboot oem unlock " + code + "\n\n" +
                    "ဒါက bootloader ကို unlock လုပ်တယ် — userdata ဖျက်ခံရနိုင်တယ်။\n" +
                    "ဖုန်းပိုင်ရှင် ခွင့်ပြုချက် ရှိမှ လုပ်ပါ။\n\nဆက်လုပ်မလား?",
                    "Confirm OEM Unlock", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            await ExecuteCommandCleanAsync("fastboot.exe", "oem unlock " + code, "Huawei OEM Unlock (code)");
        }

        // ================= KIRIN BOOTLOADER UNLOCK (PotatoNV core) =================
        // PotatoNV-next ရဲ့ library (Potato.ImageFlasher + Potato.Fastboot) ကို သုံးပြီး
        // user က Kirin model ရွေးလိုက်ရင် ဖုန်း bootloader ကို unlock လုပ်ပေးတယ်:
        //   VCOM upload (xloader + [uce] + fastboot) → fastboot mode → WVLOCK/USRKEY ရေး → reboot
        // ရလာတဲ့ code ကို '⚡ OEM Unlock (code)' နဲ့ ဆက်သုံးရတယ်။

        private string FindBootloadersFolder()
        {
            string local = Path.Combine(Application.StartupPath, "bootloaders");
            if (Directory.Exists(local)) return local;

            try
            {
                string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                if (Directory.Exists(downloads))
                {
                    foreach (string root in Directory.GetDirectories(downloads, "PotatoNV*"))
                    {
                        string direct = Path.Combine(root, "bootloaders");
                        if (Directory.Exists(direct)) return direct;

                        foreach (string sub in Directory.GetDirectories(root))
                        {
                            string nested = Path.Combine(sub, "bootloaders");
                            if (Directory.Exists(nested)) return nested;
                        }
                    }
                }
            }
            catch { }
            return "";
        }

        private void LoadKirinList()
        {
            cmbKirin.Items.Clear();

            if (string.IsNullOrEmpty(hisiBootloadersPath) || !Directory.Exists(hisiBootloadersPath))
                hisiBootloadersPath = FindBootloadersFolder();

            if (string.IsNullOrEmpty(hisiBootloadersPath) || !Directory.Exists(hisiBootloadersPath))
            {
                // startup log noise မဖြစ်စေရ — unlock နှိပ်မှ MessageBox ပြ
                Debug.WriteLine("Kirin bootloaders folder not found.");
                return;
            }

            int n = 0;
            foreach (string dir in Directory.GetDirectories(hisiBootloadersPath))
            {
                string mf = Path.Combine(dir, "manifest.xml");
                if (!File.Exists(mf)) continue;
                try
                {
                    System.Xml.Linq.XDocument doc = System.Xml.Linq.XDocument.Load(mf);
                    if (doc.Root == null) continue;
                    var nameAttr = doc.Root.Attribute("name");
                    string name = nameAttr != null ? nameAttr.Value : Path.GetFileName(dir);
                    cmbKirin.Items.Add(new KirinBootloader { Name = name });
                    n++;
                }
                catch (Exception ex)
                {
                    Log("[!] Kirin manifest parse failed (" + Path.GetFileName(dir) + "): " + ex.Message, Color.Orange);
                }
            }

            if (cmbKirin.Items.Count > 0)
            {
                cmbKirin.SelectedIndex = 0;
                // startup noise မဖြစ်စေရ — Kirin list တွေ့တာ log မထုတ်တော့ (HiSilicon tab မှာ dropdown ပြပြီးသား)
            }
            else Debug.WriteLine("manifest.xml not found in the bootloaders folder.");
        }

        // VID_12D1 (Huawei) USB serial devices က COM port တွေ ရှာ — Enum\USB ထဲက Device Parameters\PortName
        private static HashSet<string> FindHuaweiComPorts()
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var usb = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
                if (usb == null) return found;
                foreach (string vidName in usb.GetSubKeyNames())
                {
                    if (!vidName.Contains("VID_12D1", StringComparison.OrdinalIgnoreCase)) continue;
                    using var vidKey = usb.OpenSubKey(vidName);
                    if (vidKey == null) continue;
                    foreach (string inst in vidKey.GetSubKeyNames())
                    {
                        using var instKey = vidKey.OpenSubKey(inst);
                        using var p = instKey?.OpenSubKey("Device Parameters");
                        if (p?.GetValue("PortName") is string com &&
                            com.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                            found.Add(com);
                    }
                }
            }
            catch { /* access denied / no USB tree */ }
            return found;
        }

        private static string ExtractComPort(string item)
        {
            if (string.IsNullOrEmpty(item)) return "";
            Match m = Regex.Match(item, @"COM\d+", RegexOptions.IgnoreCase);
            return m.Success ? m.Value.ToUpperInvariant() : item.Trim();
        }

        private void RefreshHisiPorts()
        {
            try
            {
                string keep = cmbHisiPort.SelectedItem != null ? cmbHisiPort.SelectedItem.ToString() : "";
                string keepCom = ExtractComPort(keep);

                huaweiComPorts = FindHuaweiComPorts();
                string[] ports = SerialPort.GetPortNames();
                Array.Sort(ports, (a, b) =>
                {
                    int na, nb;
                    int.TryParse(a.Replace("COM", "", StringComparison.OrdinalIgnoreCase), out na);
                    int.TryParse(b.Replace("COM", "", StringComparison.OrdinalIgnoreCase), out nb);
                    int ha = huaweiComPorts.Contains(a) ? 0 : 1;
                    int hb = huaweiComPorts.Contains(b) ? 0 : 1;
                    if (ha != hb) return ha.CompareTo(hb);
                    return na.CompareTo(nb);
                });

                cmbHisiPort.Items.Clear();
                foreach (string p in ports)
                    cmbHisiPort.Items.Add(huaweiComPorts.Contains(p) ? p + " (HUAWEI)" : p);

                if (cmbHisiPort.Items.Count == 0)
                {
                    cmbHisiPort.Items.Add("No Port");
                }
                else if (!string.IsNullOrEmpty(keepCom))
                {
                    bool restored = false;
                    foreach (object it in cmbHisiPort.Items)
                    {
                        if (string.Equals(ExtractComPort(it?.ToString()), keepCom, StringComparison.OrdinalIgnoreCase))
                        {
                            cmbHisiPort.SelectedItem = it;
                            restored = true;
                            break;
                        }
                    }
                    if (!restored) cmbHisiPort.SelectedIndex = 0;
                }
                else
                {
                    cmbHisiPort.SelectedIndex = 0;
                }

                if (huaweiComPorts.Count > 0)
                    Debug.WriteLine("HUAWEI USB COM: " + string.Join(", ", huaweiComPorts));
            }
            catch (Exception ex)
            {
                Log("Port scan error: " + ex.Message, Color.Red);
            }
        }

        private void BtnHisiBootloaders_Click(object sender, EventArgs e)
        {
            using (FolderBrowserDialog fbd = new FolderBrowserDialog
            {
                Description = "PotatoNV bootloaders folder ကို ရွေးပါ (manifest.xml ပါတဲ့ folder)"
            })
            {
                if (fbd.ShowDialog() != DialogResult.OK) return;
                hisiBootloadersPath = fbd.SelectedPath;
                SaveEdlPaths();
                LoadKirinList();
            }
        }

        private KirinBootloader ParseBootloader(string name)
        {
            // manifest.xml ထဲက image (role/path/address/hash) တွေကို ဖတ်တယ်
            foreach (string dir in Directory.GetDirectories(hisiBootloadersPath))
            {
                string mf = Path.Combine(dir, "manifest.xml");
                if (!File.Exists(mf)) continue;
                try
                {
                    System.Xml.Linq.XDocument doc = System.Xml.Linq.XDocument.Load(mf);
                    if (doc.Root == null) continue;
                    var nameAttr = doc.Root.Attribute("name");
                    string n = nameAttr != null ? nameAttr.Value : Path.GetFileName(dir);
                    if (n != name) continue;

                    var bl = new KirinBootloader { Name = n };
                    foreach (var el in doc.Root.Elements("image"))
                    {
                        var pathAttr = el.Attribute("path");
                        var roleAttr = el.Attribute("role");
                        var addrAttr = el.Attribute("address");
                        var hashAttr = el.Attribute("hash");
                        if (pathAttr == null || addrAttr == null) continue;

                        string full = Path.Combine(dir, pathAttr.Value);
                        var img = new KirinImage
                        {
                            Role = roleAttr != null ? roleAttr.Value : Path.GetFileNameWithoutExtension(pathAttr.Value),
                            Path = full,
                            Hash = hashAttr != null ? hashAttr.Value : "",
                            Address = (int)Convert.ToInt64(addrAttr.Value.Replace("0x", ""), 16)
                        };
                        if (File.Exists(full)) img.Size = new FileInfo(full).Length;
                        bl.Images.Add(img);
                    }
                    return bl;
                }
                catch (Exception ex)
                {
                    Log("Error while reading manifest: " + ex.Message, Color.Red);
                }
            }
            return null;
        }

        private static bool VerifySha1(string path, string expected)
        {
            try
            {
                using (var sha = System.Security.Cryptography.SHA1.Create())
                using (var fs = File.OpenRead(path))
                {
                    byte[] h = sha.ComputeHash(fs);
                    var sb = new StringBuilder();
                    foreach (byte b in h) sb.Append(b.ToString("x2"));
                    return string.Equals(sb.ToString(), expected, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { return false; }
        }

        private static string GenerateUnlockCode()
        {
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var rnd = new Random(Guid.NewGuid().GetHashCode());
            var sb = new StringBuilder();
            for (int i = 0; i < 16; i++) sb.Append(alphabet[rnd.Next(alphabet.Length)]);
            return sb.ToString();
        }

        // PotatoNV ရဲ့ SetNVMEProp — "getvar:nve:<prop>@" နောက်မှာ raw bytes ကို ဆက်ပို့တယ်
        private bool SetNvmeProp(Potato.Fastboot.Fastboot fb, string prop, byte[] value)
        {
            Log("[*] Writing " + prop + " (" + value.Length + " bytes)...", Color.Cyan);
            var cmd = new List<byte>();
            cmd.AddRange(Encoding.ASCII.GetBytes("getvar:nve:" + prop + "@"));
            cmd.AddRange(value);

            var res = fb.Command(cmd.ToArray());
            string payload = res != null && res.Payload != null ? res.Payload : "";
            Log("    → " + payload, Color.Gray);
            return payload.Contains("set nv ok");
        }

        private async void BtnHisiStartUnlock_Click(object sender, EventArgs e)
        {
            var selected = cmbKirin.SelectedItem as KirinBootloader;
            if (selected == null)
            {
                MessageBox.Show("Kirin model ကို အရင် ရွေးပါ။\n(📁 Bootloaders ခလုတ်နဲ့ bootloaders folder ရွေးဖို့ လိုနိုင်တယ်)",
                    "Kirin model", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string port = ExtractComPort(cmbHisiPort.SelectedItem != null ? cmbHisiPort.SelectedItem.ToString() : "");
            if (string.IsNullOrEmpty(port) || !port.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("ဖုန်းရဲ့ COM port ကို ရွေးပါ (Device Manager မှာ 'HUAWEI USB COM 1.0')။\n\n" +
                                "• ဖုန်းကို testpoint short လုပ်ပြီး ချိတ်ပါ\n• ပြီးရင် 🔄 နှိပ်ပြီး port ပြန်စစ်ပါ\n" +
                                "• HUAWEI port ဆိုရင် list ထဲမှာ '(HUAWEI)' နဲ့ အပေါ်ဆုံးမှာ ပေါ်တယ်",
                    "COM port", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (huaweiComPorts.Count > 0 && !huaweiComPorts.Contains(port))
            {
                Log("[!] " + port + " is not a HUAWEI USB COM port — with the driver installed correctly, select the '(HUAWEI)' port.", Color.Orange);
            }

            if (MessageBox.Show(
                    "Kirin Bootloader Unlock\n\n" +
                    "• Model : " + selected.Name + "\n" +
                    "• Port  : " + port + "\n\n" +
                    "ဖုန်း RAM ထဲ bootloader တင်ပြီး bootloader lock ကို ဖွင့်ပါမယ်။\n" +
                    "• userdata ဖျက်ခံရနိုင်တယ်\n" +
                    "• ဖုန်းပိုင်ရှင် ခွင့်ပြုချက် ရှိမှ လုပ်ပါ (ခိုးရာပစ္စည်း မဟုတ်ကြောင်း စစ်ပါ)\n\n" +
                    "ဆက်လုပ်မလား?",
                    "Confirm Kirin Unlock", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            KirinBootloader bl = ParseBootloader(selected.Name);
            if (bl == null || bl.Images.Count == 0)
            {
                Log("[x] Could not read manifest.xml of " + selected.Name + ".", Color.Red);
                return;
            }

            string code = await RunKirinUnlockAsync(bl, port);

            if (!string.IsNullOrEmpty(code))
            {
                Log("========================================================", Color.FromArgb(0, 180, 255));
                Log("   ✔ UNLOCK CODE : " + code, Color.LightGreen);
                Log("========================================================", Color.FromArgb(0, 180, 255));
                MessageBox.Show("Unlock ပြီးပါပြီ!\n\nUnlock code: " + code + "\n\n" +
                                "ဖုန်းကို fastboot mode ပြောင်းပြီး '⚡ OEM Unlock (code)' နဲ့ ဒီ code ကို ထည့်ပါ။",
                    "Kirin Unlock", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private static byte[] HashSha256(byte[] data)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            return sha.ComputeHash(data);
        }

        private async Task<string> RunKirinUnlockAsync(KirinBootloader bl, string port)
        {
            if (hisiCts != null) { try { hisiCts.Cancel(); hisiCts.Dispose(); } catch { } }
            hisiCts = new CancellationTokenSource();
            CancellationToken ct = hisiCts.Token;
            string unlockCode = "";

            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("   HUAWEI / HONOR - KIRIN BOOTLOADER UNLOCK", Color.White);
            Log("   " + bl.Name + "   |   Port: " + port, Color.White);
            Log("========================================================", Color.FromArgb(0, 180, 255));
            SetProgress(5, "Verifying images");

            bool ok = await Task.Run(() =>
            {
                try
                {
                    // 1) image တွေ စစ် (ဖိုင် ရှိ/မရှိ + SHA1 hash)
                    long totalSize = 0;
                    foreach (var img in bl.Images)
                    {
                        if (!File.Exists(img.Path)) { Log("[x] Image file not found: " + img.Path, Color.Red); return false; }
                        if (img.Size <= 0) { Log("[x] Image file is empty: " + Path.GetFileName(img.Path), Color.Red); return false; }
                        if (!string.IsNullOrEmpty(img.Hash) && !VerifySha1(img.Path, img.Hash))
                        {
                            Log("[x] Hash mismatch: " + Path.GetFileName(img.Path) + " - file corrupted or wrong", Color.Red);
            return false;
        }
                        totalSize += img.Size;
                    }
                    Log("[OK] Images verified (" + bl.Images.Count + " files, " + (totalSize / 1024) + " KB)", Color.LightGreen);
                    SetProgress(10, "Uploading bootloader");

                    // 2) VCOM (COM port) ကနေ RAM ထဲ တင်
                    var flasher = new Potato.ImageFlasher.ImageFlasher();
                    try
                    {
                        Log("[*] Opening " + port + "...", Color.Orange);
                        flasher.Open(port);
                        Log("[*] Uploading " + bl.Name + " bootloader into phone RAM...", Color.Cyan);

                        int done = 0;
                        int grand = (int)Math.Max(1, totalSize);
                        foreach (var img in bl.Images)
                        {
                            if (ct.IsCancellationRequested) { Log("[!] Cancelled.", Color.Orange); return false; }
                            Log("[*] uploading " + img.Role + "  (" + (img.Size / 1024) + " KB @ 0x" + img.Address.ToString("X") + ")", Color.White);
                            int baseDone = done;
                            long thisSize = img.Size;
                            flasher.Write(img.Path, img.Address, x =>
                            {
                                int pct = 10 + (int)(60.0 * (baseDone + thisSize * x / 100.0) / grand);
                                SetProgress(pct, "Uploading " + img.Role);
                            });
                            done += (int)thisSize;
                        }
                    }
                    finally
                    {
                        try { flasher.Close(); } catch { }
                    }
                    Log("[OK] Bootloader uploaded", Color.LightGreen);

                    // 3) fastboot device ကို စောင့် (cancellable)
                    SetProgress(72, "Waiting for fastboot");
                    Log("[*] Waiting for fastboot device...", Color.Orange);
                    var fb = new Potato.Fastboot.Fastboot();
                    bool found = false;
                    for (int i = 0; i < 60; i++)
                    {
                        if (ct.IsCancellationRequested) { Log("[!] Cancelled.", Color.Orange); return false; }
                        try
                        {
                            string[] devs = Potato.Fastboot.Fastboot.GetDevices();
                            if (devs != null && devs.Length > 0) { found = true; break; }
                        }
                        catch (Exception ex) { Log("[!] device scan: " + ex.Message, Color.Orange); }
                        System.Threading.Thread.Sleep(1000);
                    }
                    if (!found)
                    {
                        Log("[x] Fastboot device did not appear - check testpoint/cable/driver, then retry.", Color.Red);
                        return false;
                    }

                    fb.Connect();
                    Log("[OK] Connected - reading device info...", Color.LightGreen);
                    SetProgress(80, "Reading device info");

                    try { Log("  • Serial    : " + fb.GetSerialNumber(), Color.White); } catch { }

                    var bsn = fb.Command("oem read_bsn");
                    if (bsn != null && bsn.Status == Potato.Fastboot.Fastboot.Status.Okay)
                        Log("  • Board ID  : " + bsn.Payload, Color.White);

                    var modelRes = fb.Command("oem get-product-model");
                    if (modelRes != null) Log("  • Model     : " + modelRes.Payload, Color.White);

                    var buildRes = fb.Command("oem get-build-number");
                    if (buildRes != null && buildRes.Payload != null)
                        Log("  • Build     : " + buildRes.Payload.Replace(":", ""), Color.White);

                    // FBLOCK state — unlocked ဖြစ်ရမယ် (မဖြစ်ရင် bootloader မှားနေတယ်)
                    var fblock = fb.Command("oem lock-state info");
                    string fpayload = fblock != null && fblock.Payload != null ? fblock.Payload : "";
                    bool unlocked = Regex.IsMatch(fpayload, @"FB[\w: ]{1,}UNLOCKED");
                    if (!unlocked)
                    {
                        var bd = fb.Command("oem backdoor info");
                        string bpayload = bd != null && bd.Payload != null ? bd.Payload : "";
                        unlocked = Regex.IsMatch(bpayload, @"FB[\w: ]{1,}UNLOCKED");
                    }
                    Log("  • FBLOCK    : " + (unlocked ? "UNLOCKED" : "LOCKED"), unlocked ? Color.LightGreen : Color.OrangeRed);
                    if (!unlocked)
                    {
                        Log("[x] FBLOCK is locked - wrong Kirin model selected (pick the model matching the phone).", Color.Red);
                        try { fb.Disconnect(); } catch { }
                        return false;
                    }

                    // လက်ရှိ key ရှိရင် ပြ
                    var oldKey = fb.Command("getvar:nve:WVLOCK");
                    if (oldKey != null && oldKey.Payload != null)
                    {
                        var m = Regex.Match(oldKey.Payload, @"[\w\d]{16}");
                        if (m.Success) Log("  • Current WVLOCK key: " + m.Value, Color.Orange);
                    }

                    // 4) unlock code ထုတ် + NVME ရေး
                    SetProgress(88, "Writing unlock data");
                    unlockCode = GenerateUnlockCode();
                    Log("[*] New unlock code: " + unlockCode, Color.Cyan);

                    if (!SetNvmeProp(fb, "FBLOCK", new byte[] { 1 }))
                    {
                        Log("[!] FBLOCK set failed - trying the alternative (hwdog/backdoor)...", Color.Orange);
                        try { fb.Command("oem hwdog certify set 1"); } catch { }
                        try { fb.Command("oem backdoor set 1"); } catch { }
                    }

                    if (!SetNvmeProp(fb, "WVLOCK", Encoding.ASCII.GetBytes(unlockCode)))
                    {
                        Log("[x] Could not write WVLOCK - unlock not completed.", Color.Red);
                        try { fb.Disconnect(); } catch { }
                        return false;
                    }

                    if (!SetNvmeProp(fb, "USRKEY", HashSha256(Encoding.ASCII.GetBytes(unlockCode))))
                    {
                        Log("[x] Could not write USRKEY - unlock not completed.", Color.Red);
                        try { fb.Disconnect(); } catch { }
                        return false;
                    }

                    // 5) reboot
                    SetProgress(96, "Rebooting");
                    Log("[*] Rebooting the phone...", Color.Orange);
                    try { fb.Command("reboot"); } catch { }
                    try { fb.Disconnect(); } catch { }

                    return true;
                }
                catch (Exception ex)
                {
                    Log("[✘] Unlock error: " + ex.Message, Color.Red);
                    Log("[i] Short the testpoint, reconnect and retry.", Color.Orange);
                    return false;
                }
            }, ct);

            if (ok)
            {
                SetProgress(100, "Completed");
                Log("[OK] Kirin bootloader unlock completed!", Color.LightGreen);
            }
            else
            {
                SetProgress(0, "Failed");
                unlockCode = "";
            }

            return unlockCode;
        }

    }
}
