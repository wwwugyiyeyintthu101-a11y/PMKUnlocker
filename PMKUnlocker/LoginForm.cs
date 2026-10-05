#nullable disable
namespace PMKUnlocker;

// PMK-style login — polished purple card design
public class LoginForm : Form
{
    private TextBox txtEmail;
    private TextBox txtPassword;
    private CheckBox chkRemember;
    private Button btnLogin;
    private LinkLabel lnkRegister;
    private Label lblStatus;
    private Panel panelBody;
    private Panel panelTitle;
    private Panel panelStatus;
    private Panel panelCard;
    private bool registerMode;
    private bool loading;
    private System.Windows.Forms.Timer loadTimer;
    private int loadFrames;
    private ProgressBar updBar;
    private bool btnHover;

    private static readonly Font FTitle = new Font("Segoe UI", 15f, FontStyle.Bold);
    private static readonly Font FHead  = new Font("Segoe UI", 16f, FontStyle.Bold);
    private static readonly Font FChip  = new Font("Segoe UI", 9f, FontStyle.Bold);
    private static readonly Font FBtn   = new Font("Segoe UI", 10.5f, FontStyle.Bold);
    private static readonly Font FLbl   = new Font("Segoe UI", 8.5f, FontStyle.Bold);

    // login OK ပြီးရင် Form1 ကို ပြဖို့
    public string LoginEmail { get; private set; } = "";
    public string LoginPlan { get; private set; } = "";
    public string LoginExpiresAt { get; private set; } = "";
    public int LoginDaysLeft { get; private set; }

    // Palette — dark theme (Form1 နဲ့ လိုက်)
    private static readonly Color Accent      = Color.FromArgb(139, 92, 246);
    private static readonly Color AccentHover = Color.FromArgb(124, 58, 237);
    private static readonly Color AccentDark  = Color.FromArgb(167, 139, 250);
    private static readonly Color TitleTop    = Color.FromArgb(76, 29, 149);
    private static readonly Color TitleBot    = Color.FromArgb(124, 58, 237);
    private static readonly Color PageBg      = Color.FromArgb(18, 20, 24);
    private static readonly Color CardBg      = Color.FromArgb(32, 35, 42);
    private static readonly Color StatusBg    = Color.FromArgb(26, 29, 35);
    private static readonly Color TextDark    = Color.FromArgb(235, 235, 245);
    private static readonly Color TextMuted   = Color.FromArgb(150, 150, 170);
    private static readonly Color BorderCol   = Color.FromArgb(70, 75, 95);
    private static readonly Color InputBg     = Color.FromArgb(44, 48, 58);
    private static readonly Color Success     = Color.FromArgb(80, 220, 140);
    private static readonly Color Danger      = Color.FromArgb(255, 110, 110);

