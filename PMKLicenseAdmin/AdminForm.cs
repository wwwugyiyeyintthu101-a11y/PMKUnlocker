#nullable disable
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace PMKLicenseAdmin;

public class AdminForm : Form
{
    private static readonly Color Bg = Color.FromArgb(18, 20, 26);
    private static readonly Color Card = Color.FromArgb(28, 31, 42);
    private static readonly Color BorderCol = Color.FromArgb(42, 47, 61);
    private static readonly Color FieldBg = Color.FromArgb(14, 16, 22);
    private static readonly Color Fg = Color.FromArgb(232, 232, 240);
    private static readonly Color Muted = Color.FromArgb(154, 160, 175);
    private static readonly Color Accent = Color.FromArgb(167, 139, 250);
    private static readonly Color OkCol = Color.FromArgb(5, 150, 105);
    private static readonly Color Danger = Color.FromArgb(220, 38, 38);
    private static readonly Color Warn = Color.FromArgb(37, 99, 235);
    private static readonly Color Slate = Color.FromArgb(51, 65, 85);

    private readonly TextBox txtKey;
    private readonly TextBox txtServer;
    private readonly TextBox txtEmail;
    private readonly TextBox txtPass;
    private readonly ComboBox cmbPlan;
    private readonly TextBox txtNote;
    private readonly Label lblMsg;
    private readonly DataGridView grid;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public AdminForm()
    {
        Text = "PMK လိုင်စင် စီမံခန့်ခွဲရေး";
        ClientSize = new Size(1100, 700);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Bg;
        Font = new Font("Segoe UI", 9.5f);
        MinimumSize = new Size(900, 560);

        var title = new Label
        {
            Text = "PMK Mobile Tool — လိုင်စင် စီမံခန့်ခွဲရေး",
            Dock = DockStyle.Top,
            Height = 48,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(18, 0, 0, 0),
            ForeColor = Accent,
            Font = new Font("Segoe UI", 13f, FontStyle.Bold),
            BackColor = Bg
        };

        var pnlConn = MakeCard(12, 56, 1076, 96);
        var lblKey = MakeLabel("Admin Key (X-Admin-Key)", 14, 12);
        // Admin key — source မှာ hardcode မလုပ်တော့; server ထုတ်လိုက်သော adminkey.txt ကနေ ကူးထည့်ပါ
        txtKey = MakeBox(14, 34, 280, "admin key (adminkey.txt)");
        txtKey.UseSystemPasswordChar = true;

        var lblSrv = MakeLabel("ဆာဗာ URL", 310, 12);
        txtServer = MakeBox(310, 34, 360, "http://127.0.0.1:5260");
        txtServer.Text = ReadServerUrl();

        var btnLoad = MakeBtn("စာရင်းယူ", 690, 34, 110, OkCol, async (_, _) => await LoadListAsync());
        var btnSearch = MakeBtn("ရှာ", 810, 34, 90, Slate, (_, _) => Search());
        var btnPing = MakeBtn("ချိတ်ဆက်စမ်း", 910, 34, 70, Warn, async (_, _) => await PingAsync());

        lblMsg = new Label
        {
            Text = "",
            Location = new Point(14, 68),
            Size = new Size(1040, 22),
            ForeColor = Muted,
            Font = new Font("Segoe UI", 9f)
        };
        pnlConn.Controls.AddRange(new Control[] { lblKey, txtKey, lblSrv, txtServer, btnLoad, btnSearch, btnPing, lblMsg });

        var pnlForm = MakeCard(12, 160, 1076, 168);
        var hForm = new Label
        {
            Text = "အကောင့် ဖန်တီး / ပြင်ဆင်",
            Location = new Point(14, 10),
            AutoSize = true,
            ForeColor = Fg,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold)
        };

        var lblEmail = MakeLabel("အီးမေးလ်", 14, 36);
        txtEmail = MakeBox(14, 58, 300, "user@gmail.com");

