#nullable disable
namespace PMKUnlocker;

// Login → "Forgot password?" — server က reset link ကို email ပို့တယ် (၃၀ မိနစ်၊ တစ်ခါသုံး)
public class PasswordResetForm : Form
{
    private readonly TextBox txtEmail;
    private readonly Button btnSend;
    private readonly Label lblStatus;
    private bool busy;

    private static readonly Color CardBg = Color.FromArgb(16, 42, 52);
    private static readonly Color InputBg = Color.FromArgb(41, 38, 92);
    private static readonly Color Accent = Color.FromArgb(34, 211, 238);
    private static readonly Color TextDark = Color.FromArgb(235, 235, 245);
    private static readonly Color TextMuted = Color.FromArgb(165, 172, 202);
    private static readonly Color Success = Color.FromArgb(80, 220, 140);
    private static readonly Color Danger = Color.FromArgb(255, 110, 110);

    public PasswordResetForm(string email)
    {
        Text = "Password Reset";
        ClientSize = new Size(430, 268);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = CardBg;
        ForeColor = TextDark;
        Font = new Font("Segoe UI", 9.5f);

        var lblHead = new Label
        {
            Text = "Password မေ့နေပါသလား?",
            Location = new Point(22, 18),
            AutoSize = true,
            Font = new Font("Segoe UI", 13f, FontStyle.Bold),
            ForeColor = Color.FromArgb(103, 232, 249)
        };
        var lblInfo = new Label
        {
            Text = "Account ရဲ့ email ထည့်ပါ — reset link (၃၀ မိနစ်၊ တစ်ခါသုံး) ကို email ပို့ပေးမယ်။",
            Location = new Point(22, 52),
            Size = new Size(384, 38),
            ForeColor = TextMuted
        };
        var lblEmail = new Label
        {
            Text = "EMAIL ID",
            Location = new Point(22, 96),
            AutoSize = true,
            ForeColor = TextMuted,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
        };
        txtEmail = new TextBox
        {
            Location = new Point(22, 116),
            Size = new Size(384, 30),
            Font = new Font("Segoe UI", 11f),
            BackColor = InputBg,
            ForeColor = TextDark,
            BorderStyle = BorderStyle.FixedSingle,
            Text = email ?? ""
        };
        btnSend = new Button
        {
            Text = "Reset link ပို့",
            Location = new Point(22, 158),
            Size = new Size(384, 40),
            FlatStyle = FlatStyle.Flat,
            BackColor = Accent,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btnSend.FlatAppearance.BorderSize = 0;
        btnSend.Click += async (_, _) => await SendAsync();

        lblStatus = new Label
        {
            Text = "Link မရောက်ရင် admin ထံမှ reset link တောင်းပါ။",
            Location = new Point(22, 208),
            Size = new Size(384, 46),
            ForeColor = TextMuted
        };

        Controls.AddRange(new Control[] { lblHead, lblInfo, lblEmail, txtEmail, btnSend, lblStatus });
        AcceptButton = btnSend;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { Close(); return; }
        base.OnKeyDown(e);
    }

    private async Task SendAsync()
    {
        if (busy) return;
        string email = txtEmail.Text.Trim();
        busy = true;
        btnSend.Enabled = false;
        txtEmail.Enabled = false;
        lblStatus.ForeColor = TextMuted;
        lblStatus.Text = "Server ကို ဆက်သွယ်နေသည်...";
        try
        {
            LicenseClient.Result res = await LicenseClient.ForgotPasswordAsync(email);
            lblStatus.ForeColor = res.Ok ? Success : Danger;
            lblStatus.Text = res.Message;
            if (res.Ok && res.Sent)
                lblStatus.Text += "\nInbox (Spam ပါ) စစ်ပြီး link ကို ဖွင့်ပါ။";
        }
        finally
        {
            busy = false;
            btnSend.Enabled = true;
            txtEmail.Enabled = true;
        }
    }
}
