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
        // Fastboot command တွေအတွက် device ရှိ/မရှိ အရင်စစ် — မရှိရင် "Completed" ဆိုပြီး ဘာမှ မပြတာ မဖြစ်အောင်
        // Fastboot command တွေအတွက် device ရှိ/မရှိ အရင်စစ် — မရှိရင် ဘယ်လို ရောက်အောင် လုပ်ရမလဲ ရှင်းရှင်းပြ
        private async Task<bool> FastbootReadyAsync(string action)
        {
            string fb = await ExecuteCommandQuickAsync("fastboot.exe", "devices");
            if (!string.IsNullOrWhiteSpace(fb)) return true;

            string adb = await ExecuteCommandQuickAsync("adb.exe", "devices");
            if (!string.IsNullOrWhiteSpace(adb) && adb.Contains("\tdevice"))
            {
                Log("[!] No fastboot device - the phone is currently in ADB (Android) mode.", Color.Orange);
                Log("[i] To enter fastboot mode: ADB · REBOOT → Reboot Operations = 'Reboot Bootloader' → Execute Reboot, " +
                    "or hold Power + Vol- while connecting USB.", Color.Orange);
            }
            else
            {
                Log("[!] No device found (neither ADB nor fastboot) - connect the phone first.", Color.Orange);
                Log("[i] If the phone IS in fastboot mode but not detected: install the Android USB driver " +
                    "(Device Manager → 'Android Bootloader Interface').", Color.Orange);
            }
            Log("[i] (" + action + ": retry once the phone is in fastboot mode)", Color.Gray);
            return false;
        }

        // fastboot variant တချို့မှာ per-key တန်ဖိုးတွေ မှားလာတတ်တယ် (ဥပမာ current-slot → "yes")။
        // ဒါကြောင့် ကိန်း/bool ဖြစ်ရမယ့် key တွေကို စစ်ပြီး မဖြစ်ရင် ပစ်တယ် (မှားတဲ့ info မပြရအောင်)။
        private static bool PlausibleFastbootValue(string key, string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return false;
            string t = v.Trim();
            switch (key.ToLowerInvariant())
            {
                case "secure":
                case "unlocked":
                    return t == "0" || t == "1" || t.Equals("yes", StringComparison.OrdinalIgnoreCase) || t.Equals("no", StringComparison.OrdinalIgnoreCase);
                case "current-slot":
                    return t == "_a" || t == "_b" || t == "a" || t == "b";
                case "slot-count":
                case "anti":
                case "battery-voltage":
                case "off-mode-charge":
                    return t.All(char.IsDigit);
                case "max-download-size":
                    return t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || t.All(char.IsDigit);
                default:
                    return true;
            }
        }

        private static Dictionary<string, string> ParseFastbootVars(string outp)
        {
            Dictionary<string, string> vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(outp)) return vars;
            foreach (string raw in outp.Split('\n'))
            {
                string ln = raw.Replace("(bootloader)", "").Trim();
                int c = ln.IndexOf(':');
                if (c <= 0) continue;
                string k = ln.Substring(0, c).Trim();
                string v = ln.Substring(c + 1).Trim();
                if (k.Length > 0 && !k.Contains(" ") && !k.Contains("=")) vars[k] = v;
            }
            return vars;
        }

        // fastboot getvar output ထဲက "လိုအပ်တဲ့" variable တွေပဲ ပြတယ် (အားလုံး 100+ လိုင်း မပြ)။
        // 'getvar all' ကို မထောက်ပံ့တဲ့ fastboot variant (ဥပမာ fastbootd) ဆိုရင် key တစ်ခုချင်း မေးတဲ့ လမ်းကို သုံးတယ်။
        // အပြည့်အစုံကို %TEMP%\pmk_fastboot_vars.txt မှာ သိမ်းထားတယ်။
        private async Task ShowFastbootVarsAsync(string args, string title)
        {
            string outp = await ExecuteCommandQuickAsync("fastboot.exe", args);
            Dictionary<string, string> vars = ParseFastbootVars(outp);

            // ပြမယ့် key တွေ (အစဉ်လိုက်)
            string[][] show = new string[][] {
                new[] { "product",        "Product" },
                new[] { "variant",        "Variant" },
                new[] { "device",         "Codename" },
                new[] { "board",          "Board" },
                new[] { "version-bootloader", "Bootloader" },
                new[] { "version-baseband",   "Baseband" },
                new[] { "version-preloader",  "Preloader" },
                new[] { "version",        "BL version" },
                new[] { "secure",         "Secure boot" },
                new[] { "unlocked",       "Bootloader unlocked" },
                new[] { "current-slot",   "Current slot" },
                new[] { "slot-count",     "Slot count" },
                new[] { "anti",           "ARB index (anti)" },
                new[] { "max-download-size", "Max download size" },
                new[] { "battery-voltage", "Battery (mV)" },
                new[] { "off-mode-charge", "Off-mode charge" },
                new[] { "serialno",       "Serial" },
                new[] { "cpuid",          "CPU ID" },
            };

            // 'getvar all' မရရင် (FAILED / unknown command) → key တစ်ခုချင်း ပြန်မေး
            bool allFailed = vars.Count == 0 || outp.IndexOf("FAILED", StringComparison.OrdinalIgnoreCase) >= 0;
            string rawAll = outp;
            int rejected = 0;
            if (allFailed)
            {
                Log("[i] This fastboot does not support 'getvar all' - reading key variables one by one ...", Color.Gray);
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                foreach (string[] row in show)
                {
                    string r = await ExecuteCommandQuickAsync("fastboot.exe", "getvar " + row[0]);
                    sb.Append(r).Append("\n");
                    Dictionary<string, string> one = ParseFastbootVars(r);
                    string v;
                    if (one.TryGetValue(row[0], out v) && PlausibleFastbootValue(row[0], v))   // key + တန်ဖိုး ကိုက်မှ လက်ခံ
                        vars[row[0]] = v;
                    else if (!string.IsNullOrWhiteSpace(v))
                        rejected++;
                }
                rawAll = sb.ToString();
            }

            Log("======================================", Color.FromArgb(0, 180, 255));
            Log(title.ToUpperInvariant(), Color.White);
            Log("======================================", Color.FromArgb(0, 180, 255));

            int shown = 0;
            foreach (string[] row in show)
            {
                string v;
                if (!vars.TryGetValue(row[0], out v) || string.IsNullOrWhiteSpace(v)) continue;
                Log(row[1].PadRight(22) + ": " + v, Color.White);
                shown++;
            }
            if (shown == 0)
                Log("(no key variables found - check that the phone is in fastboot mode)", Color.Orange);

            Log("--------------------------------------", Color.FromArgb(0, 180, 255));
            Log("[i] " + shown + " key variables shown.", Color.Gray);
            if (allFailed && shown > 0)
                Log("[i] Note: per-key queries used (this fastboot variant has no 'getvar all').", Color.Gray);
            if (allFailed && rejected > 0)
                Log("[i] " + rejected + " value(s) ignored (inconsistent on this fastboot variant).", Color.Gray);

            try
            {
                string dump = Path.Combine(Path.GetTempPath(), "pmk_fastboot_vars.txt");
                File.WriteAllText(dump, "=== " + args + " ===\n" + rawAll);
                Log("[i] Full dump: " + dump, Color.Gray);
            }
            catch (Exception ex)
            {
                Log("[!] Fastboot dump write failed: " + ex.Message, Color.Orange);
            }
        }

        // Current slot ဖတ်ပြီး A↔B toggle — ဖုန်းက A ဆို B, B ဆို A ချိန်းပေးတယ်
        private async void BtnFbSwitchSlot_Click(object sender, EventArgs e)
        {
            if (!await FastbootReadyAsync("Switch Slot")) return;

            string rawCur = await ExecuteCommandQuickAsync("fastboot.exe", "getvar current-slot");
            string rawSfx = await ExecuteCommandQuickAsync("fastboot.exe", "getvar slot-suffixes");
            string rawCnt = await ExecuteCommandQuickAsync("fastboot.exe", "getvar slot-count");
            string rawUsr = await ExecuteCommandQuickAsync("fastboot.exe", "getvar is-userspace");
            Log("---- slot info (raw) ----", Color.Gray);
            foreach (string raw in new[] { rawCur, rawSfx, rawCnt, rawUsr })
                foreach (string line in raw.Split('\n'))
                {
                    string t = line.Replace("(bootloader)", "").Trim();
                    if (t.Length > 0 && t.Contains(':') && !t.StartsWith("Finished", StringComparison.OrdinalIgnoreCase))
                        Log("   " + t, Color.Gray);
                }

            string before = SlotFromGetvar(rawCur);
            if (before != "a" && before != "b")
            {
                Log("[!] Cannot read current slot — " + (string.IsNullOrWhiteSpace(rawCur) ? "(no output)" : rawCur.Trim()), Color.Orange);
                Log("[i] (May be a non-A/B device/variant — check current-slot with 🔍 Read Fastboot)", Color.Gray);
                return;
            }

            string cnt = GetvarValue(rawCnt, "slot-count");
            string sfx = GetvarValue(rawSfx, "slot-suffixes").ToLowerInvariant().Replace("_", "").Trim();
            bool singleSlot = cnt == "1" ||
                (cnt.Length == 0 && sfx.Length > 0 && !(sfx.Contains('a') && sfx.Contains('b')));
            if (singleSlot)
            {
                Log($"[!] This phone is single-slot (A-only) — A⇄B toggle not possible (slot-count={cnt}, suffixes={(GetvarValue(rawSfx, "slot-suffixes") == "" ? "(empty)" : GetvarValue(rawSfx, "slot-suffixes"))})", Color.OrangeRed);
                return;
            }

            if (GetvarValue(rawUsr, "is-userspace").StartsWith("y", StringComparison.OrdinalIgnoreCase))
                Log("[i] fastbootd (userspace) mode — slot switch may only take effect after reboot", Color.Cyan);

            string next = before == "a" ? "b" : "a";
            string rawUnb = await ExecuteCommandQuickAsync("fastboot.exe", "getvar slot-unbootable:" + next);
            string rawSucc = await ExecuteCommandQuickAsync("fastboot.exe", "getvar slot-successful:" + next);
            string rawRetry = await ExecuteCommandQuickAsync("fastboot.exe", "getvar slot-retry-count:" + next);
            Log($"---- slot {next.ToUpperInvariant()} state ----", Color.Gray);
            foreach (string raw in new[] { rawUnb, rawSucc, rawRetry })
                foreach (string line in raw.Split('\n'))
                {
                    string t = line.Replace("(bootloader)", "").Trim();
                    if (t.Length > 0 && t.Contains(':') && !t.StartsWith("Finished", StringComparison.OrdinalIgnoreCase))
                        Log("   " + t, Color.Gray);
                }
            if (GetvarValue(rawUnb, "slot-unbootable:" + next).StartsWith("y", StringComparison.OrdinalIgnoreCase))
                Log($"[!] Slot {next.ToUpperInvariant()} is unbootable — bootloader may reject it (boot image missing/failed)", Color.Orange);

            Log($"[*] Current slot: {before.ToUpperInvariant()} → switching to {next.ToUpperInvariant()}...", Color.Orange);
            bool ok = await ExecuteCommandCleanAsync("fastboot.exe", "set_active " + next,
                $"Switch Slot {before.ToUpperInvariant()} → {next.ToUpperInvariant()}", showRawOutput: true);
            if (!ok) return;

            // verify — တစ်ခါ retry (bootloader settle အတွက်)
            await Task.Delay(500);
            string after = SlotFromGetvar(await ExecuteCommandQuickAsync("fastboot.exe", "getvar current-slot"));
            if (after != next && after != "a" && after != "b")
            {
                await Task.Delay(1500);
                after = SlotFromGetvar(await ExecuteCommandQuickAsync("fastboot.exe", "getvar current-slot"));
            }

            if (after == next)
            {
                Log($"[+] Slot switched — current slot is now {next.ToUpperInvariant()}.", Color.LightGreen);
                return;
            }

            if (after != "a" && after != "b")
            {
                Log("[i] set_active OK — no current-slot output when verifying (device may have rebooted)", Color.Gray);
                return;
            }

            Log($"[!] set_active OKAY but current-slot = {after.ToUpperInvariant()} still unchanged", Color.Orange);
            Log("[i] Some bootloaders apply the switch only after reboot — you can re-check in bootloader", Color.Gray);

            var ask = MessageBox.Show(
                "set_active က device က OKAY ပြန်ပေမဲ့ current-slot က " + after.ToUpperInvariant() + " အတိုင်းပါ။\n\n" +
                "တစ်ချို့ bootloader များ reboot ပြီးမှ switch ဖြစ်တယ်။\n" +
                "Reboot to bootloader ပြီး ပြန်စစ်မလား?",
                "Switch Slot", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (ask != DialogResult.Yes) return;

            Log("[*] Rebooting to bootloader...", Color.Orange);
            if (!await ExecuteCommandCleanAsync("fastboot.exe", "reboot-bootloader", "Reboot Bootloader", showRawOutput: true))
            {
                Log("[!] reboot-bootloader failed — reboot to bootloader manually and retry Switch Slot", Color.Orange);
                return;
            }

            string after2 = "";
            for (int i = 0; i < 45; i++)
            {
                await Task.Delay(1000);
                after2 = SlotFromGetvar(await ExecuteCommandQuickAsync("fastboot.exe", "getvar current-slot"));
                if (after2 == "a" || after2 == "b") break;
            }

            if (after2 == next)
                Log($"[+] Slot switched — current slot is now {next.ToUpperInvariant()} (applies after reboot).", Color.LightGreen);
            else if (after2 == before)
                Log($"[!] Already rebooted but current-slot = {after2.ToUpperInvariant()} — bootloader did not accept the switch (locked? / check the slot state block above)", Color.OrangeRed);
            else
                Log("[!] Device did not come back to fastboot after reboot — wait a moment and retry Switch Slot", Color.Orange);
        }

        // fastboot getvar output ထဲက value တန်ဖိုး (variant format အမျိုးမျိုးကို ကာကွယ်)
        private static string GetvarValue(string raw, string key)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var vars = ParseFastbootVars(raw);
            if (vars.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)) return v.Trim();
            var m = System.Text.RegularExpressions.Regex.Match(raw,
                System.Text.RegularExpressions.Regex.Escape(key) + @":[ \t]*([^\r\n]*)");
            return m.Success ? m.Groups[1].Value.Replace("(bootloader)", "").Trim() : "";
        }

        private static string SlotFromGetvar(string raw)
        {
            string v = GetvarValue(raw, "current-slot").Trim().TrimStart('_').ToLowerInvariant();
            if (v == "a" || v == "b") return v;
            var m = System.Text.RegularExpressions.Regex.Match(raw ?? "",
                @"current-slot:?\s*_?([ab])\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.ToLowerInvariant() : v;
        }

        // ===== MI FLASH · XIAOMI FASTBOOT FLASHING (MST style, flash_all.bat) =====

        private bool SetMiFlashOps(List<MiFlashOp> ops)
        {
            if (ops.Count == 0) return false;
            miFlashOps = ops;
            dgvMiParts.Rows.Clear();
            foreach (MiFlashOp op in ops)
            {
                string partCol = "", fileCol = "", offsetCol = "", lengthCol = "", sizeCol = "";
                if (op.Kind == "flash")
                {
                    partCol = op.Partition;
                    fileCol = Path.GetFileName(op.File);
                    long bytes = 0;
                    try { bytes = new FileInfo(op.File).Length; } catch { }
                    offsetCol = "0000000000";
                    lengthCol = bytes > 0 ? bytes.ToString("X10") : "0000000000";
                    sizeCol = FormatMiSize(bytes);
                }
                else if (op.Kind == "erase")
                {
                    partCol = op.Partition;
                    fileCol = "erase";
                }
                else if (op.Kind == "set_active")
                {
                    string r = op.Raw.StartsWith("set_active", StringComparison.OrdinalIgnoreCase)
                        ? op.Raw.Substring("set_active".Length).Trim() : op.Raw;
                    partCol = r;
                    fileCol = "set_active";
                }
                else if (op.Kind == "reboot")
                {
                    partCol = "reboot";
                }
                else
                {
                    int sp = op.Raw.IndexOf(' ');
                    partCol = sp > 0 ? op.Raw.Substring(0, sp) : op.Raw;
                    fileCol = sp > 0 ? op.Raw.Substring(sp + 1) : "";
                }
                dgvMiParts.Rows.Add(true, partCol, fileCol, offsetCol, lengthCol, sizeCol, op.Kind);
            }
            return true;
        }

        private static string FormatMiSize(long bytes)
        {
            if (bytes >= 1048576) return (bytes / 1048576.0).ToString("0.00", CultureInfo.InvariantCulture) + " MB";
            if (bytes >= 1024) return (bytes / 1024.0).ToString("0.00", CultureInfo.InvariantCulture) + " KB";
            return bytes + " B";
        }

        private void BtnMiRomFolder_Click(object sender, EventArgs e)
        {
            using var ofd = new OpenFileDialog
            {
                Title = "flash_all.bat ရွေးပါ",
                Filter = "MIUI flash script (flash_all*.bat)|flash_all*.bat|Batch files (*.bat)|*.bat"
            };
            if (lblMiRomPath.Tag is string last && File.Exists(last))
            {
                ofd.FileName = last;
                ofd.InitialDirectory = Path.GetDirectoryName(last);
            }
            else if (lblMiRomPath.Tag is string lastDir && Directory.Exists(lastDir))
                ofd.InitialDirectory = lastDir;
            if (ofd.ShowDialog(this) != DialogResult.OK) return;

            string bat = ofd.FileName;
            List<MiFlashOp> ops;
            try { ops = MiFlash.ParseBat(bat); }
            catch (Exception ex) { Log("[!] " + Path.GetFileName(bat) + " parse failed: " + ex.Message, Color.OrangeRed); return; }
            if (!SetMiFlashOps(ops))
            {
                Log("[!] " + Path.GetFileName(bat) + " — 0 fastboot commands — not a flash_all.bat", Color.Orange);
                MessageBox.Show(this,
                    "ဒီ .bat ထဲမှာ fastboot command မပါဘူး။\n\nMIUI Fastboot ROM ရဲ့ flash_all.bat\n(သို့) flash_all_lock.bat ကို ရွေးပါ။",
                    "Mi Flash", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            lblMiRomPath.Text = bat;
            lblMiRomPath.Tag = bat;
            Log(" Checking firmware type... ( FASTBOOT ( BAT ) )", Color.FromArgb(210, 210, 210));
            Log("[+] Mi Flash loaded — " + ops.Count(o => o.Kind == "flash") + " file(s), " +
                ops.Count + " op(s) — " + Path.GetFileName(bat), Color.LightGreen);
        }

        private async void BtnMiFlash_Click(object sender, EventArgs e)
        {
            await RunFlashWorkflowAsync(BtnMiFlashAsync, autoRebootAfter: false);
        }

        private async Task BtnMiFlashAsync()
        {
            if (isTaskRunning)
            {
                Log("[!] Another operation is running — wait for it to finish or press STOP.", Color.OrangeRed);
                return;
            }
            if (miFlashOps.Count == 0 || dgvMiParts.Rows.Count == 0)
            {
                Log("[!] No ROM loaded — pick '📄 flash_all.bat'", Color.Orange);
                return;
            }

            var selected = new List<MiFlashOp>();
            for (int i = 0; i < dgvMiParts.Rows.Count && i < miFlashOps.Count; i++)
                if (dgvMiParts.Rows[i].Cells[0].Value is bool cb && cb)
                    selected.Add(miFlashOps[i]);
            if (selected.Count == 0)
            {
                Log("[!] No op selected — tick at least one checkbox", Color.Orange);
                return;
            }

            int flashCount = selected.Count(o => o.Kind == "flash");
            DialogResult ask = MessageBox.Show(this,
                "MI Fastboot Flashing — " + flashCount + " file(s), " + selected.Count + " op(s)" +
                (chkMiEraseFirst.Checked ? " (+ " + MiFlash.EraseFirst.Length + " erase first)" : "") + "\n\n" +
                "Erase first: " + (chkMiEraseFirst.Checked ? "ON" : "OFF") +
                "    Auto Reboot: " + (chkMiAutoReboot.Checked ? "ON" : "OFF"),
                "MI Fastboot Flashing", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (ask != DialogResult.Yes) return;

            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log(" Operation : XIAOMI FASTBOOT FLASHING", Color.White);
            Log("========================================================", Color.FromArgb(0, 180, 255));

            if (!await FastbootReadyAsync("Mi Fastboot Flashing"))
            {
                Log(" Waiting fastboot device... Failed", Color.OrangeRed);
                return;
            }
            Log(" Waiting fastboot device... Ok", Color.LightGreen);

            string all = await ExecuteCommandQuickAsync("fastboot.exe", "getvar all");
            if (string.IsNullOrWhiteSpace(all))
            {
                Log(" Connecting to device...Failed", Color.OrangeRed);
                return;
            }
            Log(" Connecting to device...Ok", Color.LightGreen);
            // MST style DEVICE INFORMATION — key vars အနည်းငယ်သာ (partition-type/size dump ကို မပြ)
            var infoKeep = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "product", "serialno", "version-bootloader", "unlocked", "secure",
                "current-slot", "slot-count", "anti", "crc", "max-download-size", "is-userspace"
            };
            foreach (string line in all.Split('\n'))
            {
                string t = line.Replace("(bootloader)", "").Trim();
                if (t.Length == 0 || t.StartsWith("Finished", StringComparison.OrdinalIgnoreCase) ||
                    t.StartsWith("OKAY", StringComparison.OrdinalIgnoreCase) ||
                    t.StartsWith("Waiting for device", StringComparison.OrdinalIgnoreCase)) continue;
                int ci = t.IndexOf(':');
                if (ci <= 0) continue;
                string key = t.Substring(0, ci).Trim();
                if (!infoKeep.Contains(key)) continue;
                Log("  * " + key.PadRight(24) + "... " + t.Substring(ci + 1).Trim(),
                    Color.FromArgb(200, 210, 225));
            }

            stopRequested = false;
            var swMi = System.Diagnostics.Stopwatch.StartNew();
            long bytesDone = 0;
            // bat ထဲက guard (getvar partition-type / crc) — device မပြရင် မလုပ် (bat အတိုင်း)
            var guardSkipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (selected.Any(o => o.Cond.Length > 0))
            {
                selected.RemoveAll(o =>
                {
                    if (o.Cond.Length == 0) return false;
                    bool run = MiFlash.GuardMatches(o.Cond, all);
                    if (!run) guardSkipped.Add(o.Partition);
                    return !run;
                });
                if (guardSkipped.Count > 0)
                    Log(" Skipping (bat guard — partition not present): " + string.Join(", ", guardSkipped),
                        Color.FromArgb(210, 210, 210));
            }

            MiFlash.ValidateDevice(miFlashOps, all);
            if (selected.Count == 0) throw new InvalidDataException("No operations remain after device guards.");
            using var imageLease = await Task.Run(() => new MiFlash.ImageLease(selected));
            if (stopRequested) return;
            int failCount = 0, done = 0, total = selected.Count;

            if (chkMiEraseFirst.Checked)
            {
                // bat ထဲမှာ ပြီးတော့ erase လုပ်မယ့် partition တွေကို မထပ်ဖျက်နဲ့ —
                // guard ကြောင့် ကျော်ခံထားရတဲ့ partition (ဥပမာ opcust) လည်း မထပ်ကြိုးစားနဲ့
                var batErase = new HashSet<string>(
                    selected.Where(o => o.Kind == "erase").Select(o => o.Partition),
                    StringComparer.OrdinalIgnoreCase);
                foreach (string p in MiFlash.EraseFirst)
                {
                    if (batErase.Contains(p) || guardSkipped.Contains(p)) continue;
                    if (stopRequested) break;
                    bool okE = await ExecuteCommandCleanAsync("fastboot.exe", "erase " + p, "Erase [" + p + "]", quiet: true, quietFail: true);
                    Log(" Erasing [ erase ] -> [ " + p + " ]... " + (okE ? "Ok" : "Failed"), okE ? Color.LightGreen : Color.OrangeRed);
                    if (!okE) { workflowFailed = true; return; }
                }
            }

            foreach (MiFlashOp op in selected)
            {
                if (stopRequested) { Log("[!] Stopped by user.", Color.Orange); break; }
                done++;
                if (op.Kind == "reboot")
                {
                    // Auto Reboot က အဆုံးမှာ တစ်ခါထဲ — reboot row ကို ဒီမှာ ကျော်
                    SetProgress(done * 100 / Math.Max(1, total), "Mi Flash (" + done + "/" + total + ")");
                    continue;
                }

                if (op.Kind == "flash")
                {
                    if (!File.Exists(op.File))
                    {
                        Log(" Flashing [ " + Path.GetFileName(op.File) + " ] -> [ " + op.Partition + " ]... Failed (file not found)",
                            Color.OrangeRed);
                        failCount++;
                    }
                    else
                    {
                        long len = 0;
                        try { len = new FileInfo(op.File).Length; } catch { }
                        bool okF = await ExecuteCommandCleanAsync("fastboot.exe",
                            op.Options + " flash " + op.Partition + " \"" + op.File + "\"", "Mi Flash [" + op.Partition + "]", quiet: true, quietFail: true);
                        Log(" Flashing [ " + Path.GetFileName(op.File) + " ] -> [ " + op.Partition + " ]... " + (okF ? "Ok" : "Failed"),
                            okF ? Color.LightGreen : Color.OrangeRed);
                        if (okF) bytesDone += len; else failCount++;
                    }
                }
                else if (op.Kind == "erase")
                {
                    bool okE2 = await ExecuteCommandCleanAsync("fastboot.exe", op.Options + " erase " + op.Partition, "Mi Erase [" + op.Partition + "]", quiet: true, quietFail: true);
                    Log(" Erasing [ erase ] -> [ " + op.Partition + " ]... " + (okE2 ? "Ok" : "Failed"), okE2 ? Color.LightGreen : Color.OrangeRed);
                    if (!okE2) failCount++;
                }
                else
                {
                    bool okR = await ExecuteCommandCleanAsync("fastboot.exe", op.Raw, "Mi [" + op.Label + "]", quiet: true, quietFail: true);
                    Log(" fastboot " + op.Raw + "... " + (okR ? "Ok" : "Failed"), okR ? Color.LightGreen : Color.OrangeRed);
                    if (!okR) failCount++;
                }

                if (failCount > 0) { workflowFailed = true; break; }
                if (swMi.Elapsed.TotalSeconds >= 0.5 && bytesDone > 0)
                    UpdateFlashSpeed(bytesDone / 1048576.0 / swMi.Elapsed.TotalSeconds);
                SetProgress(done * 100 / Math.Max(1, total), "Mi Flash (" + done + "/" + total + ")");
            }

            // Auto Reboot — MST style တစ်ခါထဲ (flash fail ရင် မ reboot — half state ကနေ မစ)
            if (!stopRequested && chkMiAutoReboot.Checked)
            {
                if (failCount == 0)
                {
                    bool rb = await ExecuteCommandCleanAsync("fastboot.exe", "reboot", "Mi Reboot", quiet: true, quietFail: true);
                    Log(" Rebooting device... " + (rb ? "Ok" : "Failed"), rb ? Color.LightGreen : Color.OrangeRed);
                }
                else
                    Log("[!] Flash failed " + failCount + " op — Auto Reboot skipped.", Color.Orange);
            }

            TimeSpan e2 = swMi.Elapsed;
            string elapsed = e2.TotalMinutes >= 1
                ? (int)e2.TotalMinutes + " minutes " + e2.Seconds + " seconds"
                : e2.Seconds + " seconds";
            Log(" Elapsed Time... " + elapsed, Color.Yellow);
            Log(" Finished at local time: [" + DateTime.Now.ToString("dd.MM.yy HH:mm:ss") + "]", Color.Yellow);
            if (failCount > 0) Log("[!] " + failCount + " op(s) FAILED — check the log", Color.OrangeRed);
            ResetProgress();
        }

        private async void BtnFbInfo_Click(object sender, EventArgs e)
        {
            if (!await FastbootReadyAsync("Fastboot Full Info")) return;
            await ShowFastbootVarsAsync("getvar all", "Fastboot Info (key variables)");
        }

        private async void BtnFbFlash_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog ofd = new OpenFileDialog { Filter = "Image Files (*.img)|*.img" })
            {
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    string partition = Path.GetFileNameWithoutExtension(ofd.FileName).ToLower();
                    await ExecuteCommandCleanAsync("fastboot.exe", "flash " + partition + " \"" + ofd.FileName + "\"", "Flash [" + partition + "]");
                }
            }
        }

        private async void BtnFbBootTemp_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog ofd = new OpenFileDialog { Filter = "Image Files (*.img)|*.img" })
            {
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    await ExecuteCommandCleanAsync("fastboot.exe", "boot \"" + ofd.FileName + "\"", "Boot Temp Image");
                }
            }
        }

        private async void BtnFbErase_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show("Erase Userdata & Cache partitions?", "Warning", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                await ExecuteCommandCleanAsync("fastboot.exe", "erase cache", "Erase Cache");
                await ExecuteCommandCleanAsync("fastboot.exe", "erase userdata", "Erase Userdata");
            }
        }

    }
}
