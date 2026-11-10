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
        private async Task<bool> ExecuteCommandCleanAsync(string fileName, string arguments, string taskTitle,
            bool clearPartitions = false, bool showRawOutput = false, bool quiet = false,
            Func<List<string>, bool> tolerateRaw = null, int timeoutSec = 0, bool timeoutMeansSuccess = false,
            bool quietFail = false)
        {
            try { arguments = await BindDeviceArgumentsAsync(fileName, arguments); }
            catch (Exception ex)
            {
                workflowFailed = true;
                if (rebootWaitActive) return false;
                // Temp Root flow: device ပျောက် → reconnect တစ်ကြိမ် စောင့်၊ မရရင် abort (silent)
                if (tempRootFlowActive && ex.Message.IndexOf("not connected", StringComparison.Ordinal) >= 0)
                {
                    if (!await TryRecoverTempRootDeviceAsync()) return false;
                    try { arguments = await BindDeviceArgumentsAsync(fileName, arguments); }
                    catch (Exception ex2)
                    {
                        tempRootAborted = true;
                        Log("[FAIL] " + ex2.Message, Color.Red);
                        return false;
                    }
                }
                else
                {
                    Log("[FAIL] " + ex.Message, Color.Red);
                    return false;
                }
            }
            bool ok = await ExecuteCommandCoreAsync(fileName, arguments, taskTitle, clearPartitions, showRawOutput, quiet, tolerateRaw, timeoutSec, timeoutMeansSuccess, quietFail);
            if (flashWorkflowContext.Value && !ok) workflowFailed = true;
            return ok;
        }

        private async Task<bool> ExecuteCommandCoreAsync(string fileName, string arguments, string taskTitle,
            bool clearPartitions, bool showRawOutput, bool quiet, Func<List<string>, bool> tolerateRaw = null,
            int timeoutSec = 0, bool timeoutMeansSuccess = false, bool quietFail = false)
        {
            if (isTaskRunning || (flashWorkflowRunning && !flashWorkflowContext.Value))
            {
                Log("[!] Another operation is still running - wait for it to finish or press STOP.", Color.OrangeRed);
                return false;
            }

            if (flashWorkflowContext.Value && stopRequested) return false;
            isTaskRunning = true;
            if (!flashWorkflowContext.Value) stopRequested = false;
            currentTaskTitle = taskTitle;
            // row တွေ snapshot ယူပြီး clear — command က row အသစ် မပြန်ရင် ပြန်ထိုးပေးတယ်
            // (flash/preflight fail ဖြစ်ရင် ယခင် partition list ပျောက်မသွားစေရ)
            List<PartitionMeta> partitionSnapshot = clearPartitions ? new List<PartitionMeta>(loadedPartitions) : null;
            if (clearPartitions) ClearPartitionData();

            void RestorePartitionRows()
            {
                if (partitionSnapshot == null || partitionSnapshot.Count == 0 || loadedPartitions.Count > 0) return;
                foreach (PartitionMeta pm in partitionSnapshot) AddPartitionRow(pm.Name, pm.Offset, pm.LengthHex);
                partitionSnapshot = null;
                Log("[i] No row from GPT/size — showing previous partition list.", Color.Gray);
            }
            if (flashWorkflowContext.Value) workflowDidOp = true;

            if (!quiet)
            {
                Log("========================================================", Color.FromArgb(0, 180, 255));
                Log("           PMK TOOL - " + taskTitle.ToUpper() + "             ", Color.White);
                Log("========================================================", Color.FromArgb(0, 180, 255));
                Log("[*] Initializing Task...", Color.Orange);
            }
            SetProgress(10, "Initializing");

            int exitCode = -1;
            bool timedOut = false;
            string failure = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Raw output ကို ဖမ်းထားတယ် — parser က unknown line တွေကို ဖျောက်တာမို့
            // ပြဿနာ (native crash / error message) တွေ့ရင် အဲဒီ line တွေ ပြန်ပြဖို့နဲ့
            // ဖိုင်ထဲ သိမ်းဖို့ လိုတယ် (mtkclient ရဲ့ error line တွေက mapped list မှာ မပါဘူး)။
            object rawLock = new object();
            List<string> rawTail = new List<string>();
            string rawLogPath = Path.Combine(Path.GetTempPath(), "pmk_last_command.log");

            try
            {
                await Task.Run(() =>
                {
                    try
                    {
                        ProcessStartInfo psi = new ProcessStartInfo
                        {
                            FileName = ResolveToolPath(fileName),
                            Arguments = arguments,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            // Python child processes ကို UTF-8 နဲ့ ဖတ်တယ် — mtkclient ရဲ့ output ထဲမှာ
                            // non-ASCII character ပါလာရင် OEM codepage နဲ့ ဖတ်ရင် ပျက်တတ်တယ်။
                            StandardOutputEncoding = Encoding.UTF8,
                            StandardErrorEncoding = Encoding.UTF8
                        };

                        using (Process process = new Process { StartInfo = psi })
                        {
                            activeProcess = process;

                            Action<string, bool> onData = (line, isErr) =>
                            {
                                lock (rawLock)
                                {
                                    rawTail.Add((isErr ? "[err] " : "") + line);
                                    if (rawTail.Count > 3000) rawTail.RemoveAt(0);
                                }

                                // ========== Commercial Tool Log Style Formatting ==========
                                bool handled = false;

                                if (line.Contains("In handleProgram('"))
                                {
                                    int start = line.IndexOf("In handleProgram('") + 18;
                                    int end = line.IndexOf("')", start);
                                    if (start > 17 && end > start)
                                    {
                                        string fileNameOnly = line.Substring(start, end - start);
                                        // MST style: Flashing [ file ] -> [ PART ] (size) ... Ok
                                        string target = DequeueFlashTarget(fileNameOnly);
                                        string targetPart = target.Length > 0 ? $" -> [ {target} ]" : "";
                                        Log($" Flashing [ {fileNameOnly} ]{targetPart}{FlashSizeSuffix(fileNameOnly)} ... Ok", Color.White);

                                        // Speed: ပြီးသား file တွေရဲ့ bytes / elapsed (file တစ်ခုချင်းစီ ပြီးတိုင်း update)
                                        if (flashSpeedWatch != null && flashSpeedWatch.Elapsed.TotalSeconds > 0.3)
                                        {
                                            flashBytesDone += flashCurrentFileSize;
                                            flashCurrentFileSize = FlashFileSize(fileNameOnly);
                                            UpdateFlashSpeed(flashBytesDone / 1048576.0 / flashSpeedWatch.Elapsed.TotalSeconds);
                                        }
                                    }
                                    handled = true;
                                }
                                // ၂။ Patching လုပ်သည့် အပိုင်းကို ဖမ်းပြခြင်း
                                else if (line.Contains("In handlePatch("))
                                {
                                    Log(" Patching patch0.xml ... Ok", Color.LightGreen);
                                    handled = true;
                                }
                                // ၃။ Percent ကို Textbox ထဲ မထည့်ဘဲ ProgressBar သို့သာ တိုက်ရိုက် Update လုပ်ခြင်း
                                //     speed label ကို မထိ — handleProgram line တွေက bytes speed ပေးတယ်
                                else if (line.Contains("percent files transferred"))
                                {
                                    Match m = Regex.Match(line, @"(\d+(?:\.\d+)?)\s*%");
                                    if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double p))
                                    {
                                        SetProgress((int)p, "Flashing");
                                    }
                                    handled = true;
                                }
                                // ၄။ Device Information ပိုင်း ဖမ်းပြခြင်း
                                else if (line.Contains("TARGET SAID:") && line.Contains("storage_type"))
                                {
                                    Log(" Connecting to firehose mode... Ok", Color.LightGreen);
                                    handled = true;
                                }
                                // ==========================================================

                                // chain က စီမံပြီးသား line တွေကို parser ပြန်မထိ (Transferring နဲ့ 0 speed ပြန်မတက်အောင်)
                                if (!handled) handled = ParseCleanLogAndProgress(line);

                                // ပုံမှန် raw output ရှုပ်ရှုပ်တွေကို မပြတော့ပါ
                                // ဒါပေမဲ့ ERROR စာကြောင်းတွေကို showRawOutput ပိတ်ထားရင်တောင် အနီရောင်နဲ့ ပြ
                                // (fh_loader က file write fail ဖြစ်လည်း exit 0 ပြန်တတ်တယ် — log မှာ မမြင်ရဘဲ ကျန်ခဲ့ရင် bootloop ရှာရခက်)
                                if (!handled && !string.IsNullOrWhiteSpace(line))
                                {
                                    if (showRawOutput)
                                        Log("    " + line, Color.FromArgb(150, 150, 150));
                                    else if (!quietFail && (line.Contains("ERROR") || line.Contains("FAILED") ||
                                             line.Contains("Read 0 bytes") || line.Contains("GetFile:")))
                                        Log("    " + line, Color.OrangeRed);
                                }
                            };

                            process.OutputDataReceived += (s, args) => { if (args.Data != null) onData(args.Data, false); };
                            process.ErrorDataReceived += (s, args) => { if (args.Data != null) onData(args.Data, true); };

                            if (stopRequested) return;
                            process.Start();
                            if (stopRequested && !mediaTekWriteActive) { try { process.Kill(true); } catch (InvalidOperationException) { } }
                            process.BeginOutputReadLine();
                            process.BeginErrorReadLine();
                            // timeoutSec > 0 ဆို child process ကို စောင့်ပြီး ကြာလွန်းရင် kill —
                            // (edl.py/mtkclient က device reboot ပြီးမှ hang နေတတ်တယ် → UI အမြဲတမ်း lock)
                            if (mediaTekWriteActive)
                            {
                                // Backend checks the cancellation file between partitions. Never kill a write.
                                process.WaitForExit();
                            }
                            else if (timeoutSec > 0)
                            {
                                if (!process.WaitForExit(timeoutSec * 1000))
                                {
                                    timedOut = true;
                                    try { process.Kill(entireProcessTree: true); } catch { }
                                    try { process.WaitForExit(5000); } catch { }
                                }
                                else
                                {
                                    // WaitForExit(int) က async output drain မစောင်း — process ထွက်ပြီး
                                    // pipe ထဲ ကျန်နေတဲ့ tail line တွေ (PMK_OK စသည်) ဆုံးမသွားအောင်
                                    try { Thread.Sleep(300); } catch { }
                                }
                            }
                            else
                            {
                                // timeoutSec=0 = unlimited — ဒါပေမဲ့ hang child အတွက် 6 နာရီ safety cap ထား
                                // (STOP က activeProcess ကို kill လို့ ချက်ချင်းထွက်တယ်; legit flash/backup က ဒါထက် မကြာဘူး)
                                if (!process.WaitForExit(6 * 60 * 60 * 1000))
                                {
                                    timedOut = true;
                                    try { process.Kill(entireProcessTree: true); } catch { }
                                    try { process.WaitForExit(5000); } catch { }
                                }
                                else
                                {
                                    try { Thread.Sleep(300); } catch { }
                                }
                            }
                            exitCode = process.ExitCode;
                        }
                    }
                    catch (Exception ex)
                    {
                        failure = ex.Message;
                    }
                    finally
                    {
                        activeProcess = null;
                    }
                });
            }
            finally
            {
                isTaskRunning = false;
            }

            sw.Stop();
            if (!quiet)
                Log("--------------------------------------------------------", Color.FromArgb(0, 180, 255));

            // ဒီ command ရဲ့ raw output အားလုံးကို ဖိုင်ထဲ သိမ်းတယ် (debug အတွက်)။
            List<string> rawSnapshot;
            lock (rawLock) { rawSnapshot = new List<string>(rawTail); }
            try
            {
                List<string> rawFile = new List<string>();
                rawFile.Add("=== " + taskTitle + " | " + fileName + " " + arguments);
                rawFile.Add("");
                rawFile.AddRange(rawSnapshot);
                File.WriteAllLines(rawLogPath, rawFile);
                // tool အလိုက် သီးသန့် copy ထား — နောက် command (reboot/edl.py) က
                // pmk_last_command.log ကို overwrite လုပ်လိုက်ရင် fh_loader output ပျောက်မသွားအောင်
                string toolStem = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
                foreach (char c in Path.GetInvalidFileNameChars()) toolStem = toolStem.Replace(c, '_');
                if (toolStem.Length > 0)
                    File.WriteAllLines(Path.Combine(Path.GetTempPath(), "pmk_last_" + toolStem + ".log"), rawFile);
            }
            catch (Exception ex)
            {
                Log("[!] Raw log write failed: " + ex.Message, Color.Orange);
            }

            // မအောင်မြင်ရင် raw line နောက်ဆုံး 30 လိုင်း + log ဖိုင် path ကို ပြထားတယ်။
            void DumpRawOutput(string note)
            {
                Log(note, Color.Gray);
                int from = Math.Max(0, rawSnapshot.Count - 30);
                for (int i = from; i < rawSnapshot.Count; i++)
                    Log("   " + rawSnapshot[i], Color.FromArgb(150, 150, 150));
                Log("   (full raw log: " + rawLogPath + ")", Color.Gray);
            }

            // Native crash (access violation) လို exit code တွေကို ဘာသာပြန်ပြတယ်။
            string ExitCodeNote(int code)
            {
                switch (code)
                {
                    case -1073741819: return " (0xC0000005 = ACCESS_VIOLATION — libusb/USB driver အဆင့်မှာ crash)";
                    case 24: return " (COM port မဖွင့်နိုင်ဘူး — port busy/stuck)";
                    case 20: return " (mtkclient error marker — raw log ထဲက error line ကို ကြည့်ပါ)";
                    case -1073740791: return " (0xC0000409 = stack buffer overrun)";
                    case -1073741510: return " (0xC000013A = process ကို ရပ်လိုက်တာ)";
                    case -1073740940: return " (0xC0000374 = heap corruption)";
                    default: return "";
                }
            }

            if (failure != null)
            {
                RestorePartitionRows();
                SetProgress(0, "Failed", "0 MB/s");
                if (!quietFail)
                {
                    Log("[✘] " + taskTitle + " could not be started: " + failure, Color.Red);
                    Log("[*] Check that " + fileName + " is installed and reachable.", Color.Orange);
                    if (rawSnapshot.Count > 0) DumpRawOutput("[*] Raw command output (last 30 lines):");
                }
                if (!quiet) Log("========================================================", Color.FromArgb(0, 180, 255));
                LogHistory(taskTitle + " (could not start)", false, Math.Round(sw.Elapsed.TotalSeconds));
                return false;
            }

            if (stopRequested)
            {
                RestorePartitionRows();
                Log("[!] " + taskTitle + " was stopped before it finished.", Color.OrangeRed);
                if (!quiet) Log("========================================================", Color.FromArgb(0, 180, 255));
                LogHistory(taskTitle + " (stopped)", false, Math.Round(sw.Elapsed.TotalSeconds));
                return false;
            }

            // timeout ဖြစ်ပေမဲ့ caller က "device drop = done" သတ်မှတ်ထားတဲ့ command (auto reboot) → success
            if (timedOut && timeoutMeansSuccess)
            {
                RestorePartitionRows();
                SetProgress(100, "Completed", "Done");
                Log("[i] " + taskTitle + " — " + timeoutSec + "s timeout; process killed (device rebooted/dropped, treated as OK).", Color.Gray);
                LogHistory(taskTitle + " (timeout)", true, Math.Round(sw.Elapsed.TotalSeconds));
                return true;
            }

            if (exitCode == 0)
            {
                RestorePartitionRows();
                SetProgress(100, "Completed", "Done");
                if (!quiet)
                {
                    Log("[OK] " + taskTitle + " - Completed Successfully!", Color.LightGreen);
                    Log("Elapsed time : " + Math.Round(sw.Elapsed.TotalSeconds) + " seconds", Color.Gray);
                    Log("========================================================", Color.FromArgb(0, 180, 255));
                }
                LogHistory(taskTitle, true, Math.Round(sw.Elapsed.TotalSeconds));
                return true;
            }

            // fail ဖြစ်ပေမဲ့ caller က tolerate လုပ်ထားတဲ့ error pattern (ဥပမာ partition မရှိ) → skip အဖြစ် သတ်မှတ်
            if (exitCode != 0 && tolerateRaw != null && tolerateRaw(rawSnapshot))
            {
                RestorePartitionRows();
                SetProgress(100, "Skipped", "Done");
                Log("[i] " + taskTitle + " — expected condition (partition not present), skipped (OK).", Color.Gray);
                if (!quiet)
                {
                    Log("Elapsed time : " + Math.Round(sw.Elapsed.TotalSeconds) + " seconds", Color.Gray);
                    Log("========================================================", Color.FromArgb(0, 180, 255));
                }
                LogHistory(taskTitle + " (skipped)", true, Math.Round(sw.Elapsed.TotalSeconds));
                return true;
            }

            if (timedOut)
            {
                RestorePartitionRows();
                SetProgress(0, "Failed", "0 MB/s");
                if (!quietFail)
                {
                    Log("[FAIL] " + taskTitle + " — timed out after " + (timeoutSec > 0 ? timeoutSec + "s" : "6h hard cap") + " (process killed).", Color.OrangeRed);
                    DumpRawOutput("[*] Raw command output (last 30 lines):");
                    // wait-loop (BROM device မတွေ့) ကြောင့် ပိတ်သွားတာဆိုရင် နောက်တစ်ခါ
                    // ဘာလုပ်ရမလဲ ရှင်းရှင်းလင်းလင်း ပြန်ပြတယ် (raw hint တွေကို parser က ဖျက်ထားလို့)။
                    string timedOutRaw = string.Join("\n", rawSnapshot);
                    if (timedOutRaw.Contains("PreLoader VCOM") || timedOutRaw.Contains("Handshake failed") ||
                        timedOutRaw.Contains("BROM") || timedOutRaw.Contains("preloader"))
                        Log("[i] Recovery: power off the phone (~10s), hold Vol+ & Vol- and replug USB — mtkclient only connects in BROM mode.", Color.Cyan);
                }
                if (!quiet)
                {
                    Log("Elapsed time : " + Math.Round(sw.Elapsed.TotalSeconds) + " seconds", Color.Gray);
                    Log("========================================================", Color.FromArgb(0, 180, 255));
                }
                LogHistory(taskTitle, false, Math.Round(sw.Elapsed.TotalSeconds));
                return false;
            }

            RestorePartitionRows();
            SetProgress(0, "Failed", "0 MB/s");
            if (!quietFail)
            {
                Log("[FAIL] " + taskTitle + " (exit code " + exitCode + ")" + ExitCodeNote(exitCode), Color.OrangeRed);
                DumpRawOutput("[*] Raw command output (last 30 lines):");
                LogRecoveryHints(taskTitle, exitCode, rawSnapshot);
            }
            if (!quiet)
            {
                Log("Elapsed time : " + Math.Round(sw.Elapsed.TotalSeconds) + " seconds", Color.Gray);
                Log("========================================================", Color.FromArgb(0, 180, 255));
            }
            LogHistory(taskTitle, false, Math.Round(sw.Elapsed.TotalSeconds));
            return false;
        }

        // ================= OPERATION HISTORY (timestamped) =================
        private string HistoryFile
        {
            get { return ShopServices.DataPath("pmk_history.log"); }
        }

        private void LogHistory(string operation, bool success, double seconds)
        {
            try
            {
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\t" +
                              (success ? "OK  " : "FAIL") + "\t" +
                              Math.Round(seconds) + "s\t" + operation;
                File.AppendAllText(HistoryFile, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("LogHistory failed: " + ex.Message);
            }
        }

        // ================= ERROR RECOVERY HINTS =================
        private void LogRecoveryHints(string taskTitle, int exitCode, List<string> rawTail)
        {
            string joined = string.Join("\n", rawTail ?? new List<string>());
            // The terminal firmware guard error takes precedence over earlier,
            // recovered loader warnings from the same connection attempt.
            string firmwareError = (rawTail ?? new List<string>()).LastOrDefault(line =>
                line.Contains("PMK_ERROR: Firmware preflight blocked:") ||
                line.Contains("PMK_ERROR: Firmware sparse preparation/write failed:") ||
                line.Contains("PMK_ERROR: Firmware write failed:"));
            if (firmwareError != null)
            {
                Log("[i] Recovery: " + firmwareError.Trim(), Color.Cyan);
                return;
            }
            bool hint = false;

            void Hint(string msg)
            {
                if (hint) return;
                hint = true;
                Log("[i] Recovery: " + msg, Color.Cyan);
            }

            // COM port open failure — အတွင်းဆုံး (specific) အနေနဲ့ အပေါ်ဆုံးမှာ ထားရတယ်
            // (raw tail ထဲမှာ "not found"/"BROM" စာသားတွေလည်း ပါတတ်လို့)။
            if (exitCode == 24 || joined.Contains("could not open port") ||
                joined.Contains("parameter is incorrect"))
                Hint("COM port ကို Windows မဖွင့်ပေးဘူး (busy/stuck) — SP Flash Tool / UnlockTool စတဲ့ အခြား tool တွေပိတ်ပါ။ မပြောင်းရင် Device Manager မှာ MediaTek USB Port device ကို disable → enable လုပ်ပြီး ဖုန်းကို power-cycle/replug ပြန်စမ်းပါ။");
            else if (joined.Contains("PMK_NEED_USBDK") || joined.Contains("is UsbDk installed"))
                Hint("Raw USB access (UsbDk) လိုအပ်နေတယ် — MTK Auth Bypass ကို ထပ်နှိပ်ပါ (UsbDk driver တင်ဖို့ prompt တက်ရင် Yes နှိပ်)၊ ပြီးရင် ဖုန်းကို replug ပြီး retry လုပ်ပါ။");
            else if (joined.Contains("ACCESS_VIOLATION") || exitCode == -1073741819)
                Hint("USB driver / libusb crash — USB port ပြောင်း၊ cable တို/မကောင်း စစ်၊ WinUSB filter reinstall စမ်းပါ။");
            else if (joined.Contains("No preloader given") || joined.Contains("missing dram setup"))
                Hint("Preloader (DRAM/EMI config) မပါလို့ DA stage 2 တက်ရာမှာ ဖုန်းက USB ပြတ်သွားတယ် — ဒီဖုန်းရဲ့ firmware ထဲက preloader_<board>_*.bin ကို MTK tab ရဲ့ Preloader picker မှာ ရွေးပြီး ပြန်စမ်းပါ။");
            else if (joined.Contains("Cannot configure port") || joined.Contains("not functioning"))
                Hint("USB ချိတ်မှာ အလုပ်လုပ်နေစဉ် ပြတ်/ပြောင်းသွားတယ် — cable/hub စစ် (PC USB 2.0 port တိုက်ရိုက်သုံး)၊ဖုန်းကို power-off (~10s) လုပ်ပြီး Vol+ & Vol- နှိပ်ကာ BROM ပြန်သွင်း retry ပါ။");
            else if (joined.Contains("Permission") || joined.Contains("access denied") || exitCode == 5)
                Hint("Permission denied — tool ကို Run as administrator နဲ့ ဖွင့်ပါ သို့မဟုတ် antivirus က ပိတ်နေလား စစ်ပါ။");
            else if (joined.Contains("No such file") || joined.Contains("not found") || joined.Contains("找不到") || exitCode == 2)
                Hint("File/tool မတွေ့ပါ — bundled tool path နဲ့ firmware folder ပြန်စစ်ပါ။");
            else if (joined.Contains("BROM") || joined.Contains("preloader") || joined.Contains("handshake"))
                Hint("BROM handshake fail — ဖုန်းကို power off (~10s) လုပ်ပြီး Vol+ & Vol- နှိပ်ကာ USB ပြန်တပ်ပါ။");
            else if (joined.Contains("device not found") || joined.Contains("no device") || joined.Contains("Waiting for device"))
                Hint("Device မချိတ်မိပါ — cable/driver စစ်၊ Scan Port နှိပ်ပြီး ပြန်ကြိုးစားပါ။");
            else if (joined.Contains("verify") || joined.Contains("AVB") || joined.Contains("orange state"))
                Hint("AVB/verification — Orange State Fix သို့ DM Fix ကို စမ်းကြည့်ပါ (vbmeta disabled flags)။");
            else if (joined.Contains("userdata") || joined.Contains("encrypt") || joined.Contains("FRP"))
                Hint("Data/FRP lock — FRP Remove သို့ Userlock Reset ကို owner consent နဲ့ စမ်းပါ။");
            else if (exitCode != 0)
                Hint("Command failed — read raw log (pmk_last_command.log) and find the error line; if not, STOP → Scan Port → retry.");
        }

        // ================= PARTITION SIZE WARNING =================
        private bool WarnIfImageTooLarge(string partition, string filePath)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return true;
                long imgSize = new FileInfo(filePath).Length;
                var p = loadedPartitions.FirstOrDefault(x => x.Name.Equals(partition, StringComparison.OrdinalIgnoreCase));
                if (p == null) return true;
                long partSize;
                try { partSize = ReviewSafety.ParseSize(p.LengthHex); }
                catch { return true; }
                if (imgSize > partSize)
                {
                    string imgHuman = FormatBytesLong(imgSize);
                    string partHuman = FormatBytes(p.LengthHex);
                    Log("[!] Refusing to write " + partition + ": image " + imgHuman +
                        " is larger than the partition " + partHuman + " — it would overwrite neighbors.",
                        Color.OrangeRed);
                    ConfirmDialog.Show(
                        "Image က partition ထက် ကြီးနေတယ်!\n\n" +
                        "• Partition : " + partition + " (" + partHuman + ")\n" +
                        "• Image    : " + imgHuman + "\n\n" +
                        "ဒီအတိုင်း write ရင် data corruption / brick ဖြစ်နိုင်တယ်။\n" +
                        "ဒါကြောင့် မရေးပါဘူး — မှန်ကန်တဲ့ image ပြန်ရွေးပါ။",
                        "Partition Size Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
            }
            catch (Exception ex)
            {
                // size check မလုပ်နိုင်ရင် warn မပြဘဲ pass — log ထား
                Debug.WriteLine("WarnIfImageTooLarge: " + ex.Message);
            }
            return true;
        }

        // ================= DRIVER HELPER =================
        // Connected device VID တွေ ဖတ်ပြီး ဘယ် driver လို/လိုမလို ပြတယ်။
        // INF folder ပေးရင် pnputil နဲ့ install လုပ်ပေးတယ် (bundled INF မပါလည်း သုံးလို့ရ)။
        private void ShowDriverHelper()
        {
            using (var dlg = new Form())
            {
                dlg.Text = "Driver Helper";
                dlg.Size = new Size(720, 520);
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;
                // theme-aware (light: soft; dark: original)
                dlg.BackColor = lightTheme ? Color.FromArgb(222, 228, 240) : Color.FromArgb(18, 20, 24);
                dlg.ForeColor = lightTheme ? Color.FromArgb(35, 38, 48) : Color.White;

                var lblTitle = new Label
                {
                    Text = "USB / COM driver status — known VIDs (MediaTek, Qualcomm, Samsung, Unisoc, HiSilicon)",
                    Location = new Point(12, 10),
                    AutoSize = true,
                    ForeColor = lightTheme ? Color.FromArgb(58, 102, 208) : Color.FromArgb(86, 145, 250),
                    Font = new Font("Segoe UI", 9f, FontStyle.Bold)
                };

                var lst = new ListBox
                {
                    Location = new Point(12, 36),
                    Size = new Size(680, 200),
                    BackColor = lightTheme ? Color.FromArgb(243, 246, 252) : Color.FromArgb(38, 42, 50),
                    ForeColor = lightTheme ? Color.FromArgb(35, 38, 48) : Color.Gainsboro,
                    Font = new Font("Consolas", 9f),
                    BorderStyle = BorderStyle.FixedSingle
                };

                var outLog = new TextBox
                {
                    Location = new Point(12, 244),
                    Size = new Size(680, 160),
                    Multiline = true,
                    ScrollBars = ScrollBars.Vertical,
                    ReadOnly = true,
                    BackColor = lightTheme ? Color.FromArgb(240, 244, 251) : Color.FromArgb(24, 27, 33),
                    ForeColor = lightTheme ? Color.FromArgb(35, 38, 48) : Color.FromArgb(200, 220, 240),
                    Font = new Font("Consolas", 8.5f),
                    BorderStyle = BorderStyle.FixedSingle
                };

                var btnScan = Create3DButton("🔍 Scan USB Devices", 12, 414, 160, 34, ButtonTheme.Cyan);
                var btnInstall = Create3DButton("📦 Install INF Folder…", 180, 414, 170, 34, ButtonTheme.Green);
                var btnDevMgr = Create3DButton("⚙ Device Manager", 358, 414, 150, 34, ButtonTheme.Cyan);
                var btnClose = Create3DButton("Close", 610, 414, 82, 34, ButtonTheme.Orange);

                void Append(string line)
                {
                    if (outLog.TextLength > 0) outLog.AppendText(Environment.NewLine);
                    outLog.AppendText(line);
                }

                async void Scan()
                {
                    btnScan.Enabled = false;
                    lst.Items.Clear();
                    outLog.Clear();
                    Append("[*] Scanning PnP devices for known phone VIDs...");
                    try
                    {
                        // VID → friendly name map (unlock/flash modes)
                        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["0E8D"] = "MediaTek (BROM / Preloader / COM)",
                            ["05C6"] = "Qualcomm (EDL 9008 / DIAG)",
                            ["9008"] = "Qualcomm EDL 9008 (composite)",
                            ["04E8"] = "Samsung (Kies / Download / ADB)",
                            ["1F8A"] = "Unisoc / Spreadtrum (spd_dump)",
                            ["1782"] = "Unisoc / Spreadtrum (alt)",
                            ["12D1"] = "Huawei / HiSilicon (HUAWEI USB COM)",
                            ["2A96"] = "MediaTek (preloader alt)",
                            ["0BB4"] = "HTC / older MTK",
                            ["2717"] = "Xiaomi (ADB/Fastboot)",
                            ["18D1"] = "Google / generic ADB & Fastboot",
                            ["0FCE"] = "Sony",
                            ["1004"] = "LG",
                            ["0421"] = "Nokia (MTK/Unisoc)",
                            ["22B8"] = "Motorola",
                            ["0525"] = "Linux Function / RNDIS (some SPD)"
                        };

                        string ps =
                            "Get-PnpDevice -ErrorAction SilentlyContinue | " +
                            "Where-Object { $_.InstanceId -match 'VID_[0-9A-F]{4}' } | " +
                            "Select-Object -ExpandProperty InstanceId";
                        string res = await ExecuteCommandQuickAsync("powershell.exe",
                            "-NoProfile -Command \"" + ps.Replace("\"", "\\\"") + "\"");

                        var hits = new List<string>();
                        var seen = new HashSet<string>();
                        foreach (string line in (res ?? "").Split('\n'))
                        {
                            string id = line.Trim();
                            var m = Regex.Match(id, @"VID_([0-9A-Fa-f]{4})", RegexOptions.IgnoreCase);
                            if (!m.Success) continue;
                            string vid = m.Groups[1].Value.ToUpperInvariant();
                            if (!known.ContainsKey(vid)) continue;
                            string key = vid + "|" + id;
                            if (!seen.Add(key)) continue;
                            hits.Add(vid + "  " + known[vid]);
                            Append("  [FOUND] VID_" + vid + "  " + known[vid]);
                            Append("           " + id);
                        }

                        if (hits.Count == 0)
                        {
                            Append("[i] No known phone USB IDs present.");
                            Append("    Phone ချိတ်ပြီး Scan ထပ်နှိပ်ပါ — သို့မဟုတ် Driver ကြိုတင် install ထားပါ။");
                            lst.Items.Add("(no known phone VID connected)");
                        }
                        else
                        {
                            foreach (string h in hits.Distinct()) lst.Items.Add(h);
                            Append("[OK] " + hits.Count + " known device interface(s). If yellow ! in Dev Manager → install INF folder.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Append("[FAIL] Scan: " + ex.Message);
                    }
                    finally { btnScan.Enabled = true; }
                }

                btnScan.Click += (s, e) => Scan();
                btnDevMgr.Click += (s, e) =>
                {
                    Process.Start(new ProcessStartInfo { FileName = "devmgmt.msc", UseShellExecute = true });
                };
                btnInstall.Click += async (s, e) =>
                {
                    using (var fbd = new FolderBrowserDialog
                    {
                        Description = "Select folder containing driver .inf files (search recursive)"
                    })
                    {
                        if (fbd.ShowDialog() != DialogResult.OK) return;
                        btnInstall.Enabled = false;
                        Append("[*] pnputil /add-driver ... /install (recursive) — " + fbd.SelectedPath);
                        try
                        {
                            var psi = new ProcessStartInfo
                            {
                                FileName = "pnputil.exe",
                                Arguments = "/add-driver \"" + Path.Combine(fbd.SelectedPath, "*.inf") +
                                            "\" /subdirs /install",
                                UseShellExecute = false,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                                CreateNoWindow = true
                            };
                            using var p = Process.Start(psi);
                            // ReadToEnd/WaitForExit က unbounded — pnputil hang ရင် button အမြဲ disabled ဖြစ်မသွားအောင်300s cap
                            var pnOutTask = p.StandardOutput.ReadToEndAsync();
                            var pnErrTask = p.StandardError.ReadToEndAsync();
                            using (var pnCts = new CancellationTokenSource(TimeSpan.FromSeconds(300)))
                            {
                                try { await p.WaitForExitAsync(pnCts.Token); }
                                catch (OperationCanceledException)
                                {
                                    try { p.Kill(entireProcessTree: true); } catch { }
                                    Append("[!] pnputil — 300s timeout, process killed.");
                                    return;
                                }
                            }
                            string stdout = await pnOutTask;
                            string stderr = await pnErrTask;
                            foreach (string ln in (stdout + "\n" + stderr).Split('\n'))
                            {
                                string t = ln.Trim();
                                if (t.Length > 0) Append(t);
                            }
                            Append(p.ExitCode == 0
                                ? "[OK] pnputil finished (exit 0). Replug phone and Scan again."
                                : "[!] pnputil exit " + p.ExitCode + " — check log above (need admin for some drivers).");
                        }
                        catch (Exception ex)
                        {
                            Append("[FAIL] " + ex.Message);
                        }
                        finally { btnInstall.Enabled = true; }
                    }
                };
                btnClose.Click += (s, e) => dlg.Close();

                dlg.Controls.AddRange(new Control[] { lblTitle, lst, outLog, btnScan, btnInstall, btnDevMgr, btnClose });
                dlg.Shown += (s, e) => Scan();
                dlg.ShowDialog(this);
            }
        }

        // ================= DEVICE MODEL WHITELIST =================
        private bool IsKnownModel(string model)
        {
            if (string.IsNullOrWhiteSpace(model)) return true;
            EnsureDeviceDbLoaded();
            if (deviceDb == null) return true;
            foreach (var list in deviceDb.Values)
                foreach (var dp in list)
                    if (!string.IsNullOrEmpty(dp.Model) &&
                        dp.Model.IndexOf(model, StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
            return false;
        }

        private void WarnUnknownModel(string model)
        {
            if (string.IsNullOrWhiteSpace(model) || IsKnownModel(model)) return;
            if (!warnedModels.Add(model)) return;   // တစ်ခုတည်း တစ်ခါပဲ warn
            Log("[!] Model not in known database: " + model + " — double-check before unlock/flash.", Color.Orange);
        }

    }
}
