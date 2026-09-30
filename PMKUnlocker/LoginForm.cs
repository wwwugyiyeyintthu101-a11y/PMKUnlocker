#nullable disable
namespace PMKUnlocker;

// MST-style login — polished purple card design
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

    public LoginForm() => InitializeUi();

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

        Text = "PMK Mobile Tool [ PMK ] v7.2";
        ClientSize = new Size(460, 460);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = PageBg;
        Font = new Font("Segoe UI", 9.5f);
        KeyPreview = true;

        // ---- gradient title ----
        panelTitle = new Panel { Dock = DockStyle.Top, Height = 56 };
        panelTitle.Paint += (_, e) =>
        {
            using var lg = new System.Drawing.Drawing2D.LinearGradientBrush(
                panelTitle.ClientRectangle, TitleTop, TitleBot, 0f);
            e.Graphics.FillRectangle(lg, panelTitle.ClientRectangle);
        };
        var lblTitle = new Label
        {
            Text = "PMK Mobile Tool",
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 14f, FontStyle.Bold),
            Location = new Point(18, 8),
            AutoSize = true,
            BackColor = Color.Transparent
        };
        var lblVer = new Label
        {
            Text = "[ PMK ]  v7.2",
            ForeColor = Color.FromArgb(220, 210, 255),
            Font = new Font("Segoe UI", 9.5f),
            Location = new Point(19, 34),
            AutoSize = true,
            BackColor = Color.Transparent
        };
        panelTitle.Controls.Add(lblTitle);
        panelTitle.Controls.Add(lblVer);

        // ---- status bar ----
        panelStatus = new Panel { Dock = DockStyle.Bottom, Height = 34, BackColor = StatusBg };
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

        // ---- body ----
        panelBody = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = PageBg,
            Padding = new Padding(28, 22, 28, 14)
        };

        // white card
        panelCard = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = CardBg,
            Padding = new Padding(30, 26, 30, 20)
        };
        panelCard.Paint += (_, e) =>
        {
            var rect = new Rectangle(0, 0, panelCard.Width - 1, panelCard.Height - 1);
            ControlPaint.DrawBorder(e.Graphics, rect,
                BorderCol, ButtonBorderStyle.Solid);
            using var accent = new SolidBrush(Accent);
            e.Graphics.FillRectangle(accent, 0, 0, panelCard.Width, 4);
        };

        // layout inside card (absolute but well spaced)
        int left = 30;
        int fieldW = 370;

        var lblEmail = new Label
        {
            Text = "Email ID",
            Location = new Point(left, 24),
            AutoSize = true,
            ForeColor = TextDark,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold)
        };
        txtEmail = MakeTextBox(left, 50, fieldW, "user@gmail.com");

        var lblPass = new Label
        {
            Text = "Password",
            Location = new Point(left, 96),
            AutoSize = true,
            ForeColor = TextDark,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold)
        };
        txtPassword = MakeTextBox(left, 122, fieldW - 70, "••••••••");
        txtPassword.UseSystemPasswordChar = true;

        var chkShow = new CheckBox
        {
            Text = "Show",
            Location = new Point(left + fieldW - 62, 124),
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
            Location = new Point(left, 164),
            AutoSize = true,
            ForeColor = TextDark,
            Font = new Font("Segoe UI", 9.5f),
            Checked = false,
            Cursor = Cursors.Hand
        };

        // round Login
        btnLogin = new Button
        {
            Location = new Point(322, 168),
            Size = new Size(76, 76),
            FlatStyle = FlatStyle.Flat,
            BackColor = Accent,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Cursor = Cursors.Hand,
            TabStop = false
        };
        btnLogin.FlatAppearance.BorderSize = 0;
        btnLogin.Click += async (_, _) => await OnLoginAsync();
        btnLogin.Region = System.Drawing.Region.FromHrgn(
            CreateRoundRectRgn(0, 0, btnLogin.Width, btnLogin.Height, btnLogin.Width, btnLogin.Height));
        btnLogin.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var bg = new SolidBrush(btnLogin.Enabled || loading ? Accent : Color.Gray);
            e.Graphics.FillEllipse(bg, 0, 0, btnLogin.Width - 1, btnLogin.Height - 1);
            if (loading)
            {
                // spinner dots
                var c = btnLogin.ClientRectangle;
                int cx = c.Width / 2, cy = c.Height / 2 - 4;
                for (int i = 0; i < 3; i++)
                {
                    int phase = (loadFrames + i) % 3;
                    int alpha = phase == 0 ? 255 : phase == 1 ? 140 : 70;
                    using var brush = new SolidBrush(Color.FromArgb(alpha, Color.White));
                    e.Graphics.FillEllipse(brush, cx - 14 + i * 12, cy - 4, 8, 8);
                }
                TextRenderer.DrawText(e.Graphics, "Loading", btnLogin.Font,
                    new Rectangle(0, cy + 10, c.Width, 20), Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            else
            {
                string label = registerMode ? "⊙\nCreate" : "⊙\nLogin";
                TextRenderer.DrawText(e.Graphics, label, btnLogin.Font,
                    btnLogin.ClientRectangle, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.HidePrefix);
            }
        };
        btnLogin.MouseEnter += (_, _) => { if (btnLogin.Enabled && !loading) btnLogin.BackColor = AccentHover; };
        btnLogin.MouseLeave += (_, _) => { btnLogin.BackColor = Accent; };

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
            Location = new Point(left, 248),
            AutoSize = true,
            LinkColor = AccentDark,
            ActiveLinkColor = Accent,
            ForeColor = TextDark,
            Font = new Font("Segoe UI", 9.5f),
            Cursor = Cursors.Hand
        };
        lnkRegister.Links.Clear();
        lnkRegister.Links.Add(22, 14, "register");
        lnkRegister.LinkClicked += (_, _) => ToggleRegisterMode(!registerMode);

        // server hint
        var lblHint = new Label
        {
            Text = "⌂  " + LicenseClient.BaseUrl,
            Location = new Point(left, 278),
            AutoSize = true,
            ForeColor = Color.FromArgb(130, 130, 150),
            Font = new Font("Segoe UI", 8.5f)
        };

        panelCard.Controls.AddRange(new Control[]
        {
            lblEmail, txtEmail,
            lblPass, txtPassword, chkShow,
            chkRemember, btnLogin,
            lnkRegister, lblHint
        });
        panelBody.Controls.Add(panelCard);

        // Prefill remember-me
        if (LocalLogin.TryRemembered(out string saved, out string savedPw))
        {
            if (!string.IsNullOrEmpty(saved)) txtEmail.Text = saved;
            if (!string.IsNullOrEmpty(savedPw)) txtPassword.Text = savedPw;
            chkRemember.Checked = true;
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
    }

    private static TextBox MakeTextBox(int x, int y, int w, string placeholder)
    {
        var tb = new TextBox
        {
            Location = new Point(x, y),
            Size = new Size(w, 30),
            Font = new Font("Segoe UI", 10.5f),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = InputBg,
            ForeColor = TextDark,
            MaxLength = 120
        };
        tb.GotFocus += (_, _) => { if (tb.Text == placeholder) { tb.Text = ""; tb.ForeColor = TextDark; } };
        tb.LostFocus += (_, _) => { if (string.IsNullOrWhiteSpace(tb.Text)) { tb.Text = placeholder; tb.ForeColor = Color.FromArgb(120, 120, 140); } };
        tb.Text = placeholder;
        tb.ForeColor = Color.FromArgb(120, 120, 140);
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
            var work = Task.Run(async () =>
            {
                string email = txtEmail.Text.Trim();
                string pw = txtPassword.Text;

                if (registerMode)
                {
                    var reg = await LicenseClient.RegisterAsync(email, pw);
                    return (reg, (LicenseClient.Result)null);
                }

                var result = await LicenseClient.LoginAsync(email, pw);
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

            string email2 = txtEmail.Text.Trim();
            string pw2 = txtPassword.Text;
            if (chkRemember.Checked) LocalLogin.SaveRemembered(email2, pw2);
            else LocalLogin.ClearRemembered();

            LoginEmail = email2;
            LoginPlan = loginRes.Plan;
            LoginExpiresAt = loginRes.ExpiresAt;
            LoginDaysLeft = loginRes.DaysLeft;

            string plan = string.IsNullOrEmpty(loginRes.Plan) ? "" : " [" + loginRes.Plan + "]";
            string days = loginRes.DaysLeft > 0 ? " · " + loginRes.DaysLeft + "d left" : "";
            lblStatus.ForeColor = Success;
            lblStatus.Text = "Login OK" + plan + days;
            await Task.Delay(300); // brief success pause
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

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect, int nWidthEllipse, int nHeightEllipse);
}