    public LoginForm()
    {
        InitializeUi();
        // PMK icon — exe ထဲ embedded pmk.ico (title bar / taskbar)
        try { var ic = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); if (ic != null) Icon = ic; } catch { }
        // auto-login မရှိ — Login button နှိပ်မှဝင်ရ (remember-me က email/prefill ပဲ)
        // + update gate check — form ပေါ်တာနဲ့ တပြိုင်နက် စ (login gate က စောင့်ယူ)
        Shown += (_, _) =>
        {
            // fade-in (200ms)
            var ft = new System.Windows.Forms.Timer { Interval = 20 };
            ft.Tick += (_, _) =>
            {
                Opacity = Math.Min(1.0, Opacity + 0.12);
                if (Opacity >= 1.0) { ft.Stop(); ft.Dispose(); }
            };
            ft.Start();
            try
            {
                if (!string.IsNullOrWhiteSpace(UpdateChecker.Repo))
                    UpdateChecker.PendingCheck = UpdateChecker.CheckAsync();
            }
            catch { }
        };
    }

    // Windows 10/11 dark title bar
    [System.Runtime.InteropServices.DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void ApplyDarkTitleBar()
    {
        try
        {
            int on = 1;
            if (DwmSetWindowAttribute(Handle, 20, ref on, 4) != 0)
                DwmSetWindowAttribute(Handle, 19, ref on, 4); // older Win10
        }
        catch { }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyDarkTitleBar();
    }

    private void InitializeUi()
    {
        SuspendLayout();

        Text = "PMK Mobile Tool [ PMK ] v7.3";
        ClientSize = new Size(490, 540);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = PageBg;
        Font = new Font("Segoe UI", 9.5f);
        KeyPreview = true;
        Opacity = 0;   // Shown မှာ fade-in

        // ---- gradient title + app icon + version chip ----
        panelTitle = new Panel { Dock = DockStyle.Top, Height = 64 };
        panelTitle.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var lg = new System.Drawing.Drawing2D.LinearGradientBrush(
                panelTitle.ClientRectangle, TitleTop, TitleBot, 35f))
                g.FillRectangle(lg, panelTitle.ClientRectangle);
            using (var hl = new SolidBrush(Color.FromArgb(55, 255, 255, 255)))
                g.FillRectangle(hl, 0, panelTitle.Height - 1, panelTitle.Width, 1);
            try
            {
                using var ic = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (ic != null) g.DrawIcon(ic, new Rectangle(22, 17, 30, 30));
            }
            catch { }
            TextRenderer.DrawText(g, "PMK Mobile Tool", FTitle,
                new Rectangle(64, 6, 300, 52), Color.White,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            string ver = "v" + UpdateChecker.CurrentVersion;
            var chip = new Rectangle(panelTitle.Width - 78, 21, 58, 22);
            using (var cb = new SolidBrush(Color.FromArgb(65, 255, 255, 255)))
                FillRounded(g, cb, chip, 11);
            TextRenderer.DrawText(g, ver, FChip, chip,
                Color.FromArgb(240, 240, 255),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            using (var cp = new Pen(Color.FromArgb(80, 255, 255, 255)))
                DrawRounded(g, cp, chip, 11);
        };

        // ---- status bar ----
        panelStatus = new Panel { Dock = DockStyle.Bottom, Height = 36, BackColor = StatusBg };
        panelStatus.Paint += (_, e) =>
        {
            using var p = new Pen(Color.FromArgb(40, 139, 92, 246));
            e.Graphics.DrawLine(p, 0, 0, panelStatus.Width, 0);
        };
        lblStatus = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(16, 0, 0, 0),
            ForeColor = TextMuted,
            Text = "License sign-in — admin creates accounts",
            Font = new Font("Segoe UI", 9f)
        };
        panelStatus.Controls.Add(lblStatus);

        // update download progress — status bar ညာဘက်မှာ (start hidden)
        updBar = new ProgressBar
        {
            Dock = DockStyle.Right,
            Width = 0,
            Height = 14,
            Style = ProgressBarStyle.Continuous,
            Visible = false
        };
        var updSpacer = new Panel { Dock = DockStyle.Right, Width = 14, BackColor = StatusBg };
        panelStatus.Controls.Add(updBar);
        panelStatus.Controls.Add(updSpacer);

        // ---- body: bg gradient + glow blobs + card shadow ----
        panelBody = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = PageBg,
            Padding = new Padding(30, 24, 30, 16)
        };
        panelBody.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var bg = new System.Drawing.Drawing2D.LinearGradientBrush(
                panelBody.ClientRectangle, Color.FromArgb(11, 13, 20), Color.FromArgb(20, 22, 40), 90f))
                g.FillRectangle(bg, panelBody.ClientRectangle);
            DrawGlow(g, new Rectangle(panelBody.Width - 150, -90, 270, 270), Color.FromArgb(32, 139, 92, 246));
            DrawGlow(g, new Rectangle(-80, panelBody.Height - 110, 230, 230), Color.FromArgb(24, 56, 189, 247));
            // card drop shadow
            var cr = panelCard.Bounds;
            if (cr.Width > 4)
            {
                using var sp = new System.Drawing.Drawing2D.GraphicsPath();
                AddRounded(sp, new Rectangle(cr.X - 3, cr.Y + 5, cr.Width + 6, cr.Height + 4), 18);
                using var sb = new SolidBrush(Color.FromArgb(50, 0, 0, 0));
                g.FillPath(sb, sp);
            }
        };

        // rounded card — region cuts corners so bg gradient shows through
        panelCard = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = CardBg
        };
        panelCard.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var lg = new System.Drawing.Drawing2D.LinearGradientBrush(
                panelCard.ClientRectangle, Color.FromArgb(35, 38, 50), Color.FromArgb(26, 28, 38), 90f))
                g.FillRectangle(lg, panelCard.ClientRectangle);
            var rc = new Rectangle(0, 0, panelCard.Width - 1, panelCard.Height - 1);
            using (var p = new Pen(Color.FromArgb(64, 72, 108), 1.4f))
                DrawRounded(g, p, rc, 16);
            using (var acc = new System.Drawing.Drawing2D.LinearGradientBrush(
                new Rectangle(0, 0, panelCard.Width, 4), Accent, Color.FromArgb(34, 211, 238), 0f))
                g.FillRectangle(acc, 0, 0, panelCard.Width, 4);
        };
        panelCard.Resize += (_, _) => UpdateCardRegion();
        void UpdateCardRegion()
        {
            if (panelCard.Width < 4 || panelCard.Height < 4) return;
            var r = System.Drawing.Region.FromHrgn(
                CreateRoundRectRgn(0, 0, panelCard.Width, panelCard.Height, 32, 32));
            panelCard.Region?.Dispose();
            panelCard.Region = r;
        }

        // layout inside card (absolute but well spaced)
        int left = 32;
        int fieldW = 366;

        var lblHead = new Label
        {
            Text = "Welcome back",
            Location = new Point(left, 26),
            AutoSize = true,
            ForeColor = Color.White,
            Font = FHead
        };
        var lblSub = new Label
        {
            Text = "Sign in to continue to PMK Mobile Tool",
            Location = new Point(left, 62),
            AutoSize = true,
            ForeColor = TextMuted,
            Font = new Font("Segoe UI", 9.5f)
        };

        var lblEmail = new Label
        {
            Text = "EMAIL ID",
            Location = new Point(left, 106),
            AutoSize = true,
            ForeColor = Color.FromArgb(158, 164, 196),
            Font = FLbl
        };
        txtEmail = MakeField(left, 128, fieldW, 42, "user@gmail.com", out var emailWrap);

        var lblPass = new Label
        {
            Text = "PASSWORD",
            Location = new Point(left, 188),
            AutoSize = true,
            ForeColor = Color.FromArgb(158, 164, 196),
            Font = FLbl
        };
        txtPassword = MakeField(left, 210, fieldW - 78, 42, "••••••••", out var passWrap);
        txtPassword.UseSystemPasswordChar = true;

        var chkShow = new CheckBox
        {
            Text = "Show",
            Location = new Point(left + fieldW - 70, 222),
            AutoSize = true,
            ForeColor = TextMuted,
            Font = new Font("Segoe UI", 9f),
            Cursor = Cursors.Hand
        };
        chkShow.CheckedChanged += (_, _) =>
            txtPassword.UseSystemPasswordChar = !chkShow.Checked;

        chkRemember = new CheckBox
        {
            Text = "Remember me",
            Location = new Point(left, 266),
            AutoSize = true,
            ForeColor = TextDark,
            Font = new Font("Segoe UI", 9.5f),
            Checked = false,
            Cursor = Cursors.Hand
        };

        // gradient rounded Login
        btnLogin = new Button
        {
            Location = new Point(left, 298),
            Size = new Size(fieldW, 46),
            FlatStyle = FlatStyle.Flat,
            BackColor = Accent,
            ForeColor = Color.White,
            Font = FBtn,
            Cursor = Cursors.Hand,
            TabStop = false
        };
        btnLogin.FlatAppearance.BorderSize = 0;
        btnLogin.Click += async (_, _) => await OnLoginAsync();
        btnLogin.Region = System.Drawing.Region.FromHrgn(
            CreateRoundRectRgn(0, 0, fieldW, 46, 24, 24));
        btnLogin.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var rc = new Rectangle(0, 0, btnLogin.Width - 1, btnLogin.Height - 1);
            Color c1, c2, bd;
            if (!btnLogin.Enabled)
            {
                c1 = Color.FromArgb(70, 74, 92); c2 = Color.FromArgb(54, 58, 74); bd = Color.FromArgb(90, 95, 115);
            }
            else if (btnHover)
            {
                c1 = Color.FromArgb(167, 139, 250); c2 = Color.FromArgb(124, 58, 237); bd = Color.FromArgb(196, 181, 253);
            }
            else
            {
                c1 = Color.FromArgb(139, 92, 246); c2 = Color.FromArgb(109, 40, 217); bd = Color.FromArgb(167, 139, 250);
            }
            using (var lg = new System.Drawing.Drawing2D.LinearGradientBrush(
                btnLogin.ClientRectangle, c1, c2, 90f))
                g.FillRectangle(lg, btnLogin.ClientRectangle);
            using (var gl = new System.Drawing.Drawing2D.LinearGradientBrush(
                new Rectangle(0, 0, btnLogin.Width, 20), Color.FromArgb(45, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
                g.FillRectangle(gl, 2, 2, btnLogin.Width - 4, 18);
            using (var p = new Pen(bd, 1.3f))
                DrawRounded(g, p, rc, 12);
            if (loading)
            {
                var c = btnLogin.ClientRectangle;
                int cx = c.Width / 2, cy = c.Height / 2 - 3;
                for (int i = 0; i < 3; i++)
                {
                    int phase = (loadFrames + i) % 3;
                    int alpha = phase == 0 ? 255 : phase == 1 ? 140 : 70;
                    using var brush = new SolidBrush(Color.FromArgb(alpha, Color.White));
                    g.FillEllipse(brush, cx - 44 + i * 12, cy - 4, 8, 8);
                }
                TextRenderer.DrawText(g, registerMode ? "Creating account..." : "Signing in...", FBtn,
                    new Rectangle(0, cy + 6, c.Width, 20), Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            else
            {
                string label = registerMode ? "Create Account  →" : "Sign In  →";
                TextRenderer.DrawText(g, label, FBtn, btnLogin.ClientRectangle, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        };
        btnLogin.MouseEnter += (_, _) => { if (btnLogin.Enabled && !loading) { btnHover = true; btnLogin.Invalidate(); } };
        btnLogin.MouseLeave += (_, _) => { btnHover = false; btnLogin.Invalidate(); };

        // loading spinner timer
        loadTimer = new System.Windows.Forms.Timer { Interval = 120 };
        loadTimer.Tick += (_, _) =>
        {
            loadFrames = (loadFrames + 1) % 3;
            btnLogin.Invalidate();
        };

        // Register link
        lnkRegister = new LinkLabel
        {
            Text = "Account မရှိသေးပါ — Register Here",
            Location = new Point(left, 362),
            AutoSize = true,
            LinkColor = AccentDark,
            ActiveLinkColor = Accent,
            ForeColor = TextMuted,
            Font = new Font("Segoe UI", 9.5f),
            Cursor = Cursors.Hand
        };
        lnkRegister.Links.Clear();
        lnkRegister.Links.Add(22, 14, "register");
        lnkRegister.LinkClicked += (_, _) => ToggleRegisterMode(!registerMode);

        panelCard.Controls.AddRange(new Control[]
        {
            lblHead, lblSub,
            lblEmail, emailWrap,
            lblPass, passWrap, chkShow,
            chkRemember, btnLogin,
            lnkRegister
        });
        panelBody.Controls.Add(panelCard);

        // Prefill remember-me
        if (LocalLogin.TryRemembered(out string saved, out string savedSecret, out bool savedIsToken))
        {
            if (!string.IsNullOrEmpty(saved)) txtEmail.Text = saved;
            if (savedIsToken)
            {
                // token mode — password မသိမ်းတော့ (auto-login ပိတ်ထား — Login နှိပ်မှ ဝင်ရ)
                chkRemember.Checked = true;
            }
            else if (!string.IsNullOrEmpty(savedSecret))
            {
                // password blob — prefill (auto-login မရှိ — Login နှိပ်မှ ဝင်ရ)
                txtPassword.Text = savedSecret;
                chkRemember.Checked = true;
            }
        }
        else
        {
            string emailOnly = LocalLogin.SavedEmail() ?? "";
            if (!string.IsNullOrEmpty(emailOnly)) txtEmail.Text = emailOnly;
        }

        Controls.Add(panelBody);
        Controls.Add(panelTitle);
        Controls.Add(panelStatus);

        AcceptButton = btnLogin;
        ResumeLayout(false);
        PerformLayout();
        UpdateCardRegion();
    }

    // rounded-rect paint helpers
    private static void AddRounded(System.Drawing.Drawing2D.GraphicsPath path, Rectangle r, int rad)
    {
        int d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
        if (d < 2) { path.AddRectangle(r); return; }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
    }

    private static void FillRounded(Graphics g, Brush b, Rectangle r, int rad)
    {
        using var p = new System.Drawing.Drawing2D.GraphicsPath();
        AddRounded(p, r, rad);
        g.FillPath(b, p);
    }

    private static void DrawRounded(Graphics g, Pen p, Rectangle r, int rad)
    {
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        AddRounded(path, r, rad);
        g.DrawPath(p, path);
    }

    private static void DrawGlow(Graphics g, Rectangle bounds, Color center)
    {
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddEllipse(bounds);
        using var br = new System.Drawing.Drawing2D.PathGradientBrush(path)
        {
            CenterColor = center,
            SurroundColors = new[] { Color.FromArgb(0, center.R, center.G, center.B) }
        };
        g.FillPath(br, path);
    }

    // rounded input field: wrapper panel (border/glow) + borderless textbox
    private TextBox MakeField(int x, int y, int w, int h, string placeholder, out Panel result)
    {
        var wrap = new Panel
        {
            Location = new Point(x, y),
            Size = new Size(w, h),
            BackColor = InputBg,
            Padding = new Padding(14, 2, 14, 2)
        };
        wrap.Region = System.Drawing.Region.FromHrgn(CreateRoundRectRgn(0, 0, w, h, 24, 24));
        var tb = new TextBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            Font = new Font("Segoe UI", 11.5f),
            BackColor = InputBg,
            ForeColor = TextDark,
            MaxLength = 120,
            Text = placeholder
        };
        tb.ForeColor = Color.FromArgb(120, 120, 140);
        tb.GotFocus += (_, _) =>
        {
            wrap.Tag = true;
            wrap.Invalidate();
            if (tb.Text == placeholder) { tb.Text = ""; tb.ForeColor = TextDark; }
        };
        tb.LostFocus += (_, _) =>
        {
            wrap.Tag = false;
            wrap.Invalidate();
            if (string.IsNullOrWhiteSpace(tb.Text)) { tb.Text = placeholder; tb.ForeColor = Color.FromArgb(120, 120, 140); }
        };
        wrap.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            bool on = wrap.Tag is true;
            if (on)
            {
                using var glow = new Pen(Color.FromArgb(45, 139, 92, 246), 5f);
                DrawRounded(g, glow, new Rectangle(2, 2, wrap.Width - 5, wrap.Height - 5), 12);
            }
            using var p = new Pen(on ? Accent : Color.FromArgb(58, 64, 94), on ? 2f : 1.3f);
            DrawRounded(g, p, new Rectangle(1, 1, wrap.Width - 3, wrap.Height - 3), 12);
        };
        wrap.Controls.Add(tb);
        result = wrap;
        return tb;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
        base.OnKeyDown(e);
    }

    private void ToggleRegisterMode(bool on)
    {
        registerMode = on;
        if (on)
        {
            lnkRegister.Text = "Already have account?  Login  Here";
            lnkRegister.Links.Clear();
            lnkRegister.Links.Add(21, 5, "login");
            lblStatus.Text = "Register mode — Email + Password (min 4) → Admin approve မှ ဝင်ရ";
            txtEmail.Clear();
            txtPassword.Clear();
            chkRemember.Checked = false;
            txtEmail.Focus();
        }
        else
        {
            lnkRegister.Text = "Account မရှိသေးပါ — Register Here";
            lnkRegister.Links.Clear();
            lnkRegister.Links.Add(22, 14, "register");
            lblStatus.Text = "License sign-in — admin creates accounts";
            string saved2 = LocalLogin.SavedEmail() ?? "";
            if (!string.IsNullOrEmpty(saved2)) txtEmail.Text = saved2;
            txtPassword.Clear();
            txtPassword.Focus();
        }
        btnLogin.Invalidate();
    }

    private async Task OnLoginAsync()
    {
        if (loading) return;
        loading = true;
        btnLogin.Enabled = false;
        loadFrames = 0;
        loadTimer.Start();
        txtEmail.Enabled = false;
        txtPassword.Enabled = false;
        chkRemember.Enabled = false;
        lnkRegister.Enabled = false;
        lblStatus.ForeColor = TextMuted;
        lblStatus.Text = registerMode ? "Registering..." : "Loading — checking license server...";
        try
        {
            // minimum loading UX so user sees spinner
            bool remember = chkRemember.Checked;   // UI thread မှာယူ (background မှာ Control မထိ)
            var work = Task.Run(async () =>
            {
                string email = txtEmail.Text.Trim();
                string pw = txtPassword.Text;

                if (registerMode)
                {
                    var reg = await LicenseClient.RegisterAsync(email, pw);
                    return (reg, (LicenseClient.Result)null);
                }

                // remember=true → server က auto-login token ပြန်ပေးမယ် (password မသိမ်းရ)
                var result = await LicenseClient.LoginAsync(email, pw, remember);
                return ((LicenseClient.Result)null, result);
            });

            await Task.Delay(800); // show loading at least 0.8s
            var (regRes, loginRes) = await work;

            if (registerMode)
            {
                lblStatus.ForeColor = regRes.Ok ? Success : Danger;
                lblStatus.Text = regRes.Message;
                if (regRes.Ok)
                {
                    ToggleRegisterMode(false);
                    txtEmail.Text = txtEmail.Text; // keep
                    lblStatus.ForeColor = Success;
                    lblStatus.Text = "Registered — Admin approval စောင့်ဆိုင်းပါ";
                }
                return;
            }

            if (!loginRes.Ok)
            {
                lblStatus.Text = loginRes.Message;
                lblStatus.ForeColor = Danger;
                txtPassword.SelectAll();
                txtPassword.Focus();
                return;
            }

            // Client-side expiry enforce — server မှာလည်း block ပေမဲ့ client clock နဲ့ ပြန်စစ်
            bool expired = loginRes.DaysLeft <= 0;
            if (!expired && DateTime.TryParse(loginRes.ExpiresAt, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out DateTime expCli))
            {
                DateTime expUtc = expCli.Kind == DateTimeKind.Local
                    ? expCli.ToUniversalTime()
                    : DateTime.SpecifyKind(expCli, DateTimeKind.Utc);
                expired = expUtc <= DateTime.UtcNow;
            }
            if (expired)
            {
                lblStatus.Text = "Subscription သက်တမ်းကုန်ပြီ — admin renew လုပ်ပါ";
                lblStatus.ForeColor = Danger;
                txtPassword.SelectAll();
                txtPassword.Focus();
                return;
            }

            string email2 = txtEmail.Text.Trim();
            // Remember me → password ကို DPAPI နဲ့ သိမ်း (next launch မှာ prefill — Login နှိပ်ရုံ)
            if (chkRemember.Checked)
                LocalLogin.SaveRemembered(email2, txtPassword.Text);
            else LocalLogin.ClearRemembered();

            CompleteLogin(email2, loginRes);
            await Task.Delay(300); // brief success pause
            if (!await EnforceUpdateGateAsync()) return;   // update gate — block ဆို app ပိတ်
            DialogResult = DialogResult.OK;
            Close();
        }
        finally
        {
            loadTimer.Stop();
            loading = false;
            btnLogin.Enabled = true;
            txtEmail.Enabled = true;
            txtPassword.Enabled = true;
            chkRemember.Enabled = true;
            lnkRegister.Enabled = true;
            btnLogin.Invalidate();
        }
    }

    // ===== LOGIN UPDATE GATE =====
    // update ရှိ → update လုပ်ရမယ် (No နှိပ်ရင် app ပိတ်); check fail/offline → strict block;
    // နောက်ဆုံး version ဆို Form1 ဝင်ခွင့်ပေး → true — failure ဆို ဘယ်တော့မှ false မပြန် (app ပိတ်)
    private async Task<bool> EnforceUpdateGateAsync()
    {
        UpdateInfo info;
        try
        {
            lblStatus.ForeColor = TextMuted;
            lblStatus.Text = "Checking for updates...";
            info = await UpdateChecker.AwaitCheckForGateAsync(25000);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "Update check failed:\n" + ex.Message + "\n\n" +
                "Cannot verify the latest version — the tool will close.",
                "Update required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Environment.Exit(0);
            return false;
        }
        if (info == null) return true;   // နောက်ဆုံး version (သို့) update source off

        DialogResult r = MessageBox.Show(this,
            "A new version (v" + info.Version + ") is required.  (current: v" + UpdateChecker.CurrentVersion + ")\n\n" +
            (string.IsNullOrWhiteSpace(info.Notes) ? "" : info.Notes + "\n\n") +
            "Download and install now?\nClick No to exit the tool.",
            "Update required", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
        if (r != DialogResult.Yes)
        {
            Environment.Exit(0);
            return false;
        }

        string oldTitle = Text;
        try
        {
            // download progress — status bar + progress bar + title မှာ ရာခိုင်နှုန်းပြ
            updBar.Value = 0;
            updBar.Width = 150;
            updBar.Visible = true;
            lblStatus.ForeColor = Accent;
            lblStatus.Text = "Downloading update... 0%";
            string setup = await UpdateChecker.DownloadAsync(info, pct =>
            {
                Text = "Downloading update... " + pct + "%";
                lblStatus.Text = "Downloading update... " + pct + "%";
                if (pct > updBar.Value && pct <= 100) updBar.Value = pct;
            });
            lblStatus.ForeColor = Success;
            lblStatus.Text = "SHA-256 verified — installing update...";
            updBar.Value = 100;
            UpdateChecker.StartInstallAndExit(setup);
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Text = oldTitle;
            MessageBox.Show(this, "Update download failed:\n" + ex.Message + "\n\n" +
                "The tool will close — relaunch to try again.",
                "Update required", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Environment.Exit(0);
        }
        return false;
    }

    // login OK → Form1 ကိုပြဖို့ fields ဖြည့်
    private void CompleteLogin(string email, LicenseClient.Result res)
    {
        LoginEmail = email;
        LoginPlan = res.Plan;
        LoginExpiresAt = res.ExpiresAt;
        LoginDaysLeft = res.DaysLeft;

        string plan = string.IsNullOrEmpty(res.Plan) ? "" : " [" + res.Plan + "]";
        string days = res.DaysLeft > 0 ? " · " + res.DaysLeft + "d left" : "";
        lblStatus.ForeColor = Success;
        lblStatus.Text = "Login OK" + plan + days;
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect, int nWidthEllipse, int nHeightEllipse);
}
