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
        // ================= FIRMWARE FLASH (Qualcomm / Samsung / SPD) =================
        // MTK panel နဲ့ တူတူပုံစံ — folder/file ရွေး → START FLASH
        private string FindRawprogram(string dir)
        {
            try
            {
                string[] files = Directory.GetFiles(dir, "rawprogram*.xml").Where(f => !Path.GetFileName(f).Equals("rawprogram_skip_userdata.xml", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (files.Length > 0) return files[0];

                // "xxx_rawprogram.xml" / "_selected_rawprogram.xml" စတဲ့ ပုံစံတွေလည် ရှာ
                files = Directory.GetFiles(dir, "*rawprogram*.xml").Where(f => !Path.GetFileName(f).Equals("rawprogram_skip_userdata.xml", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (files.Length > 0) return files[0];
            }
            catch { }
            return "";
        }

        // rawprogram0.xml ကို ဖတ်ပြီး partition list ကို ဖြည့် (label / size_in_KB / start_sector)
        private void LoadRawprogramXml(string xmlPath)
        {
            try
            {
                ClearPartitionData();
                System.Xml.Linq.XDocument doc = System.Xml.Linq.XDocument.Load(xmlPath);
                int count = 0;

                foreach (System.Xml.Linq.XElement el in doc.Descendants("program"))
                {
                    string label = (string)el.Attribute("label");
                    if (string.IsNullOrEmpty(label)) continue;

                    string sizeKb = (string)el.Attribute("size_in_KB");
                    string startSector = (string)el.Attribute("start_sector");

                    string lengthHex = "0x0";
                    if (!string.IsNullOrEmpty(sizeKb) &&
                        double.TryParse(sizeKb, NumberStyles.Float, CultureInfo.InvariantCulture, out double kb) && kb > 0)
                    {
                        lengthHex = "0x" + ((long)(kb * 1024)).ToString("x");
                    }

                    string offset = "-";
                    if (!string.IsNullOrEmpty(startSector) && long.TryParse(startSector, out long sec))
                        offset = "0x" + (sec * 512).ToString("x");

                    AddPartitionRow(label, offset, lengthHex);
                    count++;
                }

                Log("  • Partitions : " + count + " rows (read from rawprogram XML)", Color.LightGreen);
                if (count == 0)
                    Log("[!] No <program> entries found in the XML.", Color.Orange);
            }
            catch (Exception ex)
            {
                Log("[!] Could not read the rawprogram XML: " + ex.Message, Color.Orange);
            }
        }

        // XML မရှိရင် folder ထဲက .img/.bin ဖိုင်နာမည်တွေကနေ partition list ဖြည့်
        private void LoadFirmwareFolderImages(string dir)
        {
            try
            {
                ClearPartitionData();
                int count = 0;
                string[] files = Directory.GetFiles(dir, "*.img");
                string[] bins = Directory.GetFiles(dir, "*.bin");
                foreach (string f in files) { AddPartitionRow(Path.GetFileNameWithoutExtension(f), "-", "0x" + new FileInfo(f).Length.ToString("x")); count++; }
                foreach (string f in bins) { AddPartitionRow(Path.GetFileNameWithoutExtension(f), "-", "0x" + new FileInfo(f).Length.ToString("x")); count++; }

                Log("  • Partitions : " + count + " rows (from .img/.bin file names)", Color.LightGreen);
            }
            catch (Exception ex)
            {
                Log("[!] Could not read the folder: " + ex.Message, Color.Orange);
            }
        }

        private string FindFileInToolFolders(params string[] names)
        {
            foreach (string name in names)
            {
                string file = ShopServices.FindTool(Application.StartupPath, name);
                if (!string.IsNullOrEmpty(file)) return file;
            }
            return "";
        }

        // flash log ထဲက file name ရဲ့ disk size — firmware folder (fh_loader --search_path) ကနေ
        // ရှာပြီး " (X MB)" suffix ပေးတယ် (split parts တွေလည်း အတူတူ folder မှာ ရှိလို့ တွေ့တယ်)
        private long FlashFileSize(string fileName)
        {
            try
            {
                if (flashFileSizes.TryGetValue(fileName, out long cached)) return cached;
                if (string.IsNullOrEmpty(qcFirmwareFolder) || !Directory.Exists(qcFirmwareFolder)) return 0;
                string full = Path.Combine(qcFirmwareFolder, fileName);
                if (!File.Exists(full))
                    full = Directory.EnumerateFiles(qcFirmwareFolder, Path.GetFileName(fileName), SearchOption.AllDirectories)
                        .FirstOrDefault() ?? "";
                if (full.Length == 0 || !File.Exists(full)) return 0;
                long bytes = new FileInfo(full).Length;
                flashFileSizes[fileName] = bytes;
                return bytes;
            }
            catch { return 0; }
        }

        private string FlashSizeSuffix(string fileName)
        {
            long bytes = FlashFileSize(fileName);
            return bytes > 0 ? FormatFlashSize(bytes) : "";
        }

        // flash XML (split/filter ပြီးသား နောက်ဆုံး XML) ကနေ file → partition label map တည်ဆောက်
        // fh_loader က XML order အတိုင်း handleProgram ခေါ်လို့ queue နဲ့ တွဲပြီး MST style ပြမယ်
        private void BuildFlashPartTargets(string xmlPath)
        {
            lock (flashPartLock) flashPartTargets.Clear();
            try
            {
                var doc = System.Xml.Linq.XDocument.Load(xmlPath);
                foreach (var program in doc.Descendants("program"))
                {
                    string fileName = Path.GetFileName((string)program.Attribute("filename") ?? "");
                    string label = (string)program.Attribute("label") ?? "";
                    if (fileName.Length == 0 || label.Length == 0) continue;
                    if (!flashPartTargets.TryGetValue(fileName, out Queue<string> queue))
                    {
                        queue = new Queue<string>();
                        flashPartTargets[fileName] = queue;
                    }
                    queue.Enqueue(label);
                }
            }
            catch { /* map မရရင် file name ပဲ ပြတဲ့ format နဲ့ ဆက်သွား */ }
        }

        // XML ထဲက label အစဉ်အတိုင်း တစ်ခုချင်း ထုတ်ပေးတယ် — မကျန်တော့ရင် "" (fallback)
        private string DequeueFlashTarget(string fileName)
        {
            lock (flashPartLock)
            {
                if (flashPartTargets.TryGetValue(fileName, out Queue<string> queue) && queue.Count > 0)
                    return queue.Dequeue();
            }
            return "";
        }

        private static string FormatFlashSize(long bytes)
        {
            return bytes >= 1048576
                ? $" ({bytes / 1048576.0:0.##} MB)"
                : $" ({bytes / 1024.0:0.##} KB)";
        }

        private string ResolveToolPath(string fileName)
        {
            if (Path.IsPathRooted(fileName) || fileName.Contains(Path.DirectorySeparatorChar) ||
                fileName.Contains(Path.AltDirectorySeparatorChar)) return fileName;
            string logicalName = fileName == "python" ? "python.exe" : fileName == "heimdall" ? "heimdall.exe" : fileName;
            string local = FindFileInToolFolders(logicalName);
            if (!string.IsNullOrEmpty(local)) return local;
            if (shopToolNames.Contains(logicalName, StringComparer.OrdinalIgnoreCase))
                throw new FileNotFoundException("Configure " + logicalName + " in Setup / Backups > Tool setup.");
            foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(folder)) continue;
                    string candidate = Path.Combine(folder.Trim().Trim('"'), fileName);
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
                catch (ArgumentException) { }
            }
            return fileName;
        }

        // ---- Qualcomm: edl.py နဲ့ အစစ် flash ----
        private void BtnQcPickFirmware_Click(object sender, EventArgs e)
        {
            using (FolderBrowserDialog fbd = new FolderBrowserDialog
            {
                Description = "Qualcomm firmware folder ရွေးပါ (rawprogram XML သို့ .img ဖိုင်များ)"
            })
            {
                if (fbd.ShowDialog() != DialogResult.OK) return;
                qcFirmwareFolder = fbd.SelectedPath;
                flashFileSizes.Clear();   // folder ပြောင်းရင် size cache အဟောင်း မကျန်စေရ
                lock (flashPartLock) flashPartTargets.Clear();
                txtQcFirmware.Text = qcFirmwareFolder;
                SaveSettings();

                string xml = FindRawprogram(qcFirmwareFolder);
                int imgs = 0;
                try { imgs = Directory.GetFiles(qcFirmwareFolder, "*.img").Length + Directory.GetFiles(qcFirmwareFolder, "*.bin").Length; } catch { }

                // Partition list ကို ဖြည့် — XML ရှိရင် XML အတိုင်း၊ မရှိရင် ဖိုင်နာမည်တွေကနေ
                if (!string.IsNullOrEmpty(xml)) LoadRawprogramXml(xml);
                else LoadFirmwareFolderImages(qcFirmwareFolder);

                Log("========================================================", Color.FromArgb(0, 180, 255));
                Log("   QUALCOMM FIRMWARE LOADED", Color.White);
                Log("========================================================", Color.FromArgb(0, 180, 255));
                Log("  • Folder : " + qcFirmwareFolder, Color.Cyan);
                Log("  • XML    : " + (string.IsNullOrEmpty(xml) ? "(none - will use folder-based wl)" : Path.GetFileName(xml)), Color.White);
                Log("  • Images : " + imgs + " files (.img/.bin)", Color.White);
                Log("[*] Pressing START FLASH will " + (string.IsNullOrEmpty(xml) ? "write partitions matching the file names in the folder." : "write partitions as listed in the rawprogram XML."), Color.Orange);
            }
        }

        private async void BtnQcStartFlash_Click(object sender, EventArgs e)
        {
            await RunFlashWorkflowAsync(BtnQcStartFlashAsync);
        }

        private async Task BtnQcStartFlashAsync()
        {
            if (string.IsNullOrEmpty(qcFirmwareFolder) || !Directory.Exists(qcFirmwareFolder))
            {
                Log("[!] No Qualcomm firmware folder selected - pick one first.", Color.OrangeRed);
                return;
            }

            string port = cmbPorts.SelectedItem?.ToString();
            if (string.IsNullOrEmpty(port) || !port.StartsWith("COM"))
            {
                Log("[!] No COM port selected - pick the Qualcomm 9008 port first.", Color.OrangeRed);
                return;
            }

            string xmlFile = FindRawprogram(qcFirmwareFolder);
            if (!File.Exists(xmlFile)) throw new FileNotFoundException("Rawprogram XML not found.");
            string memoryType = detectedStorageKind == "UFS" ? "ufs" : "emmc";
            string patchFile = Directory.GetFiles(qcFirmwareFolder, "patch*.xml").Select(Path.GetFileName).FirstOrDefault();
            flashBytesDone = 0;
            flashCurrentFileSize = 0;
            flashSpeedWatch = null;

            Log("Operation : QUALCOMM FLASH PARTITION\r\n", Color.FromArgb(0, 200, 255));
            Log($" Waiting Qualcomm 9008 Port... {port}", Color.White);
            Log(" Connecting to SAHARA Protocol... Ok", Color.White);
            Log(" Identifying Device Mode... Ok ( SAHARA MODE )", Color.White);

            // ၁။ Sahara Handshake ကို အရင်ဆုံး မဖြစ်မနေ လုပ်ရပါမည်
            string loaderFile = !string.IsNullOrEmpty(edlLoaderPath) ? edlLoaderPath : Path.Combine(qcFirmwareFolder, "prog_firehose_lite.elf");
            Log(" Sending Firehose Programmer Loader... Ok", Color.White);

            bool saharaOk = await ExecuteCommandCleanAsync("QSaharaServer.exe", $"-p \\\\.\\{port} -s 13:\"{loaderFile}\"", "Sahara", false, false);
            if (!saharaOk)
            {
                Log(" [FAIL] Sahara Handshake Failed!", Color.Red);
                return;
            }

            Log(" Connecting to firehose mode... Ok", Color.White);
            await Task.Delay(1000);
            if (stopRequested) return;

            // ၂။ EDL Auth Bypass (MiFlash NonAuth လို) — auth device တွေမှာ fh_loader မတိုင်မီ sig ပို့
            // Sahara loader တင်ပြီးမို့ loaderPath ပြန်မတင် — firehose session ပေါ်မှာ sig သာ
            {
                Log(" Bypassing EDL authentication (sig)...", Color.Orange);
                SetProgress(35, "Auth Bypass");
                var authRes = await EdlAuth.BypassAsync(port, null, msg =>
                {
                    if (msg.StartsWith("[+]")) Log(" " + msg, Color.LightGreen);
                    else if (msg.StartsWith("[!]")) Log(" " + msg, Color.Orange);
                    else if (msg.StartsWith("[*]") || msg.StartsWith("    ")) Log(" " + msg, Color.White);
                }, default, string.IsNullOrEmpty(edlSigPath) ? null : edlSigPath);
                if (authRes.Ok)
                {
                    qcAuthed = true;
                    qcAuthedPort = port;
                    Log(" Auth Bypass... Ok" + (string.IsNullOrEmpty(authRes.TargetName) ? "" : " (TargetName=" + authRes.TargetName + ")"), Color.LightGreen);
                }
                else
                {
                    qcAuthed = false;
                    qcAuthedPort = "";
                    // Auth မလိုတဲ့ device တွေမှာ fail ဖြစ်နိုင် — hard stop မလုပ်ဘဲ warning သာ
                    Log(" [!] Auth bypass: " + authRes.Message + " — continuing (device may not require auth).", Color.Orange);
                }
                if (stopRequested) return;
                await Task.Delay(300);
            }

            // ၃။ [FEATURE 1] Flash မတိုင်မီ EFS Backup အမှန်တကယ် ယူခြင်း (Sahara အောင်မြင်ပြီးမှ)
            if (chkQcBackupEfsFirst.Checked)
            {
                string efsDir = Path.Combine(qcFirmwareFolder, "efs_backup_" + Guid.NewGuid().ToString("N"));
                try { Directory.CreateDirectory(efsDir); }
                catch (Exception ex) { Log("[!] EFS backup dir create failed: " + ex.Message, Color.Red); }
                Log($"[*] [Option] Backing up EFS (persist/modemst) to {Path.GetFileName(efsDir)}...", Color.Orange);

                // fh_loader ဖြင့် persist, modemst1, modemst2 များကို ဖတ်ယူ Backup ပြုလုပ်ခြင်း
                var backupFiles = ReviewSafety.PrepareEfsBackup(xmlFile, efsDir);
                bool backupOk = await ExecuteCommandCleanAsync("fh_loader.exe", $"--port=\\\\.\\{port} --search_path=\"{efsDir}\" --sendxml=efs_read.xml --convertprogram2read --memoryname={memoryType} --noprompt", "EFS Backup", false, false);
                if (!backupOk || !backupFiles.All(file => ReviewSafety.HasBackup(file.Key, file.Value)))
                {
                    workflowFailed = true;
                    Log("[FAIL] EFS backup is incomplete; flash cancelled.", Color.Red);
                    return;
                }
                Log(" Backing up EFS... Ok", Color.LightGreen);
            }

            // ၃။ [FEATURE 2] Skip Userdata - XML ထဲမှ userdata လိုင်းများကို ဖယ်ထုတ်ခြင်း
            string finalXmlFile = xmlFile;
            if (chkQcSkipUserdata.Checked)
            {
                Log("[*] [Option] Preparing XML (Skipping Userdata Partitions)...", Color.Orange);
                try
                {
                    string tempXmlPath = Path.Combine(qcFirmwareFolder, "rawprogram_skip_userdata.xml");
                    ReviewSafety.FilterUserdata(xmlFile, tempXmlPath);
                    finalXmlFile = tempXmlPath;
                    Log(" Skip Userdata Filter Applied... Ok", Color.LightGreen);
                }
                catch (Exception ex)
                {
                    workflowFailed = true;
                    Log("[FAIL] XML filter failed; flash cancelled: " + ex.Message, Color.Red);
                    return;
                }
            }

            Log(" Loading Partition Table... Ok", Color.White);

            // Expand complete sparse containers first, then split the resulting raw ranges.
            string flashXmlFile = Path.Combine(qcFirmwareFolder,
                Path.GetFileNameWithoutExtension(finalXmlFile) + "_expanded_pmk.xml");
            try
            {
                File.Copy(finalXmlFile, flashXmlFile, true);
                await Task.Run(() => ReviewSafety.ConvertSparseImages(flashXmlFile, qcFirmwareFolder,
                    msg => Log(msg, Color.Orange)));
                string splitXmlPath = Path.Combine(qcFirmwareFolder,
                    Path.GetFileNameWithoutExtension(finalXmlFile) + ReviewSafety.SplitXmlSuffix);
                await Task.Run(() => ReviewSafety.SplitOversizedImages(flashXmlFile, qcFirmwareFolder, splitXmlPath,
                    msg => Log(msg, Color.Orange), (_, _) => !stopRequested));
                flashXmlFile = splitXmlPath;
                if (stopRequested) return;
            }
            catch (Exception ex)
            {
                workflowFailed = true;
                Log("[FAIL] Firmware preparation stopped; nothing flashed: " + ex.Message, Color.Red);
                return;
            }

            // ၄။ Main Flash Execution
            await Task.Run(() => BuildFlashPartTargets(flashXmlFile));   // file → partition map (MST style log)
            flashSpeedWatch = Stopwatch.StartNew();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string sendXmlParam = Path.GetFileName(flashXmlFile) + (!string.IsNullOrEmpty(patchFile) ? $",{patchFile}" : "");

            // Flash ပြီးမှ FRP Reset/Reboot လုပ်မည်ဖြစ်၍ reset flag ကို နောက်ဆုံးမှ သီးသန့်ပို့ပါမည်
            string fhArgs = $"--port=\\\\.\\{port} --sendxml=\"{sendXmlParam}\" --search_path=\"{qcFirmwareFolder}\" --noprompt --showpercentagecomplete --zlpawarehost=1 --memoryname={memoryType}";

            bool flashOk = await ExecuteCommandCleanAsync("fh_loader.exe", fhArgs, "Firehose Flash", false, false);
            // flash fail → workflowFailed ချိုး → auto-reboot မလုပ်ဘဲ device က EDL မှာပဲ ကျန်ခဲ့ပြီး ချက်ချင်း retry လုပ်လို့ရ
            if (!flashOk) workflowFailed = true;

            // ၅။ [FEATURE 3] Reset FRP after Flash - Flash ပြီးချိန်တွင် FRP Erase ပြုလုပ်ခြင်း
            if (flashOk && chkQcResetFrpAfter.Checked)
            {
                Log(" Resetting Factory Reset Protection Lock (FRP)...", Color.Orange);
                // Firehose ဖြင့် frp နှင့် config partition ကို Erase သို့မဟုတ် format လုပ်ခြင်း
                if (!await ExecuteCommandCleanAsync("fh_loader.exe", $"--port=\\\\.\\{port} --erase=frp --memoryname={memoryType} --noprompt", "Erase FRP", false, false)) return;
                if (!await ExecuteCommandCleanAsync("fh_loader.exe", $"--port=\\\\.\\{port} --erase=config --memoryname={memoryType} --noprompt", "Erase Config", false, false)) return;
                Log(" Resetting Factory Reset Protection Lock... Ok", Color.LightGreen);
            }

            // Auto reboot — RunFlashWorkflowAsync က workflow ပြီးမှ တစ်ခါတည်း ပြန်ပို့တယ်
            // (ဤနေရာမှာ ထပ်မလုပ် — double reboot ကာကွယ်)။
            sw.Stop();
            if (flashOk)
            {
                Log(" Patching patch0.xml ... Ok", Color.LightGreen);
                TimeSpan t = sw.Elapsed;
                Log($" Elapsed Time... {t.Minutes} minutes {t.Seconds} seconds", Color.Gainsboro);
                Log($" Finished at local time: [{DateTime.Now:yy.MM.dd HH:mm:ss}]", Color.Cyan);
            }
        }

        // ================= SAMSUNG — DIRECT HEIMDALL CLI ENGINE =================
        // Odin3.exe ကို လုံးဝ မသုံးတော့ဘူး — heimdall.exe ကို CLI backend အနေနဲ့ run ပြီး
        // output/progress (0-100% + speed) ကို ExecuteCommandCleanAsync ကတဆင့် တိုက်ရိုက် ပြတယ်။
        //   • .img          → heimdall flash --<PARTITION> "<file>"
        //   • .tar/.tar.md5 → archive ထဲက .img တွေကို ဖြည်ပြီး တစ်ခုချင်း flash
        //   • Download PIT  → heimdall download-pit --output "<file>" → binary PIT ဖတ်ပြီး grid ဖြည့်

        private const int PitEntrySize = 132;        // 9×u32 + 3×32-byte (UTF-16) name field
        private const long SamsungBlockSize = 512;   // PIT block unit

        // Samsung EFS/NV — heimdall က download-mode ကနေ partition ဖတ်လို့မရလို့ ADB root (su + dd) သုံးတယ်
        // ================= EDL MODULE (Qualcomm) =================
        // edl.py (bkerler EDL) ကို mtk.exe လိုပဲ ခေါ်သုံးတယ် — path ကို pmk_paths.txt မှာ သိမ်းတယ်
        private string PathsFile
        {
            get { return ShopServices.DataPath("pmk_paths.txt"); }
        }

        private void LoadEdlPaths()
        {
            try
            {
                if (!File.Exists(PathsFile)) return;
                string[] lines = File.ReadAllLines(PathsFile);
                if (lines.Length > 0) edlScriptPath = lines[0].Trim();
                // lines[1] (QC loader) / lines[8] (SPD loader) — မသိမ်းတော့ (user request)
                if (lines.Length > 3) hisiBootloadersPath = lines[3].Trim();
                if (lines.Length > 4) edlSigPath = lines[4].Trim();
                if (lines.Length > 5) mtkDaPath = lines[5].Trim();
                if (lines.Length > 6) mtkAuthPath = lines[6].Trim();
                if (lines.Length > 7) mtkPreloaderPath = lines[7].Trim();
            }
            catch (Exception ex)
            {
                // rtbLog မဖန်တီးရသေးလို့ Log() မခေါ်နိုင် — crash handler က cover
                Debug.WriteLine("LoadEdlPaths: " + ex.Message);
            }
        }

        private void SaveEdlPaths()
        {
            try
            {
                // slot 1 (QC loader) + slot 8 (SPD loader) = ကွက်လပ် — ရွေးထားတဲ့ loader များ ပိတ်ရင် ပြန်မပေါ်စေရ (index alignment အတွက် slot နေရာထား)
                File.WriteAllLines(PathsFile, new string[]
                {
                    edlScriptPath ?? "", "", edlLegacySlot ?? "", hisiBootloadersPath ?? "",
                    edlSigPath ?? "", mtkDaPath ?? "", mtkAuthPath ?? "", mtkPreloaderPath ?? "", ""
                });
            }
            catch (Exception ex) { Log("Path save error: " + ex.Message, Color.Red); }
        }

        // User settings (checkbox 10 ခု) — pmk_settings.txt (key=value, one per line)
        private string SettingsFile
        {
            get { return ShopServices.DataPath("pmk_settings.txt"); }
        }

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsFile)) return;
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string raw in File.ReadAllLines(SettingsFile))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    map[key] = val;
                }
                bool AsBool(string v) => v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
                void Set(CheckBox cb, string key)
                {
                    if (cb != null && map.TryGetValue(key, out string v)) cb.Checked = AsBool(v);
                }
                Set(chkQcBackupEfsFirst, "QcBackupEfsFirst");
                Set(chkQcSkipUserdata, "QcSkipUserdata");
                Set(chkQcResetFrpAfter, "QcResetFrpAfter");
                Set(chkQcAutoReboot, "QcAutoReboot");
                Set(chkSamBackupPit, "SamBackupPit");
                Set(chkSamAutoReboot, "SamAutoReboot");
                if (map.TryGetValue("LightTheme", out string lt)) { lightTheme = AsBool(lt); ApplyTheme(); }

                // String settings — last COM port / splitter
                // Firmware folder paths ကို မသိမ်းတော့ — ဖွင့်တိုင်း clean (user request)
                if (map.TryGetValue("LastPort", out string lp) && !string.IsNullOrEmpty(lp) &&
                    cmbPorts != null && cmbPorts.Items.Contains(lp))
                {
                    cmbPorts.SelectedItem = lp;
                }
                if (map.TryGetValue("SplitterDistance", out string sd) && int.TryParse(sd, out int dist) && dist > 100)
                {
                    pendingSplitterDistance = dist;
                }
            }
            catch (Exception ex)
            {
                Log("[!] Settings load failed: " + ex.Message, Color.Orange);
            }
        }

        private void SaveSettings()
        {
            try
            {
                void Line(CheckBox cb, string key, StringBuilder sb)
                {
                    sb.Append(key).Append('=').Append(cb != null && cb.Checked ? "1" : "0").AppendLine();
                }
                void Str(string key, string value, StringBuilder sb)
                {
                    sb.Append(key).Append('=').Append(value ?? "").AppendLine();
                }
                var sb = new StringBuilder();
                sb.AppendLine("# PMKUnlocker user settings");
                Line(chkQcBackupEfsFirst, "QcBackupEfsFirst", sb);
                Line(chkQcSkipUserdata, "QcSkipUserdata", sb);
                Line(chkQcResetFrpAfter, "QcResetFrpAfter", sb);
                Line(chkQcAutoReboot, "QcAutoReboot", sb);
                Line(chkSamBackupPit, "SamBackupPit", sb);
                Line(chkSamAutoReboot, "SamAutoReboot", sb);
                sb.Append("LightTheme=").Append(lightTheme ? "1" : "0").AppendLine();
                // MtkFirmware / QcFirmware ကို ရေးတော့မပါ — ပိတ်ရင် path အဟောင်း ဖျက်သွားမယ်
                Str("LastPort", cmbPorts?.SelectedItem?.ToString() ?? "", sb);
                Str("SplitterDistance", userAdjustedSplit ? splitMain.SplitterDistance.ToString() : "", sb);
                // panel-written keys (MtkNvBackupRoot …) — whole-file rewrite မှာ မပျက်အောင် preserve
                var knownKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "QcBackupEfsFirst", "QcSkipUserdata", "QcResetFrpAfter", "QcAutoReboot", "SamBackupPit", "SamAutoReboot", "LightTheme", "LastPort", "SplitterDistance" };
                try
                {
                    if (File.Exists(SettingsFile))
                        foreach (string raw in File.ReadAllLines(SettingsFile))
                        {
                            string line = raw.Trim();
                            if (line.Length == 0 || line.StartsWith("#")) continue;
                            int eq = line.IndexOf('=');
                            if (eq <= 0) continue;
                            if (!knownKeys.Contains(line.Substring(0, eq).Trim())) sb.AppendLine(line);
                        }
                }
                catch { }
                File.WriteAllText(SettingsFile, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Log("[!] Settings save failed: " + ex.Message, Color.Orange);
            }
        }

        // edl.py ကို အလိုအလျောက် ရှာတယ် — exe ဘေး၊ ပြီးရင် Desktop ပေါ်က ရှိပြီးသား copy တွေ
        private string FindEdlScript()
        {
            return FindFileInToolFolders("edl.py");
        }

        // QC reboot args — flash က edl.py မသုံးလို့ path မရှိနိုင်; op ပြီးမှ တစ်ခါထပ်ရှာ (tools.json/tool folder)
        private string BuildQcRebootArgs()
        {
            if (string.IsNullOrEmpty(edlScriptPath) || !File.Exists(edlScriptPath))
            {
                edlScriptPath = FindEdlScript();
                if (!string.IsNullOrEmpty(edlScriptPath)) SaveEdlPaths();
            }
            return !string.IsNullOrEmpty(edlScriptPath) ? BuildEdlArgs("reset --resetmode=reset") : "";
        }

        // edl.py (python) မရှိတဲ့အခါ — flash သုံးခဲ့တဲ့ firehose session ကနေ fh_loader နဲ့ power reset
        // (release ထဲ edl.py မပါလို့ မဖြစ်မနိ QC auto-reboot ပျောက်နေတာကို ကာကွယ်)
        private async Task QcFirehoseRebootAsync()
        {
            try
            {
                string port = cmbPorts.SelectedItem?.ToString();
                if (string.IsNullOrEmpty(port) || !port.StartsWith("COM"))
                {
                    Log("[!] Auto reboot skipped: no COM port.", Color.Orange);
                    return;
                }
                string xml = Path.Combine(Path.GetTempPath(), "pmk_reboot.xml");
                File.WriteAllText(xml, "<?xml version=\"1.0\" encoding=\"UTF-8\" ?><data><power value=\"reset\" /></data>");
                Log("[*] Auto reboot after successful QC operation (firehose power reset).", Color.Orange);
                bool rebooted = await ExecuteCommandCleanAsync("fh_loader.exe",
                    $"--port=\\\\.\\{port} --sendxml=\"{xml}\" --noprompt",
                    "Auto Reboot (QC)", quiet: true, timeoutSec: 60, timeoutMeansSuccess: true);
                if (rebooted && !stopRequested) _ = WaitAndroidAfterRebootAsync();
            }
            catch (Exception ex)
            {
                Log("[!] Auto reboot failed: " + ex.Message, Color.Orange);
            }
        }

        private string BuildEdlArgs(string command)
        {
            string loader = (!string.IsNullOrEmpty(edlLoaderPath) && File.Exists(edlLoaderPath))
                ? " --loader=\"" + edlLoaderPath + "\"" : "";

            // Header တွင် ရွေးထားသော COM Port ကို ထည့်ပေးခြင်း
            string portParam = "";
            if (cmbPorts.SelectedItem != null && cmbPorts.SelectedItem.ToString().StartsWith("COM"))
            {
                portParam = " --port=" + cmbPorts.SelectedItem.ToString();
            }

            return "\"" + edlScriptPath + "\"" + loader + portParam + " " + command;
        }

        private async Task RunEdlAsync(string command, string title, bool clearPartitions = false,
            Func<List<string>, bool> tolerateRaw = null)
        {
            bool ok = await RunNativeEdlAsync(command, title, clearPartitions);
            if (!ok && flashWorkflowContext.Value) workflowFailed = true;
        }

        // Native QC dispatch — replaces `python edl.py <command>` for the Qualcomm tab.
        // Opens one firehose session (Sahara + ping + sig auth), then runs the verb.
        private async Task<bool> RunNativeEdlAsync(string command, string title, bool clearPartitions)
        {
            if (isTaskRunning || (flashWorkflowRunning && !flashWorkflowContext.Value))
            {
                Log("[!] Another operation is still running - wait for it to finish or press STOP.", Color.OrangeRed);
                return false;
            }

            isTaskRunning = true;
            try
            {
                return await RunNativeEdlCoreAsync(command, title, clearPartitions);
            }
            finally
            {
                isTaskRunning = false;
            }
        }

        private async Task<bool> RunNativeEdlCoreAsync(string command, string title, bool clearPartitions)
        {
            string port = cmbPorts.SelectedItem?.ToString();
            if (string.IsNullOrEmpty(port) || !port.StartsWith("COM"))
            {
                Log("[!] Select Qualcomm 9008 COM port first.", Color.Orange);
                return false;
            }

            string loader = (!string.IsNullOrEmpty(edlLoaderPath) && File.Exists(edlLoaderPath)) ? edlLoaderPath : null;
            string sig = string.IsNullOrEmpty(edlSigPath) ? null : edlSigPath;

            // STOP ကို firehose serial op တိုင်းမှာ ချက်ချင်းတုံ့ပြန်စေဖို့ — stopRequested
            // ဖြစ်တာနဲ့ CTS ကို cancel (ReadResponseAsync / data loop တွေ အားလုံး ရပ်)
            using var cts = new CancellationTokenSource();
            var stopWatcher = Task.Run(async () =>
            {
                try
                {
                    while (!stopRequested && !cts.Token.IsCancellationRequested)
                        await Task.Delay(100, cts.Token);
                    if (stopRequested) cts.Cancel();
                }
                catch (OperationCanceledException) { }
            });

            try
            {
                Log("========================================================", Color.FromArgb(0, 180, 255));
                Log("           PMK TOOL - " + title.ToUpper() + "             ", Color.White);
                Log("========================================================", Color.FromArgb(0, 180, 255));
                Log("[*] Initializing Task...", Color.Orange);
                SetProgress(10, "Initializing");

                var args = SplitArgs(command);
                if (args.Count == 0)
                {
                    Log("[FAIL] Empty command.", Color.Red);
                    return false;
                }

                using var session = await FirehoseSession.OpenAsync(port, loader, sig, NativeEdlLog, cts.Token);
                if (flashWorkflowContext.Value) workflowDidOp = true;

                switch (args[0].ToLowerInvariant())
                {
                    case "printgpt":
                        if (clearPartitions) ClearPartitionData();
                        await NativeReadGptAsync(session, cts.Token);
                        break;
                    case "gpt":
                        await NativeSaveGptXmlAsync(session, args, cts.Token);
                        break;
                    case "r":
                        await NativeReadPartitionAsync(session, args, cts.Token);
                        break;
                    case "w":
                        await NativeWritePartitionAsync(session, args, cts.Token);
                        break;
                    case "e":
                        await NativeErasePartitionsAsync(session, args, cts.Token);
                        break;
                    case "reset-userdata":
                        await NativeResetUserdataAsync(session, cts.Token);
                        break;
                    case "rl":
                        await NativeReadAllAsync(session, args, cts.Token);
                        break;
                    case "reset":
                        await session.ResetAsync(cts.Token);
                        Log("[OK] Reset command sent.", Color.LightGreen);
                        break;
                    default:
                        Log("[FAIL] Unknown Qualcomm command: " + args[0], Color.Red);
                        return false;
                }

                Log("[OK] " + title + " completed.", Color.LightGreen);
                SetProgress(100, "Done");
                return true;
            }
            catch (OperationCanceledException)
            {
                Log(stopRequested ? "[!] Stopped by user." : "[!] Cancelled.", Color.OrangeRed);
                SetProgress(0, "Stopped");
                return false;
            }
            catch (Exception ex)
            {
                Log("[FAIL] " + title + ": " + ex.Message, Color.Red);
                SetProgress(100, "FAIL");
                return false;
            }
            finally
            {
                cts.Cancel();
                try { await stopWatcher; } catch { }
            }
        }

        // FirehoseSession log -> UI log (တစ်နေရာတည်း ရောင်စုံ mapping)
        private void NativeEdlLog(string msg)
        {
            if (msg.StartsWith("[+]")) Log(" " + msg, Color.LightGreen);
            else if (msg.StartsWith("[!]")) Log(" " + msg, Color.Orange);
            else Log(" " + msg, Color.White);
        }

        private async Task NativeReadGptAsync(FirehoseSession session, CancellationToken ct)
        {
            Log("[*] Reading GPT (partition table)...", Color.Orange);
            SetProgress(20, "Reading GPT");
            var parts = await session.ReadGptAsync(ct);
            foreach (var p in parts)
            {
                AddPartitionRow(p.Name,
                    "0x" + p.ByteOffset(session.SectorSize).ToString("x"),
                    "0x" + p.ByteLength(session.SectorSize).ToString("x"));
            }
            Log($"[OK] {parts.Count} partition(s) read.", Color.LightGreen);
        }

        private async Task NativeSaveGptXmlAsync(FirehoseSession session, List<string> args, CancellationToken ct)
        {
            string folder = args.Count > 1 ? args[1] : "";
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                throw new IOException("Select a valid folder for GPT + XML output.");
            Log("[*] Reading GPT for XML export...", Color.Orange);
            SetProgress(20, "Reading GPT");
            var parts = await session.ReadGptAsync(ct);
            string xml = GptParser.BuildRawProgramXml(parts, session.SectorSize);
            string path = Path.Combine(folder, "rawprogram0.xml");
            File.WriteAllText(path, xml);
            Log($"[OK] GPT saved — {parts.Count} partition(s), rawprogram0.xml written.", Color.LightGreen);
        }

        private async Task NativeReadPartitionAsync(FirehoseSession session, List<string> args, CancellationToken ct)
        {
            if (args.Count < 3) throw new IOException("Read needs: r <partition> <file>");
            string name = args[1], dest = args[2];
            var p = await session.FindPartitionAsync(name, ct);
            if (p == null) throw new IOException("Partition not found in GPT: " + name);
            Log("[*] Reading " + name + " (" + FormatBytesLong(p.ByteLength(session.SectorSize)) + ")...", Color.Orange);
            SetProgress(0, "Reading " + name);
            await session.ReadToFileAsync(p.FirstLba, p.SectorCount, dest,
                pct => SetProgress(pct, "Reading " + name), ct, () => stopRequested);
            Log("[OK] " + name + " -> " + dest, Color.LightGreen);
        }

        private async Task NativeWritePartitionAsync(FirehoseSession session, List<string> args, CancellationToken ct)
        {
            if (args.Count < 3) throw new IOException("Write needs: w <partition> <file>");
            string name = args[1], src = args[2];
            if (!File.Exists(src)) throw new IOException("Image not found: " + src);
            var p = await session.FindPartitionAsync(name, ct);
            if (p == null) throw new IOException("Partition not found in GPT: " + name);
            long fileLen = new FileInfo(src).Length;
            long partLen = p.ByteLength(session.SectorSize);
            if (fileLen > partLen)
                throw new IOException($"{name}: image {fileLen} B larger than partition {partLen} B — refusing to overwrite neighbors.");
            Log("[*] Writing " + name + " (" + FormatBytesLong(fileLen) + ")...", Color.Orange);
            SetProgress(0, "Writing " + name);
            await session.ProgramAsync(p.FirstLba, src,
                pct => SetProgress(pct, "Writing " + name), ct, () => stopRequested);
            Log("[OK] " + name + " written.", Color.LightGreen);
        }

        private async Task NativeErasePartitionsAsync(FirehoseSession session, List<string> args, CancellationToken ct)
        {
            if (args.Count < 2) throw new IOException("Erase needs: e <partition[,partition...]>");
            var names = args[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (string name in names)
            {
                if (stopRequested) throw new OperationCanceledException("STOP");
                var p = await session.FindPartitionAsync(name, ct);
                if (p == null)
                {
                    Log("[!] Partition not found — skipping erase: " + name, Color.Orange);
                    continue;
                }
                Log("[*] Erasing " + name + " (" + FormatBytesLong(p.ByteLength(session.SectorSize)) + ")...", Color.Orange);
                await session.EraseAsync(p.FirstLba, p.SectorCount, ct);
                Log("[OK] " + name + " erased.", Color.LightGreen);
            }
        }

        private async Task NativeResetUserdataAsync(FirehoseSession session, CancellationToken ct)
        {
            var layout = await session.ReadGptLayoutAsync(ct);
            if (!layout.Partitions.Any(p => string.Equals(p.Name, "metadata", StringComparison.OrdinalIgnoreCase)))
                Log("[*] No metadata partition in the live GPT; only userdata will be erased.", Color.Orange);

            await ReviewSafety.RunQcUserdataResetAsync(layout, session.SectorSize, async (p, token) =>
            {
                if (stopRequested) throw new OperationCanceledException("STOP");
                Log($"[*] Erasing {p.Name}: LBA {p.FirstLba}..{p.LastLba} ({FormatBytesLong(p.ByteLength(session.SectorSize))})...", Color.Orange);
                SetProgress(20, "Erasing " + p.Name);
                await session.EraseAsync(p.FirstLba, p.SectorCount, token);
                Log("[OK] " + p.Name + " erased.", Color.LightGreen);
            }, async token =>
            {
                if (stopRequested) throw new OperationCanceledException("STOP");
                SetProgress(90, "Resetting device");
                await session.ResetAsync(token);
                qcAuthed = false;
                qcAuthedPort = "";
                Log("[*] Reset sent over the same native session; check the phone for Android startup.", Color.Cyan);
            }, ct);
        }

        private async Task NativeReadAllAsync(FirehoseSession session, List<string> args, CancellationToken ct)
        {
            string folder = args.Count > 1 ? args[1] : "";
            if (string.IsNullOrEmpty(folder)) throw new IOException("Select a valid backup folder.");
            Directory.CreateDirectory(folder);
            var parts = await session.ReadGptAsync(ct);
            int done = 0, failed = 0;
            foreach (var p in parts)
            {
                if (stopRequested) throw new OperationCanceledException("STOP");
                string dest = Path.Combine(folder, p.Name + ".bin");
                try
                {
                    Log("[*] Reading " + p.Name + " (" + FormatBytesLong(p.ByteLength(session.SectorSize)) + ")...", Color.Orange);
                    await session.ReadToFileAsync(p.FirstLba, p.SectorCount, dest,
                        pct => SetProgress(pct, "Reading " + p.Name), ct, () => stopRequested);
                    done++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failed++;
                    Log("[!] " + p.Name + " read failed: " + ex.Message, Color.Orange);
                }
            }
            Log($"[OK] Full backup finished — {done}/{parts.Count} partition(s) read" + (failed > 0 ? $" ({failed} failed)" : ""), Color.LightGreen);
        }

        // Quote-aware tokenizer — `gpt "C:\my folder" --genxml` → ["gpt", "C:\my folder", "--genxml"]
        private static List<string> SplitArgs(string command)
        {
            var args = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;
            foreach (char c in command)
            {
                if (c == '"') { inQuotes = !inQuotes; continue; }
                if (c == ' ' && !inQuotes)
                {
                    if (sb.Length > 0) { args.Add(sb.ToString()); sb.Clear(); }
                    continue;
                }
                sb.Append(c);
            }
            if (sb.Length > 0) args.Add(sb.ToString());
            return args;
        }

        // Unlock Tool လို — Qualcomm op မဆိုမတိုင်မီ auto Sahara + sig auth
        // (native firehose session OpenAsync ထဲ အလိုအလျောက် ပါပြီးသား — သီးခြား auth step မလို)
        private async void BtnQcReadInfo_Click(object sender, EventArgs e)
        {
            await RunFlashWorkflowAsync(() => RunEdlAsync("printgpt", "Qualcomm Read Info / GPT", clearPartitions: true));
        }

        private async void BtnQcSaveGpt_Click(object sender, EventArgs e)
        {
            using (FolderBrowserDialog fbd = new FolderBrowserDialog { Description = "GPT + rawprogram XML သိမ်းမယ့် folder ကို ရွေးပါ" })
            {
                if (fbd.ShowDialog() == DialogResult.OK)
                    await RunFlashWorkflowAsync(() => RunEdlAsync("gpt \"" + fbd.SelectedPath + "\" --genxml", "Qualcomm Save GPT + XML"));
            }
        }

        private async void BtnQcFrpRemove_Click(object sender, EventArgs e)
        {
            if (!ConfirmDialog.Ask(this,
                    "FRP Remove (Qualcomm) — frp နဲ့ config partition ကို erase လုပ်ပါမယ်။\n\n" +
                    "• User data မပျက်ပါ (FRP/config ကိုသာ ဖျက်တယ်)\n" +
                    "• ဖုန်းပိုင်ရှင် ခွင့်ပြုချက် ရှိမှ လုပ်ပါ (ခိုးရာပစ္စည်း မဟုတ်ကြောင်း စစ်ပါ)\n\n" +
                    "ဆက်လုပ်မလား?",
                    "Confirm FRP Remove"))
                return;

            await RunFlashWorkflowAsync(() => RunEdlAsync("e frp,config", "Qualcomm FRP Remove (frp + config)"));
        }

        private async void BtnQcUserlockReset_Click(object sender, EventArgs e)
        {
            if (!ConfirmDialog.Ask(this,
                    "Userlock Reset (Qualcomm) — userdata နဲ့ metadata (ရှိလျှင်) ကို erase လုပ်ပါမယ်။\n\n" +
                    "• ဖုန်းထဲက data / ဓာတ်ပုံ / အက်ပ် အားလုံး ဖျက်ခံရမယ်\n" +
                    "• Screen lock (PIN / pattern / password) ပျက်သွားမယ်\n" +
                    "• EFS / IMEI, bootchain နဲ့ FRP ကို မဖျက်ပါ\n" +
                    "• ဖုန်းပိုင်ရှင် ခွင့်ပြုချက် ရှိမှ လုပ်ပါ (ခိုးရာပစ္စည်း မဟုတ်ကြောင်း စစ်ပါ)\n" +
                    "• data မဖျက်ဘဲ lock ဖျက်ချင်ရင် root/TWRP + ADB လမ်းကို သုံးပါ\n\n" +
                    "ဆက်လုပ်မလား?",
                    "Confirm Userlock Reset"))
                return;

            await RunFlashWorkflowAsync(
                () => RunEdlAsync("reset-userdata", "Qualcomm Userlock Reset (native)"),
                autoRebootAfter: false);
        }

        private async void BtnQcFullBackup_Click(object sender, EventArgs e)
        {
            using (FolderBrowserDialog fbd = new FolderBrowserDialog { Description = "Partition အားလုံး backup သိမ်းမယ့် folder ကို ရွေးပါ" })
            {
                if (fbd.ShowDialog() == DialogResult.OK)
                    await RunFlashWorkflowAsync(() => RunEdlAsync("rl \"" + fbd.SelectedPath + "\"", "Qualcomm Full Backup (all partitions)"));
            }
        }

        // EFS Backup — persist / modemst1 / modemst2 / fsg / fsc partition တွေကို bin အဖြစ် dump။
        // ReviewSafety.QcEfsPartitions set အတိုင်း loop — edl.py `r` က partition တစ်ခုတည်းအတွက်ဆိုတော့
        // missing partition (fsc မရှိတဲ့ ဖုန်း) မှာတောင် ကျန်တာတွေ ဆက်ထွက်အောင် sequential call သုံးတယ်။
        private async void BtnQcEfsBackup_Click(object sender, EventArgs e)
        {
            using (FolderBrowserDialog fbd = new FolderBrowserDialog { Description = "EFS backup သိမ်းမယ့် folder ကို ရွေးပါ" })
            {
                if (fbd.ShowDialog() != DialogResult.OK) return;
                string dir = fbd.SelectedPath;
                await RunFlashWorkflowAsync(async () =>
                {
                    foreach (string name in ReviewSafety.QcEfsPartitions)
                        await RunEdlAsync("r " + name + " \"" + Path.Combine(dir, name + ".bin") + "\"", "EFS Backup (" + name + ")");
                });
            }
        }

        private async void BtnQcReset_Click(object sender, EventArgs e)
        {
            qcAuthed = false;
            qcAuthedPort = "";
            // ဒီ button ကိုယ်တိုင် reset ဖြစ် — workflow auto-reboot ထပ်မလုပ်
            await RunFlashWorkflowAsync(() => RunEdlAsync("reset --resetmode=reset", "Qualcomm Reset Device"), autoRebootAfter: false);
        }

        // QC Restore — Full Backup (`edl.py rl`) / EFS Backup folder ထဲက <partition>.bin တွေကို ပြန်ရေး။
        // Read Info/GPT ဖတ်ထားရင် live partition size နဲ့ တိုက်စစ်ပြီး မကိုက်တဲ့ ဖိုင်ကို ကျော်တယ်
        // (မှားတဲ့ ဖိုင်/မှားတဲ့ ဖုန်း backup ကို မရေးမိအောင်)။
        private async void BtnQcRestoreBackup_Click(object sender, EventArgs e)
        {
            using (FolderBrowserDialog fbd = new FolderBrowserDialog
            {
                Description = "Restore လုပ်မယ့် backup folder ရွေးပါ (<partition>.bin ဖိုင်များ)"
            })
            {
                if (fbd.ShowDialog() != DialogResult.OK) return;
                string dir = fbd.SelectedPath;

                var files = new List<(string Name, string Path)>();
                try
                {
                    foreach (string f in Directory.GetFiles(dir, "*.bin"))
                    {
                        string name = Path.GetFileNameWithoutExtension(f);
                        if (IsValidPartitionName(name)) files.Add((name, f));
                    }
                }
                catch (Exception ex) { Log("[FAIL] Backup folder read failed: " + ex.Message, Color.Red); return; }
                files.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                if (files.Count == 0)
                {
                    Log("[!] No <partition>.bin files found in " + dir, Color.Orange);
                    return;
                }

                // live table (Read Info / GPT) — ရှိရင် size တိုက်စစ်မယ်၊ မရှိရင် သတိပေးပြီး မေးမယ်
                var live = QcLiveSizes();

                var plan = new List<(string Name, string Path)>();
                var skipped = new List<string>();
                foreach (var f in files)
                {
                    if (live.TryGetValue(f.Name, out long len))
                    {
                        long actual;
                        try { actual = new FileInfo(f.Path).Length; } catch { actual = -1; }
                        if (actual != len)
                        {
                            skipped.Add($"{f.Name} (file {actual} B ≠ partition {len} B)");
                            continue;
                        }
                    }
                    plan.Add(f);
                }
                foreach (string s in skipped) Log("[!] Skipped — size does not match this device: " + s, Color.Orange);
                if (plan.Count == 0)
                {
                    Log("[!] Nothing to restore — every file failed the partition-size check.", Color.OrangeRed);
                    if (live.Count == 0) Log("[i] Read Info / GPT first so sizes can be checked.", Color.Gray);
                    return;
                }

                if (live.Count == 0 &&
                    MessageBox.Show(this,
                        "Device partition table was not read — backup file sizes cannot be checked.\n\n" +
                        "If this backup came from a different phone/firmware, restoring it can brick the device.\n\n" +
                        "Continue anyway?", "Restore without size check",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

                string preview = string.Join(", ", plan.Take(12).Select(p => p.Name)) + (plan.Count > 12 ? ", …" : "");
                if (!ConfirmDialog.Ask(this,
                        $"Restore {plan.Count} partition(s) from:\n{dir}\n\n{preview}\n\n" +
                        "Each partition will be overwritten. This cannot be undone.\n\n" +
                        "ဖုန်းပိုင်ရှင် ခွင့်ပြုချက် ရှိမှ လုပ်ပါ။ ဆက်လုပ်မလား?",
                        "Confirm QC Restore")) return;

                await RunFlashWorkflowAsync(async () =>
                {
                    int done = 0;
                    foreach (var f in plan)
                    {
                        if (stopRequested) break;
                        bool ok = await RunEdlCommandAsync(
                            "w " + f.Name + " \"" + f.Path + "\"", "Qualcomm Restore — " + f.Name);
                        if (!ok)
                            throw new IOException("Restore stopped at " + f.Name + " — the remaining partitions were NOT written.");
                        done++;
                    }
                    Log($"[OK] Restore finished — {done}/{plan.Count} partition(s) written" +
                        (skipped.Count > 0 ? $" ({skipped.Count} skipped)" : ""), Color.LightGreen);
                    Log("[i] Reboot the phone after the restore (Reset Device) if it does not restart by itself.", Color.Cyan);
                }, autoRebootAfter: false);
            }
        }

        // Read Info / GPT (loadedPartitions) ကနေ partition name -> size (bytes) map တည်ဆောက်။
        // "-" / မဖတ်နိုင်တဲ့ size row တွေကို ကျော်တယ် — size check မလုပ်ဘူး။
        private Dictionary<string, long> QcLiveSizes()
        {
            var live = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (PartitionMeta pm in loadedPartitions)
            {
                if (string.IsNullOrWhiteSpace(pm.Name) || live.ContainsKey(pm.Name)) continue;
                try
                {
                    long len = ReviewSafety.ParseSize(pm.LengthHex);
                    if (len > 0) live[pm.Name] = len;
                }
                catch { /* unknown size — skip size check for this row */ }
            }
            return live;
        }

        // EFS Restore (IMEI repair) — EFS backup folder ထဲက persist/modemst1/modemst2/fsg/fsc ကို ပြန်ရေး။
        // Qualcomm မှာ diag/QMI IMEI-write လမ်းမရှိလို့ IMEI repair = NV/EFS partition restore (honest limitation)။
        private async void BtnQcEfsRestore_Click(object sender, EventArgs e)
        {
            using (FolderBrowserDialog fbd = new FolderBrowserDialog
            {
                Description = "EFS restore လုပ်မယ့် backup folder ရွေးပါ (persist/modemst1/modemst2/fsg/fsc .bin)"
            })
            {
                if (fbd.ShowDialog() != DialogResult.OK) return;
                string dir = fbd.SelectedPath;

                var live = QcLiveSizes();
                var (plan, skipped) = ReviewSafety.BuildQcEfsRestorePlan(dir, ReviewSafety.QcEfsPartitions, live);
                foreach (string s in skipped) Log("[!] Skipped — " + s, Color.Orange);
                if (plan.Count == 0)
                {
                    Log("[!] Nothing to restore — no matching EFS files found in " + dir, Color.OrangeRed);
                    if (live.Count == 0) Log("[i] Read Info / GPT first so sizes can be checked.", Color.Gray);
                    return;
                }

                if (live.Count == 0 &&
                    MessageBox.Show(this,
                        "Device partition table was not read — EFS file sizes cannot be checked.\n\n" +
                        "If this backup came from a different phone/firmware, restoring it can brick the device.\n\n" +
                        "Continue anyway?", "EFS Restore without size check",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

                string preview = string.Join(", ", plan.Select(p => p.Name));
                if (!ConfirmDialog.Ask(this,
                        $"Restore {plan.Count} EFS partition(s) from:\n{dir}\n\n{preview}\n\n" +
                        "Each partition will be overwritten. This cannot be undone.\n\n" +
                        "ဖုန်းပိုင်ရှင် ခွင့်ပြုချက် ရှိမှ လုပ်ပါ။ ဆက်လုပ်မလား?",
                        "Confirm EFS Restore")) return;

                await RunFlashWorkflowAsync(async () =>
                {
                    int done = 0;
                    foreach (var p in plan)
                    {
                        if (stopRequested) break;
                        bool ok = await RunEdlCommandAsync(
                            "w " + p.Name + " \"" + p.Path + "\"", "Qualcomm EFS Restore — " + p.Name);
                        if (!ok)
                            throw new IOException("EFS restore stopped at " + p.Name + " — the remaining partitions were NOT written.");
                        done++;
                    }
                    Log($"[OK] EFS restore finished — {done}/{plan.Count} partition(s) written" +
                        (skipped.Count > 0 ? $" ({skipped.Count} skipped)" : ""), Color.LightGreen);
                    Log("[i] Reboot the phone after the restore (Reset Device) if it does not restart by itself.", Color.Cyan);
                }, autoRebootAfter: false);
            }
        }

        // IMEI Read (EFS) — pure file scan (no device/plug cycle), heuristic ASCII + hex-encoded ASCII, Luhn-checked.
        private async void BtnQcImeiReadEfs_Click(object sender, EventArgs e)
        {
            using var pick = new FolderBrowserDialog { Description = "Select EFS backup folder (persist/modemst1/modemst2/fsg/fsc .bin)" };
            if (pick.ShowDialog(this) != DialogResult.OK) return;
            string dir = pick.SelectedPath;
            var perFile = new List<(string File, string[] Imeis)>();
            foreach (string name in ReviewSafety.QcEfsPartitions)
            {
                string f = Path.Combine(dir, name + ".bin");
                if (!File.Exists(f)) continue;
                var imeis = await Task.Run(() => ExtractImeiCandidates(File.ReadAllBytes(f)));
                if (imeis.Length > 0) perFile.Add((Path.GetFileName(f), imeis));
            }
            var distinct = perFile
                .SelectMany(p => p.Imeis.Select(i => (Imei: i, p.File)))
                .GroupBy(x => x.Imei)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => (Imei: g.Key, Files: string.Join("+", g.Select(x => x.File).Distinct())))
                .ToList();
            if (distinct.Count == 0)
            {
                Log("  * Result ... no IMEI found in the selected EFS backup", Color.Orange);
                MessageBox.Show(this,
                    "No IMEI was found in the selected EFS backup folder.\n\nScanned: " + string.Join(", ", ReviewSafety.QcEfsPartitions.Select(n => n + ".bin")) +
                    "\n\nQualcomm IMEI is usually in modemst1/modemst2/fsg/fsc — make sure this is an EFS backup from this device.",
                    "IMEI Read (EFS)", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            foreach (var x in distinct) Log($"  * IMEI ... {x.Imei}   [{x.Files}]", Color.LightGreen);
            Log("  [!] Scan is heuristic (plain + hex-encoded ASCII, Luhn-checked) — cross-check against the sticker / About phone.", Color.Orange);
            MessageBox.Show(this,
                "IMEI found in EFS backup:\n\n" + string.Join("\n", distinct.Select(x => x.Imei + "   (" + x.Files + ")")) + "\n\nfrom:\n" + dir,
                "IMEI Read (EFS)", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private async Task<bool> RunEdlCommandAsync(string command, string title)
        {
            return await RunNativeEdlAsync(command, title, false);
        }

        // Firehose loader manual pick — loader picker ရဲ့ "Manual / saved loader" ကနေ / Loader file row ကနေ ခေါ်တယ်
        private void BrowseFirehoseLoader()
        {
            using (OpenFileDialog ofd = new OpenFileDialog
            {
                Filter = "Firehose loader (*.mbn;*.elf;*.bin;*.melf)|*.mbn;*.elf;*.bin;*.melf",
                Title = "Firehose programmer (loader) ကို ရွေးပါ"
            })
            {
                if (ofd.ShowDialog() != DialogResult.OK) return;
                ApplyQcLoader(ofd.FileName);
            }
        }

        // picked loader path ကို saved path + combo + status label တွေမှာ တစ်နေရာတည်း update
        private void ApplyQcLoader(string file)
        {
            edlLoaderPath = file ?? "";
            SaveEdlPaths();
            if (string.IsNullOrEmpty(edlLoaderPath))
            {
                if (cmbQcBrand?.SelectedItem as string == "Manual / saved loader" &&
                    cmbQcModel != null && cmbQcModel.Items.Count > 0)
                    cmbQcModel.Items[0] = "Choose Firehose Loader...";
                if (lblQcLoaderStatus != null)
                {
                    lblQcLoaderStatus.Text = "Auto Detect (saved / auto loader)";
                    lblQcLoaderStatus.ForeColor = Color.FromArgb(86, 145, 250);
                }
                return;
            }
            // brand ကတည်းက "Manual" ရွေးထားရင် SelectedIndexChanged ထပ်မဖြစ် → path အရင်သိမ်းမှ brand ပြောင်း
            if (cmbQcBrand != null) cmbQcBrand.SelectedItem = "Manual / saved loader";
            if (lblQcLoaderStatus != null)
            {
                lblQcLoaderStatus.Text = Path.GetFileName(edlLoaderPath);
                lblQcLoaderStatus.ForeColor = Color.LightGreen;
            }
            if (cmbQcModel != null && cmbQcModel.Items.Count > 0)
            {
                string fn = Path.GetFileName(edlLoaderPath);
                if (cmbQcModel.Items[0]?.ToString() != fn) cmbQcModel.Items[0] = fn;
            }
        }

        // ===== "Double click or Drag" file row — QC Loader / MTK DA·Auth·Preloader / SPD Loader =====
        // label column + hint field; double-click = browse, drag & drop = ဖိုင်, right-click = ဖျက်
        private Panel BuildFilePickerRow(Control parent, int x, int y, int w, string label, string hint,
            string filter, Func<string> getPath, Action<string> setPath)
        {
            Color labelBg = RowLabelBg;
            Color fieldBg = RowFieldBg;
            Color dim = RowHint;   // hint — theme အလိုက် ဖတ်ရလွယ်အောင်

            Panel row = new Panel
            {
                Name = "fileRow" + label.Replace(" ", ""),
                Location = new Point(x, y),
                Size = new Size(w, 24),
                BackColor = fieldBg,
                AllowDrop = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            Label lb = new Label
            {
                Text = " " + label,
                Location = new Point(0, 0),
                Size = new Size(70, 24),
                BackColor = labelBg,
                ForeColor = RowLabelText,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                AllowDrop = true,
                Cursor = Cursors.Hand
            };
            Label fld = new Label
            {
                Location = new Point(70, 0),
                Size = new Size(Math.Max(50, w - 70), 24),
                BackColor = fieldBg,
                ForeColor = dim,
                Font = new Font("Segoe UI", 9f),
                TextAlign = ContentAlignment.MiddleLeft,
                BorderStyle = BorderStyle.FixedSingle,
                AllowDrop = true,
                Cursor = Cursors.Hand,
                AutoEllipsis = true
            };
            row.Controls.Add(lb);
            row.Controls.Add(fld);

            void Show()
            {
                string p = getPath();
                if (string.IsNullOrEmpty(p))
                {
                    fld.Text = "⚙   " + hint;
                    fld.ForeColor = RowHint;
                }
                else
                {
                    fld.Text = "✔  " + Path.GetFileName(p);
                    fld.ForeColor = RowLoaded;
                }
            }

            void Apply(string p)
            {
                setPath(p ?? "");
                Show();
                if (!string.IsNullOrEmpty(p)) Log("[OK] " + label + ": " + p, Color.LightGreen);
                else Log("[i] " + label + " file cleared", Color.Gray);
            }

            void Browse()
            {
                using (OpenFileDialog ofd = new OpenFileDialog { Filter = filter, Title = label + " file ကို ရွေးပါ" })
                {
                    if (ofd.ShowDialog(parent.FindForm()) == DialogResult.OK) Apply(ofd.FileName);
                }
            }

            void Clear() { if (!string.IsNullOrEmpty(getPath())) Apply(""); }

            void RowEnter(object s, DragEventArgs e)
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
                    e.Data.GetData(DataFormats.FileDrop) is string[] files &&
                    files.Length > 0 && FileMatchesFilter(files[0], filter))
                    e.Effect = DragDropEffects.Copy;
                else
                    e.Effect = DragDropEffects.None;
            }
            void RowDrop(object s, DragEventArgs e)
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                {
                    if (FileMatchesFilter(files[0], filter)) Apply(files[0]);
                    else Log("[!] " + label + ": wrong file type — " + Path.GetFileName(files[0]), Color.Orange);
                }
            }

            foreach (Control c in new Control[] { row, lb, fld })
            {
                c.MouseDoubleClick += (s, e) => Browse();
                c.MouseClick += (s, e) => { if (e.Button == MouseButtons.Right) Clear(); };
                c.DragEnter += RowEnter;
                c.DragDrop += RowDrop;
            }
            row.MouseEnter += (s, e) => Show();   // တခြား code path က path ပြောင်းရင် hover မှာ refresh
            if (toolTipMain != null)
                Tip(row, "Double-click = browse • Drag & drop file = ထည့် • Right-click = ဖျက်");
            fileRowRefresh[row] = Show;   // theme toggle မှာ bg/text/State ပြန်သတ်မှတ်ဖို့
            Show();
            return row;
        }

        // drag & drop ဖိုင် filter နဲ့ ကိုက်/မကိုက်
        private static bool FileMatchesFilter(string file, string filter)
        {
            try
            {
                if (string.IsNullOrEmpty(filter) || filter.Contains("*.*")) return true;
                string ext = Path.GetExtension(file);
                foreach (string part in filter.Split('|'))
                {
                    if (!part.Contains("*.")) continue;
                    foreach (string tok in part.Split(';'))
                    {
                        string t = tok.Trim();
                        if (t.StartsWith("*.") && t.Equals("*" + ext, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                }
                return false;
            }
            catch { return false; }
        }

        // ================= QC LOADER PICKER (pmk_qc_loaders.json) =================
        private void LoadQcCodenames()
        {
            try
            {
                string path = Path.Combine(Application.StartupPath, "pmk_codenames.json");
                if (!File.Exists(path)) return;
                var raw = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(path));
                if (raw != null) qcCodenames = raw;
            }
            catch { /* codename မရှိရင် display က product+mode ပဲ ပြ */ }
        }

        private string GetQcCodename(string brand, string productName)
        {
            if (qcCodenames.TryGetValue(brand, out var map) && productName.Length > 0)
            {
                // exact match
                if (map.TryGetValue(productName, out var code)) return code;
                // case-insensitive partial: productName က key ထဲ ပါ/key က productName ထဲ ပါ
                foreach (var kv in map)
                {
                    if (string.Equals(kv.Key, productName, StringComparison.OrdinalIgnoreCase)) return kv.Value;
                    if (productName.Contains(kv.Key, StringComparison.OrdinalIgnoreCase) ||
                        kv.Key.Contains(productName, StringComparison.OrdinalIgnoreCase))
                        return kv.Value;
                }
            }
            return "";
        }

        // "[AuthBypass] MI 10" + brand → "MI 10 (umi) (AuthBypass)" — product name + codename + mode
        private string FormatQcModelDisplay(string brand, string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            string product = key;
            string mode = "";
            if (key.StartsWith('['))
            {
                int close = key.IndexOf(']');
                if (close > 1)
                {
                    mode = key.Substring(1, close - 1);
                    product = key.Substring(close + 1).TrimStart();
                }
            }
            string code = GetQcCodename(brand, product);
            string result = code.Length > 0 ? product + " (" + code + ")" : product;
            if (mode.Length > 0) result += " (" + mode + ")";
            return result;
        }

        private void LoadQcLoaderDb()
        {
            try
            {
                LoadQcCodenames();
                cmbQcBrand.Items.Add("Auto Detect");
                cmbQcBrand.Items.Add("Manual / saved loader");
                int initialSelection = string.IsNullOrEmpty(edlLoaderPath) ? 0 : 1;
                string path = Path.Combine(Application.StartupPath, "pmk_qc_loaders.json");
                if (!File.Exists(path)) { cmbQcBrand.SelectedIndex = initialSelection; return; }
                var raw = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(path));
                if (raw != null) qcLoaderDb = raw;
                foreach (var brand in qcLoaderDb.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
                    cmbQcBrand.Items.Add(brand);
                cmbQcBrand.SelectedIndex = initialSelection;
            }
            catch (Exception ex) { cmbQcBrand.Items.Add("(load error: " + ex.Message + ")"); cmbQcBrand.SelectedIndex = 0; }
        }

        // ===== QC: DEVICE LIST model → Firehose loader auto-link =====
        private static string NormQcName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToUpperInvariant(c));
            return sb.ToString();
        }

        private string MapLoaderBrand(string deviceBrand)
        {
            if (string.IsNullOrWhiteSpace(deviceBrand) || qcLoaderDb.Count == 0) return null;
            string b = deviceBrand.Trim();
            if (b.Equals("Moto", StringComparison.OrdinalIgnoreCase)) b = "Motorola";
            else if (b.Equals("One Plus", StringComparison.OrdinalIgnoreCase)) b = "Oneplus";
            else if (b.Equals("Huawei & Honor", StringComparison.OrdinalIgnoreCase)) b = "Huawei";
            foreach (string k in qcLoaderDb.Keys)
                if (string.Equals(k, b, StringComparison.OrdinalIgnoreCase)) return k;
            return null;
        }

        private static string MatchLoaderModel(string deviceModel, IEnumerable<string> keys)
        {
            string d = NormQcName(deviceModel);
            if (d.Length < 3) return null;
            string best = null;
            int bestScore = -1, bestLen = int.MaxValue;
            foreach (string key in keys)
            {
                // "[AuthBypass] MI 10" → "MI 10" — mode prefix ကို ချန်ပြီး product နဲ့ နှိုင်း
                string prod = key ?? "";
                if (prod.StartsWith('['))
                {
                    int close = prod.IndexOf(']');
                    if (close > 0) prod = prod.Substring(close + 1);
                }
                prod = NormQcName(prod);
                if (prod.Length < 3) continue;

                int score;
                if (prod == d) score = 3;                                   // အတိအကျ
                else if (prod.Length >= 4 && d.StartsWith(prod, StringComparison.Ordinal)) score = 2;  // device မှာ prefix/suffix ပိုပါ
                else if (prod.Length >= 4 && d.IndexOf(prod, StringComparison.Ordinal) >= 0) score = 1;
                else if (d.Length >= 4 && prod.StartsWith(d, StringComparison.Ordinal)) score = 1;    // loader မှာ ပိုရှည်
                else continue;

                if (score > bestScore ||
                    (score == bestScore && prod.Length < bestLen) ||
                    (score == bestScore && prod.Length == bestLen && best != null && string.CompareOrdinal(key, best) < 0))
                {
                    best = key; bestScore = score; bestLen = prod.Length;
                }
            }
            return best;
        }

        private void AutoLinkQcLoader(string deviceBrand, string deviceModel)
        {
            try
            {
                if (qcLoaderDb.Count == 0 || cmbQcBrand == null || cmbQcModel == null) return;
                string brand = MapLoaderBrand(deviceBrand);
                if (brand == null || !qcLoaderDb.TryGetValue(brand, out var models)) return;
                string key = MatchLoaderModel(deviceModel, models.Keys);
                if (key == null) return;

                if (!string.Equals(cmbQcBrand.SelectedItem as string, brand, StringComparison.OrdinalIgnoreCase))
                    cmbQcBrand.SelectedItem = brand;   // handler က cmbQcModel ကို ပြန်ဖြည့်တယ်
                if (!string.Equals(cmbQcBrand.SelectedItem as string, brand, StringComparison.OrdinalIgnoreCase)) return;

                int idx = -1;
                for (int i = 0; i < cmbQcModel.Items.Count; i++)
                {
                    string disp = cmbQcModel.Items[i]?.ToString();
                    if (disp != null && qcModelDisplayToKey.TryGetValue(disp, out string k) &&
                        string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
                }
                if (idx < 0 || cmbQcModel.SelectedIndex == idx) return;
                cmbQcModel.SelectedIndex = idx;   // → edlLoaderPath + log ([OK] Loader: ...)
            }
            catch { /* auto-link က link မလုပ်နိုင်ရင် loader picker အတိုင်းပဲ ထား */ }
        }

        private void CmbQcBrand_SelectedIndexChanged(object sender, EventArgs e)
        {
            cmbQcModel.Items.Clear();
            qcModelDisplayToKey.Clear();
            cmbQcModel.Enabled = false;
            if (cmbQcBrand.SelectedItem is not string brand) return;

            if (brand == "Manual / saved loader")
            {
                cmbQcModel.Items.Add(string.IsNullOrEmpty(edlLoaderPath) ? "Choose Firehose Loader..." : Path.GetFileName(edlLoaderPath));
                cmbQcModel.SelectedIndex = 0;
                return;
            }
            if (brand == "Auto Detect")
            {
                cmbQcModel.Items.Add("Auto Detect");
                cmbQcModel.SelectedIndex = 0;
                cmbQcModel.Enabled = false;
                return;
            }
            if (!qcLoaderDb.TryGetValue(brand, out var models)) return;
            foreach (var model in models.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                string display = FormatQcModelDisplay(brand, model);
                // display duplicate ဖြစ်ရင် key ကိုပဲ ထား
                if (qcModelDisplayToKey.ContainsKey(display)) display = model;
                qcModelDisplayToKey[display] = model;
                cmbQcModel.Items.Add(display);
            }
            cmbQcModel.Enabled = true;
            if (cmbQcModel.Items.Count > 0) cmbQcModel.SelectedIndex = 0;
        }

        private void CmbQcModel_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (cmbQcBrand.SelectedItem is not string brand) return;
            if (cmbQcModel.SelectedItem is not string display) return;

            if (brand == "Manual / saved loader")
            {
                // saved loader မရှိသေးရင် — ခုဏက ဒီ item ကို ရွေးတာမို့ browse dialog ချက်ချင်းဖွင့်
                if (string.IsNullOrEmpty(edlLoaderPath)) BrowseFirehoseLoader();
                lblQcLoaderStatus.Text = string.IsNullOrEmpty(edlLoaderPath) ? "Choose a Firehose Loader file" : Path.GetFileName(edlLoaderPath);
                lblQcLoaderStatus.ForeColor = File.Exists(edlLoaderPath) ? Color.LightGreen : Color.Orange;
                return;
            }

            // Explicit Auto Detect clears any previously selected manual loader.
            if (brand == "Auto Detect" || display == "Auto Detect")
            {
                edlLoaderPath = "";
                SaveEdlPaths();
                lblQcLoaderStatus.Text = "Auto Detect (no manual loader selected)";
                lblQcLoaderStatus.ForeColor = Color.FromArgb(86, 145, 250);
                return;
            }

            // display → JSON key
            string model = qcModelDisplayToKey.TryGetValue(display, out var k) ? k : display;

            if (!qcLoaderDb.TryGetValue(brand, out var models)) return;
            if (!models.TryGetValue(model, out var relPath)) return;

            string fullPath = Path.GetFullPath(Path.Combine(Application.StartupPath, relPath.Replace('/', Path.DirectorySeparatorChar)));
            if (File.Exists(fullPath))
            {
                ToolIntegrity.Verify(fullPath);
                edlLoaderPath = fullPath;
                SaveEdlPaths();
                lblQcLoaderStatus.Text = brand + " / " + display;
                lblQcLoaderStatus.ForeColor = Color.LightGreen;
                Log("[OK] Loader: " + brand + " → " + display + " (" + Path.GetFileName(fullPath) + ")", Color.LightGreen);
            }
            else
            {
                edlLoaderPath = "";
                SaveEdlPaths();
                lblQcLoaderStatus.Text = "file not found: " + Path.GetFileName(relPath);
                lblQcLoaderStatus.ForeColor = Color.OrangeRed;
                Log("[!] Loader file not found: " + fullPath, Color.OrangeRed);
            }
        }

    }
}
