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
    public partial class Form1 : Form
    {
        // Windows Device Change Message Constants
        private const int WM_DEVICECHANGE = 0x0219;
        private const int DBT_DEVICEARRIVAL = 0x8000;
        private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
        private const int DBT_DEVNODES_CHANGED = 0x0007;

        // Top Header Controls
        private Panel panelTopHeader;
        private Label lblPort;
        private ComboBox cmbPorts;
        private Button btnRefreshPorts;
        private Button btnOpenDeviceManager;
        private Button btnDrivers;
        private Label lblDeviceModeStatus;
        private Label lblLicenseInfo; // login email + expiry days
        private Label lblClock;       // live time
        private Panel panelLicenseBadge; // purple license chip behind email
        private System.Windows.Forms.Timer clockTimer;

        // UI Tabs
        private TabControl tabControl;
        private TabPage tabAdb;
        private TabPage tabFb;
        private TabPage tabMtk;
        private TabPage tabQc;
        private TabPage tabSamsung;
        private TabPage tabSpd;
        private TabPage tabHisi;
        private RichTextBox rtbLog;
        private Button btnClearLog;
        private Button btnStopTask;

        // Progress Bar Panel Controls
        private Panel panelProgress;
        private Panel pbarTrack;      // rounded custom progress bar (MST style)
        private Panel pbarTicks;      // bottom row ruler ticks
        private Label lblProgressTitle;
        private Label lblProgressPercent;
        private Label lblProgressStatus;
        private Label lblStatusCaption;
        private Label lblSpeedBadge;
        private int progressPercent;
        private long flashBytesDone;               // QC flash ပြီးသွားပြီးသား file တွေရဲ့ bytes (speed)
        private long flashCurrentFileSize;         // လက်ရှိ ရေးနေတဲ့ file size (နောက် line ရောက်မှ count)
        private Stopwatch flashSpeedWatch;

        // Layout containers — tab+partition (အပေါ်) နဲ့ log (အောက်) ကို ဆွဲကြီးငယ် လုပ်နိုင်ဖို့
        private SplitContainer splitMain;
        private Panel panelPartition;
        private Panel panelLogArea;
        private TableLayoutPanel logLayout;
        private TableLayoutPanel topLayout;
        private TableLayoutPanel rootLayout;
        private FlowLayoutPanel platformBar;
        private List<Button> platformBtns;
        private FlowLayoutPanel panelLogButtons;
        private Panel panelPartitionHeader;
        private FlowLayoutPanel panelPartBtns;
        private Button btnPartAll, btnPartNone;
        private readonly Dictionary<Control, Color> labelFgBackup = new();
        private readonly HashSet<Control> themePaintWired = new();

        // ADB Controls
        private Button btnAdbInfo;
        private Button btnAdbFrp;   // ADB FRP Reset (USB debugging ဖွင့်ထားတဲ့ ဖုန်း)
        private Button btnAdbFrpRoot;   // ADB FRP Reset (root ရှိတဲ့ ဖုန်း — su လိုတယ်)
        private Button btnXiaomiTempRootFrp; // Xiaomi Temp Root (ADB + su/exploit) — FRP က FRP Reset (ROOT)
        private Button btnAdbBatteryInfo;
        private Button btnAdbScreenshot;
        private Button btnAdbListApps;
        private Button btnAdbInstallApk;
        private Button btnAdbRemoveLock;
        private Button btnAdbFactoryReset;
        private Button btnAdbPartitions; // ROOT partition list + RW remount
        private ComboBox cmbAdbReboot;
        private Button btnAdbRebootExecute;

        // Fastboot Controls
        private Button btnFbInfo;
        private Button btnFbArb;
        private Button btnFbRebootSystem;
        private Button btnFbRebootRecovery;
        private Button btnFbRebootEdl;
        private Button btnFbFlash;
        private Button btnFbBootTemp;

        private Button btnFbErase;

        private Button btnFbSwitchSlot;

        private Panel grpMiFlash;

        private Button btnMiRomFolder;

        private Button btnMiFlashGo;

        private Label lblMiRomPath;

        private CheckBox chkMiEraseFirst;

        private CheckBox chkMiAutoReboot;

        private DataGridView dgvMiParts;

        private List<MiFlashOp> miFlashOps = new();
        private DataGridView dgvPartitions;
        private TextBox txtPartFilter;
        private ContextMenuStrip ctxPartitionMenu;

        // Header: platform + theme status
        private Label lblPlatformStatus;
        private Label lblBattery;
        private Button btnThemeToggle;
        private bool lightTheme = false;
        private readonly HashSet<string> warnedModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Qualcomm (EDL 9008) Controls — edl.py (bkerler EDL) ကို သုံးတယ်
        private Button btnQcReadInfo;
        private Button btnQcSaveGpt;
        private Button btnQcFrpRemove;
        private Button btnQcUserlockReset;
        private Button btnQcFullBackup;
        private Button btnQcEfsBackup;
        private Button btnQcRestoreBackup;
        private Button btnQcEfsRestore;
        private Button btnQcImeiReadEfs;
        private Button btnQcReset;
        // EDL auto-auth session (Unlock Tool style) — COM port တစ်ခုအတွက် တစ်ခါသာ
        private string qcAuthedPort { get => qcLoaderSession.Port; set => qcLoaderSession.Port = value; }
        private bool qcAuthed { get => qcLoaderSession.Authenticated; set => qcLoaderSession.Authenticated = value; }

        // Firehose loader brand/model picker (pmk_qc_loaders.json)
        private ComboBox cmbQcBrand;
        private ComboBox cmbQcModel;
        private Panel grpQcLoaderPicker;
        private Label lblQcLoaderStatus;
        private Dictionary<string, Dictionary<string, string>> qcLoaderDb = new(StringComparer.OrdinalIgnoreCase);
        // display name ("MI 10 (umi) (AuthBypass)") → JSON key ("[AuthBypass] MI 10")
        private Dictionary<string, string> qcModelDisplayToKey = new(StringComparer.OrdinalIgnoreCase);
        // brand → product name → codename (pmk_codenames.json)
        private Dictionary<string, Dictionary<string, string>> qcCodenames = new(StringComparer.OrdinalIgnoreCase);

        // Samsung / Spreadtrum / Hisilicon Controls (adb / fastboot နဲ့)
        private Button btnSamInfo;
        private Button btnSamFrp;
        private Button btnSamKnox;
        private Button btnSamDownload;
        private Button btnSamReboot;
        private Button btnSamSideload;
        private Button btnSamMtpInfo;
        private Button btnSamMtpFrp;
        private Button btnSamMtpFactoryReset;
        private Button btnSamDlInfo;
        private Button btnSamSoftBrick;

        private Button btnSpdInfo;
        private Button btnSpdFrp;
        private Button btnSpdReboot;
        private Button btnSpdFastboot;

        private Button btnHisiInfo;
        private Button btnHisiFrp;
        private Button btnHisiReboot;
        private Button btnHisiFastboot;
        private Button btnHisiOemUnlock;
        private Button btnHisiStartUnlock;
        private Button btnHisiRefreshPorts;
        private Button btnHisiBootloaders;
        private Button btnHisiInstallDrivers;
        private Button btnHisiEnableAdb;
        private Button btnHisiUninstallDrivers;
        private ComboBox cmbKirin;
        private ComboBox cmbHisiPort;
        private HashSet<string> huaweiComPorts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Firmware flash panels (Qualcomm / Samsung / SPD) — MTK panel နဲ့ တူတူပုံစံ
        private TextBox txtQcFirmware, txtSamFirmware, txtSpdFirmware;
        private Button btnQcPickFirmware, btnQcStartFlash, btnSamPickFirmware, btnSamStartFlash;
        private Button btnSamDownloadPit;
        private Button btnSamEfsBackup;
        private Button btnSamEfsRestore;
        private Button btnSpdPickFirmware, btnSpdDirectFlash;
        private Button btnSpdReadGpt, btnSpdReadPart, btnSpdWritePart, btnSpdErasePart;
        private Button btnSpdDiagFrp, btnSpdDiagUserlock;

        // Samsung PIT (binary) ဖတ်ထားတဲ့ entry စာရင်း — .tar ထဲက .img တွေကို partition နာမည်နဲ့
        // တွဲဖို့ (Flash Filename → Partition Name) သုံးတယ်။
        private List<PitEntry> samPitEntries = new List<PitEntry>();
        private CheckBox chkQcAutoReboot;
        private CheckBox chkQcBackupEfsFirst;
        private CheckBox chkQcSkipUserdata;
        private CheckBox chkQcResetFrpAfter;
        private CheckBox chkSamBackupPit;
        private CheckBox chkSamAutoReboot;
        private string qcFirmwareFolder = "";
        private readonly Dictionary<string, long> flashFileSizes = new(StringComparer.OrdinalIgnoreCase);
        // MST style `Flashing [ file.img ] -> [ PART ]... Ok` log အတွက် rawprogram XML က
        // filename → label map (same file တစ်ခုကို ၂ partition သုံးရင် queue အဖြစ် XML order အတိုင်း)
        private readonly Dictionary<string, Queue<string>> flashPartTargets = new(StringComparer.OrdinalIgnoreCase);
        private readonly object flashPartLock = new();
        private string samFirmwareFile = "";
        private string spdFirmwareFile = "";

        // Kirin bootloader unlock (PotatoNV core) — bootloaders folder နဲ့ running task
        private string hisiBootloadersPath = "";
        private CancellationTokenSource hisiCts;
        private bool userAdjustedSplit = false;   // user က splitter ကို လက်နဲ့ ဆွဲထားလား
        private int pendingSplitterDistance = -1; // settings က restore မည့် splitter distance (Load မှ apply)

        // EDL module (edl.py)၊ firehose loader၊ PotatoNV လမ်းကြောင်းများ — pmk_paths.txt မှာ သိမ်းတယ်
        private string edlScriptPath = "";
        private readonly QcLoaderSession qcLoaderSession = new();
        private string edlLoaderPath { get => qcLoaderSession.LoaderPath; set => qcLoaderSession.LoaderPath = value; }
        private string edlSigPath = "";        // pmk_paths.txt line 4 — optional custom SIG file
        private const string edlLegacySlot = "";   // pmk_paths.txt line 3 — အရင် PotatoNV GUI path အတွက် နေရာ (မဖျက်ရ၊ format မပြောင်းရ)

        // MTK/SPD picker ရွေးထားတဲ့ loader/DA/auth/preloader ဖိုင်များ — pmk_paths.txt line 5-8
        private string mtkDaPath = "";
        private string mtkAuthPath = "";
        private string mtkPreloaderPath = "";
        private string spdLoaderPath = "";
        private string _spdSelectedModel = "";   // SPD tab ရဲ့ ရွေးထားတဲ့ model — loader folder တိုက်စစ်ဖို့
        private Control qcFileRow;   // QC LOADER PICKER ထဲက Loader file row (Load တစ်ခါပဲ ထည့်)
        // file picker rows — theme toggle အခါ refresh ဖို့ (row panel → Show action)
        private readonly Dictionary<Panel, Action> fileRowRefresh = new();

        // file row color တွေ — light/dark theme အလိုက်
        private Color RowLabelBg => lightTheme ? Color.FromArgb(216, 223, 237) : Color.FromArgb(30, 34, 41);
        private Color RowFieldBg => lightTheme ? Color.FromArgb(245, 248, 253) : Color.FromArgb(24, 27, 33);
        private Color RowLabelText => lightTheme ? Color.FromArgb(45, 52, 66) : Color.FromArgb(205, 216, 232);
        private Color RowHint => lightTheme ? Color.FromArgb(56, 64, 78) : Color.FromArgb(185, 200, 224);
        private Color RowLoaded => lightTheme ? Color.FromArgb(0, 105, 50) : Color.LightGreen;

        // Kirin bootloader manifest (manifest.xml) ဖတ်ထားတာ
        private class KirinImage
        {
            public string Role;
            public string Path;
            public string Hash;
            public int Address;
            public long Size;
        }

        private class KirinBootloader
        {
            public string Name;
            public List<KirinImage> Images = new List<KirinImage>();
            public override string ToString() { return Name; }
        }

        // Header tooltip (VCOM Mode checkbox ကို ဖျောက်ပြီး — transport ကို auto-detect လုပ်တယ်)
        private ToolTip toolTipMain;

        // State Tracking & Active Process
        private string lastDeviceState = "";
        private bool mediaTekWriteActive;
        private Process activeProcess;
        private bool isDetecting;
        private readonly Dictionary<string, DateTime> lastLogUtc = new();
        private bool isTaskRunning = false;
        private bool flashWorkflowRunning;
        private readonly AsyncLocal<bool> flashWorkflowContext = new AsyncLocal<bool>();

        private volatile bool workflowDidOp = false;
        private bool workflowFailed;
        // parser ကို ဘယ် task ထဲမှာ run ဖြစ်နေလဲ သိအောင် — QC/MTK log wording ခွဲသုံးဖို့
        private string currentTaskTitle = "";

        // MTK/QC op ပြီးရင် Auto reboot checkbox ပေါ်မူတည်ပြီး ဖုန်းကို reboot ပြန်ပို့တယ်
        // (BROM/EDL mode ကနေ Android ပြန်ဝင်အောင်)။
        private async Task MaybeAutoRebootAsync(string platform, string arguments, string serial)
        {
            if (stopRequested) return;
            bool ok = await ExecuteCommandCleanAsync("python", arguments, "Auto Reboot (" + platform + ")", quiet: true, timeoutSec: 180);
            if (ok && !stopRequested) _ = WaitAndroidAfterRebootAsync();
        }
        // Reboot ပြီးရင် device ပြန် boot လာ/ADB ပြန်တက်လာမလား စောင့်ပြီး log ပြတယ် —
        // (UI မ block — devicePollTimer က header label ကို ဆက် update နေမယ်)
        private async Task WaitAndroidAfterRebootAsync()
        {
            try
            {
                Log("[*] Waiting for device to boot back into Android...", Color.Cyan);
                for (int i = 0; i < 60; i++)   // 5s × 60 = 300s (wipe ပြီး first boot ကြာနိုင်)
                {
                    await Task.Delay(5000);
                    if (stopRequested || IsDisposed) return;
                    string adbRes = await ExecuteCommandQuickAsync("adb.exe", "devices", 5000);
                    if (!string.IsNullOrEmpty(adbRes) && adbRes.Contains("\tdevice"))
                    {
                        string model = (await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.product.model", 5000)).Trim();
                        Log("[OK] Device back online: ADB -> " + (string.IsNullOrWhiteSpace(model) ? "Android device" : model), Color.LightGreen);
                        _ = CheckAllDevicesAsync(false);
                        return;
                    }
                    if (!string.IsNullOrEmpty(adbRes) && adbRes.Contains("\tunauthorized"))
                    {
                        Log("[OK] Device back online: ADB unauthorized — allow USB debugging on the phone screen.", Color.Yellow);
                        _ = CheckAllDevicesAsync(false);
                        return;
                    }
                    string fbRes = await ExecuteCommandQuickAsync("fastboot.exe", "devices", 3000);
                    if (!string.IsNullOrEmpty(fbRes) && fbRes.Contains("fastboot"))
                    {
                        // fastboot ရောက်နေရင် Android မဟုတ်သေး — reboot ပို့ပြီး ဆက်စောင့်
                        Log("[i] FASTBOOT detected - sending fastboot reboot to Android...", Color.DeepSkyBlue);
                        await ExecuteCommandQuickAsync("fastboot.exe", "reboot", 15000);
                        continue;
                    }
                }
                Log("[!] Device not back after 300s — check the screen / replug USB.", Color.Orange);
            }
            catch { }
        }

        private async Task RunFlashWorkflowAsync(Func<Task> action, bool autoRebootAfter = true, bool? forceReboot = null)
        {
            if (isTaskRunning || flashWorkflowRunning)
            {
                Log("[!] Another operation is still running.", Color.OrangeRed);
                return;
            }
            string rebootPlatform = tabControl.SelectedTab == tabQc ? "QC" : "";
            bool rebootEnabled = forceReboot ?? (rebootPlatform == "QC" && chkQcAutoReboot.Checked);
            string rebootSerial = "";            workflowFailed = false;
            flashWorkflowRunning = true;
            flashWorkflowContext.Value = true;
            stopRequested = false;
            workflowDidOp = false;

            var inputs = AllControls(this).Where(c => c != btnStopTask &&
                (c is Button || c is ComboBox || c is CheckBox || c is TextBox || c is DataGridView || c is TabControl))
                .Where(c => c.Enabled).ToList();
            foreach (var input in inputs) input.Enabled = false;
            try
            {
                try { await action(); }
                catch (Exception ex) { workflowFailed = true; Log("[FAIL] Operation stopped: " + ex.Message, Color.Red); }

                // command တကယ် run ပြီးမှ reboot args တွက် — QC ရဲ့ edlScriptPath ဖြေရှင်းချက်က op
                // အတွင်း/ပြီးမှ ဖြစ်နိုင်လို့ (flash က edl.py မသုံး — အရင် စတဲ့အချိန်က snapshot ယူထားလို့
                // path အလွတ်ဖြစ်ရင် reboot တိတ်တည်း ပျောက်သွားတဲ့ bug)
                string rebootArguments = rebootPlatform == "QC" ? BuildQcRebootArgs() : "";
                bool qcFirehoseFallback = rebootPlatform == "QC" && rebootArguments.Length == 0;
                if (ReviewSafety.ShouldReboot(autoRebootAfter && rebootEnabled &&
                    (rebootArguments.Length > 0 || qcFirehoseFallback),
                    workflowDidOp, workflowFailed, stopRequested))
                {
                    if (rebootArguments.Length > 0)
                    {
                        await MaybeAutoRebootAsync(rebootPlatform, rebootArguments, rebootSerial);
                    }
                    else
                        await QcFirehoseRebootAsync();   // edl.py မရှိ → fh_loader firehose power reset
                }
            }
            finally
            {
                foreach (var input in inputs) if (!input.IsDisposed) input.Enabled = true;
                flashWorkflowContext.Value = false;
                flashWorkflowRunning = false;
                workflowDidOp = false;
            }
        }
        private volatile bool stopRequested = false;

        // Partition Metadata Storage for Scatter File Generation
        private List<PartitionMeta> loadedPartitions = new List<PartitionMeta>();
        private string detectedStorageKind = "";

        private class PartitionMeta
        {
            public string Name { get; set; }
            public string HumanSize { get; set; }
            public string Offset { get; set; }
            public string LengthHex { get; set; }
        }

        // Samsung PIT entry (binary PIT format — heimdall/Odin စံအတိုင်း)။
        // block unit က 512 bytes (SamsungBlockSize)။
        private class PitEntry
        {
            public string Name = "";
            public string FlashFilename = "";
            public long BlockOffset;
            public long BlockCount;
        }

        private readonly string licEmail;
        private readonly string licPlan;
        private readonly string licExpires;
        private readonly int licDays;
        // client-side license expiry — parse မအောင်ရင် MaxValue (login gate က block ပြီးသားမို့ တားစရာမလို)
        private DateTime licenseExpiresUtc = DateTime.MaxValue;

        public Form1() : this("", "", "", 0) { }

        public Form1(string email, string plan, string expires, int days)
        {
            licEmail = email;
            licPlan = plan;
            licExpires = expires;
            licDays = days;
            if (DateTime.TryParse(expires, null, System.Globalization.DateTimeStyles.RoundtripKind, out var licExpDt))
                licenseExpiresUtc = licExpDt.Kind == DateTimeKind.Local
                    ? licExpDt.ToUniversalTime()
                    : DateTime.SpecifyKind(licExpDt, DateTimeKind.Utc);

            InitializeComponent();
            // PMK icon — exe ထဲ embedded pmk.ico (title bar / taskbar)
            try { var ic = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); if (ic != null) Icon = ic; } catch { }
            try { ShopServices.MigrateSettings(Application.StartupPath); }
            catch (Exception ex) { Debug.WriteLine("Settings migration: " + ex.Message); }
            LoadEdlPaths();
            SetupProfessionalUI();
            BuildShopPanel();
            LoadSettings();       // checkbox state တွေ ပြန်ဖတ် (SetupProfessionalUI ပြီးမှ — control တွေ ဖန်တီးပြီးမှ)
            // saved theme မရှိရင် ApplyTheme() မခေါ်လို့ dialog ကို default (dark) ကို တိုက်ရိုက် sync
            ConfirmDialog.DarkMode = !lightTheme;
            LoadKirinList();       // Kirin bootloader manifest တွေ ဖတ် (HiSilicon tab)
            RefreshHisiPorts();    // testpoint mode COM port list (HUAWEI first)
            _ = CheckAllDevicesAsync(false);
            FitToScreen();         // မျက်နှာပြင် သေးတဲ့ PC ဆိုရင် အချိုးကျ ကျုံ့
            VerifyBundledTools();  // adb/fastboot/heimdall စသည် ရှိ/မရှိ startup check

            // Failed mtkclient runs leave GUID session folders behind (the staged .bin dumps in them once grew
            // this tree to 52 GB). Background so startup never waits on it; only GUID-shaped folders are ever
            // touched, so seccfg-backup / native-da / archives cannot be pruned by construction.
            _ = Task.Run(() =>
            {
                try
                {
                    var (pruned, bytes) = MediaTek.MtkSessionPrune.Prune(ShopServices.DataDirectory);
                    if (pruned > 0)
                        Log("[OK] Pruned " + pruned + " old MediaTek session folder(s) — " +
                            FormatBytesLong(bytes) + " freed.", Color.LightGreen);
                }
                catch { }
            });

            // App ပိတ်ရင် running child process + poll timer ကို ရှင်း
            this.FormClosing += (s, e) =>
            {
                if (mediaTekCancel != null)
                {
                    e.Cancel = true;
                    BtnStopTask_Click(s, e);
                    Log("[!] Waiting for a partition boundary before closing. Close again after the operation ends.", Color.Orange);
                    return;
                }
                SaveSettings();
                try { devicePollTimer?.Stop(); devicePollTimer?.Dispose(); } catch { }
                try { clockTimer?.Stop(); clockTimer?.Dispose(); } catch { }
                // NATIVE DA tab: a held COM port outlives the process and blocks the next bench run
                try { ReleaseNativeDaSession(); } catch { }
                try
                {
                    if (activeProcess != null && !activeProcess.HasExited)
                        activeProcess.Kill(entireProcessTree: true);
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

        private void UpdateClock()
        {
            if (lblClock == null || lblClock.IsDisposed) return;
            var now = DateTime.Now;
            lblClock.Text = now.ToString("HH:mm:ss") + "  " + now.ToString("yyyy-MM-dd ddd");
        }

        private Label MakeLicenseLabel()
        {
            string email = string.IsNullOrEmpty(licEmail) ? "—" : licEmail;
            string plan = string.IsNullOrEmpty(licPlan) ? "" : " · " + licPlan;
            string exp = "";
            if (!string.IsNullOrEmpty(licExpires))
            {
                if (DateTime.TryParse(licExpires, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                    exp = " · exp " + dt.ToLocalTime().ToString("yyyy-MM-dd");
            }
            string days = licDays > 0 ? " · " + licDays + "d left" : "";

            var lbl = new Label
            {
                Text = "👤 " + email + plan + exp + days,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight,
                Padding = new Padding(0, 0, 4, 0),
                AutoEllipsis = true,
                BackColor = Color.Transparent,
                ForeColor = Color.FromArgb(235, 225, 255),
                Font = new Font("Segoe UI", 9.75f, FontStyle.Bold)
            };
            if (toolTipMain != null)
                Tip(lbl,
                    "Logged in: " + licEmail + "\r\nPlan: " + licPlan +
                    "\r\nExpires: " + licExpires + "\r\nDays left: " + licDays);
            return lbl;
        }

        private void VerifyBundledTools()
        {
            string[] tools = { "adb.exe", "fastboot.exe", "heimdall.exe", "fh_loader.exe", "QSaharaServer.exe" };
            foreach (string t in tools)
            {
                try
                {
                    // heimdall.exe က samsung\ ထဲမှာ — root တစ်ခုတည်း မစစ်ဘဲ subfolder ပါ ရှာ
                    // (FindFileInToolFolders → ToolIntegrity.Verify — tamper ရင် throw)
                    if (string.IsNullOrEmpty(FindFileInToolFolders(t)))
                        Log("[!] Bundled tool missing: " + t, Color.Orange);
                }
                catch (InvalidDataException ex)
                {
                    Log("[!] " + ex.Message, Color.OrangeRed);
                }
            }
        }

        // စာသားတစ်ကြောင်း ရိုက်ထည့်ဖို့ ရိုးရှင်းတဲ့ dialog — tool ရဲ့ theme နဲ့
        private string PromptInput(string title, string prompt)
        {
            using (Form f = new Form
            {
                Text = title,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.CenterParent,
                ClientSize = new Size(450, 190),
                BackColor = Color.FromArgb(22, 25, 30),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5f)
            })
            {
                Label lbl = new Label
                {
                    Text = prompt,
                    Location = new Point(16, 16),
                    Size = new Size(418, 60),
                    ForeColor = Color.FromArgb(210, 222, 235)
                };
                TextBox txt = new TextBox
                {
                    Location = new Point(16, 82),
                    Size = new Size(418, 28),
                    BackColor = Color.FromArgb(38, 42, 50),
                    ForeColor = Color.White,
                    BorderStyle = BorderStyle.FixedSingle,
                    Font = new Font("Consolas", 10.5f)
                };
                Button ok = new Button
                {
                    Text = "OK",
                    Location = new Point(252, 128),
                    Size = new Size(88, 34),
                    BackColor = Color.FromArgb(86, 145, 250),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    DialogResult = DialogResult.OK
                };
                ok.FlatAppearance.BorderSize = 0;
                Button cancel = new Button
                {
                    Text = "Cancel",
                    Location = new Point(346, 128),
                    Size = new Size(88, 34),
                    BackColor = Color.FromArgb(60, 66, 76),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    DialogResult = DialogResult.Cancel
                };
                cancel.FlatAppearance.BorderSize = 0;

                f.AcceptButton = ok;
                f.CancelButton = cancel;
                f.Controls.AddRange(new Control[] { lbl, txt, ok, cancel });
                txt.Focus();

                return f.ShowDialog(this) == DialogResult.OK ? txt.Text.Trim() : "";
            }
        }

    }
}
