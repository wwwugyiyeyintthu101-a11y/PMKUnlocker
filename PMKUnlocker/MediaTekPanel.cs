#nullable disable
using PMKUnlocker.MediaTek;
using PMKUnlocker.MtkFirmware;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PMKUnlocker;

public partial class Form1
{
    private TextBox mediaTekScatter;
    private DataGridView mediaTekGrid;
    private Label mediaTekSummary;
    private CheckBox mediaTekSkipUserdata;
    private FirmwarePackage mediaTekPackage;
    private CancellationTokenSource mediaTekCancel;
    private MtkClientEngine mediaTekEngine;
    private MtkConnection mediaTekConnection;
    // MST-style display state — one flag set per mtkclient session
    private bool mstSearching, mstHwInfo, mstStorage, mstInternal;
    private readonly List<string> mstStorageBuf = new();

    // ================= MEDIAKET TAB (Qualcomm style — 3D buttons + group panels) =================
    // Connection picker + DA/Auth/Preloader = အပေါ်မှာ အမြဲပြ
    // အောက်မှာ sub-tabs (INFO / BACKUP / FLASH) — တစ်ခုချင်းစီရဲ့ content ပဲ ပြတယ်
    private ComboBox cmbMtkBrand;
    private ComboBox cmbMtkModel;
    private Label lblMtkDeviceStatus;
    private TextBox txtMtkDaBox;
    private TextBox txtMtkAuthBox;
    private TextBox txtMtkPreBox;
    private TabControl mtkModes;
    private Dictionary<string, Dictionary<string, MtkDeviceInfo>> mtkDeviceDb = new();
    // ဒီ session က live GPT cache — NV Restore က GPT re-read မလုပ်ချင်ရင် (1 plug cycle) ဒါကိုသုံးတယ်
    private IReadOnlyList<MtkPartition> mediaTekLiveTable;
    private void BuildMediaTekTab()
    {
        // ---- Device picker (QC LOADER PICKER ပုံစံ — Brand/Model → DA/Auth/Preloader auto-fill) ----
        Panel grpMtkPicker = new()
        {
            Location = new Point(15, 11),
            Size = new Size(880, 76),
            BackColor = Color.FromArgb(23, 26, 32),
            Tag = "grp"
        };
        EnsureThemePaint(grpMtkPicker);
        var lblMtkTitle = new Label { Text = "🔌 MediaTek Device Picker", Location = new Point(9, 13), AutoSize = true, ForeColor = Color.FromArgb(86, 145, 250), Font = new Font("Segoe UI", 8.5f, FontStyle.Bold) };
        var lblBrandTag = new Label { Text = "Brand:", Location = new Point(175, 14), AutoSize = true, ForeColor = Color.Gainsboro, Font = new Font("Segoe UI", 8.5f) };
        cmbMtkBrand = new ComboBox { Location = new Point(225, 10), Size = new Size(150, 26), DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(38, 42, 50), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9f) };
        var lblModelTag = new Label { Text = "Model:", Location = new Point(390, 14), AutoSize = true, ForeColor = Color.Gainsboro, Font = new Font("Segoe UI", 8.5f) };
        cmbMtkModel = new ComboBox { Location = new Point(440, 10), Size = new Size(180, 26), DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(38, 42, 50), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9f) };
        lblMtkDeviceStatus = new Label { Text = "Auto Detect (no manual DA selected)", Location = new Point(630, 14), AutoSize = false, Size = new Size(240, 20), ForeColor = Color.FromArgb(86, 145, 250), Font = new Font("Segoe UI", 7.5f), AutoEllipsis = true };

        // Row 2: DA / Auth / Preloader
        var txtMtkDa = MtkFileRow("DA Agent", () => mtkDaPath, p => mtkDaPath = p, "Download agent|*.bin;*.img|All files|*.*");
        var txtMtkAuth = MtkFileRow("Auth", () => mtkAuthPath, p => mtkAuthPath = p, "Authentication|*.auth;*.bin|All files|*.*");
        var txtMtkPre = MtkFileRow("Preloader", () => mtkPreloaderPath, p => mtkPreloaderPath = p, "Preloader|*.bin|All files|*.*");
        txtMtkDaBox = txtMtkDa.Controls.OfType<TextBox>().FirstOrDefault();
        txtMtkAuthBox = txtMtkAuth.Controls.OfType<TextBox>().FirstOrDefault();
        txtMtkPreBox = txtMtkPre.Controls.OfType<TextBox>().FirstOrDefault();
        txtMtkDa.Location = new Point(9, 44);
        txtMtkAuth.Location = new Point(300, 44);
        txtMtkPre.Location = new Point(592, 44);

        grpMtkPicker.Controls.AddRange(new Control[] { lblMtkTitle, lblBrandTag, cmbMtkBrand, lblModelTag, cmbMtkModel, lblMtkDeviceStatus, txtMtkDa, txtMtkAuth, txtMtkPre });
        LoadMtkDeviceDb();
        cmbMtkBrand.SelectedIndexChanged += CmbMtkBrand_SelectedIndexChanged;
        cmbMtkModel.SelectedIndexChanged += CmbMtkModel_SelectedIndexChanged;

        // ---- Sub-tabs: INFO / BACKUP / FLASH ----
        mtkModes = new TabControl()
        {
            Location = new Point(15, 93),
            Size = new Size(880, 482),
            Appearance = TabAppearance.Normal,
            DrawMode = TabDrawMode.Normal,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold)
        };
        mtkModes.TabPages.Add(new TabPage("INFO") { BackColor = Color.FromArgb(28, 31, 38) });
        mtkModes.TabPages.Add(new TabPage("UNLOCK") { BackColor = Color.FromArgb(28, 31, 38) });
        mtkModes.TabPages.Add(new TabPage("BACKUP") { BackColor = Color.FromArgb(28, 31, 38) });
        mtkModes.TabPages.Add(new TabPage("FLASH") { BackColor = Color.FromArgb(28, 31, 38) });
        foreach (TabPage tp in mtkModes.TabPages)
        {
            tp.Padding = new Padding(8);
            tp.ForeColor = Color.Gainsboro;
        }

        // ================= INFO TAB =================
        TabPage infoTab = mtkModes.TabPages[0];
        Button btnMtkReadInfo = Create3DButton("🔍 Read Info / GPT", UiX(0), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Cyan);
        btnMtkReadInfo.Click += async (_, _) => { try { await RunMediaTekWorkflowAsync(async () => { await ReadMediaTekGptAsync(true); }); } catch (Exception ex) { Log("[FAIL] " + ex.Message, Color.Red); } };
        if (toolTipMain != null) Tip(btnMtkReadInfo, "mtkclient printgpt — read device GPT into Flash grid.");

        Button btnMtkSaveGpt = Create3DButton("📋 Save GPT", UiX(1), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Purple);
        btnMtkSaveGpt.Click += async (_, _) => { try { await SaveMediaTekGptAsync(); } catch (Exception ex) { Log("[FAIL] " + ex.Message, Color.Red); } };

        Button btnMtkReset = Create3DButton("🔄 Reset Device", UiX(2), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Green);
        btnMtkReset.Click += async (_, _) => { try { await RunMediaTekWorkflowAsync(async () => { var r = await MediaTekCommandAsync(new[] { "reset" }); r.RequireSuccess(); Log("[i] Reset command returned. Check the phone; Android boot is not verified.", Color.Cyan); }); } catch (Exception ex) { Log("[FAIL] " + ex.Message, Color.Red); } };

        Panel grpInfo = CreateGroupPanel("SERVICE", UiX(0), UiY(0), 3, btnMtkReadInfo, btnMtkSaveGpt, btnMtkReset);
        infoTab.Controls.Add(grpInfo);

        Label lblInfoHint = new()
        {
            Location = new Point(UiX(0), UiY(1) + 10),
            Size = new Size(850, 40),
            Text = "Power off the phone, connect in MediaTek mode (BROM / Preloader), then select its COM port. DA / Auth / Preloader depend on the device. Read Info loads the live partition table into FLASH tab.",
            ForeColor = Color.Gray,
            Font = new Font("Segoe UI", 8f),
            AutoEllipsis = true
        };
        infoTab.Controls.Add(lblInfoHint);

        // ================= UNLOCK TAB =================
        TabPage unlockTab = mtkModes.TabPages[1];
        // mtkclient syntax: e <partition> = erase, w <partition> <file> = write
        Button btnMtkFrpRemove = Create3DButton("🔑 FRP Remove", UiX(0), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Cyan);
        btnMtkFrpRemove.Click += async (_, _) => { try { await RunMediaTekWorkflowAsync(async () => await MtkEraseAndResetAsync("frp", "[OK] FRP partition erased — Google account lock removed.")); } catch (Exception ex) { Log("[FAIL] " + ex.Message, Color.Red); } };
        if (toolTipMain != null) Tip(btnMtkFrpRemove, "mtkclient e frp + reset in ONE session — erase FRP partition, Google account lock removal.");

        Button btnMtkUserlockRemove = Create3DButton("📱 Userlock Remove", UiX(1), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Purple);
        btnMtkUserlockRemove.Click += async (_, _) => { try { await RunMediaTekWorkflowAsync(async () => await MtkEraseAndResetAsync("userdata", "[OK] Userdata erased — pattern/PIN/password lock removed. All user data lost.")); } catch (Exception ex) { Log("[FAIL] " + ex.Message, Color.Red); } };
        if (toolTipMain != null) Tip(btnMtkUserlockRemove, "mtkclient e userdata + reset in ONE session — removes screen lock. ALL USER DATA IS LOST.");

        Button btnMtkMiCloud = Create3DButton("☁️ Mi Cloud Remove", UiX(2), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Orange);
        btnMtkMiCloud.Click += async (_, _) => { try { await RunMediaTekWorkflowAsync(async () => await MtkEraseAndResetAsync("persist", "[OK] Persist erased — Mi Cloud / account data cleared.")); } catch (Exception ex) { Log("[FAIL] " + ex.Message, Color.Red); } };
        if (toolTipMain != null) Tip(btnMtkMiCloud, "mtkclient e persist + reset in ONE session — Xiaomi Mi Cloud / Mi Account removal.");

        Button btnMtkNvReset = Create3DButton("📶 NV Reset", UiX(0), UiY(1), UiBtnW, UiBtnH, ButtonTheme.Cyan);
        btnMtkNvReset.Click += async (_, _) => { try { await RunMediaTekWorkflowAsync(async () => await MtkEraseAndResetAsync("nvdata", "[OK] NV data erased — IMEI/baseband reset. Restore NV backup after.")); } catch (Exception ex) { Log("[FAIL] " + ex.Message, Color.Red); } };
        if (toolTipMain != null) Tip(btnMtkNvReset, "mtkclient e nvdata + reset in ONE session — NV/IMEI reset. Backup NV first!");

        Button btnMtkSeccfgUnlock = Create3DButton("🔓 Bootloader Unlock", UiX(1), UiY(1), UiBtnW, UiBtnH, ButtonTheme.Green);
        btnMtkSeccfgUnlock.Click += async (_, _) => { try { await SeccfgAutoPatchAsync(true); } catch (Exception ex) { Log("[FAIL] " + ex.Message, Color.Red); } };
        if (toolTipMain != null) Tip(btnMtkSeccfgUnlock, "Native da seccfg unlock + cached patch fallback + reset in ONE session (single plug cycle), auto-retry once.");

        Button btnMtkSeccfgLock = Create3DButton("🔒 Bootloader Relock", UiX(2), UiY(1), UiBtnW, UiBtnH, ButtonTheme.Orange);
        btnMtkSeccfgLock.Click += async (_, _) => { try { await SeccfgAutoPatchAsync(false); } catch (Exception ex) { Log("[FAIL] " + ex.Message, Color.Red); } };
        if (toolTipMain != null) Tip(btnMtkSeccfgLock, "Native da seccfg lock + cached patch fallback + reset in ONE session (single plug cycle), auto-retry once.");

        Panel grpUnlock = CreateGroupPanel("UNLOCK / REPAIR", UiX(0), UiY(0), 3, btnMtkFrpRemove, btnMtkUserlockRemove, btnMtkMiCloud);
        Panel grpUnlock2 = CreateGroupPanel("NV / SECURE BOOT", UiX(0), UiY(2), 3, btnMtkNvReset, btnMtkSeccfgUnlock, btnMtkSeccfgLock);

        Button btnMtkNvBackup = Create3DButton("📂 NV Backup", UiX(0), UiY(4), UiBtnW, UiBtnH, ButtonTheme.Cyan);
        btnMtkNvBackup.Click += async (_, _) => { try { await NvBackupAsync(); } catch (Exception ex) { Log("[FAIL] " + ex.Message, Color.Red); } };
        if (toolTipMain != null) Tip(btnMtkNvBackup, "Read nvram/nvdata/nvcfg/proinfo/protect1/protect2 in ONE session (one plug cycle) — auto folder under NVBackup.");

        Button btnMtkNvRestore = Create3DButton("⬆️ NV Restore", UiX(1), UiY(4), UiBtnW, UiBtnH, ButtonTheme.Purple);
        btnMtkNvRestore.Click += async (_, _) => { try { await NvRestoreAsync(); } catch (Exception ex) { Log("[FAIL] " + ex.Message, Color.Red); } };
        if (toolTipMain != null) Tip(btnMtkNvRestore, "NV backup folder → write + reset in ONE plug cycle. Sizes pre-checked against cached GPT; otherwise printgpt in the same run verifies them.");

        Button btnMtkNvErase = Create3DButton("🗑 NV Erase", UiX(2), UiY(4), UiBtnW, UiBtnH, ButtonTheme.Red);
        btnMtkNvErase.Click += async (_, _) =>
        {
            if (MessageBox.Show(this, "Erase ALL NV partitions?\n\n" + string.Join(", ", NvPartitions) + "\n\nIMEI/baseband will be lost — restore an NV backup afterwards.", "NV Erase", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try { await RunMediaTekWorkflowAsync(async () => await MtkEraseAndResetAsync(string.Join(",", NvPartitions), "[OK] NV erased — all NV partitions wiped. Restore NV backup now.")); } catch (Exception ex) { Log("[FAIL] " + ex.Message, Color.Red); }
        };
        if (toolTipMain != null) Tip(btnMtkNvErase, "mtkclient e nvram,nvdata,nvcfg,proinfo,protect1,protect2 + reset in ONE session. DESTRUCTIVE — backup first.");

        Panel grpNv = CreateGroupPanel("NV BACKUP / RESTORE / ERASE", UiX(0), UiY(4), 3, btnMtkNvBackup, btnMtkNvRestore, btnMtkNvErase);
        unlockTab.Controls.AddRange(new Control[] { grpUnlock, grpUnlock2, grpNv });

        Label lblUnlockHint = new()
        {
            Location = new Point(UiX(0), UiY(6) + 8),
            Size = new Size(850, 50),
            Text = "Bootloader Unlock/Relock and all erase ops run in ONE mtkclient session (native + fallback + reset) — one unplug/plug per operation, auto-retry once on failure. Backup NV before NV Erase/NV Reset; NV Backup and NV Restore each run in one plug cycle. Userlock Remove erases all user data.",
            ForeColor = Color.Gray,
            Font = new Font("Segoe UI", 8f),
            AutoEllipsis = true
        };
        unlockTab.Controls.Add(lblUnlockHint);

        // ================= BACKUP TAB =================
        TabPage backupTab = mtkModes.TabPages[2];
        Button btnMtkBackup = Create3DButton("💾 Backup Selected", UiX(0), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Orange);
        btnMtkBackup.Click += async (_, _) => { try { await BackupMediaTekAsync(); } catch (Exception ex) { Log("[FAIL] " + ex.Message, Color.Red); } };

        Button btnMtkBackupNv = Create3DButton("📂 Backup NV", UiX(1), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Cyan);
        btnMtkBackupNv.Click += async (_, _) => { try { await BackupMediaTekAsync(true); } catch (Exception ex) { Log("[FAIL] " + ex.Message, Color.Red); } };

        Button btnMtkRestore = Create3DButton("⬆️ Restore Image", UiX(2), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Purple);
        btnMtkRestore.Click += async (_, _) => { try { await RestoreMediaTekImageAsync(); } catch (Exception ex) { Log("[FAIL] " + ex.Message, Color.Red); } };

        Panel grpBackup = CreateGroupPanel("BACKUP / RESTORE", UiX(0), UiY(0), 3, btnMtkBackup, btnMtkBackupNv, btnMtkRestore);
        backupTab.Controls.Add(grpBackup);

        Label lblBackupHint = new()
        {
            Location = new Point(UiX(0), UiY(1) + 10),
            Size = new Size(850, 40),
            Text = "Read Info first, then tick partitions in FLASH tab. Backups are checked against live partition sizes and stored in a new folder. Restore writes one selected USER partition (exact-size raw backup only).",
            ForeColor = Color.Gray,
            Font = new Font("Segoe UI", 8f),
            AutoEllipsis = true
        };
        backupTab.Controls.Add(lblBackupHint);

        // ================= FLASH TAB =================
        TabPage flashTab = mtkModes.TabPages[3];

        // Scatter picker row
        mediaTekScatter = new TextBox
        {
            Location = new Point(0, 4),
            Size = new Size(600, 26),
            ReadOnly = true,
            BackColor = Color.FromArgb(38, 42, 50),
            ForeColor = Color.Gainsboro,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 9f)
        };
        Button btnMtkPickScatter = Create3DButton("📂 Scatter…", 610, 3, 100, 28, ButtonTheme.Purple);
        btnMtkPickScatter.Click += (_, _) => PickMediaTekScatter();
        Button btnMtkSelectNormal = Create3DButton("☑ Normal", 715, 3, 90, 28, ButtonTheme.Cyan);
        btnMtkSelectNormal.Click += (_, _) => SelectMediaTekNormal();

        flashTab.Controls.AddRange(new Control[] { mediaTekScatter, btnMtkPickScatter, btnMtkSelectNormal });

        // Partition grid — tab အပြည့် ဖြည့်
        mediaTekGrid = new DataGridView
        {
            Location = new Point(0, 38),
            Size = new Size(856, 370),
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            RowHeadersVisible = false,
            BackgroundColor = Color.FromArgb(28, 31, 38),
            BorderStyle = BorderStyle.FixedSingle,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            Font = new Font("Segoe UI", 8.5f)
        };
        mediaTekGrid.DefaultCellStyle.BackColor = Color.FromArgb(28, 31, 38);
        mediaTekGrid.DefaultCellStyle.ForeColor = Color.Gainsboro;
        mediaTekGrid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(86, 145, 250);
        mediaTekGrid.DefaultCellStyle.SelectionForeColor = Color.White;
        mediaTekGrid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(23, 26, 32);
        mediaTekGrid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(86, 145, 250);
        mediaTekGrid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        mediaTekGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "selected", HeaderText = "✓", Width = 34, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
        foreach (var col in new[] { ("partition", "Partition"), ("file", "Image"), ("offset", "Offset"), ("size", "Size"), ("status", "Status") })
            mediaTekGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = col.Item1, HeaderText = col.Item2, ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        mediaTekGrid.CurrentCellDirtyStateChanged += (_, _) => { if (mediaTekGrid.IsCurrentCellDirty) mediaTekGrid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        mediaTekGrid.CellValueChanged += (_, _) => RefreshMediaTekSummary();
        flashTab.Controls.Add(mediaTekGrid);

        // Bottom row: options + flash button + summary (grid အောက် 10px ခွာ)
        mediaTekSkipUserdata = new CheckBox
        {
            Text = "Skip userdata / metadata",
            Location = new Point(0, 418),
            AutoSize = true,
            Checked = true,
            ForeColor = Color.FromArgb(255, 200, 90),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
        };
        Button btnMtkClearSel = Create3DButton("✕ Clear", 195, 414, 80, 28, ButtonTheme.Orange);
        btnMtkClearSel.Click += (_, _) => { foreach (DataGridViewRow row in mediaTekGrid.Rows) row.Cells[0].Value = false; };

        Button btnMtkFlash = Create3DButton("◉ FLASH SELECTED", 656, 410, 200, 36, ButtonTheme.Red);
        btnMtkFlash.Click += async (_, _) => { try { await FlashMediaTekAsync(); } catch (Exception ex) { Log("[FAIL] " + ex.Message, Color.Red); } };
        if (toolTipMain != null) Tip(btnMtkFlash, "Write selected USER partitions via mtkclient. GPT and boot regions are not written.");

        mediaTekSummary = new Label
        {
            Location = new Point(0, 455),
            Size = new Size(856, 22),
            Text = "Ready · no firmware selected",
            AutoEllipsis = true,
            ForeColor = Color.Gray,
            Font = new Font("Segoe UI", 8f)
        };

        flashTab.Controls.AddRange(new Control[] { mediaTekSkipUserdata, btnMtkClearSel, btnMtkFlash, mediaTekSummary });

        // ---- Add everything to tabMtk ----
        tabMtk.Controls.AddRange(new Control[] { grpMtkPicker, mtkModes });

        // ---- helper: DA/Auth/Preloader row (label + textbox + browse) ----
        Control MtkFileRow(string label, Func<string> get, Action<string> set, string filter)
        {
            Panel rowPanel = new() { Size = new Size(285, 30), BackColor = Color.Transparent, Dock = DockStyle.None };
            rowPanel.Controls.Add(new Label { Text = label, Location = new Point(0, 6), AutoSize = true, ForeColor = Color.Gainsboro, Font = new Font("Segoe UI", 8f) });
            var text = new TextBox { Location = new Point(60, 2), Size = new Size(160, 24), Text = get(), ReadOnly = true, BackColor = Color.FromArgb(38, 42, 50), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 8f) };
            text.TextChanged += (_, _) => set(text.Text.Trim());
            rowPanel.Controls.Add(text);
            var browse = new Button { Text = "…", Location = new Point(225, 2), Size = new Size(28, 24), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(45, 50, 60), ForeColor = Color.White };
            browse.Click += (_, _) => { using var pick = new OpenFileDialog { Filter = filter }; if (pick.ShowDialog(this) == DialogResult.OK) text.Text = pick.FileName; };
            rowPanel.Controls.Add(browse);
            return rowPanel;
        }
    }

    // ================= MTK DEVICE PICKER (pmk_mtk_devices.json) =================
    private sealed record MtkDeviceInfo(string Da, string Auth, string Preloader);

    private void LoadMtkDeviceDb()
    {
        try
        {
            cmbMtkBrand.Items.Add("Auto Detect");
            string path = Path.Combine(Application.StartupPath, "pmk_mtk_devices.json");
            if (!File.Exists(path)) { cmbMtkBrand.SelectedIndex = 0; return; }
            var raw = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Dictionary<string, string>>>>(File.ReadAllText(path));
            if (raw != null)
            {
                foreach (var brandEntry in raw)
                {
                    var modelMap = new Dictionary<string, MtkDeviceInfo>(StringComparer.OrdinalIgnoreCase);
                    foreach (var modelEntry in brandEntry.Value)
                    {
                        var files = modelEntry.Value;
                        modelMap[modelEntry.Key] = new MtkDeviceInfo(
                            files.GetValueOrDefault("da", ""),
                            files.GetValueOrDefault("auth", ""),
                            files.GetValueOrDefault("preloader", ""));
                    }
                    mtkDeviceDb[brandEntry.Key] = modelMap;
                }
            }
            foreach (var brand in mtkDeviceDb.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
                cmbMtkBrand.Items.Add(brand);
            cmbMtkBrand.SelectedIndex = 0;
        }
        catch (Exception ex) { cmbMtkBrand.Items.Add("(load error: " + ex.Message + ")"); cmbMtkBrand.SelectedIndex = 0; }
    }

    private void CmbMtkBrand_SelectedIndexChanged(object sender, EventArgs e)
    {
        cmbMtkModel.Items.Clear();
        cmbMtkModel.Enabled = false;
        if (cmbMtkBrand.SelectedItem is not string brand) return;
        if (brand == "Auto Detect")
        {
            mtkDaPath = ""; mtkAuthPath = ""; mtkPreloaderPath = "";
            lblMtkDeviceStatus.Text = "Auto Detect (no manual DA selected)";
            lblMtkDeviceStatus.ForeColor = Color.FromArgb(86, 145, 250);
            return;
        }
        if (!mtkDeviceDb.TryGetValue(brand, out var models)) return;
        foreach (var model in models.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            cmbMtkModel.Items.Add(model);
        cmbMtkModel.Enabled = true;
        if (cmbMtkModel.Items.Count > 0) cmbMtkModel.SelectedIndex = 0;
    }

    private void CmbMtkModel_SelectedIndexChanged(object sender, EventArgs e)
    {
        if (cmbMtkBrand.SelectedItem is not string brand || cmbMtkModel.SelectedItem is not string model) return;
        if (brand == "Auto Detect" || !mtkDeviceDb.TryGetValue(brand, out var models) || !models.TryGetValue(model, out var info)) return;
        string baseDir = Path.Combine(Application.StartupPath, "mtk");
        mtkDaPath = ResolveMtkPath(info.Da, baseDir);
        mtkAuthPath = ResolveMtkPath(info.Auth, baseDir);
        mtkPreloaderPath = ResolveMtkPath(info.Preloader, baseDir);
        if (txtMtkDaBox != null) txtMtkDaBox.Text = mtkDaPath;
        if (txtMtkAuthBox != null) txtMtkAuthBox.Text = mtkAuthPath;
        if (txtMtkPreBox != null) txtMtkPreBox.Text = mtkPreloaderPath;
        bool daOk = !string.IsNullOrWhiteSpace(mtkDaPath) && File.Exists(mtkDaPath);
        lblMtkDeviceStatus.Text = daOk ? brand + " " + model + " · DA loaded"
            : string.IsNullOrWhiteSpace(mtkDaPath) ? brand + " " + model + " · Auto Detect (BROM bypass)"
            : brand + " " + model + " · DA not found";
        lblMtkDeviceStatus.ForeColor = daOk ? Color.LightGreen
            : string.IsNullOrWhiteSpace(mtkDaPath) ? Color.FromArgb(86, 145, 250) : Color.Orange;
    }

    private static string ResolveMtkPath(string relative, string baseDir)
    {
        if (string.IsNullOrWhiteSpace(relative)) return "";
        return Path.IsPathRooted(relative) ? relative : Path.Combine(baseDir, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    private void PickMediaTekScatter()
    {
        using var pick = new OpenFileDialog { Filter = "MediaTek scatter|*scatter*.txt|Text files|*.txt" };
        if (pick.ShowDialog(this) != DialogResult.OK) return;
        mediaTekPackage = ScatterParser.Load(pick.FileName);
        mediaTekScatter.Text = pick.FileName;
        mediaTekGrid.Rows.Clear();
        foreach (var entry in mediaTekPackage.Entries)
        {
            bool supported = IsMediaTekUserImage(entry);
            int i = mediaTekGrid.Rows.Add(supported && FlashRisk.DefaultSelected(entry), entry.Name, entry.FileName, entry.StartHex, FormatBytesLong(entry.Size),
                !supported ? "Unavailable" : FlashRisk.Classify(entry) == RiskLevel.HighRisk ? "High risk" : "Ready");
            mediaTekGrid.Rows[i].Tag = entry;
            mediaTekGrid.Rows[i].Cells[0].ReadOnly = !supported;
        }
        foreach (var issue in mediaTekPackage.Issues) Log(issue.ToString(), issue.Severity == IssueSeverity.Error ? Color.OrangeRed : Color.Gray);
        RefreshMediaTekSummary();
    }
    private static bool IsMediaTekUserImage(ScatterEntry e) => e.Selectable && e.ImageExists && e.ImageError == null &&
        e.EffectiveImageSize is > 0 && e.EffectiveImageSize <= e.Size && e.RegionRaw == "EMMC_USER" &&
        !new[] { "preloader", "pgpt", "sgpt", "gpt", "otp", "flashinfo" }.Contains(e.Name.ToLowerInvariant());
    private List<string> SelectedMediaTekNames() => mediaTekGrid.Rows.Cast<DataGridViewRow>()
        .Where(r => r.Cells[0].Value is true).Select(r => (string)r.Cells[1].Value).ToList();
    private void SelectMediaTekNormal()
    {
        foreach (DataGridViewRow r in mediaTekGrid.Rows)
            r.Cells[0].Value = r.Tag is ScatterEntry e && IsMediaTekUserImage(e) && FlashRisk.DefaultSelected(e);
    }
    private void RefreshMediaTekSummary()
    {
        if (mediaTekSummary != null) mediaTekSummary.Text = $"{SelectedMediaTekNames().Count} selected · {mediaTekPackage?.Platform ?? "Live device"} · {mediaTekPackage?.Storage ?? "GPT"}";
    }
    private async Task RunMediaTekWorkflowAsync(Func<Task> operation)
    {
        await RunFlashWorkflowAsync(async () =>
        {
            mediaTekEngine = new MtkClientEngine();
            using var cancel = new CancellationTokenSource(); mediaTekCancel = cancel;
            // Port က dead field — engine က --serialport ဘယ်တော့မှ မထည့် (auto USB detect)
            mediaTekConnection = new("", mtkDaPath, mtkAuthPath, mtkPreloaderPath);
            try { await operation(); }
            catch (OperationCanceledException) { workflowFailed = true; Log("[!] MediaTek operation stopped.", Color.Orange); }
            finally { mediaTekWriteActive = false; mediaTekCancel = null; }
        }, autoRebootAfter: false);
    }
    private async Task<MtkCommandResult> MediaTekCommandAsync(string[] args, bool writes = false, TimeSpan? timeout = null, string attempt = null)
    {
        if (stopRequested) throw new OperationCanceledException();
        mstSearching = mstHwInfo = mstStorage = mstInternal = false;
        mstStorageBuf.Clear();
        // attempt ပါရင် caller က ကိုယ်တိုင် header ရိုက်ထားပြီး — ထပ်မရိုက်နှင့်
        if (attempt == null)
            Log($" Operation : {MtkOperationName(args)}", Color.White);
        // launch မတိုင်ခင် ဖုန်း USB က ဖြုတ်ပြီးမှ mtkclient စ — attach-at-enumeration သာ
        await EnsureMediaTekUnpluggedAsync();
        Log(" Waiting for device — plug the phone in BROM/Preloader mode now...", Color.Yellow);
        mediaTekWriteActive = writes;
        using var timeoutCts = timeout.HasValue ? CancellationTokenSource.CreateLinkedTokenSource(mediaTekCancel.Token) : null;
        if (timeoutCts != null) timeoutCts.CancelAfter(timeout.Value);
        CancellationToken token = timeoutCts?.Token ?? mediaTekCancel.Token;
        try
        {
            return await mediaTekEngine.RunAsync(mediaTekConnection, args,
                line => LogMtkLine(line), token, writes);
        }
        finally { FlushMtkStorage(); mediaTekWriteActive = false; }
    }

    // MST-style operation header
    private static string MtkOperationName(string[] args)
    {
        if (args.Length >= 2 && args[0] == "multi")
        {
            string c = args[1];
            if (c.Contains("da seccfg unlock", StringComparison.Ordinal)) return "XIAOMI DIRECT BOOTLOADER UNLOCK";
            if (c.Contains("da seccfg lock", StringComparison.Ordinal)) return "XIAOMI BOOTLOADER LOCK";
            if (c.Contains("e nvram")) return "NV ERASE (all NV partitions)";
            if (c.Contains("w nvram")) return "NV RESTORE (backup images)";
            if (c.Contains("r nvram")) return "NV BACKUP (nvram/nvdata/nvcfg/proinfo/protect)";
            var m = Regex.Match(c, @"^e ([^;]+)");
            if (m.Success) return m.Groups[1].Value.ToLowerInvariant() switch
            {
                "frp" => "FRP RESET (Google account)",
                "userdata" => "USERLOCK RESET (userdata erase)",
                "persist" => "MICLOUD RESET (persist erase)",
                "nvdata" => "NV RESET (baseband/IMEI)",
                _ => "PARTITION ERASE (" + m.Groups[1].Value.ToUpperInvariant() + ")"
            };
        }
        return args[0] switch
        {
            "printgpt" or "gpt" => "READ PARTITION TABLE",
            "r" => "READ " + (args.Length > 1 ? args[1].ToUpperInvariant() : ""),
            "w" => "WRITE " + (args.Length > 1 ? args[1].ToUpperInvariant() : ""),
            "e" => "ERASE " + (args.Length > 1 ? args[1].ToUpperInvariant() : ""),
            "reset" => "RESET DEVICE",
            _ => string.Join(" ", args).ToUpperInvariant()
        };
    }

    // MST-style log: raw mtkclient line → clean ` step...Ok` / `  * key ... value` line.
    // evidence logic (r.Lines) still sees RAW lines — display only.
    private void LogMtkLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        string raw = Regex.Replace(line, @"\x1B\[[0-9;]*[a-zA-Z]", "");
        if (string.IsNullOrWhiteSpace(raw)) return;
        var mapped = MapMtkMstLine(raw.Trim());
        if (mapped.HasValue) Log(mapped.Value.text, mapped.Value.color);
    }

    private (string text, Color color)? MapMtkMstLine(string t)
    {
        bool has(string k) => t.Contains(k, StringComparison.OrdinalIgnoreCase);
        string after(string k)
        {
            int i = t.IndexOf(k, StringComparison.OrdinalIgnoreCase);
            return i < 0 ? "" : t.Substring(i + k.Length).Replace("\t", " ").Trim();
        }

        // native da seccfg skipped (already unlocked / unknown algo) — friendly, not red
        if (has("already unlocked or algo is unknown"))
            return (" Native unlock skipped (already unlocked or algo unknown)", Color.Orange);

        // အမှားများ → raw ပဲ နီ (HasError regex နဲ့ ကိုက်အောင် pattern တွေ အကုန်ပါ)
        if (has("error") || t.Contains("FAIL") || has("failed") || has("failure") ||
            has("traceback") || has("exception") || has("not found") || has("could not") ||
            t.Contains("USBError") || t.Contains("invalid choice") || t.Contains("Aborting") ||
            t.Contains("FileNotFoundError") || t.Contains("Please disconnect"))
            return (t, Color.OrangeRed);

        // ---- attach / handshake ----
        if (has("Status: Waiting for") || has("please reconnect mobile")) return null;
        if (has("DeviceClass - Detected") || has("Port - Device detected"))
        {
            if (mstSearching) return null;
            mstSearching = true;
            return (" Searching for device...Ok", Color.LightGreen);
        }
        if (has("Detected regular mode")) return (" Connecting with device...Ok", Color.LightGreen);
        if (has("BROM mode detected")) return null;

        // ---- device information ----
        if (has("CPU:"))
        {
            if (!mstHwInfo)
            {
                mstHwInfo = true;
                Log(" Getting Device Hardware Information...Ok", Color.LightGreen);
            }
            return ("  * Chipset ... " + after("CPU:"), Color.LightSkyBlue);
        }
        if (has("HW code:")) return ("  * Code ... " + after("HW code:"), Color.LightSkyBlue);
        if (has("ME_ID:")) return ("  * MEID ... " + after("ME_ID:"), Color.LightSkyBlue);
        if (has("Auth file is required")) return ("  * Auth file ... not provided (BROM bypass)", Color.Orange);

        // ---- exploit ----
        if (has("Device is protected")) return (" Looking for supported exploit functions...Ok", Color.LightGreen);
        if (has("Bypassing security")) return (" Executing Mediatek BROM Exploit...", Color.Gold);
        if (has("Done sending payload")) return (" Executing Mediatek BROM Exploit...Ok", Color.LightGreen);
        if (has("Exploitation") || has("Successfully sent payload")) return null;
        if (has("No preloader given")) return (" Dumping Preloader from Device...Ok ( From Device )", Color.LightGreen);

        // ---- download agent ----
        if (has("Uploading xflash stage 1")) return (" Loading Download-Agent...", Color.Gainsboro);
        if (has("uploaded stage 1")) return (" Sending Download-Agent Stage1...Ok", Color.LightGreen);
        if (has("Successfully received DA sync")) return (" Checking Download-Agent Capability...Ok", Color.LightGreen);
        if (has("uploaded stage 2")) return (" Sending Download-Agent Stage2...Ok", Color.LightGreen);

        // ---- storage information (buffer → flush after internal info, MST order) ----
        if (has("EMMC ID:"))
        {
            mstStorageBuf.Add("  * EMMC NAME ... " + after("EMMC ID:"));
            return null;
        }
        if (has("EMMC Boot1 Size:")) { mstStorageBuf.Add("  * Boot1 Size ... " + MstSizeLine(t)); return null; }
        if (has("EMMC Boot2 Size:")) { mstStorageBuf.Add("  * Boot2 Size ... " + MstSizeLine(t)); return null; }
        if (has("EMMC USER Size:")) { mstStorageBuf.Add("  * Userarea Size ... " + MstSizeLine(t)); return null; }

        // ---- custom payload jump ----
        if (has("Extensions were accepted")) return (" Booting device into custom payload...Ok", Color.LightGreen);
        if (has("Lockstate") || has("hwtype found"))
        {
            if (!mstInternal)
            {
                mstInternal = true;
                Log(" Getting Device Internal Information...Ok", Color.LightGreen);
                FlushMtkStorage();
            }
            return null;
        }

        // ---- operation results (storage block က အရင် flush) ----
        if (has("Successfully wrote seccfg")) { FlushMtkStorage(); return (" Unlocking Device Bootloader ... Ok", Color.LightGreen); }
        if (has("already unlocked")) { FlushMtkStorage(); return (" Unlocking Device Bootloader ... Ok ( already unlocked )", Color.LightGreen); }
        if (t.StartsWith("Wrote "))
        {
            FlushMtkStorage();
            var m = Regex.Match(t, @"to sector (\d+)");
            return (" Writing image ... Ok" + (m.Success ? " ( sector " + m.Groups[1].Value + " )" : ""), Color.LightGreen);
        }
        if (has("All partitions formatted")) { FlushMtkStorage(); return (" All partitions formatted ... Ok", Color.LightGreen); }
        if (has("Formatted sector") || has("Erased"))
        {
            FlushMtkStorage();
            var m = Regex.Match(t, @"sector (\d+)");
            return (" Erasing partition ... Ok" + (m.Success ? " ( sector " + m.Groups[1].Value + " )" : ""), Color.LightGreen);
        }
        if (has("Reset command was sent")) { FlushMtkStorage(); return (" Powering off device... Ok", Color.LightGreen); }

        // ကျန် line အားလုံး = noise
        return null;
    }

    private void FlushMtkStorage()
    {
        if (mstStorage || mstStorageBuf.Count == 0) return;
        mstStorage = true;
        Log("", Color.Gainsboro);
        Log("STORAGE INFORMATION", Color.White);
        foreach (string l in mstStorageBuf) Log(l, Color.LightSkyBlue);
        mstStorageBuf.Clear();
    }

    private static string MstBytes(long v) =>
        v >= 1L << 30 ? (v / (double)(1L << 30)).ToString("F3") + " GB" : (v / (double)(1L << 20)).ToString("F3") + " MB";

    private static string MstSizeLine(string t)
    {
        var m = Regex.Match(t, "0x[0-9a-fA-F]+");
        if (!m.Success) return "";
        long v = Convert.ToInt64(m.Value, 16);
        return $"0x{v:X10} [{MstBytes(v)}]";
    }

    // Device ချိတ်ဖို့ စောင့် — VID_0E8D (MediaTek) COM port ပေါ်ရင် auto-select
    private async Task<string> WaitForMediaTekPortAsync(CancellationToken cancel)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            cancel.ThrowIfCancellationRequested();
            // PnP ကနေ MediaTek COM port ရှာ (VID_0E8D)
            string pnpPort = FindMediaTekComPort();
            if (pnpPort != null)
            {
                Log(" Device detected on " + pnpPort + "...Ok", Color.LightGreen);
                if (cmbPorts.InvokeRequired)
                    cmbPorts.Invoke(() => { if (cmbPorts.Items.Contains(pnpPort)) cmbPorts.SelectedItem = pnpPort; });
                else if (cmbPorts.Items.Contains(pnpPort)) cmbPorts.SelectedItem = pnpPort;
                return pnpPort;
            }
            await Task.Delay(500, cancel);
        }
        throw new TimeoutException("No MediaTek device found after 60 seconds. Check the cable and driver.");
    }

    // VID_0E8D ပါတဲ့ COM port ကို PnP ကနေ ရှာ
    private static string FindMediaTekComPort()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT * FROM Win32_PnPEntity WHERE DeviceID LIKE '%VID_0E8D%'");
            foreach (var obj in searcher.Get())
            {
                string name = obj["Name"]?.ToString() ?? "";
                var m = System.Text.RegularExpressions.Regex.Match(name, @"(COM\d+)");
                if (m.Success) return m.Groups[1].Value;
            }
        }
        catch { }
        // fallback: existing COM ports ထဲမှာ VID မပေါ်ရင် serial port list ကနေ
        var ports = System.IO.Ports.SerialPort.GetPortNames();
        return ports.Length == 1 ? ports[0] : null;
    }
    // VID_0E8D USB device ရှိ/မရှိ (strict PnP — COM port fallback မပါ)
    private static bool IsMediaTekUsbPresent()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT * FROM Win32_PnPEntity WHERE DeviceID LIKE '%VID_0E8D%'");
            foreach (var obj in searcher.Get()) return true;
        }
        catch { }
        return false;
    }
    // mtkclient က enumeration moment မှာပဲ attach ရမယ် — device ကြိုတင်တက်နေရင်
    // Kamakiri payload error / "Please disconnect" fail ဖြစ်တယ် (proven rule: tool first, plug after)
    private async Task EnsureMediaTekUnpluggedAsync()
    {
        if (!IsMediaTekUsbPresent()) return;
        Log("[!] Phone is still connected — unplug it now. Plug it back when the next prompt appears.", Color.Orange);
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (IsMediaTekUsbPresent())
        {
            if (stopRequested) throw new OperationCanceledException();
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Phone was not unplugged in time. Unplug it and try again.");
            await Task.Delay(500, mediaTekCancel.Token);
        }
    }
    private async Task<IReadOnlyList<MtkPartition>> ReadMediaTekGptAsync(bool display = false)
    {
        var result = await MediaTekCommandAsync(new[] { "printgpt" });
        result.RequireSuccess();
        var table = MtkClientEngine.ParseGpt(result);
        mediaTekLiveTable = table;
        if (mediaTekPackage?.Platform is { Length: > 0 } platform &&
            !result.Lines.Any(l => l.Contains(platform, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Device chipset could not be matched to scatter " + platform + ". Nothing written.");
        if (display)
        {
            mediaTekPackage = null; mediaTekScatter.Clear(); mediaTekGrid.Rows.Clear();
            foreach (var p in table) mediaTekGrid.Rows.Add(false, p.Name, "", "0x" + p.Offset.ToString("x"), FormatBytesLong(p.Length), "Live GPT");
            RefreshMediaTekSummary();
            // Device info panel ပြ
            string cpu = FindMtkInfo(result.Lines, @"CPU:\s+(.+)");
            string emmcId = FindMtkInfo(result.Lines, @"EMMC ID:\s+(\S+)");
            string emmcUser = FindMtkInfo(result.Lines, @"EMMC USER Size:\s+(0x[0-9a-fA-F]+)");
            if (!string.IsNullOrEmpty(cpu) || !string.IsNullOrEmpty(emmcId))
                lblMtkDeviceStatus.Text = cpu + " · " + emmcId + (emmcUser != "" ? " · " + FormatBytesLong(Convert.ToInt64(emmcUser, 16)) : "");
            lblMtkDeviceStatus.ForeColor = Color.LightGreen;
            // FLASH tab ပြ
            if (mtkModes != null && mtkModes.TabCount >= 4) mtkModes.SelectedIndex = 3;
            Log("[OK] GPT loaded: " + table.Count + " partitions → FLASH tab", Color.LightGreen);
        }
        return table;
    }
    private static string FindMtkInfo(IReadOnlyList<string> lines, string pattern)
    {
        foreach (string line in lines)
        {
            var m = Regex.Match(line, pattern);
            if (m.Success) return m.Groups[1].Value.Trim();
        }
        return "";
    }
    // seccfg original image cache ရှာ — MMMM magic ရှိတဲ့ backup အသစ်ဆုံး
    private static byte[] FindCachedSeccfg(out string src)
    {
        src = null;
        string bakDir = Path.Combine(ShopServices.DataDirectory, "MediaTekSessions", "seccfg-backup");
        if (!Directory.Exists(bakDir)) return null;
        foreach (string f in Directory.GetFiles(bakDir, "seccfg-*.bin").OrderByDescending(File.GetLastWriteTimeUtc))
        {
            try
            {
                byte[] b = File.ReadAllBytes(f);
                if (b.Length >= 0x40 && b[0] == 0x4D && b[1] == 0x4D && b[2] == 0x4D && b[3] == 0x4D)
                { src = f; return b; }
            }
            catch { }
        }
        return null;
    }
    // Erase + reset in ONE mtkclient session — one plug cycle per operation.
    private async Task MtkEraseAndResetAsync(string partition, string okMsg)
    {
        var sw = Stopwatch.StartNew();
        var r = await MediaTekCommandAsync(new[] { "multi", $"e {partition};reset" }, writes: true);
        // erase evidence: single = "Formatted sector"; comma list = "All partitions formatted."
        // (partial multi-erase prints only mixed sector lines — no summary → must fail)
        bool multi = partition.Contains(',');
        bool formatted = multi
            ? r.Lines.Any(l => l.Contains("All partitions formatted", StringComparison.OrdinalIgnoreCase))
            : r.Lines.Any(l => l.Contains("Formatted sector", StringComparison.OrdinalIgnoreCase) ||
                              l.Contains("All partitions formatted", StringComparison.OrdinalIgnoreCase));
        bool resetOk = r.Lines.Any(l => l.Contains("Reset command was sent", StringComparison.OrdinalIgnoreCase));
        if (!formatted)
        {
            foreach (string l in r.ErrorLines.Take(6)) Log("  ! " + l.Trim(), Color.OrangeRed);
            if (r.ExitCode != 0) Log($"  * Exit code ... {r.ExitCode}", Color.OrangeRed);
            throw new IOException(resetOk
                ? "partition erase failed (reset was sent)."
                : r.HasError ? "mtkclient reported an error." : "erase was not confirmed.");
        }
        string msg = okMsg.StartsWith("[OK] ") ? okMsg.Substring(5) : okMsg;
        Log($"  * Result ... {msg}", Color.LightGreen);
        if (resetOk)
            Log(" Finished — device is powering off. Unplug the USB cable.", Color.LightGreen);
        else
            Log(" Reset did not run — reboot the phone to apply.", Color.Orange);
        Log($" Elapsed Time... {sw.Elapsed.TotalSeconds:F0} seconds", Color.Gainsboro);
        Log($" Finished at local time: [{DateTime.Now:dd.MM.yy HH:mm:ss}]", Color.Gainsboro);
    }
    private async Task SeccfgAutoPatchAsync(bool unlock)
    {
        // Empirical: state 0x03 = UNLOCKED (MST-written valid image), 0x00 = LOCKED.
        uint target = unlock ? 3u : 0u;
        await RunMediaTekWorkflowAsync(async () =>
        {
            string flag = unlock ? "unlock" : "lock";
            string what = unlock ? "UNLOCK" : "LOCK";
            // ONE session: native da seccfg → cached patch write → reset.
            // mtkclient multi runs every command even when one fails (no break),
            // so one plug cycle covers the whole flow on either path.
            string tmp = null;
            string backupLine = null, stateLine = null, patchLine = null;
            byte[] orig = FindCachedSeccfg(out string src);
            if (orig != null)
            {
                uint st = BitConverter.ToUInt32(orig, 0x0C);
                byte[] patched = (byte[])orig.Clone();
                if (st == target)
                    backupLine = $"  * Backup image ... {Path.GetFileName(src)} (state 0x{st:X8}) — writing as-is";
                else
                {
                    BitConverter.GetBytes(target).CopyTo(patched, 0x0C);
                    stateLine = $"  * Patch state ... 0x{st:X8} → 0x{target:X8} ({flag})";
                }
                tmp = Path.Combine(Path.GetTempPath(), "pmk_seccfg_out_" + Guid.NewGuid().ToString("N") + ".bin");
                File.WriteAllBytes(tmp, patched);
                patchLine = $"  * Patch file ... {Path.GetFileName(tmp)}";
            }
            // No spaces after ';' — mtkclient multi splits each segment with split(" "),
            // and a leading space becomes an empty argv token (argparse: invalid choice '').
            string cmds = tmp != null ? $"da seccfg {flag};w seccfg {tmp};reset" : $"da seccfg {flag};reset";
            const int maxAttempts = 3;
            string lastErr = "unknown";
            var sw = Stopwatch.StartNew();
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                Log($" Operation : {MtkOperationName(new[] { "multi", cmds })} (attempt {attempt}/{maxAttempts})", Color.White);
                if (backupLine != null) Log(backupLine, Color.LightSkyBlue);
                if (stateLine != null) Log(stateLine, Color.LightSkyBlue);
                if (patchLine != null) Log(patchLine, Color.Gainsboro);
                try
                {
                    var r = await MediaTekCommandAsync(new[] { "multi", cmds }, writes: true,
                        attempt: $" (attempt {attempt}/{maxAttempts})");
                    bool native = r.Lines.Any(l => l.Contains("Successfully wrote seccfg.", StringComparison.OrdinalIgnoreCase));
                    bool wrote = tmp != null && MtkClientEngine.HasWriteEvidence(r, tmp);
                    if (!native && !wrote)
                    {
                        foreach (string l in r.ErrorLines.Take(6)) Log("  ! " + l.Trim(), Color.OrangeRed);
                        if (r.ExitCode != 0) Log($"  * Exit code ... {r.ExitCode}", Color.OrangeRed);
                        throw new IOException(r.HasError ? "mtkclient reported an error." : "seccfg write was not confirmed.");
                    }
                    bool resetOk = r.Lines.Any(l => l.Contains("Reset command was sent", StringComparison.OrdinalIgnoreCase));
                    string via = native ? "native DA" : "patch write";
                    Log($"  * Result ... seccfg {what} written via {via}", Color.LightGreen);
                    if (resetOk)
                        Log(" Finished — device is powering off. Unplug the USB cable.", Color.LightGreen);
                    else
                        Log(" Reset did not run — power the phone on manually.", Color.Orange);
                    Log($" Elapsed Time... {sw.Elapsed.TotalSeconds:F0} seconds", Color.Gainsboro);
                    Log($" Finished at local time: [{DateTime.Now:dd.MM.yy HH:mm:ss}]", Color.Gainsboro);
                    return;
                }
                catch (IOException ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException)
                {
                    lastErr = ex.Message;
                    if (attempt < maxAttempts)
                    {
                        Log($" Operation failed: {ex.Message}", Color.OrangeRed);
                        Log(" Unplug now; plug the phone in on the next prompt.", Color.Yellow);
                    }
                }
            }
            if (tmp == null)
            {
                // No cache and native failed: read → patch → write (first-time path).
                Log(" Reading seccfg for patch write (native attempts exhausted).", Color.Orange);
                string rtmp = Path.Combine(Path.GetTempPath(), "pmk_seccfg_" + Guid.NewGuid().ToString("N") + ".bin");
                var rr = await MediaTekCommandAsync(new[] { "r", "seccfg", rtmp }, writes: true);
                rr.RequireSuccess();
                if (!File.Exists(rtmp) || new FileInfo(rtmp).Length == 0)
                    throw new IOException("seccfg read failed — empty file.");
                byte[] fresh = File.ReadAllBytes(rtmp);
                Log($"  * seccfg read ... {fresh.Length} bytes", Color.Gainsboro);
                if (fresh.Length < 0x40 || fresh[0] != 0x4D || fresh[1] != 0x4D || fresh[2] != 0x4D || fresh[3] != 0x4D)
                    throw new InvalidDataException("seccfg magic mismatch (expected MMMM) — unknown format, not writing.");
                string bakDir = Path.Combine(ShopServices.DataDirectory, "MediaTekSessions", "seccfg-backup");
                Directory.CreateDirectory(bakDir);
                string bak = Path.Combine(bakDir, $"seccfg-read-{DateTime.Now:yyyyMMdd-HHmmss}.bin");
                File.WriteAllBytes(bak, fresh);
                Log($"  * Backup saved ... {bak}", Color.Gainsboro);
                await PatchAndWriteSeccfgAsync(fresh, unlock, target);
                return;
            }
            throw new IOException($"seccfg {what} failed after {maxAttempts} attempts ({lastErr}).");
        });
    }
    private async Task PatchAndWriteSeccfgAsync(byte[] orig, bool unlock, uint target)
    {
        uint state = BitConverter.ToUInt32(orig, 0x0C);
        uint crc = BitConverter.ToUInt32(orig, 0x1C);
        Log($"  * Lock state ... 0x{state:X8} @0x0C, checksum 0x{crc:X8} @0x1C", Color.Gainsboro);
        // Xiaomi seccfg checksum = device-bound — do NOT modify it.
        byte[] patched = (byte[])orig.Clone();
        if (state == target)
            Log($"[MTK] seccfg already {(unlock ? "unlocked" : "locked")} in cache — writing as-is.", Color.Orange);
        else
        {
            BitConverter.GetBytes(target).CopyTo(patched, 0x0C);
            Log($"[MTK] Lock state: 0x{state:X8} → 0x{target:X8} ({(unlock ? "unlock" : "lock")})", Color.Gold);
        }
        string tmp = Path.Combine(Path.GetTempPath(), "pmk_seccfg_out_" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(tmp, patched);
        Log($"  * Patched bytes ... {BitConverter.ToString(patched, 0, 32)}", Color.Gainsboro);
        Log(" Writing seccfg image (multi + reset)...", Color.Gainsboro);
        try
        {
            // write + reset တစ် session တည်း — same DA connection ကနေ reset ချ
            var wr = await MediaTekCommandAsync(new[] { "multi", "w seccfg " + tmp + ";reset" });
            if (!MtkClientEngine.HasWriteEvidence(wr, tmp))
                throw new IOException(wr.HasError ? "mtkclient reported an error." : "seccfg write completion was not confirmed.");
            if (wr.HasError)
                Log("[OK] seccfg " + (unlock ? "UNLOCK" : "LOCK") + " written. Reset did not run — power the phone on manually.", Color.Orange);
            else
                Log("[OK] seccfg " + (unlock ? "UNLOCK" : "LOCK") + " written. Reset sent — unplug the USB cable.", Color.LightGreen);
            return;
        }
        catch (IOException ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException)
        {
            Log("[!] write+reboot failed (" + ex.Message + ") — retrying write without reboot.", Color.Orange);
        }
        Log(" Writing seccfg image (retry, no reboot)...", Color.Gainsboro);
        var plain = await MediaTekCommandAsync(new[] { "w", "seccfg", tmp });
        MtkClientEngine.RequireWriteEvidence(plain, "seccfg", tmp);
        Log("[OK] seccfg " + (unlock ? "UNLOCK" : "LOCK") + " written. Reboot the phone to apply.", Color.LightGreen);
        try
        {
            var rs = await MediaTekCommandAsync(new[] { "reset" }, timeout: TimeSpan.FromSeconds(25));
            if (!rs.Lines.Any(l => l.Contains("Reset command was sent", StringComparison.OrdinalIgnoreCase)))
                throw new IOException("reset was not confirmed.");
            Log("[OK] Reset sent — unplug the USB cable; the phone restarts or powers off.", Color.LightGreen);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            Log("[!] Auto-reboot no response — unplug and power on the phone manually.", Color.Orange);
        }
    }
    private Task SaveMediaTekGptAsync() => RunMediaTekWorkflowAsync(async () =>
    {
        using var pick = new FolderBrowserDialog { Description = "Save GPT backup" };
        if (pick.ShowDialog(this) != DialogResult.OK) return;
        string dir = Path.Combine(pick.SelectedPath, "GPT-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var result = await MediaTekCommandAsync(new[] { "gpt", dir }); result.RequireSuccess();
        if (!Directory.EnumerateFiles(dir).Any(f => new FileInfo(f).Length > 0)) throw new IOException("GPT backup files were not produced.");
        Log("[OK] GPT files saved: " + dir, Color.LightGreen);
    });
    private Task BackupMediaTekAsync() => BackupMediaTekAsync(false);
    private Task BackupMediaTekAsync(bool nvOnly) => RunMediaTekWorkflowAsync(async () =>
    {
        var selected = SelectedMediaTekNames();
        var table = await ReadMediaTekGptAsync();
        if (nvOnly) selected = table.Where(p => new[] { "nvram", "nvdata", "nvcfg", "proinfo", "protect1", "protect2" }.Contains(p.Name)).Select(p => p.Name).ToList();
        if (selected.Count == 0) throw new InvalidOperationException("Select partitions in Flash first.");
        using var pick = new FolderBrowserDialog { Description = "Select backup destination" };
        if (pick.ShowDialog(this) != DialogResult.OK) return;
        string dir = Path.Combine(pick.SelectedPath, "MTK-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        foreach (string name in selected)
        {
            var part = table.Single(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            string file = Path.Combine(dir, name + ".bin");
            var result = await MediaTekCommandAsync(new[] { "r", name, file }); result.RequireSuccess();
            if (!File.Exists(file) || new FileInfo(file).Length != part.Length) throw new IOException("Backup incomplete: " + name);
            Log("[OK] Backup " + name, Color.LightGreen);
        }
        ShopServices.WriteBackup(dir, "MTK", table.ToDictionary(p => p.Name, p => p.Length), selected, Application.ProductVersion);
        RegisterBackup(dir); Log("[OK] Backup sizes and SHA-256 recorded: " + dir, Color.LightGreen);
    });

    private static readonly string[] NvPartitions = { "nvram", "nvdata", "nvcfg", "proinfo", "protect1", "protect2" };

    // printgpt output → (live table, NV sizes dict); groups: [1]=name [2]=Offset [3]=Length
    private static (List<MtkPartition> Table, Dictionary<string, long> NvSizes) ParseGptLines(IEnumerable<string> lines)
    {
        var live = new List<MtkPartition>();
        var nv = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines)
        {
            var m = Regex.Match(line, @"^\s*([\w-]+):\s+Offset\s+(0x[\da-fA-F]+),\s+Length\s+(0x[\da-fA-F]+)");
            if (!m.Success) continue;
            long off, len;
            try
            {
                off = long.Parse(m.Groups[2].Value[2..], System.Globalization.NumberStyles.HexNumber);
                len = long.Parse(m.Groups[3].Value[2..], System.Globalization.NumberStyles.HexNumber);
            }
            catch (Exception) { continue; }
            if (off < 0 || len <= 0) continue;
            live.Add(new(m.Groups[1].Value, off, len));
            if (NvPartitions.Contains(m.Groups[1].Value, StringComparer.OrdinalIgnoreCase))
                nv[m.Groups[1].Value] = len;
        }
        return (live, nv);
    }

    // ONE plug cycle — multi takes a SINGLE script string (parser.parse_args per ";"):
    // printgpt;r nvram nvram.bin;… — table parsed from the same run, missing NV parts are skipped.
    private Task NvBackupAsync() => RunMediaTekWorkflowAsync(async () =>
    {
        var sw = Stopwatch.StartNew();
        string script = "printgpt";
        foreach (string n in NvPartitions) script += $";r {n} {n}.bin";
        var r = await MediaTekCommandAsync(new[] { "multi", script });
        // GPT sizes from the same run (no RequireSuccess — a missing NV part is not fatal)
        var (live, sizes) = ParseGptLines(r.Lines);
        if (live.Count > 0) mediaTekLiveTable = live;
        if (sizes.Count == 0)
        {
            foreach (string l in r.ErrorLines.Take(6)) Log("  ! " + l.Trim(), Color.OrangeRed);
            try { Directory.Delete(mediaTekEngine.SessionDirectory, true); } catch { }
            throw new IOException("Partition table was not read — NV backup aborted.");
        }
        string dest = Path.Combine(ShopServices.DataDirectory, "NVBackup", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(dest);
        string sess = mediaTekEngine.SessionDirectory;
        int saved = 0;
        foreach (var kv in sizes)
        {
            string staged = Path.Combine(sess, kv.Key + ".bin");
            if (!File.Exists(staged) || new FileInfo(staged).Length != kv.Value)
            {
                foreach (string l in r.ErrorLines.Take(6)) Log("  ! " + l.Trim(), Color.OrangeRed);
                try { Directory.Delete(mediaTekEngine.SessionDirectory, true); } catch { }
                throw new IOException($"NV backup incomplete: {kv.Key} (expected {kv.Value}, got {(File.Exists(staged) ? new FileInfo(staged).Length : 0)})");
            }
            File.Move(staged, Path.Combine(dest, kv.Key + ".bin"), true);
            Log($"  * Backup {kv.Key} ... {MstBytes(kv.Value)}", Color.LightGreen);
            saved++;
        }
        ShopServices.WriteBackup(dest, "MTK", sizes.ToDictionary(p => p.Key, p => p.Value),
            sizes.Keys.ToList(), Application.ProductVersion);
        RegisterBackup(dest);
        Log($"  * Result ... NV backup saved — {saved} partitions", Color.LightGreen);
        Log(" [OK] " + dest, Color.LightGreen);
        Log($" Elapsed Time... {sw.Elapsed.TotalSeconds:F0} seconds", Color.Gainsboro);
        Log($" Finished at local time: [{DateTime.Now:dd.MM.yy HH:mm:ss}]", Color.Gainsboro);
    });

    // ONE session — multi w part file;…;reset (exact-size checked vs live GPT first)
    private async Task NvRestoreAsync()
    {
        string nvRoot = Path.Combine(ShopServices.DataDirectory, "NVBackup");
        using var pick = new FolderBrowserDialog { Description = "Select NV backup folder (nvram.bin, nvdata.bin, …)" };
        if (Directory.Exists(nvRoot)) pick.SelectedPath = nvRoot;
        if (pick.ShowDialog(this) != DialogResult.OK) return;
        string srcDir = pick.SelectedPath;
        var files = NvPartitions.Where(n => File.Exists(Path.Combine(srcDir, n + ".bin"))).ToList();
        if (files.Count == 0) throw new InvalidOperationException("No NV backup files (nvram.bin, nvdata.bin …) in the selected folder.");
        string detail = string.Join("\n", files.Select(f => f + ".bin"));
        if (MessageBox.Show(this, "Restore NV backup?\n\n" + detail + "\n\nfrom:\n" + srcDir, "NV Restore", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        await RunMediaTekWorkflowAsync(async () =>
        {
            var sw = Stopwatch.StartNew();
            // cache ရှိရင် write မတိုင်ခင် pre-check; မရှိရင် script ထဲက printgpt နဲ့ post-check —
            // GPT re-read cycle လုံးဝမရှိတော့ဘူး (1 plug အမြဲ)
            var table = mediaTekLiveTable;
            bool prechecked = table != null && files.All(n => table.Any(p => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase)));
            if (prechecked)
            {
                foreach (string n in files)
                {
                    var part = table.Single(p => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
                    long len = new FileInfo(Path.Combine(srcDir, n + ".bin")).Length;
                    if (len != part.Length)
                        throw new InvalidDataException($"Size mismatch: {n} ({len} vs {part.Length}) — backup does not match this device.");
                }
                Log("  * Table ... cached live GPT — sizes pre-checked", Color.Cyan);
            }
            string sess = mediaTekEngine.SessionDirectory;
            Directory.CreateDirectory(sess); // run မတိုင်ခင် copy — engine က run မှာမှ create လုပ်လို့
            foreach (string n in files)
                File.Copy(Path.Combine(srcDir, n + ".bin"), Path.Combine(sess, n + ".bin"), true);
            // ONE plug — printgpt (post-verify) + writes + reset in a single multi script
            string script = "printgpt;" + string.Join(";", files.Select(n => $"w {n} {n}.bin")) + ";reset";
            var r = await MediaTekCommandAsync(new[] { "multi", script }, writes: true);
            var missing = files.Where(n => !MtkClientEngine.HasWriteEvidence(r, n + ".bin")).ToList();
            bool resetOk = r.Lines.Any(l => l.Contains("Reset command was sent", StringComparison.OrdinalIgnoreCase));
            var (live, _) = ParseGptLines(r.Lines);
            if (live.Count > 0) mediaTekLiveTable = live;
            if (missing.Count > 0 || !resetOk)
            {
                foreach (string l in r.ErrorLines.Take(6)) Log("  ! " + l.Trim(), Color.OrangeRed);
                if (r.ExitCode != 0) Log($"  * Exit code ... {r.ExitCode}", Color.OrangeRed);
                throw new IOException(missing.Count > 0
                    ? "NV restore incomplete: " + string.Join(", ", missing)
                    : "reset did not run after NV restore.");
            }
            foreach (string n in files) Log($"  * Restore {n} ... Ok", Color.LightGreen);
            bool warned = false;
            if (!prechecked)
            {
                if (live.Count == 0)
                {
                    Log("  [!] GPT not read — backup sizes not verified against device", Color.Orange);
                    warned = true;
                }
                else
                {
                    foreach (string n in files)
                    {
                        var p = live.SingleOrDefault(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
                        long len = new FileInfo(Path.Combine(srcDir, n + ".bin")).Length;
                        if (p == null)
                        { Log($"  [!] {n} — not in device GPT", Color.Orange); warned = true; }
                        else if (p.Length != len)
                        { Log($"  [!] {n} — backup {len} vs device {p.Length} (write stays inside the partition)", Color.Orange); warned = true; }
                    }
                }
            }
            Log(warned
                ? "  * Result ... NV restored with size warnings — verify before relying on it"
                : $"  * Result ... NV restored — {files.Count} partitions (one session)",
                warned ? Color.Orange : Color.LightGreen);
            Log(" Finished — device is powering off. Unplug the USB cable.", Color.LightGreen);
            Log($" Elapsed Time... {sw.Elapsed.TotalSeconds:F0} seconds", Color.Gainsboro);
            Log($" Finished at local time: [{DateTime.Now:dd.MM.yy HH:mm:ss}]", Color.Gainsboro);
        });
    }
    private async Task FlashMediaTekAsync()
    {
        if (mediaTekPackage == null) throw new InvalidOperationException("Select a scatter file first.");
        var names = SelectedMediaTekNames();
        if (mediaTekSkipUserdata.Checked) names.RemoveAll(n => n is "userdata" or "metadata");
        if (names.Count == 0) throw new InvalidOperationException("Select at least one image.");
        // Re-parse disk content: a cached UI selection is not a preflight result.
        var fresh = ScatterParser.Load(mediaTekPackage.ScatterPath);
        if (!FirmwarePreflight.Run(fresh, names, null, new BackendCapabilities { Name = "mtkclient", SupportsFlash = true }).CanFlash ||
            names.Any(n => fresh.Find(n) is not { } e || !IsMediaTekUserImage(e)))
            throw new InvalidDataException("Selected firmware failed validation. Reload the scatter and review its image errors.");
        if (MessageBox.Show(this, "Write these USER partitions?\n\n" + string.Join(", ", names) + "\n\nSelected partitions will be overwritten. GPT and boot regions will not be written.", "Confirm flash", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        mediaTekPackage = fresh;
        await RunMediaTekWorkflowAsync(async () =>
        {
            var table = await ReadMediaTekGptAsync();
            foreach (string name in names)
            {
                var image = fresh.Find(name); var live = table.SingleOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (live == null || live.Offset != image.PhysicalStart || live.Length != image.Size)
                    throw new InvalidDataException("Live partition differs from scatter: " + name + ". Nothing written.");
            }
            var locks = new List<FileStream>(); var plan = new List<(string Name, string Path)>();
            string stage = Path.Combine(ShopServices.DataDirectory, "Staging", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            try
            {
                foreach (string name in names)
                {
                    string path = fresh.Find(name).ResolvedPath;
                    locks.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
                    if (ReviewSafety.IsAndroidSparse(path))
                    {
                        string raw = Path.Combine(stage, name + ".img");
                        Log("[Preparing] Expanding " + name, Color.Cyan);
                        await Task.Run(() => ReviewSafety.ExpandSparseImage(path, raw)); path = raw;
                        locks.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
                    }
                    if (new FileInfo(path).Length > fresh.Find(name).Size) throw new InvalidDataException("Image too large: " + name);
                    plan.Add((name, path));
                }
                foreach (var item in plan)
                {
                    var result = await MediaTekCommandAsync(new[] { "w", item.Name, item.Path }, writes: true);
                    MtkClientEngine.RequireWriteEvidence(result, item.Name, item.Path);
                    Log("[OK] Written: " + item.Name + " · readback not performed", Color.LightGreen);
                }
                Log(stopRequested ? "[!] Stopped after current partition." : "[OK] Selected writes completed. Readback and Android boot are not verified.", Color.LightGreen);
            }
            finally
            {
                foreach (var handle in locks) handle.Dispose();
                try { Directory.Delete(stage, true); } catch (IOException) { }
            }
        });
    }
    private async Task RestoreMediaTekImageAsync()
    {
        var names = SelectedMediaTekNames();
        if (names.Count != 1) throw new InvalidOperationException("Select exactly one live USER partition in Flash.");
        using var pick = new OpenFileDialog { Filter = "Partition image|*.img;*.bin|All files|*.*" };
        if (pick.ShowDialog(this) == DialogResult.OK) await RunMediaTekPartitionAsync("w", names[0], pick.FileName);
    }
    private async Task<bool> RunMediaTekPartitionAsync(string op, string partition, string file)
    {
        if (op is not ("r" or "w")) throw new InvalidOperationException("Use selected-image flashing; raw erase is not exposed in this module.");
        if (!Regex.IsMatch(partition, @"^[A-Za-z0-9_-]+$") || new[] { "preloader", "gpt", "pgpt", "sgpt", "otp", "flashinfo" }.Contains(partition.ToLowerInvariant()))
            throw new InvalidOperationException("Only named USER partitions are supported.");
        if (op == "w" && MessageBox.Show(this, "Overwrite " + partition + " with the selected image?", "Restore image", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return false;
        bool completed = false;
        await RunMediaTekWorkflowAsync(async () =>
        {
            var table = await ReadMediaTekGptAsync();
            var part = table.Single(p => p.Name.Equals(partition, StringComparison.OrdinalIgnoreCase));
            using var handle = op == "w" ? new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
            if (op == "w" && (handle.Length != part.Length || ReviewSafety.IsAndroidSparse(file)))
                throw new InvalidDataException("Restore requires an exact-size raw partition backup. Use Scatter Flash for firmware images.");
            var result = await MediaTekCommandAsync(new[] { op, partition, file }, writes: op == "w");
            if (op == "w") MtkClientEngine.RequireWriteEvidence(result, partition, file); else result.RequireSuccess();
            if (op == "r" && (!File.Exists(file) || new FileInfo(file).Length != part.Length)) throw new IOException("Partition read is incomplete.");
            completed = true; Log("[OK] " + partition + " · " + (op == "w" ? "write reported; readback not performed" : "backup size checked"), Color.LightGreen);
        });
        return completed;
    }
}
