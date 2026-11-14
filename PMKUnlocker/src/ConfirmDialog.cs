using System.Media;

namespace PMKUnlocker;

// Native MessageBox (Segoe UI) က မြန်မာစာကို shaping မလုပ်လို့ အက္ခရာတွေ ပြိုပြီး/tofu ဖြစ်တယ်။
// WinForms Label ကတော့ Myanmar Text font နဲ့ ကောင်းကောင်း ပြတယ် — ဒါကြောင့်
// confirmation / message တွေကို ဒီ modal dialog ကနေ ပြတယ်။
internal static class ConfirmDialog
{
    // Form1.ApplyTheme() က theme ပြောင်းတိုင်း ဒီကို sync လုပ်တယ် (single main window app)
    internal static bool DarkMode;

    private static readonly Lazy<Font> TextFont = new(CreateTextFont);

    // Destructive confirm အတွက် default က "No" — Enter/Esc မှားနှိပ်ရင် မလုပ်မိအောင်။
    internal static bool Ask(IWin32Window? owner, string text, string caption, bool destructive = true) =>
        Show(owner, text, caption, MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
             destructive ? MessageBoxDefaultButton.Button2 : MessageBoxDefaultButton.Button1) == DialogResult.Yes;

    internal static DialogResult Show(
        IWin32Window? owner,
        string text,
        string caption,
        MessageBoxButtons buttons = MessageBoxButtons.OK,
        MessageBoxIcon icon = MessageBoxIcon.Information,
        MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1)
    {
        PlaySound(icon);
        using var dlg = new DialogForm(text, caption, buttons, icon, defaultButton)
        {
            StartPosition = owner != null ? FormStartPosition.CenterParent : FormStartPosition.CenterScreen
        };
        return owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog();
    }

    internal static DialogResult Show(
        string text,
        string caption,
        MessageBoxButtons buttons = MessageBoxButtons.OK,
        MessageBoxIcon icon = MessageBoxIcon.Information,
        MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1) =>
        Show(null, text, caption, buttons, icon, defaultButton);

    private static Font CreateTextFont()
    {
        // Myanmar Text က Windows 8.1+ မှာ ပါတယ် — မရှိရင် Segoe UI (GDI font linking) ပဲ သုံး
        try { using var probe = new FontFamily("Myanmar Text"); }
        catch { return new Font("Segoe UI", 9.5f); }
        return new Font("Myanmar Text", 10f);
    }

    private static void PlaySound(MessageBoxIcon icon)
    {
        try
        {
            switch (icon)
            {
                case MessageBoxIcon.Warning: SystemSounds.Exclamation.Play(); break;
                case MessageBoxIcon.Error: SystemSounds.Hand.Play(); break;
                case MessageBoxIcon.Question: SystemSounds.Question.Play(); break;
                case MessageBoxIcon.Information: SystemSounds.Asterisk.Play(); break;
            }
        }
        catch { }   // sound device မရှိလည်း dialog ပြဖို့ မတားရ
    }

    private static Bitmap IconBitmap(MessageBoxIcon icon) => icon switch
    {
        MessageBoxIcon.Warning => SystemIcons.Warning.ToBitmap(),
        MessageBoxIcon.Error => SystemIcons.Error.ToBitmap(),
        MessageBoxIcon.Question => SystemIcons.Question.ToBitmap(),
        _ => SystemIcons.Information.ToBitmap()
    };

    private static List<(string Text, DialogResult Result)> ButtonDefs(MessageBoxButtons buttons) => buttons switch
    {
        MessageBoxButtons.OKCancel => new() { ("OK", DialogResult.OK), ("Cancel", DialogResult.Cancel) },
        MessageBoxButtons.YesNo => new() { ("Yes", DialogResult.Yes), ("No", DialogResult.No) },
        MessageBoxButtons.YesNoCancel => new() { ("Yes", DialogResult.Yes), ("No", DialogResult.No), ("Cancel", DialogResult.Cancel) },
        MessageBoxButtons.RetryCancel => new() { ("Retry", DialogResult.Retry), ("Cancel", DialogResult.Cancel) },
        MessageBoxButtons.AbortRetryIgnore => new() { ("Abort", DialogResult.Abort), ("Retry", DialogResult.Retry), ("Ignore", DialogResult.Ignore) },
        _ => new() { ("OK", DialogResult.OK) }
    };

    private static readonly TextFormatFlags LineFlags = TextFormatFlags.SingleLine;

