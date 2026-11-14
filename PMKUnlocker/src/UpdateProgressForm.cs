namespace PMKUnlocker;

// remember-me token auto-login က login screen ကို ကျော်တာမို့ update gate အတွက်
// login card မပါတဲ့ သေးငယ်တဲ့ progress window (check / download ကို ဒီမှာ ပြ)
internal sealed class UpdateProgressForm : Form
{
    private readonly Label lbl;
    private readonly ProgressBar bar;

    internal UpdateProgressForm()
    {
        Text = "PMK Mobile Tool — Update";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(380, 100);
        Font = new Font("Segoe UI", 9.5f);

        lbl = new Label
        {
            AutoSize = false,
            Location = new Point(16, 20),
            Size = new Size(348, 22),
            Text = "Checking for updates..."
        };
        bar = new ProgressBar
        {
            Location = new Point(16, 56),
            Size = new Size(348, 18),
            Style = ProgressBarStyle.Continuous,
            Visible = false
        };

        Controls.Add(lbl);
        Controls.Add(bar);
    }

    internal void SetStatus(GatePhase phase, string text)
    {
        lbl.Text = text;
        if (phase is GatePhase.Downloading or GatePhase.Verified or GatePhase.Failed)
            bar.Visible = true;
    }

    internal void SetProgress(int pct)
    {
        bar.Visible = true;
        if (pct > bar.Value && pct <= 100) bar.Value = pct;
    }
}
