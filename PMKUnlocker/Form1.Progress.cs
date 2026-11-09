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
        // Window က fixed-size မို့ မျက်နှာပြင် သေးတဲ့ PC (သို့) DPI ကြီးတဲ့ PC တွေမှာ
        // အောက်ပိုင်း ဖြတ်မခံရအောင် တစ်ချိုးကျ ကျုံ့ပေးတယ် (ခလုတ်/စာသား အားလုံး လိုက်ကျုံ့)။
        private void FitToScreen()
        {
            try
            {
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                float availW = wa.Width - 16f;
                float availH = wa.Height - 48f;      // taskbar + အနားလွတ်
                float scale = Math.Min(availW / this.Width, availH / this.Height);

                if (scale < 0.99f)
                {
                    scale = Math.Max(scale, 0.5f);
                    this.Scale(new SizeF(scale, scale));
                    Log("[*] Window (" + wa.Width + "x" + wa.Height + ") - scaling to fit " +
                        (int)Math.Round(scale * 100) + "% (if text looks too small, check the Windows display scale).",
                        Color.Orange);
                }
            }
            catch (Exception ex)
            {
                Log("Screen fit error: " + ex.Message, Color.Red);
            }
        }

        // ================= PROGRESS BAR CONTROLLER =================
        private void SetProgress(int percent, string status, string speed = "")
        {
            // output thread က app ပိတ်ပြီးဆုံးချိန် call ရင် crash မဖြစ်အောင်
            if (panelProgress == null || panelProgress.IsDisposed || !panelProgress.IsHandleCreated) return;
            try
            {
                if (panelProgress.InvokeRequired)
                {
                    panelProgress.Invoke(new Action(() => SetProgress(percent, status, speed)));
                    return;
                }

                progressPercent = Math.Min(100, Math.Max(0, percent));
                if (lblProgressPercent != null) lblProgressPercent.Text = progressPercent + "%";
                if (lblProgressStatus != null) lblProgressStatus.Text = status;
                if (!string.IsNullOrEmpty(speed) && lblSpeedBadge != null)
                    lblSpeedBadge.Text = "Speed: " + FormatSpeed(speed);
                if (pbarTrack != null) pbarTrack.Invalidate();
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        // "5.4 MB/s"/"12.3"/"0 MB/s" တွေကို MST style "0.0000 MB/s" format ပြောင်း
        private static string FormatSpeed(string speed)
        {
            Match m = Regex.Match(speed?.Trim() ?? "", @"^([\d.]+)\s*(B/s|KB/s|MB/s|GB/s)$", RegexOptions.IgnoreCase);
            if (m.Success &&
                double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                return v.ToString("0.0000", CultureInfo.InvariantCulture) + " " + m.Groups[2].Value.ToUpperInvariant();
            if (double.TryParse(speed, NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
                return n.ToString("0.0000", CultureInfo.InvariantCulture) + " MB/s";
            return "0.0000 MB/s";   // Done / Calculating... / မသိတဲ့ input
        }

        // QC flash speed — file boundary တိုင်း update (percent line က speed label ကို မထိအောင်)
        private void UpdateFlashSpeed(double mbps)
        {
            if (lblSpeedBadge == null || lblSpeedBadge.IsDisposed || !lblSpeedBadge.IsHandleCreated) return;
            try
            {
                if (lblSpeedBadge.InvokeRequired)
                {
                    lblSpeedBadge.Invoke(new Action(() => UpdateFlashSpeed(mbps)));
                    return;
                }
                lblSpeedBadge.Text = "Speed: " +
                    mbps.ToString("0.0000", CultureInfo.InvariantCulture) + " MB/s";
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private void ResetProgress()
        {
            SetProgress(0, "Ready", "0 MB/s");
        }

        // panelProgress အတွင်း layout — title / percent / bar / Status|ticks|Speed rows
        private void LayoutProgressSection()
        {
            if (panelProgress == null || lblProgressTitle == null) return;
            int w = panelProgress.ClientSize.Width;
            if (w < 100) return;
            // text height တွေ တိုင်းထားတာ (96 dpi): title 15, percent 17, bar 14, Status/Speed 17
            // strip 72→80px — 72 မှာ အောက်တန်း bottom margin 3px ပဲကျလို့ Ready/Speed က အောက်နားကို ထိနေတယ်
            lblProgressTitle.Bounds = new Rectangle(0, 2, w, 15);
            lblProgressPercent.Bounds = new Rectangle(0, 17, w, 17);
            pbarTrack.Bounds = new Rectangle(8, 36, Math.Max(40, w - 16), 14);
            lblStatusCaption.Location = new Point(10, 55);
            lblProgressStatus.Location = new Point(56, 54);
            int speedW = 195;
            lblSpeedBadge.Bounds = new Rectangle(w - speedW - 8, 54, speedW, 17);
            int ticksL = 150, ticksR = w - speedW - 16;
            pbarTicks.Bounds = new Rectangle(ticksL, 57, Math.Max(30, ticksR - ticksL), 12);
        }

        private static GraphicsPath RoundRectPath(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            int d = rad * 2;
            if (d < 2) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // MST style rounded bar: dark track + blue gradient fill (percent ပေါ်မူတည်)
        private void PbarTrack_Paint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, pbarTrack.Width - 1, pbarTrack.Height - 1);
            if (r.Width < 4 || r.Height < 4) return;
            int rad = Math.Min(7, r.Height / 2);

            using (GraphicsPath track = RoundRectPath(r, rad))
            using (var tb = new SolidBrush(lightTheme ? Color.FromArgb(206, 213, 227) : Color.FromArgb(38, 42, 50)))
            {
                g.FillPath(tb, track);
                int fill = (int)Math.Round(r.Width * (progressPercent / 100.0));
                if (fill > 2)
                {
                    Rectangle fr = new Rectangle(r.X, r.Y, Math.Min(fill, r.Width), r.Height);
                    using (GraphicsPath fp = RoundRectPath(fr, rad))
                    using (var gb = new LinearGradientBrush(fr,
                        lightTheme ? Color.FromArgb(120, 165, 255) : Color.FromArgb(110, 165, 255),
                        Color.FromArgb(47, 110, 237), LinearGradientMode.Vertical))
                    {
                        g.SetClip(track);
                        g.FillPath(gb, fp);
                        g.ResetClip();
                    }
                }
                using (var pen = new Pen(lightTheme ? Color.FromArgb(178, 186, 202) : Color.FromArgb(56, 61, 72)))
                    g.DrawPath(pen, track);
            }
        }

        // Status နဲ့ Speed ကြား ruler ticks (အမြင့်တူများပြား)
        private void PbarTicks_Paint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            Rectangle c = pbarTicks.ClientRectangle;
            if (c.Width < 6 || c.Height < 6) return;
            Color line = lightTheme ? Color.FromArgb(158, 166, 182) : Color.FromArgb(72, 78, 90);
            Color tick = lightTheme ? Color.FromArgb(138, 146, 162) : Color.FromArgb(88, 95, 108);
            int midY = c.Height / 2;
            using (var pen = new Pen(line)) g.DrawLine(pen, 0, midY, c.Width, midY);
            using (var tp = new Pen(tick))
            {
                int step = 10;
                for (int x = 0; x <= c.Width; x += step)
                {
                    int h = (x % (step * 5) == 0) ? 9 : 5;
                    g.DrawLine(tp, x, midY - h / 2, x, midY + h / 2);
                }
            }
        }

        private void DgvPartitions_CellMouseDown(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0)
            {
                dgvPartitions.ClearSelection();
                dgvPartitions.Rows[e.RowIndex].Selected = true;
            }
        }

        private bool ParseCleanLogAndProgress(string rawLine)
        {
            if (string.IsNullOrWhiteSpace(rawLine)) return true;
            Match pct = Regex.Match(rawLine, @"(\d+(?:\.\d+)?)\s*%");
            if (pct.Success && double.TryParse(pct.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                SetProgress((int)value, "Transferring");
            Match match = Regex.Match(rawLine.Trim(), @"^([a-zA-Z0-9_\-]+):\s+Offset\s+(0x[0-9a-fA-F]+),\s+Length\s+(0x[0-9a-fA-F]+)");
            if (match.Success) { AddPartitionRow(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value); return true; }
            Match qc = Regex.Match(rawLine, @"^([a-zA-Z0-9_\-]+)[ \t]*\t([0-9A-Fa-f]{1,16})\t([0-9A-Fa-f]{1,16})\t");
            if (qc.Success) { AddPartitionRow(qc.Groups[1].Value, "0x" + qc.Groups[2].Value, "0x" + qc.Groups[3].Value); return true; }
            return false;
        }
        // Partition row တစ်ခု ထည့်တယ် — mtkclient (MediaTek) နဲ့ edl.py (Qualcomm) နှစ်မျိုးလုံးအတွက်
        private void AddPartitionRow(string partName, string offset, string lengthHex)
        {
            if (string.IsNullOrWhiteSpace(partName)) return;
            if (dgvPartitions.InvokeRequired) { dgvPartitions.Invoke(new Action(() => AddPartitionRow(partName, offset, lengthHex))); return; }
            string humanSize = FormatBytes(lengthHex);
            loadedPartitions.Add(new PartitionMeta { Name = partName, HumanSize = humanSize, Offset = offset, LengthHex = lengthHex });
            dgvPartitions.Rows.Add(false, partName, humanSize, offset, "", "");
            ApplyPartitionFilter();
        }

    }
}