    private static int MeasureLineWidth(string s, Font font) =>
        TextRenderer.MeasureText(s, font, Size.Empty, LineFlags).Width;

    private static int WidestLine(List<string> lines, Font font)
    {
        int w = 0;
        foreach (string line in lines) w = Math.Max(w, MeasureLineWidth(line, font));
        return w;
    }

    private static int BlockHeight(int count, int lineH, int lineGap) =>
        count <= 0 ? lineH : count * lineH + (count - 1) * lineGap + 4;

    // Label/RichTextBox က line-height ကို မချိန်နိုင်လို့ စာလုံးကို ကိုယ်တိုင် wrap လုပ်တယ်
    private static List<string> WrapText(string text, Font font, int maxWidth)
    {
        var lines = new List<string>();
        string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (string para in normalized.Split('\n'))
        {
            if (para.Length == 0) { lines.Add(string.Empty); continue; }
            string cur = string.Empty;
            foreach (string word in para.Split(' '))
            {
                string candidate = cur.Length == 0 ? word : cur + " " + word;
                if (cur.Length == 0 || MeasureLineWidth(candidate, font) <= maxWidth)
                {
                    cur = candidate;
                }
                else
                {
                    lines.Add(cur);
                    cur = word;
                }
            }
            lines.Add(cur);
        }
        while (lines.Count > 1 && lines[0].Length == 0) lines.RemoveAt(0);
        while (lines.Count > 1 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        if (lines.Count == 0) lines.Add(string.Empty);
        return lines;
    }

    private sealed class TextBlock : Control
    {
        private readonly List<string> lines;
        private readonly int lineH;
        private readonly int lineGap;

        internal TextBlock(List<string> lines, Font font, Color fore, Color back,
            int lineH, int lineGap, int width, int height)
        {
            this.lines = lines;
            this.lineH = lineH;
            this.lineGap = lineGap;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Font = font;
            ForeColor = fore;
            BackColor = back;
            Size = new Size(width, height);
            Text = string.Join("\n", lines);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            int y = 0;
            foreach (string line in lines)
            {
                if (line.Length > 0)
                    TextRenderer.DrawText(e.Graphics, line, Font, new Point(0, y), ForeColor, LineFlags);
                y += lineH + lineGap;
            }
        }
    }

    private sealed class DialogForm : Form
    {
        internal DialogForm(string text, string caption, MessageBoxButtons buttons,
            MessageBoxIcon icon, MessageBoxDefaultButton defaultButton)
        {
            float s = DeviceDpi > 0 ? DeviceDpi / 96f : 1f;

            bool dark = DarkMode;
            Color bg = dark ? Color.FromArgb(24, 26, 31) : Color.FromArgb(245, 247, 252);
            Color fg = dark ? Color.White : Color.FromArgb(30, 32, 40);
            Color btnBg = dark ? Color.FromArgb(45, 49, 58) : Color.FromArgb(228, 232, 241);
            Color btnBorder = dark ? Color.FromArgb(72, 78, 92) : Color.FromArgb(176, 184, 200);
            Color accent = dark ? Color.FromArgb(86, 145, 250) : Color.FromArgb(58, 102, 208);
            Font textFont = TextFont.Value;

            Text = caption;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = bg;
            ForeColor = fg;
            Font = textFont;

            int pad = Scale(18, s);
            int iconSize = icon == MessageBoxIcon.None ? 0 : Scale(32, s);
            int gap = Scale(12, s);
            int btnW = Scale(96, s);
            int btnH = Scale(30, s);
            int btnGap = Scale(8, s);
            int rowGap = Scale(18, s);

            Rectangle screen = (Screen.PrimaryScreen ?? Screen.FromPoint(Cursor.Position)).WorkingArea;
            int maxTextW = Math.Max(Scale(220, s), Math.Min(Scale(460, s), screen.Width - Scale(140, s)));
            int maxTextH = Math.Max(Scale(120, s), screen.Height - Scale(240, s));

            // မြန်မာ stacked အက္ခရာတွေက font ရဲ့ line box ထက် ကျော်တက်တာမို့ line တစ်ကြား
            // အနည်းငယ် ခြားပြီး ကိုယ်တိုင် စာလုံးချ (Label ရဲ့ default line-height က ထပ်နေတယ်)။
            int lineH = TextRenderer.MeasureText("Ag", textFont, Size.Empty, TextFormatFlags.SingleLine).Height;
            int lineGap = Scale(4, s);
            int wrapW = maxTextW;

            List<string> lines = WrapText(text, textFont, wrapW);
            int textW = Math.Min(wrapW, Math.Max(Scale(200, s), WidestLine(lines, textFont) + Scale(2, s)));
            int textH = BlockHeight(lines.Count, lineH, lineGap);

            bool scroll = textH > maxTextH;
            if (scroll)
            {
                // scrollbar ရဲ့ width ကို ကြိုယူထား — rewrap ဖြစ်ပြီး horizontal scrollbar မပေါ်ရ
                wrapW = maxTextW - SystemInformation.VerticalScrollBarWidth;
                lines = WrapText(text, textFont, wrapW);
                textW = Math.Min(wrapW, Math.Max(Scale(200, s), WidestLine(lines, textFont) + Scale(2, s)));
                textH = BlockHeight(lines.Count, lineH, lineGap);
            }

            int textX = pad + (iconSize > 0 ? iconSize + gap : 0);
            int textAreaW = scroll ? maxTextW : textW;
            int textAreaH = scroll ? maxTextH : textH;
            ClientSize = new Size(
                Math.Max(Scale(340, s), textX + textAreaW + pad),
                pad + textAreaH + rowGap + btnH + pad);

            if (iconSize > 0)
            {
                Controls.Add(new PictureBox
                {
                    Image = IconBitmap(icon),
                    SizeMode = PictureBoxSizeMode.Zoom,
                    Size = new Size(iconSize, iconSize),
                    Location = new Point(pad, pad),
                    BackColor = bg
                });
            }

            var block = new TextBlock(lines, textFont, fg, bg, lineH, lineGap, textW, textH);

            if (scroll)
            {
                var host = new Panel
                {
                    AutoScroll = true,
                    BackColor = bg,
                    Location = new Point(textX, pad),
                    Size = new Size(maxTextW, maxTextH)
                };
                host.Controls.Add(block);
                Controls.Add(host);
            }
            else
            {
                block.Location = new Point(textX, pad);
                Controls.Add(block);
            }

            var defs = ButtonDefs(buttons);
            int n = defs.Count;
            int totalBtnW = n * btnW + (n - 1) * btnGap;
            int x0 = Math.Max(pad, ClientSize.Width - pad - totalBtnW);
            int y = ClientSize.Height - pad - btnH;

            int defIndex = defaultButton switch
            {
                MessageBoxDefaultButton.Button2 => 1,
                MessageBoxDefaultButton.Button3 => 2,
                _ => 0
            };
            defIndex = Math.Min(defIndex, n - 1);

            Font btnFont = SystemFonts.MessageBoxFont ?? new Font("Segoe UI", 9f);
            Button? defaultBtn = null, firstBtn = null, cancelBtn = null, noBtn = null, okBtn = null;

            for (int i = 0; i < n; i++)
            {
                var (labelText, result) = defs[i];
                var btn = new Button
                {
                    Text = labelText,
                    Font = btnFont,
                    Size = new Size(btnW, btnH),
                    Location = new Point(x0 + i * (btnW + btnGap), y),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = btnBg,
                    ForeColor = fg,
                    UseVisualStyleBackColor = false,
                    DialogResult = result
                };
                btn.FlatAppearance.BorderColor = btnBorder;
                btn.FlatAppearance.BorderSize = 1;
                if (i == defIndex)
                {
                    btn.FlatAppearance.BorderColor = accent;
                    btn.FlatAppearance.BorderSize = Scale(2, s) > 2 ? 2 : 1;
                    defaultBtn = btn;
                }

                firstBtn ??= btn;
                if (result == DialogResult.Cancel) cancelBtn ??= btn;
                if (result == DialogResult.No) noBtn ??= btn;
                if (result == DialogResult.OK) okBtn ??= btn;
                Controls.Add(btn);
            }

            AcceptButton = defaultBtn ?? firstBtn;
            // Esc က Yes/No dialog မှာ "No" (ဘာမှ မလုပ်) ဖြစ်ရမယ် — destructive op မတော်တဆ မလုပ်ရ
            CancelButton = cancelBtn ?? noBtn ?? okBtn;
        }

        private static int Scale(int value, float scale) => Math.Max(1, (int)Math.Round(value * scale));

        // Main window လို dark title bar — မရှိရင် dialog က ဖြူပြီး မကိုက်ဘူး
        [System.Runtime.InteropServices.DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!DarkMode) return;
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(Handle, 20, ref on, 4) != 0)
                    DwmSetWindowAttribute(Handle, 19, ref on, 4);   // older Win10
            }
            catch { }
        }
    }
}
