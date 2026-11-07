#nullable disable
using PMKUnlocker.MediaTek;
using PMKUnlocker.MtkFirmware;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PMKUnlocker;

public partial class Form1
{
    private TextBox mediaTekScatter;
    private DataGridView mediaTekGrid;
    private Label mediaTekSummary;
    private CheckBox mediaTekSkipUserdata;
    private CheckBox mediaTekFlashableOnly;
    // opt-in: write ပြီးတာနဲ့ partition ကို ပြန်ဖတ် (same session) ပြီး SHA-256 တိုက်စစ်
    private CheckBox mediaTekVerifyReadback;
    private readonly HashSet<string> mediaTekChecked = new(StringComparer.OrdinalIgnoreCase);
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
    // Loader auto-detect: catalog loader resolved for the connected chip + the last chip we reported
    private MtkChipsetMatch mtkAutoMatch;
    private string mtkLastChipNotice = "";
    // Brand combo modes: "Auto Detect" = mtkclient's own DA (validated default), "Auto + catalog DA" = pass the resolved loader
    const string MtkAutoMode = "Auto Detect";
    const string MtkAutoCatalogMode = "Auto + catalog DA";
    // ဒီ session က live GPT cache — NV Restore က GPT re-read မလုပ်ချင်ရင် (1 plug cycle) ဒါကိုသုံးတယ်
    private IReadOnlyList<MtkPartition> mediaTekLiveTable;
    // B5.4: which phone that table came from — the DA's own geometry + HW code, never an identity value. A native
    // hand-off replaces it; mtkclient's printgpt path leaves it null (that output does not report the HW code in a
    // form we could bind on, and an unknown binding is honest "we do not know", not a guess).
    private string mediaTekTableBinding;
    // Operation Progress — flash တစ်ခုတည်းအတွက် (writes 0→95, workflow end 100)။
    // python wfscatter က GPT+images တစ်ပြိုင်နက်ရေးတယ် — staging/prep မရှိ (sparse = python ဘက် streaming)
    private int mtkProgressTotal, mtkProgressDone;
    private long mtkProgressBytes, mtkPrepTotal, mtkProgressCurTotal;
    // first Write line မှာ speed-denominator watch ကို restart — DA handshake ကို speed ထဲ မထည့်ချင်လို့
    private bool mtkWriteWatchArmed;
    // PMK reset က TOPRGU stub နဲ့ chip ကို reset လုပ်တဲ့အခါ USB link က mid-handshake ပြတ်တယ်; mtkclient ရဲ့
    // DAXFlash က အဲဒီနောက် "Stage was't executed. Maybe dram issue ?." ဆိုတဲ့ generic error ကို ပြတယ်.
    // ဒါက 2.2.9 မှာ expected reset handshake အနေနဲ့ မှတ်ထားပြီးသား — raw transcript ထဲ ရှိဆဲ, screen ပေါ်မှာပဲ မပြ.
    private volatile bool mtkResetStarted;
    private readonly Dictionary<string, long> mtkProgressSizes = new(StringComparer.OrdinalIgnoreCase);
    // tee1/tee2 (scp1/scp2, sspm_1/sspm_2) တွေက image file တူတယ် — token တစ်ခုကို script order
    // အတိုင်း queue လုပ်ထားပြီး Wrote line တိုင်းမှာ တစ်ခုတစ်လေ ပြန်ထုတ်သုံးတယ်
    private readonly Dictionary<string, Queue<string>> mtkProgressNameQueue = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> mtkProgressOrder = new();
    private Stopwatch mtkProgressWatch;

    // FLASH executor step — scatter-validated write list run in ONE multi script (one plug cycle)
    private sealed class MtkFlashStep
    {
        public string Title = "";
        // (partition, image path, scatter PhysicalStart, scatter size)
        public readonly List<(string Name, string Path, long Start, long Size)> Files = new();
    }
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
            // 482 clipped the FLASH page: its summary label sits at design y 455..477 while the page client area
            // ended at ~444, so the line under the grid was never painted. 522 gives it 40 px of room.
            Size = new Size(880, 522),
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

        // Post-B5.2 (PMK's call): the standalone "Reset Device" button leaves this group. The INFO tab's native
        // Reboot Device does the same job over a live DA session from RUN 48's measured recipe (hardware-proven in
        // RUN 49), and every mtkclient flow that needs a reset already carries ";reset" inside its own session
        // (MtkEraseAndResetAsync, the NV restore, AutoRebootAsync's own reset) — so no capability was lost here,
        // only a button that duplicated a live-session control and sat beside it on the same tab.
        Panel grpInfo = CreateGroupPanel("SERVICE", UiX(0), UiY(0), 2, btnMtkReadInfo, btnMtkSaveGpt);
        infoTab.Controls.Add(grpInfo);

        // This hint used to start at UiX(0), one row under the SERVICE frame — but CreateGroupPanel is added to the
        // tab first, so the frame paints IN FRONT and the sentence appeared cut in half (only its tail, past the
        // frame's right edge, was readable). It now sits in the clear space beside the frame, and wraps instead of
        // ellipsising, because the whole line is operator instruction, not decoration.
        Label lblInfoHint = new()
        {
            Location = new Point(UiX(0) + 335, UiY(0) + 12),
            Size = new Size(500, 56),
            Text = "Power off the phone, connect in MediaTek mode (BROM / Preloader), then select its COM port. DA / Auth / Preloader depend on the device. Read Info loads the live partition table into FLASH tab.",
            ForeColor = Color.Gray,
            Font = new Font("Segoe UI", 8f),
            AutoEllipsis = false
        };
        infoTab.Controls.Add(lblInfoHint);

        // ================= UNLOCK TAB =================
        TabPage unlockTab = mtkModes.TabPages[1];
        // mtkclient syntax: e <partition> = erase, w <partition> <file> = write
        Button btnMtkFrpRemove = Create3DButton("🔑 FRP Remove", UiX(0), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Cyan);
        btnMtkFrpRemove.Click += async (_, _) => { try { await MtkServiceEraseAsync(userlock: false); } catch (Exception ex) { Log("[FAIL] " + ex.Message, Color.Red); } };
        if (toolTipMain != null) Tip(btnMtkFrpRemove, "Erase frp only + reset, native when a session is live and mtkclient otherwise. Confirmation required. Userdata and NV are kept; verify Android setup afterwards.");

        Button btnMtkUserlockRemove = Create3DButton("📱 Userlock Remove", UiX(1), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Purple);
        btnMtkUserlockRemove.Click += async (_, _) => { try { await MtkServiceEraseAsync(userlock: true); } catch (Exception ex) { Log("[FAIL] " + ex.Message, Color.Red); } };
        if (toolTipMain != null) Tip(btnMtkUserlockRemove, "Erase userdata only + reset, native when a session is live and mtkclient otherwise. ALL USER DATA IS LOST. Metadata, FRP, NV and bootchain are kept; verify Android setup afterwards.");

        Button btnMtkMiCloud = Create3DButton("☁️ Mi Cloud Remove", UiX(2), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Orange);
        btnMtkMiCloud.Click += async (_, _) => { try { await RunMediaTekWorkflowAsync(async () => await MtkEraseAndResetAsync("persist", "[OK] Persist erased — Mi Cloud / account data cleared.")); } catch (Exception ex) { Log("[FAIL] " + ex.Message, Color.Red); } };
        if (toolTipMain != null) Tip(btnMtkMiCloud, "mtkclient e persist + reset in ONE session — Xiaomi Mi Cloud / Mi Account removal.");

        Button btnMtkSeccfgUnlock = Create3DButton("🔓 Bootloader Unlock", UiX(1), UiY(1), UiBtnW, UiBtnH, ButtonTheme.Green);
        btnMtkSeccfgUnlock.Click += async (_, _) => { try { await SeccfgAutoPatchAsync(true); } catch (Exception ex) { Log("[FAIL] " + ex.Message, Color.Red); } };
        if (toolTipMain != null) Tip(btnMtkSeccfgUnlock, "Native da seccfg unlock + cached patch fallback + reset in ONE session (single plug cycle), auto-retry once.");

        Button btnMtkSeccfgLock = Create3DButton("🔒 Bootloader Relock", UiX(2), UiY(1), UiBtnW, UiBtnH, ButtonTheme.Orange);
        btnMtkSeccfgLock.Click += async (_, _) => { try { await SeccfgAutoPatchAsync(false); } catch (Exception ex) { Log("[FAIL] " + ex.Message, Color.Red); } };
        if (toolTipMain != null) Tip(btnMtkSeccfgLock, "Native da seccfg lock + cached patch fallback + reset in ONE session (single plug cycle), auto-retry once.");

        Panel grpUnlock = CreateGroupPanel("UNLOCK / REPAIR", UiX(0), UiY(0), 3, btnMtkFrpRemove, btnMtkUserlockRemove, btnMtkMiCloud);
        Panel grpUnlock2 = CreateGroupPanel("SECURE BOOT (SECCFG)", UiX(0), UiY(2), 2, btnMtkSeccfgUnlock, btnMtkSeccfgLock);

        Button btnMtkNvBackup = Create3DButton("📂 NV Backup", UiX(0), UiY(4), UiBtnW, UiBtnH, ButtonTheme.Cyan);
        btnMtkNvBackup.Click += async (_, _) =>
        {
            try
            {
                string def = GetMtkSetting(MtkNvBackupRootKey) ?? Path.Combine(ShopServices.DataDirectory, "NVBackup");
                try { Directory.CreateDirectory(def); } catch { }
                using var pick = new FolderBrowserDialog
                {
                    Description = "Select NV backup destination (timestamp subfolder will be created inside)",
                    SelectedPath = def
                };
                if (pick.ShowDialog(this) != DialogResult.OK) return;
                SetMtkSetting(MtkNvBackupRootKey, pick.SelectedPath);
                // One NV backup button, one folder shape. A live session reads the six in one boot; without one the
                // mtkclient route still reads them in ONE session (`multi`), not one boot per partition.
                if (nativeDa != null) await NativeGuarded("NV backup", () => NativeBackupAsync(true, pick.SelectedPath));
                else await NvBackupAsync(pick.SelectedPath);
            }
            catch (Exception ex) { Log("[FAIL] " + ex.Message, Color.Red); }
        };
        if (toolTipMain != null) Tip(btnMtkNvBackup, "nvram/nvdata/nvcfg/proinfo/protect1/protect2 in ONE session — through the live native DA when one is open, otherwise one mtkclient session. Destination root is picked (remembered for next time), timestamp subfolder inside, manifest + SHA-256 recorded.");

        Button btnMtkNvRestore = Create3DButton("⬆️ NV Restore", UiX(1), UiY(4), UiBtnW, UiBtnH, ButtonTheme.Purple);
        btnMtkNvRestore.Click += async (_, _) => { try { await NvRestoreAsync(); } catch (Exception ex) { Log("[FAIL] " + ex.Message, Color.Red); } };
        if (toolTipMain != null) Tip(btnMtkNvRestore, "NV backup folder → write + reset in ONE session. Live native DA: manifest hash-checked and every extent matched to this device byte for byte before anything is sent. No session: mtkclient, sizes pre-checked against the cached GPT.");

