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
        // ================= STOP TASK HANDLER =================
        private void BtnStopTask_Click(object sender, EventArgs e)
        {
            try
            {
                if (isTaskRunning || flashWorkflowRunning)
                {
                stopRequested = true;
                // MTK: cancel အမြဲပို့ — engine က DA phase မတိုင်ခင် (searching/handshake) သတ်မယ်;
                // flash writing စပြီးပြီဆို current step ပြီးအောင်ဆက် (mid-write kill က partition ပျက်နိုင်)
                mediaTekCancel?.Cancel();
                if (mediaTekWriteActive)
                {
                    Log("[!] Stop requested — aborting before flash begins; if a write already started it will finish the current step.", Color.Orange);
                    return;
                }
                var process = activeProcess;
                try { if (process != null && !process.HasExited) process.Kill(true); }
                catch (InvalidOperationException) { }
                // RunQuickAsync processes (adb wait-for-device etc.) ကိုပါ ရပ်
                try { ReviewSafety.KillAllQuick(); } catch { }
                Log("[!] Operation Stopped by User.", Color.OrangeRed);
                SetProgress(0, "Stopped", "0 MB/s");
                }
                else
                {
                    Log("[*] No active background task is running.", Color.Gray);
                }
            }
            catch (Exception ex)
            {
                Log("Stop Error: " + ex.Message, Color.Red);
            }
        }

        // Log ကို ဖိုင်အဖြစ် save တယ်
        private void BtnExportLog_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(rtbLog.Text))
            {
                Log("[!] Log is empty - nothing to export.", Color.Orange);
                return;
            }

            using (SaveFileDialog sfd = new SaveFileDialog
            {
                Filter = "Log file (*.log;*.txt)|*.log;*.txt|All Files (*.*)|*.*",
                FileName = "pmk_log_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log",
                Title = "Export Log"
            })
            {
                if (sfd.ShowDialog() != DialogResult.OK) return;
                try
                {
                    // export မှာ identifier (IMEI/serial/hex id) တွေ ပါမသွားအောင် redact
                    string redacted = MtkFirmware.LogRedactor.Redact(rtbLog.Text);
                    File.WriteAllText(sfd.FileName, redacted, Encoding.UTF8);
                    bool changed = !string.Equals(redacted, rtbLog.Text, StringComparison.Ordinal);
                    Log("[OK] Log exported: " + sfd.FileName +
                        (changed ? " (identifiers redacted)" : ""), Color.LightGreen);
                }
                catch (Exception ex)
                {
                    Log("[x] Log export failed: " + ex.Message, Color.Red);
                }
            }
        }

        // Partition နာမည် command-injection မဖြစ်အောင် စစ်တယ် (a-z A-Z 0-9 _ - ပဲ ခွင့်ပြု)
        private void ClearLogForNewOp()
        {
            if (rtbLog == null || rtbLog.IsDisposed || !rtbLog.IsHandleCreated) return;
            if (rtbLog.InvokeRequired)
            {
                try { rtbLog.Invoke((Action)ClearLogForNewOp); } catch { }
                return;
            }
            rtbLog.Clear();
        }

        // null/handle-မရှိ control ကို tooltip ပေးရင် crash မဖြစ်အောင် (9/23 startup crash ×4)
        private void Tip(Control control, string text)
        {
            if (control == null || control.IsDisposed || toolTipMain == null) return;
            try { toolTipMain.SetToolTip(control, text); } catch { }
        }

        // Consolas မှာ char အရေအတွက် → pixel (hanging indent က pixel ယူတယ်)
        private int LogIndentPx(int chars) =>
            TextRenderer.MeasureText(new string('0', Math.Max(chars, 1)), rtbLog.Font).Width;

        // နောက်ဆုံးမှာ လိုင်းအလွတ် ရှိပြီးသားလား (block တွေ ကပ်မနေရ)
        private static bool EndsWithBlankLine(string text) =>
            text.EndsWith("\n\n", StringComparison.Ordinal) ||
            text.EndsWith("\r\n\r\n", StringComparison.Ordinal) ||
            text.EndsWith("\n\r\n", StringComparison.Ordinal);

        private void Log(string message, Color? color = null)
        {
            // form/log box ပိတ်ပြီးသား သို့ handle မတည်ရသေးရင် thread-pool
            // output က crash မဖြစ်အောင် ကာတယ် (9/30 ObjectDisposedException ×2)
            if (rtbLog == null || rtbLog.IsDisposed || !rtbLog.IsHandleCreated) return;
            try
            {
                if (rtbLog.InvokeRequired)
                {
                    rtbLog.Invoke(new Action(() => Log(message, color)));
                    return;
                }

                rtbLog.SelectionStart = rtbLog.TextLength;
                rtbLog.SelectionLength = 0;
                Color body = color ?? Color.FromArgb(0, 255, 128);
                // ရှည်တဲ့စာကြောင်း ကွေးကျတဲ့အခါ ဒုတိယစာကြောင်းတွေကို စာသားစတဲ့နေရာအောက် တည့်တည့်ချ
                // (Paragraph indent က တစ်ကြောင်းချင်းပဲ သက်ရောက်လို့ line တစ်ကြောင်းချင်း ထည့်တယ်)
                string[] lines = (message ?? "").Replace("\r\n", "\n").Split('\n');
                bool block = lines.Length > 1;
                if (block && rtbLog.TextLength > 0 && !EndsWithBlankLine(rtbLog.Text))
                    rtbLog.AppendText("\r\n");   // block စမယ့် အပေါ်မှာ လိုင်းအလွတ် ခြား
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    int lead = line.Length - line.TrimStart(' ').Length;
                    // ခေါင်းစဉ် ပြီးနောက် အသေးစိတ်လိုင်းတွေကို ခပ်မှိန်မှိန် — block တစ်ခုကို အလျင်အမြန် ဖတ်ရအောင်
                    Color lineColor = block && i > 0
                        ? Color.FromArgb(body.R * 85 / 100, body.G * 85 / 100, body.B * 85 / 100)
                        : body;
                    rtbLog.SelectionHangingIndent = LogIndentPx(line.StartsWith("  * ") ? lead + 2 : Math.Max(lead, 2));
                    if (line.StartsWith("  * "))
                    {
                        rtbLog.SelectionColor = Color.FromArgb(110, 128, 146);   // bullet ကို မှိန်ထား
                        rtbLog.AppendText("  * ");
                        rtbLog.SelectionColor = lineColor;
                        rtbLog.AppendText(line.Substring(4) + "\r\n");
                    }
                    else
                    {
                        rtbLog.SelectionColor = lineColor;
                        rtbLog.AppendText(line + "\r\n");
                    }
                    rtbLog.SelectionHangingIndent = 0;
                }
                if (block) rtbLog.AppendText("\r\n");   // block ပြီးရင် အောက်ကစာနဲ့ ခြား
                rtbLog.SelectionColor = rtbLog.ForeColor;
                rtbLog.ScrollToCaret();
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private void LogPair(string label, string value, Color labelColor, Color valueColor, bool boldValue = true)
        {
            if (rtbLog == null || rtbLog.IsDisposed || !rtbLog.IsHandleCreated) return;
            try
            {
                if (rtbLog.InvokeRequired)
                {
                    rtbLog.Invoke(new Action(() => LogPair(label, value, labelColor, valueColor, boldValue)));
                    return;
                }

                rtbLog.SelectionStart = rtbLog.TextLength;
                rtbLog.SelectionLength = 0;
                rtbLog.SelectionColor = labelColor;
                rtbLog.AppendText(label);
                // value က ရှည်လို့ ကွေးကျရင် ဒုတိယစာကြောင်းတွေ value column အောက် တည့်တည့် ကျမယ်
                rtbLog.SelectionHangingIndent = LogIndentPx(label.Length);
                rtbLog.SelectionColor = valueColor;
                if (boldValue)
                {
                    using (Font bold = new Font(rtbLog.Font, FontStyle.Bold))
                    {
                        rtbLog.SelectionFont = bold;
                        rtbLog.AppendText(value + "\r\n");
                        rtbLog.SelectionFont = rtbLog.Font;
                    }
                }
                else
                {
                    rtbLog.AppendText(value + "\r\n");
                }
                rtbLog.SelectionHangingIndent = 0;
                rtbLog.SelectionColor = rtbLog.ForeColor;
                rtbLog.ScrollToCaret();
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        /// <summary>
        /// One block for a structured result: the header names the run, and every fact under it lines up in two
        /// columns with its own colour. A run that ends in twelve separate sentences reads as twelve events;
        /// this reads as one answer.
        /// </summary>
        private void LogFactBlock(string header, Color headerColor,
            IEnumerable<(string Label, string Value, Color ValueColor)> facts)
        {
            if (rtbLog == null || rtbLog.IsDisposed || !rtbLog.IsHandleCreated) return;
            try
            {
                if (rtbLog.InvokeRequired)
                {
                    var copy = facts.ToList();
                    rtbLog.Invoke(new Action(() => LogFactBlock(header, headerColor, copy)));
                    return;
                }

                if (rtbLog.TextLength > 0 && !EndsWithBlankLine(rtbLog.Text))
                    rtbLog.AppendText("\r\n");   // block တွေ ကပ်မနေရ
                rtbLog.SelectionColor = headerColor;
                rtbLog.AppendText(header + "\r\n");
                foreach (var (label, value, valueColor) in facts)
                {
                    rtbLog.SelectionColor = Color.FromArgb(110, 120, 136);
                    rtbLog.AppendText(label.PadRight(10));
                    rtbLog.SelectionHangingIndent = LogIndentPx(10);
                    rtbLog.SelectionColor = valueColor;
                    rtbLog.AppendText(value + "\r\n");
                    rtbLog.SelectionHangingIndent = 0;
                }
                rtbLog.SelectionColor = rtbLog.ForeColor;
                rtbLog.ScrollToCaret();
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        // စာကြောင်းတူကို ခဏအတွင်း ထပ်မရေး — mtkclient က DA/bypass marker ၂ကြိမ်
        // ပို့လို့ "[OK] Bypassing authentication" / "Sending Download-Agent... OK" လိုမျိုး
        // ၂ခါ မထွက်အောင် (false = ဒီစာကြောင်းကို ကျော်လိုက်တယ်)။ ကြားက စာကြောင်း
        // ပါ/မပါ မသက်ဆိုင် — message တူ window အတွင်းဆို ကျော်တယ်
        private bool LogOnce(string message, Color? color = null, double windowSec = 20)
        {
            long now = DateTime.UtcNow.Ticks;
            if (lastLogUtc.Count > 64)   // အဟောင်းတွေ prune (dictionary မကြီးအောင်)
            {
                List<string> stale = null;
                foreach (var kv in lastLogUtc)
                    if (now - kv.Value.Ticks > TimeSpan.TicksPerMinute * 2)
                        (stale ?? (stale = new List<string>())).Add(kv.Key);
                if (stale != null) foreach (string k in stale) lastLogUtc.Remove(k);
            }
            DateTime prev;
            if (lastLogUtc.TryGetValue(message, out prev) &&
                now - prev.Ticks < (long)(windowSec * TimeSpan.TicksPerSecond)) return false;
            lastLogUtc[message] = DateTime.UtcNow;
            Log(message, color);
            return true;
        }

        private string FormatBytes(string hexLength)
        {
            try
            {
                long bytes = long.Parse(hexLength.Replace("0x", ""), NumberStyles.HexNumber);
                return FormatBytesLong(bytes);
            }
            catch
            {
                return hexLength;
            }
        }

        private string FormatBytesLong(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024)
                return ((double)bytes / (1024L * 1024 * 1024)).ToString("F2") + " GB";
            if (bytes >= 1024L * 1024)
                return ((double)bytes / (1024L * 1024)).ToString("F2") + " MB";
            if (bytes >= 1024L)
                return (bytes / 1024.0).ToString("F2") + " KB";
            return bytes.ToString() + " B";
        }

    }
}