        var lblPass = MakeLabel("စကားဝှက် (အနည်းဆုံး ၄ လုံး — ကွက်လပ် = မပြောင်း)", 330, 36);
        txtPass = MakeBox(330, 58, 260, "စကားဝှက်");

        var lblPlan = MakeLabel("လိုင်စင် အမျိုးအစား", 606, 36);
        cmbPlan = new ComboBox
        {
            Location = new Point(606, 58),
            Size = new Size(200, 30),
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = FieldBg,
            ForeColor = Fg,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10f)
        };
        cmbPlan.Items.AddRange(new object[] { "၁ လ", "၃ လ", "၆ လ", "၁၂ လ" });
        cmbPlan.SelectedIndex = 0;

        var lblNote = MakeLabel("မှတ်စု (မဖြစ်မနေမဟုတ် — ဖုန်း/ဆိုင်)", 822, 36);
        txtNote = MakeBox(822, 58, 230, "ဆိုင်မှတ်စု");

        var btnSave = MakeBtn("သိမ်း", 14, 100, 110, Accent, async (_, _) => await UpsertAsync());
        var btnApprove = MakeBtn("ခွင့်ပြု", 134, 100, 110, Warn, async (_, _) => await ApproveAsync());
        var btnRenew = MakeBtn("သက်တမ်းတိုး", 254, 100, 130, OkCol, async (_, _) => await RenewAsync());
        var btnBlock = MakeBtn("ပိတ်", 394, 100, 80, Danger, async (_, _) => await SetBlockAsync(true));
        var btnUnblock = MakeBtn("ဖွင့်", 484, 100, 90, Slate, async (_, _) => await SetBlockAsync(false));
        var btnDel = MakeBtn("ဖျက်", 584, 100, 80, Danger, async (_, _) => await DeleteAsync());
        var btnClear = MakeBtn("ရှင်း", 674, 100, 80, Slate, (_, _) => ClearForm());

        pnlForm.Controls.AddRange(new Control[]
        {
            hForm, lblEmail, txtEmail, lblPass, txtPass, lblPlan, cmbPlan, lblNote, txtNote,
            btnSave, btnApprove, btnRenew, btnBlock, btnUnblock, btnDel, btnClear
        });

        var pnlGrid = MakeCard(12, 340, 1076, 348);
        grid = new DataGridView
        {
            Location = new Point(10, 10),
            Size = new Size(1056, 328),
            BackgroundColor = FieldBg,
            BorderStyle = BorderStyle.None,
            GridColor = BorderCol,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            ReadOnly = true,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            AllowUserToResizeRows = false
        };
        grid.DefaultCellStyle.BackColor = Card;
        grid.DefaultCellStyle.ForeColor = Fg;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(76, 58, 140);
        grid.DefaultCellStyle.SelectionForeColor = Color.White;
        grid.ColumnHeadersDefaultCellStyle.BackColor = FieldBg;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Accent;
        grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        grid.EnableHeadersVisualStyles = false;
        grid.CellClick += (_, e) =>
        {
            if (e.RowIndex < 0) return;
            if (grid.Rows[e.RowIndex].Cells["colEmail"].Value is string em && !string.IsNullOrEmpty(em))
            {
                txtEmail.Text = em;
                SetMsg("ရွေးထားသည် — " + em, true);
            }
        };
        grid.Columns.AddRange(new DataGridViewColumn[]
        {
            new DataGridViewTextBoxColumn { Name = "colEmail", HeaderText = "အီးမေးလ်", FillWeight = 28 },
            new DataGridViewTextBoxColumn { Name = "colPlan", HeaderText = "လိုင်စင်", FillWeight = 10 },
            new DataGridViewTextBoxColumn { Name = "colExp", HeaderText = "သက်တမ်းကုန်", FillWeight = 14 },
            new DataGridViewTextBoxColumn { Name = "colDays", HeaderText = "ရက်ကျန်", FillWeight = 8 },
            new DataGridViewTextBoxColumn { Name = "colStatus", HeaderText = "အခြေအနေ", FillWeight = 12 },
            new DataGridViewTextBoxColumn { Name = "colNote", HeaderText = "မှတ်စု", FillWeight = 28 }
        });
        pnlGrid.Controls.Add(grid);

        Controls.AddRange(new Control[] { title, pnlConn, pnlForm, pnlGrid });

        AcceptButton = btnLoad;
        Shown += async (_, _) => await LoadListAsync();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int on = 1;
            if (DwmSetWindowAttribute(Handle, 20, ref on, 4) != 0)
                DwmSetWindowAttribute(Handle, 19, ref on, 4);
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private static Panel MakeCard(int x, int y, int w, int h)
    {
        var p = new Panel { Location = new Point(x, y), Size = new Size(w, h), BackColor = Card };
        p.Paint += (_, e) =>
        {
            using var pen = new Pen(BorderCol);
            e.Graphics.DrawRectangle(pen, 0, 0, p.Width - 1, p.Height - 1);
        };
        return p;
    }

    private static Label MakeLabel(string text, int x, int y) => new()
    {
        Text = text,
        Location = new Point(x, y),
        AutoSize = true,
        ForeColor = Muted,
        Font = new Font("Segoe UI", 8.5f)
    };

    private static void PlaceholderGotFocus(object sender, EventArgs e)
    {
        if (sender is TextBox tb && tb.Tag is string ph && tb.Text == ph)
        {
            tb.Text = "";
            tb.ForeColor = Fg;
        }
    }

    private static void PlaceholderLostFocus(object sender, EventArgs e)
    {
        if (sender is TextBox tb && string.IsNullOrWhiteSpace(tb.Text) && tb.Tag is string ph)
        {
            tb.Text = ph;
            tb.ForeColor = Color.FromArgb(130, 130, 150);
        }
    }

    private static TextBox MakeBox(int x, int y, int w, string placeholder)
    {
        var tb = new TextBox
        {
            Location = new Point(x, y),
            Size = new Size(w, 28),
            BackColor = FieldBg,
            ForeColor = Color.FromArgb(130, 130, 150),
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 10f),
            MaxLength = 160,
            Tag = placeholder
        };
        tb.GotFocus += PlaceholderGotFocus;
        tb.LostFocus += PlaceholderLostFocus;
        tb.Text = placeholder;
        return tb;
    }

    private static Button MakeBtn(string text, int x, int y, int w, Color bg, EventHandler onClick)
    {
        var b = new Button
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(w, 32),
            FlatStyle = FlatStyle.Flat,
            BackColor = bg,
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            TabStop = false
        };
        b.FlatAppearance.BorderSize = 0;
        b.Click += onClick;
        return b;
    }

    private static string ReadServerUrl()
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PMKMobileTool", "pmk_license_url.txt");
            if (File.Exists(path))
            {
                string u = File.ReadAllText(path).Trim();
                if (u.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return u.TrimEnd('/');
            }
        }
        catch { }
        return "http://127.0.0.1:5260";
    }

    private string BaseUrl => (txtServer.Text ?? "").Trim().TrimEnd('/');
    private string AdminKey
    {
        get
        {
            string k = (txtKey.Text ?? "").Trim();
            return k == "admin key (adminkey.txt)" ? "" : k; // placeholder မပို့ရ
        }
    }
    private string Email => FieldVal(txtEmail, "user@gmail.com");
    private string Pass => FieldVal(txtPass, "စကားဝှက်");
    private string Note => FieldVal(txtNote, "ဆိုင်မှတ်စု");

    private static string FieldVal(TextBox tb, string placeholder) =>
        string.IsNullOrWhiteSpace(tb.Text) || tb.Text == placeholder ? "" : tb.Text.Trim();

    private string PlanValue => cmbPlan.SelectedIndex switch
    {
        1 => "quarterly",
        2 => "halfyearly",
        3 => "yearly",
        _ => "monthly"
    };

    private int PlanMonths => cmbPlan.SelectedIndex switch { 1 => 3, 2 => 6, 3 => 12, _ => 1 };

    private static string PlanLabel(string p) => p switch
    {
        "quarterly" => "၃ လ",
        "halfyearly" => "၆ လ",
        "yearly" => "၁၂ လ",
        "monthly" => "၁ လ",
        _ => p
    };

    private void SetMsg(string text, bool ok)
    {
        lblMsg.Text = text;
        lblMsg.ForeColor = ok ? Color.FromArgb(110, 231, 183) : Color.FromArgb(252, 165, 165);
    }

    private void ClearForm()
    {
        txtEmail.Text = "user@gmail.com";
        txtEmail.ForeColor = Color.FromArgb(130, 130, 150);
        txtPass.Text = "စကားဝှက်";
        txtPass.ForeColor = Color.FromArgb(130, 130, 150);
        txtNote.Text = "ဆိုင်မှတ်စု";
        txtNote.ForeColor = Color.FromArgb(130, 130, 150);
        cmbPlan.SelectedIndex = 0;
        SetMsg("ရှင်းပြီးပါပြီ", true);
    }

    private async Task<(int status, string body)> RequestAsync(string path, object body)
    {
        try
        {
            using var req = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, BaseUrl + path);
            req.Headers.Add("X-Admin-Key", AdminKey);
            if (body != null)
            {
                string json = JsonSerializer.Serialize(body, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }
            using var resp = await Http.SendAsync(req);
            string text = await resp.Content.ReadAsStringAsync();
            return ((int)resp.StatusCode, text);
        }
        catch (Exception ex)
        {
            return (0, ex.Message);
        }
    }

    private static bool TryOk(string body, out string message)
    {
        message = "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("ok", out var okEl))
            {
                message = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                return okEl.GetBoolean();
            }
        }
        catch { }
        return false;
    }

    private async Task LoadListAsync()
    {
        SetMsg("ဖတ်နေသည်...", true);
        var (status, body) = await RequestAsync("/api/admin/list", null);
        if (status == 401) { SetMsg("Admin key မှားနေသည်", false); return; }
        if (status == 0) { SetMsg("ဆာဗာ မရောက်ပါ (" + BaseUrl + ") — " + body, false); return; }
        if (status != 200) { SetMsg("HTTP " + status, false); return; }

        try
        {
            using var doc = JsonDocument.Parse(body);
            grid.Rows.Clear();
            int pend = 0;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                string email = el.GetProperty("email").GetString() ?? "";
                string plan = el.GetProperty("plan").GetString() ?? "";
                string expRaw = el.GetProperty("expiresAt").GetString() ?? "";
                string exp = expRaw.Length >= 10 ? expRaw[..10] : "";
                int days = el.GetProperty("daysLeft").GetInt32();
                bool blocked = el.GetProperty("blocked").GetBoolean();
                bool pending = el.GetProperty("pending").GetBoolean();
                bool expired = el.GetProperty("expired").GetBoolean();
                string note = el.GetProperty("note").GetString() ?? "";
                string st = blocked ? "ပိတ်ထား" : pending ? "စောင့်ဆိုင်း" : expired ? "သက်တမ်းကုန်" : "အသုံးပြုနေ";
                if (pending) pend++;
                int i = grid.Rows.Add(email, PlanLabel(plan), exp, days, st, note);
                var row = grid.Rows[i];
                Color stColor = st switch
                {
                    "အသုံးပြုနေ" => Color.FromArgb(167, 243, 208),
                    "စောင့်ဆိုင်း" => Color.FromArgb(147, 197, 253),
                    "သက်တမ်းကုန်" => Color.FromArgb(253, 230, 138),
                    _ => Color.FromArgb(254, 202, 202)
                };
                row.Cells["colStatus"].Style.ForeColor = stColor;
                row.Cells["colStatus"].Style.SelectionForeColor = stColor;
            }
            int n = grid.Rows.Count;
            SetMsg("အကောင့် " + n + " ခု" + (pend > 0 ? " — စောင့်ဆိုင်း " + pend + " ခု" : ""), true);
        }
        catch (Exception ex)
        {
            SetMsg("ဖတ်ရာတွင် အမှား — " + ex.Message, false);
        }
    }

    private async Task PingAsync()
    {
        try
        {
            using var resp = await Http.GetAsync(BaseUrl + "/");
            string t = await resp.Content.ReadAsStringAsync();
            SetMsg(resp.IsSuccessStatusCode ? "ဆာဗာ အဆင်ပြေ — " + t : "HTTP " + (int)resp.StatusCode, resp.IsSuccessStatusCode);
        }
        catch (Exception ex)
        {
            SetMsg("ဆာဗာ မရောက်ပါ — " + ex.Message, false);
        }
    }

    private void Search()
    {
        string q = Email.ToLowerInvariant();
        int n = 0;
        foreach (DataGridViewRow row in grid.Rows)
        {
            bool hit = string.IsNullOrEmpty(q) ||
                (row.Cells["colEmail"].Value?.ToString() ?? "").ToLowerInvariant().Contains(q) ||
                (row.Cells["colNote"].Value?.ToString() ?? "").ToLowerInvariant().Contains(q);
            row.Visible = hit;
            if (hit) n++;
        }
        SetMsg("\"" + Email + "\" အတွက် တွေ့သည် " + n + " ခု", true);
    }

    private bool RequireEmail()
    {
        if (string.IsNullOrEmpty(Email))
        {
            SetMsg("အီးမေးလ် ထည့်ပါ (row နှိပ်၍လည်း ရွေးနိုင်)", false);
            return false;
        }
        return true;
    }

    private async Task UpsertAsync()
    {
        if (!RequireEmail()) return;
        var (status, body) = await RequestAsync("/api/admin/upsert", new
        {
            email = Email,
            password = Pass,
            plan = PlanValue,
            note = Note
        });
        await HandleActionAsync(status, body, "သိမ်းခြင်း");
    }

    private async Task ApproveAsync()
    {
        if (!RequireEmail()) return;
        var (status, body) = await RequestAsync("/api/admin/approve", new { email = Email, plan = PlanValue });
        await HandleActionAsync(status, body, "ခွင့်ပြုခြင်း");
    }

    private async Task RenewAsync()
    {
        if (!RequireEmail()) return;
        var (status, body) = await RequestAsync("/api/admin/renew", new { email = Email, months = PlanMonths });
        await HandleActionAsync(status, body, "သက်တမ်းတိုးခြင်း");
    }

    private async Task SetBlockAsync(bool blocked)
    {
        if (!RequireEmail()) return;
        var (status, body) = await RequestAsync("/api/admin/block", new { email = Email, blocked });
        await HandleActionAsync(status, body, blocked ? "ပိတ်ခြင်း" : "ဖွင့်ခြင်း");
    }

    private async Task DeleteAsync()
    {
        if (!RequireEmail()) return;
        if (MessageBox.Show(Email + " ကို ဖျက်မလား?", "အတည်ပြုပါ",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        var (status, body) = await RequestAsync("/api/admin/delete", new { email = Email, blocked = true });
        await HandleActionAsync(status, body, "ဖျက်ခြင်း");
    }

    private async Task HandleActionAsync(int status, string body, string op)
    {
        if (status == 401) { SetMsg("Admin key မှားနေသည်", false); return; }
        if (status == 0) { SetMsg(op + " မအောင်မြင် — ဆာဗာ မရောက်ပါ (" + BaseUrl + ") — " + body, false); return; }
        if (status != 200) { SetMsg(op + " HTTP " + status, false); return; }

        if (TryOk(body, out string msg))
        {
            SetMsg(string.IsNullOrEmpty(msg) ? op + " ပြီးပါပြီ" : msg, true);
            await LoadListAsync();
        }
        else
        {
            SetMsg(string.IsNullOrEmpty(msg) ? op + " မအောင်မြင်ပါ" : msg, false);
        }
    }
}
