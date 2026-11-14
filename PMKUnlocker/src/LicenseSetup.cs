namespace PMKUnlocker;

internal static class LicenseSetup
{
    internal static bool Show(IWin32Window owner)
    {
        using var dialog = new Form
        {
            Text = "License server settings", ClientSize = new Size(510, 205),
            StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false
        };
        var help = new Label
        {
            Left = 16, Top = 16, Width = 478, Height = 62,
            Text = "ဆိုင်သုံး License Server ၏ HTTPS လိပ်စာကို ထည့်ပါ။\nServer မရှိသေးလျှင် admin က အရင်တပ်ဆင်ပေးရန် လိုပါသည်။\nLocal testing အတွက်သာ http://127.0.0.1:5260 ကို သုံးပါ။"
        };
        var address = new TextBox { Left = 16, Top = 86, Width = 478,
            Text = LicenseClient.HasConfiguredServer ? LicenseClient.BaseUrl : "",
            PlaceholderText = "https://your-license-server" };
        var error = new Label { Left = 16, Top = 115, Width = 478, Height = 36, ForeColor = Color.Firebrick };
        var save = new Button { Text = "Save", Left = 318, Top = 165, Width = 85 };
        var cancel = new Button { Text = "Cancel", Left = 409, Top = 165, Width = 85, DialogResult = DialogResult.Cancel };
        save.Click += (_, _) =>
        {
            try { LicenseClient.BaseUrl = address.Text; dialog.DialogResult = DialogResult.OK; }
            catch (Exception ex) { error.Text = ex.Message; }
        };
        dialog.Controls.AddRange(new Control[] { help, address, error, save, cancel });
        dialog.AcceptButton = save;
        dialog.CancelButton = cancel;
        return dialog.ShowDialog(owner) == DialogResult.OK;
    }
}