        Button btnMtkNvReset = Create3DButton("📶 NV Reset", UiX(0), UiY(4), UiBtnW, UiBtnH, ButtonTheme.Cyan);
        btnMtkNvReset.Click += async (_, _) =>
        {
            // The same confirmation NV Erase asks for. This one erases a single partition, but it still cannot be
            // undone from the PC without an NV backup, and the operator should see which five are NOT touched.
            if (MessageBox.Show(this, "Reset NV data?\n\nErases: nvdata (only).\nNot touched: nvram, nvcfg, proinfo, protect1, protect2.\n\nThe modem rebuilds nvdata on the next boot. If this device carried a patched NV, restore an NV backup afterwards.", "NV Reset", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            // One dialog, then whichever session is open. The native route re-resolves nvdata against the device's
            // own GPT and erases nothing at all if the phone does not report that partition.
            try { if (nativeDa != null) { await NativeGuarded("NV reset", NativeNvResetAsync); return; } await RunMediaTekWorkflowAsync(async () => await MtkEraseAndResetAsync("nvdata", "[OK] NV data erased — IMEI/baseband reset. Restore NV backup after.")); } catch (Exception ex) { Log("[FAIL] " + ex.Message, Color.Red); }
        };
        if (toolTipMain != null) Tip(btnMtkNvReset, "e nvdata + reset in ONE session, over the live native DA when one is open — erases nvdata ONLY (nvram/nvcfg/proinfo/protect1/protect2 untouched). Run NV Backup first.");

        Button btnMtkNvErase = Create3DButton("🗑 NV Erase", UiX(1), UiY(4), UiBtnW, UiBtnH, ButtonTheme.Red);
        btnMtkNvErase.Click += async (_, _) =>
        {
            if (MessageBox.Show(this, "Erase ALL NV partitions?\n\n" + string.Join(", ", NvPartitions) + "\n\nIMEI/baseband will be lost — restore an NV backup afterwards.", "NV Erase", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            // The same one-dialog rule as NV Reset: the dialog is about the operation, the route only decides which
            // session carries it. Over a live session all six are resolved against the device's own GPT first, so a
            // phone missing one of them has nothing erased rather than five of six.
            try { if (nativeDa != null) { await NativeGuarded("NV erase", NativeNvEraseAsync); return; } await RunMediaTekWorkflowAsync(async () => await MtkEraseAndResetAsync(string.Join(",", NvPartitions), "[OK] NV erased — all NV partitions wiped. Restore NV backup now.")); } catch (Exception ex) { Log("[FAIL] " + ex.Message, Color.Red); }
        };
        if (toolTipMain != null) Tip(btnMtkNvErase, "e nvram,nvdata,nvcfg,proinfo,protect1,protect2 + reset in ONE session. Over the live native DA all six are resolved against the device's own GPT first, so a phone missing one erases nothing at all. DESTRUCTIVE — backup first.");

        Panel grpNv = CreateGroupPanel("NV BACKUP / RESTORE", UiX(0), UiY(4), 2, btnMtkNvBackup, btnMtkNvRestore);
        // The two wipes sit in their own frame, side by side with the frames above: NV Reset is one partition
        // (nvdata), NV Erase is all six. Same row, so the tab height the other tabs use is unchanged.
        Panel grpNvWipe = CreateGroupPanel("NV RESET / ERASE (DESTRUCTIVE)", UiX(0) + 2 * UiColStep + 25, UiY(4), 2, btnMtkNvReset, btnMtkNvErase);

        Button btnMtkImeiRead = Create3DButton("📱 IMEI Read (NV)", UiX(0), UiY(6), UiBtnW, UiBtnH, ButtonTheme.Cyan);
        btnMtkImeiRead.Click += async (_, _) => { try { await ReadImeiFromBackupAsync(); } catch (Exception ex) { Log("[FAIL] " + ex.Message, Color.Red); } };
        if (toolTipMain != null) Tip(btnMtkImeiRead, "Scan an NV backup folder (nvram.bin / nvdata.bin / proinfo.bin) for IMEI — read-only, no device connection or plug cycle.");

        Button btnMtkFormatAll = Create3DButton("🗑 Format All", UiX(1), UiY(6), UiBtnW, UiBtnH, ButtonTheme.Red);
        btnMtkFormatAll.Click += async (_, _) => { try { await FormatAllAsync(); } catch (Exception ex) { Log("[FAIL] " + ex.Message, Color.Red); } };
        if (toolTipMain != null) Tip(btnMtkFormatAll, "Erase OS + user data partitions (whitelist ∩ live GPT) + reset in ONE session, over the live native DA when one is open and the batched client otherwise. Bootchain (preloader/lk/uboot/tee), seccfg and NV/IMEI are kept. Phone will not boot — reflash full firmware afterwards.");

        // Two frames, same 325 px width and gutter as the NV row above: a 1-column frame is 170 px, and at
        // Segoe UI 8.5 Bold these titles measure 161 and 167 px — the closing bracket gets clipped.
        Panel grpImeiRead = CreateGroupPanel("IMEI READ (FROM BACKUP)", UiX(0), UiY(6), 2, btnMtkImeiRead);
        Panel grpFormatAll = CreateGroupPanel("FORMAT ALL (DESTRUCTIVE)", UiX(0) + 2 * UiColStep + 25, UiY(6), 2, btnMtkFormatAll);

        // ---- IMEI Write (da imei --write wiring) ----
        Panel grpImeiWrite = new()
        {
            Location = new Point(UiX(0), UiY(8)),
            Size = new Size(880, 64),
            BackColor = Color.FromArgb(23, 26, 32),
            Tag = "grp"
        };
        EnsureThemePaint(grpImeiWrite);
        var lblImeiWriteTitle = new Label { Text = "📝 IMEI WRITE (DA)", Location = new Point(9, 8), AutoSize = true, ForeColor = Color.FromArgb(86, 145, 250), Font = new Font("Segoe UI", 8.5f, FontStyle.Bold), Tag = "accent" };
        var lblImei1 = new Label { Text = "IMEI 1:", Location = new Point(9, 38), AutoSize = true, ForeColor = Color.Gainsboro, Font = new Font("Segoe UI", 8.5f) };
        var txtImei1 = new TextBox { Location = new Point(70, 34), Size = new Size(150, 24), MaxLength = 15, BackColor = Color.FromArgb(38, 42, 50), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 9f) };
        var lblImei2 = new Label { Text = "IMEI 2:", Location = new Point(240, 38), AutoSize = true, ForeColor = Color.Gainsboro, Font = new Font("Segoe UI", 8.5f) };
        var txtImei2 = new TextBox { Location = new Point(301, 34), Size = new Size(150, 24), MaxLength = 15, BackColor = Color.FromArgb(38, 42, 50), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 9f) };
        KeyPressEventHandler imeiDigitsOnly = (_, e) => { if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true; };
        txtImei1.KeyPress += imeiDigitsOnly;
        txtImei2.KeyPress += imeiDigitsOnly;
        Button btnMtkImeiWrite = Create3DButton("✍ Write IMEI", 465, 30, UiBtnW, UiBtnH, ButtonTheme.Green);
        btnMtkImeiWrite.Click += async (_, _) => { try { await WriteImeiAsync(txtImei1.Text.Trim(), txtImei2.Text.Trim()); } catch (Exception ex) { Log("[FAIL] " + ex.Message, Color.Red); } };
        if (toolTipMain != null) Tip(btnMtkImeiWrite, "mtkclient da imei --write — writes LD0B-format IMEI into nvdata. CSSD (RSA-signed) devices are NOT re-signed without OEM private keys.");
        var lblImeiCssd = new Label { Text = "CSSD (RSA-signed) devices not re-signed — LD0B devices only", Location = new Point(630, 34), AutoSize = false, Size = new Size(240, 28), ForeColor = Color.FromArgb(255, 170, 60), Font = new Font("Segoe UI", 7.5f), AutoEllipsis = true };
        grpImeiWrite.Controls.AddRange(new Control[] { lblImeiWriteTitle, lblImei1, txtImei1, lblImei2, txtImei2, btnMtkImeiWrite, lblImeiCssd });

        unlockTab.Controls.AddRange(new Control[] { grpUnlock, grpUnlock2, grpNv, grpNvWipe, grpImeiRead, grpFormatAll, grpImeiWrite });

        Label lblUnlockHint = new()
        {
            Location = new Point(UiX(0), UiY(10)),
            Size = new Size(850, 30),
            Text = "Each op runs in ONE session and needs one plug cycle: with a live native DA, FRP/Userlock and NV ops go through it, otherwise mtkclient (native + fallback + reset, auto-retry once). NV Reset erases nvdata only; NV Erase wipes all six NV — run NV Backup first. NV Restore writes only from a hash-verified backup folder and reads every partition back. Format All erases OS + user data, keeps bootchain/seccfg/NV. IMEI Read works offline from a backup.",
            ForeColor = Color.Gray,
            Font = new Font("Segoe UI", 7.5f),
            AutoEllipsis = true
        };
        unlockTab.Controls.Add(lblUnlockHint);

        // ================= BACKUP TAB =================
        TabPage backupTab = mtkModes.TabPages[2];
        Button btnMtkBackup = Create3DButton("💾 Backup Selected", UiX(0), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Orange);
        btnMtkBackup.Click += async (_, _) => { try { await BackupMediaTekAsync(); } catch (Exception ex) { Log("[FAIL] " + ex.Message, Color.Red); } };

        // The NV set has one button, on the UNLOCK tab, beside the NV Restore / NV Erase it feeds. It used to sit here
        // too, as a second route to the same six partitions with a second folder shape.
        Button btnMtkRestore = Create3DButton("⬆️ Restore Image", UiX(1), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Purple);
        btnMtkRestore.Click += async (_, _) => { try { await RestoreMediaTekImageAsync(); } catch (Exception ex) { Log("[FAIL] " + ex.Message, Color.Red); } };

        Panel grpBackup = CreateGroupPanel("BACKUP / RESTORE", UiX(0), UiY(0), 2, btnMtkBackup, btnMtkRestore);
        backupTab.Controls.Add(grpBackup);

        Label lblBackupHint = new()
        {
            // Below the frame, not inside it: the group panel is added first, so index 0 puts it on top of a label
            // that starts within its width, and the first words of the hint are painted over.
            Location = new Point(UiX(0), UiY(0) + 74),
            Size = new Size(850, 40),
            Text = "Tick partitions on the FLASH tab, then Backup Selected: one session reads them all — a live native DA when one is booted, otherwise mtkclient's single multi script — and writes <chipset>_scatter.txt beside the images, so the folder is a firmware package the FLASH tab can re-flash. Restore writes one selected USER partition (exact-size raw backup only). The six NV partitions have their own button on the UNLOCK tab.",
            ForeColor = Color.Gray,
            Font = new Font("Segoe UI", 8f),
            AutoEllipsis = false
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
        Button btnMtkSelectNormal = Create3DButton("☑ Safe", 715, 3, 90, 28, ButtonTheme.Cyan);
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
            MultiSelect = false,
            Font = new Font("Segoe UI", 8.5f)
        };
        mediaTekGrid.DefaultCellStyle.BackColor = Color.FromArgb(28, 31, 38);
        mediaTekGrid.DefaultCellStyle.ForeColor = Color.Gainsboro;
        mediaTekGrid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(86, 145, 250);
        mediaTekGrid.DefaultCellStyle.SelectionForeColor = Color.White;
        mediaTekGrid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(23, 26, 32);
        mediaTekGrid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(86, 145, 250);
        mediaTekGrid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        // A row click repaints the whole row (nine cells, three of them owner-drawn buttons). Without this the grid
        // erases and redraws cell by cell, which is what made selecting a partition feel heavy on a full GPT.
        SetDoubleBuffered(mediaTekGrid);
        mediaTekGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "selected", HeaderText = "✓", Width = 34, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
        foreach (var col in new[] { ("partition", "Partition"), ("file", "Image"), ("offset", "Offset"), ("size", "Size"), ("status", "Status") })
            mediaTekGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = col.Item1, HeaderText = col.Item2, ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        mediaTekGrid.CurrentCellDirtyStateChanged += (_, _) => { if (mediaTekGrid.IsCurrentCellDirty) mediaTekGrid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        // Only the tick column changes the summary; the other five are read-only, so a per-cell refresh on every
        // value the row fill sets was pure repaint work.
        mediaTekGrid.CellValueChanged += (_, e) => { if (!mediaTekGridFilling && e.ColumnIndex == 0) RefreshMediaTekSummary(); };
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
        mediaTekFlashableOnly = new CheckBox
        {
            Text = "Flashable only",
            Location = new Point(210, 418),
            AutoSize = true,
            Checked = true,
            ForeColor = Color.Gainsboro,
            Font = new Font("Segoe UI", 8.5f)
        };
        if (toolTipMain != null) Tip(mediaTekFlashableOnly, "MST လို — image ပါတဲ့ flashable partition တွေပဲ ပြမယ်။ ဖြုတ်ရင် scatter ထဲက partition အားလုံး (Unavailable အပါ) ပေါ်မယ်.");
        mediaTekFlashableOnly.CheckedChanged += (_, _) => RebuildMediaTekGrid();
        mediaTekVerifyReadback = new CheckBox
        {
            Text = "Verify readback (SHA-256)",
            Location = new Point(340, 418),
            AutoSize = true,
            Checked = false,
            ForeColor = Color.FromArgb(255, 200, 90),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
        };
        if (toolTipMain != null)
            Tip(mediaTekVerifyReadback,
                "Write ပြီးနောက် partition တွေကို ပြန်ဖတ်ပြီး SHA-256 တိုက်စစ်မယ် (same plug cycle).\r\n" +
                "Sparse image ကို ကျော်မယ် — device ထဲမှာ expand ဖြစ်နေလို့ တိုက်လို့မရဘူး.\r\n" +
                "ဖတ်ချိန် ပိုကြာမယ် (partition size ပေါ် မူတည်).");
        Button btnMtkClearSel = Create3DButton("✕ Clear", 560, 414, 80, 28, ButtonTheme.Orange);
        btnMtkClearSel.Click += (_, _) => { foreach (DataGridViewRow row in mediaTekGrid.Rows) row.Cells[0].Value = false; };

        Button btnMtkFlash = Create3DButton("◉ FLASH SELECTED", 656, 410, 200, 36, ButtonTheme.Red);
        btnMtkFlash.Click += async (_, _) => { try { await FlashMediaTekAsync(); } catch (Exception ex) { Log("[FAIL] " + ex.Message, Color.Red); } };
        if (toolTipMain != null) Tip(btnMtkFlash, "Rebuild GPT from the scatter file, then write the checked partitions (sparse-aware, one plug cycle). Unchecked partitions — including preloader — are skipped.");

        // Two lines: what the loaded scatter file says about itself, then what the current tick set would write and
        // through which route. 36px holds both 8pt lines inside the FLASH page (which clips at ~494 design px).
        mediaTekSummary = new Label
        {
            Location = new Point(0, 452),
            Size = new Size(856, 36),
            Text = "Ready · no firmware selected",
            ForeColor = Color.Gray,
            Font = new Font("Segoe UI", 8f)
        };

        flashTab.Controls.AddRange(new Control[] { mediaTekSkipUserdata, mediaTekFlashableOnly, mediaTekVerifyReadback, btnMtkClearSel, btnMtkFlash, mediaTekSummary });
        // The label is created with a design-time string, so paint the real two lines now — a tool that opens on
        // the FLASH page must already say what the grid is before anyone loads a scatter or ticks a row.
        RefreshMediaTekSummary();

        // ---- NATIVE tab (Phase 1.75 Path B bench harness) — only when a boot recipe is actually present ----
        BuildNativeDaTab();

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

    // ================= MTK DEVICE PICKER (pmk_mtk_devices.json + mtk/loaders drop-in) =================
    private void LoadMtkDeviceDb()
    {
        try
        {
            cmbMtkBrand.Items.Add(MtkAutoMode);
            cmbMtkBrand.Items.Add(MtkAutoCatalogMode);
            string jsonPath = Path.Combine(Application.StartupPath, "pmk_mtk_devices.json");
            string loadersRoot = Path.Combine(Application.StartupPath, "mtk", "loaders");
            mtkDeviceDb = MtkDaCatalog.Load(jsonPath, loadersRoot);
            foreach (var brand in mtkDeviceDb.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
                cmbMtkBrand.Items.Add(brand);
            cmbMtkBrand.SelectedIndex = 0;
        }
        catch (Exception ex) { cmbMtkBrand.Items.Add("(load error: " + ex.Message + ")"); cmbMtkBrand.SelectedIndex = 0; }
    }

    static bool IsMtkAutoMode(string brand) =>
        brand == MtkAutoMode || brand == MtkAutoCatalogMode;

    private void CmbMtkBrand_SelectedIndexChanged(object sender, EventArgs e)
    {
        cmbMtkModel.Items.Clear();
        cmbMtkModel.Enabled = false;
        if (cmbMtkBrand.SelectedItem is not string brand) return;
        if (IsMtkAutoMode(brand))
        {
            mtkDaPath = ""; mtkAuthPath = ""; mtkPreloaderPath = "";
            if (txtMtkDaBox != null) txtMtkDaBox.Text = "";
            if (txtMtkAuthBox != null) txtMtkAuthBox.Text = "";
            if (txtMtkPreBox != null) txtMtkPreBox.Text = "";
            mtkLastChipNotice = "";
            if (brand == MtkAutoCatalogMode && mtkAutoMatch != null) ApplyCatalogLoader();
            else
            {
                lblMtkDeviceStatus.Text = brand == MtkAutoCatalogMode
                    ? "Catalog DA — run Read Info first"
                    : "Auto Detect (mtkclient built-in DA)";
                lblMtkDeviceStatus.ForeColor = Color.FromArgb(86, 145, 250);
            }
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
        if (IsMtkAutoMode(brand) || !mtkDeviceDb.TryGetValue(brand, out var models) || !models.TryGetValue(model, out var info)) return;
        string baseDir = Path.Combine(Application.StartupPath, "mtk");
        mtkDaPath = MtkDaCatalog.Resolve(info.Da, baseDir);
        mtkAuthPath = MtkDaCatalog.Resolve(info.Auth, baseDir);
        mtkPreloaderPath = MtkDaCatalog.Resolve(info.Preloader, baseDir);
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

    // ================= LOADER AUTO-DETECT (chipset seen in a log → catalog DA/Auth/Preloader) =================
    // mtkclient reports the SoC on every BROM/Preloader session ("CPU: MT6765/MT8768t(...)", payloads/mt6765_payload.bin),
    // and ADB Read Full Info / Detect report the same token earlier (ro.soc.model). Either way the catalog is
    // consulted and the result is reported; which loader is then passed to mtkclient depends on the Brand mode:
    // · "Auto Detect" (default) — nothing is overridden: mtkclient keeps uploading its own MTK_DA_V5.bin through
    //   the xflash patch path, which is what every MTK operation validated on hardware so far used. A foreign
    //   AllInOne DA breaks it (Redmi 9A 2026-10-28: "DAconfig - Using custom loader …" → "Error on boot to,
    //   addr: 0x4fff0000" → "DA Extensions failed to enable"). If a session then fails on the loader,
    //   HandleMediaTekLoaderFailure advances this match to the next run automatically.
    // · "Auto + catalog DA" — the resolved loader is passed explicitly (chips mtkclient's DA set cannot serve,
    //   or when an auth file is needed); a DA-extension boot error drops it back to the built-in path.
    // · a hand-picked brand/model is never rewritten — it only gets a warning when it contradicts the chip.
    private void ApplyMediaTekLoaderFromOutput(IReadOnlyList<string> lines)
    {
        if (IsDisposed || lines == null || lines.Count == 0 || mtkDeviceDb.Count == 0) return;
        string chip = MtkDaCatalog.PrimaryChipset(FindMtkInfo(lines, @"CPU:\s+(.+)"));
        if (chip == "") chip = MtkDaCatalog.ChipsetFromPayloadLines(lines);
        if (chip == "" || string.Equals(chip, mtkLastChipNotice, StringComparison.OrdinalIgnoreCase)) return;
        mtkLastChipNotice = chip;

        string baseDir = Path.Combine(Application.StartupPath, "mtk");
        string brand = cmbMtkBrand.SelectedItem as string;
        string model = cmbMtkModel.SelectedItem as string;
        bool manual = !string.IsNullOrEmpty(brand) && !IsMtkAutoMode(brand);
        var match = MtkDaCatalog.FindByChipset(mtkDeviceDb, baseDir, chip, manual ? brand : null);

        if (manual)
        {
            if (MtkDaCatalog.DaMatchesChipset(model ?? "", mtkDaPath, chip))
                Log("  * Loader kept: " + brand + " " + model + " matches detected " + chip + " (manual selection).", Color.Cyan);
            else
                Log(" [!] Detected " + chip + " but the manually selected loader is " + brand + " " + model +
                    (match != null ? " — " + match.Brand + " " + match.Model + " (" + match.Basis + ") fits the chip. " : ". ") +
                    "Your selection is still used; set Brand back to Auto Detect to follow the device.", Color.Orange);
            return;
        }
        mtkAutoMatch = match;
        if (match == null)
        {
            Log("  * Chipset " + chip + " has no bundled DA/Auth — mtkclient's own loader is the only option here.", Color.Orange);
            lblMtkDeviceStatus.Text = chip + " · no catalog loader";
            lblMtkDeviceStatus.ForeColor = Color.Orange;
            return;
        }
        if (brand == MtkAutoCatalogMode)
        {
            ApplyCatalogLoader();
            Log("  * Catalog loader applied for " + chip + ": " + DescribeMatch(match) + " — passed as --loader on the next operation.", Color.LightGreen);
        }
        else
        {
            lblMtkDeviceStatus.Text = chip + " · built-in DA (catalog: " + match.Model + ")";
            lblMtkDeviceStatus.ForeColor = Color.FromArgb(86, 145, 250);
            Log("  * Catalog loader for " + chip + ": " + DescribeMatch(match) +
                " — not used, mtkclient's built-in DA stays default. Choose Brand \"" + MtkAutoCatalogMode +
                "\" (or the model by hand) if an operation needs it.", Color.Cyan);
        }
    }

    static string DescribeMatch(MtkChipsetMatch m) =>
        m.Brand + " " + m.Model + " (" + m.Basis + (string.IsNullOrEmpty(m.Info.Auth) ? ", no auth file" : " + auth file") +
        (string.IsNullOrEmpty(m.Info.Preloader) ? "" : " + preloader") + ")";

    // ============== PRE-COMMAND CHIPSET (ADB prop → catalog loader, before the phone enters BROM) ==============
    // Read Full Info / Detect read ro.soc.model ("MT6765" / "MediaTek Helio G35") and ro.board.platform
    // ("mt6765"), which is the same token the BROM session prints later. Resolving it here means the MTK tab
    // already knows the chip — and the matching loader — without a failed session first. Like the log-side
    // detect it is report-only in "Auto Detect"; "Auto + catalog DA" fills the rows, a manual brand wins.
    internal void NoteMediaTekChipFromAdb(string socProp, string boardProp)
    {
        if (IsDisposed || mtkDeviceDb.Count == 0) return;
        string chip = MtkDaCatalog.PrimaryChipset(socProp ?? "");
        if (chip == "") chip = MtkDaCatalog.PrimaryChipset(boardProp ?? "");
        if (chip == "") return;
        string brand = cmbMtkBrand?.SelectedItem as string;
        if (!string.IsNullOrEmpty(brand) && !IsMtkAutoMode(brand)) return;
        var match = MtkDaCatalog.FindByChipset(mtkDeviceDb, Path.Combine(Application.StartupPath, "mtk"), chip);
        mtkAutoMatch = match;
        mtkLastChipNotice = chip;
        if (match == null)
        {
            Log("  * " + chip + " (from ADB) has no bundled DA/Auth — mtkclient's own loader will be used.", Color.Orange);
            lblMtkDeviceStatus.Text = chip + " · no catalog loader";
            lblMtkDeviceStatus.ForeColor = Color.Orange;
            return;
        }
        if (brand == MtkAutoCatalogMode)
        {
            ApplyCatalogLoader();
            Log("  * Catalog loader for " + chip + " (ADB): " + DescribeMatch(match) + " — ready as --loader.", Color.LightGreen);
        }
        else
        {
            lblMtkDeviceStatus.Text = chip + " · built-in DA (catalog: " + match.Model + ")";
            lblMtkDeviceStatus.ForeColor = Color.FromArgb(86, 145, 250);
            Log("  * Catalog loader for " + chip + " (ADB): " + DescribeMatch(match) +
                " — ready. Brand \"" + MtkAutoCatalogMode + "\" uses it, \"Auto Detect\" keeps mtkclient's built-in DA" +
                " and switches here itself if that DA fails.", Color.Cyan);
        }
    }

    // Pass the detected catalog loader to the engine (Brand = "Auto + catalog DA").
    private void ApplyCatalogLoader()
    {
        if (mtkAutoMatch == null) return;
        ApplyMtkLoaderPaths(mtkAutoMatch.Info, Path.Combine(Application.StartupPath, "mtk"));
        lblMtkDeviceStatus.Text = "Catalog DA · " + mtkAutoMatch.Brand + " " + mtkAutoMatch.Model;
        lblMtkDeviceStatus.ForeColor = Color.LightGreen;
    }

    // Loader self-heal, both directions, driven by what the session actually did:
    //  · "Auto Detect" + a DA/loader error + the catalog has a loader for this chip → advance to it,
    //    so chips mtkclient's own DA cannot serve get the right --loader without the user hunting menus.
    //  · "Auto + catalog DA" + the DA-extension boot error → the foreign DA is not usable on this device
    //    (Redmi 9A 2026-10-28) → drop back to mtkclient's built-in loader.
    // Neither path re-runs the operation: the phone has to be re-plugged first, which only PMK can do.
    private void HandleMediaTekLoaderFailure(MtkCommandResult result)
    {
        if (IsDisposed || result == null || !result.HasError) return;
        string brand = cmbMtkBrand.SelectedItem as string;
        if (brand == MtkAutoCatalogMode)
        {
            if (!result.Lines.Any(l => LoaderBootError(l))) return;
            int auto = IndexOfItem(cmbMtkBrand, MtkAutoMode);
            if (auto >= 0) cmbMtkBrand.SelectedIndex = auto;   // handler clears the loader rows
            Log(" [!] The catalog DA did not boot this device's DA extensions — switched Brand back to \"" + MtkAutoMode +
                "\" (mtkclient built-in DA). Unplug, re-plug and run the operation again.", Color.Orange);
            return;
        }
        if (brand != MtkAutoMode || mtkAutoMatch == null) return;
        // only advance for the chip this session reported (or one ADB already proved) — never a stale match
        string chip = MtkDaCatalog.PrimaryChipset(FindMtkInfo(result.Lines, @"CPU:\s+(.+)"));
        if (chip == "") chip = MtkDaCatalog.ChipsetFromPayloadLines(result.Lines);
        if (chip != "" && !string.Equals(chip, mtkAutoMatch.Chipset, StringComparison.OrdinalIgnoreCase)) return;
        if (!result.Lines.Any(l => LoaderBootError(l) || LoaderMissingError(l))) return;
        int cat = IndexOfItem(cmbMtkBrand, MtkAutoCatalogMode);
        if (cat < 0) return;
        mtkLastChipNotice = "";                       // let the new mode report the chip again
        cmbMtkBrand.SelectedIndex = cat;              // handler applies the stored match
        Log(" [!] mtkclient's built-in loader failed on this device — advanced to the catalog loader " +
            DescribeMatch(mtkAutoMatch) + ". Unplug, re-plug and run the operation again.", Color.Orange);
    }

    // The DA reached the phone but could not take over (custom DA without the xflash extensions).
    static bool LoaderBootError(string line) =>
        line.Contains("DA Extensions failed", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Error on boot to", StringComparison.OrdinalIgnoreCase);

    // mtkclient has no loader for this chipset (unsupported chip / DA or auth rejected at BROM).
    static bool LoaderMissingError(string line) =>
        line.Contains("unsupported chipset", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("no da for", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("can't find da", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("cannot find da", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("da file not found", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("loader not found", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("invalid auth file", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("auth file", StringComparison.OrdinalIgnoreCase) && line.Contains("error", StringComparison.OrdinalIgnoreCase);

    static int IndexOfItem(ComboBox cmb, string value)
    {
        if (cmb == null || string.IsNullOrEmpty(value)) return -1;
        for (int i = 0; i < cmb.Items.Count; i++)
            if (cmb.Items[i] is string s && string.Equals(s, value, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private void ApplyMtkLoaderPaths(MtkDeviceInfo info, string baseDir)
    {
        mtkDaPath = MtkDaCatalog.Resolve(info.Da, baseDir);
        mtkAuthPath = MtkDaCatalog.Resolve(info.Auth, baseDir);
        mtkPreloaderPath = MtkDaCatalog.Resolve(info.Preloader, baseDir);
        if (txtMtkDaBox != null) txtMtkDaBox.Text = mtkDaPath;
        if (txtMtkAuthBox != null) txtMtkAuthBox.Text = mtkAuthPath;
        if (txtMtkPreBox != null) txtMtkPreBox.Text = mtkPreloaderPath;
    }

    private void PickMediaTekScatter()
    {
        using var pick = new OpenFileDialog { Filter = "MediaTek scatter|*scatter*.txt|Text files|*.txt" };
        if (pick.ShowDialog(this) != DialogResult.OK) return;
        mediaTekPackage = ScatterParser.Load(pick.FileName);
        mediaTekScatter.Text = pick.FileName;
        // ဖိုင် load လိုက်တာနဲ့ flashable အကုန် အမှန်ခြစ် — မရေးချင်တာ user က ဖြုတ်မယ်
        mediaTekChecked.Clear();
        foreach (var entry in mediaTekPackage.Entries)
            if (IsMediaTekUserImage(entry) && FlashRisk.DefaultSelected(entry)) mediaTekChecked.Add(entry.Name);
        mediaTekGrid.Rows.Clear(); // အဟောင်း rows က sync ဝင်မလာအောင်
        RebuildMediaTekGrid();
        foreach (var issue in mediaTekPackage.Issues) Log(issue.ToString(), issue.Severity == IssueSeverity.Error ? Color.OrangeRed : Color.Gray);
        RefreshMediaTekSummary();
    }

    // Bulk row fill: one layout pass and one summary refresh instead of one per cell. The flag is also read by the
    // CellValueChanged handler above, so a table load cannot repaint the grid while it is still being built.
    private bool mediaTekGridFilling;
    private void BeginMediaTekGridFill()
    {
        mediaTekGridFilling = true;
        mediaTekGrid.SuspendLayout();
    }
    private void EndMediaTekGridFill()
    {
        mediaTekGrid.ResumeLayout();
        mediaTekGridFilling = false;
        RefreshMediaTekSummary();
    }
    private void RebuildMediaTekGrid()
    {
        if (mediaTekPackage == null) return; // Live GPT mode — rows က filter နဲ့ မဆိုင်
        // filter မပြောင်းခင် grid ပေါ်က လက်ရှိ checkbox state ကို set ထဲ ပြန်သိမ်း
        foreach (DataGridViewRow r in mediaTekGrid.Rows)
        {
            if (r.Tag is not ScatterEntry e) continue;
            if (r.Cells[0].Value is true) mediaTekChecked.Add(e.Name);
            else mediaTekChecked.Remove(e.Name);
        }
        bool flashableOnly = mediaTekFlashableOnly?.Checked ?? false;
        BeginMediaTekGridFill();
        try
        {
            mediaTekGrid.Rows.Clear();
            foreach (var entry in mediaTekPackage.Entries)
            {
                bool supported = IsMediaTekUserImage(entry);
                if (flashableOnly && !supported) continue;
                int i = mediaTekGrid.Rows.Add(supported && mediaTekChecked.Contains(entry.Name), entry.Name, entry.FileName,
                    entry.StartHex, FormatBytesLong(entry.Size), FlashRow.StatusText(entry, supported));
                mediaTekGrid.Rows[i].Tag = entry;
                mediaTekGrid.Rows[i].Cells[0].ReadOnly = !supported;
                // The status cell names the risk and the route that can actually write the row, so a row the live
                // native session cannot write says so before the click instead of in the refusal after it.
                mediaTekGrid.Rows[i].Cells[5].ToolTipText = FlashRow.StatusTip(entry, supported);
            }
        }
        finally { EndMediaTekGridFill(); }
    }
    // USER ရွေးချယ်နိုင်တဲ့ entries + preloader/preloader_backup (EMMC_BOOT1_BOOT2, MST ပုံစံ)
    // pgpt/sgpt/gpt/otp/flashinfo ကို python wfscatter က GPT build ထဲ မထည့် — C# ဘက်ကနေပဲ block
    private static bool IsMediaTekUserImage(ScatterEntry e) => e.Selectable && e.ImageExists && e.ImageError == null &&
        e.EffectiveImageSize is > 0 && e.EffectiveImageSize <= e.Size &&
        ((e.RegionRaw == "EMMC_USER" &&
          !new[] { "pgpt", "sgpt", "gpt", "otp", "flashinfo" }.Contains(e.Name.ToLowerInvariant())) ||
         (e.RegionRaw != null && e.RegionRaw.StartsWith("EMMC_BOOT", StringComparison.OrdinalIgnoreCase) &&
          e.PhysicalStart == 0 && e.Name.ToLowerInvariant() is "preloader" or "preloader_backup"));
    private List<string> SelectedMediaTekNames() => mediaTekGrid.Rows.Cast<DataGridViewRow>()
        .Where(r => r.Cells[0].Value is true).Select(r => (string)r.Cells[1].Value).ToList();
    // "☑ Safe" button — Normal-risk flashable တွေကိုပဲ ပြန်ခြစ် (load default နဲ့အတူတူ)
    private void SelectMediaTekNormal()
    {
        foreach (DataGridViewRow r in mediaTekGrid.Rows)
            r.Cells[0].Value = r.Tag is ScatterEntry e && IsMediaTekUserImage(e) && FlashRisk.DefaultSelected(e);
        RefreshMediaTekSummary();
    }
    private void RefreshMediaTekSummary()
    {
        if (mediaTekSummary == null) return;
        int count = SelectedMediaTekNames().Count;
        if (mediaTekPackage == null)
        {
            mediaTekSummary.Text = FlashRow.PackageText(null) + "\n" + FlashRow.SummaryText(count, 0, nativeDa != null);
            return;
        }
        long bytes = 0;
        foreach (DataGridViewRow r in mediaTekGrid.Rows)
            if (r.Cells[0].Value is true && r.Tag is ScatterEntry e && e.EffectiveImageSize is { } len)
                bytes += len;
        mediaTekSummary.Text = FlashRow.PackageText(mediaTekPackage) + "\n" +
            FlashRow.SummaryText(count, bytes, nativeDa != null);
    }
    private async Task RunMediaTekWorkflowAsync(Func<Task> operation)
    {
        // A live native session owns the phone's only BROM face; mtkclient attaches at enumeration and cannot share
        // it — two clients on one BROM corrupt both sessions, and a takeover mid-session answers 0xfe to every byte
        // (RUN 15). The reverse direction (a native boot beside a running workflow) is refused in NativeBootAsync.
        if (nativeDa != null)
            throw new InvalidOperationException("A native DA session is live on this phone — press Eject Device first. " +
                "Two clients on one BROM corrupt both sessions, and a takeover mid-session answers 0xfe to every byte (RUN 15).");
        await RunFlashWorkflowAsync(async () =>
        {
            mediaTekEngine = new MtkClientEngine();
            using var cancel = new CancellationTokenSource(); mediaTekCancel = cancel;
            // Port က dead field — engine က --serialport ဘယ်တော့မှ မထည့် (auto USB detect)
            mediaTekConnection = new("", mtkDaPath, mtkAuthPath, mtkPreloaderPath);
            SetProgress(0, "Starting");
            try { await operation(); SetProgress(100, "Completed", "Done"); }
            catch (OperationCanceledException) { workflowFailed = true; Log("[!] MediaTek operation stopped.", Color.Orange); SetProgress(0, "Stopped", "0 MB/s"); }
            catch { SetProgress(0, "Failed", "0 MB/s"); throw; }
            finally
            {
                mediaTekWriteActive = false; mediaTekCancel = null;
                mtkProgressTotal = 0; mtkProgressDone = 0; mtkProgressBytes = 0;
                mtkPrepTotal = 0; mtkProgressCurTotal = 0; mtkWriteWatchArmed = false;
                mtkResetStarted = false;
                mtkProgressWatch = null;
                mtkProgressSizes.Clear(); mtkProgressNameQueue.Clear(); mtkProgressOrder.Clear();
            }
        }, autoRebootAfter: false);
    }
    private async Task<MtkCommandResult> MediaTekCommandAsync(string[] args, bool writes = false, TimeSpan? timeout = null, string attempt = null)
    {
        if (stopRequested) throw new OperationCanceledException();
        mstSearching = mstHwInfo = mstStorage = mstInternal = false;
        mstStorageBuf.Clear();
        // reset မရှိသေးတဲ့ run မှာ "Stage was't executed" က တကယ့် stage2 failure — ဒါကို မဖျောက်ရ
        mtkResetStarted = false;
        // attempt ပါရင် caller က ကိုယ်တိုင် header ရိုက်ထားပြီး — ထပ်မရိုက်နှင့်
        if (attempt == null)
            Log($" Operation : {MtkOperationName(args)}", Color.White);
        // launch မတိုင်ခင် ဖုန်း USB က ဖြုတ်ပြီးမှ mtkclient စ — attach-at-enumeration သာ
        await EnsureMediaTekUnpluggedAsync();
        Log(" Waiting for device — plug the phone in BROM/Preloader mode now...", Color.Yellow);
        // percent ကိုမထိဘဲ status ပဲ update — flash က staging % နဲ့ လာတယ် (output thread-safe: SetProgress က Invoke)
        SetProgress(progressPercent, "Connecting");
        mediaTekWriteActive = writes;
        using var timeoutCts = timeout.HasValue ? CancellationTokenSource.CreateLinkedTokenSource(mediaTekCancel.Token) : null;
        if (timeoutCts != null) timeoutCts.CancelAfter(timeout.Value);
        CancellationToken token = timeoutCts?.Token ?? mediaTekCancel.Token;
        try
        {
            var result = await mediaTekEngine.RunAsync(mediaTekConnection, args,
                line => LogMtkLine(line), token, writes);
            // Loader auto-detect runs after the command so an in-flight session keeps the loader
            // it already handed to mtkclient; the next operation gets the resolved one.
            // The engine may complete off the UI thread (ConfigureAwait(false)) → marshal the picker update.
            var lines = result?.Lines;
            if (!IsDisposed)
            {
                if (InvokeRequired) BeginInvoke(new Action(() =>
                {
                    // detect first — the self-heal needs the match this run produced
                    try { ApplyMediaTekLoaderFromOutput(lines); HandleMediaTekLoaderFailure(result); } catch { }
                }));
                else
                {
                    ApplyMediaTekLoaderFromOutput(lines);
                    HandleMediaTekLoaderFailure(result);
                }
            }
            return result;
        }
        finally { FlushMtkStorage(); mediaTekWriteActive = false; }
    }

    // MST-style operation header
    private static string MtkOperationName(string[] args)
    {
        if (args.Length >= 2 && args[0] == "multi")
        {
            string c = args[1];
            if (c.Contains("wfscatter", StringComparison.Ordinal)) return "MEDIATEK FLASH";
            if (c.Contains("da seccfg unlock", StringComparison.Ordinal)) return "XIAOMI DIRECT BOOTLOADER UNLOCK";
            if (c.Contains("da seccfg lock", StringComparison.Ordinal)) return "XIAOMI BOOTLOADER LOCK";
            if (c.Contains("e nvram")) return "NV ERASE (all NV partitions)";
            if (c.Contains("w nvram")) return "NV RESTORE (backup images)";
            if (c.Contains("r nvram")) return "NV BACKUP (nvram/nvdata/nvcfg/proinfo/protect)";
            // The firmware backup's own shape: printgpt + one `r` per ticked partition, all in one multi session.
            // It sits after the NV checks so the six-partition NV script keeps its own title.
            if (c.StartsWith("printgpt;r ", StringComparison.Ordinal))
            {
                int n = Regex.Matches(c, @";r ").Count;
                return "MEDIATEK FIRMWARE BACKUP (" + n + " partition" + (n == 1 ? "" : "s") + ")";
            }
            var m = Regex.Match(c, @"^e ([^;]+)");
            if (m.Success)
            {
                string ps = m.Groups[1].Value;
                if (ps.Count(ch => ch == ',') >= 5) return "MEDIATEK FORMAT ALL (" + ps.Split(',').Length + " partitions)";
                return ps.ToLowerInvariant() switch
                {
                    "frp" => "FRP RESET (Google account)",
                    "userdata" => "USERLOCK RESET (userdata erase)",
                    "persist" => "MICLOUD RESET (persist erase)",
                    "nvdata" => "NV RESET (baseband/IMEI)",
                    _ => "PARTITION ERASE (" + ps.ToUpperInvariant() + ")"
                };
            }
        }
        if (args.Length >= 2 && string.Equals(args[0], "da", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(args[1], "imei", StringComparison.OrdinalIgnoreCase))
            return "MEDIATEK IMEI WRITE";
        return args[0] switch
        {
            "printgpt" or "gpt" => "READ PARTITION TABLE",
            "wfscatter" => "MEDIATEK FLASH",
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
        // mtkclient prints its own reason through its logger, coloured: red = error, yellow = warning. The mapper
        // below is a milestone table and used to drop those, which is how a failed flash showed "[FAIL] write not
        // confirmed" with no cause on screen (2026-11-05 run: "DaHandler - [LIB]: Scatter has no partitions.").
        bool libError = line.Contains("[LIB]", StringComparison.Ordinal) &&
                        line.Contains("\u001b[31m", StringComparison.Ordinal);
        bool libWarn = line.Contains("[LIB]", StringComparison.Ordinal) &&
                       line.Contains("\u001b[33m", StringComparison.Ordinal);
        string raw = Regex.Replace(line, @"\x1B\[[0-9;]*[a-zA-Z]", "");
        if (string.IsNullOrWhiteSpace(raw)) return;
        string display = raw.Trim();
        // PMK reset က နောက်ဆုံး step; chip reset က USB link ကို ဖြတ်တာမို့ mtkclient က "Stage was't executed"
        // ကို ပြတယ်. 2.2.9 lock note နဲ့ 2026-11-06 08:40 run နှစ်ခုလုံးမှာ reset ပြီးမှ ပေါ်တာကို တွေ့ရတယ် —
        // ဒါကြောင့် PMK reset line နောက်မှာလာတဲ့ ဒီ line တစ်ကြောင်းကိုပဲ ဖျောက်တယ် (raw log ထဲ ရှိဆဲ).
        if (display.Contains("PMK reset:", StringComparison.Ordinal)) mtkResetStarted = true;
        if (mtkResetStarted && display.Contains("Stage was't executed", StringComparison.Ordinal)) return;
        TrackMtkWriteProgress(display);
        var mapped = MapMtkMstLine(display);
        if (mapped.HasValue) { Log(mapped.Value.text, mapped.Value.color); return; }
        if (libError || libWarn)
        {
            FlushMtkStorage();
            Log("  ! " + display, libError ? Color.OrangeRed : Color.Orange);
        }
    }

    // mtkclient ရဲ့ per-file `Wrote <path> to sector <n> with sector count <m>.` (evidence နဲ့ ကိုက်တဲ့ raw format)
    // ကနေ Operation Progress 0→95% + Total-bytes + speed တိုက်ရိုက် update — output thread ကနေခေါ်တယ်
    private void TrackMtkWriteProgress(string raw)
    {
        if (mtkProgressTotal <= 0 || !raw.StartsWith("Wrote ", StringComparison.Ordinal)) return;
        int i = raw.IndexOf(" to sector ", StringComparison.Ordinal);
        if (i <= 6) return;
        string token = raw.Substring(6, i - 6);
        if (mtkWriteWatchArmed) { mtkProgressWatch?.Restart(); mtkWriteWatchArmed = false; }
        // bytes written: sparse = python progress က HEX2 (flash bytes); raw fallback = sector count × 512
        Match sc = Regex.Match(raw, @"with sector count (\d+)");
        long sz = mtkProgressCurTotal > 0 ? mtkProgressCurTotal
            : sc.Success && long.TryParse(sc.Groups[1].Value, out long sec) ? sec * 512
            : mtkProgressSizes.TryGetValue(token, out long fb) ? fb : 0;
        mtkProgressCurTotal = 0;
        mtkProgressDone++;
        mtkProgressBytes += sz;
        int done = Math.Min(mtkProgressDone, mtkProgressTotal);
        double elapsed = mtkProgressWatch?.Elapsed.TotalSeconds ?? 0;
        if (elapsed > 0.3) UpdateFlashSpeed(mtkProgressBytes / 1048576.0 / elapsed);
        SetProgress(95 * done / mtkProgressTotal, $"Flashing ({done}/{mtkProgressTotal}) · Total - {MstTotal(mtkProgressBytes)}");
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

        // reset မှာ stub jump → chip reset ဖြစ်တဲ့အခါ USB pipe ပြတ်ပြီး pyusb ရိုက်တဲ့ line —
        // expected (success လက္ခဏာ) မို့ log ထဲ မပြတော့ဘူး; raw transcript ထဲ ရှိဆဲ (diagnostics အတွက်)
        if (t.Contains("USBError", StringComparison.OrdinalIgnoreCase) &&
            t.Contains("Input/Output Error", StringComparison.OrdinalIgnoreCase))
            return null;

        // အမှားများ → raw ပဲ နီ (HasError regex နဲ့ ကိုက်အောင် pattern တွေ အကုန်ပါ)
        if (has("error") || t.Contains("FAIL") || has("failed") || has("failure") ||
            has("traceback") || has("exception") || has("not found") || has("could not") ||
            t.Contains("USBError") || t.Contains("invalid choice") || t.Contains("Aborting") ||
            t.Contains("FileNotFoundError") || t.Contains("Please disconnect"))
            return (t, Color.OrangeRed);

        // wfscatter ရဲ့ GPT rebuild + mismatch warning → MST style ပဲ တိုက်ရိုက်ပြ
        if (t.StartsWith("Updating Primary GPT", StringComparison.Ordinal) ||
            t.StartsWith("Updating Secondary GPT", StringComparison.Ordinal))
            return (" " + t, Color.LightGreen);
        if (t.StartsWith("Warning:", StringComparison.Ordinal))
            return (" " + t, Color.Orange);

        // mtkclient ရဲ့ \r per-chunk progress: `Progress: |bar| 42.5% Write: (0x..), 1.2MB/s 02m10s left`
        // → Operation Progress/speed/status တိုက်ရိုက် update — log ထဲမထည့် (chunk လိုက် dump များလွန်း)
        Match pg = Regex.Match(t, @"(\d+(?:\.\d+)?)%\s+(Write|Read|Erasing|Peek):");
        if (pg.Success && double.TryParse(pg.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double pp))
        {
            Match sm = Regex.Match(t, @"([\d.]+)\s*(B/s|KB/s|MB/s|GB/s)");
            if (sm.Success && double.TryParse(sm.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double mbps))
            {
                if (sm.Groups[2].Value.Equals("B/s", StringComparison.OrdinalIgnoreCase)) mbps /= 1048576.0;
                else if (sm.Groups[2].Value.Equals("KB/s", StringComparison.OrdinalIgnoreCase)) mbps /= 1024.0;
                else if (sm.Groups[2].Value.Equals("GB/s", StringComparison.OrdinalIgnoreCase)) mbps *= 1024.0;
                UpdateFlashSpeed(mbps);
            }
            string kind = pg.Groups[2].Value switch
            {
                "Write" => "Writing",
                "Read" => "Reading",
                "Erasing" => "Erasing",
                _ => "Peek"
            };
            if (mtkProgressTotal > 0)
            {
                // flash: ပြီးသား files + လက်ရှိ file % → 0→95% တွေ့ဆက် (mtkclient % က real signal)။
                // Write: kind ပဲ — DA/handshake ဘက် Read/Erasing က flash progress ကို မပျက်စေနှင့်
                if (!kind.Equals("Writing", StringComparison.Ordinal)) return null;
                if (mtkWriteWatchArmed) { mtkProgressWatch?.Restart(); mtkWriteWatchArmed = false; }
                // (0xDONE/0xTOTAL) hex pair → Total-bytes display (sparse = flash bytes)
                Match hx = Regex.Match(t, @"Write:\s*\(0x([0-9A-Fa-f]+)/0x([0-9A-Fa-f]+)\)");
                if (hx.Success)
                {
                    long.TryParse(hx.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long cur);
                    if (long.TryParse(hx.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long tot))
                        mtkProgressCurTotal = tot;
                    SetProgress((int)(95.0 * Math.Min(mtkProgressDone, mtkProgressTotal) / mtkProgressTotal),
                        $"Writing {MstTotal(mtkProgressBytes + Math.Max(cur, 0))}");
                    return null;
                }
                int curi = Math.Min(mtkProgressDone, mtkProgressTotal - 1);
                string name = curi >= 0 && curi < mtkProgressOrder.Count ? mtkProgressOrder[curi] : "image";
                double frac = Math.Min(mtkProgressDone, mtkProgressTotal) + pp / 100.0;
                SetProgress((int)(95.0 * frac / mtkProgressTotal), $"{kind} {name} {(int)pp}%");
            }
            else SetProgress((int)pp, $"{kind} {(int)pp}%");   // non-flash: op တစ်ခုချင်း 0→100
            return null;
        }

        // patched writeflash — device-announce packet size (outer chunk) = speed-cap diagnostic
        if (t.StartsWith("Packet length:", StringComparison.OrdinalIgnoreCase))
            return (" " + t, Color.Cyan);

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
        // unlock/lock နှစ်ခုလုံး native DA seccfg write — wording neutral (relock run မှာ "Unlocking" မပြမိအောင်)
        if (has("Successfully wrote seccfg")) { FlushMtkStorage(); return (" Writing Bootloader seccfg ... Ok", Color.LightGreen); }
        if (has("already unlocked")) { FlushMtkStorage(); return (" Writing Bootloader seccfg ... Ok ( already unlocked )", Color.LightGreen); }
        if (t.StartsWith("Wrote "))
        {
            FlushMtkStorage();
            // token → partition name queue (script order) — tee1/tee2 စတာတွေ file တူလို့ တစ်ခုတည်း key
            string token = "";
            int cut = t.IndexOf(" to sector ", StringComparison.Ordinal);
            if (cut > 6) token = t.Substring(6, cut - 6);
            string who = "";
            if (mtkProgressNameQueue.TryGetValue(token, out var nameq) && nameq.Count > 0)
                who = nameq.Dequeue();
            return who.Length > 0
                ? (" Flashing [ " + Path.GetFileName(token) + " ] -> [ " + who + " ]... Ok", Color.LightGreen)
                : (" Flashing [ " + Path.GetFileName(token) + " ]... Ok", Color.LightGreen);
        }
        if (has("All partitions formatted")) { FlushMtkStorage(); return (" All partitions formatted ... Ok", Color.LightGreen); }
        if (has("Formatted sector") || has("Erased"))
        {
            FlushMtkStorage();
            var m = Regex.Match(t, @"sector (\d+)");
            return (" Erasing partition ... Ok" + (m.Success ? " ( sector " + m.Groups[1].Value + " )" : ""), Color.LightGreen);
        }
        if (has("Reset command was sent")) { FlushMtkStorage(); return (" Rebooting device... Ok", Color.LightGreen); }

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

    // MST Total ပုံစံ: `Total - 3.63 G`
    private static string MstTotal(long v) =>
        v >= 1L << 30 ? (v / (double)(1L << 30)).ToString("F2") + " G" : (v / (double)(1L << 20)).ToString("F2") + " M";

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
        var deadline = DateTime.UtcNow.AddSeconds(180);
        var nextHint = DateTime.UtcNow.AddSeconds(30);
        while (IsMediaTekUsbPresent())
        {
            if (stopRequested) throw new OperationCanceledException();
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Phone was not unplugged within 180s. Unplug the USB cable, then run FLASH again.");
            if (DateTime.UtcNow >= nextHint)
            {
                int left = Math.Max(1, (int)(deadline - DateTime.UtcNow).TotalSeconds);
                Log($" [!] Still connected — unplug the cable ({left}s left)...", Color.Orange);
                nextHint = DateTime.UtcNow.AddSeconds(30);
            }
            await Task.Delay(500, mediaTekCancel.Token);
        }
    }
    private async Task<IReadOnlyList<MtkPartition>> ReadMediaTekGptAsync(bool display = false)
    {
        var result = await MediaTekCommandAsync(new[] { "printgpt" });
        result.RequireSuccess();
        var table = MtkClientEngine.ParseGpt(result);
        // mtkclient's printgpt does not report the DA geometry, so a table from here carries no binding. It still
        // serves this run through SetLiveTable, but it is deliberately not written to the cache: an unbound table
        // would be refused on load anyway, and writing it would erase the binding a native boot recorded.
        SetLiveTable(table, null);
        if (mediaTekPackage?.Platform is { Length: > 0 } platform &&
            !result.Lines.Any(l => l.Contains(platform, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Device chipset could not be matched to scatter " + platform + ". Nothing written.");
        if (display)
        {
            mediaTekPackage = null; mediaTekScatter.Clear();
            BeginMediaTekGridFill();
            try
            {
                mediaTekGrid.Rows.Clear();
                foreach (var p in table) mediaTekGrid.Rows.Add(false, p.Name, "", "0x" + p.Offset.ToString("x"), FormatBytesLong(p.Length), "Live GPT");
            }
            finally { EndMediaTekGridFill(); }
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
    // scatter-validated live GPT ကို disk ပေါ် persist — app restart / backup / restore မှာ extra plug cycle မလိုတော့
    private string MtkGptCacheFile()
    {
        if (mediaTekPackage == null || string.IsNullOrEmpty(mediaTekPackage.ScatterPath) || !File.Exists(mediaTekPackage.ScatterPath))
            return null;
        using var sha = System.Security.Cryptography.SHA1.Create();
        string hex = Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(mediaTekPackage.ScatterPath))).ToLowerInvariant();
        return Path.Combine(ShopServices.DataDirectory, "MediaTekGptCache", "gpt-" + hex[..16] + ".json");
    }
    /// <summary>
    /// Persist a partition table for reuse, together with the device it came from. A table with no binding is not
    /// written at all: the cache exists to save a plug cycle, and a table that cannot name its device must never
    /// become the reason an extent is trusted. Skipping the write also means an mtkclient printgpt cannot erase the
    /// binding a native boot recorded.
    /// </summary>
    private void SaveMediaTekGptCache(IReadOnlyList<MtkPartition> table, string binding)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(binding)) return;
            string file = MtkGptCacheFile();
            if (file == null || table == null || table.Count == 0) return;
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, JsonSerializer.Serialize(new { SavedUtc = DateTime.UtcNow, Binding = binding, Partitions = table }));
        }
        catch { /* cache failure must never fail the operation */ }
    }
    private IReadOnlyList<MtkPartition> LoadMediaTekGptCache(out string when, out string binding)
    {
        when = null;
        binding = null;
        try
        {
            string file = MtkGptCacheFile();
            if (file == null || !File.Exists(file)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.TryGetProperty("SavedUtc", out var whenEl) &&
                DateTime.TryParse(whenEl.GetString(), null, DateTimeStyles.RoundtripKind, out var saved))
                when = saved.ToLocalTime().ToString("dd.MM.yy HH:mm");
            // Files written before B5.4 carry no Binding — an absent field is "unknown device", not "any device".
            if (doc.RootElement.TryGetProperty("Binding", out var bindEl) && bindEl.ValueKind == JsonValueKind.String)
                binding = bindEl.GetString();
            // A cache that cannot name the device it came from is not a cache we may use: it would hand the next
            // phone the previous one's extents. Drop the file so the operator is told once, not on every operation.
            if (string.IsNullOrWhiteSpace(binding))
            {
                try { File.Delete(file); } catch { }
                return null;
            }
            var list = new List<MtkPartition>();
            foreach (var e in doc.RootElement.GetProperty("Partitions").EnumerateArray())
                list.Add(new MtkPartition(e.GetProperty("Name").GetString(),
                    e.GetProperty("Offset").GetInt64(), e.GetProperty("Length").GetInt64()));
            return list.Count > 0 ? list : null;
        }
        catch { return null; }
    }
    /// <summary>
    /// Every live partition table enters through here, together with which device it came from. A table without its
    /// provenance is how a second phone ends up flashed with the first one's extents, so the pairing is not left to
    /// the caller remembering two fields.
    /// </summary>
    private void SetLiveTable(IReadOnlyList<MtkPartition> table, string binding)
    {
        mediaTekLiveTable = table;
        mediaTekTableBinding = binding;
    }
    // liveTable → disk cache → live read cycle (SP Flash Tool-like: cache hit ဆို plug တစ်ကြိမ်တည်း)
    private async Task<IReadOnlyList<MtkPartition>> GetMediaTekTableAsync()
    {
        if (mediaTekLiveTable != null) return mediaTekLiveTable;
        var cached = LoadMediaTekGptCache(out string when, out string cachedBinding);
        if (cached != null)
        {
            SetLiveTable(cached, cachedBinding);
            Log("  * Partition table reused from cache (" + (when ?? "earlier") + ") — taken from " + cachedBinding +
                ", no extra plug cycle.", Color.Cyan);
            return cached;
        }
        if (when != null)
            Log("  * Cached partition table from " + when + " was ignored — it did not record which device it came " +
                "from, so its extents are not trusted. Reading the device GPT instead.", Color.Orange);
        else
            Log(" No cached partition table — reading GPT first (one extra cycle).", Color.Orange);
        SetProgress(0, "Reading GPT");
        return await ReadMediaTekGptAsync();
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
    private async Task MtkServiceEraseAsync(bool userlock)
    {
        string name = userlock ? "userdata" : "frp";
        string title = userlock ? "Userlock Remove" : "FRP Remove";
        if (MessageBox.Show(this, MtkNativeBench.ServiceEraseConfirmText(userlock), title,
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        if (nativeDa != null)
        {
            await NativeGuarded(title, async () =>
            {
                RequireNativeSession();
                CancellationToken ct = nativeCancel.Token;
                ct.ThrowIfCancellationRequested();
                var table = await nativeDa.ReadPartitionTableAsync(ct).ConfigureAwait(false);
                var storage = await nativeDa.QueryStorageAsync(ct).ConfigureAwait(false);
                MtkDaExtent target = MtkNativeBench.ServiceEraseTarget(table, storage, userlock);
                ct.ThrowIfCancellationRequested();
                NativeSay("[*] Native DA: erasing only " + target.Describe + ". All other partitions are kept.");
                await nativeDa.ErasePartitionAsync(target.Name, ct).ConfigureAwait(false);
                NativeSay("[OK] Native DA: " + target.Name + " erase acknowledged. Android setup/lock state is not yet verified.");
                ct.ThrowIfCancellationRequested();
                await NativeRebootAsync().ConfigureAwait(false);
            });
            return;
        }

        await RunMediaTekWorkflowAsync(() => MtkEraseAndResetAsync(name,
            "[OK] " + name + " erase reported. Verify Android setup/lock state after boot; all other partitions were kept."));
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
            Log(" Finished — device is rebooting to system. Unplug the USB cable.", Color.LightGreen);
        else
            Log(" Reset did not run — reboot the phone to apply.", Color.Orange);
        Log($" Elapsed Time... {sw.Elapsed.TotalSeconds:F0} seconds", Color.Gainsboro);
        Log($" Finished at local time: [{DateTime.Now:dd.MM.yy HH:mm:ss}]", Color.Gainsboro);
    }
    // Format All — erase OS + data (whitelist ∩ live GPT), keep bootchain/seccfg/NV, then reset.
    private async Task FormatAllAsync()
    {
        if (nativeDa != null)
        {
            await NativeGuarded("format all", NativeFormatAllAsync);
            return;
        }
        await RunMediaTekWorkflowAsync(async () =>
        {
            var table = await GetMediaTekTableAsync();
            var targets = MtkNativeBench.FormatAllTargets(table.Select(p => p.Name), NvPartitions).ToList();
            if (!ConfirmFormatAll(targets)) return;
            await MtkEraseAndResetAsync(string.Join(",", targets),
                $"[OK] Format All — {targets.Count} OS/data partitions erased (bootchain + NV/IMEI kept).");
        });
    }

    /// <summary>
    /// The one Format All confirmation, word for word the same on either route: the erase list, what is kept,
    /// and the honest consequence — the phone will not boot until full firmware is flashed back.
    /// </summary>
    private bool ConfirmFormatAll(IReadOnlyList<string> targets)
    {
        if (targets.Count == 0)
            throw new InvalidOperationException("No OS/data partitions found in the device GPT — nothing to format.");
        string list = string.Join(", ", targets);
        string keep = "preloader · bootloader (lk/uboot/tee) · GPT · seccfg (secure boot) · NV/IMEI (nvram/nvdata/nvcfg/proinfo)";
        if (MessageBox.Show(this,
            "FORMAT ALL — erase OS + user data?\n\n" +
            "ERASE (" + targets.Count + "):\n" + list + "\n\n" +
            "KEPT:\n" + keep + "\n\n" +
            "The phone will NOT boot afterwards — flash full firmware to recover. IMEI/baseband are preserved. Continue?",
            "Format All", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            Log(" Format All cancelled by user.", Color.Orange);
            return false;
        }
        return true;
    }

    // IMEI Read — pure file scan, no device/plug cycle. Heuristic ASCII + BCD, Luhn-checked.
    private async Task ReadImeiFromBackupAsync()
    {
        string nvRoot = Path.Combine(ShopServices.DataDirectory, "NVBackup");
        using var pick = new FolderBrowserDialog { Description = "Select NV backup folder (nvram.bin, nvdata.bin, proinfo.bin, …)" };
        if (Directory.Exists(nvRoot)) pick.SelectedPath = nvRoot;
        if (pick.ShowDialog(this) != DialogResult.OK) return;
        string dir = pick.SelectedPath;
        var perFile = new List<(string File, string[] Imeis)>();
        foreach (string name in NvPartitions)
        {
            string f = Path.Combine(dir, name + ".bin");
            if (!File.Exists(f)) continue;
            var imeis = await Task.Run(() => ExtractImeiCandidates(File.ReadAllBytes(f)));
            if (imeis.Length > 0) perFile.Add((Path.GetFileName(f), imeis));
        }
        var distinct = perFile
            .SelectMany(p => p.Imeis.Select(i => (Imei: i, p.File)))
            .GroupBy(x => x.Imei)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (Imei: g.Key, Files: string.Join("+", g.Select(x => x.File).Distinct())))
            .ToList();
        if (distinct.Count == 0)
        {
            Log("  * Result ... no IMEI found in the selected NV backup", Color.Orange);
            MessageBox.Show(this,
                "No IMEI was found in the selected NV backup folder.\n\nScanned: " + string.Join(", ", NvPartitions.Select(n => n + ".bin")) +
                "\n\nIMEI is normally stored in nvram.bin / nvdata.bin / proinfo.bin — make sure this is a full NV backup from this device.",
                "IMEI Read", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        foreach (var x in distinct) Log($"  * IMEI ... {x.Imei}   [{x.Files}]", Color.LightGreen);
        Log("  [!] Scan is heuristic (plain + hex-encoded ASCII, Luhn-checked) — cross-check against the sticker / About phone.", Color.Orange);
        MessageBox.Show(this,
            "IMEI found in NV backup:\n\n" + string.Join("\n", distinct.Select(x => x.Imei + "   (" + x.Files + ")")) + "\n\nfrom:\n" + dir,
            "IMEI Read", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static string[] ExtractImeiCandidates(byte[] data)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        string txt = Encoding.Latin1.GetString(data);
        // 1) Plain ASCII: a 15-digit run bounded by non-digits (proinfo / debug fields).
        ScanPlain(txt, found);
        // 2) Hex-encoded ASCII (MTK nvdata/nvram): the IMEI digits are stored as the
        //    ASCII-hex of the number, e.g. "861762056302584" -> "383631...353834".
        //    Decode each even-length hex run at both nibble alignments and rescan.
        for (int i = 0; i < txt.Length;)
        {
            if (!IsHex(txt[i])) { i++; continue; }
            int j = i;
            while (j < txt.Length && IsHex(txt[j])) j++;
            int len = j - i;
            if (len >= 10)
            {
                for (int off = 0; off <= 1; off++)
                {
                    int even = (len - off) & ~1;
                    if (even >= 10) ScanPlain(HexToAscii(txt, i + off, even), found);
                }
            }
            i = j;
        }
        return found.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    private static void ScanPlain(string s, HashSet<string> found)
    {
        for (int i = 0; i < s.Length;)
        {
            if (s[i] < '0' || s[i] > '9') { i++; continue; }
            int j = i;
            while (j < s.Length && s[j] >= '0' && s[j] <= '9') j++;
            if (j - i == 15 && IsPlausibleImei(s.Substring(i, 15))) found.Add(s.Substring(i, 15));
            i = j;
        }
    }

    private static bool IsHex(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static string HexToAscii(string s, int start, int len)
    {
        var sb = new StringBuilder(len / 2);
        for (int k = 0; k < len; k += 2)
            sb.Append((char)((HexVal(s[start + k]) << 4) | HexVal(s[start + k + 1])));
        return sb.ToString();
    }

    private static int HexVal(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        _ => c - 'A' + 10
    };

    private static bool IsPlausibleImei(string d)
    {
        if (!MtkClientEngine.LuhnValid(d)) return false;
        int zeros = 0;
        bool allSame = true;
        for (int i = 0; i < 15; i++)
        {
            if (d[i] != d[0]) allSame = false;
            if (d[i] == '0') zeros++;
        }
        return !allSame && zeros <= 10;
    }

    // IMEI Write — wires mtkclient's `da imei --write` (LD0B AES-BCD nvdata).
    // CSSD criticalData is RSA-signed by the OEM private key — never re-signed offline,
    // so CSSD devices (e.g. Redmi 9A) may keep their old IMEI. Honest limitation, logged + UI note.
    private async Task WriteImeiAsync(string imei1, string imei2)
    {
        if (!MtkClientEngine.LuhnValid(imei1) || !MtkClientEngine.LuhnValid(imei2))
            throw new InvalidDataException("Enter two valid 15-digit IMEIs (Luhn checksum must pass).");
        if (imei1 == imei2)
            throw new InvalidDataException("IMEI 1 and IMEI 2 must be different.");

        // mtkclient defaults: seed = hex("12abcdef"), aeskey = custom_key_seed padded, product = thunder
        var args = new[]
        {
            "da", "imei", imei1 + "," + imei2, "--write",
            "--seed", "3132616263646566",
            "--aeskey", "0102030405060708090A0B0C0D0E0F1011120B1415161718191A1B1C00000000",
            "--product", "thunder"
        };

        await RunMediaTekWorkflowAsync(async () =>
        {
            Log("  * Note ... da imei rewrites LD0B IMEI. RSA-signed criticalData (CSSD devices) is NOT re-signed — verify in About phone afterwards.", Color.Orange);
            var r = await MediaTekCommandAsync(args, writes: true);
            if (MtkClientEngine.IsImeiWriteSuccess(r))
            {
                Log($"  * Result ... IMEI {imei1} / {imei2} written to nvdata (LD0B).", Color.LightGreen);
                Log("  [!] CSSD devices: if About phone still shows the old IMEI, the signed criticalData needs OEM keys.", Color.Orange);
            }
            else
            {
                foreach (string l in r.ErrorLines.Take(6)) Log("  ! " + l.Trim(), Color.OrangeRed);
                throw new IOException(r.HasError ? "mtkclient reported an error." : "IMEI write was not confirmed.");
            }
        });
    }
    private async Task SeccfgAutoPatchAsync(bool unlock)
    {
        // Empirical: state 0x03 = UNLOCKED (MST-written valid image), 0x00 = LOCKED.
        uint target = unlock ? 3u : 0u;
        await RunMediaTekWorkflowAsync(async () =>
        {
            string flag = unlock ? "unlock" : "lock";
            string what = unlock ? "UNLOCK" : "LOCK";
            // Primary path: native `da seccfg` — the DA reads the live partition and
            // computes the device-bound checksum itself, so no cached image is written.
            // The seccfg-backup cache is an archive only; a cached image from another
            // phone (checksum is device-bound) must never be patch-written to this device.
            string cmds = $"da seccfg {flag};reset";
            const int maxAttempts = 3;
            string lastErr = "unknown";
            var sw = Stopwatch.StartNew();
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                Log($" Operation : {MtkOperationName(new[] { "multi", cmds })} (attempt {attempt}/{maxAttempts})", Color.White);
                try
                {
                    var r = await MediaTekCommandAsync(new[] { "multi", cmds }, writes: true,
                        attempt: $" (attempt {attempt}/{maxAttempts})");
                    bool native = r.Lines.Any(l => l.Contains("Successfully wrote seccfg.", StringComparison.OrdinalIgnoreCase));
                    if (!native)
                    {
                        foreach (string l in r.ErrorLines.Take(6)) Log("  ! " + l.Trim(), Color.OrangeRed);
                        if (r.ExitCode != 0) Log($"  * Exit code ... {r.ExitCode}", Color.OrangeRed);
                        throw new IOException(r.HasError ? "mtkclient reported an error." : "seccfg write was not confirmed.");
                    }
                    bool resetOk = r.Lines.Any(l => l.Contains("Reset command was sent", StringComparison.OrdinalIgnoreCase));
                    Log($"  * Result ... seccfg {what} written via native DA", Color.LightGreen);
                    if (resetOk)
                        Log(" Finished — device is rebooting to system. Unplug the USB cable.", Color.LightGreen);
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
            // Native failed — read the LIVE seccfg (never a cached image), patch, write.
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
            Log($"[MTK] seccfg already {(unlock ? "unlocked" : "locked")} on device — writing as-is.", Color.Orange);
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
            Log("[OK] Reset sent — device reboots to system, unplug the USB cable.", Color.LightGreen);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            Log("[!] Auto-reboot no response — unplug and power on the phone manually.", Color.Orange);
        }
    }
    private Task SaveMediaTekGptAsync() => nativeDa != null
        // A live session already holds the device's own header and entry array, so the native route is the same
        // folder for one extra read instead of a second full boot.
        ? NativeGuarded("GPT save", NativeSaveGptAsync)
        : RunMediaTekWorkflowAsync(async () =>
    {
        using var pick = new FolderBrowserDialog { Description = "Save GPT backup" };
        if (pick.ShowDialog(this) != DialogResult.OK) return;
        string dir = Path.Combine(pick.SelectedPath, "GPT-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var result = await MediaTekCommandAsync(new[] { "gpt", dir }); result.RequireSuccess();
        if (!Directory.EnumerateFiles(dir).Any(f => new FileInfo(f).Length > 0)) throw new IOException("GPT backup files were not produced.");
        Log("[OK] GPT files saved: " + dir, Color.LightGreen);
    });
    private async Task BackupMediaTekAsync()
    {
        // A live session already owns the phone: one boot, one READ per partition, and the scatter comes off the
        // table it cached. Nothing here needs the batched client.
        if (nativeDa != null)
        {
            await NativeGuarded("partition backup", () => NativeBackupAsync(false));
            return;
        }

        // Without a live session the batched client still reads the whole selection in ONE boot: `multi` runs
        // printgpt and every `r` as one script. The old one-boot-per-partition loop is gone; what stays is the
        // same exact-size gate the native path uses, checked against the GPT this run itself reads.
        List<string> names = SelectedMediaTekNames();
        if (names.Count == 0)
        {
            Log("[!] No partitions are ticked on the FLASH tab. Press Read Info / GPT on the INFO tab to load the " +
                "live table (or load a scatter), tick the partitions to back up, then run Backup Selected again. " +
                "Nothing was read.", Color.Orange);
            return;
        }
        List<string> unsafeNames = names.Where(n => !Regex.IsMatch(n, @"^[A-Za-z0-9_-]+$")).ToList();
        if (unsafeNames.Count > 0)
        {
            Log("[FAIL] Unsafe partition name(s) in the selection: " + string.Join(", ", unsafeNames) +
                ". Nothing was read.", Color.Red);
            return;
        }

        string dest;
        using (var pick = new FolderBrowserDialog { Description = "Select backup destination" })
        {
            if (pick.ShowDialog(this) != DialogResult.OK)
            {
                Log("[*] Backup declined — no folder was chosen, nothing was read.", Color.Gray);
                return;
            }
            dest = Path.Combine(pick.SelectedPath, "MTK-" + Guid.NewGuid().ToString("N"));
        }

        await RunMediaTekWorkflowAsync(async () =>
        {
            var sw = Stopwatch.StartNew();
            Directory.CreateDirectory(dest);
            // ONE plug cycle: the table and every image come off the same session.
            string script = "printgpt;" + string.Join(";", names.Select(n => $"r {n} {n}.bin"));
            var r = await MediaTekCommandAsync(new[] { "multi", script });
            var (live, _) = ParseGptLines(r.Lines);
            if (live.Count > 0) SetLiveTable(live, null);
            if (live.Count == 0)
            {
                foreach (string l in r.ErrorLines.Take(6)) Log("  ! " + l.Trim(), Color.OrangeRed);
                try { Directory.Delete(dest, true); } catch { }
                throw new IOException("Partition table was not read — firmware backup aborted.");
            }

            string sess = mediaTekEngine.SessionDirectory;
            var layout = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in names)
            {
                var part = live.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (part == null)
                {
                    foreach (string l in r.ErrorLines.Take(6)) Log("  ! " + l.Trim(), Color.OrangeRed);
                    try { Directory.Delete(dest, true); } catch { }
                    throw new IOException("Firmware backup aborted: this device's GPT has no partition named '" +
                        name + "'.");
                }
                string staged = Path.Combine(sess, part.Name + ".bin");
                long got = File.Exists(staged) ? new FileInfo(staged).Length : -1;
                if (got != part.Length)
                {
                    foreach (string l in r.ErrorLines.Take(6)) Log("  ! " + l.Trim(), Color.OrangeRed);
                    try { Directory.Delete(dest, true); } catch { }
                    throw new IOException($"Firmware backup incomplete: {part.Name} (expected {part.Length}, got {got})");
                }
                layout[part.Name] = part.Length;
            }
            foreach (var kv in layout)
            {
                File.Move(Path.Combine(sess, kv.Key + ".bin"), Path.Combine(dest, kv.Key + ".bin"), true);
                Log($"  * Backup {kv.Key} ... {MstBytes(kv.Value)}", Color.LightGreen);
            }
            ShopServices.WriteBackup(dest, "MTK", layout, layout.Keys, Application.ProductVersion);
            RegisterBackup(dest);
            Log($"  * Result ... firmware backup saved — {layout.Count} partitions", Color.LightGreen);
            Log(" [OK] " + dest, Color.LightGreen);
            // The folder is only a firmware package once the scatter beside it describes the whole live table.
            TryWriteClientFirmwareScatter(dest, live, layout.Keys, r.Lines);
            Log($" Elapsed Time... {sw.Elapsed.TotalSeconds:F0} seconds", Color.Gainsboro);
            Log($" Finished at local time: [{DateTime.Now:dd.MM.yy HH:mm:ss}]", Color.Gainsboro);
        });
    }

    /// <summary>
    /// P1.5 fallback — the scatter the native backup writes, built from the GPT the batched run just read. The
    /// storage word is what the client printed for this unit; a UFS or an unreported storage gets no file, because
    /// the region word of a UFS scatter has never been read off a real package on this bench. Every live extent
    /// gets a row (the dumped ones as images, the rest reserved) for the same reason the native writer does:
    /// <c>wfscatter</c> rebuilds the GPT from the rows it parses, so a package holding only the ticked partitions
    /// would delete the rest of the phone's table.
    /// </summary>
    private void TryWriteClientFirmwareScatter(string dir, IReadOnlyList<MtkPartition> live,
        IEnumerable<string> dumped, IReadOnlyList<string> lines)
    {
        string chipset = MtkDaCatalog.PrimaryChipset(FindMtkInfo(lines, @"CPU:\s+(.+)"));
        if (chipset.Length == 0) chipset = MtkDaCatalog.ChipsetFromPayloadLines(lines);
        string storage = lines.Any(l => l.Contains("EMMC ID:", StringComparison.OrdinalIgnoreCase)) ? "EMMC"
            : lines.Any(l => l.Contains("UFS ID:", StringComparison.OrdinalIgnoreCase) ||
                             l.Contains("UFS Blocksize:", StringComparison.OrdinalIgnoreCase)) ? "UFS"
            : "";
        IReadOnlyList<ScatterRow> rows = MtkNativeBench.ScatterRowsFromGpt(live, dumped, storage, out string refusal);
        if (rows == null)
        {
            Log("[*] No scatter written — " + refusal, Color.Gray);
            return;
        }
        if (chipset.Length == 0)
        {
            Log("[*] No scatter written — this run did not report the chipset, and a package needs the platform " +
                "its rows belong to. The images and the manifest are saved.", Color.Gray);
            return;
        }
        try
        {
            string path = Path.Combine(dir, ScatterWriter.DefaultFileName(chipset));
            File.WriteAllText(path, ScatterWriter.Build(chipset, "PMK-device-dump", "EMMC", rows));
            Log("[+] Firmware package written — " + Path.GetFileName(path) + ": " +
                rows.Count(r => r.IsDownload) + " flashable row(s) of " + rows.Count + " — the " +
                rows.Count(r => r.IsReserved) + " rows without an image keep the rest of the live table.", Color.LightGreen);
            Log("[*] Load that file on the FLASH tab to re-flash these images; the reserved rows stop the GPT " +
                "rebuild from deleting partitions this dump did not read.", Color.Gray);
        }
        catch (Exception ex)
        {
            // The images and the manifest are already on disk; losing the index must not throw the run away.
            Log("[!] Dumps saved, scatter NOT written: " + ex.Message, Color.Orange);
        }
    }

    private static readonly string[] NvPartitions = { "nvram", "nvdata", "nvcfg", "proinfo", "protect1", "protect2" };
    private const string MtkNvBackupRootKey = "MtkNvBackupRoot";

    // The Format All erase policy (FormatAllEraseable / FormatAllProtected / FormatAllTargets) lives in
    // MtkNativeBench beside the never-program gate it shares, so regression checks pin what it may erase.

    // pmk_settings.txt merge-read (Form1.SaveSettings also preserves these panel keys)
    private static string GetMtkSetting(string key)
    {
        try
        {
            string path = ShopServices.DataPath("pmk_settings.txt");
            if (!File.Exists(path)) return null;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                if (line.Substring(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    string v = line.Substring(eq + 1).Trim();
                    return v.Length == 0 ? null : v;
                }
            }
        }
        catch { }
        return null;
    }

    private static void SetMtkSetting(string key, string value)
    {
        try
        {
            string path = ShopServices.DataPath("pmk_settings.txt");
            var lines = File.Exists(path) ? new List<string>(File.ReadAllLines(path)) : new List<string>();
            bool replaced = false;
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                if (line.Substring(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = key + "=" + value;
                    replaced = true;
                    break;
                }
            }
            if (!replaced) lines.Add(key + "=" + value);
            File.WriteAllLines(path, lines);
        }
        catch { }
    }

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
    private Task NvBackupAsync(string destRoot = null) => RunMediaTekWorkflowAsync(async () =>
    {
        var sw = Stopwatch.StartNew();
        string script = "printgpt";
        foreach (string n in NvPartitions) script += $";r {n} {n}.bin";
        var r = await MediaTekCommandAsync(new[] { "multi", script });
        // GPT sizes from the same run (no RequireSuccess — a missing NV part is not fatal)
        var (live, sizes) = ParseGptLines(r.Lines);
        if (live.Count > 0) SetLiveTable(live, null);
        if (sizes.Count == 0)
        {
            foreach (string l in r.ErrorLines.Take(6)) Log("  ! " + l.Trim(), Color.OrangeRed);
            try { Directory.Delete(mediaTekEngine.SessionDirectory, true); } catch { }
            throw new IOException("Partition table was not read — NV backup aborted.");
        }
        string root = string.IsNullOrWhiteSpace(destRoot) ? Path.Combine(ShopServices.DataDirectory, "NVBackup") : destRoot;
        string dest = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
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
        // One NV restore button, two routes — the same rule as NV Backup. A live session hash-checks the manifest,
        // matches every extent to this device byte for byte and writes over the session it already has; without one
        // the batched client still runs its single `multi` session. Each route asks its own confirmation, so the
        // operator sees exactly one dialog, and sees it with the facts the route actually checked.
        if (nativeDa != null)
        {
            await NativeGuarded("NV restore", () => NativeNvRestoreAsync(srcDir));
            return;
        }
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
            if (live.Count > 0) SetLiveTable(live, null);
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
            Log(" Finished — device is rebooting to system. Unplug the USB cable.", Color.LightGreen);
            Log($" Elapsed Time... {sw.Elapsed.TotalSeconds:F0} seconds", Color.Gainsboro);
            Log($" Finished at local time: [{DateTime.Now:dd.MM.yy HH:mm:ss}]", Color.Gainsboro);
        });
    }
    // FLASH → one validated write list; validation identical to the old direct path (confirm happens at run)
    private MtkFlashStep BuildFlashStep()
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
        mediaTekPackage = fresh;
        MtkFlashStep step = new()
        {
            Title = "FLASH: " + string.Join(", ", names)
        };
        foreach (string name in names)
        {
            var e = fresh.Find(name);
            step.Files.Add((name, e.ResolvedPath, e.PhysicalStart, e.Size));
        }
        return step;
    }

    // FLASH → ONE wfscatter multi script → ONE plug cycle: python rebuilds GPT + streams images (sparse-aware)
    private async Task RunMtkFlashAsync(MtkFlashStep step)
    {
        await RunMediaTekWorkflowAsync(async () =>
        {
            var sw = Stopwatch.StartNew();
            Log($" Operation : MEDIATEK FLASH ({step.Files.Count} partitions, one plug cycle)", Color.White);
            // progress arm — python writes 0→95%, workflow end 100% (staging/prep မရှိ — python က sparse streaming)
            mtkProgressTotal = step.Files.Count;
            mtkProgressDone = 0; mtkProgressBytes = 0; mtkProgressCurTotal = 0;
            mtkPrepTotal = step.Files.Sum(f => f.Size);
            mtkProgressSizes.Clear(); mtkProgressNameQueue.Clear(); mtkProgressOrder.Clear();
            SetProgress(0, "Validating selection");
            string scatterPath = Path.GetFullPath(mediaTekPackage.ScatterPath);
            foreach (var f in step.Files)
            {
                string full = Path.GetFullPath(f.Path);
                if (!File.Exists(full)) throw new FileNotFoundException("Image is missing: " + f.Name, full);
                // partition size = Total-bytes fallback (sparse က python progress HEX ကိုပဲ သုံး)
                mtkProgressSizes[full] = f.Size;
                if (!mtkProgressNameQueue.TryGetValue(full, out var nameq))
                    mtkProgressNameQueue[full] = nameq = new Queue<string>();
                nameq.Enqueue(f.Name);
                // EMMC_BOOT1_BOOT2 preloader က boot1+boot2 နှစ်ကြောင်း evidence လာမယ် — MST display အတွက်
                if (f.Name.Equals("preloader", StringComparison.OrdinalIgnoreCase) &&
                    mediaTekPackage.Find(f.Name) is { RegionRaw: not null } be &&
                    be.RegionRaw.Equals("EMMC_BOOT1_BOOT2", StringComparison.OrdinalIgnoreCase))
                    nameq.Enqueue("preloader_backup");
                mtkProgressOrder.Add(f.Name);
            }
            // readback verify (opt-in) — write ပြီးတာနဲ့ partition ကို ပြန်ဖတ်ပြီး source image ရဲ့
            // ပထမ N bytes နဲ့ SHA-256 တိုက်မယ် (same plug cycle, multi script ထဲ ထည့်လိုက်တာ)
            var readbacks = new List<(string Name, string Source, string SessionFile)>();
            if (mediaTekVerifyReadback?.Checked == true)
            {
                foreach (var f in step.Files)
                {
                    if (mediaTekPackage.Find(f.Name) is not { RegionRaw: "EMMC_USER" }) continue;   // boot1/boot2 preloader မပါ
                    if (ReviewSafety.IsAndroidSparse(f.Path))
                    {
                        Log(" [i] " + f.Name + " — sparse image, readback verify skipped", Color.Gray);
                        continue;
                    }
                    readbacks.Add((f.Name, Path.GetFullPath(f.Path), "readback-" + SafeReadbackName(f.Name) + ".img"));
                }
                if (readbacks.Count > 0)
                    Log($" [i] Readback verify: {readbacks.Count} partition(s) read back + hashed in the same session", Color.Cyan);
            }
            // wfscatter <scatter> <name,…>;r <part> <file>;…;reset — python shlex-splits (quoted paths = space-safe)
            // Partition names feed the multi script raw — reject anything that could inject extra commands.
            var invalidNames = step.Files.Where(f => !Regex.IsMatch(f.Name, @"^[A-Za-z0-9_-]+$")).Select(f => f.Name).ToList();
            if (invalidNames.Count > 0)
                throw new InvalidDataException("Unsafe partition name(s) in selection: " + string.Join(", ", invalidNames) + ".");
            string script = "wfscatter " + QuoteCmdArg(scatterPath) + " " +
                string.Join(",", step.Files.Select(f => f.Name));
            if (readbacks.Count > 0)
                script += ";" + string.Join(";", readbacks.Select(r => "r " + r.Name + " " + r.SessionFile));
            script += ";reset";
            mtkProgressWatch = Stopwatch.StartNew();
            mtkWriteWatchArmed = true;   // first Write line မှာ speed-denominator watch ကို restart
            var r = await MediaTekCommandAsync(new[] { "multi", script }, writes: true, attempt: "");
            bool resetOk = r.Lines.Any(l => l.Contains("Reset command was sent", StringComparison.OrdinalIgnoreCase));
            // per-file evidence — tee1/tee2 စတာတွေ token တူလို့ တစ်ခုချင်း consume (count-based)
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string l in r.Lines)
            {
                string t = l.TrimStart();
                if (!t.StartsWith("Wrote ", StringComparison.Ordinal)) continue;
                int c = t.IndexOf(" to sector ", StringComparison.Ordinal);
                if (c <= 6) continue;
                string tok = t.Substring(6, c - 6);
                counts[tok] = counts.GetValueOrDefault(tok) + 1;
            }
            var failed = new List<string>();
            foreach (var f in step.Files)
            {
                string tok = Path.GetFullPath(f.Path);
                if (counts.TryGetValue(tok, out int n) && n > 0) counts[tok] = n - 1;
                else failed.Add(f.Name);
            }
            if (failed.Count > 0)
            {
                foreach (var f in step.Files)
                    if (failed.Contains(f.Name)) Log(" [FAIL] " + f.Name + " — write not confirmed", Color.OrangeRed);
                foreach (string l in r.ErrorLines.Take(8)) Log("  ! " + l.Trim(), Color.OrangeRed);
                if (r.ExitCode != 0) Log($"  * Exit code ... {r.ExitCode}", Color.OrangeRed);
                throw new IOException("Write completion was not confirmed: " + string.Join(", ", failed) + ". It may be partially written.");
            }
            if (readbacks.Count > 0) VerifyReadbacks(readbacks);
            Log($" Total - {MstTotal(mtkProgressBytes)}", Color.Gainsboro);
            Log(resetOk
                    ? " Finished — device is rebooting. Unplug the USB cable."
                    : " Reset did not run — reboot the phone to apply.",
                resetOk ? Color.LightGreen : Color.Orange);
            Log($" Elapsed Time... {sw.Elapsed.Minutes} minutes {sw.Elapsed.Seconds} seconds", Color.Gainsboro);
            Log($" Finished at local time: [{DateTime.Now:dd.MM.yy HH:mm:ss}]", Color.Gainsboro);
            Log(" [!] First boot after a full flash can take 5-10 minutes (black screen while Android", Color.Gold);
            Log("     initializes). If it stays dark, charge the phone 15 minutes before retrying.", Color.Gold);
        });
    }

    // Readback ဖိုင်တွေကို source image ရဲ့ size အထိ SHA-256 တိုက်ပြီး session ထဲက readback ဖိုင်ကို ဖျက်တယ်
    // (partition size က image ထက် ကြီးနိုင် — ကျန်တဲ့ padding ကို မတိုက်ဘူး)
    private void VerifyReadbacks(List<(string Name, string Source, string SessionFile)> readbacks)
    {
        string dir = mediaTekEngine.SessionDirectory;
        var bad = new List<string>();
        try
        {
            foreach (var rb in readbacks)
            {
                string full = Path.Combine(dir, rb.SessionFile);
                try
                {
                    if (!File.Exists(full))
                    {
                        bad.Add(rb.Name + " (readback file missing)");
                        Log(" [FAIL] " + rb.Name + " — readback file was not produced", Color.OrangeRed);
                        continue;
                    }
                    long srcLen = new FileInfo(rb.Source).Length;
                    if (new FileInfo(full).Length < srcLen)
                    {
                        bad.Add(rb.Name + " (readback shorter than image)");
                        Log(" [FAIL] " + rb.Name + " — readback shorter than the image", Color.OrangeRed);
                        continue;
                    }
                    string want = Sha256Prefix(rb.Source, srcLen);
                    string have = Sha256Prefix(full, srcLen);
                    if (!want.Equals(have, StringComparison.OrdinalIgnoreCase))
                    {
                        bad.Add(rb.Name + " (SHA-256 mismatch)");
                        Log(" [FAIL] " + rb.Name + " — readback SHA-256 MISMATCH", Color.OrangeRed);
                        continue;
                    }
                    Log(" [OK] " + rb.Name + " — readback SHA-256 matches", Color.LightGreen);
                }
                catch (Exception ex)
                {
                    bad.Add(rb.Name + " (" + ex.Message + ")");
                    Log(" [FAIL] " + rb.Name + " — readback check error: " + ex.Message, Color.OrangeRed);
                }
            }
        }
        finally
        {
            // readback ဖိုင်တွေ ချက်ချင်း ဖျက် — session folder ကြီးထွားမှု မဖြစ်အောင်
            foreach (var rb in readbacks)
            {
                try { File.Delete(Path.Combine(dir, rb.SessionFile)); } catch { }
            }
        }
        if (bad.Count > 0)
            throw new IOException("Readback verification failed: " + string.Join(", ", bad) + ". The write may be corrupt — do not boot the phone on it.");
        Log($" [OK] Readback verified — {readbacks.Count} partition(s)", Color.LightGreen);
    }

    private static string SafeReadbackName(string name) =>
        new string(name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());

    private static string Sha256Prefix(string path, long bytes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha = System.Security.Cryptography.SHA256.Create();
        byte[] buffer = new byte[1024 * 1024];
        long left = bytes;
        while (left > 0)
        {
            int n = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
            if (n <= 0) throw new IOException("Unexpected end of file: " + path);
            sha.TransformBlock(buffer, 0, n, null, 0);
            left -= n;
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash ?? Array.Empty<byte>());
    }

    // multi script က shlex-split လုပ်တယ် — quote only when the path contains a space
    private static string QuoteCmdArg(string p) => p.IndexOf(' ') >= 0 ? "\"" + p + "\"" : p;

    private async Task FlashMediaTekAsync()
    {
        // A live session owns the port; sparse expansion stays on the host and never starts a second client.
        if (nativeDa != null)
        {
            // The live GPT fills the grid with names and extents but no images: the package is what supplies the
            // bytes, and BuildFlashStep is the only thing that knows how to read it. Say that here instead of letting
            // the generic "select a scatter" refusal imply the session is the problem.
            if (mediaTekPackage == null)
                throw new InvalidOperationException("A native session is live, but no scatter package is loaded. Load " +
                    "the package on the FLASH tab first — the live GPT supplies the extents, and the package supplies " +
                    "the images this route checks against them. Nothing was written.");
            var nativeStep = BuildFlashStep();
            await NativeGuarded("flash", () => NativeFlashAsync(nativeStep));
            return;
        }
        var step = BuildFlashStep();
        if (MessageBox.Show(this, "Write these partitions?\n\n" + string.Join(", ", step.Files.Select(f => f.Name)) + "\n\nThe GPT partition table will be rebuilt from the scatter file, then the selected partitions will be written (sparse-aware, one plug cycle).", "Confirm flash", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        await RunMtkFlashAsync(step);
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
            var table = await GetMediaTekTableAsync();
            var part = table.Single(p => p.Name.Equals(partition, StringComparison.OrdinalIgnoreCase));
            using var handle = op == "w" ? new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
            if (op == "w" && (handle.Length != part.Length || ReviewSafety.IsAndroidSparse(file)))
                throw new InvalidDataException("Restore requires an exact-size raw partition backup. Use Scatter Flash for firmware images.");
            bool verify = op == "w" && mediaTekVerifyReadback?.Checked == true;
            string full = Path.GetFullPath(file);
            string rbFile = "readback-" + SafeReadbackName(partition) + ".img";
            // verify ON ဆို write + readback ကို multi script တစ်ခုတည်းထဲ ထည့် — plug cycle မတိုးဘူး
            var args = verify
                ? new[] { "multi", "w " + partition + " " + QuoteCmdArg(full) + ";r " + partition + " " + rbFile }
                : new[] { op, partition, file };
            var result = await MediaTekCommandAsync(args, writes: op == "w");
            if (op == "w")
            {
                if (verify)
                {
                    MtkClientEngine.RequireWriteEvidence(result, partition, full);
                    VerifyReadbacks(new List<(string, string, string)> { (partition, full, rbFile) });
                    Log("[OK] " + partition + " · write + readback verified", Color.LightGreen);
                }
                else
                {
                    MtkClientEngine.RequireWriteEvidence(result, partition, file);
                    Log("[OK] " + partition + " · write reported; readback not performed", Color.LightGreen);
                }
            }
            else
            {
                result.RequireSuccess();
                if (!File.Exists(file) || new FileInfo(file).Length != part.Length) throw new IOException("Partition read is incomplete.");
                Log("[OK] " + partition + " · backup size checked", Color.LightGreen);
            }
            completed = true;
        });
        return completed;
    }
}
