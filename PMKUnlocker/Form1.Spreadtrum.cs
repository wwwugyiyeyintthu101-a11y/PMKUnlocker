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
        private string spdPacExtractedFrom = "";

        private string FindSpdDump()
        {
            string configured = ShopServices.FindTool(Application.StartupPath, "spd_dump.exe");
            if (!string.IsNullOrEmpty(configured)) return configured;
            string sprd = Path.Combine(Application.StartupPath, "spd", "sprd", "spd_dump.exe");
            if (File.Exists(sprd))
            {
                ToolIntegrity.Verify(sprd);
                return sprd;
            }
            return "spd_dump";
        }

        private string FindUnpac()
        {
            string u = FindFileInToolFolders("unpac.exe");
            return string.IsNullOrEmpty(u) ? "unpac" : u;
        }

        // PAC ကို unpac.exe နဲ့ ဖြည်တယ် (session တစ်ခုအတွင်း cache — ထပ်ဖြည်စရာ မလို)
        private async Task<bool> EnsurePacExtractedAsync()
        {
            if (string.IsNullOrEmpty(spdFirmwareFile) || !File.Exists(spdFirmwareFile))
            {
                ConfirmDialog.Show("PAC firmware ဖိုင် မရွေးရသေးပါ — 📂 PAC နဲ့ အရင် ရွေးပါ။", "PAC",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            if (!string.IsNullOrEmpty(spdPacExtractedFrom) &&
                string.Equals(spdPacExtractedFrom, spdFirmwareFile, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(spdExtractDir))
                return true;

            string dir = Path.Combine(Path.GetTempPath(),
                "pmk_spd_" + Path.GetFileNameWithoutExtension(spdFirmwareFile));
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex) { Log("[!] SPD extract dir create failed: " + ex.Message, Color.Red); }

            Log("[*] Extracting PAC (unpac): " + Path.GetFileName(spdFirmwareFile), Color.Cyan);
            bool ok = await ExecuteCommandCleanAsync(FindUnpac(),
                "-d \"" + dir + "\" extract \"" + spdFirmwareFile + "\"", "SPD - Unpack PAC", false, true);
            if (!ok)
            {
                Log("[x] PAC extract failed - check unpac.exe and the PAC file, then retry.", Color.Red);
                return false;
            }

            spdExtractDir = dir;
            spdPacExtractedFrom = spdFirmwareFile;
            Log("[OK] PAC extracted -> " + dir, Color.LightGreen);
            return true;
        }

        // FDL Prefix တည်ဆောက်ခြင်း (PAC ထဲမှဖြစ်စေ၊ spd folder ထဲမှဖြစ်စေ Auto ရှာယူခြင်း)
        private string BuildSpdFdlPrefix()
        {
            string fdl1 = "", fdl2 = "";

            // ၀။ SPD Loader row (user pick) — filename ထဲ fdl2 ပါရင် fdl2 အဖြစ်၊ မဟုတ်ရင် fdl1 override
            if (!string.IsNullOrEmpty(spdLoaderPath) && File.Exists(spdLoaderPath))
            {
                string ln = Path.GetFileName(spdLoaderPath).ToLowerInvariant();
                if (ln.Contains("fdl2")) fdl2 = spdLoaderPath;
                else fdl1 = spdLoaderPath;
            }

            // ၁။ PAC ဖြည်ထားတာ ရှိရင် အရင်သုံးပါ (user pick ရှိရင် အဲဒါပဲ ကျန်)
            if (Directory.Exists(spdExtractDir))
            {
                foreach (string f in Directory.GetFiles(spdExtractDir))
                {
                    string n = Path.GetFileName(f).ToLowerInvariant();
                    if (n.Contains("fdl1") && string.IsNullOrEmpty(fdl1)) fdl1 = f;
                    else if (n.Contains("fdl2") && string.IsNullOrEmpty(fdl2)) fdl2 = f;
                }
            }

            // ၂။ PAC မရှိပါက spd/loaders folder ထဲက FDL များကို အလိုအလျောက် ရှာဖွေပါ
            if (string.IsNullOrEmpty(fdl1) || string.IsNullOrEmpty(fdl2))
            {
                string loadersRoot = Path.Combine(Application.StartupPath, "spd", "loaders");
                if (Directory.Exists(loadersRoot))
                {
                    // ရွေးထားတဲ့ chipset ကို loader folder နဲ့ တိုက်စစ်ပါ (SpdModelMatchesLoader နဲ့ တူညီစွာ)
                    string selModel = _spdSelectedModel.Trim();
                    string matchedDir = "";

                    if (!string.IsNullOrEmpty(selModel))
                    {
                        foreach (string dir in Directory.GetDirectories(loadersRoot))
                        {
                            string dn = Path.GetFileName(dir).ToLowerInvariant();
                            int u = dn.IndexOf('_');
                            string chip = new string((u > 0 ? dn.Substring(0, u) : dn)
                                .Where(c => char.IsLetterOrDigit(c)).ToArray());
                            if (chip.Length > 0 && SpdModelMatchesLoader(selModel, new List<string> { chip }))
                            {
                                matchedDir = dir;
                                break;
                            }
                        }
                    }

                    // တိုက်မတွေ့ရင် ပထမဆုံး folder ကို သုံး
                    if (string.IsNullOrEmpty(matchedDir))
                    {
                        var dirs = Directory.GetDirectories(loadersRoot);
                        if (dirs.Length > 0)
                        {
                            matchedDir = dirs[0];
                            if (!string.IsNullOrEmpty(selModel))
                                Log("[!] Chipset without FDL loader: " + selModel +
                                    " — fallback " + Path.GetFileName(matchedDir) + " may be wrong", Color.Orange);
                        }
                    }

                    // folder တစ်ခုတည်းက fdl1+fdl2 နှစ်ခုလုံး ရှိမှ ယူပါ — တစ်ဝက်ကို တခြား chipset
                    // folder ကနေ ဖြည့်တာ မလုပ်တော့ပါ (မတူတဲ့ pair = SPD ဖုန်း brick)
                    FdlPair pair = !string.IsNullOrEmpty(matchedDir) ? SpdLoaderPair.FromFolder(matchedDir) : null;
                    if (pair == null)
                    {
                        List<string> others = Directory.GetDirectories(loadersRoot)
                            .Where(d => !string.Equals(d, matchedDir, StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        pair = SpdLoaderPair.FirstFromFolders(others);
                        if (pair != null && !string.IsNullOrEmpty(matchedDir))
                            Log("[!] No complete FDL pair in " + Path.GetFileName(matchedDir) +
                                " — using " + Path.GetFileName(pair.Folder), Color.Orange);
                    }
                    if (pair != null)
                    {
                        if (string.IsNullOrEmpty(fdl1)) fdl1 = pair.Fdl1;
                        if (string.IsNullOrEmpty(fdl2)) fdl2 = pair.Fdl2;
                    }
                }
            }

            // ၃။ တိုက်ရိုက် spd folder ထဲတွင် ရှိနေပါက ထပ်မံရှာဖွေပါ (folder တစ်ခုတည်းက pair ကိုသာ)
            if (string.IsNullOrEmpty(fdl1) || string.IsNullOrEmpty(fdl2))
            {
                string spdDir = Path.Combine(Application.StartupPath, "spd");
                if (Directory.Exists(spdDir))
                {
                    FdlPair pair = SpdLoaderPair.FromFolder(spdDir) ??
                        SpdLoaderPair.FirstFromFolders(Directory.GetDirectories(spdDir)
                            .Where(d => !string.Equals(Path.GetFileName(d), "loaders", StringComparison.OrdinalIgnoreCase)));
                    if (pair != null)
                    {
                        if (string.IsNullOrEmpty(fdl1)) fdl1 = pair.Fdl1;
                        if (string.IsNullOrEmpty(fdl2)) fdl2 = pair.Fdl2;
                    }
                }
            }

            if (!string.IsNullOrEmpty(fdl1) && !string.IsNullOrEmpty(fdl2) && !SpdLoaderPair.SameFolder(fdl1, fdl2))
                Log("[!] FDL1 and FDL2 come from different folders (" + Path.GetFileName(fdl1) + " + " +
                    Path.GetFileName(fdl2) + ") — a mismatched pair bricks an SPD phone", Color.Orange);

            ToolIntegrity.Verify(fdl1);
            ToolIntegrity.Verify(fdl2);
            string a1 = Environment.GetEnvironmentVariable("PMK_SPD_FDL1_ADDR") ?? "0x5000";
            string a2 = Environment.GetEnvironmentVariable("PMK_SPD_FDL2_ADDR") ?? "0x9efffe00";

            string prefix = "--wait 300";
            if (!string.IsNullOrEmpty(fdl1)) prefix += " fdl \"" + fdl1 + "\" " + a1;
            if (!string.IsNullOrEmpty(fdl2)) prefix += " fdl \"" + fdl2 + "\" " + a2;
            prefix += " exec";

            return prefix;
        }

        // ဖြည်ထားတဲ့ folder ထဲက partition images (.bin/.img, FDL မဟုတ်တာ)
        private List<string> SpdImageFiles(out List<string> parts)
        {
            List<string> files = new List<string>();
            parts = new List<string>();
            try
            {
                if (!Directory.Exists(spdExtractDir)) return files;
                foreach (string f in Directory.GetFiles(spdExtractDir))
                {
                    string name = Path.GetFileName(f);
                    string low = name.ToLowerInvariant();
                    string ext = Path.GetExtension(low);
                    if (ext != ".bin" && ext != ".img") continue;
                    if (low.Contains("fdl1") || low.Contains("fdl2")) continue;
                    if (low.Contains("xml") || low.Contains("nvitem") || low.Contains("pac")) continue;
                    files.Add(f);
                    parts.Add(Path.GetFileNameWithoutExtension(name));
                }
            }
            catch { }
            return files;
        }

        private async void BtnSpdPickFirmware_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog ofd = new OpenFileDialog
            {
                Filter = "Spreadtrum PAC (*.pac)|*.pac|All Files (*.*)|*.*",
                Title = "Spreadtrum PAC firmware ကို ရွေးပါ"
            })
            {
                if (ofd.ShowDialog() != DialogResult.OK) return;
                spdFirmwareFile = ofd.FileName;
                txtSpdFirmware.Text = spdFirmwareFile;
                spdPacExtractedFrom = ""; // cache reset

                ClearPartitionData(); // ဇယားဟောင်း ရှင်းထုတ်ခြင်း

                Log("========================================================", Color.FromArgb(0, 180, 255));
                Log("   SPREADTRUM PAC LOADED", Color.White);
                Log("========================================================", Color.FromArgb(0, 180, 255));
                Log("  • PAC  : " + Path.GetFileName(spdFirmwareFile), Color.Cyan);
                Log("  • Size : " + (new FileInfo(spdFirmwareFile).Length / (1024.0 * 1024.0)).ToString("F1") + " MB", Color.White);

                // PAC ဖိုင်ကို ဖြည်ပြီး Partition များကို ဇယားထဲ ထည့်သွင်းခြင်း
                if (await EnsurePacExtractedAsync())
                {
                    List<string> parts;
                    List<string> files = SpdImageFiles(out parts);
                    for (int i = 0; i < files.Count; i++)
                    {
                        long sz = new FileInfo(files[i]).Length;
                        AddPartitionRow(parts[i], "-", "0x" + sz.ToString("X"));
                    }
                    SetAllPartitionChecks(true);
                    Log("  • Partitions : " + parts.Count + " partitions loaded into list", Color.LightGreen);
                }
            }
        }

        private async void BtnSpdDirectFlash_Click(object sender, EventArgs e)
        {
            await RunFlashWorkflowAsync(BtnSpdDirectFlashAsync);
        }

        private async Task BtnSpdDirectFlashAsync()
        {
            if (!await EnsurePacExtractedAsync()) return;

            string spd = FindSpdDump();
            List<string> parts;
            List<string> files = SpdImageFiles(out parts);
            if (files.Count == 0)
            {
                ConfirmDialog.Show("ဖြည်ထားတဲ့ folder ထဲမှာ partition image (.bin/.img) မတွေ့ပါ။", "SPD",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string plan = "";
            for (int i = 0; i < files.Count && i < 20; i++)
                plan += "• " + parts[i].PadRight(16) + " <- " + Path.GetFileName(files[i]) + "\n";
            if (files.Count > 20) plan += "• ... (" + (files.Count - 20) + " more)\n";

            if (ConfirmDialog.Show("spd_dump (CLI) နဲ့ PAC flash လုပ်မလား?\n\n" + plan + "\n" +
                                "• ဖုန်းကို Download/FDL mode မှာ ထားပါ\n" +
                                "• ဖုန်းပိုင်ရှင် ခွင့်ပြုချက် ရှိမှ လုပ်ပါ\n\nဆက်လုပ်မလား?",
                    "Confirm PAC Flash", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            string args = BuildSpdFdlPrefix();
            for (int i = 0; i < files.Count; i++)
                args += " w " + parts[i] + " \"" + files[i] + "\"";
            args += " reset";

            await ExecuteCommandCleanAsync(spd, args, "Spreadtrum PAC Flash (spd_dump)", false, true);
        }

        private async void BtnSpdReadGpt_Click(object sender, EventArgs e)
        {
            // အကယ်၍ PAC ရွေးထားခဲ့ရင်တော့ Auto ဖြည်ပေးမည်
            if (!string.IsNullOrEmpty(spdFirmwareFile) && File.Exists(spdFirmwareFile))
            {
                await EnsurePacExtractedAsync();
            }

            string spd = FindSpdDump();
            ClearPartitionData(); // Partition ဇယားဟောင်း ရှင်းထုတ်ခြင်း

            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("   SPREADTRUM / UNISOC - READ GPT PARTITIONS", Color.White);
            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("[*] Waiting for device... (Power off, hold Vol- or Vol+ and connect USB)", Color.Orange);

            // spd_dump p (print partition list) ခေါ်ယူခြင်း
            bool ok = await ExecuteCommandCleanAsync(spd, BuildSpdFdlPrefix() + " p", "SPD - Partition List", false, true);

            if (ok)
            {
                Log("[OK] Read GPT Completed Successfully!", Color.LightGreen);
            }
        }

        private async void BtnSpdReadPart_Click(object sender, EventArgs e)
        {
            // PAC ရှိရင် Auto ဖြည်မည်၊ မရှိရင် Built-in FDL သုံးမည်
            if (!string.IsNullOrEmpty(spdFirmwareFile) && File.Exists(spdFirmwareFile))
                await EnsurePacExtractedAsync();

            string spd = FindSpdDump();

            // Partition ဇယားထဲက ရွေးထားရင် ၎င်းနာမည်ကို ယူမည်၊ မရွေးထားရင် ရိုက်ထည့်ခိုင်းမည်
            string part = "";
            if (dgvPartitions.SelectedRows.Count > 0 && dgvPartitions.SelectedRows[0].Cells["colName"].Value != null)
                part = dgvPartitions.SelectedRows[0].Cells["colName"].Value.ToString();
            else
                part = PromptInput("Read Partition", "ဖတ်ယူမည့် Partition နာမည် ရိုက်ပါ (ဥပမာ boot, persist, nvitem)");

            if (string.IsNullOrWhiteSpace(part)) return;
            if (!IsValidPartitionName(part))
            {
                Log("[x] Invalid partition name - allowed: a-z, 0-9, _ , -", Color.Red);
                return;
            }

            using (FolderBrowserDialog fbd = new FolderBrowserDialog { Description = "Partition dump သိမ်းမည့် folder ရွေးပါ" })
            {
                if (fbd.ShowDialog() != DialogResult.OK) return;

                Log($"[*] Reading Partition [{part.Trim()}] -> Saving to {fbd.SelectedPath}...", Color.Cyan);
                string args = BuildSpdFdlPrefix() + " path \"" + fbd.SelectedPath + "\" r " + part.Trim();

                bool ok = await ExecuteCommandCleanAsync(spd, args, "SPD - Read [" + part.Trim() + "]", false, true);
                if (ok) Log("[OK] " + part.Trim() + " Dump Completed Successfully!", Color.LightGreen);
            }
        }

        private async void BtnSpdWritePart_Click(object sender, EventArgs e)
        {
            if (!string.IsNullOrEmpty(spdFirmwareFile) && File.Exists(spdFirmwareFile))
                await EnsurePacExtractedAsync();

            string spd = FindSpdDump();

            string part = "";
            if (dgvPartitions.SelectedRows.Count > 0 && dgvPartitions.SelectedRows[0].Cells["colName"].Value != null)
                part = dgvPartitions.SelectedRows[0].Cells["colName"].Value.ToString();
            else
                part = PromptInput("Write Partition", "ရေးသွင်းမည့် Partition နာမည် ရိုက်ပါ (ဥပမာ boot, recovery, vbmeta)");

            if (string.IsNullOrWhiteSpace(part)) return;
            if (!IsValidPartitionName(part))
            {
                Log("[x] Invalid partition name - allowed: a-z, 0-9, _ , -", Color.Red);
                return;
            }

            using (OpenFileDialog ofd = new OpenFileDialog
            {
                Filter = "Image Files (*.img;*.bin)|*.img;*.bin|All Files (*.*)|*.*",
                Title = $"Select image for [{part.Trim()}]"
            })
            {
                if (ofd.ShowDialog() != DialogResult.OK) return;

                if (MessageBox.Show($"Write {Path.GetFileName(ofd.FileName)} into [{part.Trim()}]?",
                    "Confirm Write", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;

                Log($"[*] Writing {Path.GetFileName(ofd.FileName)} into [{part.Trim()}]...", Color.Cyan);
                string args = BuildSpdFdlPrefix() + " w " + part.Trim() + " \"" + ofd.FileName + "\" reset";

                await ExecuteCommandCleanAsync(spd, args, "SPD - Write [" + part.Trim() + "]", false, true);
            }
        }

        private async void BtnSpdErasePart_Click(object sender, EventArgs e)
        {
            if (!string.IsNullOrEmpty(spdFirmwareFile) && File.Exists(spdFirmwareFile))
                await EnsurePacExtractedAsync();

            string spd = FindSpdDump();

            string part = "";
            if (dgvPartitions.SelectedRows.Count > 0 && dgvPartitions.SelectedRows[0].Cells["colName"].Value != null)
                part = dgvPartitions.SelectedRows[0].Cells["colName"].Value.ToString();
            else
                part = PromptInput("Erase Partition", "ဖျက်မည့် Partition နာမည် ရိုက်ပါ (ဥပမာ userdata, cache, misc, persist)");

            if (string.IsNullOrWhiteSpace(part)) return;
            if (!IsValidPartitionName(part))
            {
                Log("[x] Invalid partition name - allowed: a-z, 0-9, _ , -", Color.Red);
                return;
            }

            if (MessageBox.Show($"Erase / Format [{part.Trim()}] partition?",
                "Confirm Erase", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            Log($"[*] Erasing Partition [{part.Trim()}]...", Color.OrangeRed);
            string args = BuildSpdFdlPrefix() + " e " + part.Trim();

            bool ok = await ExecuteCommandCleanAsync(spd, args, "SPD - Erase [" + part.Trim() + "]", false, true);
            if (ok) Log("[OK] Erased [" + part.Trim() + "] Successfully!", Color.LightGreen);
        }

        // ================= UNLOCK (Diag) — spd_dump diag channel ကနေ FDL erase =================
        // Industry "Diag mode" (EFT/ResearchDownload) နဲ့ တူညီတဲ့ flow: FDL1/FDL2 load → exec →
        // partition erase → reset။ spd_dump က SPRD diag driver (Channel9) ကိုသုံးပြီး port ကို
        // auto ရှာတယ် (serial select မလို)။ skip_confirm=1 default မို့ batch erase မှာ prompt မတက်ဘူး၊
        // မရှိတဲ့ partition ဆိုရင် "part not exist" ပြပြီး နောက် command ကို continue လုပ်တယ်။
        //   • FRP   = persist (Hovatek ResearchDownload erase-persist နည်း) + frp fallback
        //   • Userlock = userdata + cache format (data wipe — Yes/No dialog နဲ့ အရင်မေး)

        private async void BtnSpdDiagFrp_Click(object sender, EventArgs e)
        {
            if (!string.IsNullOrEmpty(spdFirmwareFile) && File.Exists(spdFirmwareFile))
                await EnsurePacExtractedAsync();

            string spd = FindSpdDump();

            if (ConfirmDialog.Show(
                "FRP Reset (Diag mode)?\n\n" +
                "persist / frp partition ကို erase ပြီး phone ကို reboot လုပ်မည်။\n\n" +
                "Device: Power off → Vol- (သို့) Vol+ နှိပ်ထားပြီး USB ချိတ်ပါ။",
                "FRP Reset (Diag)", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("   SPD - FRP RESET (DIAG MODE)", Color.White);
            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("[*] Waiting for device... (Power off, hold Vol- or Vol+ and connect USB)", Color.Orange);
            Log("[*] Erasing persist / frp...", Color.OrangeRed);

            string args = BuildSpdFdlPrefix() + " e persist e frp reset";
            bool ok = await ExecuteCommandCleanAsync(spd, args, "SPD - FRP Reset (Diag)", false, true);
            if (ok)
            {
                Log("[OK] FRP Reset job finished!", Color.LightGreen);
                Log("[i] After phone reboot, check in setup wizard whether FRP is gone (if log says 'part not exist', that partition is missing)", Color.Gray);
            }
        }

        private async void BtnSpdDiagUserlock_Click(object sender, EventArgs e)
        {
            if (!string.IsNullOrEmpty(spdFirmwareFile) && File.Exists(spdFirmwareFile))
                await EnsurePacExtractedAsync();

            string spd = FindSpdDump();

            if (ConfirmDialog.Show(
                "Userlock + FRP Reset (Diag mode)?\n\n" +
                "⚠ userdata (data) + cache + persist/frp ကို erase မည် —\n" +
                "ဖုန်းထဲက Data နဲ့ Lock အကုန် ပျက်သွားမယ်!\n\n" +
                "Device: Power off → Vol- (သို့) Vol+ နှိပ်ထားပြီး USB ချိတ်ပါ။",
                "Userlock + FRP Reset (Diag)", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("   SPD - USERLOCK + FRP RESET (DIAG MODE)", Color.White);
            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("[*] Waiting for device... (Power off, hold Vol- or Vol+ and connect USB)", Color.Orange);
            Log("[*] Erasing userdata / cache / persist / frp...", Color.OrangeRed);

            string args = BuildSpdFdlPrefix() + " e userdata e cache e persist e frp reset";
            bool ok = await ExecuteCommandCleanAsync(spd, args, "SPD - Userlock + FRP Reset (Diag)", false, true);
            if (ok)
            {
                Log("[OK] Userlock + FRP Reset job finished!", Color.LightGreen);
                Log("[i] First boot can take 1-3 minutes — check in setup wizard that lock/migration is gone", Color.Gray);
            }
        }

    }
}
