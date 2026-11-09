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
        private static readonly string[] SamsungEfsPartitions = { "efs", "modem", "persist", "param" };

        private async void BtnSamEfsBackup_Click(object sender, EventArgs e)
        {
            if (!await EnsureAdbRootAsync("Samsung EFS backup")) return;
            using (FolderBrowserDialog fbd = new FolderBrowserDialog { Description = "Samsung EFS backup သိမ်းမယ့် folder ရွေးပါ" })
            {
                if (fbd.ShowDialog() != DialogResult.OK) return;
                string dir = Path.Combine(fbd.SelectedPath, "SamsungEFS_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                try { Directory.CreateDirectory(dir); }
                catch (Exception ex) { Log("[FAIL] Backup folder create failed: " + ex.Message, Color.Red); return; }

                await RunFlashWorkflowAsync(async () =>
                {
                    int ok = 0;
                    var missing = new List<string>();
                    foreach (string part in SamsungEfsPartitions)
                    {
                        if (stopRequested) break;
                        if (await RunAdbRootPartitionOpAsync("r", part, Path.Combine(dir, part + ".bin"),
                                "Samsung EFS backup (" + part + ")"))
                            ok++;
                        else
                            missing.Add(part);
                    }
                    Log($"[OK] Samsung EFS backup — {ok}/{SamsungEfsPartitions.Length} partition(s) → {dir}", Color.LightGreen);
                    if (missing.Count > 0)
                        Log("[i] Not backed up (missing/unreadable on this model): " + string.Join(", ", missing), Color.Gray);
                }, autoRebootAfter: false);
            }
        }

        private async void BtnSamEfsRestore_Click(object sender, EventArgs e)
        {
            using (FolderBrowserDialog fbd = new FolderBrowserDialog { Description = "Samsung EFS backup folder ရွေးပါ (efs.bin / modem.bin / persist.bin / param.bin)" })
            {
                if (fbd.ShowDialog() != DialogResult.OK) return;
                string dir = fbd.SelectedPath;

                var files = new List<(string Name, string Path)>();
                foreach (string part in SamsungEfsPartitions)
                {
                    string f = Path.Combine(dir, part + ".bin");
                    if (File.Exists(f)) files.Add((part, f));
                }
                if (files.Count == 0)
                {
                    Log("[!] Backup folder ထဲမှာ efs.bin / modem.bin / persist.bin / param.bin မတွေ့ပါ: " + dir, Color.Orange);
                    return;
                }
                if (!await EnsureAdbRootAsync("Samsung EFS restore")) return;

                // size တိုက်စစ် — partition size မသိတဲ့ model မှာ သတိပေးချက်နဲ့ ဆက်ခွင့်ပြု
                var plan = new List<(string Name, string Path)>();
                var skipped = new List<string>();
                bool unknownSize = false;
                foreach (var f in files)
                {
                    long partBytes = await GetAdbPartitionBytesAsync(f.Name);
                    long fileBytes;
                    try { fileBytes = new FileInfo(f.Path).Length; } catch { fileBytes = -1; }
                    if (partBytes <= 0) { unknownSize = true; plan.Add(f); continue; }
                    if (fileBytes != partBytes)
                    {
                        skipped.Add($"{f.Name} (file {fileBytes} B ≠ partition {partBytes} B)");
                        continue;
                    }
                    plan.Add(f);
                }
                foreach (string s in skipped) Log("[!] Skipped — size does not match this device: " + s, Color.Orange);
                if (plan.Count == 0)
                {
                    Log("[!] Nothing to restore — every file failed the partition-size check.", Color.OrangeRed);
                    return;
                }
                if (unknownSize)
                {
                    Log("[!] Some partition sizes could not be read — restore is unverified.", Color.Orange);
                    if (MessageBox.Show(this, "Partition size မသိနိုင်တဲ့ partition ရှိတယ်။\nSize မတိုက်စစ်ဘဲ ဆက်ရေးမလား?",
                            "Samsung EFS restore", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                }
                if (MessageBox.Show(this,
                        "Restore " + plan.Count + " partition(s)?\n\n" + string.Join(", ", plan.Select(p => p.Name)) +
                        "\n\nEFS မှားရင် IMEI/baseband ပျက်နိုင်တယ်။ Backup က ဒီဖုန်းအတွက် ဟုတ်ကြောင်း သေချာအောင်။\n\nဆက်လုပ်မလား?",
                        "Confirm Samsung EFS restore", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

                await RunFlashWorkflowAsync(async () =>
                {
                    int done = 0;
                    foreach (var f in plan)
                    {
                        if (stopRequested) break;
                        if (!await RunAdbRootPartitionOpAsync("w", f.Name, f.Path, "Samsung EFS restore (" + f.Name + ")"))
                            throw new IOException("Restore stopped at " + f.Name + " — remaining partitions were NOT written.");
                        done++;
                    }
                    Log($"[OK] Samsung EFS restore finished — {done}/{plan.Count} partition(s) written. Reboot the phone.", Color.LightGreen);
                }, autoRebootAfter: false);
            }
        }

        private async Task<bool> EnsureAdbRootAsync(string title)
        {
            string id = (await ProbeSuAsync()).Trim();
            if (id.Contains("uid=0")) return true;
            Log("[!] " + title + ": root (su) မရပါ — Magisk / KernelSU root ဖုန်းမှာသာ လုပ်လို့ရတယ်.", Color.OrangeRed);
            MessageBox.Show("Root (su) မရပါ။\n\nEFS backup/restore ကို ADB + root နဲ့သာ လုပ်လို့ရတယ် (heimdall က partition ဖတ်လို့မရ)။",
                title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        private async Task<long> GetAdbPartitionBytesAsync(string partition)
        {
            string sz = (await ExecuteCommandQuickAsync("adb.exe",
                "shell su -c \"blockdev --getsize64 /dev/block/by-name/" + partition + " 2>/dev/null\"")).Trim();
            if (long.TryParse(sz, out long bytes) && bytes > 0) return bytes;
            string sec = (await ExecuteCommandQuickAsync("adb.exe",
                "shell su -c \"cat /sys/class/block/$(basename $(readlink -f /dev/block/by-name/" + partition + "))/size 2>/dev/null\"")).Trim();
            if (long.TryParse(sec, out long sectors) && sectors > 0) return sectors * 512;
            return 0;
        }

        private string FindHeimdall()
        {
            string h = FindFileInToolFolders("heimdall.exe");
            return string.IsNullOrEmpty(h) ? "heimdall" : h;
        }

        // ---- Samsung: firmware/image ရွေး (.img / .tar / .tar.md5 — အားလုံး heimdall နဲ့ တိုက်ရိုက်) ----
        private void BtnSamPickFirmware_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog ofd = new OpenFileDialog
            {
                Filter = "Samsung firmware (*.img;*.tar;*.tar.md5)|*.img;*.tar;*.tar.md5|All Files (*.*)|*.*",
                Title = "Samsung firmware/image ကို ရွေးပါ"
            })
            {
                if (ofd.ShowDialog() != DialogResult.OK) return;
                samFirmwareFile = ofd.FileName;
                txtSamFirmware.Text = samFirmwareFile;

                ClearPartitionData(); // Partition ဇယားဟောင်းကို ရှင်းထုတ်ခြင်း

                bool isTar = samFirmwareFile.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)
                          || samFirmwareFile.EndsWith(".tar.md5", StringComparison.OrdinalIgnoreCase);

                int count = 0;

                if (isTar)
                {
                    try
                    {
                        // .tar / .tar.md5 archive ထဲက partition ဖိုင်များကို ဖတ်ပြီး Grid ထဲ ထည့်ခြင်း
                        using (FileStream fs = File.OpenRead(samFirmwareFile))
                        using (TarReader reader = new TarReader(fs))
                        {
                            TarEntry entry;
                            while ((entry = reader.GetNextEntry()) != null)
                            {
                                if (entry.EntryType != TarEntryType.RegularFile &&
                                    entry.EntryType != TarEntryType.V7RegularFile) continue;

                                string name = Path.GetFileName(entry.Name);
                                if (string.IsNullOrEmpty(name)) continue;

                                string partName = MapSamsungPartition(name);
                                if (string.IsNullOrEmpty(partName)) partName = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();

                                string lenHex = "0x" + entry.Length.ToString("X");
                                AddPartitionRow(partName, "-", lenHex);
                                count++;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("[!] Error reading archive: " + ex.Message, Color.Orange);
                    }
                }
                else
                {
                    // .img ဖိုင်တစ်ခုတည်း ရွေးထားပါက
                    string partName = MapSamsungPartition(samFirmwareFile);
                    if (string.IsNullOrEmpty(partName)) partName = Path.GetFileNameWithoutExtension(samFirmwareFile).ToUpperInvariant();
                    long fileLen = new FileInfo(samFirmwareFile).Length;
                    AddPartitionRow(partName, "-", "0x" + fileLen.ToString("X"));
                    count = 1;
                }

                // Checkbox အားလုံးကို Default အနေဖြင့် အမှန်ခြစ်ပေးခြင်း
                SetAllPartitionChecks(true);

                Log("========================================================", Color.FromArgb(0, 180, 255));
                Log("   SAMSUNG FIRMWARE LOADED", Color.White);
                Log("========================================================", Color.FromArgb(0, 180, 255));
                Log("  • File       : " + Path.GetFileName(samFirmwareFile), Color.Cyan);
                Log("  • Size       : " + (new FileInfo(samFirmwareFile).Length / (1024.0 * 1024.0)).ToString("F1") + " MB", Color.White);
                Log("  • Partitions : " + count + " partitions loaded into list", Color.LightGreen);
            }
        }

        private static string ReadUtf16Fixed(byte[] data, int offset, int byteLen)
        {
            if (offset < 0 || offset + byteLen > data.Length) return "";
            string s = Encoding.Unicode.GetString(data, offset, byteLen);
            int nul = s.IndexOf('\0');
            if (nul >= 0) s = s.Substring(0, nul);
            return s.Trim();
        }

        // Samsung PIT (binary): header 28 bytes (magic 0x12349876 + entry count + reserved),
        // entry တစ်ခု 132 bytes၊ နာမည် field တွေက UTF-16LE (32 bytes = 16 chars)။
        private List<PitEntry> ParsePit(string path)
        {
            List<PitEntry> list = new List<PitEntry>();
            try
            {
                byte[] data = File.ReadAllBytes(path);
                if (data.Length < 28 + PitEntrySize) return list;
                if (BitConverter.ToUInt32(data, 0) != 0x12349876) return list;

                int count = (int)BitConverter.ToUInt32(data, 4);
                int maxPossible = (data.Length - 28) / PitEntrySize;
                if (count <= 0 || count > maxPossible) count = maxPossible;

                for (int i = 0, off = 28; i < count && off + PitEntrySize <= data.Length; i++, off += PitEntrySize)
                {
                    PitEntry e = new PitEntry
                    {
                        BlockOffset = BitConverter.ToUInt32(data, off + 20),
                        BlockCount = BitConverter.ToUInt32(data, off + 24),
                        Name = ReadUtf16Fixed(data, off + 36, 32),
                        FlashFilename = ReadUtf16Fixed(data, off + 68, 32)
                    };
                    if (!string.IsNullOrEmpty(e.Name)) list.Add(e);
                }
            }
            catch (Exception ex) { Log("[x] PIT parse error: " + ex.Message, Color.Red); }
            return list;
        }

        // heimdall download-pit → binary PIT ကို သိမ်းပြီး ဖတ် → partitions grid ဖြည့်
        private async Task<bool> DownloadPitAsync(string heimdall, bool silent)
        {
            string outPath;
            if (silent)
            {
                outPath = Path.Combine(Application.StartupPath,
                    "samsung_pit_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".pit");
            }
            else
            {
                using (SaveFileDialog sfd = new SaveFileDialog
                {
                    Filter = "PIT file (*.pit)|*.pit|All Files (*.*)|*.*",
                    FileName = "samsung_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".pit",
                    Title = "Save Samsung PIT table"
                })
                {
                    if (sfd.ShowDialog() != DialogResult.OK) return false;
                    outPath = sfd.FileName;
                }
            }

            bool ok = await ExecuteCommandCleanAsync(heimdall, "download-pit --output \"" + outPath + "\"",
                                                     "Samsung - Download PIT");
            if (!ok || !File.Exists(outPath))
            {
                Log("[x] PIT download failed - phone must be in Download Mode (Vol- + Home + Power / adb reboot download).", Color.Red);
                return false;
            }

            List<PitEntry> entries = ParsePit(outPath);
            if (entries.Count == 0)
            {
                Log("[x] Cannot read the PIT file (bad binary format) - " + Path.GetFileName(outPath), Color.Red);
                return false;
            }

            samPitEntries = entries;
            ClearPartitionData();
            foreach (PitEntry e in entries)
            {
                string offHex = "0x" + (e.BlockOffset * SamsungBlockSize).ToString("X");
                string lenHex = "0x" + (e.BlockCount * SamsungBlockSize).ToString("X");
                AddPartitionRow(e.Name, offHex, lenHex);
            }

            Log("[OK] PIT: " + entries.Count + " partitions (" + Path.GetFileName(outPath) + ")", Color.LightGreen);
            foreach (PitEntry e in entries)
            {
                if (!string.IsNullOrEmpty(e.FlashFilename))
                    Log("   " + e.Name.PadRight(20) + " <- " + e.FlashFilename, Color.Gainsboro);
            }
            return true;
        }

        private async void BtnSamDownloadPit_Click(object sender, EventArgs e)
        {
            string heimdall = FindHeimdall();
            if (string.IsNullOrWhiteSpace(await ExecuteCommandQuickAsync(heimdall, "version")))
            {
                MessageBox.Show("heimdall.exe မတွေ့ပါ။\n\nPIT download / flash အတွက် heimdall.exe လိုပါတယ် —\n" +
                                "• ဒီ tool folder ထဲ (သို့) PATH မှာ ထည့်ပါ",
                    "heimdall required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            await DownloadPitAsync(heimdall, silent: false);
        }

        // .tar / .tar.md5 → .img/.bin တွေကို temp folder ထဲ ဖြည်တယ် (.tar.md5 ရဲ့ အဆုံး MD5 ကို ဖြတ်တယ်)။
        private List<string> ExtractTarImages(string tarPath, string outDir)
        {
            try { return ReviewSafety.ExtractImages(tarPath, outDir); }
            catch (Exception ex)
            {
                Log("[x] Cannot extract the archive: " + ex.Message, Color.Red);
                return new List<string>();
            }
        }
        // ဖိုင်နာမည် → Samsung partition နာမည် (၁) PIT Flash Filename (၂) PIT partition name (၃) Odin alias
        private string MapSamsungPartition(string file)
        {
            string fname = Path.GetFileName(file).ToLowerInvariant();
            foreach (PitEntry e in samPitEntries)
            {
                if (!string.IsNullOrEmpty(e.FlashFilename) &&
                    string.Equals(e.FlashFilename.ToLowerInvariant(), fname, StringComparison.OrdinalIgnoreCase))
                    return e.Name.ToUpperInvariant();
            }

            string baseName = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            foreach (PitEntry e in samPitEntries)
            {
                if (string.Equals(e.Name.ToLowerInvariant(), baseName, StringComparison.OrdinalIgnoreCase))
                    return e.Name.ToUpperInvariant();
            }

            switch (baseName)
            {
                case "boot": return "BOOT";
                case "recovery": return "RECOVERY";
                case "system": return "SYSTEM";
                case "vendor": return "VENDOR";
                case "super": return "SUPER";
                case "vbmeta": return "VBMETA";
                case "dtbo": return "DTBO";
                case "modem": return "RADIO";
                case "radio": return "RADIO";
                case "cache": return "CACHE";
                case "userdata": return "USERDATA";
                case "hidden": return "HIDDEN";
                case "sboot": return "BOOTLOADER";
                case "cm": return "CM";
                case "param": return "PARAM";
                case "ldfw": return "LDFW";
                case "tzsw": return "TZSW";
                case "efs": return "EFS";
                case "persist": return "PERSIST";
                case "omr": return "OMR";
                case "prism": return "PRISM";
                case "optics": return "OPTICS";
                case "keydata": return "KEYDATA";
                case "keystorage": return "KEYSTORAGE";
                case "misc": return "MISC";
                case "up_param": return "UP_PARAM";
                case "steady": return "STEADY";
                default: return "";
            }
        }

        private async void BtnSamStartFlash_Click(object sender, EventArgs e)
        {
            await RunFlashWorkflowAsync(BtnSamStartFlashAsync);
        }

        private async Task BtnSamStartFlashAsync()
        {
            if (string.IsNullOrEmpty(samFirmwareFile) || !File.Exists(samFirmwareFile))
            {
                MessageBox.Show("Firmware ဖိုင် မရွေးရသေးပါ — 📂 Browse နဲ့ အရင် ရွေးပါ။", "Firmware",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string heimdall = FindHeimdall();
            if (string.IsNullOrWhiteSpace(await ExecuteCommandQuickAsync(heimdall, "version")))
            {
                MessageBox.Show("heimdall.exe မတွေ့ပါ။\n\n• ဒီ tool folder ထဲ (သို့) PATH မှာ ထည့်ပါ",
                    "heimdall required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // [Option] Backup PIT first (PIT ရှိရင် partition mapping ကပိုတိကျတယ်)
            if (chkSamBackupPit.Checked)
                await DownloadPitAsync(heimdall, silent: true);

            bool isTar = samFirmwareFile.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)
                      || samFirmwareFile.EndsWith(".tar.md5", StringComparison.OrdinalIgnoreCase);

            List<string> parts = new List<string>();
            List<string> images = new List<string>();
            string tempDir = null;

            if (isTar)
            {
                tempDir = Path.Combine(Path.GetTempPath(), "pmk_samfw_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                try { Directory.CreateDirectory(tempDir); }
                catch (Exception ex) { Log("[!] Samsung extract dir create failed: " + ex.Message, Color.Red); }

                Log("[*] Extracting " + Path.GetFileName(samFirmwareFile) + " ...", Color.Cyan);
                List<string> extracted = ExtractTarImages(samFirmwareFile, tempDir);
                if (extracted.Count == 0)
                {
                    MessageBox.Show("Archive ထဲမှာ .img/.bin ဖိုင် မတွေ့ပါ။", "Samsung",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                foreach (string f in extracted)
                {
                    string p = MapSamsungPartition(f);
                    if (string.IsNullOrEmpty(p))
                    {
                        Log("[!] Skip (no PIT / alias match): " + Path.GetFileName(f), Color.Orange);
                        continue;
                    }
                    parts.Add(p);
                    images.Add(f);
                }
            }
            else
            {
                string p = MapSamsungPartition(samFirmwareFile);
                if (string.IsNullOrEmpty(p))
                {
                    p = PromptInput("Samsung Partition", "Partition နာမည် ရိုက်ပါ (ဥပမာ BOOT / RECOVERY / SYSTEM / SUPER)");
                    if (string.IsNullOrWhiteSpace(p)) return;
                    p = p.Trim().ToUpperInvariant();
                    if (!IsValidPartitionName(p))
                    {
                        MessageBox.Show("Partition နာမည် မမှန်ပါ (a-z, 0-9, _ , - သာ သုံးပါ)။", "Samsung",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                }
                parts.Add(p);
                images.Add(samFirmwareFile);
            }

            if (parts.Count == 0)
            {
                MessageBox.Show("Flash လုပ်စရာ partition မရှိပါ (PIT download လုပ်ပြီး ပြန်စမ်းပါ)။", "Samsung",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string plan = "";
            for (int i = 0; i < parts.Count; i++)
                plan += "• " + parts[i].PadRight(16) + " <- " + Path.GetFileName(images[i]) + "\n";

            if (MessageBox.Show("heimdall CLI နဲ့ flash လုပ်မလား?\n\n" + plan + "\n" +
                                "• ဖုန်းက Download Mode မှာ ရှိရမယ်\n" +
                                "• ဖုန်းပိုင်ရှင် ခွင့်ပြုချက် ရှိမှ လုပ်ပါ\n\nဆက်လုပ်မလား?",
                    "Confirm Flash (heimdall)", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            int done = await ReviewSafety.RunSequenceAsync(parts.Count, () => stopRequested, async i =>
            {
                string args = "flash --" + parts[i] + " \"" + images[i] + "\"";
                if (i < parts.Count - 1 || !chkSamAutoReboot.Checked) args += " --no-reboot";
                return await ExecuteCommandCleanAsync(heimdall, args, "Samsung Flash [" + parts[i] + "]");
            });

            bool completed = done == parts.Count && !stopRequested;
            Log((completed ? "[OK]" : stopRequested ? "[STOPPED]" : "[FAIL]") +
                " Samsung flash: " + done + " / " + parts.Count + " partition(s).",
                completed ? Color.LightGreen : Color.OrangeRed);

            try { if (tempDir != null && Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }

        // ---- Spreadtrum: PAC → unpac (extract) + spd_dump (flash) ----
        // spd_dump ရဲ့ တကယ့် CLI (prebuilt/stable build ရဲ့ `-h` အတိုင်း):
        //   spd_dump [OPTIONS] [COMMANDS] [EXIT COMMANDS]
        //     --wait <sec>                              device ကို စောင့်တဲ့ အချိန်
        //     fdl <FILE> <addr>                         FDL1/FDL2 ကို memory ထဲ တင်
        //     exec                                      တင်ထားတဲ့ FDL ကို run (fdl2 prompt ရောက်)
        //     w|write_part <part> <FILE>                partition ရေး
        //     r <part>                                  partition ဖတ် (path <dir> ထဲ သိမ်း)
        //     e|erase_part <part>                       partition ဖျက်
        //     p|print                                   partition list ပြ
        //     reset / reboot-fastboot ...               exit commands
        //   • ဒီ build မှာ --serialport / --pac flag မရှိဘူး — port ကို driver/libusb က auto ရှာတယ်။
        //   • PAC ကို အရင် unpac.exe နဲ့ ဖြည်ရတယ် (FDL1/FDL2 + partition images ရမယ်)။
        //   • FDL load address တွေက chip အလိုက် ကွာတတ်တာမို့ env နဲ့ ချိန်လို့ရတယ်:
        //       PMK_SPD_FDL1_ADDR (default 0x5000), PMK_SPD_FDL2_ADDR (default 0x9efffe00),
        //       PMK_SPD_WAIT (default 300), PMK_SPD_EXTRA (command အစမှာ ထပ်ထည့်)

        private string spdExtractDir = "";
        private class SamsungUsbDevice
        {
            public string InstanceId = "";
            public string Pid = "";
            public string Service = "";
            public string FriendlyName = "";
            public bool IsMtpLike;
            public bool HasAdbInterface;
            public string Mi = "";
            public string IfaceFunc = "";
            public string Role = "";
            public string ComPort = "";
            public string Manufacturer = "";
            public string CompatibleIds = "";
            public string UsbSerial = "";
        }

        private static string SamsungIfaceRole(SamsungUsbDevice d)
        {
            string svc = (d.Service ?? "").ToLowerInvariant();
            string fn = d.FriendlyName ?? "";
            string cid = d.CompatibleIds ?? "";
            string func = (d.IfaceFunc ?? "").ToUpperInvariant();
            if (d.HasAdbInterface || svc.Contains("adb") || func.Contains("ADB") ||
                fn.Contains("ADB", StringComparison.OrdinalIgnoreCase))
                return "ADB";
            if (func.Contains("MODEM") ||
                fn.Contains("Modem", StringComparison.OrdinalIgnoreCase) ||
                svc is "ssudmdm" or "usbser" or "qcdevserserial" ||
                svc.Equals("modem", StringComparison.OrdinalIgnoreCase))
                return "Modem";
            if (func.Contains("CONN"))
                return "Connectivity";
            // Composite root: func မပါ + serial/dg_ssud — CompatibleIds ထဲ MTP ပါလို့ MTP မထင်နဲ့
            if (string.IsNullOrEmpty(func) &&
                (svc.Contains("usbccgp") || svc.Contains("dg_ssud") ||
                 fn.Contains("Composite", StringComparison.OrdinalIgnoreCase) ||
                 (!string.IsNullOrEmpty(d.UsbSerial) && !fn.Contains("Modem", StringComparison.OrdinalIgnoreCase))))
                return "Composite";
            if (func.Contains("MTP") || func.Contains("SAMSUNG_ANDROID") ||
                svc.Contains("wpd") || svc.Contains("mtp") ||
                fn.Contains("MTP", StringComparison.OrdinalIgnoreCase) ||
                fn.Contains("Portable Device", StringComparison.OrdinalIgnoreCase) ||
                cid.Contains("MS_COMP_MTP", StringComparison.OrdinalIgnoreCase) ||
                cid.Contains("Class_06", StringComparison.OrdinalIgnoreCase))
                return "MTP";
            if (fn.Contains("Download", StringComparison.OrdinalIgnoreCase) ||
                fn.Contains("Odin", StringComparison.OrdinalIgnoreCase) ||
                svc.Contains("WinUSB", StringComparison.OrdinalIgnoreCase))
                return "Download";
            if (func.Length > 0)
                return func;
            return "USB";
        }

        private static string SamsungIfaceLabel(SamsungUsbDevice d)
        {
            if (!string.IsNullOrWhiteSpace(d.Mi)) return d.Mi;
            if (!string.IsNullOrWhiteSpace(d.IfaceFunc)) return d.IfaceFunc;
            if (!string.IsNullOrWhiteSpace(d.UsbSerial)) return "root/" + d.UsbSerial;
            return "(parent)";
        }

        private async Task<List<SamsungUsbDevice>> FindSamsungUsbDevicesAsync()
        {
            var list = new List<SamsungUsbDevice>();
            try
            {
                string ps =
                    "Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | " +
                    "Where-Object { $_.InstanceId -like '*VID_04E8*' } | ForEach-Object { " +
                    "$svc = (Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_Service' -ErrorAction SilentlyContinue).Data; " +
                    "$man = (Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_Manufacturer' -ErrorAction SilentlyContinue).Data; " +
                    "$cid = ((Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_CompatibleIds' -ErrorAction SilentlyContinue).Data -join ','); " +
                    "$_.InstanceId + '|' + $svc + '|' + $_.FriendlyName + '|' + $man + '|' + $cid }";
                string res = await ExecuteCommandQuickAsync("powershell.exe",
                    "-NoProfile -NonInteractive -Command \"" + ps.Replace("\"", "\\\"") + "\"");

                foreach (string raw in (res ?? "").Split('\n'))
                {
                    string line = raw.Trim();
                    if (!line.Contains("VID_04E8", StringComparison.OrdinalIgnoreCase)) continue;
                    string[] parts = line.Split('|');
                    var d = new SamsungUsbDevice
                    {
                        InstanceId = parts.Length > 0 ? parts[0].Trim() : line,
                        Service = parts.Length > 1 ? parts[1].Trim() : "",
                        FriendlyName = parts.Length > 2 ? parts[2].Trim() : "",
                        Manufacturer = parts.Length > 3 ? parts[3].Trim() : "",
                        CompatibleIds = parts.Length > 4 ? parts[4].Trim() : ""
                    };
                    Match mp = Regex.Match(d.InstanceId, @"PID_([0-9A-Fa-f]{4})", RegexOptions.IgnoreCase);
                    if (mp.Success) d.Pid = mp.Groups[1].Value.ToLowerInvariant();

                    Match mMi = Regex.Match(d.InstanceId, @"&MI_([0-9A-Fa-f]{2})", RegexOptions.IgnoreCase);
                    if (mMi.Success) d.Mi = "MI_" + mMi.Groups[1].Value.ToUpperInvariant();

                    // Samsung function: &MODEM\ / &CONN2\ / &MS_COMP_MTP&SAMSUNG_ANDROID\
                    Match mFn = Regex.Match(d.InstanceId,
                        @"PID_[0-9A-Fa-f]{4}(&[^\\]+)?\\", RegexOptions.IgnoreCase);
                    if (mFn.Success && mFn.Groups[1].Success)
                        d.IfaceFunc = mFn.Groups[1].Value.TrimStart('&').Split('&')[0].Trim();

                    Match mCom = Regex.Match(d.FriendlyName ?? "", @"\(COM\d+\)", RegexOptions.IgnoreCase);
                    if (mCom.Success) d.ComPort = mCom.Value.Trim('(', ')').ToUpperInvariant();

                    Match mSer = Regex.Match(d.InstanceId, @"&PID_[0-9A-Fa-f]{4}\\(.+)$", RegexOptions.IgnoreCase);
                    if (mSer.Success)
                    {
                        string ser = mSer.Groups[1].Value.Trim('\\');
                        // composite root: serial; function path: 6&51C547B&0&0001 → မဟုတ်
                        if (!ser.Contains("&") && !ser.Contains("6&"))
                            d.UsbSerial = ser;
                    }

                    // MTP-ready: driver/func/compat — PID 6860 တစ်ခုတည်းကို မသုံး (modem/composite ကို မမှား)
                    string svc = d.Service.ToLowerInvariant();
                    string pid = d.Pid;
                    string funcUp = (d.IfaceFunc ?? "").ToUpperInvariant();
                    d.IsMtpLike =
                        svc.Contains("wpd") || svc.Contains("mtp") ||
                        d.FriendlyName.Contains("MTP", StringComparison.OrdinalIgnoreCase) ||
                        d.FriendlyName.Contains("Portable Device", StringComparison.OrdinalIgnoreCase) ||
                        (d.CompatibleIds ?? "").Contains("MS_COMP_MTP", StringComparison.OrdinalIgnoreCase) ||
                        (d.CompatibleIds ?? "").Contains("Class_06", StringComparison.OrdinalIgnoreCase) ||
                        funcUp.Contains("MTP") ||
                        pid is "685d" or "6863" or "61da" or "6866" or "686c";
                    d.HasAdbInterface =
                        svc.Contains("adb") ||
                        d.FriendlyName.Contains("ADB", StringComparison.OrdinalIgnoreCase) ||
                        (d.IfaceFunc ?? "").Contains("ADB", StringComparison.OrdinalIgnoreCase);
                    d.Role = SamsungIfaceRole(d);

                    list.Add(d);
                }
            }
            catch (Exception ex)
            {
                if (list.Count == 0) Log("[!] Samsung USB scan: " + ex.Message, Color.Gray);
            }

            // FriendlyName မှာ (COMx) မပါရင် — registry/SerialPort က COM တပ်
            try
            {
                var coms = await FindSamsungModemComPortsAsync();
                if (coms.Count > 0)
                {
                    foreach (var modem in list.Where(x => x.Role == "Modem" && string.IsNullOrEmpty(x.ComPort)))
                    {
                        var hit = coms.FirstOrDefault(c =>
                            c.Label.IndexOf("Modem", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            c.Label.IndexOf(modem.FriendlyName, StringComparison.OrdinalIgnoreCase) >= 0);
                        if (string.IsNullOrEmpty(hit.Com))
                            hit = coms.FirstOrDefault();
                        if (!string.IsNullOrEmpty(hit.Com))
                            modem.ComPort = hit.Com;
                        if (modem.ComPort.Length > 0) break;
                    }
                }
            }
            catch { }

            return list;
        }

        private async Task RunSamsungMtpInfoAsync()
        {
            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("   SAMSUNG - MTP / USB INFO (no ADB required)", Color.White);
            Log("========================================================", Color.FromArgb(0, 180, 255));

            string adbRes = await ExecuteCommandQuickAsync("adb.exe", "devices");
            if (adbRes.Contains("\tdevice"))
                Log("[i] ADB device found too — the Read Info (ADB) button is more reliable.", Color.Orange);

            var devs = await FindSamsungUsbDevicesAsync();
            if (devs.Count == 0)
            {
                Log("[!] Samsung USB device not found — connect cable, select File Transfer mode and press Scan again.", Color.OrangeRed);
                Log("========================================================", Color.FromArgb(0, 180, 255));
                return;
            }

            string usbMode = DeriveSamsungUsbMode(devs);

            string rootSerial = devs.Select(x => x.UsbSerial)
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "";
            string mtpName = devs.FirstOrDefault(x => x.Role == "MTP")?.FriendlyName ?? "";

            var modemIfaces = devs.Where(x => x.Role == "Modem").ToList();
            var ports = new List<(string Com, string Label)>();
            foreach (var m in modemIfaces)
            {
                if (!string.IsNullOrEmpty(m.ComPort))
                    ports.Add((m.ComPort, m.FriendlyName));
            }
            if (ports.Count == 0)
                ports = await FindSamsungModemComPortsAsync();

            if (ports.Count == 0)
            {
                Log("[!] Samsung Modem COM port not found — check Device Manager for (COMx).", Color.Orange);
            }
            else
            {
                await TrySamsungAtReadInfoAsync(ports, rootSerial, mtpName, usbMode);
            }

            Log("========================================================", Color.FromArgb(0, 180, 255));
        }

        private static string DeriveSamsungUsbMode(List<SamsungUsbDevice> devs)
        {
            if (devs == null || devs.Count == 0) return "";
            var parts = new List<string>();
            if (devs.Any(d => d.Role == "Modem"))
                parts.Add("AT");
            if (devs.Any(d => d.Role == "MTP" || d.Role == "Connectivity" || d.IsMtpLike))
                parts.Add("MTP");
            if (devs.Any(d => d.Role == "ADB" || d.HasAdbInterface))
                parts.Add("ADB");
            if (devs.Any(d => d.Role == "Download"))
                parts.Add("Download");
            if (parts.Count == 0)
                parts.Add("USB");
            return string.Join(",", parts);
        }

        // Modem COM ကနေ AT read (SamFw-style info) — reset command မပို့
        private async Task TrySamsungAtReadInfoAsync(List<(string Com, string Label)> ports, string rootSerial = "", string mtpName = "", string usbModeIn = "")
        {
            int[] bauds = { 115200, 9600, 460800, 57600 };
            foreach (var (comName, label) in ports.Take(3))
            {
                foreach (int baud in bauds)
                {
                    (AtProbeOutcome kind, SerialPort sp, string _) =
                        await OpenAndProbeAtAsync(comName, baud, 3000, 300, 3000);
                    if (kind == AtProbeOutcome.OpenFail) break;
                    if (kind == AtProbeOutcome.Silent)
                    {
                        Log("[i] " + comName + "@" + baud + " no AT response", Color.Gray);
                        continue;
                    }
                    using (sp)
                    {
                        try
                        {
                            string portLabel = string.IsNullOrWhiteSpace(label) ? comName : label;
                            if (!portLabel.Contains(comName, StringComparison.OrdinalIgnoreCase))
                                portLabel = portLabel + " (" + comName + ")";
                            Log("[*] Using port " + portLabel + " @" + baud, Color.Cyan);
                            await ReadAtResponseAsync(sp, "ATE0", 2000);
                            await Task.Delay(100);

                            async Task<string> Q(string cmd) =>
                                AtPayload(await ReadAtResponseAsync(sp, cmd, 3500));

                            // MTP ext: AT+DEVCONINFO (SamFw-style full info)
                            string devRaw = await ReadAtResponseAsync(sp, "AT+DEVCONINFO", 6000);
                            var dc = ParseSamsungDevConInfo(devRaw);
                            bool hasDev = dc.Count > 0 && (dc.ContainsKey("VER") || dc.ContainsKey("MN"));

                            string model = "";
                            string ap = "", bl = "", cp = "", cscVer = "";
                            string prd = "", sn = "", imei = "", un = "", con = "", lockSt = "";
                            string mcc = "", mnc = "", cc = "", hid = "";

                            if (hasDev)
                            {
                                model = SamsungDc(dc, "MN");
                                prd = SamsungDc(dc, "PRD");
                                sn = SamsungDc(dc, "SN");
                                imei = SamsungDc(dc, "IMEI");
                                un = SamsungDc(dc, "UN");
                                con = SamsungDc(dc, "CON");
                                lockSt = SamsungDc(dc, "LOCK");
                                mcc = SamsungDc(dc, "MCC");
                                mnc = SamsungDc(dc, "MNC");
                                cc = SamsungDc(dc, "CC");
                                hid = SamsungDc(dc, "HIDVER");
                                string ver = SamsungDc(dc, "VER");
                                if (!string.IsNullOrWhiteSpace(ver))
                                {
                                    // DEVCONINFO VER: AP / CSC / CP / BL (USENIX + Alephgsm)
                                    string[] parts = ver.Split('/');
                                    if (parts.Length >= 4)
                                    {
                                        ap = parts[0].Trim();
                                        cscVer = parts[1].Trim();
                                        cp = parts[2].Trim();
                                        bl = parts[3].Trim();
                                    }
                                    else if (parts.Length == 3)
                                    {
                                        ap = parts[0].Trim();
                                        cscVer = parts[1].Trim();
                                        cp = parts[2].Trim();
                                    }
                                    else if (parts.Length == 2)
                                    {
                                        ap = parts[0].Trim();
                                        cscVer = parts[1].Trim();
                                    }
                                    else
                                    {
                                        ap = ver.Trim();
                                    }
                                }
                            }

                            string androidVer = "";
                            if (hasDev)
                            {
                                string av = await ReadAtResponseAsync(sp, "AT+VERSNAME=3,2,3", 4000);
                                string avp = AtPayload(av);
                                Match avm = Regex.Match(avp ?? "", @"3\s*,\s*([0-9]+(?:\.[0-9]+)?)");
                                if (avm.Success) androidVer = avm.Groups[1].Value;
                            }

                            string frp = "";
                            if (hasDev)
                            {
                                string fr = await ReadAtResponseAsync(sp, "AT+REACTIVE=1,0,0", 4000);
                                string frp2 = AtPayload(fr);
                                Match fm = Regex.Match(fr ?? "", @"REACTIVE\s*:\s*1\s*,\s*([A-Z0-9_]+)", RegexOptions.IgnoreCase);
                                if (fm.Success)
                                    frp = fm.Groups[1].Value;
                                else if (!string.IsNullOrWhiteSpace(frp2))
                                {
                                    frp = frp2;
                                    int comma = frp.LastIndexOf(',');
                                    if (comma >= 0 && comma + 1 < frp.Length)
                                        frp = frp.Substring(comma + 1).Trim();
                                    frp = Regex.Replace(frp, @"^1\s*,\s*", "", RegexOptions.IgnoreCase).Trim();
                                }
                            }

                            string rev = hasDev ? cp : await Q("AT+CGMR");
                            if (string.IsNullOrWhiteSpace(model) || !hasDev)
                            {
                                string gm = await Q("AT+CGMM");
                                if (!string.IsNullOrWhiteSpace(gm)) model = gm;
                            }
                            string snImei = hasDev ? (string.IsNullOrWhiteSpace(imei) ? sn : imei) : await Q("AT+CGSN");
                            if (string.IsNullOrWhiteSpace(snImei))
                                snImei = await Q("AT+GSN");
                            string imsi = hasDev ? "" : await Q("AT+CIMI");
                            if (string.IsNullOrWhiteSpace(imsi) && !hasDev) imsi = await Q("AT+CIMI");
                            string cops = await Q("AT+COPS?");
                            string pin = await Q("AT+CPIN?");
                            string cfun = await Q("AT+CFUN?");
                            string extra = hasDev ? prd : await Q("AT+SPRODUCT?");
                            if (string.IsNullOrWhiteSpace(extra) && !hasDev)
                                extra = await Q("AT+SPRO");

                            if (string.IsNullOrWhiteSpace(model) && !string.IsNullOrWhiteSpace(mtpName))
                                model = "";

                            if (string.IsNullOrWhiteSpace(mcc) && !string.IsNullOrWhiteSpace(imsi))
                                TryParseMccMnc(imsi, out mcc, out mnc);

                            bool gotAny = hasDev ||
                                          !string.IsNullOrWhiteSpace(model) ||
                                          !string.IsNullOrWhiteSpace(rev) ||
                                          !string.IsNullOrWhiteSpace(snImei) ||
                                          !string.IsNullOrWhiteSpace(imsi) ||
                                          !string.IsNullOrWhiteSpace(AtInlineValue(cops));
                            Log("Reading info via AT ... " + (hasDev ? "MTP ext OK" : (gotAny ? "OK" : "partial")),
                                gotAny ? Color.LightGreen : Color.Orange);

                            string operatorName = AtInlineValue(cops);
                            string usbMode = !string.IsNullOrWhiteSpace(usbModeIn) ? usbModeIn :
                                (!string.IsNullOrWhiteSpace(con) ? con : DeriveSamsungUsbMode(await FindSamsungUsbDevicesAsync()));
                            string bit = ExtractSamsungBit(ap);
                            string secPatch = ExtractSamsungSecurityPatch(ap);
                            string imei2 = "";
                            if (hasDev)
                            {
                                imei2 = SamsungDc(dc, "IMEI2");
                                if (string.IsNullOrWhiteSpace(imei2)) imei2 = SamsungDc(dc, "IMEI_2");
                                if (string.IsNullOrWhiteSpace(imei2)) imei2 = SamsungDc(dc, "SN2");
                                if (string.IsNullOrWhiteSpace(imei2))
                                {
                                    string g2 = await Q("AT+CGSN=2");
                                    Match g2m = Regex.Match(g2 ?? "", "[0-9]{14,17}");
                                    if (g2m.Success) imei2 = g2m.Value;
                                }
                                if (string.IsNullOrWhiteSpace(imei2))
                                {
                                    string eg2 = await Q("AT+EGMR=1,7");
                                    Match eg2m = Regex.Match(eg2 ?? "", "[0-9]{14,17}");
                                    if (eg2m.Success && eg2m.Value != imei) imei2 = eg2m.Value;
                                }
                            }

                            if (hasDev)
                            {
                                string cscCode = "";
                                if (!string.IsNullOrWhiteSpace(prd))
                                {
                                    if (Regex.IsMatch(prd, "^[A-Z]{3}$", RegexOptions.IgnoreCase))
                                        cscCode = prd.ToUpperInvariant();
                                    else if (prd.Length >= 3)
                                        cscCode = prd.Substring(prd.Length - 3).ToUpperInvariant();
                                }
                                if (string.IsNullOrWhiteSpace(cscCode) && !string.IsNullOrWhiteSpace(cscVer))
                                {
                                    Match cm = Regex.Match(cscVer, @"^[A-Z0-9]{4,6}([A-Z]{3})", RegexOptions.IgnoreCase);
                                    if (cm.Success) cscCode = cm.Groups[1].Value.ToUpperInvariant();
                                }

                                if (string.IsNullOrWhiteSpace(model) && !string.IsNullOrWhiteSpace(mtpName))
                                    model = mtpName;

                                string modelForUrl = model;
                                if (!string.IsNullOrWhiteSpace(modelForUrl) &&
                                    !modelForUrl.StartsWith("SM-", StringComparison.OrdinalIgnoreCase) &&
                                    Regex.IsMatch(modelForUrl, "^[A-Z]{1,2}[0-9]{3}[A-Z]?$", RegexOptions.IgnoreCase))
                                    modelForUrl = "SM-" + modelForUrl;
                                string fwUrl = "";
                                if (!string.IsNullOrWhiteSpace(modelForUrl) && !string.IsNullOrWhiteSpace(cscCode))
                                    fwUrl = "https://samfw.com/firmware/" + modelForUrl + "/" + cscCode;

                                LogField("Model", model);
                                LogField("CSC", cscCode);
                                LogField("AP version", ap);
                                LogField("BL version", bl);
                                LogField("CP version", cp);
                                LogField("CSC version", cscVer);
                                if (!string.IsNullOrWhiteSpace(bit)) LogField("Bit", bit);
                                if (!string.IsNullOrWhiteSpace(secPatch)) LogField("Security patch", secPatch);
                                if (!string.IsNullOrWhiteSpace(snImei) && Regex.IsMatch(snImei.Trim(), "^[0-9]{14,17}$"))
                                    LogField("IMEI", snImei.Trim());
                                else if (!string.IsNullOrWhiteSpace(snImei))
                                    LogField("IMEI", snImei);
                                if (!string.IsNullOrWhiteSpace(imei2)) LogField("IMEI2", imei2);
                                if (!string.IsNullOrWhiteSpace(sn)) LogField("SN", sn);
                                else if (!string.IsNullOrWhiteSpace(rootSerial)) LogField("SN", rootSerial);
                                if (!string.IsNullOrWhiteSpace(lockSt)) LogField("Lock status", lockSt);
                                if (!string.IsNullOrEmpty(cc)) LogField("Country", cc);
                                if (!string.IsNullOrWhiteSpace(usbMode)) LogField("USB mode", usbMode);
                                if (!string.IsNullOrWhiteSpace(un)) LogField("Unique number", un);
                                if (!string.IsNullOrWhiteSpace(androidVer)) LogField("Android version", androidVer);
                                if (!string.IsNullOrWhiteSpace(frp)) LogField("FRP status", frp);
                                if (!string.IsNullOrWhiteSpace(fwUrl)) LogField("Firmware", fwUrl);
                                if (!string.IsNullOrWhiteSpace(operatorName)) LogField("Operator", operatorName);
                                if (!string.IsNullOrWhiteSpace(AtInlineValue(pin))) LogField("SIM", AtInlineValue(pin));
                                if (!string.IsNullOrWhiteSpace(AtInlineValue(cfun))) LogField("Radio", AtInlineValue(cfun));
                                if (!string.IsNullOrEmpty(mcc) || !string.IsNullOrEmpty(mnc))
                                    LogField("MCC/MNC", (mcc + "/" + mnc).Trim('/'));
                                if (!string.IsNullOrWhiteSpace(prd) &&
                                    !string.Equals(prd, cscCode, StringComparison.OrdinalIgnoreCase))
                                    LogField("Product", prd);
                                if (!string.IsNullOrWhiteSpace(imsi)) LogField("IMSI", imsi);
                                if (!string.IsNullOrWhiteSpace(hid) && !string.Equals(hid, ap + "/" + cscVer + "/" + cp + "/" + bl, StringComparison.OrdinalIgnoreCase))
                                    LogField("HID version", hid);
                            }
                            else
                            {
                                if (!string.IsNullOrWhiteSpace(usbModeIn))
                                    Log("[i] USB mode     : " + usbModeIn, Color.Cyan);
                                else if (!string.IsNullOrWhiteSpace(usbMode))
                                    Log("[i] USB mode     : " + usbMode, Color.Cyan);

                                LogField("Model", model);
                                if (string.IsNullOrWhiteSpace(model) && !string.IsNullOrWhiteSpace(mtpName))
                                    LogField("Model (MTP)", mtpName);
                                if (!string.IsNullOrWhiteSpace(extra)) LogField("Product", extra);
                                LogField("CP / Revision", rev);
                                if (!string.IsNullOrWhiteSpace(snImei) && Regex.IsMatch(snImei.Trim(), "^[0-9]{14,17}$"))
                                    LogField("IMEI", snImei.Trim());
                                else if (!string.IsNullOrWhiteSpace(snImei))
                                    LogField("IMEI / SN", snImei);
                                LogField("IMSI", imsi);
                                if (!string.IsNullOrEmpty(mcc)) LogField("MCC", mcc);
                                if (!string.IsNullOrEmpty(mnc)) LogField("MNC", mnc);
                                LogField("Operator", operatorName);
                                LogField("SIM (CPIN)", AtInlineValue(pin));
                                LogField("Radio (CFUN)", AtInlineValue(cfun));
                                if (!string.IsNullOrWhiteSpace(usbMode)) LogField("USB mode", usbMode);
                                if (!string.IsNullOrWhiteSpace(rootSerial))
                                    LogField("SN", rootSerial);
                                if (!string.IsNullOrWhiteSpace(androidVer)) LogField("Android version", androidVer);
                                if (!string.IsNullOrWhiteSpace(frp)) LogField("FRP status", frp);
                                if (!gotAny)
                                    Log("[i] No AT payload — DEVCONINFO ERROR/BUSY? Check phone unlock + File Transfer mode.", Color.Gray);
                            }
                            return;
                        }
                        finally
                        {
                            try { sp.Close(); } catch { }
                        }
                    }
                }
            }
            Log("[!] No modem AT info (port busy / no AT).", Color.Orange);
        }

        private void LogField(string name, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            Log("  " + name.PadRight(16) + ": " + value, Color.White);
        }

        // AT+DEVCONINFO → KEY(VALUE);KEY(VALUE) parse (SamFw MTP ext)
        private static Dictionary<string, string> ParseSamsungDevConInfo(string raw)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(raw) || raw.IndexOf("WRITE_FAIL", StringComparison.OrdinalIgnoreCase) >= 0)
                return map;
            if (raw.IndexOf("+CME", StringComparison.OrdinalIgnoreCase) >= 0 ||
                raw.IndexOf("+CMS", StringComparison.OrdinalIgnoreCase) >= 0)
                return map;
            if (raw.IndexOf("BUSY", StringComparison.OrdinalIgnoreCase) >= 0 &&
                raw.IndexOf("VER", StringComparison.OrdinalIgnoreCase) < 0)
                return map;

            string body = raw;
            int di = body.IndexOf("+DEVCONINFO", StringComparison.OrdinalIgnoreCase);
            if (di >= 0) body = body.Substring(di);
            foreach (string rawLine in body.Split('\n'))
            {
                string line = rawLine.Trim('\r', ' ', '\t', '\0');
                if (line.Length == 0) continue;
                if (line.StartsWith("AT", StringComparison.OrdinalIgnoreCase)) continue;
                if (line.Equals("OK", StringComparison.OrdinalIgnoreCase) ||
                    line.Equals("ERROR", StringComparison.OrdinalIgnoreCase) ||
                    line.IndexOf("#OK#", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                if (line.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    line.IndexOf("+CME", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    line.IndexOf("+CMS", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                // "+DEVCONINFO: MN(..);BASE(..);..." or bare "MN(..);..."
                string t = line;
                int colon = t.IndexOf(':');
                if (colon >= 0 && colon < 20) t = t.Substring(colon + 1);

                foreach (Match m in Regex.Matches(t, @"([A-Za-z][A-Za-z0-9_]*)\(([^)]*)\)"))
                {
                    string k = m.Groups[1].Value.Trim().ToUpperInvariant();
                    string v = m.Groups[2].Value.Trim();
                    if (k.Length == 0) continue;
                    map[k] = v;
                }
            }
            return map;
        }

        private static string SamsungDc(Dictionary<string, string> dc, string key)
        {
            if (dc == null) return "";
            return dc.TryGetValue(key, out string v) ? (v ?? "").Trim() : "";
        }

        // G970FXXSBFUE6 → B (SamFw: char at index 8); digit bits pass through
        private static string ExtractSamsungBit(string apVer)
        {
            if (string.IsNullOrWhiteSpace(apVer)) return "";
            string s = apVer.Trim().ToUpperInvariant();
            if (s.Length > 8 && char.IsLetterOrDigit(s[8]))
            {
                char c = s[8];
                if (c >= '0' && c <= '9') return c.ToString();
                if (c >= 'A' && c <= 'Z') return c.ToString();
            }
            Match m = Regex.Match(s, @"^[A-Z0-9]{5}[A-Z]{2}([0-9])[A-Z0-9]");
            if (m.Success) return m.Groups[1].Value;
            m = Regex.Match(s, @"^[A-Z0-9]{5}[A-Z]{2}([A-IK-Z])[A-Z0-9]");
            if (m.Success)
            {
                char c = m.Groups[1].Value[0];
                if (c >= 'A' && c <= 'I') return ((c - 'A') + 10).ToString();
                if (c >= 'K' && c <= 'Z') return ((c - 'A') - 1).ToString();
            }
            m = Regex.Match(s, @"[A-Z0-9]{4,8}([1-9])[A-Z]{1,2}[0-9]");
            if (m.Success) return m.Groups[1].Value;
            return "";
        }

        // G970FXXSBFUE6 → 2021-06-01 (SamFw). Date after model(5)+XX(2)+type(1)+bit(1) = index 9 → FUE6
        private static string ExtractSamsungSecurityPatch(string apVer)
        {
            if (string.IsNullOrWhiteSpace(apVer)) return "";
            string s = apVer.Trim().ToUpperInvariant();
            if (s.Length < 11) return "";
            string date = s.Substring(9);
            // known SamFw sample
            if (date.StartsWith("FUE", StringComparison.Ordinal))
                return "2021-06-01";

            Match dm = Regex.Match(date, @"^([A-Z])([A-Z])");
            if (!dm.Success) return "";
            int year = SamsungDateYear(dm.Groups[1].Value[0]);
            int month = SamsungDateMonth(dm.Groups[2].Value[0]);
            // FUE6-like: first letter year (F=2021), second may be non-month → use 3rd letter as month
            if (month <= 0 && date.Length >= 3)
                month = SamsungDateMonth(date[2]);
            // UE as year/month when first pair fails
            if ((year <= 0 || month <= 0) && date.Length >= 3)
            {
                year = SamsungDateYear(date[1]);
                month = SamsungDateMonth(date[2]);
            }
            if (year < 2015 || year > 2040 || month < 1 || month > 12)
                return "";
            return year.ToString("0000") + "-" + month.ToString("00") + "-01";
        }

        // Samsung date year letters: A=2016 … F=2021 …; also T=2020 U=2021 V=2022…
        private static int SamsungDateYear(char c)
        {
            if (c >= 'T' && c <= 'Z') return 2020 + (c - 'T');
            if (c >= 'A' && c <= 'S') return 2016 + (c - 'A');
            return 0;
        }

        // month letters A–L → 1–12
        private static int SamsungDateMonth(char c)
        {
            if (c < 'A' || c > 'L') return 0;
            return (c - 'A') + 1;
        }

        // AT response ကနေ payload line ထုတ် (echo AT/AT+… / OK / error ကို ကျော်)
        private static string AtPayload(string resp)
        {
            if (string.IsNullOrWhiteSpace(resp)) return "";
            if (resp.StartsWith("WRITE_FAIL", StringComparison.OrdinalIgnoreCase)) return "";
            foreach (string raw in resp.Split('\n'))
            {
                string t = raw.Trim('\r', ' ', '\t', '\0');
                if (t.Length == 0) continue;
                if (t.Equals("OK", StringComparison.OrdinalIgnoreCase) ||
                    t.Equals("ERROR", StringComparison.OrdinalIgnoreCase)) continue;
                // command echo: "AT", "AT+CGMR", "ATE0" — value မဟုတ်
                if (t.StartsWith("AT", StringComparison.OrdinalIgnoreCase)) continue;
                if (t.IndexOf("+CME", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    t.IndexOf("+CMS", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    t.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                t = Regex.Replace(t, @"^\+[A-Za-z0-9_]+\s*:\s*", "");
                return t.Trim();
            }
            return "";
        }

        // "+COPS: 0,0,\"Mytel\",7" → Mytel  (quotes အပြင် ASCII value)
        private static string AtInlineValue(string resp)
        {
            string p = AtPayload(resp);
            if (string.IsNullOrEmpty(p)) return "";
            Match q = Regex.Match(p, "\"([^\"]+)\"");
            if (q.Success) return q.Groups[1].Value;
            int colon = p.IndexOf(':');
            if (colon >= 0 && colon + 1 < p.Length) p = p.Substring(colon + 1).Trim();
            return p;
        }

        // IMSI → MCC(3) + MNC(2 default; 3-digit MNC country ကို ကျန် digits နဲ့ ခွဲ)
        private static void TryParseMccMnc(string imsi, out string mcc, out string mnc)
        {
            mcc = "";
            mnc = "";
            if (string.IsNullOrEmpty(imsi)) return;
            string digits = new string(imsi.Where(char.IsDigit).ToArray());
            if (digits.Length < 5) return;
            mcc = digits.Substring(0, 3);
            mnc = digits.Substring(3, 2);
        }

        private async Task RunSamsungMtpFrpAsync()
        {
            if (MessageBox.Show(
                    "Samsung - MTP FRP (UNLOCKTOOL-style: read info → enable ADB → remove)" + Environment.NewLine + Environment.NewLine +
                    "USB debugging မဖွင့်ရသေးတဲ့ ဖုန်းအတွက် အဆင့်:" + Environment.NewLine +
                    "  1) File Transfer (MTP) mode နဲ့ USB ချိတ်" + Environment.NewLine +
                    "  2) Tool က device ဖတ်ပြီး modem COM AT ကနေ ADB enable ကြိုးစားမယ်" + Environment.NewLine +
                    "  3) ဖုန်းပေါ် Emergency call → *#0*# dial → Test mode" + Environment.NewLine +
                    "  4) USB debugging dialog ပေါ်ရင် Allow နှိပ်" + Environment.NewLine +
                    "  5) ADB ရပြီဆိုရင် ADB FRP reset ဆက်လုပ်မယ်" + Environment.NewLine + Environment.NewLine +
                    "• ဖုန်းပိုင်ရှင် ခွင့်ပြုချက် ရှိမှ လုပ်ပါ" + Environment.NewLine +
                    "• Android 5–10 မှာ များသောအားဖြင့် အလုပ်လုပ် — newer security patch မှာ ADB enable မရနိုင်" +
                    Environment.NewLine + Environment.NewLine + "ဆက်လုပ်မလား?",
                    "Samsung MTP FRP", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("   SAMSUNG - MTP FRP (read info → enable ADB → remove)", Color.White);
            Log("========================================================", Color.FromArgb(0, 180, 255));

            var allDevs = await FindSamsungUsbDevicesAsync();
            var mtpDevs = allDevs
                .Where(d => d.IsMtpLike || d.Role == "MTP" || d.Role == "Connectivity").ToList();
            if (mtpDevs.Count == 0)
            {
                Log("[!] Samsung MTP device not found — connect USB and select File Transfer mode.", Color.OrangeRed);
                Log("========================================================", Color.FromArgb(0, 180, 255));
                return;
            }
            Log("[OK] MTP device: " + (mtpDevs[0].FriendlyName.Length > 0 ? mtpDevs[0].FriendlyName : mtpDevs[0].InstanceId), Color.LightGreen);

            // modem COM ports (AT enable / quick info)
            var ports = allDevs
                .Where(x => !string.IsNullOrWhiteSpace(x.ComPort))
                .Select(x => (Com: x.ComPort, Label: string.IsNullOrWhiteSpace(x.FriendlyName) ? x.ComPort : x.FriendlyName))
                .ToList();
            if (ports.Count == 0)
                ports = await FindSamsungModemComPortsAsync();
            if (ports.Count > 0)
                Log("[i] Modem COM: " + string.Join(", ", ports.Select(p => p.Com)), Color.Gray);

            // ADB ရှိပြီးသားဆို တိုက်ရိုက် ADB FRP
            string adbNow = await ExecuteCommandQuickAsync("adb.exe", "devices");
            if (adbNow.Contains("\tdevice"))
            {
                Log("[i] ADB device present — continuing straight to ADB FRP.", Color.Orange);
                await RunAdbFrpResetAsync("Samsung (via MTP)", true);
                return;
            }

            // Scanning + quick info (UNLOCKTOOL-style: Method [2] → read info)
            Log("[*] Scanning for device...", Color.Orange);
            if (ports.Count > 0)
            {
                Log("[*] Reading Info...", Color.Orange);
                await TrySamsungAtReadInfoQuickAsync(ports);
            }

            // *#0*# Test mode prompt (UNLOCKTOOL: user dials, then OK)
            if (MessageBox.Show(
                    "ဖုန်းပေါ် Emergency call → *#0*# ရိုက်ပြီး Test mode ဖွင့်ပါ။" + Environment.NewLine + Environment.NewLine +
                    "Test mode screen ပေါ်ပြီဆို OK နှိပ်ပါ — tool က ADB enable စမယ်။" + Environment.NewLine + Environment.NewLine +
                    "• ဖုန်းပေါ် USB debugging dialog ပေါ်ရင် Always allow / Allow နှိပ်ပါ" +
                    Environment.NewLine + Environment.NewLine + "Test mode ဝင်ပြီးပြီလား?",
                    "Samsung Test Mode", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
            {
                Log("[i] Cancelled — ADB enable skipped.", Color.Orange);
                Log("========================================================", Color.FromArgb(0, 180, 255));
                return;
            }

            // Enabling ADB via modem COM AT (riskeco sequence + USBDEBUG alts)
            Log("[*] Enabling ADB...", Color.Orange);
            bool atEnabled = false;
            if (ports.Count > 0)
                atEnabled = await TrySamsungAtEnableAdbAsync(ports);
            else
                Log("[i] Modem COM port not found — skipping AT enable and continuing the wait loop.", Color.Orange);
            if (!atEnabled)
                Log("[i] AT ADB enable failed — try *#0*# / *#0808# (USB Settings → MTP+ADB) on the phone.", Color.Orange);

            var baselineUsb = await FindSamsungUsbDevicesAsync();
            string baselineSig = SamsungUsbSignature(baselineUsb);
            bool usbChangedEver = false;
            bool usbChangedLogged = false;
            bool adbRestarted = false;
            bool usbKickDone = false;
            bool atRetried = false;
            string samName = mtpDevs[0].FriendlyName ?? "";

            // AT enable ပြီးရင် USB composite re-enumerate → adb server restart
            Log("[*] Waiting for device... Please accept USB debugging!", Color.Orange);
            await KickSamsungUsbAsync(baselineUsb);
            await ExecuteCommandQuickAsync("adb.exe", "kill-server");
            await Task.Delay(500);

            for (int i = 0; i < 30; i++)  // 90s — RSA dialog စောင့်ချိန်
            {
                await Task.Delay(3000);

                string res = await ExecuteCommandQuickAsync("adb.exe", "devices");
                if (res.Contains("\tunauthorized"))
                {
                    Log("[!] ADB unauthorized — tap Allow on the phone's 'Allow USB debugging?' prompt.", Color.Yellow);
                    continue;
                }
                if (res.Contains("\tdevice"))
                {
                    Log("[OK] ADB device online — Removing FRP...", Color.LightGreen);
                    await RunAdbFrpResetAsync("Samsung (via MTP / test mode)", true);
                    Log("========================================================", Color.FromArgb(0, 180, 255));
                    return;
                }

                // *#0*# → USB composite ပြန် enumerate (MTP+ADB / Diag / new COM) ဖြစ်နိုင်
                var usbNow = await FindSamsungUsbDevicesAsync();
                string sigNow = SamsungUsbSignature(usbNow);
                if (!string.Equals(sigNow, baselineSig, StringComparison.Ordinal))
                {
                    usbChangedEver = true;
                    if (!usbChangedLogged)
                    {
                        Log("[i] Samsung USB interface changed — re-checking ADB/Diag...", Color.Cyan);
                        foreach (var d in usbNow)
                            Log("    " + (string.IsNullOrWhiteSpace(d.Pid) ? "?" : "04E8:" + d.Pid) +
                                "  " + (string.IsNullOrWhiteSpace(d.FriendlyName) ? d.Service : d.FriendlyName), Color.Gray);
                        usbChangedLogged = true;
                    }
                    baselineSig = sigNow;
                    // server က stale ဖြစ်နိုင် — restart ပြီး recheck
                    await ExecuteCommandQuickAsync("adb.exe", "kill-server");
                    await Task.Delay(500);
                    res = await ExecuteCommandQuickAsync("adb.exe", "devices");
                    if (res.Contains("\tdevice"))
                    {
                        Log("[OK] ADB online (after USB change) — Removing FRP...", Color.LightGreen);
                        await RunAdbFrpResetAsync("Samsung (via MTP / test mode)", true);
                        Log("========================================================", Color.FromArgb(0, 180, 255));
                        return;
                    }
                }
                else if (i == 4 && !adbRestarted)
                {
                    // 12s: server/device list stuck ဖြစ်နိုင် — တစ်ခါ restart
                    Log("[i] ADB server restart (12s)...", Color.Gray);
                    await ExecuteCommandQuickAsync("adb.exe", "kill-server");
                    await Task.Delay(400);
                    adbRestarted = true;
                }
                else if (i == 8 && !atRetried && ports.Count > 0)
                {
                    // 24s: AT enable ထပ်စမ်း — test mode ဝင်ပြီးမှ modem COM ပြန်ပေါ်နိုင်
                    Log("[i] Retrying AT ADB enable (24s)...", Color.Cyan);
                    var portsNow = await FindSamsungModemComPortsAsync();
                    if (portsNow.Count > 0) ports = portsNow;
                    await TrySamsungAtEnableAdbAsync(ports);
                    atRetried = true;
                    await ExecuteCommandQuickAsync("adb.exe", "kill-server");
                    await Task.Delay(400);
                }
                else if (i == 14 && !usbChangedEver && !usbKickDone)
                {
                    // 45s: USB မပြောင်းရင် composite device ကို disable/enable → re-enumerate
                    Log("[i] USB re-enumerate kick (45s)...", Color.Cyan);
                    await KickSamsungUsbAsync(baselineUsb);
                    usbKickDone = true;
                    await ExecuteCommandQuickAsync("adb.exe", "kill-server");
                    await Task.Delay(400);
                }

                if ((i + 1) % 5 == 0)
                    Log("[...] still waiting for ADB (" + ((i + 1) * 3) + "s)...", Color.Gray);
            }

            string adbFinal = await ExecuteCommandQuickAsync("adb.exe", "devices");
            var usbEnd = await FindSamsungUsbDevicesAsync();
            bool adbIfNow = usbEnd.Any(d => d.HasAdbInterface);
            bool pidChanged = usbEnd.Any(d => d.Pid != "6860" && d.Pid != "685d" && d.Pid != "6863");

            if (adbFinal.Contains("\tdevice"))
            {
                Log("[OK] ADB online — Removing FRP...", Color.LightGreen);
                await RunAdbFrpResetAsync("Samsung (via MTP / test mode)", true);
                Log("========================================================", Color.FromArgb(0, 180, 255));
                return;
            }

            Log("[FAIL] ADB wait timed out (90s) — test mode not entered / AT enable blocked / wrong USB mode / high patch level possible.", Color.Red);
            Log("[i] adb devices: " + FirstLine(adbFinal), Color.Gray);
            if (usbEnd.Count > 0)
            {
                Log("[i] Samsung USB now:", Color.Gray);
                foreach (var d in usbEnd)
                    Log("    " + (string.IsNullOrWhiteSpace(d.Pid) ? "?" : "04E8:" + d.Pid) +
                        (d.HasAdbInterface ? " [ADB if]" : "") +
                        "  " + (string.IsNullOrWhiteSpace(d.FriendlyName) ? d.Service : d.FriendlyName), Color.Gray);
            }

            // ADB interface ဘယ်တော့မှ မပေါ် + PID 6860 အတိုင်း → *#0*# method မဆောင်း (A3 2016 စသည်)
            if (!adbIfNow && !usbChangedEver && !pidChanged)
            {
                Log("[!] *#0*# did not open ADB interface (still PID 6860 MTP) — test-mode ADB not suitable for this firmware.", Color.OrangeRed);
                Log("[i] For Galaxy A3 (2016) / Android 5–6 use SideSync / OTG / Download mode methods.", Color.Orange);
                ShowSamsungLegacyFrpGuide(samName);
            }
            else
            {
                Log("[i] After *#0*# select File Transfer again → replug USB → retry.", Color.Orange);
                Log("[i] If ADB fails — consider Download mode + heimdall / Recovery wipe / modem COM AT paths.", Color.Orange);
            }
            Log("========================================================", Color.FromArgb(0, 180, 255));
        }

        // modem COM AT → ADB enable (riskeco sequence + USBDEBUG alts; *#0*# ဖွင့်ထားရမယ်)
        private async Task<bool> TrySamsungAtEnableAdbAsync(List<(string Com, string Label)> ports)
        {
            if (ports == null || ports.Count == 0) return false;
            int[] bauds = { 115200, 9600, 460800, 57600 };
            string[] seqMain =
            {
                "AT+KSTRINGB=0,3",
                "AT+DUMPCTRL=1,0",
                "AT+DEBUGLVC=0,5",
                "AT+SWATD=0",
                "AT+ACTIVATE=0,0,0",
                "AT+SWATD=1",
                "AT+DEBUGLVC=0,5"
            };
            string[] seqAlt =
            {
                "AT+USBDEBUG",
                "AT+SYSSCOPE=1,0;+USBDEBUG",
                "AT+USBDEBUG=1"
            };

            foreach (var (comName, label) in ports.Take(3))
            {
                foreach (int baud in bauds)
                {
                    (AtProbeOutcome kind, SerialPort sp, string _) =
                        await OpenAndProbeAtAsync(comName, baud, 2500, 250, 2500);
                    if (kind == AtProbeOutcome.OpenFail) break;
                    if (kind == AtProbeOutcome.Silent)
                    {
                        Log("[i] " + comName + "@" + baud + " no AT response", Color.Gray);
                        continue;
                    }
                    using (sp)
                    {
                        try
                        {
                            Log("[*] AT ADB enable via " + comName + " (Label: " +
                                (string.IsNullOrWhiteSpace(label) ? "-" : label) + ") @" + baud, Color.Cyan);
                            await ReadAtResponseAsync(sp, "ATE0", 2000);

                            bool anyOk = false;
                            foreach (string cmd in seqMain)
                            {
                                string r = await ReadAtResponseAsync(sp, cmd, 2500);
                                bool ok = AtIsSuccess(r);
                                if (ok) anyOk = true;
                                Log("    " + cmd + " → " + AtBrief(r), ok ? Color.LightGreen : Color.Gray);
                            }
                            foreach (string cmd in seqAlt)
                            {
                                string r = await ReadAtResponseAsync(sp, cmd, 2500);
                                bool ok = AtIsSuccess(r);
                                if (ok) anyOk = true;
                                Log("    " + cmd + " → " + AtBrief(r), ok ? Color.LightGreen : Color.Gray);
                            }
                            if (anyOk)
                            {
                                Log("[OK] AT ADB enable sequence sent — waiting for USB re-enumerate.", Color.LightGreen);
                                return true;
                            }
                            Log("[i] " + comName + "@" + baud + " AT no reply/blocked — trying different baud...", Color.Orange);
                        }
                        finally
                        {
                            try { sp.Close(); } catch { }
                        }
                    }
                }
            }
            Log("[!] AT ADB enable: all ports failed — try *#0808# (USB Settings → MTP+ADB) on the phone.", Color.Orange);
            return false;
        }

        // FRP မတိုင်ခင် device ဖတ် (Model/IMEI/SN — SamFw-style တို)
        private async Task TrySamsungAtReadInfoQuickAsync(List<(string Com, string Label)> ports)
        {
            int[] bauds = { 115200, 9600, 460800, 57600 };
            foreach (var (comName, label) in ports.Take(2))
            {
                foreach (int baud in bauds)
                {
                    (AtProbeOutcome kind, SerialPort sp, string _) =
                        await OpenAndProbeAtAsync(comName, baud, 3000, 250, 3000, quiet: true);
                    if (kind == AtProbeOutcome.OpenFail) break;
                    if (kind != AtProbeOutcome.Talks) continue;
                    using (sp)
                    {
                        try
                        {
                            await ReadAtResponseAsync(sp, "ATE0", 2000);
                            async Task<string> Q(string cmd) =>
                                AtPayload(await ReadAtResponseAsync(sp, cmd, 3500));

                            string model = await Q("AT+CGMM");
                            string sn = await Q("AT+CGSN");
                            if (string.IsNullOrWhiteSpace(sn)) sn = await Q("AT+SN");
                            if (!string.IsNullOrWhiteSpace(model))
                                Log("[i] Model: " + model.Trim(), Color.White);
                            if (!string.IsNullOrWhiteSpace(sn) && sn.Trim().Length >= 8)
                                Log("[i] SN: " + sn.Trim(), Color.White);
                            return;
                        }
                        finally
                        {
                            try { sp.Close(); } catch { }
                        }
                    }
                }
            }
            Log("[i] Quick info: cannot read via modem COM AT — continuing as MTP device.", Color.Gray);
        }

        // Samsung PnP devices ကို disable/enable → *#0*# ပြီးမှ USB stack ပြန် attach
        private async Task KickSamsungUsbAsync(List<SamsungUsbDevice> devs)
        {
            try
            {
                foreach (var d in devs.Where(x => !string.IsNullOrWhiteSpace(x.InstanceId)).Take(6))
                {
                    string id = d.InstanceId.Replace("'", "''");
                    string ps =
                        "$id = '" + id + "'; " +
                        "Disable-PnpDevice -InstanceId $id -Confirm:$false -ErrorAction SilentlyContinue; " +
                        "Start-Sleep -Milliseconds 400; " +
                        "Enable-PnpDevice -InstanceId $id -Confirm:$false -ErrorAction SilentlyContinue";
                    await ExecuteCommandQuickAsync("powershell.exe",
                        "-NoProfile -NonInteractive -Command \"" + ps.Replace("\"", "\\\"") + "\"");
                }
                await Task.Delay(1500);
                Log("[i] USB kick done — with *#0*# done, ADB may appear now.", Color.Cyan);
            }
            catch (Exception ex)
            {
                Log("[i] USB kick: " + ex.Message, Color.Gray);
            }
        }

        // Android 5–6 Samsung (*#0*# ADB မရ) — SideSync / OTG / Download နည်း guide
        private void ShowSamsungLegacyFrpGuide(string deviceName)
        {
            Log("[i] ===== LEGACY SAMSUNG FRP (no *#0*# ADB) =====", Color.FromArgb(0, 200, 255));
            Log("[i] Device: " + (string.IsNullOrWhiteSpace(deviceName) ? "Samsung" : deviceName), Color.White);
            Log("[i] 1) SideSync (most common for Android 5–6):", Color.White);
            Log("[i]    Install Samsung SideSync on PC → open → connect USB", Color.White);
            Log("[i]    If a phone popup appears, install/allow → enter browser/settings", Color.White);
            Log("[i]    → continue with Google account manager / FRP APK method", Color.White);
            Log("[i] 2) OTG: USB-OTG + flash drive (file manager APK) — enter setup wizard", Color.White);
            Log("[i] 3) Download mode (Vol- + Home + Power) → heimdall flash combination/firmware", Color.White);
            Log("[i] 4) Recovery wipe — wipes data but NOT FRP (Android 5.1+)", Color.White);
            Log("[i] ================================================", Color.FromArgb(0, 200, 255));

            bool sideSync = FindSideSyncPath(out string ssPath);
            var r = MessageBox.Show(
                "Samsung legacy FRP guide" + Environment.NewLine + Environment.NewLine +
                "*#0*# method: ADB interface မပေါ်ပါ (A3 2016 စသည်)។" + Environment.NewLine + Environment.NewLine +
                "SideSync: " + (sideSync ? "တွေ့ပါတယ် — ဖွင့်မလား?" : "PC မှာ မတွေ့ပါ (install လို)") + Environment.NewLine +
                (sideSync ? ssPath + Environment.NewLine + Environment.NewLine : "") +
                "SideSync ဖွင့်မလား?",
                "Samsung Legacy FRP", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (r == DialogResult.Yes && sideSync)
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = ssPath,
                        UseShellExecute = true
                    });
                    Log("[*] SideSync opened — keep USB connected and wait for the phone popup.", Color.Orange);
                }
                catch (Exception ex)
                {
                    Log("[!] SideSync start: " + ex.Message, Color.OrangeRed);
                }
            }
        }

        private static bool FindSideSyncPath(out string path)
        {
            path = "";
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf8 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string[] candidates =
            {
                Path.Combine(pf8, "Samsung", "SideSync", "SideSync.exe"),
                Path.Combine(pf, "Samsung", "SideSync", "SideSync.exe"),
                Path.Combine(pf8, "Samsung", "SideSync 4.x", "SideSync.exe"),
                @"C:\Program Files (x86)\Samsung\SideSync\SideSync.exe",
                @"C:\Program Files\Samsung\SideSync\SideSync.exe"
            };
            foreach (string c in candidates)
            {
                if (!string.IsNullOrWhiteSpace(c) && File.Exists(c)) { path = c; return true; }
            }
            return false;
        }

        // Samsung PnP set → stable signature (PID/service/iface) for change detect
        private static string SamsungUsbSignature(List<SamsungUsbDevice> devs)
        {
            if (devs == null || devs.Count == 0) return "";
            return string.Join(";", devs
                .Select(d => d.Pid + "|" + d.Service + "|" + (d.HasAdbInterface ? "A" : "-"))
                .OrderBy(s => s, StringComparer.Ordinal));
        }

        // Samsung MTP Factory Reset — ADB မစောင့်ဘဲ (တခြား tool တွေလို)
        // 1) ADB ရှိပြီးသားဆို ချက်ချင်း wipe
        // 2) မရှိရင် Samsung modem COM (AT) ကြိုးစား → မရရင် Recovery button guide (ADB မလို)
        private async Task RunSamsungMtpFactoryResetAsync()
        {
            if (MessageBox.Show(
                    "Samsung - MTP Factory Reset (no ADB wait)" + Environment.NewLine + Environment.NewLine +
                    "ADB စောင့်စရာ မလိုပါ — အောက်ပါနည်းဖြင့်:" + Environment.NewLine +
                    "  • ADB ဖွင့်ထားပြီးသားဆို → ချက်ချင်း wipe" + Environment.NewLine +
                    "  • မဟုတ်ရင် → Recovery mode (button) ဝင်ပြီး wipe လုပ်" + Environment.NewLine +
                    "     (Vol+ + Power — ADB / *#0*# မလို)" + Environment.NewLine + Environment.NewLine +
                    "• ဖုန်းထဲက data / ဓာတ်ပုံ / အက်ပ် အားလုံး ဖျက်ခံရမယ်" + Environment.NewLine +
                    "• Pattern/PIN နဲ့ Google account (FRP) ပါ ပျက်သွားနိုင်တယ်" + Environment.NewLine +
                    "• ဖုန်းပိုင်ရှင် ခွင့်ပြုချက် ရှိမှ လုပ်ပါ" + Environment.NewLine + Environment.NewLine +
                    "ဆက်လုပ်မလား?",
                    "Samsung MTP Factory Reset", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("   SAMSUNG - MTP FACTORY RESET (no ADB wait)", Color.White);
            Log("========================================================", Color.FromArgb(0, 180, 255));

            var mtpDevs = (await FindSamsungUsbDevicesAsync())
                .Where(d => d.IsMtpLike || d.Role == "MTP" || d.Role == "Connectivity").ToList();
            if (mtpDevs.Count == 0)
            {
                Log("[!] Samsung MTP device not found — connect USB and select File Transfer mode.", Color.OrangeRed);
                Log("========================================================", Color.FromArgb(0, 180, 255));
                return;
            }
            Log("[OK] MTP device: " + (mtpDevs[0].FriendlyName.Length > 0 ? mtpDevs[0].FriendlyName : mtpDevs[0].InstanceId), Color.LightGreen);

            // 1) ADB ရှိပြီးသားဆို — ချက်ချင်း wipe (စောင့်စရာမလို)
            string adbNow = await ExecuteCommandQuickAsync("adb.exe", "devices");
            if (adbNow.Contains("\tdevice"))
            {
                Log("[i] ADB device present — starting Factory reset immediately.", Color.Orange);
                await RunAdbFactoryResetCoreAsync();
                Log("========================================================", Color.FromArgb(0, 180, 255));
                return;
            }

            // 2) Samsung modem COM port (AT) — MTP နဲ့ ပေါ်တဲ့ Samsung Mobile USB Modem
            //    *2767*3855# master reset code ကို AT dial ကြိုးစား (အချို့ model မှာ အလုပ်လုပ်)
            bool atTried = await TrySamsungAtFactoryResetAsync();
            if (atTried)
            {
                Log("========================================================", Color.FromArgb(0, 180, 255));
                return;
            }

            // 3) ADB မရှိ / AT မရ — Recovery mode guide (ADB လုံးဝမလို)
            Log("[i] No ADB / modem AT — will wipe via Recovery mode button (no ADB needed).", Color.Orange);
            ShowSamsungRecoveryFactoryResetGuide();
            Log("========================================================", Color.FromArgb(0, 180, 255));
        }

        // Samsung Mobile USB Modem COM port ကနေ AT+CFUN / master reset code ကြိုးစား
        // တခြား tool တွေ (SamFw etc) က COM port ကို AT နဲ့ သုံး — ADB မစောင့်ရ
        private async Task<bool> TrySamsungAtFactoryResetAsync()
        {
            try
            {
                // Modem ဦးစားပေး — Connectivity Device V2 (AT မဟုတ်) ကို ရှောင်
                var candidates = await FindSamsungModemComPortsAsync();
                if (candidates.Count == 0)
                {
                    string[] ports = SerialPort.GetPortNames();
                    Log("[i] Samsung modem COM not found — will try " + ports.Length + " COM port(s).", Color.Gray);
                    foreach (string p in ports)
                        candidates.Add((p, "fallback " + p));
                }
                if (candidates.Count == 0) return false;

                foreach (var (comName, label) in candidates)
                {
                    Log("[*] Trying Samsung modem AT on " + comName + " (" + label + ") ...", Color.Cyan);
                    bool ok = await TryAtFactoryResetOnPortAsync(comName);
                    if (ok)
                    {
                        Log("[OK] AT commands sent — phone may reboot / wipe if supported.", Color.LightGreen);
                        Log("[i] If it doesn't enter, see the Recovery mode guide (below).", Color.Orange);
                        ShowSamsungRecoveryFactoryResetGuide();
                        return true;
                    }
                }

                Log("[i] AT not accepted on any Samsung COM (lockscreen/permission/wrong port?) — skip.", Color.Orange);
                return false;
            }
            catch (Exception ex)
            {
                Log("[i] AT path error: " + ex.Message, Color.Gray);
                return false;
            }
        }

        // Samsung serial/Modem PnP → COM list; Modem first, Connectivity last
        private async Task<List<(string Com, string Label)>> FindSamsungModemComPortsAsync()
        {
            var list = new List<(string Com, string Label)>();
            void Add(string com, string name)
            {
                com = (com ?? "").Trim().ToUpperInvariant();
                if (!com.StartsWith("COM")) com = "COM" + com;
                if (!Regex.IsMatch(com, @"^COM\d+$")) return;
                if (list.Any(x => string.Equals(x.Com, com, StringComparison.OrdinalIgnoreCase))) return;
                list.Add((com, name ?? com));
            }

            try
            {
                // Win32_PnPEntity.Name မှာ "SAMSUNG ... (COM7)" ပုံမှန်ပါ
                string ps =
                    "Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue | " +
                    "Where-Object { $_.Name -match 'SAMSUNG|Samsung' -and $_.Name -match 'COM\\d+' } | " +
                    "Sort-Object @{e={ if ($_.Name -match 'Modem') {0} elseif ($_.Name -match 'Diagnostics') {1} elseif ($_.Name -match 'Connectivity') {3} else {2} }}, Name | " +
                    "ForEach-Object { if ($_.Name -match '(COM\\d+)') { $Matches[1] + '|' + $_.Name } }";
                string res = await ExecuteCommandQuickAsync("powershell.exe",
                    "-NoProfile -NonInteractive -Command \"" + ps.Replace("\"", "\\\"") + "\"");
                foreach (string line in (res ?? "").Split('\n'))
                {
                    string t = line.Trim();
                    int bar = t.IndexOf('|');
                    if (bar <= 0) continue;
                    Add(t.Substring(0, bar), t.Substring(bar + 1).Trim());
                }
            }
            catch { }

            // Win32_SerialPort — FriendlyName မှာ COM မပါတဲ့ Samsung modem
            try
            {
                string ps =
                    "Get-CimInstance Win32_SerialPort -ErrorAction SilentlyContinue | " +
                    "Where-Object { $_.PNPDeviceID -like '*VID_04E8*' -or $_.Name -match 'SAMSUNG|Samsung' } | " +
                    "ForEach-Object { $_.DeviceID + '|' + $_.Name }";
                string res = await ExecuteCommandQuickAsync("powershell.exe",
                    "-NoProfile -NonInteractive -Command \"" + ps.Replace("\"", "\\\"") + "\"");
                foreach (string line in (res ?? "").Split('\n'))
                {
                    string t = line.Trim();
                    int bar = t.IndexOf('|');
                    if (bar <= 0) continue;
                    Add(t.Substring(0, bar), t.Substring(bar + 1).Trim());
                }
            }
            catch { }

            // Enum\USB\VID_04E8 → Device Parameters\PortName (Huawei နည်းတူ)
            try
            {
                using var usb = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
                if (usb != null)
                {
                    foreach (string vidName in usb.GetSubKeyNames())
                    {
                        if (!vidName.Contains("VID_04E8", StringComparison.OrdinalIgnoreCase)) continue;
                        using var vidKey = usb.OpenSubKey(vidName);
                        if (vidKey == null) continue;
                        foreach (string inst in vidKey.GetSubKeyNames())
                        {
                            using var instKey = vidKey.OpenSubKey(inst);
                            using var p = instKey?.OpenSubKey("Device Parameters");
                            if (p?.GetValue("PortName") is string com &&
                                com.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                                Add(com, "Samsung USB " + inst);
                        }
                    }
                }
            }
            catch { }

            // PnP FriendlyName (COMx)
            if (list.Count == 0)
            {
                try
                {
                    string ps2 =
                        "Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | " +
                        "Where-Object { $_.FriendlyName -match 'SAMSUNG|Samsung' } | " +
                        "Sort-Object @{e={ if ($_.FriendlyName -match 'Modem') {0} else {1} }} | " +
                        "ForEach-Object { if ($_.FriendlyName -match '\\((COM\\d+)\\)') { $Matches[1] + '|' + $_.FriendlyName } }";
                    string res2 = await ExecuteCommandQuickAsync("powershell.exe",
                        "-NoProfile -NonInteractive -Command \"" + ps2.Replace("\"", "\\\"") + "\"");
                    foreach (string line in (res2 ?? "").Split('\n'))
                    {
                        string t = line.Trim();
                        int bar = t.IndexOf('|');
                        if (bar <= 0) continue;
                        Add(t.Substring(0, bar), t.Substring(bar + 1).Trim());
                    }
                }
                catch { }
            }

            // Modem ဦးစားပေး
            return list
                .OrderBy(x => x.Label.IndexOf("Modem", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 :
                    x.Label.IndexOf("Connectivity", StringComparison.OrdinalIgnoreCase) >= 0 ? 2 : 1)
                .ThenBy(x => x.Com, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // တစ် port အတွက် AT probe → master reset (multi-baud, DTR/RTS)
        private async Task<bool> TryAtFactoryResetOnPortAsync(string comName)
        {
            int[] bauds = { 115200, 9600, 460800, 57600 };
            foreach (int baud in bauds)
            {
                (AtProbeOutcome kind, SerialPort sp, string at0) =
                    await OpenAndProbeAtAsync(comName, baud, 2000, 250, 2000, warmupCrlf: true);
                if (kind == AtProbeOutcome.OpenFail) return false;
                Log("[i] AT@" + baud + " resp: " + AtBrief(at0), Color.Gray);
                if (kind != AtProbeOutcome.Talks) continue;
                using (sp)
                {
                    try
                    {
                        await ReadAtResponseAsync(sp, "ATE0");

                        bool resetOk = false;

                        // Master reset USSD via ATD — *2767*3855# (voice dial; 4s — USSD ကြာနိုင်)
                        Log("[*] Sending master reset code *2767*3855# via AT ...", Color.Cyan);
                        string r1 = await ReadAtResponseAsync(sp, "ATD*2767*3855#;", 4000);
                        Log("[i] ATD; resp: " + AtBrief(r1), Color.Gray);
                        if (AtIsSuccess(r1)) resetOk = true;

                        // semicolon fail/error ဆို data-dial variant ထပ်စမ်း
                        if (!AtIsSuccess(r1))
                        {
                            r1 = await ReadAtResponseAsync(sp, "ATD*2767*3855#", 4000);
                            Log("[i] ATD resp: " + AtBrief(r1), Color.Gray);
                            if (AtIsSuccess(r1)) resetOk = true;
                        }

                        // CFUN full reset — 1,1 = full reset (lock/security က block နိုင်)
                        string r2 = await ReadAtResponseAsync(sp, "AT+CFUN=1,1", 3000);
                        Log("[i] AT+CFUN=1,1 resp: " + AtBrief(r2), Color.Gray);
                        if (AtIsSuccess(r2)) resetOk = true;
                        else if (AtHasError(r2) &&
                                 r2.IndexOf("NOT_ALLOWED", StringComparison.OrdinalIgnoreCase) >= 0)
                            Log("[i] CFUN blocked (NOT_ALLOWED) — unlock the screen, then retry / use Recovery.", Color.Orange);

                        if (!AtIsSuccess(r2))
                        {
                            r2 = await ReadAtResponseAsync(sp, "AT+CFUN=1", 3000);
                            Log("[i] AT+CFUN=1 resp: " + AtBrief(r2), Color.Gray);
                            if (AtIsSuccess(r2)) resetOk = true;
                        }

                        // handshake OK ကို success မထင် — reset cmd OK (error မပါ) သာ
                        if (resetOk)
                            return true;
                    }
                    finally
                    {
                        try { sp.Close(); } catch { }
                    }
                }
            }
            return false;
        }

        // AT serial prologue — info / quick-info / ADB-enable / factory-reset 4 နေရာမှာ
        // ထပ်နေတဲ့ open → settle → "AT" probe ကို ဒီမှာ ပေါင်းတယ်။
        //   OpenFail = open fail (caller break/return), Silent = မဖြောင်း (continue),
        //   Talks = sp open — caller က using(sp) + body ဆက်ပြီး dispose လုပ်တယ်။
        private enum AtProbeOutcome { OpenFail, Silent, Talks }

        private async Task<(AtProbeOutcome Outcome, SerialPort Sp, string Probe)> OpenAndProbeAtAsync(
            string comName, int baud, int readTimeoutMs, int settleMs, int probeTimeoutMs,
            bool warmupCrlf = false, bool quiet = false)
        {
            var sp = new SerialPort(comName, baud, Parity.None, 8, StopBits.One)
            {
                DtrEnable = true,
                RtsEnable = true,
                ReadTimeout = readTimeoutMs,
                WriteTimeout = 2000
            };
            try { sp.Open(); }
            catch (Exception ex)
            {
                if (!quiet) Log("[i] " + comName + "@" + baud + " open fail: " + ex.Message, Color.Gray);
                sp.Dispose();
                return (AtProbeOutcome.OpenFail, null, null);
            }
            try
            {
                await Task.Delay(settleMs);
                sp.DiscardInBuffer();
                sp.DiscardOutBuffer();
                if (warmupCrlf)
                {
                    sp.Write("\r\n");
                    await Task.Delay(100);
                    sp.DiscardInBuffer();
                }
                string at0 = await ReadAtResponseAsync(sp, "AT", probeTimeoutMs);
                bool talks = AtHasOk(at0) || AtHasError(at0) ||
                    (at0 != null && (at0.Contains("AT") || at0.Contains("+")));
                if (!talks)
                {
                    sp.Dispose();
                    return (AtProbeOutcome.Silent, null, at0);
                }
                return (AtProbeOutcome.Talks, sp, at0);
            }
            catch
            {
                sp.Dispose();
                throw;
            }
        }

        // AT response တွင် result line "OK" ရှိမှန်း (echo/line-break ကို OK မထင်)
        private static bool AtHasOk(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (string raw in s.Split('\n'))
            {
                string t = raw.Trim('\r', ' ', '\t', '\0');
                if (t.Equals("OK", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool AtHasError(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            return s.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   s.IndexOf("+CME", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   s.IndexOf("+CMS", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // OK ရှိ + error မပါ မှ success — Samsung က "CME ERROR" + "OK" နောက်ဆက်ပို့တတ်
        private static bool AtIsSuccess(string s)
        {
            if (AtHasError(s)) return false;
            return AtHasOk(s);
        }

        private static string AtBrief(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "(none)";

            // error line ကို ဦးစားပေးပြ
            foreach (string raw in s.Split('\n'))
            {
                string t = raw.Trim('\r', ' ', '\t', '\0');
                if (t.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    t.IndexOf("+CME", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    t.IndexOf("+CMS", StringComparison.OrdinalIgnoreCase) >= 0)
                    return t;
            }

            foreach (string raw in s.Split('\n'))
            {
                string t = raw.Trim('\r', ' ', '\t', '\0');
                if (t.Equals("OK", StringComparison.OrdinalIgnoreCase))
                    return "OK";
            }

            string one = FirstLine(s);
            if (AtIsSuccess(s) && !one.Trim().Equals("OK", StringComparison.OrdinalIgnoreCase))
                one += " [OK]";
            return one;
        }

        // AT command → response (line-based; timeoutMs default 2s)
        private async Task<string> ReadAtResponseAsync(SerialPort sp, string cmd, int timeoutMs = 2000)
        {
            try
            {
                sp.DiscardInBuffer();
                sp.Write(cmd + "\r\n");
            }
            catch (Exception ex) { return "WRITE_FAIL:" + ex.Message; }

            var sb = new System.Text.StringBuilder();
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (sp.BytesToRead > 0)
                    {
                        sb.Append(sp.ReadExisting());
                        string s = sb.ToString();
                        if (AtHasOk(s) || AtHasError(s))
                            break;
                    }
                    else
                    {
                        await Task.Delay(40);
                    }
                }
                catch { break; }
            }
            await Task.Delay(50);
            try
            {
                if (sp.BytesToRead > 0)
                    sb.Append(sp.ReadExisting());
            }
            catch { }
            return sb.ToString();
        }

        // ADB မလို — Recovery button combo နဲ့ manual wipe guide
        private void ShowSamsungRecoveryFactoryResetGuide()
        {
            Log("[i] ===== RECOVERY MODE FACTORY RESET (no ADB) =====", Color.FromArgb(0, 200, 255));
            Log("[i] 1) Power off the phone", Color.White);
            Log("[i] 2) Keep holding Vol+ and press Power — release when Samsung logo appears", Color.White);
            Log("[i]    (If there is a Home button: Vol+ + Home + Power)", Color.White);
            Log("[i] 3) Once in Recovery menu:", Color.White);
            Log("[i]      • Select Wipe data / Factory reset", Color.White);
            Log("[i]      • Select Yes — delete all user data", Color.White);
            Log("[i]      • Select Reboot system now", Color.White);
            Log("[i] 4) Vol+ / Vol- = navigate, Power = confirm", Color.White);
            Log("[i] ======================================================", Color.FromArgb(0, 200, 255));

            MessageBox.Show(
                "Recovery Mode Factory Reset (ADB မလို)" + Environment.NewLine + Environment.NewLine +
                "1) ဖုန်း Power off ပါ" + Environment.NewLine + Environment.NewLine +
                "2) Vol+ ကို ဆက်နှိပ်ထားပြီး Power နှိပ်" + Environment.NewLine +
                "   Samsung logo ပေါ်မှ လွှတ်ပါ" + Environment.NewLine +
                "   (Home ရှိရင်: Vol+ + Home + Power)" + Environment.NewLine + Environment.NewLine +
                "3) Recovery menu ထဲမှာ:" + Environment.NewLine +
                "   • Wipe data / Factory reset" + Environment.NewLine +
                "   • Yes — delete all user data" + Environment.NewLine +
                "   • Reboot system now" + Environment.NewLine + Environment.NewLine +
                "Vol+ / Vol- = ရွေး, Power = OK",
                "Samsung Recovery Factory Reset", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ================= Samsung Download (Odin) Mode Info =================
        // heimdall detect + USB PnP (VID_04E8 + download PID) ကနေ mode/serial ဖတ်တယ်။
        // ADB / MTP မလို — Vol- + Home + Power (သို့) Vol+ + Vol- + USB နဲ့ Download mode ဝင်ထားရင် ရတယ်။
        private async Task RunSamsungDownloadInfoAsync()
        {
            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("   SAMSUNG - DOWNLOAD (ODIN) MODE INFO", Color.White);
            Log("========================================================", Color.FromArgb(0, 180, 255));

            // ၁) heimdall tool ရှိ/မရှိ + version
            string heimdall = FindHeimdall();
            string ver = await ExecuteCommandQuickAsync(heimdall, "version");
            if (string.IsNullOrWhiteSpace(ver) || ver.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                ver.Contains("is not recognized", StringComparison.OrdinalIgnoreCase))
            {
                Log("[!] heimdall.exe not found — Download mode detect/PIT/flash unavailable.", Color.OrangeRed);
                Log("    Check that bundled-tools\\samsung\\heimdall.exe exists.", Color.Orange);
            }
            else
            {
                Log("  • Heimdall      : " + ver.Trim().Split('\n')[0].Trim(), Color.White);
            }

            // ၂) heimdall detect — Download mode မှာ device ရှိ/မရှိ
            string detect = await ExecuteCommandQuickAsync(heimdall, "detect");
            string detectLow = (detect ?? "").ToLowerInvariant();
            bool heimdallOk = detectLow.Contains("device detected") || detectLow.Contains("found");
            if (heimdallOk)
                Log("  • Heimdall      : Device detected (Download mode) ✓", Color.LightGreen);
            else
                Log("  • Heimdall      : not detected — " + FirstLine(detect), Color.Orange);

            // ၃) USB PnP — Samsung VID + Download-mode PID
            //     classic: 685D (Odin), alt: 6866/6877/6860 sometimes show with cable in DL
            var allSam = await FindSamsungUsbDevicesAsync();
            var dlPids = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "685d", "6866", "685c", "685e", "6a1a", "6a1b" };
            var dlDevs = allSam.Where(d => dlPids.Contains(d.Pid) ||
                d.FriendlyName.Contains("Download", StringComparison.OrdinalIgnoreCase) ||
                d.FriendlyName.Contains("Odin", StringComparison.OrdinalIgnoreCase) ||
                d.Service.Contains("WinUSB", StringComparison.OrdinalIgnoreCase)).ToList();

            if (dlDevs.Count == 0 && allSam.Count > 0)
            {
                // Samsung USB ရှိပေမယ့် known DL PID မဟုတ် — ရှိတာအားလုံး ပြ
                Log("  • Samsung USB   : (download PID not found — devices present):", Color.Orange);
                foreach (var d in allSam.Take(4))
                    Log("      PID " + d.Pid + " | " + (string.IsNullOrWhiteSpace(d.FriendlyName) ? d.Service : d.FriendlyName), Color.Gray);
            }
            else if (dlDevs.Count > 0)
            {
                foreach (var d in dlDevs)
                {
                    Log("  • PID           : 04E8:" + d.Pid + "  ✓ Download-mode USB", Color.LightGreen);
                    Log("  • Friendly      : " + (string.IsNullOrWhiteSpace(d.FriendlyName) ? "(n/a)" : d.FriendlyName), Color.White);
                    Log("  • Service       : " + (string.IsNullOrWhiteSpace(d.Service) ? "(n/a)" : d.Service), Color.White);
                    // InstanceId ထဲမှာ USB serial များသောအားဖြင့် ပါတယ်: ...VID_04E8&PID_685D\SERIAL
                    Match sm = Regex.Match(d.InstanceId, @"&PID_[0-9A-Fa-f]{4}\\(.+)$", RegexOptions.IgnoreCase);
                    if (sm.Success)
                        Log("  • USB Serial    : " + sm.Groups[1].Value.Trim('\\'), Color.White);
                    Log("  • InstanceId    : " + d.InstanceId, Color.Gray);
                }
            }
            else
            {
                Log("[!] Samsung USB device not found — not in Download mode / cable not connected.", Color.OrangeRed);
                Log("    Vol- + Home + Power  or  Vol+ + Vol- + plug USB → Warning screen → press Vol+ to enter.", Color.Orange);
            }

            // ၄) အခြေအနေ summary
            if (heimdallOk || dlDevs.Count > 0)
            {
                Log("[OK] Download mode — ready for PIT download / Heimdall flash.", Color.LightGreen);
                Log("[i] Use Download PIT to view partitions, Firmware FLASH block to flash.", Color.Orange);
            }
            else
            {
                Log("[i] Not in Download mode yet — put the phone in DL mode and press again.", Color.Orange);
            }
            Log("========================================================", Color.FromArgb(0, 180, 255));
        }

        private static string FirstLine(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "(no output)";
            string line = s.Trim().Split('\n')[0].Trim();
            return line.Length > 80 ? line.Substring(0, 80) + "..." : line;
        }

        // heimdall detect: exit!=0 ဆိုရင်လည်း stderr (e.g. "Failed to detect...") ပြန်ဖို့ — RunQuickAsync မသုံး
        private static async Task<string> RunHeimdallDetectAsync(string heimdall)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = heimdall,
                    Arguments = "detect",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return "(heimdall start failed)";
                var stdout = p.StandardOutput.ReadToEndAsync();
                var stderr = p.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(8000);
                try
                {
                    await Task.WhenAll(p.WaitForExitAsync(timeout.Token), stdout, stderr).WaitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    return "(detect timeout)";
                }
                string outText = (await stdout).Trim();
                string errText = (await stderr).Trim();
                if (!string.IsNullOrWhiteSpace(outText)) return outText;
                if (!string.IsNullOrWhiteSpace(errText))
                    return p.ExitCode == 0 ? errText : "exit " + p.ExitCode + ": " + errText;
                return "(no output, exit " + p.ExitCode + ")";
            }
            catch (Exception ex) { return ex.Message; }
        }

        // ================= Samsung SoftBrick Fix (Odin 0x64/0x01) =================
        // Download mode error screen → flash-count reset → normal Download mode (firmware မလို)
        private async Task RunSamsungSoftBrickAsync()
        {
            if (isTaskRunning)
            {
                Log("[!] Another operation is still running - wait for it to finish or press STOP.", Color.OrangeRed);
                return;
            }

            isTaskRunning = true;
            stopRequested = false;
            try
            {
                Log("========================================================", Color.FromArgb(0, 180, 255));
                Log("   SAMSUNG - SOFTBRICK FIX (Odin flash-count reset)", Color.White);
                Log("========================================================", Color.FromArgb(0, 180, 255));
                SetProgress(5, "Detecting Download mode");

                // Optional: heimdall detect for clearer status (non-blocking fail).
                // RunQuickAsync returns "" on exit!=0; heimdall writes errors to stderr — capture both.
                try
                {
                    string heimdall = FindHeimdall();
                    string detect = await RunHeimdallDetectAsync(heimdall);
                    string dlow = (detect ?? "").ToLowerInvariant();
                    if (dlow.Contains("device detected") || dlow.Contains("found"))
                        Log("  • Heimdall      : Device detected (Download mode) ✓", Color.LightGreen);
                    else
                        Log("  • Heimdall      : " + FirstLine(detect), Color.Orange);
                }
                catch { /* heimdall optional */ }

                if (SamsungSoftBrick.FindDownloadModeDevice(out int vid, out int pid) == null)
                {
                    Log("[!] Samsung Download mode USB not found (VID_04E8).", Color.OrangeRed);
                    Log("    Put the phone in Download mode and replug USB:", Color.Orange);
                    Log("    Vol- + Vol- + USB (newer)  or  Vol- + Home + Power → Warning → Vol+", Color.Orange);
                    Log("    Samsung USB driver / Zadig WinUSB must be installed.", Color.Orange);
                    SetProgress(0, "Failed");
                    return;
                }

                Log($"  • USB           : 04E8:{pid:X4}", Color.White);
                SetProgress(20, "Odin handshake");
                Log("[*] Odin protocol: ODIN/LOKE → BeginSession → ResetFlashCount → End → Reboot", Color.Cyan);

                string err = "";
                bool ok = await Task.Run(() =>
                    SamsungSoftBrick.ResetFlashCount(
                        msg =>
                        {
                            Color c = msg.StartsWith("[OK]", StringComparison.Ordinal) ? Color.LightGreen
                                    : msg.StartsWith("[!]", StringComparison.Ordinal) ? Color.Orange
                                    : Color.White;
                            Log(msg, c);
                        },
                        rebootToOdin: true,
                        out err));

                if (ok)
                {
                    SetProgress(100, "SoftBrick fix OK");
                    Log("[OK] SoftBrick Fix done — flash count reset; Download mode can be entered again.", Color.LightGreen);
                    Log("[i] To re-flash firmware after the error screen is gone, use the Firmware FLASH block.", Color.Orange);
                }
                else
                {
                    SetProgress(0, "Failed");
                    Log("[FAIL] " + (string.IsNullOrWhiteSpace(err) ? "SoftBrick Fix failed" : err), Color.Red);
                    Log("    Close heimdall/Odin, change cable, re-enter Download mode and retry.", Color.Orange);
                }
                Log("========================================================", Color.FromArgb(0, 180, 255));
            }
            catch (Exception ex)
            {
                SetProgress(0, "Failed");
                Log("[FAIL] SoftBrick Fix: " + ex.Message, Color.Red);
            }
            finally
            {
                isTaskRunning = false;
            }
        }

        private async void BtnSamSideload_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog ofd = new OpenFileDialog
            {
                Filter = "Update ZIP (*.zip)|*.zip|All Files (*.*)|*.*",
                Title = "Sideload လုပ်မယ့် ZIP ကို ရွေးပါ"
            })
            {
                if (ofd.ShowDialog() != DialogResult.OK) return;
                Log("[*] On the phone, select Recovery -> 'Apply update from ADB'.", Color.Orange);
                await ExecuteCommandCleanAsync("adb.exe", "sideload \"" + ofd.FileName + "\"", "Sideload " + Path.GetFileName(ofd.FileName));
            }
        }

    }
}
