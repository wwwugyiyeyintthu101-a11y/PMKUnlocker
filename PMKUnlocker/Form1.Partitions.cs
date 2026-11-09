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
        private static bool IsValidPartitionName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            foreach (char c in name.Trim())
                if (!char.IsLetterOrDigit(c) && c != '_' && c != '-') return false;
            return true;
        }

        // ရွေးထားတဲ့ (checkbox ခြစ်ထားတဲ့) partition စာရင်း — filter ဖြင့် မမြင်ရသေးတဲ့ row တွေ မပါ
        private List<string> GetCheckedPartitions()
        {
            var list = new List<string>();
            foreach (DataGridViewRow r in dgvPartitions.Rows)
            {
                if (!r.Visible) continue;
                object v = r.Cells["colCheck"].Value;
                if (v == null || !(bool)v) continue;
                object nm = r.Cells["colName"].Value;
                if (nm != null && !string.IsNullOrEmpty(nm.ToString())) list.Add(nm.ToString());
            }
            return list;
        }

        private void SetAllPartitionChecks(bool state)
        {
            int n = 0;
            foreach (DataGridViewRow r in dgvPartitions.Rows)
            {
                if (!r.Visible) continue;
                r.Cells["colCheck"].Value = state;
                n++;
            }
            Log("[*] Partitions " + (state ? "selected ✓" : "cleared ✗") + " (" + n + " shown rows)",
                state ? Color.LightGreen : Color.Gray);
        }

        // Header က filter box — partition name နဲ့ မကိုက်ရင် row ကို ဝှက်ထားတယ်
        private void ApplyPartitionFilter()
        {
            if (dgvPartitions == null) return;
            string q = (txtPartFilter?.Text ?? "").Trim();
            if (q == "Filter…") q = "";
            q = q.ToLowerInvariant();
            foreach (DataGridViewRow r in dgvPartitions.Rows)
            {
                object nm = r.Cells["colName"].Value;
                string name = nm == null ? "" : nm.ToString().ToLowerInvariant();
                r.Visible = q.Length == 0 || name.Contains(q);
            }
        }

        private void ClearPartitionData()
        {
            dgvPartitions.Rows.Clear();
            loadedPartitions.Clear();
        }

        // ===== Device props helper =====
        // တချို့ device တွေမှာ ro.product.brand/model က အလွတ် ဖြစ်နေတတ်တယ် (ဥပမာ Redmi Note 11 / Android 12
        // — တကယ့် တန်ဖိုးတွေက ro.product.bootimage.* မှာ ရှိတယ်)။ ဒါကြောင့် getprop အားလုံးကို တစ်ခါ
        // dump လုပ်ပြီး fallback chain နဲ့ ဖတ်တယ် (adb call လည်း သက်သာတယ်)။
        private Dictionary<string, string> deviceProps = null;
        // ရည်ရွယ်ချက်ရှိ reboot wait window (auto-reboot retry) အတွင်း — ဖုန်းပျောက်နေတာ သဘာဝမို့
        // disconnect error တွေကို friendly line နဲ့ suppress လုပ်
        private volatile bool rebootWaitActive = false;
        // Temp Root flow အတွင်း payload/exploit run မှာ device ပျောက်ရင် — တစ်ကြိမ်ပဲ
        // wait-for-device နဲ့ ပြန်စောင့် (kernel crash → reboot) · မပြန်ရင် abort → command spam ရပ်
        private volatile bool tempRootFlowActive = false;
        private volatile bool tempRootAborted = false;
        private bool tempRootRecoveryDone = false;
        // disconnect wait/back ကို episode တစ်ခုမှာ တစ်ချက်ပဲ ပြ — gate + background helper နှစ်ခု
        // လုံးက ပြောတဲ့ dup line ရှင်းရန်
        private bool tempRootReconnectLogged = false;
        // Partition list က MTK နဲ့ Qualcomm နှစ်ခုလုံးအတွက် ပြတယ် — လက်ရှိ tab ပေါ် မူတည်ပြီး
        // မှန်တဲ့ tool ကို ရွေးတယ် (MediaTek → mtk.exe / Qualcomm → edl.py)
        private bool IsQualcommTab()
        {
            return (tabControl.SelectedTab == tabQc);
        }

        private async Task<bool> RunPartitionOpAsync(string op, string partition, string filePath, string title)
        {
            if (op == "w" && filePath != null && !WarnIfImageTooLarge(partition, filePath))
                return false;

            string arg = op + " " + partition + (filePath != null ? " \"" + filePath + "\"" : "");

            if (IsQualcommTab())
            {
                return await RunNativeEdlAsync(arg, title, false);
            }

            // SPD tab — spd_dump (mtk.exe မသုံးရ — မှားသုံးရင် brick အန္တရာယ်)
            if (tabControl.SelectedTab == tabSpd)
            {
                string spd = FindSpdDump();
                string prefix = BuildSpdFdlPrefix();
                string spdArgs;
                if (op == "r")
                    spdArgs = prefix + " path \"" + Path.GetDirectoryName(filePath ?? "") + "\" r " + partition;
                else if (op == "w")
                    spdArgs = prefix + " w " + partition + " \"" + filePath + "\"";
                else
                    spdArgs = prefix + " e " + partition;
                return await ExecuteCommandCleanAsync(spd, spdArgs, title, false, true);
            }

            // Samsung tab — generic r/w/e context menu မသုံးရ (heimdall ကွဲပြား; mtk.exe မခေါးရ)
            if (tabControl.SelectedTab == tabSamsung)
            {
                Log("[!] Partition read/write/erase is not available on the Samsung tab - use Firmware Flash / PIT.", Color.Orange);
                return false;
            }

            // ADB tab + root — adb su dd နဲ့ r/w/e (by-name / dm path)
            if (tabControl.SelectedTab == tabAdb)
                return await RunAdbRootPartitionOpAsync(op, partition, filePath, title);

            if (tabControl.SelectedTab != tabMtk)
            {
                Log("[!] Partition operation is only supported on MediaTek / Qualcomm / Spreadtrum tabs.", Color.Orange);
                return false;
            }

            return await RunMediaTekPartitionAsync(op, partition, filePath);
        }

        // ADB root partition r/w/e — /dev/block/by-name/<name> ကို su + dd နဲ့
        private async Task<bool> RunAdbRootPartitionOpAsync(string op, string partition, string filePath, string title)
        {
            string id = (await ProbeSuAsync()).Trim();
            if (!id.Contains("uid=0"))
            {
                Log("[!] " + title + ": no root (su) — try Xiaomi Temp Root / Magisk / KernelSU", Color.OrangeRed);
                ConfirmDialog.Show("Root (su) မရပါ။\n\nADB partition r/w/e အတွက် root လိုအပ်ပါတယ်。",
                    title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            string dev = partition.StartsWith("/dev/", StringComparison.Ordinal)
                ? partition
                : "/dev/block/by-name/" + partition;
            // extra mounts (system/vendor…) က by-name မရှိ — path တိုက်ရိုက်ပေးနိုင်
            string chk = (await ExecuteCommandQuickAsync("adb.exe",
                "shell su -c \"ls -l " + dev + " 2>/dev/null || readlink -f /dev/block/by-name/" + partition + "\"")).Trim();
            if (chk.Contains("No such") || string.IsNullOrWhiteSpace(chk))
            {
                // fallback: by-name symlink resolve
                string rl = (await ExecuteCommandQuickAsync("adb.exe",
                    "shell su -c \"readlink -f /dev/block/by-name/" + partition + "\"")).Trim();
                if (!string.IsNullOrWhiteSpace(rl) && !rl.Contains("No such")) dev = rl;
                else
                {
                    Log("[!] " + title + ": device node not found for [" + partition + "]", Color.OrangeRed);
                    return false;
                }
            }
            else if (chk.Contains("->"))
            {
                int arrow = chk.LastIndexOf("->", StringComparison.Ordinal);
                string target = chk.Substring(arrow + 2).Trim();
                if (!string.IsNullOrWhiteSpace(target)) dev = target;
            }

            if (op == "r")
            {
                string local = filePath ?? Path.Combine(Path.GetTempPath(), partition + ".img");
                Directory.CreateDirectory(Path.GetDirectoryName(local) ?? Path.GetTempPath());
                string remote = "/data/local/tmp/pmk_" + partition + ".img";
                Log("[*] READ [" + partition + "] " + dev + " → " + local, Color.Cyan);
                bool ok = await ExecuteCommandCleanAsync("adb.exe",
                    "shell su -c \"dd if=" + dev + " of=" + remote + " bs=1048576 2>/dev/null\"",
                    "dd " + partition, false, false, quiet: true);
                if (!ok) { Log("[!] dd failed [" + partition + "]", Color.OrangeRed); return false; }
                ok = await ExecuteCommandCleanAsync("adb.exe", "pull \"" + remote + "\" \"" + local + "\"",
                    "pull " + partition, false, false, quiet: true);
                await ExecuteCommandQuickAsync("adb.exe",
                    "shell su -c \"rm -f " + remote + "\"");
                if (ok && File.Exists(local))
                    Log("[OK] READ [" + partition + "] → " + local + " (" + FormatBytesLong(new FileInfo(local).Length) + ")", Color.LightGreen);
                else
                    Log("[!] READ [" + partition + "] failed (pull or file missing)", Color.OrangeRed);
                return ok && File.Exists(local);
            }

            if (op == "w")
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                {
                    Log("[!] " + title + ": file not found " + (filePath ?? "(null)"), Color.OrangeRed);
                    return false;
                }
                string remote = "/data/local/tmp/pmk_write_" + partition + Path.GetExtension(filePath);
                long flen = new FileInfo(filePath).Length;
                Log("[*] WRITE [" + partition + "] " + Path.GetFileName(filePath) + " (" + FormatBytesLong(flen) + ") → " + dev, Color.Cyan);
                bool ok = await ExecuteCommandCleanAsync("adb.exe", "push \"" + filePath + "\" \"" + remote + "\"",
                    "push " + partition, false, false, quiet: true);
                if (!ok) { Log("[!] push failed [" + partition + "]", Color.OrangeRed); return false; }
                long blocks = flen > 0 ? (flen + 1048575) / 1048576 : 1;
                ok = await ExecuteCommandCleanAsync("adb.exe",
                    "shell su -c \"dd if=" + remote + " of=" + dev + " bs=1048576 count=" + blocks + " 2>/dev/null\"",
                    "dd " + partition, false, false, quiet: true);
                await ExecuteCommandQuickAsync("adb.exe",
                    "shell su -c \"rm -f " + remote + "\"");
                if (ok) Log("[OK] WRITE [" + partition + "] ← " + Path.GetFileName(filePath), Color.LightGreen);
                else Log("[!] WRITE [" + partition + "] failed", Color.OrangeRed);
                return ok;
            }

            if (op == "e")
            {
                string sz = (await ExecuteCommandQuickAsync("adb.exe",
                    "shell su -c \"blockdev --getsize64 " + dev + " 2>/dev/null\"")).Trim();
                long.TryParse(sz, out long bytes);
                if (bytes <= 0)
                {
                    string sec = (await ExecuteCommandQuickAsync("adb.exe",
                        "shell su -c \"cat /sys/class/block/$(basename $(readlink -f " + dev + "))/size 2>/dev/null\"")).Trim();
                    if (long.TryParse(sec, out long sectors) && sectors > 0) bytes = sectors * 512;
                }
                long blocks = bytes > 0 ? (bytes + 1048575) / 1048576 : 1;
                Log("[*] ERASE [" + partition + "] zero " + FormatBytesLong(bytes > 0 ? bytes : 1048576) + " → " + dev, Color.Orange);
                bool ok = await ExecuteCommandCleanAsync("adb.exe",
                    "shell su -c \"dd if=/dev/zero of=" + dev + " bs=1048576 count=" + blocks + " 2>/dev/null\"",
                    "erase " + partition, false, false, quiet: true);
                if (ok) Log("[OK] ERASE [" + partition + "]", Color.LightGreen);
                else Log("[!] ERASE [" + partition + "] failed", Color.OrangeRed);
                return ok;
            }

            return false;
        }

        private async void MenuReadPartition_Click(object sender, EventArgs e)
        {
            List<string> parts = GetCheckedPartitions();

            if (parts.Count == 0)
            {
                if (dgvPartitions.SelectedRows.Count == 0) return;
                string single = dgvPartitions.SelectedRows[0].Cells["colName"].Value.ToString();
                using (SaveFileDialog sfd = new SaveFileDialog { Filter = "Image Files (*.img)|*.img|Binary Files (*.bin)|*.bin", FileName = single + ".img" })
                {
                    if (sfd.ShowDialog() == DialogResult.OK)
                        await RunPartitionOpAsync("r", single, sfd.FileName, "Read Partition [" + single + "]");
                }
                return;
            }

            using (FolderBrowserDialog fbd = new FolderBrowserDialog { Description = "Select folder to save " + parts.Count + " partition(s)" })
            {
                if (fbd.ShowDialog() != DialogResult.OK) return;
                Log("[*] Reading " + parts.Count + " partition(s) -> " + fbd.SelectedPath, Color.Cyan);
                int ok = 0;
                foreach (string part in parts)
                {
                    string file = Path.Combine(fbd.SelectedPath, part + ".img");
                    if (await RunPartitionOpAsync("r", part, file, "Read [" + part + "]")) ok++;
                }
                Log("[OK] Read " + ok + " / " + parts.Count + " -> " + fbd.SelectedPath, ok == parts.Count ? Color.LightGreen : Color.Orange);
            }
        }

        private async void MenuWritePartition_Click(object sender, EventArgs e)
        {
            List<string> parts = GetCheckedPartitions();

            if (parts.Count == 0)
            {
                if (dgvPartitions.SelectedRows.Count == 0) return;
                string single = dgvPartitions.SelectedRows[0].Cells["colName"].Value.ToString();
                using (OpenFileDialog ofd = new OpenFileDialog { Filter = "Image Files (*.img)|*.img|All Files (*.*)|*.*", Title = "Select File for [" + single + "]" })
                {
                    if (ofd.ShowDialog() == DialogResult.OK)
                    {
                        if (MessageBox.Show("Write " + Path.GetFileName(ofd.FileName) + " into [" + single + "]?", "Confirm Write", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                            await RunPartitionOpAsync("w", single, ofd.FileName, "Write Partition [" + single + "]");
                    }
                }
                return;
            }

            using (FolderBrowserDialog fbd = new FolderBrowserDialog { Description = "Select folder containing <partition>.img files - " + parts.Count + " checked" })
            {
                if (fbd.ShowDialog() != DialogResult.OK) return;

                int have = 0;
                foreach (string part in parts)
                    if (File.Exists(Path.Combine(fbd.SelectedPath, part + ".img"))) have++;

                if (have == 0)
                {
                    MessageBox.Show("No matching .img file in that folder. File names must match partition names (boot.img, vbmeta.img ...).", "Firmware", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                string ask = "Write " + have + " of " + parts.Count + " checked partition(s) from " + fbd.SelectedPath + " ?  (user data may be wiped - owner consent required)";
                if (MessageBox.Show(ask, "Confirm Write", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

                int ok = 0;
                foreach (string part in parts)
                {
                    string file = Path.Combine(fbd.SelectedPath, part + ".img");
                    if (!File.Exists(file)) { Log("[!] Missing: " + part + ".img - skipped", Color.Orange); continue; }
                    if (await RunPartitionOpAsync("w", part, file, "Write [" + part + "]")) ok++;
                }
                Log("[OK] Wrote " + ok + " / " + parts.Count, ok > 0 ? Color.LightGreen : Color.OrangeRed);
            }
        }

        private async void MenuErasePartition_Click(object sender, EventArgs e)
        {
            List<string> parts = GetCheckedPartitions();

            if (parts.Count == 0)
            {
                if (dgvPartitions.SelectedRows.Count == 0) return;
                string single = dgvPartitions.SelectedRows[0].Cells["colName"].Value.ToString();
                if (MessageBox.Show("Erase/Format [" + single + "]?", "Confirm Erase", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                    await RunPartitionOpAsync("e", single, null, "Erase Partition [" + single + "]");
                return;
            }

            string ask = "Erase/Format " + parts.Count + " partition(s): " + string.Join(", ", parts.ToArray()) + " ?  (may destroy data - owner consent required)";
            if (MessageBox.Show(ask, "Confirm Erase (" + parts.Count + ")", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            int ok = 0;
            foreach (string part in parts)
                if (await RunPartitionOpAsync("e", part, null, "Erase [" + part + "]")) ok++;
            Log("[OK] Erased " + ok + " / " + parts.Count, ok > 0 ? Color.LightGreen : Color.OrangeRed);
        }

        // Main partition grid right-click → Mount RW (root). Selected row name → /proc/mounts lookup → remount.
        private async void MenuMountRwPartition_Click(object sender, EventArgs e)
        {
            Log("[*] Menu: Mount RW clicked", Color.Gray);
            DataGridViewRow row = null;
            string pname = null, pmp = null, pmode = null, pdev = null;
            if (dgvPartitions.SelectedRows.Count > 0)
                row = dgvPartitions.SelectedRows[0];
            if (row != null)
            {
                pname = row.Cells["colName"]?.Value?.ToString();
                pdev = row.Cells["colOffset"]?.Value?.ToString();
                pmp = row.Cells["colMount"]?.Value?.ToString();
                pmode = row.Cells["colMode"]?.Value?.ToString();
            }
            if (string.IsNullOrWhiteSpace(pname))
            {
                ConfirmDialog.Show("Partition တစ်ခု ရွေးပါ။", "Mount RW", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string dv = await ExecuteCommandQuickAsync("adb.exe", "devices");
            if (string.IsNullOrWhiteSpace(dv) || !dv.Contains("\tdevice"))
            {
                MessageBox.Show("ADB device not found.", "Mount RW", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string id = (await ProbeSuAsync()).Trim();
            if (!id.Contains("uid=0"))
            {
                ConfirmDialog.Show("Root (su) မရပါ။\n\n• Xiaomi Temp Root / Magisk / KernelSU ရအောင်လုပ်ပါ",
                    "Mount RW", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Log("[!] Mount RW: su not available", Color.OrangeRed);
                return;
            }

            // Prefer grid cell values (loaded at Partitions ROOT time); re-lookup if empty/stale
            if (string.IsNullOrEmpty(pmp) || pmp == "—" || string.IsNullOrEmpty(pmode) || pmode == "—")
            {
                string mounts = (await ExecuteCommandQuickAsync("adb.exe", "shell su -c \"cat /proc/mounts\"")).Trim();
                foreach (string line in mounts.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] pp = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (pp.Length < 4) continue;
                    string dev = pp[0];
                    string mpath = pp[1];
                    string opts = pp[3];
                    string bn = Path.GetFileName(dev.TrimEnd('/'));
                    bool match =
                        dev.EndsWith("/" + pname, StringComparison.OrdinalIgnoreCase) ||
                        bn.Equals(pname, StringComparison.OrdinalIgnoreCase) ||
                        Path.GetFileName(mpath.TrimEnd('/')).Equals(pname, StringComparison.OrdinalIgnoreCase);
                    if (!match && bn.StartsWith("dm-", StringComparison.OrdinalIgnoreCase))
                    {
                        string dmName = (await ExecuteCommandQuickAsync("adb.exe",
                            "shell su -c \"cat /sys/block/" + bn + "/dm/name 2>/dev/null\"")).Trim();
                        if (!string.IsNullOrEmpty(dmName))
                        {
                            if (dmName.EndsWith("-verity", StringComparison.OrdinalIgnoreCase))
                                dmName = dmName.Substring(0, dmName.Length - "-verity".Length);
                            match = dmName.Equals(pname, StringComparison.OrdinalIgnoreCase);
                        }
                    }
                    if (match)
                    {
                        pmp = mpath;
                        pmode = opts.Split(',').Any(o => o.Equals("ro", StringComparison.OrdinalIgnoreCase)) ? "ro" : "rw";
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(pmp) || pmp == "—" || pmp == "")
            {
                // Raw partition (frp, nvram…) — mount remount မလုပ်ရဘဲ root dd နဲ့ write
                Log("[i] " + pname + " is not mounted — raw block (root write via dd).", Color.Gray);
                DialogResult pick = ConfirmDialog.Show(
                    pname + " is not mounted (raw block device)." + Environment.NewLine + Environment.NewLine +
                    "Root write လုပ်မလား?" + Environment.NewLine + Environment.NewLine +
                    "Yes = Zero/erase [" + pname + "] (dd if=/dev/zero) — frp အတွက် FRP clear လို" + Environment.NewLine +
                    "No = File ရွေးပြီး [" + pname + "]ထဲ write" + Environment.NewLine +
                    "Cancel = မလုပ်တော့ဘူး",
                    "Root write " + pname,
                    MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (pick == DialogResult.Yes)
                {
                    if (ConfirmDialog.Show(
                            "Zero/erase [" + pname + "]?" + Environment.NewLine + Environment.NewLine +
                            "• dd if=/dev/zero → " + (pdev ?? pname) + Environment.NewLine +
                            "• frp ဆိုရင် Google account (FRP) ပျက်သွားမယ်" + Environment.NewLine +
                            "• ဖုန်းပိုင်ရှင် ခွင့်ပြုချက် ရှိမှ လုပ်ပါ",
                            "Confirm Zero " + pname, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                            await RunPartitionOpAsync("e", pname, null, "Root Zero [" + pname + "]");
                }
                else if (pick == DialogResult.No)
                {
                    using (OpenFileDialog ofd = new OpenFileDialog
                    {
                        Filter = "All Files (*.*)|*.*|Image Files (*.img)|*.img",
                        Title = "Write file to [" + pname + "]"
                    })
                    {
                        if (ofd.ShowDialog() == DialogResult.OK &&
                            MessageBox.Show("Write " + Path.GetFileName(ofd.FileName) + " → [" + pname + "]?",
                                "Confirm Root Write", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                            await RunPartitionOpAsync("w", pname, ofd.FileName, "Root Write [" + pname + "]");
                    }
                }
                return;
            }

            if (pmode != null && pmode.Equals("rw", StringComparison.OrdinalIgnoreCase))
            {
                Log("[i] " + pname + " already mounted rw at " + pmp, Color.Gray);
                MessageBox.Show(pname + " is already rw at " + pmp, "Mount RW", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (ConfirmDialog.Show(
                    "Remount " + pmp + " (" + pname + ") as Read-Write?" + Environment.NewLine + Environment.NewLine +
                    "• mount -o remount,rw " + pmp + Environment.NewLine +
                    "• SELinux / AVB က remount ကို တားနိုင်တယ်" + Environment.NewLine +
                    "• ဖုန်းပိုင်ရှင် ခွင့်ပြုချက် ရှိမှ လုပ်ပါ",
                    "Confirm Mount RW", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            Log("[*] Remount RW: " + pmp + " [" + pname + "] ...", Color.Orange);
            bool ok = await ExecuteCommandCleanAsync("adb.exe",
                "shell su -c \"mount -o remount,rw " + pmp + "\"",
                "Mount RW " + pmp, false, true);
            if (!ok && !string.IsNullOrEmpty(pdev))
            {
                await ExecuteCommandCleanAsync("adb.exe",
                    "shell su -c \"mount -o remount,rw " + pdev + " " + pmp + "\"",
                    "Mount RW (by device)", false, true);
            }

            string mounts2 = (await ExecuteCommandQuickAsync("adb.exe", "shell su -c \"cat /proc/mounts\"")).Trim();
            bool nowRw = false;
            foreach (string line in mounts2.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] pp = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (pp.Length < 4) continue;
                if (pp[1] == pmp)
                {
                    nowRw = !pp[3].Split(',').Any(o => o.Equals("ro", StringComparison.OrdinalIgnoreCase));
                    break;
                }
            }
            if (nowRw)
            {
                Log("[OK] " + pmp + " → rw", Color.LightGreen);
                if (row != null) row.Cells["colMode"].Value = "rw";
                MessageBox.Show(pmp + " is now rw.", "Mount RW", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                Log("[!] remount may have failed — check log / SELinux", Color.OrangeRed);
                MessageBox.Show("Remount status unknown — check log / SELinux.", "Mount RW", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

    }
}
