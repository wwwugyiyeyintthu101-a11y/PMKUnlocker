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
        // - နောက်ဆုံး: provision/settings + wizard skip (best-effort, no-root path — any brand)
        private async void BtnXiaomiTempRootFrp_Click(object sender, EventArgs e)
        {
            // exit path တိုင်းမှာ tempRootFlowActive ကို ပြန်ချရမယ် — မချရင် နောက်ပိုင်း ADB error တွေ
            // တိတ်ဆိတ်သွားပြီး recovery path မှား ဝင်တတ်တယ် (confirm dialog မှာ No နှိပ်တာ အပါအဝင်)။
            try
            {
                await RunTempRootFlowAsync();
            }
            catch (Exception exFlow)
            {
                Log("[!] Temp Root flow stopped on an error: " + exFlow.Message, Color.OrangeRed);
                Log("[i] Phone ကို စစ်ပြီး ထပ်စမ်းပါ — exploit run နေတုန်း ဖုန်း reboot ဖြစ်နိုင်တယ်။", Color.Gray);
            }
            finally
            {
                tempRootFlowActive = false;
            }
        }

        private async Task RunTempRootFlowAsync()
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
                    "Android Temp Root", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
            lastBootVerdictKey = "";
            lastBootVerdict = BootVerdict.Unknown;
            currentBootId = "";
            lastExploitAttemptId = "";
            LoadBootLedgerOnce();
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
            // Xiaomi/MIUI flag — MST token payload က Xiaomi-only ဖြစ်လို့ အဲဒီ step ကိုပဲ ကျော်မယ်။
            // ကျန်တဲ့ (su/adb root, kernel exploit, provision) တွေက Android တိုင်းမှာ run မယ်။
            bool looksMiui = brand.IndexOf("xiaomi", StringComparison.OrdinalIgnoreCase) >= 0 ||
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
            // ရွေးချယ်မှု: GLK1 profile ကိုက်တဲ့ exploit ရှိရင် အဲဒါကို အရင်စမ်း၊ မရှိရင် ရိုးရိုး primary (DFRoot) ကို အရင်
            var exploitMatches = KernelExploit.FindMatches(manifest, fp);
            ExploitEntry exploitMatch = null;
            string exploitStatus = exploitMatches.Count == 0
                ? "no match — " + (string.IsNullOrWhiteSpace(fp.Kernel) ? "?" : fp.Kernel)
                : "matched: " + exploitMatches.Count + " entr" + (exploitMatches.Count == 1 ? "y" : "ies");
            ExploitEntry profileFirst = exploitMatches.FirstOrDefault(e => KernelExploit.HasExactProfile(exploitsRoot, e, fp.Kernel));
            // GhostLock reclaim ဖြစ်နိုင်ခြေ နည်းလို့ ကျရှုံးရင် kernel panic + reboot ဖြစ်တယ် (garnet တိုင်းထား: boot ၄ ခါ ၁ ခါ)။
            // ဒါကြောင့် profile ကိုက်ရင်တောင် DFRoot ကို အရင်စမ်းပြီး၊ မရမှ profile exploit ကို fallback အနေနဲ့ ဆက်စမ်းတယ်။
            if (profileFirst != null && exploitMatches.Count > 1)
                exploitMatch = exploitMatches.First(e => !string.Equals(e.Id, profileFirst.Id, StringComparison.Ordinal));
            else if (profileFirst != null) exploitMatch = profileFirst;
            else if (exploitMatches.Count > 0) exploitMatch = exploitMatches[0];
            // measured-patched firmware (exploits\patched-kernels.txt) သို့မဟုတ် published fix floor —
            // kernel က stale waiter ကို ရှင်းထားရင် GhostLock ရဲ့ stack write က ဘယ် profile နဲ့မှ မရောက်နိုင်
            string patchedNote = KernelExploit.PatchedKernelNote(exploitsRoot, fp.Kernel, fp.Patch);
            // gate ကို entry တစ်ခုချင်းအတွက် — DFRoot (APK) ကို kernel fix floor နဲ့ မပိတ်ရ
            bool unsupportedFirmware = exploitMatch != null && IsGhostLockExploit(exploitMatch) && patchedNote != null;

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
                    "Android Temp Root", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string suId = (await ProbeSuAsync()).Trim();
            bool hasRoot = suId.Contains("uid=0");
            // KernelSU (LKM / jailbreak mode) — su binary မရှိဘဲ module က root ပေးနေတယ်။
            // ဒီ state ကို "no root" လို့ မယူဘူး (garnet / Android 16 မှာ တိုင်းထားတယ်)။
            RootProbeState rootState = await ProbeRootStateAsync();
            bool moduleRoot = !hasRoot && rootState.Verdict == RootVerdict.ModuleLoaded;
            bool rootReady = hasRoot || moduleRoot;

            if (!looksMiui)
            {
                Log("[i] Brand='" + brand + "' (no MIUI) — Xiaomi-only MST token step is skipped; every other step runs.", Color.Gray);
            }

            string mode = rootReady
                ? "temp root (ready)"
                : unsupportedFirmware
                    ? "unsupported firmware (kernel patched)"
                    : exploitMatch != null
                        ? (string.Equals(exploitMatch.RunMode, "apk", StringComparison.OrdinalIgnoreCase)
                            ? "APK UI exploit → temp root"
                            : "kernel exploit → temp root")
                        : "no-root (ADB only)";
            string exploitLine = exploitMatch != null
                ? "Match : " + exploitMatch.Name + (string.IsNullOrWhiteSpace(exploitMatch.Id) ? "" : " (" + exploitMatch.Id + ")")
                : "Match : none";
            if (ConfirmDialog.Show(
                    "Android Temp Root" + Environment.NewLine + Environment.NewLine +
                    "Model : " + (string.IsNullOrWhiteSpace(model) ? "?" : model) + Environment.NewLine +
                    "Brand : " + (string.IsNullOrWhiteSpace(brand) ? "?" : brand) +
                    (string.IsNullOrWhiteSpace(miui) ? "" : "  ·  MIUI " + miui) + Environment.NewLine +
                    "Kernel: " + (string.IsNullOrWhiteSpace(fp.Android) ? "?" : "Android " + fp.Android) +
                    (string.IsNullOrWhiteSpace(kernelVer) ? "" : "  ·  " + kernelVer) + Environment.NewLine +
                    "Mode  : " + mode + Environment.NewLine +
                    exploitLine + Environment.NewLine + Environment.NewLine +
                    (rootReady
                        ? "• Temp root ရှိပြီး — FRP မလုပ်ဘူး" + Environment.NewLine +
                          (moduleRoot
                              ? "• Root: " + rootState.Summary + Environment.NewLine +
                                "• " + rootState.Hint + Environment.NewLine +
                                "• adb shell ဆီ su မရောက်လို့ FRP Reset (ROOT) က ငြင်းမယ် — root လိုတဲ့ app ကို manager မှာ ခွင့်ပြုပါ"
                              : "• ဆက်လုပ်ရန်: FRP Reset (ROOT) ခလုတ်")
                        : unsupportedFirmware
                            ? "• ဒီ firmware ကို GhostLock မထောက်ပံ့ဘူး — kernel မှာ CVE-2026-43499 fix ပါပြီ" + Environment.NewLine +
                              "• " + patchedNote + Environment.NewLine +
                              "• Exploit ကို မစမ်းတော့ဘူး — reboot / profile အသစ်ကလည်း မပြောင်းနိုင်" + Environment.NewLine +
                              "• No-root provision path နဲ့ ဆက်မယ်"
                        : exploitMatch != null
                            ? (string.Equals(exploitMatch.RunMode, "apk", StringComparison.OrdinalIgnoreCase)
                                ? "• su မရ — DFRoot app install + UI automation စမ်းမယ်" + Environment.NewLine +
                                  "• Developer options → 'USB debugging (Security settings)' ဖွင့်ထားရမယ်" + Environment.NewLine +
                                  "• ဖုန်းမျက်နှာပြင် ဖွင့်ထားပြီး မလော့ခ်ထားပါ (UI ကို tool ကိုယ်တိုင် tap မယ်)" + Environment.NewLine +
                                  "• Root ရရင် ရပ်မယ် — FRP Reset (ROOT) နဲ့ ဆက်"
                                : "• su မရ — kernel exploit payload စမ်းမယ် (ART-style)" + Environment.NewLine +
                                  "• Root ရရင် ရပ်မယ် — FRP Reset (ROOT) နဲ့ ဆက်" + Environment.NewLine +
                                  "• payload မရှိရင် no-root path ကျ")
                            : "• su / exploit မရှိ — provision + wizard skip (best-effort, any brand)" + Environment.NewLine +
                              "• Android 11+ မှာ full FRP အတွက် root/temp root လိုနိုင်") + Environment.NewLine + Environment.NewLine +
                    "ဖုန်းပိုင်ရှင် ခွင့်ပြုချက် ရှိမှ လုပ်ပါ။ ဆက်လုပ်မလား?",
                    "Confirm Android Temp Root", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            Log("═══ Android Temp Root · " + model + " (" + fp.Codename + ") · " + selectedAdbSerial + " ═══", Color.FromArgb(0, 180, 255));
            string ExploitName(ExploitEntry e) => string.IsNullOrWhiteSpace(e.Name) ? e.Id : e.Name;
            if (profileFirst != null && exploitMatch != null &&
                !string.Equals(exploitMatch.Id, profileFirst.Id, StringComparison.Ordinal))
                Log("[i] GLK1 profile ready for " + fp.Kernel + " → " + ExploitName(exploitMatch) +
                    " runs first (profile exploit goes as fallback)", Color.Cyan);
            else if (profileFirst != null)
                Log("[i] GLK1 profile ready for " + fp.Kernel + " → " + ExploitName(profileFirst) + " runs first", Color.Cyan);
            else if (exploitMatches.Count > 1)
                Log("[i] No GLK1 profile for " + fp.Kernel + " → " +
                    (exploitMatch == null ? "no exploit" : ExploitName(exploitMatch)) +
                    " runs first (profile exploit goes as fallback)", Color.Cyan);
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
                " · root " + (rootReady ? "Success" : "Denied"),
                rootReady ? Color.LightGreen : Color.Orange);
            if (moduleRoot)
            {
                Log("Root: " + rootState.Summary, Color.LightGreen);
                Log("Note: " + rootState.Hint, Color.Orange);
            }
            // root မရှိသေးရင် "do not unplug" လို့ မပြ — ဒီလိုင်းက root တကယ်ရမှသာ အဓိပ္ပာယ်ရှိတယ်
            if (rootReady)
                Log("Temp Root running — do not unplug or reboot the device.", Color.Orange);
            else if (exploitMatch == null)
            {
                // manifest မှာ ဒီ kernel အတွက် entry မရှိ → exploit ဘာလို့ မစမ်းတာလဲ ရှင်းရှင်းပြ
                string knownKernels = string.Join(", ", manifest.Exploits
                    .Where(e => e.Enabled)
                    .SelectMany(e => e.KernelPrefix ?? new string[0])
                    .Distinct()
                    .OrderBy(k => k));
                Log("[i] No exploit match for this kernel: " + (string.IsNullOrWhiteSpace(fp.Kernel) ? "?" : fp.Kernel) +
                    " — exploits\\manifest.json covers: " + (knownKernels.Length == 0 ? "none" : knownKernels), Color.Orange);
                Log("[i] So no kernel exploit/payload is tried on this boot — continuing with the no-root path.", Color.Gray);
            }
            else
                Log("No temp root yet — exploit/payload steps run next.", Color.Orange);

            // KernelSU module က root ပေးနေပြီ — payload/exploit ထပ် run ရင် ဖုန်း reboot ဖြစ်နိုင်တယ်၊ မလိုအပ်တော့ဘူး
            if (moduleRoot)
            {
                // module က root ပေးနေပြီမို့ `adb root` ကို တစ်ခါ စမ်းခွင့်ရှိတယ် — ရရင် exploit မလိုဘဲ ဆက်နိုင်တယ်
                string rootId = await TryAdbRootAsync();
                if (rootId.Contains("uid=0"))
                {
                    hasRoot = true;
                    moduleRoot = false;
                    suId = rootId;
                    Log("[OK] Module live + adb root ရ — shell is root, exploit မလိုပါ။", Color.LightGreen);
                }
                else
                {
                    LogTempRootState(rootState, model, fp.Codename, false);
                    tempRootFlowActive = false;
                    return;
                }
            }

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
            if (!hasRoot && looksMiui && skipToken)
                Log("[2/4] Token path skip — Android16/zircon (token payload unreliable here, ~30s) → going straight to kernel exploit", Color.Cyan);
            if (!hasRoot && looksMiui && !skipToken)
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
            bool moduleRootAfterExploit = false;
            bool anyExploitRan = false;
            ExploitEntry rootExploit = null;
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
                anyExploitRan = true;
                var swExploit = Stopwatch.StartNew();
                (bool exRoot, double exSec) = await TryKernelExploitAsync(exploitsRoot, exploitMatch, fp);
                swExploit.Stop();
                hasRoot = exRoot;
                // realSec >= 0 → run.log/stdout က actual exploit time (timeout wait မဟုတ်)
                exploitSec = exSec >= 0 ? exSec : swExploit.Elapsed.TotalSeconds;
                if (hasRoot) suId = await ProbeSuAsync();
                // module loaded ဖြစ်ပြီး su မရတဲ့ state — exploit ထပ်မ run ရ (fallback ကို မဆက်)
                if (!hasRoot && await RootModuleLiveAsync()) moduleRootAfterExploit = true;
                if (hasRoot || moduleRootAfterExploit) rootExploit = exploitMatch;
            }

            // Fallback — primary (DFRoot) မရရင် manifest ထဲက ကျန်တဲ့ matching exploit ကို ဆက် (GhostLock)
            // profile မကိုက်ရင် TryKernelExploitAsync ကိုယ်တိုင် "No GLK1 profile" ပြီး abort လုပ်မယ်
            if (!hasRoot && !moduleRootAfterExploit && !tempRootAborted && exploitMatch != null)
            {
                foreach (ExploitEntry alt in KernelExploit.FindMatches(manifest, fp))
                {
                    if (string.Equals(alt.Id, exploitMatch.Id, StringComparison.Ordinal)) continue;
                    if (tempRootAborted) break;
                    Log("[i] Fallback exploit: " + (string.IsNullOrWhiteSpace(alt.Name) ? alt.Id : alt.Name), Color.Cyan);
                    anyExploitRan = true;
                    var swAlt = Stopwatch.StartNew();
                    (bool altRoot, double altSec) = await TryKernelExploitAsync(exploitsRoot, alt, fp);
                    swAlt.Stop();
                    if (altRoot)
                    {
                        exploitMatch = alt;
                        hasRoot = true;
                        suId = await ProbeSuAsync();
                        exploitSec = altSec >= 0 ? altSec : swAlt.Elapsed.TotalSeconds;
                        rootExploit = alt;
                        break;
                    }
                    // fallback လည်း "module live, su မရ" ဖြစ်ရင် နောက် exploit ကို မဆက်တော့ဘူး
                    if (await RootModuleLiveAsync()) { moduleRootAfterExploit = true; rootExploit = alt; break; }
                }
            }

            // DFRoot က ဒီဖုန်းမှာ root ရဖူးပြီ → နောက် run တွေမှာ GhostLock ကို မစမ်းတော့ (panic အန္တရာယ်)
            if (rootExploit != null && IsDfrootExploit(rootExploit))
            {
                RememberDfrootVerified(selectedAdbSerial);
                Log("[i] DFRoot က ဒီဖုန်းမှာ temp root ရပြီ — နောက်ပိုင်း GhostLock ကို မစမ်းတော့ပါ။", Color.Gray);
            }

            if (tempRootAborted && !hasRoot)
            {
                tempRootFlowActive = false;
                Log("[i] Temp Root flow stopped — device disconnected.", Color.OrangeRed);
                return;
            }

            // module က live ဖြစ်နေပြီ — temp root ရှိပြီ၊ ဒါပေမယ့် adb shell ဆီ su မရောက်။
            // ဒီ state မှာ exploit ထပ်တွန်း / reboot လုပ်ရင် ရနေတဲ့ root ကို ဆုံးရှုံးမယ်။
            if (!hasRoot && (moduleRootAfterExploit || await RootModuleLiveAsync()))
            {
                // exploit run မရှိတော့ဘူး → `adb root` ကို တစ်ခါ စမ်းခွင့်ရှိတယ်။
                // KernelSU ADB Root ဖွင့်ထားရင် adbd က root ဖြစ်လာပြီး shell id → uid=0 (ဒါဆို ကိုယ်တိုင် ဆက်သွား)။
                string rootId = await TryAdbRootAsync();
                if (rootId.Contains("uid=0"))
                {
                    hasRoot = true;
                    suId = rootId;
                    Log("[OK] Module live + adb root ရ — shell is root, root handle ရပြီ။", Color.LightGreen);
                }
                else
                {
                    RootProbeState liveState = await ProbeRootStateAsync();
                    LogTempRootState(liveState, model, fp.Codename, moduleRootAfterExploit);
                    return;
                }
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
                Log(rootHandlePrefix != null || rootViaAdbRoot
                        ? "[OK] Root handle: " + RootHandle + " — FRP Reset (ROOT) က ဒါကို သုံးမယ"
                        : "[!] Root handle: မရ — KernelSU loaded ဖြစ်ပေမဲ့ adb shell ဆီ su မရောက်ဘူး " +
                          "(FRP Reset (ROOT) က ငြင်းမယ် · no-root path သုံးပါ)",
                    (rootHandlePrefix != null || rootViaAdbRoot) ? Color.LightGreen : Color.Orange);
                Log("Next: press 🔓 FRP Reset (ROOT)  ·  reboot removes temp root", Color.Orange);
                tempRootFlowActive = false;
                return;
            }

            Log(unsupportedFirmware && !anyExploitRan
                    ? "[4/4] No temp root — firmware unsupported (kernel patched) → No-root provision path: provision + wizard skip..."
                    : "[4/4] No temp root → No-root provision path: provision + wizard skip...",
                Color.Cyan);

            // Step B: provision settings (no-root path, any brand)
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

            // Step C: setup wizards (Google + vendor)
            // pm disable က system pkgs များ — best effort only
            foreach (string pkg in new[] { "com.google.android.setupwizard", "com.android.setupwizard" })
            {
                string outp = (await ExecuteCommandQuickAsync("adb.exe", "shell pm disable-user --user 0 " + pkg)).Trim();
                if (outp.IndexOf("new state: disabled", StringComparison.OrdinalIgnoreCase) >= 0)
                    Log("[OK] " + pkg + " → disabled", Color.LightGreen);
            }

            // Step D: Google/vendor account packages clear
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

            // Step E2: no-root provision path — second space / user provision tricks
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
Log("[!] Provision settings not applied — Android 11+ may need temp root (Magisk/su).", Color.Orange);
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
            Color titleC = dark ? Color.FromArgb(139, 92, 246) : Color.FromArgb(124, 58, 237);
            Color hintC = dark ? Color.FromArgb(150, 165, 185) : Color.FromArgb(70, 80, 95);
            Color okBg = dark ? Color.FromArgb(139, 92, 246) : Color.FromArgb(124, 58, 237);
            Color cancelBg = dark ? Color.FromArgb(60, 66, 76) : Color.FromArgb(150, 158, 170);
            Color greenC = dark ? Color.FromArgb(0, 220, 140) : Color.FromArgb(0, 130, 70);
            Color redC = dark ? Color.FromArgb(255, 110, 80) : Color.FromArgb(200, 60, 30);
            Color cyanC = dark ? Color.FromArgb(139, 92, 246) : Color.FromArgb(124, 58, 237);

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
                    using (var p = new Pen(Color.FromArgb(139, 92, 246)))
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
        // root ရနိုင်တဲ့ လမ်းတွေ — ပထမ အောင်တဲ့ တစ်ခုကို handle အဖြစ် သိမ်းတယ်
        private static readonly string[] SuProbeCommands =
        {
            "shell su -c id",
            "shell su 0 id",
            "shell /system/bin/su -c id",
            "shell /data/adb/ksu/bin/su -c id",
            "shell /debug_ramdisk/su -c id",
            "shell /data/local/tmp/su -c id",
            "shell /data/local/tmp/cve-2026-43499-root -c id"
        };
        // "su -c" / "/data/local/tmp/su -c" — null = root handle မရှိ (KernelSU module only)
        private static string rootHandlePrefix = null;
        // KernelSU Settings → ADB Root ဖွင့်ထားရင် adbd ကိုယ်တိုင် root ဖြစ်နေတယ် — su မလိုဘူး
        private static bool rootViaAdbRoot = false;
        // Exploit run နေတုန်း adb root ကို မခေါ်ရ — userdebug build မှာ adbd restart ဖြစ်ပြီး
        // run နေတဲ့ exploit shell ကို သတ်ပစ်တယ် (root ရခါနီး ပျက်သွားနိုင်တယ်)။
        private static bool exploitRunActive = false;

        // Exploit entry က GhostLock (CVE-2026-43499) လား — patched-kernel gate က ဒီ entry တွေအတွက်ပဲ
        // (DFRoot APK က kernel fix floor နဲ့ မသက်ဆိုင်ဘူး)
        private static bool IsGhostLockExploit(ExploitEntry ex) =>
            ex != null && !string.IsNullOrEmpty(ex.Id) &&
            ex.Id.IndexOf("ghostlock", StringComparison.OrdinalIgnoreCase) >= 0;

        // DFRoot က ဒီဖုန်းမှာ temp root ရဖူးပြီလား — pmk_settings.txt ထဲမှာ serial နဲ့ မှတ်ထားတယ်။
        // မှတ်ထားရင် နောက် run တွေမှာ GhostLock (CVE-2026-43499) ကို လုံးဝ မစမ်းတော့ဘူး (panic အန္တရာယ်)။
        // ပြန်ဖွင့်ချင်ရင် pmk_settings.txt ထဲက DfrootVerifiedSerials လိုင်းကို ဖျက်ပါ။
        private const string DfrootVerifiedKey = "DfrootVerifiedSerials";

        private static bool IsDfrootExploit(ExploitEntry ex) =>
            ex != null && !string.IsNullOrEmpty(ex.Id) &&
            ex.Id.IndexOf("dfroot", StringComparison.OrdinalIgnoreCase) >= 0;

        // DirtyFrag မိသားစု (DFRoot APK / DirtyFrag payload) — kernel floor နဲ့ KMI ကို ကြိုပြရန်
        private static bool IsDirtyFragExploit(ExploitEntry ex) =>
            ex != null && !string.IsNullOrEmpty(ex.Id) &&
            (ex.Id.IndexOf("dfroot", StringComparison.OrdinalIgnoreCase) >= 0 ||
             ex.Id.IndexOf("dirtyfrag", StringComparison.OrdinalIgnoreCase) >= 0);

        private List<string> LoadDfrootVerifiedSerials()
        {
            var serials = new List<string>();
            try
            {
                if (!File.Exists(SettingsFile)) return serials;
                foreach (string raw in File.ReadAllLines(SettingsFile))
                {
                    string line = raw.Trim();
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    if (!line.Substring(0, eq).Trim().Equals(DfrootVerifiedKey, StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (string s in line.Substring(eq + 1).Split(','))
                        if (s.Trim().Length > 0 && !serials.Contains(s.Trim(), StringComparer.OrdinalIgnoreCase))
                            serials.Add(s.Trim());
                    break;
                }
            }
            catch { }
            return serials;
        }

        private bool DfrootVerifiedFor(string serial) =>
            !string.IsNullOrWhiteSpace(serial) &&
            LoadDfrootVerifiedSerials().Contains(serial.Trim(), StringComparer.OrdinalIgnoreCase);

        private void RememberDfrootVerified(string serial)
        {
            if (string.IsNullOrWhiteSpace(serial)) return;
            try
            {
                var serials = LoadDfrootVerifiedSerials();
                if (serials.Contains(serial.Trim(), StringComparer.OrdinalIgnoreCase)) return;
                serials.Add(serial.Trim());
                string value = DfrootVerifiedKey + "=" + string.Join(",", serials);
                var lines = new List<string>();
                bool replaced = false;
                if (File.Exists(SettingsFile))
                {
                    foreach (string raw in File.ReadAllLines(SettingsFile))
                    {
                        string line = raw.Trim();
                        int eq = line.IndexOf('=');
                        bool isKey = eq > 0 && line.Substring(0, eq).Trim()
                            .Equals(DfrootVerifiedKey, StringComparison.OrdinalIgnoreCase);
                        if (isKey)
                        {
                            if (!replaced) { lines.Add(value); replaced = true; }
                            continue;
                        }
                        lines.Add(raw);
                    }
                }
                if (!replaced) lines.Add(value);
                Directory.CreateDirectory(ShopServices.DataDirectory);
                File.WriteAllLines(SettingsFile, lines, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                // မှတ်မရရင် flow ကို မရပ်ဘူး — ဒီ run အတွက် ဆက်အလုပ်လုပ်နိုင်တယ်
                Log("[i] DFRoot marker not saved: " + ex.Message, Color.Gray);
            }
        }

        /// <summary>Module live ဖြစ်နေတဲ့ state — exploit မတွန်းတော့ဘဲ ရပ်ရမယ့် တစ်နေရာတည်း စာသား။
        /// loadedByThisRun=true ဆိုရင် ဒီ run က module ကို အခုမှ တင်တာ (ALREADY PRESENT လို့ မပြရ)။</summary>
        private void LogTempRootState(RootProbeState state, string model, string codename, bool loadedByThisRun)
        {
            Log("", Color.Gray);
            Log((loadedByThisRun ? "✅ TEMP ROOT ACTIVE" : "✅ TEMP ROOT ALREADY PRESENT") +
                "  ·  " + (string.IsNullOrWhiteSpace(model) ? "?" : model) +
                (string.IsNullOrWhiteSpace(codename) ? "" : " (" + codename + ")") +
                (loadedByThisRun ? "  ·  this run loaded the module" : ""), Color.LightGreen);
            Log("Root  : " + state.Summary, Color.White);
            if (!string.IsNullOrWhiteSpace(state.Hint)) Log("Next  : " + state.Hint, Color.Orange);
            Log("Reboot removes temp root.", Color.Orange);
        }

        private string RootHandle => rootViaAdbRoot
            ? "adb root (shell is root)"
            : (string.IsNullOrWhiteSpace(rootHandlePrefix) ? "su -c" : rootHandlePrefix);
        // root အဖြစ် shell command တစ်ခု run ရန် adb args
        private string RootShellArgs(string inner) => rootViaAdbRoot
            ? "shell \"" + inner + "\""
            : "shell " + (string.IsNullOrWhiteSpace(rootHandlePrefix) ? "su -c" : rootHandlePrefix) + " \"" + inner + "\"";

        private async Task<string> ProbeSuAsync()
        {
            // adbd ကိုယ်တိုင် root ဖြစ်နေလား (KernelSU ADB Root) — su ထက် ပိုတိုတယ်
            string selfId = (await ExecuteCommandQuickAsync("adb.exe", "shell id")).Trim();
            if (selfId.Contains("uid=0"))
            {
                rootViaAdbRoot = true;
                return selfId;
            }
            rootViaAdbRoot = false;

            string last = "";
            for (int pass = 0; pass < 2; pass++)
            {
                if (pass > 0)
                {
                    // exploit run နေတုန်း `adb root` ကို မခေါ်ရ — userdebug build မှာ adbd restart ဖြစ်ပြီး
                    // run နေတဲ့ exploit shell သေတယ် (root မရခင် ပျက်သွားနိုင်တယ်)
                    if (exploitRunActive) return last;
                    // ဒုတိယ pass: adb root ကို စမ်း (KernelSU ADB Root ဖွင့်ထားရင် adbd restart ဖြစ်ပြီး root ဖြစ်တယ်)
                    await ExecuteCommandQuickAsync("adb.exe", "root");
                    await Task.Delay(1500);
                    string rootId = (await ExecuteCommandQuickAsync("adb.exe", "shell id")).Trim();
                    if (rootId.Contains("uid=0"))
                    {
                        rootViaAdbRoot = true;
                        return rootId;
                    }
                    if (last.Length == 0) return "";   // su လုံးဝမရှိ — settle မလိုတော့
                    await Task.Delay(1000);            // KernelSU/legacy su daemon settle
                }
                foreach (string cmd in SuProbeCommands)
                {
                    string s = (await ExecuteCommandQuickAsync("adb.exe", cmd)).Trim();
                    if (s.Contains("uid=0"))
                    {
                        // root ရတဲ့ လမ်းကို မှတ် — FRP Reset (ROOT) က ဒီ handle ကိုပဲ သုံးမယ
                        rootHandlePrefix = cmd.Replace("shell ", "").Replace(" id", "").Trim();
                        return s;
                    }
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
            if (!File.Exists(local))
            {
                // path အရှည်ကြီး မပြ — ပြီးတော့ [2/4] နံပါတ် မကျော်စေရ
                Log("[2/4] Token payload (MST) — not bundled, skipped", Color.Gray);
                Log("     expected: exploits\\payloads\\mst-temproot.elf", Color.Gray);
                return (false, -1);
            }
            try { ToolIntegrity.Verify(local); }
            catch (Exception exMst)
            {
                Log("[i] PMK payload integrity unavailable (" + exMst.Message + ") — skip token root.", Color.Gray);
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
            bool apkMode = string.Equals(ex.RunMode, "apk", StringComparison.OrdinalIgnoreCase);
            Log("[3/4] Kernel exploit: " + (string.IsNullOrWhiteSpace(ex.Name) ? ex.Id : ex.Name) +
                (apkMode ? " [apk]" : hostMode ? " [host]" : " [device]") +
                (attempt > 0 ? " · retry " + attempt + "/1" : ""), Color.Cyan);

            // A7 per-boot ledger: one entry gets one outcome per boot. A root or a panic recorded for
            // this boot blocks a rerun; a failed-but-alive run does not (that is what the retry is for).
            lastExploitAttemptId = ex.Id;
            string ledgerBlock = await BootLedgerBlockAsync(ex) ?? "";
            if (ledgerBlock.Length > 0)
            {
                if (ledgerBlock == "rooted")
                {
                    Log("[!] " + ex.Id + " already produced root on this boot - not running it again.", Color.OrangeRed);
                    Log("[i] Root is live - stop here and do not reboot.", Color.Orange);
                }
                else
                {
                    Log("[!] " + ex.Id + " panicked the kernel on this boot - not running it again.", Color.OrangeRed);
                    Log("[i] Reboot once to start a clean boot, then retry only if root is still missing. " +
                        "A second panic means this profile does not fit this phone.", Color.Orange);
                }
                return (false, -1);
            }

            // DirtyFrag လမ်း (DFRoot/DirtyFrag): floor က hint ပဲ — APK ကိုယ်တိုင် vulnerability စစ်တယ်။
            // ဒါပေမယ့် KMI က အရေးကြီး — loader က KMI တစ်ခုစီအတွက် kernel module သီးသန့် တင်တယ်။
            if (IsDirtyFragExploit(ex))
            {
                string dirtyNote = KernelExploit.DirtyFragFixNote(fp.Kernel, fp.Patch);
                if (!string.IsNullOrEmpty(dirtyNote)) Log("[i] " + dirtyNote, Color.Orange);
                string kmi = KernelExploit.KmiTarget(fp.Kernel);
                if (kmi != null)
                    Log("[i] KMI " + kmi + " · " + KernelExploit.KmiModuleNote(kmi, fp.Kernel), Color.Gray);
                else
                    Log("[i] KMI မသိ (kernel string မှာ -androidN- tag မပါ) — module load မအောင်နိုင်ခြေ ရှိတယ်။",
                        Color.Gray);
            }

            if (apkMode) return await TryApkUiExploitAsync(exploitsRoot, ex, fp);

            if (!hostMode && IsGhostLockExploit(ex))
            {
                // DFRoot က ဒီဖုန်းမှာ temp root ရဖူးပြီ — GhostLock ကို လုံးဝ မစမ်းတော့ဘူး (panic အန္တရာယ်)
                if (DfrootVerifiedFor(selectedAdbSerial))
                {
                    Log("[i] GhostLock (CVE-2026-43499) ကို မစမ်းတော့ဘူး — DFRoot က ဒီဖုန်းမှာ temp root ရဖူးပြီ။", Color.Gray);
                    Log("[i] ပြန်ဖွင့်ချင်ရင် ဒီဖိုင်ထဲက DfrootVerifiedSerials လိုင်းကို ဖျက်ပါ: " + SettingsFile, Color.Gray);
                    return (false, -1);
                }
                string fixNote = KernelExploit.GhostLockFixNote(fp.Kernel, fp.Patch);
                if (!string.IsNullOrEmpty(fixNote)) Log("[i] " + fixNote, Color.Orange);
                // per-entry gate: measured list → published fix floor → refuse before pushing anything
                string patchedNote = KernelExploit.PatchedKernelNote(exploitsRoot, fp.Kernel, fp.Patch);
                if (patchedNote != null)
                {
                    Log("[!] UNSUPPORTED FIRMWARE — " + fp.Kernel, Color.OrangeRed);
                    Log("    " + patchedNote, Color.Orange);
                    Log("[i] GhostLock (CVE-2026-43499) ကို မစမ်းတော့ဘူး — kernel မှာ fix ရှိပြီးသား " +
                        "(push/run လုံးဝ မလုပ်ပါ)။", Color.Orange);
                    Log("[i] A reboot or a rebuilt GLK1 profile cannot change a patched kernel.", Color.Gray);
                    return (false, -1);
                }
                // root script က manager ရဲ့ libksud.so ကို ရှာပြီး module late-load လုပ်တယ် — မရှိရင် su မရဘဲ ရပ်တယ်
                await EnsureKernelSuForGhostLockAsync(exploitsRoot);
            }

            string local;
            try { local = KernelExploit.PayloadFullPath(exploitsRoot, ex); }
            catch (Exception exPath)
            {
                // manifest .. escape / missing integrity entry — async void ထဲကို exception မလွှတ်ရ
                Log("[!] Payload path rejected: " + exPath.Message, Color.OrangeRed);
                Log("[i] exploits\\manifest.json ထဲက Path ကို စစ်ပါ (exploits folder အတွင်းသာ ခွင့်ပြု)။", Color.Gray);
                return (false, -1);
            }
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
            // GhostLock timeout policy: a hard kill can land in the middle of a write. The process gets a
            // grace window past the policy timeout, and a run killed with no verdict is never retried.
            bool runTimedOut = false;
            int runGraceSec = 0;

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
                        string compLocal;
                        try { compLocal = KernelExploit.CompanionFullPath(exploitsRoot, ex, compRel); }
                        catch (Exception exComp)
                        {
                            // manifest escape / missing integrity entry — ဒီ companion ကို ကျော်၊ exploit ကို မရပ်ဘူး
                            Log("[!] Companion path rejected (skip): " + exComp.Message, Color.Orange);
                            continue;
                        }
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
                            // GhostLock (ExactProfile) က exact match ပဲ လက်ခံ — profile တစ်ခုက kernel တစ်ခုအတွက်ပဲ
                            bool prefix = !ex.ExactProfile && !string.IsNullOrWhiteSpace(fp.Kernel) &&
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
                bool earlyRoot = false;
                string earlyEvidence = "";
                int tickSec = 0;
                runGraceSec = IsGhostLockExploit(ex) ? 90 : 0;
                int hardTimeoutSec = timeoutSec + runGraceSec;
                bool graceLogged = false;
                exploitRunActive = true;
                var expTask = ReviewSafety.RunQuickAsync(ResolveToolPath("adb.exe"), runCmd,
                    hardTimeoutSec * 1000, true);
                try
                {
                    // Root ကို ချက်ချင်းသိအောင်: exploit run နေတုန်း 10s တစ်ခါ su probe / GhostLock ksu log ကို စစ်။
                    // (stdout မထွက်ဘဲ sigsuspend မှာ ရပ်နေတတ်တဲ့ GhostLock အတွက် 240s timeout ကုန်တာကို မစော့ရ၊
                    //  root ရပြီဆိုရင် ချက်ချင်းရပ် → DFRoot fallback ကို မဆက်ဘူး)
                    while (!expTask.IsCompleted && tickSec < hardTimeoutSec)
                    {
                        await Task.Delay(5000);
                        if (expTask.IsCompleted) break;
                        tickSec += 5;
                        if (runGraceSec > 0 && !graceLogged && tickSec >= timeoutSec)
                        {
                            graceLogged = true;
                            Log("[i] GhostLock still running at the " + timeoutSec + "s policy timeout - waiting up to " +
                                runGraceSec + "s more instead of killing a write in flight.", Color.Orange);
                        }
                        if (tickSec % 30 == 0)
                            Log("[i] Exploit running... " + tickSec + "s / " + hardTimeoutSec + "s", Color.Gray);
                        if (tickSec % 10 != 0 || tempRootAborted) continue;
                        // quick probe: `su -c id` တစ်ခုတည်း၊ 5s — ProbeSuAsync ရဲ့ 7-command ladder က
                        // 240s budget ထဲမှာ ~105s စောင့်နိုင်တယ်။ `adb root` လည်း မခေါ်ရ။
                        string suNow = (await ProbeSuQuickAsync()).Trim();
                        if (suNow.Contains("uid=0"))
                        {
                            earlyRoot = true;
                            earlyEvidence = "quick su probe · " + suNow;
                            break;
                        }
                        string ksuNow = (await ExecuteCommandQuickAsync("adb.exe",
                            "shell cat /data/local/tmp/.ghostlock_ksu.log")).Trim();
                        if (ksuNow.IndexOf("root script start uid=0", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            ksuNow.IndexOf("KernelSU module loaded", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            ksuNow.IndexOf("KernelSU ready", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            earlyRoot = true;
                            earlyEvidence = "GhostLock ksu log · uid=0";
                            break;
                        }
                    }
                    if (earlyRoot)
                    {
                        if (realSec < 0) realSec = tickSec;
                        Log("[OK] Root detected while the exploit was still running — " + earlyEvidence, Color.LightGreen);
                        Log("[i] GhostLock ကို ချက်ချင်းရပ်လိုက်ပြီ (timeout ကို မစော့ပါ) · DFRoot fallback ကို မဆက်ပါ။", Color.Cyan);
                        await ExecuteCommandQuickAsync("adb.exe",
                            "shell \"kill -9 $(pidof ghostlock) 2>/dev/null; true\"");
                        if (!expTask.IsCompleted) await Task.WhenAny(expTask, Task.Delay(5000));
                        suPathCache.Clear(); // root တက်ပြီ → PATH su ပြန်ရှာ
                    }
                    try { runOut = expTask.IsCompleted ? await expTask : ""; }
                    catch { runOut = ""; }
                }
                finally
                {
                    // exploit shell က ကျန်နေရင်တောင် `adb root` ကို ပြန်ခွင့်ပြု (adbd restart က အဲဒီ shell ကို သတ်မယ်)
                    exploitRunActive = false;
                }
                // A kill at the hard timeout leaves no verdict and may have cut a write in half.
                runTimedOut = !expTask.IsCompleted && !earlyRoot;
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
                else if (!earlyRoot)
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
                    "Complete any dialog in the tool window, then re-run Android Temp Root.", Color.OrangeRed);
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
            // NOT terminal: "… route failed; backing off" — the loader drops one route and tries the next.
            if (KernelExploit.GhostLockBackedOffRoute(sigSrc))
                Log("[i] GhostLock က route တစ်ခုကို စွန့်ပြီး fallback ကို ပြောင်းတယ် (`route failed; backing off`) — " +
                    "ဒါ verdict မဟုတ်ဘူး၊ run က ဆက်သွားနေတယ်။", Color.Gray);

            bool w1Exhausted = KernelExploit.GhostLockWriteFailed(sigSrc);
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

            // A timeout kill can land in the middle of a GhostLock write. There is no verdict to read, so a
            // same-boot retry would stack a second write on an unknown page state - stop and let the user
            // check root, then reboot once before trying again.
            if (runTimedOut && IsGhostLockExploit(ex))
            {
                Log("[!] GhostLock hit the " + (timeoutSec + runGraceSec) + "s hard timeout with no verdict - " +
                    "the process was killed.", Color.OrangeRed);
                Log("[i] A write may have been in flight. Do not retry on this boot:", Color.Orange);
                Log("    1) check root: adb shell su -c id  (or /data/local/tmp/.ghostlock_ksu.log)", Color.Gray);
                Log("    2) if root did not appear, reboot once and run again.", Color.Gray);
                return (false, realSec);
            }

            if (attempt == 0)
            {
                // module live ဖြစ်နေပြီဆိုရင် root ရပြီး — reboot က temp root ကို ဖျက်မယ်၊ မ reboot ဘဲ ရပ်
                if (await RootModuleLiveAsync())
                {
                    Log("[!] KernelSU module is live — reboot က temp root ကို ဖျက်ပစ်မယ်။ Auto-reboot မလုပ်တော့ဘူး.", Color.Orange);
                    Log("[i] adb shell ဆီ su မရောက်ရင် KernelSU Manager → Settings → ADB Root ဖွင့်ပါ " +
                        "(သို့) root လိုတဲ့ app ကို manager မှာ ခွင့်ပြုပါ။", Color.Orange);
                    return (false, realSec);
                }
                if (KernelExploit.GhostLockHeapSprayFailed(sigSrc))
                    Log("[i] Heap spray က futex collision မတွေ့ဘူး — heap layout က probabilistic မို့ reboot က " +
                        "တကယ် ပြောင်းလဲနိုင်တယ်။ ဒါက profile verdict မဟုတ်ဘူး။", Color.Orange);
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

        // ================= DFRoot / DirtyFrag — APK + UI automation =================
        // DFRoot is not an ELF payload: the exploit needs the app UID, a selected manager that
        // ships libksud.so, and its own UI run. Install the pinned APKs, pick the manager, tap
        // Run twice, then prove root with the su probe. The APKs are verified by the SHA-256
        // pinned in the manifest (they are installed, not pushed, so integrity.json does not
        // cover them).
        /// <summary>DFRoot က ksud ကို SU manager app ရဲ့ nativeLibraryDir/libksud.so ကနေ stage လုပ်တယ်။
        /// ဒါကြောင့် install ထားတဲ့ manager တွေထဲက libksud.so ပါတာကို ရှာတယ် (KernelSU ကို ဦးစားပေး)။</summary>
        private async Task<string> FindSuManagerPackageAsync()
        {
            string list = await ExecuteCommandQuickAsync("adb.exe", "shell pm list packages", 15000);
            var installed = new List<string>();
            foreach (string raw in (list ?? "").Replace("\r", "").Split('\n'))
            {
                string l = raw.Trim();
                if (l.StartsWith("package:", StringComparison.Ordinal)) installed.Add(l.Substring(8).Trim());
            }
            string[] known = { "me.weishu.kernelsu", "com.dergoogler.mmrl", "com.topjohnwu.magisk", "me.bmax.apatch" };
            var hits = new List<string>();
            foreach (string want in known) if (installed.Contains(want)) hits.Add(want);
            foreach (string p in installed)
                if (!hits.Contains(p) &&
                    (p.IndexOf("kernelsu", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     p.IndexOf("sukisu", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     p.IndexOf("bakasu", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     p.IndexOf("apatch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     p.IndexOf("magisk", StringComparison.OrdinalIgnoreCase) >= 0))
                    hits.Add(p);
            foreach (string p in hits)
            {
                string pmPath = await ExecuteCommandQuickAsync("adb.exe", "shell pm path " + p, 8000);
                int at = pmPath.IndexOf("package:", StringComparison.Ordinal);
                if (at < 0) continue;
                string apk = pmPath.Substring(at + 8).Split('\n')[0].Trim();
                int slash = apk.LastIndexOf('/');
                if (slash <= 0) continue;
                string probe = await ExecuteCommandQuickAsync("adb.exe",
                    "shell ls " + apk.Substring(0, slash) + "/lib/arm64/libksud.so 2>/dev/null", 8000);
                if (probe.IndexOf("libksud.so", StringComparison.Ordinal) >= 0) return p;
            }
            return "";
        }

        /// <summary>Headless autorun: `am start -n &lt;Launch&gt; --es autorun 1 --es su_manager &lt;pkg&gt;`.
        /// APK ကိုယ်တိုင် Run ကို ၂ ချက် နှိပ်သလို လုပ်တာမို့ INJECT_EVENTS မလိုဘူး။
        /// Attempted=false = ဒီ APK က autorun မပံ့ပိုးဘူး (UI path ကို ဆက်သင့်တယ်)။</summary>
        private async Task<(bool Attempted, bool Root, double Sec)> RunHeadlessAutorunAsync(
            ExploitEntry ex, ApkUiSpec ui, string managerPkg)
        {
            var sw = Stopwatch.StartNew();
            await ExecuteCommandQuickAsync("adb.exe", "shell am force-stop " + ui.Package, 8000);
            await ExecuteCommandQuickAsync("adb.exe", "shell logcat -c", 8000);
            Log("[*] Headless autorun: " + ui.Package + " · su_manager=" + managerPkg, Color.Cyan);
            string started = await ExecuteCommandQuickAsync("adb.exe",
                "shell am start -n " + ui.Launch + " --es autorun 1 --es su_manager " + managerPkg, 20000);
            if (started.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Log("[i] am start: " + started.Replace("\r", "").Replace("\n", " ").Trim(), Color.Gray);
                return (false, false, -1);
            }

            int timeoutSec = ex.TimeoutSec <= 0 ? 240 : ex.TimeoutSec;
            DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
            string logText = "";
            bool supported = false, failed = false;
            int tick = 0;
            while (DateTime.UtcNow < deadline && !tempRootAborted)
            {
                await Task.Delay(5000);
                tick += 5;
                logText = await AdbRunAsync("logcat -d -s dfroot", 20000);
                if (!supported)
                {
                    if (logText.IndexOf("autorun=1", StringComparison.Ordinal) >= 0)
                    {
                        supported = true;
                        Log("[OK] Headless autorun accepted — UI tap မလိုပါ။", Color.LightGreen);
                    }
                    else if (tick >= 15)
                    {
                        Log("[i] Install ထားတဲ့ " + (ui.Package ?? "APK") + " က autorun မပံ့ပိုးပါ — UI path ကို ပြောင်းမယ်။", Color.Orange);
                        return (false, false, -1);
                    }
                    continue;
                }
                if (await RootModuleLiveAsync())
                {
                    sw.Stop();
                    LogDfrootResult(logText, "rooted");
                    string suId = await ProbeSuQuickAsync();
                    if (suId.Contains("uid=0"))
                    {
                        Log("[OK] Temp root OK (headless) — " + sw.Elapsed.TotalSeconds.ToString("0.0") + "s · " + suId, Color.LightGreen);
                        suPathCache.Clear();
                        return (true, true, sw.Elapsed.TotalSeconds);
                    }
                    RootProbeState hlState = await ProbeRootStateAsync();
                    Log("[OK] Temp root IS active — " + (hlState.Verdict == RootVerdict.ModuleLoaded
                        ? hlState.Summary : "kernelsu module is live"), Color.LightGreen);
                    Log("[i] KernelSU Manager → Settings → ADB Root ဖွင့်ပြီး Detect ပြန်နှိပ်ပါ (Tool ကိုယ်တိုင် adb root လုပ်လို့ မရပါ)။", Color.Orange);
                    Log("[i] Reboot မလုပ်ပါနဲ့ — temp root ပျက်သွားပါမယ်။", Color.Orange);
                    return (true, false, -1);
                }
                if (logText.IndexOf("=== exploit failed:", StringComparison.Ordinal) >= 0)
                {
                    failed = true;
                    break;
                }
                if (tick % 30 == 0)
                    Log("[i] Headless run running... " + tick + "s / " + timeoutSec + "s", Color.Gray);
            }
            sw.Stop();
            LogDfrootResult(logText, failed ? "failed" : "timeout");
            if (!failed)
            {
                string suId2 = await ProbeSuQuickAsync();
                if (suId2.Contains("uid=0"))
                {
                    Log("[OK] Temp root OK (headless) — " + suId2, Color.LightGreen);
                    suPathCache.Clear();
                    return (true, true, sw.Elapsed.TotalSeconds);
                }
            }
            Log("[!] Headless autorun က temp root မရဘူး (" + (failed ? "exploit failed" : "timeout") + ").", Color.OrangeRed);
            return (true, false, -1);
        }

        // Xiaomi/MIUI (HyperOS) မှာ streaming `adb install` ကို Play-Protect verifier ကြောင့်
        // VERIFICATION_FAILURE ဖြစ်တတ်တယ်။ verifier နှစ်ခုကို install မလုပ်ခင် ခဏပိတ်ပြီး၊ မရရင်
        // /data/local/tmp သို့ push လုပ်၍ `pm install` နဲ့ တိုက်ရိုက် တင်တယ်။
        // ဘယ်လမ်းနဲ့ပြီးပြီးပါစေ ဖုန်းရဲ့ မူလတန်ဖိုးကို finally မှာ ပြန်ထားတယ် — customer ဖုန်းမှာ
        // Play Protect/verifier ပိတ်လျက် မကျန်ရစ်စေရ။
        private async Task<(bool Ok, string Output)> InstallApkRobustAsync(string local, string package)
        {
            var previous = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string key in ApkInstallPolicy.VerifierKeys)
                previous[key] = await ReadGlobalSettingAsync(key);
            try
            {
                foreach (string key in ApkInstallPolicy.VerifierKeys)
                    await ExecuteCommandQuickAsync("adb.exe", "shell settings put global " + key + " 0", 8000);

                string insOut = await AdbRunAsync("install -r \"" + local + "\"", 300000);
                if (insOut.IndexOf("Success", StringComparison.OrdinalIgnoreCase) >= 0)
                    return (true, insOut);

                // Fallback — push + pm install (streaming install ကို ROM က ငြင်းတဲ့အခါ ဒီလမ်း ရတယ်)
                string remote = ApkInstallPolicy.RemoteStagingPath(local);
                await AdbRunAsync("push \"" + local + "\" \"" + remote + "\"", 180000);
                string pmOut = await AdbRunAsync("shell pm install -r \"" + remote + "\"", 300000);
                bool ok = pmOut.IndexOf("Success", StringComparison.OrdinalIgnoreCase) >= 0;
                await AdbRunAsync("shell rm -f \"" + remote + "\"", 8000);
                return (ok, insOut + "\n" + pmOut);
            }
            finally
            {
                foreach (string key in ApkInstallPolicy.VerifierKeys)
                    await RestoreGlobalSettingAsync(key, previous[key]);
            }
        }

        /// <summary>`settings get global &lt;key&gt;` ရဲ့ တန်ဖိုး။ မသတ်မှတ်ထားရင် "" (settings က "null" ပြန်တယ်)။
        /// ဘယ်လို ပြန်ထားမလဲ ဆိုတာ <see cref="ApkInstallPolicy.ParseSettingValue"/> က ဆုံးဖြတ်တယ် (phone မလိုဘဲ test လုပ်နိုင်)။</summary>
        private async Task<string> ReadGlobalSettingAsync(string key)
        {
            string raw = (await ExecuteCommandQuickAsync("adb.exe", "shell settings get global " + key, 8000)) ?? "";
            return ApkInstallPolicy.ParseSettingValue(raw);
        }

        /// <summary>မူလတန်ဖိုး ရှိရင် ပြန်ရေး၊ မရှိခဲ့ရင် key ကို ဖျက် — ဖုန်းကို မူလအတိုင်း ပြန်ထားတယ်။</summary>
        private async Task RestoreGlobalSettingAsync(string key, string previous)
        {
            // ဖုန်းက ပြန်ပေးတဲ့ တန်ဖိုးကို shell ထဲ မျက်ကန်း မထည့်ပါ — မထင်မှတ်တဲ့ စာလုံးပါရင် key ကို ဖျက်ပြီး
            // platform default ကို ပြန်ရောက်စေတယ် (လုံခြုံရေးအရ ဒါက ပိုမှန်တယ်)။
            if (!string.IsNullOrEmpty(previous) && !ApkInstallPolicy.IsRestorableValue(previous))
                Log("[!] " + key + " returned an unexpected value; restoring the platform default instead.", Color.Orange);
            await ExecuteCommandQuickAsync("adb.exe", ApkInstallPolicy.RestoreCommand(key, previous), 8000);
        }

        // GhostLock root script က device ပေါ်က KernelSU manager ရဲ့ libksud.so ကို ရှာပြီး kernelsu.ko ကို
        // late-load လုပ်တယ်။ manager မရှိရင် run က "KernelSU module load pending" မှာ ရပ်ပြီး adb shell က
        // root မရဘူး။ ဒါကြောင့် exploit မစမီ bundled (hash-pinned) KernelSU APK ကို install ပေးတယ်။
        // Install မရရင် exploit ကို မရပ်ပါ — root အလုပ်မလုပ်တာကို log မှာ ရှင်းရှင်းပြပါမယ်။
        private async Task EnsureKernelSuForGhostLockAsync(string exploitsRoot)
        {
            const string package = "me.weishu.kernelsu";
            try
            {
                if (await ApkInstalledAsync(package))
                {
                    Log("[i] KernelSU manager present — GhostLock can late-load kernelsu.ko.", Color.Gray);
                    return;
                }

                string apk = "";
                ExploitManifest manifest = KernelExploit.LoadManifest(Path.Combine(exploitsRoot, "manifest.json"));
                foreach (ExploitEntry entry in manifest.Exploits)
                {
                    if (entry.Packages == null) continue;
                    foreach (ExploitPackage pkg in entry.Packages)
                    {
                        if (!string.Equals(pkg.Package, package, StringComparison.Ordinal)) continue;
                        try { apk = KernelExploit.PackageFullPath(exploitsRoot, entry, pkg); }
                        catch (Exception exPkg) { Log("[!] KernelSU APK rejected: " + exPkg.Message, Color.Orange); }
                        break;
                    }
                    if (apk.Length > 0) break;
                }

                if (apk.Length == 0 || !File.Exists(apk))
                {
                    Log("[!] KernelSU manager APK not bundled — GhostLock will stop at 'KernelSU module load pending' " +
                        "and adb shell stays unrooted.", Color.Orange);
                    return;
                }

                Log("[*] Install KernelSU manager · " + Path.GetFileName(apk) + " (ksud provider for GhostLock)...",
                    Color.Cyan);
                (bool ok, string insOut) = await InstallApkRobustAsync(apk, package);
                if (ok)
                {
                    Log("[OK] KernelSU manager installed — GhostLock can late-load kernelsu.ko.", Color.LightGreen);
                    return;
                }
                Log("[!] KernelSU manager install failed — GhostLock root will stay without su.", Color.OrangeRed);
                foreach (string raw in (insOut ?? "").Replace("\r", "").Split('\n'))
                    if (raw.Trim().Length > 0) Log("    " + raw.Trim(), Color.OrangeRed);
            }
            catch (Exception ex)
            {
                Log("[!] KernelSU manager step skipped: " + ex.Message, Color.Orange);
            }
        }

        // MIUI/HyperOS ရဲ့ "Install via USB" (DISALLOW_INSTALL_USB) က adb/pm install ကို ပိတ်တယ်။
        // Mi account မလိုဘဲ တင်နိုင်တဲ့လမ်း = sideload (package installer / File Manager) မှတစ်ဆင့်တင်။
        // "Install unknown apps" ကို allow ပေးရုံပါ — "Install via USB"/Mi account လုံးဝ မလိုပါ။
        private async Task<bool> SideloadApkAsync(ExploitPackage spec, string local)
        {
            string label = string.IsNullOrWhiteSpace(spec.Package) ? Path.GetFileName(local) : spec.Package;
            string remote = "/sdcard/Download/" + Path.GetFileName(local);
            Log("[*] Sideload " + Path.GetFileName(local) + " → " + remote + " ...", Color.Cyan);
            await AdbRunAsync("push \"" + local + "\" \"" + remote + "\"", 180000);
            // Package installer ကို တိုက်ရိုက် ဖွင့် — install screen တစ်ခါတည်း ပေါ်လာမယ် (frptool လို)။
            string uri = "file://" + remote;
            string startOut = await AdbRunAsync("shell am start -W -a android.intent.action.VIEW " +
                "-d \"" + uri + "\" -t application/vnd.android.package-archive --user 0", 30000);
            if (startOut.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0)
                Log("[i] Installer ဖွင့်လို့မရ — ဖုန်းရဲ့ File Manager ကနေ " + remote +
                    " ကို ကိုယ်တိုင် ဖွင့်ပါ။", Color.Orange);
            Log("[sideload] ဖုန်းပေါ်ပေါ်လာတဲ့ 'Install' ကို နှိပ်ပါ — 'Install via USB'/Mi account မလိုပါ။",
                Color.Orange);
            if (string.IsNullOrWhiteSpace(spec.Package)) return true;
            return await WaitForManualInstallAsync(spec.Package, label, 300);
        }

        private async Task<(bool Root, double Sec)> TryApkUiExploitAsync(string exploitsRoot, ExploitEntry ex, DeviceFingerprint fp)
        {
            ApkUiSpec ui = ex.Ui ?? new ApkUiSpec();
            if (ex.Packages is not { Length: > 0 } || string.IsNullOrWhiteSpace(ui.Launch) ||
                string.IsNullOrWhiteSpace(ui.RunButtonId))
            {
                Log("[!] APK exploit entry needs Packages, Ui.Launch and Ui.RunButtonId.", Color.OrangeRed);
                return (false, -1);
            }

            var installs = new List<(ExploitPackage Spec, string Local)>();
            foreach (ExploitPackage pkg in ex.Packages)
            {
                try
                {
                    string local = KernelExploit.PackageFullPath(exploitsRoot, ex, pkg);
                    installs.Add((pkg, local));
                    Log("[OK] APK verified · " + Path.GetFileName(local) + " · " + pkg.Sha256.Substring(0, 12) + "…",
                        Color.LightGreen);
                }
                catch (Exception exPkg)
                {
                    Log("[!] " + exPkg.Message, Color.OrangeRed);
                    Log("[i] Expected at exploits\\" + (pkg.Path ?? "?"), Color.Gray);
                    return (false, -1);
                }
            }

            if (!await EnsureTempRootDeviceConnectedAsync())
            {
                Log("[!] Device not connected — APK exploit skipped.", Color.OrangeRed);
                return (false, -1);
            }
            bool inputOk = await UiInputAllowedAsync();
            bool manual = !inputOk;
            if (manual)
            {
                Log("[i] Manual mode — tool က touch မထိုးပါ။ (DFRoot headless ဖြစ်လို့ input injection မလိုပါ)", Color.Cyan);
                Log("[i] APK တစ်ခုချင်းစီအတွက် install screen ကို အလိုအလျောက် ဖွင့်ပေးမယ် — 'Install' ကိုပဲ နှိပ်ပါ။", Color.Cyan);
                Log("[i] ဖုန်း screen ကို unlock လုပ်ထားပြီး လက်ထဲမှာ ကိုင်ထားပါ။", Color.Gray);
            }

            if (manual)
            {
                foreach (var (spec, local) in installs)
                {
                    string label = string.IsNullOrWhiteSpace(spec.Package) ? Path.GetFileName(local) : spec.Package;
                    bool present = !string.IsNullOrWhiteSpace(spec.Package) && await ApkInstalledAsync(spec.Package);
                    if (present)
                    {
                        Log("[i] " + label + " already installed — skip.", Color.Gray);
                        continue;
                    }
                    // Manual mode (INJECT_EVENTS ပိတ်ထားတဲ့အခါ) — push လုပ်ပြီး package installer ကို
                    // တိုက်ရိုက် ဖွင့်။ install screen တစ်ခါတည်း ပေါ်တာမို့ File Manager ကိုယ်တိုင် ရှာစရာ မလို။
                    if (!await SideloadApkAsync(spec, local)) return (false, -1);
                }
            }
            else
            {
                foreach (var (spec, local) in installs)
                {
                    string label = string.IsNullOrWhiteSpace(spec.Package) ? Path.GetFileName(local) : spec.Package;
                    bool present = !string.IsNullOrWhiteSpace(spec.Package) && await ApkInstalledAsync(spec.Package);
                    Log("[*] Install " + Path.GetFileName(local) + " · " + label + (present ? " (already installed)" : "") + "...",
                        Color.Cyan);
                    (bool insOk, string insOut) = await InstallApkRobustAsync(local, spec.Package);
                    if (!insOk)
                    {
                        if (present && insOut.IndexOf("INSTALL_FAILED_UPDATE_INCOMPATIBLE", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (ui.Headless && !string.IsNullOrWhiteSpace(spec.Package))
                            {
                                // ကျွန်တော်တို့ရဲ့ headless build ကို လိုအပ်တယ် — signature မတူလို့ uninstall ပြီး အသစ်ပြန်တင်ရမယ်
                                Log("[i] " + label + " installed က signature မတူလို့ replace လုပ်မယ် (uninstall → install).", Color.Orange);
                                await AdbRunAsync("uninstall " + spec.Package, 60000);
                                (insOk, insOut) = await InstallApkRobustAsync(local, spec.Package);
                                if (!insOk)
                                {
                                    Log("[!] Headless APK install failed: " + label, Color.OrangeRed);
                                    foreach (string raw in (insOut ?? "").Replace("\r", "").Split('\n'))
                                        if (raw.Trim().Length > 0) Log("    " + raw.Trim(), Color.OrangeRed);
                                    return (false, -1);
                                }
                                Log("[OK] " + label + " replaced with the bundled headless build.", Color.LightGreen);
                            }
                            else
                                Log("[i] Installed " + label + " has a different signature — keeping the installed copy.", Color.Orange);
                        }
                        else
                        {
                            Log("[!] Install failed: " + label, Color.OrangeRed);
                            foreach (string raw in (insOut ?? "").Replace("\r", "").Split('\n'))
                                if (raw.Trim().Length > 0) Log("    " + raw.Trim(), Color.OrangeRed);
                            if (insOut.IndexOf("USER_RESTRICTED", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                Log("[i] MIUI/HyperOS 'Install via USB' ပိတ်ထားတယ် — Mi account မလိုဘဲ sideload နဲ့ တင်မယ်။",
                                    Color.Orange);
                                if (await SideloadApkAsync(spec, local)) continue;
                                return (false, -1);
                            }
                            else if (insOut.IndexOf("VERIFICATION", StringComparison.OrdinalIgnoreCase) >= 0)
                                Log("[i] Install verification rejected the APK — sideload နဲ့ ထပ်လုပ်ပါ။", Color.Orange);
                            return (false, -1);
                        }
                    }
                }
            }
            foreach (var (spec, _) in installs)
            {
                if (string.IsNullOrWhiteSpace(spec.Package)) continue;
                if (!await ApkInstalledAsync(spec.Package))
                {
                    Log("[!] " + spec.Package + " is not installed.", Color.OrangeRed);
                    return (false, -1);
                }
            }

            Log("[*] Launch " + ui.Launch + "...", Color.Cyan);
            // Headless (autorun) လမ်း: APK ကိုယ်တိုင် Run ကို ၂ ချက် နှိပ်သလို လုပ်တာမို့ touch မလိုဘူး
            if (ui.Headless)
            {
                string managerPkg = await FindSuManagerPackageAsync();
                if (string.IsNullOrWhiteSpace(managerPkg))
                    Log("[!] Headless: libksud ပါတဲ့ SU manager မတွေ့ပါ — UI path ကို ပြောင်းပါမယ်။", Color.Orange);
                else
                {
                    (bool hAttempted, bool hRoot, double hSec) = await RunHeadlessAutorunAsync(ex, ui, managerPkg);
                    if (hAttempted) return (hRoot, hSec);
                }
            }
            await ExecuteCommandQuickAsync("adb.exe", "shell input keyevent KEYCODE_WAKEUP", 8000);
            await ExecuteCommandQuickAsync("adb.exe", "shell wm dismiss-keyguard", 8000);
            await ExecuteCommandQuickAsync("adb.exe", "shell svc power stayon usb", 8000);
            if (!string.IsNullOrWhiteSpace(ui.Package))
                await ExecuteCommandQuickAsync("adb.exe", "shell am force-stop " + ui.Package, 8000);
            // logcat -c gives the run its own window; if it is refused the last "=== setup ==="
            // inside the dump still marks where this run begins.
            await ExecuteCommandQuickAsync("adb.exe", "shell logcat -c", 8000);
            string startOut = await ExecuteCommandQuickAsync("adb.exe", "shell am start -W -n " + ui.Launch, 30000);
            if (string.IsNullOrWhiteSpace(startOut) || startOut.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0)
                Log("[i] am start: " + (startOut ?? "").Replace("\r", "").Replace("\n", " ").Trim(), Color.Gray);

            XDocument doc = await WaitForUiAsync(d => UiFind(d, ui.RunButtonId, null) != null, 30);
            if (doc == null || UiFind(doc, ui.RunButtonId, null) == null)
            {
                Log("[!] App screen did not come up (uiautomator dump empty).", Color.OrangeRed);
                Log("[i] Unlock the phone and keep the screen on, then run again.", Color.Orange);
                return (false, -1);
            }

            XElement sub = UiFind(doc, ui.SuManagerSubtitleId, null);
            string subText = sub == null ? "" : UiText(sub);
            bool needManager = string.IsNullOrWhiteSpace(subText) ||
                               subText.IndexOf("Not selected", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               (!string.IsNullOrWhiteSpace(ui.SuManagerLabel) &&
                                subText.IndexOf(ui.SuManagerLabel, StringComparison.OrdinalIgnoreCase) < 0);
            if (needManager)
            {
                if (manual)
                {
                    string want = string.IsNullOrWhiteSpace(ui.SuManagerLabel) ? "KernelSU" : ui.SuManagerLabel;
                    Log("[manual] SU Manager row ကို tap → '" + want + "' ကို ရွေးပါ။", Color.Orange);
                    Log("[i] App က permission တောင်းရင် 'Access list of installed apps' ကို Allow ပေးပါ။",
                        Color.Gray);
                    XDocument sel = await WaitForUiAsync(d =>
                    {
                        if (UiAnyText(d, "No app with libksud.so found")) return true;
                        XElement s = UiFind(d, ui.SuManagerSubtitleId, null);
                        string t = s == null ? "" : UiText(s);
                        return !string.IsNullOrWhiteSpace(t) &&
                               t.IndexOf("Not selected", StringComparison.OrdinalIgnoreCase) < 0 &&
                               (string.IsNullOrWhiteSpace(ui.SuManagerLabel) ||
                                t.IndexOf(ui.SuManagerLabel, StringComparison.OrdinalIgnoreCase) >= 0);
                    }, 180);
                    if (sel != null && UiAnyText(sel, "No app with libksud.so found"))
                    {
                        Log("[!] DFRoot က installed apps list ကို မမြင်ပါ — HyperOS privacy permission ပိတ်ထားတယ်။",
                            Color.OrangeRed);
                        Log("[i] Settings → Apps → DirtyFrag → Permissions → Other permissions → " +
                            "\"Access list of installed apps\" → Always allow.", Color.Orange);
                        return (false, -1);
                    }
                    XElement s2 = sel == null ? null : UiFind(sel, ui.SuManagerSubtitleId, null);
                    subText = s2 == null ? "" : UiText(s2);
                    if (string.IsNullOrWhiteSpace(subText) ||
                        subText.IndexOf("Not selected", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Log("[!] SU manager still not selected (" + subText + ").", Color.OrangeRed);
                        return (false, -1);
                    }
                    doc = sel;
                    Log("[OK] SU manager: " + subText, Color.LightGreen);
                }
                else
                {
                    Log("[*] Select SU manager: " + (string.IsNullOrWhiteSpace(ui.SuManagerLabel) ? "?" : ui.SuManagerLabel) + "...",
                        Color.Cyan);
                    XElement row = UiFind(doc, ui.SuManagerRowId, null);
                    if (row == null || !await UiTapAsync(row))
                    {
                        Log("[!] SU Manager row not found on screen.", Color.OrangeRed);
                        return (false, -1);
                    }
                    XDocument pop = await WaitForUiAsync(d => UiFindChoice(d, ui.SuManagerLabel) != null ||
                                                              UiAnyText(d, "No app with libksud.so found"), 8);
                    if (pop != null && UiAnyText(pop, "No app with libksud.so found"))
                    {
                        // HyperOS hides the package list from apps unless this MIUI privacy op is allowed;
                        // DFRoot then sees zero candidates even though KernelSU's libksud.so exists.
                        Log("[!] DFRoot က installed apps list ကို မမြင်ပါ — HyperOS privacy permission ပိတ်ထားတယ်။",
                            Color.OrangeRed);
                        Log("[i] Settings → Apps → DirtyFrag → Permissions → Other permissions → " +
                            "\"Access list of installed apps\" → Always allow.", Color.Orange);
                        Log("[i] ပြီးရင် Android Temp Root ကို ထပ်နှိပ်ပါ။", Color.Gray);
                        return (false, -1);
                    }
                    XElement choice = pop == null ? null : UiFindChoice(pop, ui.SuManagerLabel);
                    if (choice != null) await UiTapAsync(choice);
                    else if (!await TapFirstSuManagerEntryAsync(row))
                    {
                        Log("[!] SU manager list did not appear.", Color.OrangeRed);
                        return (false, -1);
                    }
                    await Task.Delay(1200);
                    doc = await DumpUiAsync() ?? doc;
                    sub = UiFind(doc, ui.SuManagerSubtitleId, null);
                    subText = sub == null ? "" : UiText(sub);
                    if (string.IsNullOrWhiteSpace(subText) ||
                        subText.IndexOf("Not selected", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Log("[!] SU manager still not selected (" + subText + ").", Color.OrangeRed);
                        return (false, -1);
                    }
                    Log("[OK] SU manager: " + subText, Color.LightGreen);
                }
            }
            else Log("[i] SU manager already selected: " + subText, Color.Gray);

            var sw = Stopwatch.StartNew();
            bool moduleSeen = false;
            string successSpec = string.IsNullOrWhiteSpace(ui.SuccessText) ? "Rooted" : ui.SuccessText;
            string failureSpec = string.IsNullOrWhiteSpace(ui.FailureText) ? "Reboot" : ui.FailureText;
            bool Matches(string text, string spec) => !string.IsNullOrWhiteSpace(text) &&
                spec.Split('|', StringSplitOptions.RemoveEmptyEntries)
                    .Any(w => text.IndexOf(w.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
            XElement run = UiFind(doc, ui.RunButtonId, null);
            string runText = run == null ? "" : UiText(run);
            string verdict;
            if (Matches(runText, successSpec))
            {
                verdict = "rooted";
                Log("[OK] App already reports Rooted — verifying with su...", Color.LightGreen);
            }
            else if (Matches(runText, failureSpec))
            {
                Log("[!] DFRoot: the hook already fired this boot without su (" + runText + ").", Color.OrangeRed);
                Log("[i] /dev/df is one-shot — reboot the phone, then run once.", Color.Orange);
                return (false, -1);
            }
            else
            {
                bool runEnabled = !string.Equals(run?.Attribute("enabled")?.Value, "false", StringComparison.OrdinalIgnoreCase);
                if (!runEnabled)
                {
                    Log("[!] Run button is disabled (" + runText + ").", Color.OrangeRed);
                    return (false, -1);
                }
                int timeoutSec = ex.TimeoutSec <= 0 ? 240 : ex.TimeoutSec;
                verdict = "timeout";

                if (manual)
                {
                    int grace = timeoutSec + 60;
                    Log("[manual] Run exploit ကို tap ပါ — 'Are you sure?' ပေါ်လာရင် ထပ်နှိပ်ပါ။ " +
                        "Tool က " + grace + "s စောင့်ပါမယ်။", Color.Orange);
                    DateTime deadline = DateTime.UtcNow.AddSeconds(grace);
                    int tick = 0;
                    bool reminded = false;
                    while (DateTime.UtcNow < deadline && !tempRootAborted)
                    {
                        await Task.Delay(5000);
                        tick += 5;
                        // module က live ဖြစ်တာနဲ့ ချက်ချင်း သိ — app UI text ကို စောင့်မနေဘူး
                        if (!moduleSeen && await RootModuleLiveAsync())
                        {
                            moduleSeen = true;
                            verdict = "rooted";
                            Log("[OK] Temp root went live during the run — detected immediately (KernelSU module).", Color.LightGreen);
                            break;
                        }
                        XDocument cur = await DumpUiAsync();
                        if (cur == null) continue;      // exploit busy / UI thread blocked — try again
                        XElement btn = UiFind(cur, ui.RunButtonId, null);
                        string bt = btn == null ? "" : UiText(btn);
                        if (Matches(bt, successSpec)) { verdict = "rooted"; break; }
                        if (Matches(bt, failureSpec)) { verdict = "no-su"; break; }
                        if (!reminded &&
                            bt.IndexOf("Are you sure", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            Log("[manual] ဖုန်းပေါ်မှာ 'Are you sure?' ကို ထပ်နှိပ်ပါ။", Color.Orange);
                            reminded = true;
                        }
                        if (bt.IndexOf("Run exploit", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            string.Equals(btn.Attribute("enabled")?.Value, "false", StringComparison.OrdinalIgnoreCase))
                        { verdict = "failed"; break; }
                        if (tick % 30 == 0)
                            Log("[i] DFRoot waiting for your taps / running... " + tick + "s / " + grace + "s",
                                Color.Gray);
                    }
                }
                else
                {
                    Log("[*] Tap Run exploit (two-step confirm)...", Color.Cyan);
                    await UiTapAsync(run);
                    XDocument armed = await WaitForUiAsync(d =>
                    {
                        XElement b = UiFind(d, ui.RunButtonId, null);
                        return b != null && UiText(b).IndexOf("Are you sure", StringComparison.OrdinalIgnoreCase) >= 0;
                    }, 8);
                    XElement armBtn = armed == null ? null : UiFind(armed, ui.RunButtonId, null);
                    if (armBtn == null ||
                        UiText(armBtn).IndexOf("Are you sure", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        Log("[!] Run button did not arm — screen locked or tap missed.", Color.OrangeRed);
                        return (false, -1);
                    }
                    await UiTapAsync(armBtn);

                    DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
                    int tick = 0;
                    while (DateTime.UtcNow < deadline && !tempRootAborted)
                    {
                        // 5s, not 1s: uiautomator dump costs CPU, and the hook it is watching is a race.
                        await Task.Delay(5000);
                        tick += 5;
                        // module က live ဖြစ်တာနဲ့ ချက်ချင်း သိ — app UI text ကို စောင့်မနေဘူး
                        if (!moduleSeen && await RootModuleLiveAsync())
                        {
                            moduleSeen = true;
                            verdict = "rooted";
                            Log("[OK] Temp root went live during the run — detected immediately (KernelSU module).", Color.LightGreen);
                            break;
                        }
                        XDocument cur = await DumpUiAsync();
                        if (cur == null) continue;      // exploit busy / UI thread blocked — try again
                        XElement btn = UiFind(cur, ui.RunButtonId, null);
                        string bt = btn == null ? "" : UiText(btn);
                        if (Matches(bt, successSpec)) { verdict = "rooted"; break; }
                        if (Matches(bt, failureSpec)) { verdict = "no-su"; break; }
                        if (bt.IndexOf("Run exploit", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            string.Equals(btn.Attribute("enabled")?.Value, "false", StringComparison.OrdinalIgnoreCase))
                        { verdict = "failed"; break; }
                        if (tick % 30 == 0)
                            Log("[i] DFRoot running... " + tick + "s / " + timeoutSec + "s", Color.Gray);
                    }
                }
            }

            string runLog = await AdbRunAsync("logcat -d -s dfroot", 20000);
            LogDfrootResult(runLog, verdict);

            string suId = "";
            // module တကယ္ live ျဖစ္ေနရင္ su ကို ၾကာၾကာ မေစာင့္ေတာ့ဘူး (jailbreak mode မွာ su မလာဘူး)
            int suTries = moduleSeen ? 2 : 6;
            for (int i = 0; i < suTries; i++)
            {
                suId = await ProbeSuAsync();
                if (suId.Contains("uid=0")) break;
                if (i == 0) await ExecuteCommandQuickAsync("adb.exe", "root", 10000);
                if (i < suTries - 1) await Task.Delay(5000);
            }
            sw.Stop();
            if (suId.Contains("uid=0"))
            {
                Log("[OK] DFRoot temp root OK — " + sw.Elapsed.TotalSeconds.ToString("0.0") + "s · " + suId,
                    Color.LightGreen);
                suPathCache.Clear();
                return (true, sw.Elapsed.TotalSeconds);
            }
            // KernelSU jailbreak/LKM mode: module က root ပေးနေပေမဲ့ adb shell ကို su မပေးဘူး —
            // ဒါကို "exploit မအောင်" လို့ မဆုံးဖြတ်ရဘူး (reboot အကြံပြုရင် temp root ပျက်သွားမယ်)
            RootProbeState afterRun = await ProbeRootStateAsync();
            if (moduleSeen || afterRun.Verdict == RootVerdict.ModuleLoaded)
            {
                Log("[OK] Temp root IS active — " + (afterRun.Verdict == RootVerdict.ModuleLoaded
                    ? afterRun.Summary
                    : "kernelsu module was live during the run"), Color.LightGreen);
                if (afterRun.Hint != null) Log("[i] " + afterRun.Hint, Color.Orange);
                Log("[i] KernelSU Manager → Settings → ADB Root ဖွင့်ပြီး Detect ပြန်နှိပ်ပါ " +
                    "(Tool ကိုယ်တိုင် adb root လုပ်လို့ မရပါ)။", Color.Orange);
                Log("[i] Reboot မလုပ်ပါနဲ့ — temp root ပျက်သွားပါမယ်။", Color.Orange);
                return (false, -1);   // root ရှိပေမဲ့ adb shell က မသုံးနိုင်သေး
            }
            Log("[!] DFRoot finished without uid=0 (" + verdict + ").", Color.OrangeRed);
            if (runLog.IndexOf("DEVICE NOT VULNERABLE", StringComparison.OrdinalIgnoreCase) >= 0)
                Log("[i] Kernel က DirtyFrag page-cache write ကို ငြင်းတယ် (patched) — " +
                    "reboot လုပ်လည်း ထပ်မရပါ။ အခြား method လိုပါမယ်။", Color.Orange);
            else
                Log("[i] Reboot ပြီး တစ်ကြိမ်ပဲ ပြန်စမ်းပါ — /dev/df mutex က boot တစ်ခါ တစ်ချက်ပဲ။", Color.Orange);
            return (false, -1);
        }

        /// <summary>DF Root က root ရတာနဲ့ ချက်ချင်းသိဖို့ — /proc/modules ပဲ ဖတ်တဲ့ ပေါ့ပါးတဲ့ စစ်ဆေးမှု။
        /// su probe မလုပ်ဘူး (KernelSU prompt ကို ခဏခဏ မပေါ်စေရ)။
        /// tri-state: true = module live · false = module တကယ် မရှိ · null = adb အဖြေမရ (unknown)။
        /// module load ပြီးချိန် adbd ခဏ restart ဖြစ်ရင် `cat /proc/modules` က "" ပြန်တယ် — အဲဒါကို
        /// "module ပျောက်ပြီ" လို့ မယူရဘူး (မှားတဲ့ "Temp root module is gone" log ရဲ့ အရင်း)။</summary>
        private async Task<bool?> RootModuleProbeAsync()
        {
            string modules = await ExecuteCommandQuickAsync("adb.exe", "shell cat /proc/modules", 8000);
            // device မရှိ/offline ချိန် adb က error text ပြန်တယ် — အဲဒါ output မဟုတ်
            if (IsAdbNoiseAnswer(modules)) return null;
            return RootProbe.RootModules(modules).Count > 0;
        }

        /// <summary>unknown (null) ကို false အနေနဲ့ ပြန်တယ် — flow ရဲ့ ယခင် အပြုအမူ မပြောင်း။
        /// poll လို "state မပြောင်းရ" နေရာတွေက RootModuleProbeAsync ကို တိုက်ရိုက် သုံးရမယ်။</summary>
        private async Task<bool> RootModuleLiveAsync()
        {
            bool? probe = await RootModuleProbeAsync();
            return probe == true;
        }

        /// <summary>MIUI/HyperOS revokes INJECT_EVENTS from shell unless "USB debugging
        /// (Security settings)" is on; without it every input tap fails with SecurityException.</summary>
        private async Task<bool> UiInputAllowedAsync()
        {
            // input writes its SecurityException to stderr and exits non-zero — QuickAsync would
            // return "" and look like a pass, so use AdbRunAsync (stdout+stderr on failure).
            string probe = await AdbRunAsync("shell input keyevent 0", 10000);
            if (probe.IndexOf("INJECT_EVENTS", StringComparison.OrdinalIgnoreCase) < 0 &&
                probe.IndexOf("SecurityException", StringComparison.OrdinalIgnoreCase) < 0)
                return true;
            Log("[!] ADB input injection is blocked on this phone (INJECT_EVENTS).", Color.OrangeRed);
            Log("[i] 'USB debugging (Security settings)' ဖွင့်ထားရင် tool က အလိုအလျောက် tap လုပ်နိုင်မယ်။ " +
                "မဖွင့်နိုင်ရင် manual mode နဲ့ ဆက်မယ် — APK install screen ကို အလိုအလျောက် ဖွင့်ပေးတာမို့ " +
                "'Install' ကိုပဲ နှိပ်ပါ (Mi account မလိုပါ)။", Color.Gray);
            return false;
        }

        /// <summary>Run adb with the selected serial and always return stdout/stderr, even on failure.</summary>
        private async Task<string> AdbRunAsync(string arguments, int timeoutMs)
        {
            try
            {
                string bound = await BindDeviceArgumentsAsync("adb.exe", arguments);
                return await ReviewSafety.RunQuickAsync(ResolveToolPath("adb.exe"), bound, timeoutMs, true, true);
            }
            catch (Exception ex)
            {
                Log("[!] adb " + arguments.Split(' ')[0] + ": " + ex.Message, Color.Orange);
                return "";
            }
        }

        private async Task<bool> ApkInstalledAsync(string package)
        {
            string p = (await ExecuteCommandQuickAsync("adb.exe", "shell pm path " + package, 10000)).Trim();
            return p.IndexOf("package:", StringComparison.Ordinal) >= 0;
        }

        /// <summary>Manual mode: the APK was pushed to /sdcard/Download and the user installs it
        /// from the phone's file manager; poll until PackageManager reports it.</summary>
        private async Task<bool> WaitForManualInstallAsync(string package, string label, int timeoutSec)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
            int tick = 0;
            while (DateTime.UtcNow < deadline && !tempRootAborted)
            {
                await Task.Delay(3000);
                tick += 3;
                if (await ApkInstalledAsync(package))
                {
                    Log("[OK] " + label + " installed.", Color.LightGreen);
                    return true;
                }
                if (tick % 30 == 0)
                    Log("[i] Waiting for manual install: " + label + " · " + tick + "s / " + timeoutSec + "s",
                        Color.Gray);
            }
            Log("[!] " + label + " was not installed within " + timeoutSec + "s.", Color.OrangeRed);
            return false;
        }

        private async Task<XDocument> DumpUiAsync()
        {
            const string remote = "/data/local/tmp/pmk_ui.xml";
            await ExecuteCommandQuickAsync("adb.exe", "shell rm -f " + remote, 5000);
            await ExecuteCommandQuickAsync("adb.exe", "shell uiautomator dump " + remote, 25000);
            string xml = await ExecuteCommandQuickAsync("adb.exe", "shell cat " + remote, 12000);
            if (string.IsNullOrWhiteSpace(xml) || xml.IndexOf("<hierarchy", StringComparison.Ordinal) < 0)
                return null;
            try { return XDocument.Parse(xml); }
            catch { return null; }
        }

        private async Task<XDocument> WaitForUiAsync(Func<XDocument, bool> ready, int timeoutSec)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
            XDocument last = null;
            while (DateTime.UtcNow < deadline)
            {
                last = await DumpUiAsync();
                if (last != null && ready(last)) return last;
                await Task.Delay(1000);
            }
            return last;
        }

        private static IEnumerable<XElement> UiNodes(XDocument doc) =>
            doc.Descendants().Where(e => e.Name.LocalName == "node");

        private static string UiText(XElement n) => n.Attribute("text")?.Value ?? "";

        private static bool UiAnyText(XDocument doc, string needle) =>
            UiNodes(doc).Any(n => UiText(n).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);

        private static XElement UiFind(XDocument doc, string resId, string textContains)
        {
            foreach (XElement n in UiNodes(doc))
            {
                string rid = n.Attribute("resource-id")?.Value ?? "";
                if (!string.IsNullOrWhiteSpace(resId))
                {
                    bool ridMatch = rid.Equals(resId, StringComparison.Ordinal) ||
                                    rid.EndsWith(":id/" + resId, StringComparison.Ordinal) ||
                                    rid.EndsWith("/" + resId, StringComparison.Ordinal);
                    if (!ridMatch) continue;
                }
                if (!string.IsNullOrWhiteSpace(textContains) &&
                    UiText(n).IndexOf(textContains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                return n;
            }
            return null;
        }

        /// <summary>Popup rows are plain TextViews: label text exact, no resource id.</summary>
        private static XElement UiFindChoice(XDocument doc, string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return null;
            return UiNodes(doc).FirstOrDefault(n =>
                string.IsNullOrWhiteSpace(n.Attribute("resource-id")?.Value) &&
                string.Equals(UiText(n).Trim(), label, StringComparison.OrdinalIgnoreCase));
        }

        private static Rectangle? UiRect(XElement n)
        {
            string b = n.Attribute("bounds")?.Value ?? "";
            Match m = Regex.Match(b, @"\[(-?\d+),(-?\d+)\]\[(-?\d+),(-?\d+)\]");
            if (!m.Success) return null;
            int x1 = int.Parse(m.Groups[1].Value), y1 = int.Parse(m.Groups[2].Value);
            int x2 = int.Parse(m.Groups[3].Value), y2 = int.Parse(m.Groups[4].Value);
            if (x2 <= x1 || y2 <= y1) return null;
            return new Rectangle(x1, y1, x2 - x1, y2 - y1);
        }

        private async Task<bool> UiTapAsync(XElement n)
        {
            Rectangle? r = UiRect(n);
            if (r == null) return false;
            string outText = await ExecuteCommandQuickAsync("adb.exe",
                "shell input tap " + (r.Value.Left + r.Value.Width / 2) + " " + (r.Value.Top + r.Value.Height / 2), 10000);
            if (outText.IndexOf("INJECT_EVENTS", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Log("[!] Tap blocked — turn on 'USB debugging (Security settings)'.", Color.OrangeRed);
                return false;
            }
            return true;
        }

        /// <summary>Fallback when a PopupWindow does not show up in the accessibility dump:
        /// the list opens under the SU Manager card, first row 6dp padding + 28dp half-row down.</summary>
        private async Task<bool> TapFirstSuManagerEntryAsync(XElement row)
        {
            Rectangle? r = UiRect(row);
            if (r == null) return false;
            string densityOut = await ExecuteCommandQuickAsync("adb.exe", "shell wm density", 8000);
            Match dm = Regex.Match(densityOut, @"(\d+)");
            float dp = (dm.Success ? int.Parse(dm.Groups[1].Value) : 420) / 160f;
            string sizeOut = await ExecuteCommandQuickAsync("adb.exe", "shell wm size", 8000);
            Match sm = Regex.Match(sizeOut, @"(\d+)x(\d+)");
            int x = sm.Success ? int.Parse(sm.Groups[1].Value) / 2 : r.Value.Left + r.Value.Width / 2;
            int y = r.Value.Bottom + (int)(10 * dp) + (int)(28 * dp);
            await ExecuteCommandQuickAsync("adb.exe", "shell input tap " + x + " " + y, 8000);
            return true;
        }

        /// <summary>Print this run's DFRoot logcat block (setup → cleanup) with failure reason.</summary>
        private void LogDfrootResult(string runLog, string verdict)
        {
            string[] lines = (runLog ?? "").Replace("\r", "").Split('\n');
            int start = -1;
            for (int i = lines.Length - 1; i >= 0; i--)
                if (lines[i].IndexOf("=== setup ===", StringComparison.Ordinal) >= 0) { start = i; break; }
            string failReason = "";
            int shown = 0;
            bool headerShown = false;
            for (int i = start >= 0 ? start : 0; i < lines.Length && shown < 60; i++)
            {
                string t = lines[i].TrimEnd();
                if (t.Length == 0) continue;
                int tagAt = t.IndexOf(" dfroot ", StringComparison.Ordinal);
                if (tagAt >= 0)
                {
                    int colon = t.IndexOf(':', tagAt + 8);
                    t = colon >= 0 ? t.Substring(colon + 1).Trim() : t.Substring(tagAt + 8).Trim();
                }
                if (t.Length == 0) continue;
                const string failMark = "=== exploit failed: ";
                if (t.IndexOf(failMark, StringComparison.Ordinal) >= 0)
                    failReason = t.Substring(t.IndexOf(failMark, StringComparison.Ordinal) + failMark.Length)
                        .Trim().TrimEnd('=').Trim();
                bool bad = t.IndexOf("ERROR - ", StringComparison.Ordinal) >= 0 ||
                           t.IndexOf("=== exploit failed", StringComparison.Ordinal) >= 0 ||
                           t.IndexOf("DEVICE NOT VULNERABLE", StringComparison.Ordinal) >= 0;
                bool good = t.IndexOf("=== setup ===", StringComparison.Ordinal) >= 0 ||
                            t.IndexOf("=== exploit", StringComparison.Ordinal) >= 0 ||
                            t.IndexOf("=== init", StringComparison.Ordinal) >= 0 ||
                            t.IndexOf("=== cleanup ===", StringComparison.Ordinal) >= 0 ||
                            t.IndexOf("ksud start: SUCCESS", StringComparison.Ordinal) >= 0 ||
                            t.IndexOf("EXPLOIT SUCCESS", StringComparison.Ordinal) >= 0;
                bool detail = t.IndexOf("ksud:", StringComparison.Ordinal) >= 0 ||
                              t.IndexOf("bootstrap:", StringComparison.Ordinal) >= 0 ||
                              t.IndexOf("kernel module:", StringComparison.Ordinal) >= 0 ||
                              t.IndexOf("libc++:", StringComparison.Ordinal) >= 0;
                if (!bad && !good && !detail) continue;
                if (!headerShown)
                {
                    Log("[i] DFRoot log:", Color.Gray);
                    headerShown = true;
                }
                Log("   │ " + t, bad ? Color.OrangeRed : good ? Color.LightGreen : Color.Gray);
                shown++;
            }
            if (failReason.Length > 0)
                Log("[!] DFRoot: " + failReason, Color.OrangeRed);
            else if (verdict == "no-su")
                Log("[!] DFRoot: the hook fired but no su appeared — reboot, then run once.", Color.OrangeRed);
            else if (verdict == "timeout")
                Log("[!] DFRoot did not report a result within the timeout.", Color.OrangeRed);
        }

        // ================= HUAWEI DRIVER PACK (OPEN HUAWEI 2018) =================
        // csproj TargetPath = %(RecursiveDir)%(Filename)%(Extension) → output ထဲမှာ huawei-drivers\ (bundled-tools\ မပါ)

    }
}
