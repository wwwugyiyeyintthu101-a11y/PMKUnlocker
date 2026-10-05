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
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;

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
        private ProgressBar pbarOperation;
        private Label lblProgressStatus;
        private Label lblSpeedBadge;

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

        // MTK Controls (Updated with 4 Features)
        private Button btnMtkReadBrom;
        private Button btnMtkNvBackup;
        private Button btnMtkNvErase;
        private Button btnMtkNvRestore;
        private Button btnMtkSafeBackup;
        private Button btnMtkMemTest;
        private Button btnMtkFullDump;
        private Button btnMtkNormalDump;
        private Button btnMtkUnlockBL;
        private Button btnMtkRelockBL;
        private Button btnMtkDeviceModel;
        private Button btnMtkUserlockReset;
        // Scatter-based firmware flash panel (SP Flash ပုံစံ)
        private Button btnMtkPickScatter;
        private Button btnMtkFlashScatter;
        private TextBox txtScatterFolder;
        private CheckBox chkMtkAutoReboot;
        // Flash Option checkboxes (MobileSea ပုံစံ)
        private CheckBox chkMtkBackupNvFirst;
        private CheckBox chkMtkResetFrpAfter;
        private CheckBox chkMtkSkipUserdata;
        private string mtkFirmwareFolder = "";
        private Button btnMtkFrpRemove;
        private Button btnMtkOrangeStateFix;
        private Button btnMtkDmFix;
        private Button btnMtkUndoVbmeta;
        private CheckBox chkMtkBackupVbmetaFirst;
        private DataGridView dgvPartitions;
        private TextBox txtPartFilter;
        private ContextMenuStrip ctxPartitionMenu;

        // Header: platform + theme status
        private Label lblPlatformStatus;
        private Label lblBattery;
        private Button btnThemeToggle;
        private bool lightTheme = false;
        private string lastVbmetaBackupPath = "";
        private readonly HashSet<string> warnedModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Qualcomm (EDL 9008) Controls — edl.py (bkerler EDL) ကို သုံးတယ်
        private Button btnQcReadInfo;
        private Button btnQcSaveGpt;
        private Button btnQcFrpRemove;
        private Button btnQcUserlockReset;
        private Button btnQcFullBackup;
        private Button btnQcEfsBackup;
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
        // MTK op ပြီးနောက် auto reboot ရဲ့ တကယ့်ရလဒ် — pmk_mtk_op.py က ပို့တဲ့ marker ကနေ ဖြည့်တယ်
        // (exit code 0 က operation အောင်တာသာ ပြတယ်၊ ဖုန်း တက်တာကို မပြနိုင်ဘူး)။
        private string lastMtkBootState = "";
        private bool isDetecting = false;
        private Process activeProcess = null;
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
            string previousBootMode = Environment.GetEnvironmentVariable("PMK_MTK_BOOTMODE");
            string previousSerial = Environment.GetEnvironmentVariable("PMK_MTK_SERIAL");
            try
            {
                if (platform == "MTK")
                {
                    lastMtkBootState = "";
                    Environment.SetEnvironmentVariable("PMK_MTK_BOOTMODE", "1");
                    Environment.SetEnvironmentVariable("PMK_MTK_SERIAL", serial);
                }
                Log("[*] Auto reboot after successful " + platform + " operation.", Color.Orange);
                // timeoutSec: edl.py reset က device reboot ပြီးမှ port မှာ hang နေတတ်တယ် —
                // 60s ကျော်ရင် kill ပြီး device drop = reboot complete အဖြစ် သတ်မှတ် (UI မချိတ်ကျစေရ)
                bool rebooted = await ExecuteCommandCleanAsync("python", arguments, "Auto Reboot (" + platform + ")",
                    quiet: true, timeoutSec: 60, timeoutMeansSuccess: true);
                // reboot ပြီးရင် device ပြန် boot/ADB ပြန်တက်လာမလား background မှာစောင့်ပြီး log ပြ
                if (rebooted && !stopRequested) _ = WaitAndroidAfterRebootAsync();
            }
            finally
            {
                if (platform == "MTK")
                {
                    Environment.SetEnvironmentVariable("PMK_MTK_BOOTMODE", previousBootMode);
                    Environment.SetEnvironmentVariable("PMK_MTK_SERIAL", previousSerial);
                }
            }
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
                        Log("[OK] Device back online: ADB unauthorized — ဖုန်း screen မှာ USB debugging ခွင့်ပြုပါ။", Color.Yellow);
                        _ = CheckAllDevicesAsync(false);
                        return;
                    }
                    string fbRes = await ExecuteCommandQuickAsync("fastboot.exe", "devices", 3000);
                    if (!string.IsNullOrEmpty(fbRes) && fbRes.Contains("fastboot"))
                    {
                        Log("[OK] Device back online: FASTBOOT", Color.DeepSkyBlue);
                        _ = CheckAllDevicesAsync(false);
                        return;
                    }
                }
                Log("[!] Device not back after 300s — screen ပေါ်ကြည့်ပါ / USB ပြန်ဆွဲပါ။", Color.Orange);
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
            string rebootPlatform = tabControl.SelectedTab == tabMtk ? "MTK" : tabControl.SelectedTab == tabQc ? "QC" : "";
            // forceReboot = op တစ်ခုအတွက် checkbox ကို ကျော်ပြီး reboot အတိအကျ သတ်မှတ် (ဥပမာ QC Userlock Reset)
            bool rebootEnabled = forceReboot ?? (rebootPlatform == "MTK" ? chkMtkAutoReboot.Checked : rebootPlatform == "QC" && chkQcAutoReboot.Checked);
            string rebootArguments = rebootPlatform == "MTK" ? BuildMtkOpArgs("reset") :
                rebootPlatform == "QC" && !string.IsNullOrEmpty(edlScriptPath) ? BuildEdlArgs("reset --resetmode=reset") : "";
            string rebootSerial = Environment.GetEnvironmentVariable("PMK_MTK_SERIAL");
            if (string.IsNullOrWhiteSpace(rebootSerial)) rebootSerial = string.IsNullOrWhiteSpace(cachedMtkComPort) ? "off" : cachedMtkComPort;
            workflowFailed = false;
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

                // command တကယ် run ပြီးမှ reboot (folder dialog cancel ဆို reboot မလုပ်)
                if (ReviewSafety.ShouldReboot(autoRebootAfter && rebootEnabled && rebootArguments.Length > 0,
                    workflowDidOp, workflowFailed, stopRequested))
                    await MaybeAutoRebootAsync(rebootPlatform, rebootArguments, rebootSerial);
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
        private string detectedCpuPlatform = "";
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
            LoadKirinList();       // Kirin bootloader manifest တွေ ဖတ် (HiSilicon tab)
            RefreshHisiPorts();    // testpoint mode COM port list (HUAWEI first)
            _ = CheckAllDevicesAsync(false);
            FitToScreen();         // မျက်နှာပြင် သေးတဲ့ PC ဆိုရင် အချိုးကျ ကျုံ့
            VerifyBundledTools();  // adb/fastboot/heimdall စသည် ရှိ/မရှိ startup check

            // App ပိတ်ရင် running child process + poll timer ကို ရှင်း
            this.FormClosing += (s, e) =>
            {
                SaveSettings();
                try { devicePollTimer?.Stop(); devicePollTimer?.Dispose(); } catch { }
                try { clockTimer?.Stop(); clockTimer?.Dispose(); } catch { }
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
                Padding = new Padding(0, 0, 12, 0),
                AutoEllipsis = true,
                BackColor = Color.Transparent,
                ForeColor = Color.FromArgb(235, 225, 255),
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
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
            if (!File.Exists(Path.Combine(Application.StartupPath, "pmk_mtk_op.py")))
                Log("[!] pmk_mtk_op.py missing - MTK operations will fail.", Color.Orange);
            if (!File.Exists(Path.Combine(Application.StartupPath, "pmk_devices.json")))
                Log("[!] pmk_devices.json missing - device pickers will be empty.", Color.Orange);
            if (!File.Exists(Path.Combine(Application.StartupPath, "pmk_models.json")))
                Log("[!] pmk_models.json missing - model list will be empty.", Color.Orange);
        }

        private void SetupProfessionalUI()
        {
            this.Text = "PMK Mobile Tool V" + UpdateChecker.CurrentVersion;
            this.Size = new Size(1400, 900);
            // PC တိုင်းနဲ့ အဆင်ပြေအောင် — window ကို ဆွဲကြီး/ကျုံ့ လို့မရစေရ၊ maximize လည်း ပိတ်
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.MinimizeBox = true;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(18, 20, 24);
            this.ForeColor = Color.White;

            // ================= 1. HEADER BAR =================
            panelTopHeader = new Panel
            {
                Location = new Point(0, 0),
                // 72px က row2 စာ အောက်ဖြတ် — 80px (row1 @12 h34, row2 @50 h26 → 76 < 80)
                Size = new Size(1020, 80),
                BackColor = Color.FromArgb(26, 29, 35),
                Dock = DockStyle.Top
            };

            lblPort = new Label
            {
                Text = "COM Port:",
                Location = new Point(15, 18),
                AutoSize = true,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(86, 145, 250)
            };

            cmbPorts = new ComboBox
            {
                Location = new Point(95, 15),
                Size = new Size(180, 26),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI", 9.5f),
                BackColor = Color.FromArgb(38, 42, 50),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };

            btnRefreshPorts = Create3DButton("🔄 Scan Port", 285, 12, 100, 34, ButtonTheme.Cyan);
            btnRefreshPorts.Click += async (s, e) => { await CheckAllDevicesAsync(true); };

            btnOpenDeviceManager = Create3DButton("⚙ Dev Manager", 395, 12, 130, 34, ButtonTheme.Purple);
            btnOpenDeviceManager.Click += (s, e) => { Process.Start(new ProcessStartInfo { FileName = "devmgmt.msc", UseShellExecute = true }); };

            btnDrivers = Create3DButton("🛠 Drivers", 530, 12, 100, 34, ButtonTheme.Cyan);
            btnDrivers.Click += (s, e) => ShowDriverHelper();

            // VCOM Mode checkbox ကို ဖျောက်လိုက်တယ် — transport (USB / serial-VCOM) ကို tool က
            // ကိုယ်တိုင် auto-detect လုပ်တယ် (MTK COM port ရှိရင် serial၊ မရှိရင် USB)။
            // အတင်းရွေးချင်ရင် env: PMK_MTK_SERIAL=off (USB) သို့မဟုတ် PMK_MTK_SERIAL=COM5။
            toolTipMain = new ToolTip();
            Tip(btnOpenDeviceManager,
                "Windows Device Manager ဖွင့်တယ်။\r\n" +
                "MTK transport (USB / serial-VCOM) ကို tool က auto-detect လုပ်တယ် —\r\n" +
                "MediaTek device ရဲ့ COM port ရှိရင် serial၊ မရှိရင် USB (libusb/WinUSB) သုံးတယ်။\r\n" +
                "အတင်းရွေးချင်ရင် env: PMK_MTK_SERIAL=off | COM5");
            Tip(btnRefreshPorts, "Rescan serial COM ports (MTK Preloader / VCOM / SPD / HiSilicon).");
            Tip(btnDrivers, "Scan known USB VIDs and install INF driver folders.");

            lblDeviceModeStatus = new Label
            {
                // Header 80px — row2 y=50 h=26 (50+26=76 < 80) → စာ/emoji အပြည့်ပေါ်
                Text = "⚪ Disconnected",
                Location = new Point(330, 50),
                AutoSize = false,
                Size = new Size(445, 26),
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.LightGray,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
            };

            lblPlatformStatus = new Label
            {
                Text = "Platform: ADB / FB",
                Location = new Point(15, 53),
                AutoSize = true,
                ForeColor = Color.FromArgb(139, 92, 246),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            };

            lblBattery = new Label
            {
                Text = "🔋 —",
                Location = new Point(200, 53),
                AutoSize = true,
                ForeColor = Color.FromArgb(180, 195, 215),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            };
            if (toolTipMain != null)
                Tip(lblBattery, "ADB device battery level (auto-refresh with device poll)");

            btnThemeToggle = Create3DButton("🌙 Dark", 660, 12, 90, 34, ButtonTheme.Purple);
            btnThemeToggle.Click += (s, e) => ToggleTheme();
            if (toolTipMain != null)
                Tip(btnThemeToggle, "Switch between Dark and Light theme.");

            // Login email + ကျန်ရက် + clock — header ညာဘက် stack (overlap မဖြစ်အောင်)
            lblLicenseInfo = MakeLicenseLabel();

            lblClock = new Label
            {
                Text = "",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight,
                Padding = new Padding(0, 4, 12, 0),
                ForeColor = Color.FromArgb(0, 220, 160),
                Font = new Font("Segoe UI", 10f, FontStyle.Bold)
            };
            if (toolTipMain != null)
                Tip(lblClock, "Current date/time (local)");
            clockTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            clockTimer.Tick += (_, _) => UpdateClock();
            clockTimer.Start();
            UpdateClock();

            panelLicenseBadge = new Panel
            {
                Dock = DockStyle.Top,
                Height = 28,
                BackColor = Color.FromArgb(44, 36, 68)
            };
            panelLicenseBadge.Paint += (_, e) =>
            {
                var r = e.ClipRectangle;
                if (r.Width < 2 || r.Height < 2) return;
                using (var br = new LinearGradientBrush(r,
                    Color.FromArgb(58, 46, 96), Color.FromArgb(34, 28, 54),
                    LinearGradientMode.Vertical))
                    e.Graphics.FillRectangle(br, r);
                using (var pen = new Pen(Color.FromArgb(168, 120, 245)))
                    e.Graphics.DrawRectangle(pen, 0, 0, r.Width - 1, r.Height - 1);
            };
            panelLicenseBadge.Controls.Add(lblLicenseInfo);

            var panelRightInfo = new Panel
            {
                Dock = DockStyle.Right,
                Width = 500,
                BackColor = Color.Transparent,
                Padding = new Padding(0, 10, 12, 6)
            };
            // Dock order: last added = outermost/top → license above clock
            panelRightInfo.Controls.Add(lblClock);          // Fill (below)
            panelRightInfo.Controls.Add(panelLicenseBadge); // Top
            panelTopHeader.Controls.Add(panelRightInfo);

            panelTopHeader.Controls.AddRange(new Control[] { lblPort, cmbPorts, btnRefreshPorts, btnOpenDeviceManager, btnDrivers, lblDeviceModeStatus, lblPlatformStatus, lblBattery, btnThemeToggle });

            // ================= 2. TAB CONTROL =================
            // အပေါ်ဘက် split panel ထဲမှာ ဖြည့်ထားတယ် (window resize လုပ်ရင် အလိုအလျောက် လိုက်ပြောင်း)
            tabControl = new TabControl
            {
                Dock = DockStyle.Fill,
                DrawMode = TabDrawMode.OwnerDrawFixed,
                ItemSize = new Size(118, 30),
                SizeMode = TabSizeMode.Fixed
            };
            tabControl.DrawItem += TabControl_DrawItem;

            // MobileSea ပုံစံ — OS tab header တွေ ဖျောက်ပြီး ကိုယ်ပိုင် Platform bar သုံး
            tabControl.Appearance = TabAppearance.FlatButtons;
            tabControl.ItemSize = new Size(0, 1);
            tabControl.SizeMode = TabSizeMode.Fixed;

            tabAdb = new TabPage("ADB / FB") { BackColor = Color.FromArgb(28, 31, 38) };
            tabMtk = new TabPage("MediaTek") { BackColor = Color.FromArgb(28, 31, 38) };
            tabQc = new TabPage("Qualcomm") { BackColor = Color.FromArgb(28, 31, 38) };
            tabSamsung = new TabPage("Samsung") { BackColor = Color.FromArgb(28, 31, 38) };
            tabSpd = new TabPage("Spreadtrum") { BackColor = Color.FromArgb(28, 31, 38) };
            tabHisi = new TabPage("HiSilicon") { BackColor = Color.FromArgb(28, 31, 38) };

            tabControl.TabPages.AddRange(new TabPage[] { tabAdb, tabMtk, tabQc, tabSamsung, tabSpd, tabHisi });

            // --- 1. ADB TAB ---
            btnAdbInfo = Create3DButton("🔍 Read Full Info", UiX(0), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Cyan);
            btnAdbInfo.Click += BtnAdbInfo_Click;

            btnAdbBatteryInfo = Create3DButton("🔋 Battery Health", UiX(1), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Green);
            btnAdbBatteryInfo.Click += BtnAdbBatteryInfo_Click;

            btnAdbScreenshot = Create3DButton("📸 Screenshot", UiX(2), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Purple);
            btnAdbScreenshot.Click += BtnAdbScreenshot_Click;

            btnAdbListApps = Create3DButton("📦 3rd-Party Apps", UiX(3), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Purple);
            btnAdbListApps.Click += BtnAdbListApps_Click;

            btnAdbInstallApk = Create3DButton("📥 Install APK", UiX(4), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Green);
            btnAdbInstallApk.Click += BtnAdbInstallApk_Click;

            // ROOT partition list + selected partition RW remount (ADB tab)
            btnAdbPartitions = Create3DButton("📂 Partitions (ROOT)", 0, 0, UiBtnW, UiBtnH, ButtonTheme.Cyan);
            btnAdbPartitions.Click += BtnAdbPartitions_Click;
            if (toolTipMain != null)
                Tip(btnAdbPartitions,
                    "Root ရှိရင် /dev/block/by-name partition list ပြမယ်။\r\n" +
                    "ရွေးထားတဲ့ partition ကို mount -o remount,rw နဲ့ Read-Write လုပ်နိုင်တယ်။");

            Label lblAdbReboot = new Label { Text = "Reboot Operations:", Location = new Point(UiX(0), UiY(1) + 10), AutoSize = true, ForeColor = Color.LightSteelBlue, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
            cmbAdbReboot = new ComboBox
            {
                Location = new Point(UiX(0) + 130, UiY(1) + 4),
                Size = new Size(155, 28),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(38, 42, 50),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.5f)
            };
            cmbAdbReboot.Items.AddRange(new object[] { "Reboot System", "Reboot Recovery", "Reboot Bootloader", "Reboot EDL (Qualcomm)", "Reboot Safe Mode", "Power Off" });
            cmbAdbReboot.SelectedIndex = 0;

            btnAdbRebootExecute = Create3DButton("⚡ Execute Reboot", UiX(0) + 340, UiY(1), UiBtnW, UiBtnH, ButtonTheme.Orange);
            btnAdbRebootExecute.Click += BtnAdbRebootExecute_Click;


// --- 2. FASTBOOT TAB ---
            btnFbInfo = Create3DButton("🔍 Read Fastboot", UiX(0), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Cyan);
            btnFbInfo.Click += BtnFbInfo_Click;

            btnFbArb = Create3DButton("🛡 Check ARB", UiX(1), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Purple);
            btnFbArb.Click += async (s, e) =>
            {
                if (!await FastbootReadyAsync("Fastboot ARB")) return;
                await ShowFastbootVarsAsync("getvar anti", "Fastboot ARB");
            };

            btnFbFlash = Create3DButton("⚡ Flash .img", UiX(2), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Green);
            btnFbFlash.Click += BtnFbFlash_Click;

            btnFbBootTemp = Create3DButton("🚀 Boot Temp", UiX(3), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Purple);
            btnFbBootTemp.Click += BtnFbBootTemp_Click;

            btnFbRebootSystem = Create3DButton("🔄 Reboot System", UiX(0), UiY(1), UiBtnW, UiBtnH, ButtonTheme.Green);
            btnFbRebootSystem.Click += async (s, e) => { await ExecuteCommandCleanAsync("fastboot.exe", "reboot", "Reboot System"); };

            btnFbRebootRecovery = Create3DButton("🔄 Reboot Recovery", UiX(1), UiY(1), UiBtnW, UiBtnH, ButtonTheme.Orange);
            btnFbRebootRecovery.Click += async (s, e) => { await ExecuteCommandCleanAsync("fastboot.exe", "reboot recovery", "Reboot Recovery"); };

            btnFbRebootEdl = Create3DButton("⚡ Reboot EDL", UiX(2), UiY(1), UiBtnW, UiBtnH, ButtonTheme.Orange);
            btnFbRebootEdl.Click += async (s, e) => { await ExecuteCommandCleanAsync("fastboot.exe", "oem edl", "Reboot EDL"); };

            btnFbErase = Create3DButton("🗑 Erase Userdata", UiX(3), UiY(1), UiBtnW, UiBtnH, ButtonTheme.Red);
            btnFbErase.Click += BtnFbErase_Click;

            // ROOT ရှိတဲ့ ဖုန်းအတွက် FRP Reset — su နဲ့ accounts/policies ဖျက်ပြီး frp partition ကို သုညဖြည့်
            btnAdbFrpRoot = Create3DButton("🔓 FRP Reset (ROOT)", 0, 0, UiBtnW, UiBtnH, ButtonTheme.Red);
            btnAdbFrpRoot.Click += async (s3, e3) =>
            {
                string dv = await ExecuteCommandQuickAsync("adb.exe", "devices");
                if (string.IsNullOrWhiteSpace(dv) || !dv.Contains("\tdevice"))
                {
                    MessageBox.Show("ADB device not found." + Environment.NewLine + Environment.NewLine
                                    + "- Enable Developer options > USB debugging on the phone" + Environment.NewLine
                                    + "- Connect the cable and allow the RSA prompt (Allow USB debugging)",
                        "ROOT FRP Reset", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                string id = (await ProbeSuAsync()).Trim();
                if (!id.Contains("uid=0"))
                {
                    MessageBox.Show("Root (su) မရပါ။" + Environment.NewLine + Environment.NewLine
                                    + "• ဒီဖုန်းမှာ root မရှိပါ (သို့) ဖုန်း screen က su prompt ကို Allow မလုပ်ရသေးပါ" + Environment.NewLine
                                    + "• root မရှိရင် 🔓 FRP Reset (no root) ခလုတ်ကို သုံးပါ",
                        "ROOT FRP Reset", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    Log("[!] ROOT FRP: su not available (" + (string.IsNullOrWhiteSpace(id) ? "no output" : id) + ")", Color.OrangeRed);
                    return;
                }

                if (MessageBox.Show(
                        "ROOT FRP Reset (su)" + Environment.NewLine + Environment.NewLine +
                        "• /data/system/**/accounts*.db နဲ့ device_policies.xml ဖျက်" + Environment.NewLine +
                        "• provisioning / setup_complete သတ်မှတ်" + Environment.NewLine +
                        "• /dev/block/by-name/frp ရှိရင် သုည ဖြည့် (FRP partition erase)" + Environment.NewLine + Environment.NewLine +
                        "• ဖုန်းပိုင်ရှင် ခွင့်ပြုချက် ရှိမှ လုပ်ပါ" + Environment.NewLine + Environment.NewLine +
                        "ဆက်လုပ်မလား?",
                        "Confirm ROOT FRP Reset", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;

                Log("========================================================", Color.FromArgb(0, 180, 255));
                Log("   ROOT FRP RESET (su)", Color.White);
                Log("========================================================", Color.FromArgb(0, 180, 255));

                bool ok = await ExecuteCommandCleanAsync("adb.exe",
                    "shell su -c \"rm -f /data/system/accounts.db /data/system/users/0/accounts.db /data/system/users/0/accounts_ce.db /data/system/users/0/accounts_de.db\"",
                    "ROOT FRP - accounts.db", false, true);

                await ExecuteCommandCleanAsync("adb.exe",
                    "shell su -c \"rm -f /data/system/device_policies.xml /data/system/users/0/device_policies.xml\"",
                    "ROOT FRP - device_policies", false, true);

                await ExecuteCommandQuickAsync("adb.exe", "shell pm clear com.google.android.gms");
                await ExecuteCommandQuickAsync("adb.exe", "shell pm clear com.google.android.setupwizard");
                await ExecuteCommandQuickAsync("adb.exe", "shell settings put secure user_setup_complete 1");
                await ExecuteCommandQuickAsync("adb.exe", "shell settings put global device_provisioned 1");
                Log("[OK] accounts/policies cleared + provisioning set", Color.LightGreen);

                // FRP partition (ရှိရင်သာ) — root FRP ရဲ့ အပြီးသတ် လမ်း
                string frpDev = (await ExecuteCommandQuickAsync("adb.exe", "shell su -c \"ls /dev/block/by-name/frp\"")).Trim();
                if (!string.IsNullOrWhiteSpace(frpDev) && !frpDev.ToLowerInvariant().Contains("no such") && !frpDev.ToLowerInvariant().Contains("not found"))
                {
                    // Query real partition size first — fixed 1MB fails on 512KB FRP (No space left)
                    long frpBytes = 0;
                    string sizeOut = (await ExecuteCommandQuickAsync("adb.exe", "shell su -c \"blockdev --getsize64 /dev/block/by-name/frp\"")).Trim();
                    long.TryParse(sizeOut, out frpBytes);
                    if (frpBytes <= 0)
                    {
                        string secOut = (await ExecuteCommandQuickAsync("adb.exe", "shell su -c \"cat /sys/class/block/$(basename $(readlink -f /dev/block/by-name/frp))/size\"")).Trim();
                        long secs = 0;
                        long.TryParse(secOut, out secs);
                        if (secs > 0) frpBytes = secs * 512;
                    }
                    if (frpBytes <= 0) frpBytes = 524288; // observed default on amethyst
                    long ddBlocks = frpBytes / 4096;
                    if (ddBlocks < 1) ddBlocks = 1;
                    Log("[*] FRP partition: " + frpDev + " → zeroing (" + frpBytes + " bytes) ...", Color.Orange);
                    await ExecuteCommandCleanAsync("adb.exe",
                        "shell su -c \"dd if=/dev/zero of=/dev/block/by-name/frp bs=4096 count=" + ddBlocks + "\"",
                        "ROOT FRP - frp partition", false, true);
                }
                else
                {
                    Log("[i] /dev/block/by-name/frp not found - skipping partition erase", Color.Gray);
                }

                if (ok) Log("[OK] ROOT FRP reset done - press Reboot System and verify.", Color.LightGreen);
                else Log("[!] One of the steps failed - check the log above.", Color.OrangeRed);
            };

            // ADB + Fastboot ပေါင်း tab — ခလုတ်အားလုံး ဖန်တီးပြီးမှ group တွေ တည်ဆောက်တယ်
            // ADB FRP — USB debugging ရနိုင်တဲ့ ဘယ်ဖုန်းမဆို (brand မရွေး)
            btnAdbFrp = Create3DButton("🔓 FRP Reset", 0, 0, UiBtnW, UiBtnH, ButtonTheme.Red);
            btnAdbFrp.Click += async (s2, e2) =>
            {
                string dv = await ExecuteCommandQuickAsync("adb.exe", "devices");
                if (string.IsNullOrWhiteSpace(dv) || !dv.Contains("	device"))
                {
                    MessageBox.Show("ADB device not found." + Environment.NewLine + Environment.NewLine
                                    + "- Enable Developer options > USB debugging on the phone" + Environment.NewLine
                                    + "- Connect the cable and allow the RSA prompt (Allow USB debugging)" + Environment.NewLine
                                    + "- Keep the phone screen on",
                        "ADB FRP Reset", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                await RunAdbFrpResetAsync("ADB device (any brand)");
            };

            Panel grpAdbInfo = CreateGroupPanel("ADB · INFO", UiX(0), UiY(0), 2, btnAdbInfo, btnAdbBatteryInfo, btnAdbPartitions);
            Panel grpAdbApps = CreateGroupPanel("ADB · APPS", UiX(0) + 360, UiY(0), 3, btnAdbListApps, btnAdbInstallApk, btnAdbScreenshot);
            // FRP Reset ကို "Install APK" ရဲ့ အောက်မှာ ထည့်တယ် (APPS group ရဲ့ ဒုတိယ row)
            grpAdbApps.Height = 115;                                  // row ၂ ခု (Install APK အောက် တစ်တန်း)
            grpAdbApps.Controls.Add(btnAdbFrp);
            btnAdbFrp.Location = new Point(10 + UiColStep, 26 + UiRowStep);            // Install APK အောက်
            grpAdbApps.Controls.Add(btnAdbFrpRoot);
            btnAdbFrpRoot.Location = new Point(10 + 2 * UiColStep, 26 + UiRowStep);    // Screenshot အောက်

            // Pattern/PIN remove + Factory Reset — REBOOT ဘေး (tab ညာဘက်အဆုံးထိ cut မဖြစ်အောင်)
            btnAdbRemoveLock = Create3DButton("🔑 Remove Lock", 0, 0, UiBtnW, UiBtnH, ButtonTheme.Orange);
            btnAdbRemoveLock.Click += BtnAdbRemoveLock_Click;
            btnAdbFactoryReset = Create3DButton("🗑 Factory Reset", 0, 0, UiBtnW, UiBtnH, ButtonTheme.Red);
            btnAdbFactoryReset.Click += BtnAdbFactoryReset_Click;

            // Xiaomi Temp Root — ADB → su / kernel exploit (ART-style) temp root ရယူပြီး ရပ်
            // LOCK group 3-col က tab ညာဘက် cut ဖြစ် — APPS row2 col0 (FRP ဘေး) ထဲ ထည့်
            btnXiaomiTempRootFrp = Create3DButton("📱 Xiaomi Temp Root", 0, 0, UiBtnW, UiBtnH, ButtonTheme.Purple);
            btnXiaomiTempRootFrp.Click += BtnXiaomiTempRootFrp_Click;
            Tip(btnXiaomiTempRootFrp,
                "ADB → su temp root / kernel exploit (exploits\\manifest.json) ရယူပြီး ရပ်မယ် — FRP ကို FRP Reset (ROOT) နဲ့ ဆက်မယ်။");
            grpAdbApps.Controls.Add(btnXiaomiTempRootFrp);
            btnXiaomiTempRootFrp.Location = new Point(10, 26 + UiRowStep);             // FRP Reset ဘေး (col0 row2)

            Panel grpAdbLock = CreateGroupPanel("ADB · LOCK / RESET", 540, UiY(3), 2,
                btnAdbRemoveLock, btnAdbFactoryReset);

            Panel grpFbInfo = CreateGroupPanel("FASTBOOT · INFO & FLASH", UiX(0), UiY(5), 4, btnFbInfo, btnFbArb, btnFbFlash, btnFbBootTemp);
            Panel grpFbOps = CreateGroupPanel("FASTBOOT · UNLOCK & REBOOT", UiX(0), UiY(7), 4, btnFbErase, btnFbRebootSystem, btnFbRebootRecovery, btnFbRebootEdl);
            Panel grpAdbReboot = CreateGroupPanel("ADB · REBOOT", UiX(0), UiY(3), 3, lblAdbReboot, cmbAdbReboot, btnAdbRebootExecute);
            tabAdb.Controls.AddRange(new Control[] { grpAdbInfo, grpAdbApps, grpAdbLock, grpAdbReboot, grpFbInfo, grpFbOps });


            
// --- 3. MTK TAB (3-COLUMN BUTTON LAYOUT) ---
            // Column 1
            btnMtkReadBrom = Create3DButton("🔍 Read Info / GPT", UiX(0), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Cyan);
            btnMtkReadBrom.Click += BtnMtkReadBrom_Click;
            if (toolTipMain != null)
                Tip(btnMtkReadBrom, "Read device info + GPT partition map via mtk.exe printgpt.");

            // Unlock Tool ပုံစံ — NV Backup / NV Erase / NV Restore
            btnMtkNvBackup = Create3DButton("💾 NV Backup", UiX(1), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Purple);
            btnMtkNvBackup.Click += BtnMtkNvBackup_Click;

            btnMtkNvErase = Create3DButton("🗑 NV Erase", UiX(2), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Red);
            btnMtkNvErase.Click += BtnMtkNvErase_Click;

            btnMtkNvRestore = Create3DButton("↩ NV Restore", UiX(3), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Green);
            btnMtkNvRestore.Click += BtnMtkNvRestore_Click;
            if (toolTipMain != null)
            {
                Tip(btnMtkNvBackup,
                    "Backup: nvram, nvdata, nvcfg, proinfo\r\n" +
                    "GPT size verify + per-file MB log.");
                Tip(btnMtkNvErase,
                    "Format nvram + nvdata only (IMEI/baseband will be lost — backup first).");
                Tip(btnMtkNvRestore,
                    "Restore NV backup folder: writes every known NV .bin found\r\n" +
                    "(nvram/nvdata/nvcfg/proinfo/persist/seccfg/protect*/preloader).");
            }

            btnMtkMemTest = Create3DButton("🩺 Storage Health", UiX(2), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Cyan);
            btnMtkMemTest.Click += BtnMtkMemTest_Click;

            btnMtkDeviceModel = Create3DButton("🏷 Device Model", UiX(0), UiY(3), UiBtnW, UiBtnH, ButtonTheme.Purple);
            btnMtkDeviceModel.Click += BtnMtkDeviceModel_Click;

            // Column 2
            btnMtkFullDump = Create3DButton("📦 Full ROM Dump", UiX(0), UiY(1), UiBtnW, UiBtnH, ButtonTheme.Orange);
            btnMtkFullDump.Click += BtnMtkFullDump_Click;
            if (toolTipMain != null)
                Tip(btnMtkFullDump, "Dump ALL partitions (mtk.exe rl) + auto scatter — needs lots of disk space.");

            btnMtkNormalDump = Create3DButton("📁 Normal ROM Dump", UiX(1), UiY(1), UiBtnW, UiBtnH, ButtonTheme.Green);
            btnMtkNormalDump.Click += BtnMtkNormalDump_Click;
            if (toolTipMain != null)
                Tip(btnMtkNormalDump, "Dump essential partitions only (boot, recovery, vbmeta, dtbo, super…).");

            // Safe Bundle — tab ညာဘက် overflow မဖြစ်အောင် BACKUP(3col) ထဲ မထည့်ဘဲ UNLOCK ရဲ့ နောက်ဆုံး cell မှာ
            btnMtkSafeBackup = Create3DButton("🛡 Safe Bundle", UiX(3), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Green);
            btnMtkSafeBackup.Click += BtnMtkSafeBackup_Click;
            if (toolTipMain != null)
                Tip(btnMtkSafeBackup,
                    "One-click critical backup: nvram, nvdata, nvcfg, proinfo, persist, seccfg,\r\n" +
                    "boot, vbmeta, vbmeta_system, vbmeta_vendor — GPT size verify + folder open.");

            btnMtkUnlockBL = Create3DButton("🔓 Unlock Bootloader", UiX(2), UiY(1), UiBtnW, UiBtnH, ButtonTheme.Orange);
            btnMtkUnlockBL.Click += BtnMtkUnlockBL_Click;
            if (toolTipMain != null)
                Tip(btnMtkUnlockBL, "da seccfg unlock — wipes userdata. Auto reboot follows the Auto reboot checkbox.");

            btnMtkRelockBL = Create3DButton("🔒 Relock Bootloader", UiX(0), UiY(2), UiBtnW, UiBtnH, ButtonTheme.Orange);
            btnMtkRelockBL.Click += BtnMtkRelockBL_Click;
            if (toolTipMain != null)
                Tip(btnMtkRelockBL, "da seccfg lock — relock bootloader (may wipe again on some devices).");

            // Column 3 — Userlock / FRP (screen lock နဲ့ Google account lock ဖျက်ခြင်း)
            btnMtkUserlockReset = Create3DButton("🔑 Userlock Reset", UiX(1), UiY(2), UiBtnW, UiBtnH, ButtonTheme.Orange);
            btnMtkUserlockReset.Click += BtnMtkUserlockReset_Click;
            if (toolTipMain != null)
                Tip(btnMtkUserlockReset, "Erase userdata + metadata — removes screen lock. ALL user data wiped.");

            btnMtkFrpRemove = Create3DButton("🔓 FRP Remove", UiX(2), UiY(2), UiBtnW, UiBtnH, ButtonTheme.Red);
            btnMtkFrpRemove.Click += BtnMtkFrpRemove_Click;
            if (toolTipMain != null)
                Tip(btnMtkFrpRemove, "Erase frp + config — Google account lock. User data kept.");

            // Orange State / DM-Verity — vbmeta disabled-flags image ရေးခြင်း
            btnMtkOrangeStateFix = Create3DButton("🟠 Orange State Fix", UiX(0), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Orange);
            btnMtkOrangeStateFix.Click += BtnMtkOrangeStateFix_Click;

            btnMtkDmFix = Create3DButton("🛡 DM Fix (vbmeta)", UiX(1), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Purple);
            btnMtkDmFix.Click += BtnMtkDmFix_Click;

            btnMtkUndoVbmeta = Create3DButton("↩ Undo Vbmeta Fix", UiX(2), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Green);
            btnMtkUndoVbmeta.Click += BtnMtkUndoVbmeta_Click;

            // --- Scatter-based firmware flash (SP Flash ပုံစံ) ---
            Label lblFlashTitle = new Label
            {
                Text = "⚡ FIRMWARE FLASH  —  pick the scatter file, then press START FLASH",
                Location = new Point(UiX(0), UiY(3) + 8),
                AutoSize = true,
                ForeColor = Color.FromArgb(86, 145, 250),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            };

            txtScatterFolder = new TextBox
            {
                Location = new Point(UiX(0), UiY(4) - 17),
                Size = new Size(390, 26),
                ReadOnly = true,
                BackColor = Color.FromArgb(38, 42, 50),
                ForeColor = Color.Gainsboro,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 9f)
            };

            btnMtkPickScatter = Create3DButton("📂 Scatter", UiX(0) + 400, UiY(4) - 19, 100, 28, ButtonTheme.Purple);
            btnMtkPickScatter.Click += BtnMtkPickScatter_Click;

            btnMtkFlashScatter = Create3DButton("◉ FLASH", UiX(0) + 510, UiY(4) - 24, 175, 36, ButtonTheme.Red);
            btnMtkFlashScatter.Click += BtnMtkFlashScatter_Click;

            // ---- Flash Option row (MobileSea ပုံစံ) ----
            chkMtkBackupNvFirst = new CheckBox
            {
                Text = "Backup NVRAM (EFS) first",
                Location = new Point(UiX(0), UiY(4) + 106),
                AutoSize = true,
                ForeColor = Color.FromArgb(255, 200, 90),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            };

            chkMtkResetFrpAfter = new CheckBox
            {
                Text = "Reset FRP after flash",
                Location = new Point(UiX(0) + 210, UiY(4) + 106),
                AutoSize = true,
                ForeColor = Color.FromArgb(255, 200, 90),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            };

            chkMtkSkipUserdata = new CheckBox
            {
                Text = "Skip userdata",
                Location = new Point(UiX(0) + 420, UiY(4) + 106),
                AutoSize = true,
                ForeColor = Color.FromArgb(255, 200, 90),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            };

            chkMtkAutoReboot = new CheckBox
            {
                Text = "Auto reboot",
                Location = new Point(UiX(0) + 697, UiY(4) + 48),
                AutoSize = true,
                Checked = false,
                ForeColor = Color.FromArgb(0, 230, 118),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            };

            chkMtkBackupVbmetaFirst = new CheckBox
            {
                Text = "Backup vbmeta before Fix",
                Location = new Point(UiX(0) + 697, UiY(4) + 78),
                AutoSize = true,
                Checked = true,
                ForeColor = Color.FromArgb(255, 200, 90),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            };

            ctxPartitionMenu = new ContextMenuStrip();
            ToolStripMenuItem itemRead = new ToolStripMenuItem("📥 Read / Dump Partition (.img)", null, MenuReadPartition_Click);
            ToolStripMenuItem itemWrite = new ToolStripMenuItem("📤 Write / Flash Partition (.img)", null, MenuWritePartition_Click);
            ToolStripMenuItem itemErase = new ToolStripMenuItem("🗑 Erase / Format Partition", null, MenuErasePartition_Click);
            ToolStripMenuItem itemMountRw = new ToolStripMenuItem("🔓 Mount RW (remount read-write)", null, MenuMountRwPartition_Click);
            ctxPartitionMenu.Items.AddRange(new ToolStripItem[] { itemRead, itemWrite, itemErase, itemMountRw });

            dgvPartitions = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Color.FromArgb(20, 22, 26),
                ForeColor = Color.White,
                GridColor = Color.FromArgb(45, 49, 58),
                RowHeadersVisible = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                ReadOnly = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BorderStyle = BorderStyle.None,
                ContextMenuStrip = ctxPartitionMenu
            };
            dgvPartitions.CellMouseDown += DgvPartitions_CellMouseDown;
            dgvPartitions.DefaultCellStyle.BackColor = Color.FromArgb(28, 31, 38);
            dgvPartitions.DefaultCellStyle.ForeColor = Color.White;
            dgvPartitions.DefaultCellStyle.SelectionBackColor = Color.FromArgb(86, 145, 250);
            dgvPartitions.DefaultCellStyle.SelectionForeColor = Color.White;
            dgvPartitions.DefaultCellStyle.Font = new Font("Segoe UI", 9.5f);
            dgvPartitions.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(35, 40, 50);
            dgvPartitions.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(86, 145, 250);
            dgvPartitions.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            dgvPartitions.ColumnHeadersHeight = 32;
            dgvPartitions.EnableHeadersVisualStyles = false;

            // MobileSea ပုံစံ — ဘယ် partition ကို လုပ်မလဲ checkbox နဲ့ ရွေး
            DataGridViewCheckBoxColumn colChk = new DataGridViewCheckBoxColumn
            {
                Name = "colCheck",
                HeaderText = "✓",
                Width = 34,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None
            };
            dgvPartitions.Columns.Add(colChk);
            dgvPartitions.Columns.Add("colName", "Partition Name");
            dgvPartitions.Columns.Add("colSize", "Size");
            dgvPartitions.Columns.Add("colOffset", "Start Offset");
            dgvPartitions.Columns.Add("colMount", "Mount");
            dgvPartitions.Columns.Add("colMode", "Mode");
            dgvPartitions.Columns["colName"].ReadOnly = true;
            dgvPartitions.Columns["colSize"].ReadOnly = true;
            dgvPartitions.Columns["colOffset"].ReadOnly = true;

            Panel grpMtkInfo = CreateGroupPanel("INFO", UiX(0), UiY(0), 2, btnMtkReadBrom, btnMtkMemTest, btnMtkDeviceModel);
            // 3-col only — 4-col (x=375 w=675) tab width ~955 ကို ကျော်လွန် cut ဖြစ်
            // 5 buttons → 2 rows (h=115) — INFO နဲ့ အမြင့်တူ, UNLOCK UiY(3) နဲ့ overlap မရှိ
            Panel grpMtkBackup = CreateGroupPanel("BACKUP", UiX(0) + 360, UiY(0), 3,
                btnMtkNvBackup, btnMtkNvErase, btnMtkNvRestore, btnMtkFullDump, btnMtkNormalDump);
            // UiY(2) မှာ INFO(2-row h=115) နဲ့ overlap — UiY(3) ပြောင်း
            Panel grpMtkUnlock = CreateGroupPanel("UNLOCK / SAFE / BOOT FIX", UiX(0), UiY(3), 4,
                btnMtkUnlockBL, btnMtkRelockBL, btnMtkUserlockReset, btnMtkFrpRemove,
                btnMtkOrangeStateFix, btnMtkDmFix, btnMtkUndoVbmeta, btnMtkSafeBackup);

            // Flash block — UNLOCK UiY(3)+115=259 အောက်ကို ရွှေ့ (+30) ပြီး checkbox ၂ ခု Y ခွဲ
            lblFlashTitle.Location = new Point(UiX(0), UiY(4) + 85);
            txtScatterFolder.Location = new Point(UiX(0), UiY(4) + 107);
            btnMtkPickScatter.Location = new Point(UiX(0) + 400, UiY(4) + 105);
            btnMtkFlashScatter.Location = new Point(UiX(0) + 510, UiY(4) + 100);
            chkMtkAutoReboot.Location = new Point(UiX(0) + 697, UiY(4) + 78);   // +48+30 — vbmeta နဲ့ မထပ်
            chkMtkBackupVbmetaFirst.Location = new Point(UiX(0) + 697, UiY(4) + 108);
            chkMtkBackupNvFirst.Location = new Point(UiX(0), UiY(4) + 136);
            chkMtkResetFrpAfter.Location = new Point(UiX(0) + 210, UiY(4) + 136);
            chkMtkSkipUserdata.Location = new Point(UiX(0) + 420, UiY(4) + 136);

            tabMtk.Controls.AddRange(new Control[] {
                grpMtkInfo, grpMtkBackup, grpMtkUnlock,
                lblFlashTitle, txtScatterFolder, btnMtkPickScatter, btnMtkFlashScatter, chkMtkAutoReboot,
                chkMtkBackupNvFirst, chkMtkResetFrpAfter, chkMtkSkipUserdata, chkMtkBackupVbmetaFirst
            });
// --- 5. QUALCOMM TAB (EDL 9008 — edl.py / bkerler EDL) ---
            btnQcReadInfo = Create3DButton("🔍 Read Info / GPT", UiX(0), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Cyan);
            btnQcReadInfo.Click += BtnQcReadInfo_Click;
            if (toolTipMain != null)
                Tip(btnQcReadInfo, "Qualcomm: edl.py printgpt — read device info + partition map.");

            btnQcFullBackup = Create3DButton("💾 Full Backup", UiX(2), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Orange);
            btnQcFullBackup.Click += BtnQcFullBackup_Click;

            btnQcEfsBackup = Create3DButton("📂 EFS Backup", UiX(3), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Cyan);
            btnQcEfsBackup.Click += BtnQcEfsBackup_Click;

            btnQcSaveGpt = Create3DButton("📋 Save GPT + XML", UiX(1), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Purple);
            btnQcSaveGpt.Click += BtnQcSaveGpt_Click;

            btnQcFrpRemove = Create3DButton("🔓 FRP Remove", UiX(3), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Red);
            btnQcFrpRemove.Click += BtnQcFrpRemove_Click;

            btnQcUserlockReset = Create3DButton("🔑 Userlock Reset", UiX(3), UiY(1), UiBtnW, UiBtnH, ButtonTheme.Orange);
            btnQcUserlockReset.Click += BtnQcUserlockReset_Click;

            btnQcReset = Create3DButton("🔄 Reset Device", UiX(0), UiY(1), UiBtnW, UiBtnH, ButtonTheme.Green);
            btnQcReset.Click += BtnQcReset_Click;

            // Firmware flash panel (edl.py) — MTK panel နဲ့ တူတူပုံစံ
            Label lblQcFlashTitle = new Label
            {
                Text = "⚡ FIRMWARE FLASH  —  pick the firmware folder, then press START FLASH",
                Location = new Point(UiX(0), UiY(2) + 2),
                AutoSize = true,
                ForeColor = Color.FromArgb(86, 145, 250),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            };

            txtQcFirmware = new TextBox
            {
                Location = new Point(UiX(0), UiY(2) + 22),
                Size = new Size(390, 26),
                ReadOnly = true,
                BackColor = Color.FromArgb(38, 42, 50),
                ForeColor = Color.Gainsboro,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 9f)
            };

            btnQcPickFirmware = Create3DButton("📂 Folder", UiX(0) + 400, UiY(2) + 20, 100, 28, ButtonTheme.Purple);
            btnQcPickFirmware.Click += BtnQcPickFirmware_Click;

            btnQcStartFlash = Create3DButton("◉ FLASH", UiX(0) + 510, UiY(2) + 16, 175, 36, ButtonTheme.Red);
            btnQcStartFlash.Click += BtnQcStartFlash_Click;
            if (toolTipMain != null)
                Tip(btnQcStartFlash, "Flash rawprogram XML partitions via fh_loader (EDL 9008). Requires COM port + firmware folder.");

            // ---- Flash Option row (MobileSea ပုံစံ) ----
            chkQcBackupEfsFirst = new CheckBox
            {
                Text = "Backup EFS (persist/modemst) first",
                Location = new Point(UiX(0), UiY(4) + 106),
                AutoSize = true,
                ForeColor = Color.FromArgb(255, 200, 90),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            };

            chkQcSkipUserdata = new CheckBox
            {
                Text = "Skip userdata",
                Location = new Point(UiX(0) + 275, UiY(4) + 106),
                AutoSize = true,
                ForeColor = Color.FromArgb(255, 200, 90),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            };

            chkQcResetFrpAfter = new CheckBox
            {
                Text = "Reset FRP after flash",
                Location = new Point(UiX(0) + 420, UiY(4) + 106),
                AutoSize = true,
                ForeColor = Color.FromArgb(255, 200, 90),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            };

            chkQcAutoReboot = new CheckBox
            {
                Text = "Auto reboot",
                Location = new Point(UiX(0) + 697, UiY(2) + 25),
                AutoSize = true,
                Checked = true,
                ForeColor = Color.FromArgb(0, 230, 118),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            };

            Panel grpQcInfo = CreateGroupPanel("INFO", UiX(0), UiY(0), 2, btnQcReadInfo, btnQcReset);
            Panel grpQcBackup = CreateGroupPanel("BACKUP", UiX(0) + 360, UiY(0), 3, btnQcSaveGpt, btnQcFullBackup, btnQcEfsBackup);
            Panel grpQcUnlock = CreateGroupPanel("UNLOCK", UiX(0), UiY(2), 2, btnQcFrpRemove, btnQcUserlockReset);
            // SETUP group (📦 EDL Module / 🧩 Firehose Loader) — user request နဲ့ ဖယ်လိုက်
            // (loader ရွေးဖို့ = အပေါ်က LOADER PICKER၊ edl.py path = auto-find + dialog)

            // ---- Firehose Loader Brand/Model picker (pmk_qc_loaders.json) ----
            // QC tab ရဲ့ အပေါ်ဆုံးတန်း — device list Brand/Model/Detect ကို အစားထိုး
            grpQcLoaderPicker = new Panel
            {
                Location = new Point(15, 11),
                Size = new Size(930, 76),   // row1 Brand/Model + row2 Loader file row (ပုံတူ layout)
                BackColor = Color.FromArgb(23, 26, 32),
                Tag = "grp"
            };
            EnsureThemePaint(grpQcLoaderPicker);

            var lblQcLoaderTitle = new Label { Text = "🧩 LOADER PICKER (Firehose)", Location = new Point(9, 13), AutoSize = true, ForeColor = Color.FromArgb(86, 145, 250), Font = new Font("Segoe UI", 8.5f, FontStyle.Bold) };
            var lblBrandTag = new Label { Text = "Brand:", Location = new Point(175, 14), AutoSize = true, ForeColor = Color.Gainsboro, Font = new Font("Segoe UI", 8.5f) };
            cmbQcBrand = new ComboBox { Location = new Point(225, 10), Size = new Size(160, 26), DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(38, 42, 50), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9f) };
            var lblModelTag = new Label { Text = "Model:", Location = new Point(405, 14), AutoSize = true, ForeColor = Color.Gainsboro, Font = new Font("Segoe UI", 8.5f) };
            cmbQcModel = new ComboBox { Location = new Point(455, 10), Size = new Size(220, 26), DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(38, 42, 50), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9f) };
            lblQcLoaderStatus = new Label { Text = "Auto Detect (saved / auto loader)", Location = new Point(690, 14), AutoSize = false, Size = new Size(230, 20), ForeColor = Color.FromArgb(86, 145, 250), Font = new Font("Segoe UI", 7.5f), AutoEllipsis = true };

            grpQcLoaderPicker.Controls.AddRange(new Control[] { lblQcLoaderTitle, lblBrandTag, cmbQcBrand, lblModelTag, cmbQcModel, lblQcLoaderStatus });
            cmbQcBrand.SelectedIndexChanged += CmbQcBrand_SelectedIndexChanged;
            cmbQcModel.SelectedIndexChanged += CmbQcModel_SelectedIndexChanged;
            LoadQcLoaderDb();   // event wire ပြီးမှ — Auto Detect default select က model combo ပါ fill အောင်

            lblQcFlashTitle.Location = new Point(UiX(0), UiY(4) + 55);
            txtQcFirmware.Location = new Point(UiX(0), UiY(4) + 77);
            btnQcPickFirmware.Location = new Point(UiX(0) + 400, UiY(4) + 75);
            btnQcStartFlash.Location = new Point(UiX(0) + 510, UiY(4) + 70);
            chkQcAutoReboot.Location = new Point(UiX(0) + 697, UiY(4) + 78);

            tabQc.Controls.AddRange(new Control[] {
    grpQcInfo, grpQcBackup, grpQcUnlock, grpQcLoaderPicker,
    lblQcFlashTitle, txtQcFirmware, btnQcPickFirmware, btnQcStartFlash, chkQcAutoReboot,
    chkQcBackupEfsFirst, chkQcSkipUserdata, chkQcResetFrpAfter
});
            // --- 6. SAMSUNG TAB (adb / fastboot) ---
            btnSamInfo = Create3DButton("ℹ️ Read Info", UiX(0), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Cyan);
            btnSamInfo.Click += async (s, e) => await RunBrandInfoAsync("Samsung");

            btnSamFrp = Create3DButton("🔓 FRP Reset", UiX(1), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Red);
            btnSamFrp.Click += BtnSamFrp_Click;

            btnSamKnox = Create3DButton("🛡️ KG / Knox Status", UiX(2), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Green);
            btnSamKnox.Click += async (s, e) => await RunKnoxStatusAsync();

            btnSamDownload = Create3DButton("⬇️ Reboot Download", UiX(0), UiY(1), UiBtnW, UiBtnH, ButtonTheme.Orange);
            btnSamDownload.Click += async (s, e) => await ExecuteCommandCleanAsync("adb.exe", "reboot download", "Samsung - Reboot to Download Mode");

            btnSamReboot = Create3DButton("🔄 Reboot System", UiX(1), UiY(1), UiBtnW, UiBtnH, ButtonTheme.Green);
            btnSamReboot.Click += async (s, e) => await ExecuteCommandCleanAsync("adb.exe", "reboot", "Samsung - Reboot System");

            btnSamSideload = Create3DButton("📦 Sideload ZIP", UiX(2), UiY(1), UiBtnW, UiBtnH, ButtonTheme.Purple);
            btnSamSideload.Click += BtnSamSideload_Click;

            // MTP mode — USB debugging မဖွင့်ရသေး (File Transfer) အခြေအနေမှာ info/FRP
            btnSamMtpInfo = Create3DButton("📱 MTP Read Info", UiX(0), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Cyan);
            btnSamMtpInfo.Click += async (s, e) => await RunSamsungMtpInfoAsync();

            btnSamMtpFrp = Create3DButton("🔓 MTP FRP", UiX(1), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Red);
            btnSamMtpFrp.Click += async (s, e) => await RunSamsungMtpFrpAsync();

            btnSamMtpFactoryReset = Create3DButton("🗑 Factory Reset", UiX(0), UiY(1), UiBtnW, UiBtnH, ButtonTheme.Red);
            btnSamMtpFactoryReset.Click += async (s, e) => await RunSamsungMtpFactoryResetAsync();

            // Download (Odin) mode — ADB/MTP မဟုတ်ဘဲ heimdall + USB က info ဖတ်
            btnSamDlInfo = Create3DButton("⬇️ Download Info", UiX(0), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Cyan);
            btnSamDlInfo.Click += async (s, e) => await RunSamsungDownloadInfoAsync();

            // SoftBrick Fix — Odin protocol flash-count reset (0x64/0x01); firmware reflash မလို
            btnSamSoftBrick = Create3DButton("🧱 SoftBrick Fix", UiX(1), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Orange);
            btnSamSoftBrick.Click += async (s, e) => await RunSamsungSoftBrickAsync();
            if (toolTipMain != null)
                Tip(btnSamSoftBrick,
                    "Download mode error screen (softbrick) အတွက် Odin protocol flash-count reset။\r\n" +
                    "Firmware ပြန်ထည့်စရာမလို — reset ပြီးရင် Download mode ပုံမှန်ပြန်ဝင်တယ်။\r\n" +
                    "Vol+/- + USB → Warning → Vol+ နဲ့ Download mode ဝင်ပြီး နှိပ်ပါ။");

            Label lblSamHint = new Label
            {
                Text = "adb / fastboot + heimdall + MTP info/FRP/factory reset + Download-mode info/softbrick fix.",
                Location = new Point(UiX(0), UiY(2) + 8),
                AutoSize = true,
                ForeColor = Color.FromArgb(150, 165, 185),
                Font = new Font("Segoe UI", 8.5f)
            };

            Label lblSamFlashTitle = new Label
            {
                Text = "⚡ FIRMWARE FLASH (heimdall CLI)  —  .img or .tar/.tar.md5",
                Location = new Point(UiX(0), UiY(3) + 2),
                AutoSize = true,
                ForeColor = Color.FromArgb(86, 145, 250),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            };

            txtSamFirmware = new TextBox
            {
                Location = new Point(UiX(0), UiY(3) + 22),
                Size = new Size(390, 26),
                ReadOnly = true,
                BackColor = Color.FromArgb(38, 42, 50),
                ForeColor = Color.Gainsboro,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 9f)
            };

            btnSamPickFirmware = Create3DButton("📂 Browse", UiX(0) + 400, UiY(3) + 20, 100, 28, ButtonTheme.Purple);
            btnSamPickFirmware.Click += BtnSamPickFirmware_Click;

            btnSamStartFlash = Create3DButton("◉ FLASH (Heimdall)", UiX(0) + 510, UiY(3) + 16, 175, 36, ButtonTheme.Red);
            btnSamStartFlash.Click += BtnSamStartFlash_Click;

            btnSamDownloadPit = Create3DButton("💾 Download PIT", UiX(0) + 697, UiY(3) + 16, 150, 36, ButtonTheme.Purple);
            

            // ---- Flash Option row ----
            chkSamBackupPit = new CheckBox
            {
                Text = "Backup PIT first",
                Location = new Point(UiX(0), UiY(7) + 4),
                AutoSize = true,
                ForeColor = Color.FromArgb(255, 200, 90),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            };

            chkSamAutoReboot = new CheckBox
            {
                Text = "Auto reboot",
                Location = new Point(UiX(0) + 160, UiY(7) + 4),
                AutoSize = true,
                Checked = true,
                ForeColor = Color.FromArgb(0, 230, 118),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            };
            btnSamDownloadPit.Click += BtnSamDownloadPit_Click;

            Panel grpSamInfo = CreateGroupPanel("INFO (ADB)", UiX(0), UiY(0), 2, btnSamInfo, btnSamKnox);
            Panel grpSamUnlock = CreateGroupPanel("UNLOCK (ADB)", UiX(0) + 360, UiY(0), 1, btnSamFrp);
            Panel grpSamMtp = CreateGroupPanel("MTP (NO ADB)", UiX(0) + 540, UiY(0), 2, btnSamMtpInfo, btnSamMtpFrp, btnSamMtpFactoryReset);
            Panel grpSamReboot = CreateGroupPanel("REBOOT", UiX(0), UiY(2), 3, btnSamDownload, btnSamReboot, btnSamSideload);
            // MTP group 2-row (h=115) ကြောင့် DL group ကို UiY(3) ရွှေ့ — overlap မဖြစ်အောင်
            // cols=2 → Info + SoftBrick Fix (same row, MTP group width 345)
            Panel grpSamDl = CreateGroupPanel("DOWNLOAD MODE", UiX(0) + 540, UiY(3), 2, btnSamDlInfo, btnSamSoftBrick);

            lblSamFlashTitle.Location = new Point(UiX(0), UiY(4) + 55);
            txtSamFirmware.Location = new Point(UiX(0), UiY(4) + 77);
            btnSamPickFirmware.Location = new Point(UiX(0) + 400, UiY(4) + 75);
            btnSamStartFlash.Location = new Point(UiX(0) + 510, UiY(4) + 70);
            btnSamDownloadPit.Location = new Point(UiX(0) + 697, UiY(4) + 70);
            lblSamHint.Location = new Point(UiX(0), UiY(8) + 4);

            tabSamsung.Controls.AddRange(new Control[] {
    grpSamInfo, grpSamUnlock, grpSamMtp, grpSamReboot, grpSamDl,
    lblSamFlashTitle, txtSamFirmware, btnSamPickFirmware, btnSamStartFlash, btnSamDownloadPit,
    chkSamBackupPit, chkSamAutoReboot
});
            // --- 7. SPREADTRUM / UNISOC TAB (adb / fastboot) ---
            btnSpdInfo = Create3DButton("ℹ️ Read Info", UiX(0), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Cyan);
            btnSpdInfo.Click += async (s, e) => await RunBrandInfoAsync("Spreadtrum / Unisoc");

            btnSpdFrp = Create3DButton("🔓 FRP Reset", UiX(1), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Red);
            btnSpdFrp.Click += BtnSpdFrp_Click;

            btnSpdReboot = Create3DButton("🔄 Reboot System", UiX(2), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Green);
            btnSpdReboot.Click += async (s, e) => await ExecuteCommandCleanAsync("adb.exe", "reboot", "Spreadtrum - Reboot System");

            btnSpdFastboot = Create3DButton("⚡ Reboot Fastboot", UiX(3), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Orange);
            btnSpdFastboot.Click += async (s, e) => await ExecuteCommandCleanAsync("adb.exe", "reboot bootloader", "Spreadtrum - Reboot to Fastboot");

            Label lblSpdHint = new Label
            {
                Text = "PAC flash: put spd_dump.exe + unpac.exe in the tool folder (or a folder on PATH). The PAC is unpacked by unpac, then flashed directly with the spd_dump CLI (no ResearchDownload needed).",
                Location = new Point(UiX(0), UiY(1) + 8),
                AutoSize = true,
                ForeColor = Color.FromArgb(150, 165, 185),
                Font = new Font("Segoe UI", 8.5f)
            };

            Label lblSpdFlashTitle = new Label
            {
                Text = "⚡ FIRMWARE FLASH (spd_dump CLI)  —  PAC firmware",
                Location = new Point(UiX(0), UiY(2) + 2),
                AutoSize = true,
                ForeColor = Color.FromArgb(86, 145, 250),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            };

            txtSpdFirmware = new TextBox
            {
                Location = new Point(UiX(0), UiY(2) + 22),
                Size = new Size(390, 26),
                ReadOnly = true,
                BackColor = Color.FromArgb(38, 42, 50),
                ForeColor = Color.Gainsboro,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 9f)
            };

            btnSpdPickFirmware = Create3DButton("📂 PAC", UiX(0) + 400, UiY(2) + 20, 100, 28, ButtonTheme.Purple);
            btnSpdPickFirmware.Click += BtnSpdPickFirmware_Click;

            btnSpdDirectFlash = Create3DButton("◉ FLASH (spd_dump)", UiX(0) + 510, UiY(2) + 16, 200, 36, ButtonTheme.Red);
            btnSpdDirectFlash.Click += BtnSpdDirectFlash_Click;

            // SPD FDL partition ops (spd_dump direct) — Read GPT / Read / Write / Erase
            btnSpdReadGpt = Create3DButton("🗺️ Read GPT", UiX(0), UiY(4), UiBtnW, UiBtnH, ButtonTheme.Cyan);
            btnSpdReadGpt.Click += BtnSpdReadGpt_Click;

            btnSpdReadPart = Create3DButton("⬇️ Read Part", UiX(1), UiY(4), UiBtnW, UiBtnH, ButtonTheme.Green);
            btnSpdReadPart.Click += BtnSpdReadPart_Click;

            btnSpdWritePart = Create3DButton("⬆️ Write Part", UiX(2), UiY(4), UiBtnW, UiBtnH, ButtonTheme.Green);
            btnSpdWritePart.Click += BtnSpdWritePart_Click;

            btnSpdErasePart = Create3DButton("🗑️ Erase Part", UiX(3), UiY(4), UiBtnW, UiBtnH, ButtonTheme.Red);
            btnSpdErasePart.Click += BtnSpdErasePart_Click;

            // UNLOCK (Diag) — spd_dump diag channel (Channel9) ကနေ FDL erase။
            // EFT/ResearchDownload "Diag mode" လိုပဲ — persist/frp = FRP၊ userdata = userlock
            btnSpdDiagFrp = Create3DButton("🔐 FRP (Diag)", UiX(0) + 510, UiY(2) + 69, UiBtnW, UiBtnH, ButtonTheme.Red);
            btnSpdDiagFrp.Click += BtnSpdDiagFrp_Click;

            btnSpdDiagUserlock = Create3DButton("🔒 Userlock+FRP", UiX(0) + 675, UiY(2) + 69, UiBtnW, UiBtnH, ButtonTheme.Orange);
            btnSpdDiagUserlock.Click += BtnSpdDiagUserlock_Click;

            Panel grpSpdInfo = CreateGroupPanel("INFO", UiX(0), UiY(0), 1, btnSpdInfo);
            Panel grpSpdUnlock = CreateGroupPanel("UNLOCK", UiX(0) + 195, UiY(0), 1, btnSpdFrp);
            Panel grpSpdReboot = CreateGroupPanel("REBOOT", UiX(0) + 390, UiY(0), 2, btnSpdReboot, btnSpdFastboot);
            Panel grpSpdDiag = CreateGroupPanel("UNLOCK (Diag)", UiX(0) + 510, UiY(2) + 69, 2, btnSpdDiagFrp, btnSpdDiagUserlock);
            Panel grpSpdPart = CreateGroupPanel("PARTITIONS (spd_dump)", UiX(0), UiY(4) + 56, 4,
                                                btnSpdReadGpt, btnSpdReadPart, btnSpdWritePart, btnSpdErasePart);

            lblSpdFlashTitle.Location = new Point(UiX(0), UiY(2) - 9);
            txtSpdFirmware.Location = new Point(UiX(0), UiY(2) + 13);
            btnSpdPickFirmware.Location = new Point(UiX(0) + 400, UiY(2) + 11);
            btnSpdDirectFlash.Location = new Point(UiX(0) + 510, UiY(2) + 6);
            lblSpdHint.Location = new Point(UiX(0), UiY(2) + 50);

            tabSpd.Controls.AddRange(new Control[] {
    grpSpdInfo, grpSpdUnlock, grpSpdReboot, grpSpdPart, grpSpdDiag,
    lblSpdFlashTitle, txtSpdFirmware, btnSpdPickFirmware, btnSpdDirectFlash
});
            // --- 8. HISILICON (HUAWEI / HONOR) TAB — PotatoNV core နဲ့ Kirin unlock ---
            // Row 1: Kirin model + COM port (testpoint mode) + START
            btnHisiStartUnlock = Create3DButton("🔓 START UNLOCK", UiX(0), UiY(0), UiBtnW, UiBtnH, ButtonTheme.Red);
            btnHisiStartUnlock.Click += BtnHisiStartUnlock_Click;

            Label lblKirin = new Label
            {
                Text = "Kirin:",
                Location = new Point(UiX(0) + 173, UiY(0) + 10),
                AutoSize = true,
                ForeColor = Color.LightSteelBlue,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };

            cmbKirin = new ComboBox
            {
                Location = new Point(UiX(0) + 218, UiY(0) + 6),
                Size = new Size(155, 26),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(38, 42, 50),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.5f)
            };

            Label lblHisiPort = new Label
            {
                Text = "COM:",
                Location = new Point(UiX(0) + 383, UiY(0) + 10),
                AutoSize = true,
                ForeColor = Color.LightSteelBlue,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };

            cmbHisiPort = new ComboBox
            {
                Location = new Point(UiX(0) + 427, UiY(0) + 6),
                Size = new Size(140, 26),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(38, 42, 50),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.5f)
            };

            btnHisiRefreshPorts = Create3DButton("🔄", UiX(0) + 535, UiY(0) + 5, 40, 28, ButtonTheme.Cyan);
            btnHisiRefreshPorts.Click += (s, e) => RefreshHisiPorts();

            // Row 2: adb operations
            btnHisiInfo = Create3DButton("ℹ️ Read Info", UiX(0), UiY(1), UiBtnW, UiBtnH, ButtonTheme.Cyan);
            btnHisiInfo.Click += async (s, e) => await RunBrandInfoAsync("HiSilicon / Huawei");

            btnHisiFrp = Create3DButton("🔓 FRP Reset", UiX(1), UiY(1), UiBtnW, UiBtnH, ButtonTheme.Red);
            btnHisiFrp.Click += BtnHisiFrp_Click;

            btnHisiReboot = Create3DButton("🔄 Reboot System", UiX(2), UiY(1), UiBtnW, UiBtnH, ButtonTheme.Green);
            btnHisiReboot.Click += async (s, e) => await ExecuteCommandCleanAsync("adb.exe", "reboot", "HiSilicon - Reboot System");

            btnHisiFastboot = Create3DButton("⚡ Reboot Fastboot", UiX(3), UiY(1), UiBtnW, UiBtnH, ButtonTheme.Orange);
            btnHisiFastboot.Click += async (s, e) => await ExecuteCommandCleanAsync("adb.exe", "reboot bootloader", "HiSilicon - Reboot to Fastboot");

            // Row 3: tools
            btnHisiBootloaders = Create3DButton("📁 Bootloaders", UiX(4), UiY(1), UiBtnW, UiBtnH, ButtonTheme.Orange);
            btnHisiBootloaders.Click += BtnHisiBootloaders_Click;

            btnHisiOemUnlock = Create3DButton("⚡ OEM Unlock (code)", UiX(5), UiY(1), UiBtnW, UiBtnH, ButtonTheme.Red);
            btnHisiOemUnlock.Click += BtnHisiOemUnlock_Click;

            // OPEN HUAWEI 2018 menu-style: install driver pack → enable ADB → uninstall
            btnHisiInstallDrivers = Create3DButton("📦 Install Drivers", 0, 0, UiBtnW, UiBtnH, ButtonTheme.Green);
            btnHisiInstallDrivers.Click += BtnHisiInstallDrivers_Click;

            btnHisiEnableAdb = Create3DButton("🔓 Enable ADB", 0, 0, UiBtnW, UiBtnH, ButtonTheme.Cyan);
            btnHisiEnableAdb.Click += BtnHisiEnableAdb_Click;

            btnHisiUninstallDrivers = Create3DButton("🗑 Uninstall Drivers", 0, 0, UiBtnW, UiBtnH, ButtonTheme.Orange);
            btnHisiUninstallDrivers.Click += BtnHisiUninstallDrivers_Click;

            Label lblHisiHint = new Label
            {
                Text = "Kirin unlock: testpoint short → 'HUAWEI USB COM 1.0' → pick Kirin → START UNLOCK → then OEM Unlock (code).  Kirin 620-960 only.  Drivers first: Install Drivers → Enable ADB → Reboot → FRP Reset.",
                Location = new Point(UiX(0), UiY(2) + 8),
                AutoSize = true,
                ForeColor = Color.FromArgb(150, 165, 185),
                Font = new Font("Segoe UI", 8.5f)
            };

            Panel grpHisiUnlock = CreateGroupPanel("KIRIN UNLOCK", UiX(0), UiY(0), 4,
                btnHisiStartUnlock, lblKirin, cmbKirin, lblHisiPort, cmbHisiPort, btnHisiRefreshPorts);
            // CreateGroupPanel grid ကြောင့် COM dropdown က row 2 ကျသွား — တစ်ကြောင်းတည်း ပြန်ချိန်
            lblKirin.Location = new Point(185, 30);
            cmbKirin.Location = new Point(230, 26);
            lblHisiPort.Location = new Point(400, 30);
            cmbHisiPort.Location = new Point(445, 26);
            btnHisiRefreshPorts.Location = new Point(595, 25);
            Panel grpHisiInfo = CreateGroupPanel("INFO / REBOOT", UiX(0), UiY(3), 3, btnHisiInfo, btnHisiReboot, btnHisiFastboot);
            Panel grpHisiUnlock2 = CreateGroupPanel("UNLOCK / TOOLS", UiX(0) + 540, UiY(3), 2, btnHisiFrp, btnHisiOemUnlock, btnHisiBootloaders);
            // OPEN HUAWEI 2018 style: Drivers pack install + ADB enable sequence
            Panel grpHisiDrivers = CreateGroupPanel("DRIVERS / ADB (OPEN HUAWEI)", UiX(0), UiY(5), 3,
                btnHisiInstallDrivers, btnHisiEnableAdb, btnHisiUninstallDrivers);

            lblHisiHint.Location = new Point(UiX(0), UiY(5) + 72 + 4);
            tabHisi.Controls.AddRange(new Control[] { grpHisiUnlock, grpHisiInfo, grpHisiUnlock2, grpHisiDrivers, lblHisiHint });
// ================= 3. PROGRESS + LOG AREA (အောက်ပိုင်း) =================
            panelLogArea = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(18, 20, 24) };

            panelProgress = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(26, 29, 35)
            };

            pbarOperation = new ProgressBar
            {
                Location = new Point(10, 7),
                Size = new Size(640, 20),
                Style = ProgressBarStyle.Continuous,
                Value = 0,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            lblProgressStatus = new Label
            {
                Text = "Status: Ready",
                Location = new Point(660, 8),
                AutoSize = true,
                Font = new Font("Segoe UI", 9.2f, FontStyle.Bold),
                ForeColor = Color.FromArgb(86, 145, 250),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };

            lblSpeedBadge = new Label
            {
                Text = "Speed: 0 MB/s",
                Location = new Point(830, 8),
                AutoSize = true,
                Font = new Font("Segoe UI", 9.2f, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 255, 128),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };

            panelProgress.Controls.AddRange(new Control[] { pbarOperation, lblProgressStatus, lblSpeedBadge });

            // ================= 4. STOP & CLEAR BUTTONS (log ရဲ့ အပေါ်၊ ညာဘက်) =================
            btnStopTask = Create3DButton("🛑 STOP", 0, 0, 110, 28, ButtonTheme.Red);
            btnStopTask.Click += BtnStopTask_Click;

            btnClearLog = Create3DButton("🧹 Clear Log", 0, 0, 120, 28, ButtonTheme.Cyan);
            btnClearLog.Click += (s, e) => { rtbLog.Clear(); ResetProgress(); };

            Button btnExportLog = Create3DButton("💾 Export Log", 0, 0, 130, 28, ButtonTheme.Purple);
            btnExportLog.Click += BtnExportLog_Click;

            panelLogButtons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                BackColor = Color.FromArgb(22, 25, 30),
                Padding = new Padding(0, 4, 8, 0)
            };
            // margin ကို တိတိကျကျ သတ်မှတ် — မဟုတ်ရင် ခလုတ်ရဲ့ အောက်စွန်း ဖြတ်ခံရတယ်
            btnStopTask.Margin = new Padding(0, 3, 6, 3);
            btnClearLog.Margin = new Padding(0, 3, 6, 3);
            btnExportLog.Margin = new Padding(0, 3, 6, 3);
            panelLogButtons.Controls.Add(btnClearLog);
            panelLogButtons.Controls.Add(btnExportLog);
            panelLogButtons.Controls.Add(btnStopTask);

            rtbLog = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = Color.FromArgb(10, 11, 14),
                ForeColor = Color.FromArgb(0, 255, 128),
                Font = new Font("Consolas", 10f, FontStyle.Regular),
                BorderStyle = BorderStyle.None
            };
            rtbLog.Name = "pmkRtbLog";

            panelLogArea.Controls.Add(rtbLog);   // logLayout ထဲမှာ ထည့်တယ် (အောက်က ကုဒ်ကို ကြည့်)

            // ================= 5. PARTITION PANEL (tab အားလုံးမှာ အမြဲမြင်) =================
            panelPartition = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(20, 22, 26)
            };

            panelPartitionHeader = new Panel { Dock = DockStyle.Top, Height = 24, BackColor = Color.FromArgb(26, 29, 35) };
            panelPartitionHeader.Controls.Add(new Label
            {
                Text = "📋  PARTITIONS  —  right-click: Read / Write / Erase / Mount RW",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 0, 0),
                ForeColor = Color.FromArgb(86, 145, 250),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            });

            txtPartFilter = new TextBox
            {
                Dock = DockStyle.Right,
                Width = 130,
                Margin = new Padding(0, 2, 4, 2),
                BackColor = Color.FromArgb(38, 42, 50),
                ForeColor = Color.Gainsboro,
                Font = new Font("Segoe UI", 8.5f),
                BorderStyle = BorderStyle.FixedSingle
            };
            // placeholder စာသား — focus မရှိရင် "Filter…" ပြ
            txtPartFilter.GotFocus += (s, e) =>
            {
                if (txtPartFilter.Text == "Filter…") { txtPartFilter.Text = ""; txtPartFilter.ForeColor = Color.Gainsboro; }
            };
            txtPartFilter.LostFocus += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(txtPartFilter.Text))
                {
                    txtPartFilter.Text = "Filter…";
                    txtPartFilter.ForeColor = Color.Gray;
                    ApplyPartitionFilter();
                }
            };
            txtPartFilter.TextChanged += (s, e) => ApplyPartitionFilter();
            if (toolTipMain != null)
                Tip(txtPartFilter, "Partition name filter — type to show only matching rows (nvram, boot, vbmeta…)");
            panelPartitionHeader.Controls.Add(txtPartFilter);

            panelPartBtns = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                BackColor = Color.FromArgb(26, 29, 35),
                Padding = new Padding(0, 1, 6, 0)
            };
            btnPartAll = new Button { Text = "✓ All", Size = new Size(56, 21), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(30, 70, 45), ForeColor = Color.White, Font = new Font("Segoe UI", 8f, FontStyle.Bold), Cursor = Cursors.Hand, Tag = "white" };
            btnPartAll.FlatAppearance.BorderColor = Color.FromArgb(50, 205, 100);
            btnPartAll.Click += (s2, e2) => SetAllPartitionChecks(true);
            btnPartNone = new Button { Text = "✗ None", Size = new Size(62, 21), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(70, 35, 35), ForeColor = Color.White, Font = new Font("Segoe UI", 8f, FontStyle.Bold), Cursor = Cursors.Hand, Tag = "white" };
            btnPartNone.FlatAppearance.BorderColor = Color.FromArgb(230, 110, 110);
            btnPartNone.Click += (s2, e2) => SetAllPartitionChecks(false);
            panelPartBtns.Controls.Add(btnPartNone);
            panelPartBtns.Controls.Add(btnPartAll);
            panelPartitionHeader.Controls.Add(panelPartBtns);

            panelPartition.Controls.Add(dgvPartitions);          // Fill
            panelPartition.Controls.Add(panelPartitionHeader);   // Top
            // placeholder init
            txtPartFilter.Text = "Filter…";
            txtPartFilter.ForeColor = Color.Gray;

            // ================= 6. SPLIT LAYOUT =================
            // အပေါ် — tabs + partition list | အောက် — progress + log
            // splitter (⇕) ကို ဆွဲပြီး log ကို ကြီးငယ် လုပ်နိုင်တယ်
            splitMain = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterWidth = 6,
                Panel1MinSize = 120,
                Panel2MinSize = 120,
                BackColor = Color.FromArgb(45, 49, 58)
            };

            // Dock order ကြောင့် ထပ်မဖြစ်အောင် TableLayoutPanel နဲ့ တိတိကျကျ ခွဲတယ်
            // (အပေါ်: tab | partition list၊ အောက်: progress | log ခလုတ်များ | log)
            topLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = Color.FromArgb(18, 20, 24)
            };
            // ---- Platform bar (MobileSea ပုံစံ) ----
            platformBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = Color.FromArgb(22, 25, 30),
                Padding = new Padding(6, 7, 0, 0)   // raised border + button gap
            };
            string[] platformNames = { "ADB / FB", "MediaTek", "Qualcomm", "Samsung", "Spreadtrum", "HiSilicon" };
            platformBtns = new List<Button>();
            for (int i = 0; i < platformNames.Length; i++)
            {
                int idx = i;
                Button pb = new Button
                {
                    Text = platformNames[i],
                    Size = new Size(118, 32),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(38, 44, 54),
                    ForeColor = Color.White,
                    Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                    Margin = new Padding(0, 0, 4, 2),
                    Cursor = Cursors.Hand,
                    Tag = "platform"
                };
                pb.FlatAppearance.BorderColor = Color.FromArgb(70, 80, 95);
                pb.Click += (s2, e2) => { tabControl.SelectedIndex = idx; };
                platformBtns.Add(pb);
                platformBar.Controls.Add(pb);
            }

            tabControl.SelectedIndexChanged += (s2, e2) => SyncPlatformBar();

            topLayout.RowCount = 3;
            topLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));   // Platform bar
            topLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));   // tab content
            topLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 250F));  // partition list
            topLayout.Controls.Add(platformBar, 0, 0);
            topLayout.Controls.Add(tabControl, 0, 1);
            topLayout.Controls.Add(panelPartition, 0, 2);
            SyncPlatformBar();
            splitMain.Panel2.Controls.Add(topLayout);   // ညာဘက် — platform bar + tab + partition

            // Partition box — tab content အောက်မှာ ကပ်ပြီး ကျန်တဲ့ နေရာအကုန် ယူ
            // (height fixed 250 မဟုတ် — tab အလိုက် content အမြင့် ပေါ် မူတည်)
            // tabSamsung/tabSpd/tabAdb ကိုပါ ထည့် — ADB ROOT partitions က main grid မှာ ပြ
            tabControl.SelectedIndexChanged += (s, e) => SyncPartitionPanel(true);
            topLayout.Resize += (s, e) => SyncPartitionPanel(false);
            tabControl.SelectedIndexChanged += (s, e) =>
            {
                if (tabControl.SelectedTab == null || lblPlatformStatus == null) return;
                lblPlatformStatus.Text = "Platform: " + tabControl.SelectedTab.Text;
            };
            // SplitterMoving က user လက်နဲ့ ဆွဲတဲ့အခါသာ ဖြစ်တယ် (programmatic မှာ မဖြစ်) —
            // ဒါကြောင့် tab အလိုက် အလိုအလျောက် ချိန်တာကို မပိတ်စေရ
            splitMain.SplitterMoving += (s, e) => userAdjustedSplit = true;
            SyncPartitionPanel(true);

            // Form ရဲ့ တကယ့် အမြင့် ရပြီးမှ splitter ကို tab အလိုက် ပြန်ချိန်
            // (constructor ထဲမှာ ခေါ်တုန်း splitMain.Height က မှန်နေပြီးသား မဟုတ်လို့)
            this.Load += (s, e) =>
            {
                try
                {
                    if (pendingSplitterDistance > 100 && pendingSplitterDistance < splitMain.Width - 100)
                    {
                        userAdjustedSplit = true;
                        splitMain.SplitterDistance = pendingSplitterDistance;
                    }
                    else
                    {
                        userAdjustedSplit = false;
                        splitMain.SplitterDistance = (int)(splitMain.Width * 0.30);
                    }
                    SyncPartitionPanel(true);
                }
                catch { }

                // selected platform tab က startup မှာ highlight မပေါ်တတ် — Load မှာ တစ်ခါထပ်ချိန်
                SyncPlatformBar();

                // COM port list ကို "Scan Port" မနှိပ်ဘဲ အလိုအလျောက် refresh (၃ စက္ကန့်တစ်ခါ)
                // — ဖုန်း ချိတ်/ဖြုတ်လုပ်တာနဲ့ port က ချက်ချင်း ပေါ်လာအောင်။
                BuildDevicePickers();      // MTK/QC/Samsung/SPD tab တစ်ခုစီမှာ Brand → Model → CPU list
                SyncPartitionPanel(false); // content host တွေ ပြောင်းပြီးမှ row အမြင့် ပြန်ချိန်
                ApplyDoubleBuffering();    // tab switch white flicker ကာ
                RefillPortCombo();
                RefreshMtkDetection();   // background (PowerShell) — UI မဟန်းအောင်
                devicePollTimer = new System.Windows.Forms.Timer { Interval = 3000 };
                int pollTick = 0;
                bool pollTickBusy = false;
                devicePollTimer.Tick += async (s2, e2) =>
                {
                    // တစ် tick ပြီးမှ နောက် tick — CheckAllDevices ကြာ/exception ဖြစ်ရင် ထပ်ဝင်မစ
                    if (pollTickBusy) return;
                    pollTickBusy = true;
                    try
                    {
                        // license သက်တမ်းကုန် → client-side block (server check ထပ်မံ)
                        if (DateTime.UtcNow >= licenseExpiresUtc)
                        {
                            MessageBox.Show(this,
                                "Subscription သက်တမ်းကုန်ပါပြီ — tool ကို ပိတ်ပါမည်။\n\nAdmin ကို renew လုပ်ပါ။",
                                "License expired", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            Environment.Exit(0);
                        }
                        // port list ကို ၃ စက္ကန့်တစ်ခါ (အလွန် ပေါ့)၊ device status ကို ၆ စက္ကန့်တစ်ခါ
                        // (adb/fastboot/PowerShell query တွေ ပါတာမို့ ပိုလေးတယ်)။
                        RefillPortCombo();
                        pollTick++;
                        if (pollTick % 2 == 0)
                        {
                            RefreshMtkDetection();
                            if (!isDetecting) await CheckAllDevicesAsync(false);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("[Poll] " + ex.Message, Color.Orange);
                    }
                    finally
                    {
                        pollTickBusy = false;
                    }
                };
                devicePollTimer.Start();
            };

            logLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                BackColor = Color.FromArgb(18, 20, 24)
            };
            logLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));   // progress bar + status
            logLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));   // STOP / Clear Log (ခလုတ် အပြည့်မြင်ရဖို့)
            logLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));   // LOG
            logLayout.Controls.Add(panelProgress, 0, 0);
            logLayout.Controls.Add(panelLogButtons, 0, 1);
            logLayout.Controls.Add(rtbLog, 0, 2);
            splitMain.Panel1.Controls.Add(logLayout);   // ဘယ်ဘက်တိုင် — LOG (UNLOCKTOOL)

            // Root layout — header (80px = panelTopHeader နဲ့ တူရမည်; 60F ဆိုရင် row2 labels ဖြတ်) + split
            rootLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                BackColor = Color.FromArgb(18, 20, 24)
            };
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 80F));
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            panelTopHeader.Dock = DockStyle.Fill;
            rootLayout.Controls.Add(panelTopHeader, 0, 0);
            rootLayout.Controls.Add(splitMain, 0, 1);

            this.Controls.Add(rootLayout);
        }

        // ================= STOP TASK HANDLER =================
        private void BtnStopTask_Click(object sender, EventArgs e)
        {
            try
            {
                if (isTaskRunning || flashWorkflowRunning)
                {
                    stopRequested = true;
                    var process = activeProcess;
                    try { if (process != null && !process.HasExited) process.Kill(true); }
                    catch (InvalidOperationException) { }
                    // RunQuickAsync processes (adb wait-for-device etc.) ကိုပါ ရပ်
                    try { ReviewSafety.KillAllQuick(); } catch { }
                    Log("[!] Operation Stopped by User.", Color.OrangeRed);
                    SetProgress(0, "Stopped", "0 MB/s");
                }
                else
                {
                    Log("[*] No active background task is running.", Color.Gray);
                }
            }
            catch (Exception ex)
            {
                Log("Stop Error: " + ex.Message, Color.Red);
            }
        }

        // Log ကို ဖိုင်အဖြစ် save တယ်
        private void BtnExportLog_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(rtbLog.Text))
            {
                Log("[!] Log is empty - nothing to export.", Color.Orange);
                return;
            }

            using (SaveFileDialog sfd = new SaveFileDialog
            {
                Filter = "Log file (*.log;*.txt)|*.log;*.txt|All Files (*.*)|*.*",
                FileName = "pmk_log_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log",
                Title = "Export Log"
            })
            {
                if (sfd.ShowDialog() != DialogResult.OK) return;
                try
                {
                    File.WriteAllText(sfd.FileName, rtbLog.Text, Encoding.UTF8);
                    Log("[OK] Log exported: " + sfd.FileName, Color.LightGreen);
                }
                catch (Exception ex)
                {
                    Log("[x] Log export failed: " + ex.Message, Color.Red);
                }
            }
        }

        // Partition နာမည် command-injection မဖြစ်အောင် စစ်တယ် (a-z A-Z 0-9 _ - ပဲ ခွင့်ပြု)
        private static bool IsValidPartitionName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            foreach (char c in name.Trim())
                if (!char.IsLetterOrDigit(c) && c != '_' && c != '-') return false;
            return true;
        }

        // ရွေးထားတဲ့ (checkbox ခြစ်ထားတဲ့) partition စာရင်း — filter ဖြင့် မမြင်ရသေးတဲ့ row တွေ မပါ
        private List<string> GetCheckedPartitions()
        {
            var list = new List<string>();
            foreach (DataGridViewRow r in dgvPartitions.Rows)
            {
                if (!r.Visible) continue;
                object v = r.Cells["colCheck"].Value;
                if (v == null || !(bool)v) continue;
                object nm = r.Cells["colName"].Value;
                if (nm != null && !string.IsNullOrEmpty(nm.ToString())) list.Add(nm.ToString());
            }
            return list;
        }

        private void SetAllPartitionChecks(bool state)
        {
            int n = 0;
            foreach (DataGridViewRow r in dgvPartitions.Rows)
            {
                if (!r.Visible) continue;
                r.Cells["colCheck"].Value = state;
                n++;
            }
            Log("[*] Partitions " + (state ? "selected ✓" : "cleared ✗") + " (" + n + " shown rows)",
                state ? Color.LightGreen : Color.Gray);
        }

        // Header က filter box — partition name နဲ့ မကိုက်ရင် row ကို ဝှက်ထားတယ်
        private void ApplyPartitionFilter()
        {
            if (dgvPartitions == null) return;
            string q = (txtPartFilter?.Text ?? "").Trim();
            if (q == "Filter…") q = "";
            q = q.ToLowerInvariant();
            foreach (DataGridViewRow r in dgvPartitions.Rows)
            {
                object nm = r.Cells["colName"].Value;
                string name = nm == null ? "" : nm.ToString().ToLowerInvariant();
                r.Visible = q.Length == 0 || name.Contains(q);
            }
        }

        private void ClearPartitionData()
        {
            dgvPartitions.Rows.Clear();
            loadedPartitions.Clear();
        }

        // ===== Device props helper =====
        // တချို့ device တွေမှာ ro.product.brand/model က အလွတ် ဖြစ်နေတတ်တယ် (ဥပမာ Redmi Note 11 / Android 12
        // — တကယ့် တန်ဖိုးတွေက ro.product.bootimage.* မှာ ရှိတယ်)။ ဒါကြောင့် getprop အားလုံးကို တစ်ခါ
        // dump လုပ်ပြီး fallback chain နဲ့ ဖတ်တယ် (adb call လည်း သက်သာတယ်)။
        private Dictionary<string, string> deviceProps = null;
        // ရည်ရွယ်ချက်ရှိ reboot wait window (auto-reboot retry) အတွင်း — ဖုန်းပျောက်နေတာ သဘာဝမို့
        // disconnect error တွေကို friendly line နဲ့ suppress လုပ်
        private volatile bool rebootWaitActive = false;

        private async Task<bool> LoadDevicePropsAsync()
        {
            try
            {
                string dump = await ExecuteCommandQuickAsync("adb.exe", "shell getprop");
                if (string.IsNullOrWhiteSpace(dump)) { deviceProps = null; return false; }
                Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match m in Regex.Matches(dump, @"\[([^\]]+)\]:\s*\[([^\]]*)\]"))
                {
                    string k = m.Groups[1].Value.Trim();
                    string v = m.Groups[2].Value.Trim();
                    if (k.Length > 0) d[k] = v;
                }
                deviceProps = d;
                return true;
            }
            catch { deviceProps = null; return false; }
        }

        private string Prop(params string[] keys)
        {
            if (deviceProps == null) return "";
            foreach (string k in keys)
            {
                string v;
                if (deviceProps.TryGetValue(k, out v) && !string.IsNullOrWhiteSpace(v)) return v;
            }
            return "";
        }

        private static readonly string[] PropBrandKeys = {
            "ro.product.brand", "ro.product.bootimage.brand", "ro.product.vendor.brand",
            "ro.product.odm.brand", "ro.product.system.brand", "ro.product.product.brand",
            "ro.product.manufacturer", "ro.product.bootimage.manufacturer", "ro.product.name" };

        private static readonly string[] PropModelKeys = {
            "ro.product.model", "ro.product.bootimage.model", "ro.product.vendor.model",
            "ro.product.odm.model", "ro.product.system.model", "ro.product.product.model",
            "ro.product.marketname", "ro.product.name", "ro.product.device", "ro.product.bootimage.device" };

        private static readonly string[] PropDeviceKeys = {
            "ro.product.device", "ro.product.bootimage.device", "ro.product.vendor.device",
            "ro.product.odm.device", "ro.product.system.device", "ro.product.name" };

        private static readonly string[] PropSocKeys = {
            "ro.soc.model", "ro.soc.manufacturer", "ro.board.platform", "ro.hardware",
            "ro.product.board", "ro.chipname" };

        // ================= SCREEN FIT =================
        // Window က fixed-size မို့ မျက်နှာပြင် သေးတဲ့ PC (သို့) DPI ကြီးတဲ့ PC တွေမှာ
        // အောက်ပိုင်း ဖြတ်မခံရအောင် တစ်ချိုးကျ ကျုံ့ပေးတယ် (ခလုတ်/စာသား အားလုံး လိုက်ကျုံ့)။
        private void FitToScreen()
        {
            try
            {
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                float availW = wa.Width - 16f;
                float availH = wa.Height - 48f;      // taskbar + အနားလွတ်
                float scale = Math.Min(availW / this.Width, availH / this.Height);

                if (scale < 0.99f)
                {
                    scale = Math.Max(scale, 0.5f);
                    this.Scale(new SizeF(scale, scale));
                    Log("[*] Window (" + wa.Width + "x" + wa.Height + ") - scaling to fit " +
                        (int)Math.Round(scale * 100) + "% (if text looks too small, check the Windows display scale).",
                        Color.Orange);
                }
            }
            catch (Exception ex)
            {
                Log("Screen fit error: " + ex.Message, Color.Red);
            }
        }

        // ================= PROGRESS BAR CONTROLLER =================
        private void SetProgress(int percent, string status, string speed = "")
        {
            // output thread က app ပိတ်ပြီးဆုံးချိန် call ရင် crash မဖြစ်အောင်
            if (pbarOperation == null || pbarOperation.IsDisposed || !pbarOperation.IsHandleCreated) return;
            try
            {
                if (pbarOperation.InvokeRequired)
                {
                    pbarOperation.Invoke(new Action(() => SetProgress(percent, status, speed)));
                    return;
                }

                pbarOperation.Value = Math.Min(100, Math.Max(0, percent));
                lblProgressStatus.Text = status + " (" + percent.ToString() + "%)";
                if (!string.IsNullOrEmpty(speed))
                {
                    lblSpeedBadge.Text = "Speed: " + speed;
                }
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private void ResetProgress()
        {
            SetProgress(0, "Status: Ready", "0 MB/s");
        }

        private void DgvPartitions_CellMouseDown(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0)
            {
                dgvPartitions.ClearSelection();
                dgvPartitions.Rows[e.RowIndex].Selected = true;
            }
        }

        // ================= UNIFIED SMART LOG & PROGRESS PARSER =================
        private bool ParseCleanLogAndProgress(string rawLine)
        {
            if (string.IsNullOrWhiteSpace(rawLine)) return true;

            Match mPct = Regex.Match(rawLine, @"(\d+(?:\.\d+)?)\s*%");
            Match mSpd = Regex.Match(rawLine, @"([\d\.]+\s*(?:KB/s|MB/s|GB/s|B/s))");

            if (mPct.Success)
            {
                double pct = double.Parse(mPct.Groups[1].Value, CultureInfo.InvariantCulture);
                string speed = mSpd.Success ? mSpd.Groups[1].Value : "Calculating...";
                SetProgress((int)pct, "Transferring", speed);
            }

            if (rawLine.StartsWith(".") || rawLine.Contains("For brom mode") || rawLine.Contains("For preloader mode") ||
                rawLine.Contains("Power off the phone") || rawLine.Contains("AES128 CBC") || rawLine.Contains("Patching da"))
            {
                return true;
            }

            // pmk_mtk_op.py ရဲ့ post-op boot marker တွေ — auto reboot တကယ် ဖြစ်/မဖြစ် ကို
            // exit code မဟုတ်ဘဲ device ကို စစ်ပြီး ပြန်ပေးတဲ့ အချက်အလက်။
            // Python output က ASCII သက်သက်မို့ ဒီမှာ မြန်မာလို ပြန်ရေးတယ်။
            if (rawLine.Contains("PMK_BOOT_OK"))
            {
                lastMtkBootState = "OK";
                SetProgress(100, "Booted", "Done");
                Log("[OK] Phone is now in Android ✓", Color.LightGreen);
                return true;
            }
            if (rawLine.Contains("PMK_BOOT_FASTBOOT"))
            {
                lastMtkBootState = "FASTBOOT";
                Log("[i] Phone is in fastboot mode - sent `fastboot reboot` from the PC.", Color.Cyan);
                return true;
            }
            if (rawLine.Contains("PMK_MTK_STILL_ATTACHED"))
            {
                // UI မှာ စာသား မပြတော့ဘူး — အောက်က PMK_BOOT_MANUAL က အနှစ်ချုပ် ညွှန်ကြားချက်
                // တစ်လိုင်းတည်း ပြတယ် (log မရှုပ်အောင်)။
                lastMtkBootState = "ATTACHED";
                return true;
            }
            if (rawLine.Contains("PMK_BOOT_MANUAL"))
            {
                if (lastMtkBootState != "ATTACHED") lastMtkBootState = "MANUAL";
                Log("[i] Android did not start automatically - unplug USB, then hold POWER ~10-15s.", Color.Orange);
                return true;
            }
            if (rawLine.Contains("PMK_PARTIAL_ERASE"))
            {
                // (config စတဲ့ partition မရှိတာ) — log မှာ မပြတော့ဘူး
                return true;
            }
            if (rawLine.Contains("PMK_DA_REBOOT_FAILED"))
            {
                lastMtkBootState = "DA_FAILED";
                Log("[x] The DA did not accept the reboot command - unplug/replug USB and retry.", Color.Red);
                return true;
            }
            if (rawLine.Contains("PMK_MTK_NOT_BROM"))
            {
                lastMtkBootState = "NOT_BROM";
                Log("[!] Phone is not in BROM mode (currently preloader/DA mode) - handshake only works in BROM mode.", Color.OrangeRed);
                Log("[i] Power off the phone (hold POWER ~10s), then hold Vol+ and Vol- while reconnecting USB.", Color.Orange);
                return true;
            }
            if (rawLine.Contains("PMK_TRANSPORT_SERIAL"))
            {
                string trPort = rawLine.Substring(rawLine.IndexOf("PMK_TRANSPORT_SERIAL") + "PMK_TRANSPORT_SERIAL".Length).Trim();
                Log("[i] Transport: serial/VCOM" + (trPort.Length > 0 ? " (" + trPort + ")" : "") + " - using the COM port (same path as UnlockTool)", Color.Cyan);
                return true;
            }
            if (rawLine.Contains("PMK_TRANSPORT_USB"))
            {
                Log("[i] Transport: USB (libusb/WinUSB)", Color.Cyan);
                return true;
            }
            if (rawLine.Contains("PMK_KICK_SENT"))
            {
                Log("[i] Phone is sitting in BROM - sent BROM JUMP_BL to continue booting.", Color.Cyan);
                return true;
            }
            if (rawLine.Contains("PMK_BOOT_LIKELY"))
            {
                lastMtkBootState = "LIKELY";
                SetProgress(100, "Booted", "Done");
                Log("[OK] Phone left USB - Android is booting (check the screen).", Color.LightGreen);
                return true;
            }
            if (rawLine.Contains("PMK_MTK_BAD_DRIVER"))
            {
                lastMtkBootState = "BAD_DRIVER";
                Log("[!] The phone (BROM) is bound to the MTK serial driver - libusb cannot use it.", Color.OrangeRed);
                Log("[i] Use Zadig to switch 'MediaTek USB Port' to WinUSB (or libusbK), then retry.", Color.Orange);
                return true;
            }
            if (rawLine.Contains("PMK_DA_CRASH"))
            {
                lastMtkBootState = "CRASH";
                Log("[x] mtkclient crashed natively in the USB stage (libusb / 0xC0000005).", Color.Red);
                Log("[i] Power off the phone (10s), reconnect in BROM mode (hold Vol+ & Vol-), then retry.", Color.Orange);
                return true;
            }
            if (rawLine.Contains("PMK_NEED_REPLUG"))
            {
                Log("Unplug and replug the phone (fresh BROM mode), then press this button again.", Color.Orange);
                return true;
            }

            if (rawLine.Contains("Device detected :)"))
            {
                // edl.py (QC) နဲ့ mtkclient က "Device detected :)" တူတယ် — task context ပေါ်
                // မူတည်ပြီး စာသားခွဲပြ (QC log မှာ "Waiting for device" ဆိုတာ ရှုပ်တယ်)
                if (currentTaskTitle.Contains("Qualcomm", StringComparison.OrdinalIgnoreCase) ||
                    currentTaskTitle.Contains("(QC)", StringComparison.Ordinal))
                {
                    Log("[+] Firehose: device connected.", Color.LightGreen);
                }
                else
                {
                    Log("[OK] Waiting for device... Connected", Color.LightGreen);
                }
                SetProgress(20, "Connected");
            }
            else if (rawLine.Contains("Waiting for PreLoader VCOM") && !rawLine.Contains("...."))
            {
                if (lastDeviceState != "WAITING_BROM")
                {
                    lastDeviceState = "WAITING_BROM";
                    Log("Initializing usb... OK", Color.Cyan);
                    Log("Waiting for device... (BROM/preloader mode - power off the phone, hold Vol+/Vol- and connect USB)", Color.Orange);
                }
            }
            else if (rawLine.Contains("CPU:"))
            {
                string cpuText = rawLine.Substring(rawLine.IndexOf("CPU:")).Trim().Replace("\t", " ");
                Log("[+] " + cpuText, Color.Cyan);

                Match mCpu = Regex.Match(cpuText, @"\b(MT\d{4})");
                if (mCpu.Success) detectedCpuPlatform = mCpu.Groups[1].Value;
                SetProgress(30, "CPU OK");
            }
            else if (rawLine.Contains("HW code:"))
            {
                Log("[+] Hardware Code   : " + rawLine.Split(':')[1].Trim(), Color.LightGreen);
            }
            else if (rawLine.Contains("ME_ID:"))
            {
                Log("[+] MEID            : " + rawLine.Split(':')[1].Trim(), Color.Gainsboro);
            }
            else if (rawLine.Contains("SOC_ID:"))
            {
                Log("[+] SOC ID          : " + rawLine.Split(':')[1].Trim(), Color.Gainsboro);
            }
            else if (rawLine.Contains("EMMC ID:"))
            {
                detectedStorageKind = "EMMC";
                Log("  Storage        : eMMC - CID : " + rawLine.Split(':')[1].Trim(), Color.LightSkyBlue);
            }
            else if (rawLine.Contains("UFS ID:") || rawLine.Contains("UFS FWVer:") || rawLine.Contains("UFS CID:"))
            {
                detectedStorageKind = "UFS";
                string val = rawLine.Contains(":") ? rawLine.Substring(rawLine.IndexOf(':') + 1).Trim() : "";
                Log("  Storage        : UFS - " + (rawLine.Contains("FWVer") ? "FWVer : " + val : "CID : " + val), Color.LightSkyBlue);
            }
            else if (rawLine.Contains("UFS Serial:") || rawLine.Contains("UFS LU0 Size:") || rawLine.Contains("UFS LU1 Size:") || rawLine.Contains("UFS LU2 Size:"))
            {
                string val = rawLine.Contains(":") ? rawLine.Substring(rawLine.IndexOf(':') + 1).Trim() : "";
                Log("  " + (rawLine.Contains("Serial") ? "UFS Serial     : " : (rawLine.Contains("LU0") ? "UFS LU0 Size   : " : (rawLine.Contains("LU1") ? "UFS LU1 Size   : " : "UFS LU2 Size   : "))) + val, Color.LightSkyBlue);
            }
            else if (rawLine.Contains("UFS MID:"))
            {
                string val = rawLine.Contains(":") ? rawLine.Substring(rawLine.IndexOf(':') + 1).Trim() : "";
                Log("  UFS MID        : " + val, Color.LightSkyBlue);
            }
            else if (rawLine.Contains("EMMC USER Size:"))
            {
                string sizeHex = rawLine.Split(':')[1].Trim();
                Log("[+] User Area Size  : " + FormatBytes(sizeHex) + " (" + sizeHex + ")", Color.LightSkyBlue);
            }
            else if (rawLine.Contains("Bypassing security") || rawLine.Contains("Done sending payload"))
            {
                Log("[OK] Bypassing authentication", Color.LightGreen);
                SetProgress(45, "Bypass OK");
            }
            else if (rawLine.Contains("Successfully uploaded stage 2") || rawLine.Contains("DA Extensions successfully added"))
            {
                Log("[OK] Sending Download-Agent... OK", Color.LightGreen);
                Log("[OK] Syncing with device... OK", Color.LightGreen);
                SetProgress(60, "DA Loaded");
            }
            else if (rawLine.Contains("Successfully wrote seccfg"))
            {
                Log("[+] Security Config : seccfg Written Successfully (OK)", Color.LightGreen);
                SetProgress(100, "Completed", "0 MB/s");
            }
            else if (rawLine.Contains("GPT Table:"))
            {
                Log("[OK] Reading partition info... OK", Color.LightGreen);
                SetProgress(90, "GPT Verified");
            }

            Match match = Regex.Match(rawLine.Trim(), @"^([a-zA-Z0-9_\-]+):\s+Offset\s+(0x[0-9a-fA-F]+),\s+Length\s+(0x[0-9a-fA-F]+)");
            if (match.Success)
            {
                AddPartitionRow(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value);
                return true;
            }

            // edl.py (Qualcomm) ရဲ့ printgpt format — table နဲ့ ပြတယ်:
            //   boot            \t00080000\t00400000\t0x0/0x0/0x0\t0
            // (offset/length တွေက 0x မပါဘဲ hex သက်သက်)
            Match qcMatch = Regex.Match(rawLine, @"^([a-zA-Z0-9_\-]+)[ \t]*\t([0-9A-Fa-f]{1,16})\t([0-9A-Fa-f]{1,16})\t");
            if (qcMatch.Success)
            {
                AddPartitionRow(qcMatch.Groups[1].Value,
                                "0x" + qcMatch.Groups[2].Value.TrimStart('0').PadLeft(1, '0'),
                                "0x" + qcMatch.Groups[3].Value.TrimStart('0').PadLeft(1, '0'));
                return true;
            }

            return false;   // မမြင်ရတဲ့ line → raw passthrough (showRawOutput) က ပြမယ်
        }

        // Partition row တစ်ခု ထည့်တယ် — mtkclient (MediaTek) နဲ့ edl.py (Qualcomm) နှစ်မျိုးလုံးအတွက်
        private void AddPartitionRow(string partName, string offset, string lengthHex)
        {
            if (string.IsNullOrWhiteSpace(partName)) return;

            string humanSize = FormatBytes(lengthHex);
            // list mutation ကို UI thread ပဲ လုပ် — output thread က Add လုပ်နေစဉ် UI က
            // enumerate လုပ်ရင် "Collection was modified" crash ဖြစ်နိုင်လို့ invoke ထဲမှာပဲ ထည့်
            if (dgvPartitions.InvokeRequired)
            {
                dgvPartitions.Invoke(new Action(() =>
                {
                    loadedPartitions.Add(new PartitionMeta { Name = partName, HumanSize = humanSize, Offset = offset, LengthHex = lengthHex });
                    dgvPartitions.Rows.Add(false, partName, humanSize, offset, "", "");
                    ApplyPartitionFilter();
                }));
            }
            else
            {
                loadedPartitions.Add(new PartitionMeta { Name = partName, HumanSize = humanSize, Offset = offset, LengthHex = lengthHex });
                dgvPartitions.Rows.Add(false, partName, humanSize, offset, "", "");
                ApplyPartitionFilter();
            }
        }

        private async Task<bool> ExecuteCommandCleanAsync(string fileName, string arguments, string taskTitle,
            bool clearPartitions = false, bool showRawOutput = false, bool quiet = false,
            Func<List<string>, bool> tolerateRaw = null, int timeoutSec = 0, bool timeoutMeansSuccess = false)
        {
            try { arguments = await BindDeviceArgumentsAsync(fileName, arguments); }
            catch (Exception ex)
            {
                workflowFailed = true;
                if (rebootWaitActive) return false;
                Log("[FAIL] " + ex.Message, Color.Red);
                return false;
            }
            bool ok = await ExecuteCommandCoreAsync(fileName, arguments, taskTitle, clearPartitions, showRawOutput, quiet, tolerateRaw, timeoutSec, timeoutMeansSuccess);
            if (flashWorkflowContext.Value && !ok) workflowFailed = true;
            return ok;
        }

        private async Task<bool> ExecuteCommandCoreAsync(string fileName, string arguments, string taskTitle,
            bool clearPartitions, bool showRawOutput, bool quiet, Func<List<string>, bool> tolerateRaw = null,
            int timeoutSec = 0, bool timeoutMeansSuccess = false)
        {
            if (isTaskRunning || (flashWorkflowRunning && !flashWorkflowContext.Value))
            {
                Log("[!] Another operation is still running - wait for it to finish or press STOP.", Color.OrangeRed);
                return false;
            }

            if (flashWorkflowContext.Value && stopRequested) return false;
            isTaskRunning = true;
            if (!flashWorkflowContext.Value) stopRequested = false;
            currentTaskTitle = taskTitle;
            // row တွေ snapshot ယူပြီး clear — command က row အသစ် မပြန်ရင် ပြန်ထိုးပေးတယ်
            // (flash/preflight fail ဖြစ်ရင် ယခင် partition list ပျောက်မသွားစေရ)
            List<PartitionMeta> partitionSnapshot = clearPartitions ? new List<PartitionMeta>(loadedPartitions) : null;
            if (clearPartitions) ClearPartitionData();

            void RestorePartitionRows()
            {
                if (partitionSnapshot == null || partitionSnapshot.Count == 0 || loadedPartitions.Count > 0) return;
                foreach (PartitionMeta pm in partitionSnapshot) AddPartitionRow(pm.Name, pm.Offset, pm.LengthHex);
                partitionSnapshot = null;
                Log("[i] GPT/size ကနေ row မရလို့ ယခင် partition list ကို ပြန်ပြတယ်.", Color.Gray);
            }
            if (flashWorkflowContext.Value) workflowDidOp = true;

            if (!quiet)
            {
                Log("========================================================", Color.FromArgb(0, 180, 255));
                Log("           PMK TOOL - " + taskTitle.ToUpper() + "             ", Color.White);
                Log("========================================================", Color.FromArgb(0, 180, 255));
                Log("[*] Initializing Task...", Color.Orange);
            }
            SetProgress(10, "Initializing");

            int exitCode = -1;
            bool timedOut = false;
            string failure = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Raw output ကို ဖမ်းထားတယ် — parser က unknown line တွေကို ဖျောက်တာမို့
            // ပြဿနာ (native crash / error message) တွေ့ရင် အဲဒီ line တွေ ပြန်ပြဖို့နဲ့
            // ဖိုင်ထဲ သိမ်းဖို့ လိုတယ် (mtkclient ရဲ့ error line တွေက mapped list မှာ မပါဘူး)။
            object rawLock = new object();
            List<string> rawTail = new List<string>();
            string rawLogPath = Path.Combine(Path.GetTempPath(), "pmk_last_command.log");

            try
            {
                await Task.Run(() =>
                {
                    try
                    {
                        ProcessStartInfo psi = new ProcessStartInfo
                        {
                            FileName = ResolveToolPath(fileName),
                            Arguments = arguments,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            // Python child processes ကို UTF-8 နဲ့ ဖတ်တယ် — mtkclient ရဲ့ output ထဲမှာ
                            // non-ASCII character ပါလာရင် OEM codepage နဲ့ ဖတ်ရင် ပျက်တတ်တယ်။
                            StandardOutputEncoding = Encoding.UTF8,
                            StandardErrorEncoding = Encoding.UTF8
                        };

                        using (Process process = new Process { StartInfo = psi })
                        {
                            activeProcess = process;

                            Action<string, bool> onData = (line, isErr) =>
                            {
                                lock (rawLock)
                                {
                                    rawTail.Add((isErr ? "[err] " : "") + line);
                                    if (rawTail.Count > 3000) rawTail.RemoveAt(0);
                                }

                                // ========== Commercial Tool Log Style Formatting ==========

                                // ၁။ ဖိုင် ရေးသွင်းနေသည့် log ကို သန့်သန့်လေး ပြောင်းလဲခြင်း
                                if (line.Contains("In handleProgram('"))
                                {
                                    int start = line.IndexOf("In handleProgram('") + 18;
                                    int end = line.IndexOf("')", start);
                                    if (start > 17 && end > start)
                                    {
                                        string fileNameOnly = line.Substring(start, end - start);
                                        Log($" Flashing [ {fileNameOnly} ] ... Ok", Color.White);
                                    }
                                }
                                // ၂။ Patching လုပ်သည့် အပိုင်းကို ဖမ်းပြခြင်း
                                else if (line.Contains("In handlePatch("))
                                {
                                    Log(" Patching patch0.xml ... Ok", Color.LightGreen);
                                }
                                // ၃။ Percent ကို Textbox ထဲ မထည့်ဘဲ ProgressBar သို့သာ တိုက်ရိုက် Update လုပ်ခြင်း
                                else if (line.Contains("{percent files transferred"))
                                {
                                    Match m = Regex.Match(line, @"(\d+(?:\.\d+)?)\s*%");
                                    if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double p))
                                    {
                                        SetProgress((int)p, "Flashing", $"{p:F1}%");
                                    }
                                }
                                // ၄။ Device Information ပိုင်း ဖမ်းပြခြင်း
                                else if (line.Contains("TARGET SAID:") && line.Contains("storage_type"))
                                {
                                    Log(" Connecting to firehose mode... Ok", Color.LightGreen);
                                }
                                // ==========================================================

                                bool handled = ParseCleanLogAndProgress(line);

                                // ပုံမှန် raw output ရှုပ်ရှုပ်တွေကို မပြတော့ပါ
                                if (!handled && showRawOutput && !string.IsNullOrWhiteSpace(line))
                                    Log("    " + line, Color.FromArgb(150, 150, 150));
                            };

                            process.OutputDataReceived += (s, args) => { if (args.Data != null) onData(args.Data, false); };
                            process.ErrorDataReceived += (s, args) => { if (args.Data != null) onData(args.Data, true); };

                            if (stopRequested) return;
                            process.Start();
                            if (stopRequested) { try { process.Kill(true); } catch (InvalidOperationException) { } }
                            process.BeginOutputReadLine();
                            process.BeginErrorReadLine();
                            // timeoutSec > 0 ဆို child process ကို စောင့်ပြီး ကြာလွန်းရင် kill —
                            // (edl.py/mtkclient က device reboot ပြီးမှ hang နေတတ်တယ် → UI အမြဲတမ်း lock)
                            if (timeoutSec > 0)
                            {
                                if (!process.WaitForExit(timeoutSec * 1000))
                                {
                                    timedOut = true;
                                    try { process.Kill(entireProcessTree: true); } catch { }
                                    try { process.WaitForExit(5000); } catch { }
                                }
                            }
                            else
                            {
                                // timeoutSec=0 = unlimited — ဒါပေမဲ့ hang child အတွက် 6 နာရီ safety cap ထား
                                // (STOP က activeProcess ကို kill လို့ ချက်ချင်းထွက်တယ်; legit flash/backup က ဒါထက် မကြာဘူး)
                                if (!process.WaitForExit(6 * 60 * 60 * 1000))
                                {
                                    timedOut = true;
                                    try { process.Kill(entireProcessTree: true); } catch { }
                                    try { process.WaitForExit(5000); } catch { }
                                }
                            }
                            exitCode = process.ExitCode;
                        }
                    }
                    catch (Exception ex)
                    {
                        failure = ex.Message;
                    }
                    finally
                    {
                        activeProcess = null;
                    }
                });
            }
            finally
            {
                isTaskRunning = false;
            }

            sw.Stop();
            if (!quiet)
                Log("--------------------------------------------------------", Color.FromArgb(0, 180, 255));

            // ဒီ command ရဲ့ raw output အားလုံးကို ဖိုင်ထဲ သိမ်းတယ် (debug အတွက်)။
            List<string> rawSnapshot;
            lock (rawLock) { rawSnapshot = new List<string>(rawTail); }
            try
            {
                List<string> rawFile = new List<string>();
                rawFile.Add("=== " + taskTitle + " | " + fileName + " " + arguments);
                rawFile.Add("");
                rawFile.AddRange(rawSnapshot);
                File.WriteAllLines(rawLogPath, rawFile);
            }
            catch (Exception ex)
            {
                Log("[!] Raw log write failed: " + ex.Message, Color.Orange);
            }

            // မအောင်မြင်ရင် raw line နောက်ဆုံး 30 လိုင်း + log ဖိုင် path ကို ပြထားတယ်။
            void DumpRawOutput(string note)
            {
                Log(note, Color.Gray);
                int from = Math.Max(0, rawSnapshot.Count - 30);
                for (int i = from; i < rawSnapshot.Count; i++)
                    Log("   " + rawSnapshot[i], Color.FromArgb(150, 150, 150));
                Log("   (full raw log: " + rawLogPath + ")", Color.Gray);
            }

            // Native crash (access violation) လို exit code တွေကို ဘာသာပြန်ပြတယ်။
            string ExitCodeNote(int code)
            {
                switch (code)
                {
                    case -1073741819: return " (0xC0000005 = ACCESS_VIOLATION — libusb/USB driver အဆင့်မှာ crash)";
                    case -1073740791: return " (0xC0000409 = stack buffer overrun)";
                    case -1073741510: return " (0xC000013A = process ကို ရပ်လိုက်တာ)";
                    case -1073740940: return " (0xC0000374 = heap corruption)";
                    default: return "";
                }
            }

            if (failure != null)
            {
                RestorePartitionRows();
                SetProgress(0, "Failed", "0 MB/s");
                Log("[✘] " + taskTitle + " could not be started: " + failure, Color.Red);
                Log("[*] Check that " + fileName + " is installed and reachable.", Color.Orange);
                if (rawSnapshot.Count > 0) DumpRawOutput("[*] Raw command output (last 30 lines):");
                if (!quiet) Log("========================================================", Color.FromArgb(0, 180, 255));
                LogHistory(taskTitle + " (could not start)", false, Math.Round(sw.Elapsed.TotalSeconds));
                return false;
            }

            if (stopRequested)
            {
                RestorePartitionRows();
                Log("[!] " + taskTitle + " was stopped before it finished.", Color.OrangeRed);
                if (!quiet) Log("========================================================", Color.FromArgb(0, 180, 255));
                LogHistory(taskTitle + " (stopped)", false, Math.Round(sw.Elapsed.TotalSeconds));
                return false;
            }

            // timeout ဖြစ်ပေမဲ့ caller က "device drop = done" သတ်မှတ်ထားတဲ့ command (auto reboot) → success
            if (timedOut && timeoutMeansSuccess)
            {
                RestorePartitionRows();
                SetProgress(100, "Completed", "Done");
                Log("[i] " + taskTitle + " — " + timeoutSec + "s timeout; process killed (device rebooted/dropped, treated as OK).", Color.Gray);
                LogHistory(taskTitle + " (timeout)", true, Math.Round(sw.Elapsed.TotalSeconds));
                return true;
            }

            if (exitCode == 0)
            {
                RestorePartitionRows();
                SetProgress(100, "Completed", "Done");
                if (!quiet)
                {
                    Log("[OK] " + taskTitle + " - Completed Successfully!", Color.LightGreen);
                    Log("Elapsed time : " + Math.Round(sw.Elapsed.TotalSeconds) + " seconds", Color.Gray);
                    Log("========================================================", Color.FromArgb(0, 180, 255));
                }
                LogHistory(taskTitle, true, Math.Round(sw.Elapsed.TotalSeconds));
                return true;
            }

            // fail ဖြစ်ပေမဲ့ caller က tolerate လုပ်ထားတဲ့ error pattern (ဥပမာ partition မရှိ) → skip အဖြစ် သတ်မှတ်
            if (exitCode != 0 && tolerateRaw != null && tolerateRaw(rawSnapshot))
            {
                RestorePartitionRows();
                SetProgress(100, "Skipped", "Done");
                Log("[i] " + taskTitle + " — expected condition (partition not present), skipped (OK).", Color.Gray);
                if (!quiet)
                {
                    Log("Elapsed time : " + Math.Round(sw.Elapsed.TotalSeconds) + " seconds", Color.Gray);
                    Log("========================================================", Color.FromArgb(0, 180, 255));
                }
                LogHistory(taskTitle + " (skipped)", true, Math.Round(sw.Elapsed.TotalSeconds));
                return true;
            }

            if (timedOut)
            {
                RestorePartitionRows();
                SetProgress(0, "Failed", "0 MB/s");
                Log("[FAIL] " + taskTitle + " — timed out after " + (timeoutSec > 0 ? timeoutSec + "s" : "6h hard cap") + " (process killed).", Color.OrangeRed);
                DumpRawOutput("[*] Raw command output (last 30 lines):");
                if (!quiet)
                {
                    Log("Elapsed time : " + Math.Round(sw.Elapsed.TotalSeconds) + " seconds", Color.Gray);
                    Log("========================================================", Color.FromArgb(0, 180, 255));
                }
                LogHistory(taskTitle, false, Math.Round(sw.Elapsed.TotalSeconds));
                return false;
            }

            RestorePartitionRows();
            SetProgress(0, "Failed", "0 MB/s");
            Log("[FAIL] " + taskTitle + " (exit code " + exitCode + ")" + ExitCodeNote(exitCode), Color.OrangeRed);
            DumpRawOutput("[*] Raw command output (last 30 lines):");
            if (!quiet)
            {
                Log("Elapsed time : " + Math.Round(sw.Elapsed.TotalSeconds) + " seconds", Color.Gray);
                Log("========================================================", Color.FromArgb(0, 180, 255));
            }
            LogRecoveryHints(taskTitle, exitCode, rawSnapshot);
            LogHistory(taskTitle, false, Math.Round(sw.Elapsed.TotalSeconds));
            return false;
        }

        // ================= OPERATION HISTORY (timestamped) =================
        private string HistoryFile
        {
            get { return ShopServices.DataPath("pmk_history.log"); }
        }

        private void LogHistory(string operation, bool success, double seconds)
        {
            try
            {
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\t" +
                              (success ? "OK  " : "FAIL") + "\t" +
                              Math.Round(seconds) + "s\t" + operation;
                File.AppendAllText(HistoryFile, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("LogHistory failed: " + ex.Message);
            }
        }

        // ================= ERROR RECOVERY HINTS =================
        private void LogRecoveryHints(string taskTitle, int exitCode, List<string> rawTail)
        {
            string joined = string.Join("\n", rawTail ?? new List<string>());
            bool hint = false;

            void Hint(string msg)
            {
                if (hint) return;
                hint = true;
                Log("[i] Recovery: " + msg, Color.Cyan);
            }

            if (joined.Contains("ACCESS_VIOLATION") || exitCode == -1073741819)
                Hint("USB driver / libusb crash — USB port ပြောင်း၊ cable တို/မကောင်း စစ်၊ WinUSB filter reinstall စမ်းပါ။");
            else if (joined.Contains("Permission") || joined.Contains("access denied") || exitCode == 5)
                Hint("Permission denied — tool ကို Run as administrator နဲ့ ဖွင့်ပါ သို့မဟုတ် antivirus က ပိတ်နေလား စစ်ပါ။");
            else if (joined.Contains("No such file") || joined.Contains("not found") || joined.Contains("找不到") || exitCode == 2)
                Hint("File/tool မတွေ့ပါ — bundled tool path နဲ့ firmware folder ပြန်စစ်ပါ။");
            else if (joined.Contains("BROM") || joined.Contains("preloader") || joined.Contains("handshake"))
                Hint("BROM handshake fail — ဖုန်းကို power off (~10s) လုပ်ပြီး Vol+ & Vol- နှိပ်ကာ USB ပြန်တပ်ပါ။");
            else if (joined.Contains("device not found") || joined.Contains("no device") || joined.Contains("Waiting for device"))
                Hint("Device မချိတ်မိပါ — cable/driver စစ်၊ Scan Port နှိပ်ပြီး ပြန်ကြိုးစားပါ။");
            else if (joined.Contains("verify") || joined.Contains("AVB") || joined.Contains("orange state"))
                Hint("AVB/verification — Orange State Fix သို့ DM Fix ကို စမ်းကြည့်ပါ (vbmeta disabled flags)။");
            else if (joined.Contains("userdata") || joined.Contains("encrypt") || joined.Contains("FRP"))
                Hint("Data/FRP lock — FRP Remove သို့ Userlock Reset ကို owner consent နဲ့ စမ်းပါ။");
            else if (exitCode != 0)
                Hint("Command fail — raw log (pmk_last_command.log) ကို ဖတ်ပြီး error line ကို ရှာပါ၊ မရရင် STOP → Scan Port → retry။");
        }

        // ================= PARTITION SIZE WARNING =================
        private bool WarnIfImageTooLarge(string partition, string filePath)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return true;
                long imgSize = new FileInfo(filePath).Length;
                var p = loadedPartitions.FirstOrDefault(x => x.Name.Equals(partition, StringComparison.OrdinalIgnoreCase));
                if (p == null) return true;
                long partSize;
                try { partSize = ReviewSafety.ParseSize(p.LengthHex); }
                catch { return true; }
                if (imgSize > partSize)
                {
                    string imgHuman = FormatBytesLong(imgSize);
                    string partHuman = FormatBytes(p.LengthHex);
                    DialogResult r = MessageBox.Show(
                        "Image က partition ထက် ကြီ်နေတယ်!\n\n" +
                        "• Partition : " + partition + " (" + partHuman + ")\n" +
                        "• Image    : " + imgHuman + "\n\n" +
                        "ဒီအတိုင်း write ရင် data corruption / brick ဖြစ်နိုင်တယ်။\n" +
                        "ဆက်လုပ်မလား?",
                        "Partition Size Warning", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (r != DialogResult.Yes) return false;
                }
            }
            catch (Exception ex)
            {
                // size check မလုပ်နိုင်ရင် warn မပြဘဲ pass — log ထား
                Debug.WriteLine("WarnIfImageTooLarge: " + ex.Message);
            }
            return true;
        }

        // ================= DRIVER HELPER =================
        // Connected device VID တွေ ဖတ်ပြီး ဘယ် driver လို/လိုမလို ပြတယ်။
        // INF folder ပေးရင် pnputil နဲ့ install လုပ်ပေးတယ် (bundled INF မပါလည်း သုံးလို့ရ)။
        private void ShowDriverHelper()
        {
            using (var dlg = new Form())
            {
                dlg.Text = "Driver Helper";
                dlg.Size = new Size(720, 520);
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;
                // theme-aware (light: soft; dark: original)
                dlg.BackColor = lightTheme ? Color.FromArgb(222, 228, 240) : Color.FromArgb(18, 20, 24);
                dlg.ForeColor = lightTheme ? Color.FromArgb(35, 38, 48) : Color.White;

                var lblTitle = new Label
                {
                    Text = "USB / COM driver status — known VIDs (MediaTek, Qualcomm, Samsung, Unisoc, HiSilicon)",
                    Location = new Point(12, 10),
                    AutoSize = true,
                    ForeColor = lightTheme ? Color.FromArgb(58, 102, 208) : Color.FromArgb(86, 145, 250),
                    Font = new Font("Segoe UI", 9f, FontStyle.Bold)
                };

                var lst = new ListBox
                {
                    Location = new Point(12, 36),
                    Size = new Size(680, 200),
                    BackColor = lightTheme ? Color.FromArgb(243, 246, 252) : Color.FromArgb(38, 42, 50),
                    ForeColor = lightTheme ? Color.FromArgb(35, 38, 48) : Color.Gainsboro,
                    Font = new Font("Consolas", 9f),
                    BorderStyle = BorderStyle.FixedSingle
                };

                var outLog = new TextBox
                {
                    Location = new Point(12, 244),
                    Size = new Size(680, 160),
                    Multiline = true,
                    ScrollBars = ScrollBars.Vertical,
                    ReadOnly = true,
                    BackColor = lightTheme ? Color.FromArgb(240, 244, 251) : Color.FromArgb(24, 27, 33),
                    ForeColor = lightTheme ? Color.FromArgb(35, 38, 48) : Color.FromArgb(200, 220, 240),
                    Font = new Font("Consolas", 8.5f),
                    BorderStyle = BorderStyle.FixedSingle
                };

                var btnScan = Create3DButton("🔍 Scan USB Devices", 12, 414, 160, 34, ButtonTheme.Cyan);
                var btnInstall = Create3DButton("📦 Install INF Folder…", 180, 414, 170, 34, ButtonTheme.Green);
                var btnDevMgr = Create3DButton("⚙ Device Manager", 358, 414, 150, 34, ButtonTheme.Cyan);
                var btnClose = Create3DButton("Close", 610, 414, 82, 34, ButtonTheme.Orange);

                void Append(string line)
                {
                    if (outLog.TextLength > 0) outLog.AppendText(Environment.NewLine);
                    outLog.AppendText(line);
                }

                async void Scan()
                {
                    btnScan.Enabled = false;
                    lst.Items.Clear();
                    outLog.Clear();
                    Append("[*] Scanning PnP devices for known phone VIDs...");
                    try
                    {
                        // VID → friendly name map (unlock/flash modes)
                        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["0E8D"] = "MediaTek (BROM / Preloader / COM)",
                            ["05C6"] = "Qualcomm (EDL 9008 / DIAG)",
                            ["9008"] = "Qualcomm EDL 9008 (composite)",
                            ["04E8"] = "Samsung (Kies / Download / ADB)",
                            ["1F8A"] = "Unisoc / Spreadtrum (spd_dump)",
                            ["1782"] = "Unisoc / Spreadtrum (alt)",
                            ["12D1"] = "Huawei / HiSilicon (HUAWEI USB COM)",
                            ["2A96"] = "MediaTek (preloader alt)",
                            ["0BB4"] = "HTC / older MTK",
                            ["2717"] = "Xiaomi (ADB/Fastboot)",
                            ["18D1"] = "Google / generic ADB & Fastboot",
                            ["0FCE"] = "Sony",
                            ["1004"] = "LG",
                            ["0421"] = "Nokia (MTK/Unisoc)",
                            ["22B8"] = "Motorola",
                            ["0525"] = "Linux Function / RNDIS (some SPD)"
                        };

                        string ps =
                            "Get-PnpDevice -ErrorAction SilentlyContinue | " +
                            "Where-Object { $_.InstanceId -match 'VID_[0-9A-F]{4}' } | " +
                            "Select-Object -ExpandProperty InstanceId";
                        string res = await ExecuteCommandQuickAsync("powershell.exe",
                            "-NoProfile -Command \"" + ps.Replace("\"", "\\\"") + "\"");

                        var hits = new List<string>();
                        var seen = new HashSet<string>();
                        foreach (string line in (res ?? "").Split('\n'))
                        {
                            string id = line.Trim();
                            var m = Regex.Match(id, @"VID_([0-9A-Fa-f]{4})", RegexOptions.IgnoreCase);
                            if (!m.Success) continue;
                            string vid = m.Groups[1].Value.ToUpperInvariant();
                            if (!known.ContainsKey(vid)) continue;
                            string key = vid + "|" + id;
                            if (!seen.Add(key)) continue;
                            hits.Add(vid + "  " + known[vid]);
                            Append("  [FOUND] VID_" + vid + "  " + known[vid]);
                            Append("           " + id);
                        }

                        if (hits.Count == 0)
                        {
                            Append("[i] No known phone USB IDs present.");
                            Append("    Phone ချိတ်ပြီး Scan ထပ်နှိပ်ပါ — သို့မဟုတ် Driver ကြိုတင် install ထားပါ။");
                            lst.Items.Add("(no known phone VID connected)");
                        }
                        else
                        {
                            foreach (string h in hits.Distinct()) lst.Items.Add(h);
                            Append("[OK] " + hits.Count + " known device interface(s). If yellow ! in Dev Manager → install INF folder.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Append("[FAIL] Scan: " + ex.Message);
                    }
                    finally { btnScan.Enabled = true; }
                }

                btnScan.Click += (s, e) => Scan();
                btnDevMgr.Click += (s, e) =>
                {
                    Process.Start(new ProcessStartInfo { FileName = "devmgmt.msc", UseShellExecute = true });
                };
                btnInstall.Click += async (s, e) =>
                {
                    using (var fbd = new FolderBrowserDialog
                    {
                        Description = "Select folder containing driver .inf files (search recursive)"
                    })
                    {
                        if (fbd.ShowDialog() != DialogResult.OK) return;
                        btnInstall.Enabled = false;
                        Append("[*] pnputil /add-driver ... /install (recursive) — " + fbd.SelectedPath);
                        try
                        {
                            var psi = new ProcessStartInfo
                            {
                                FileName = "pnputil.exe",
                                Arguments = "/add-driver \"" + Path.Combine(fbd.SelectedPath, "*.inf") +
                                            "\" /subdirs /install",
                                UseShellExecute = false,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                                CreateNoWindow = true
                            };
                            using var p = Process.Start(psi);
                            // ReadToEnd/WaitForExit က unbounded — pnputil hang ရင် button အမြဲ disabled ဖြစ်မသွားအောင်300s cap
                            var pnOutTask = p.StandardOutput.ReadToEndAsync();
                            var pnErrTask = p.StandardError.ReadToEndAsync();
                            using (var pnCts = new CancellationTokenSource(TimeSpan.FromSeconds(300)))
                            {
                                try { await p.WaitForExitAsync(pnCts.Token); }
                                catch (OperationCanceledException)
                                {
                                    try { p.Kill(entireProcessTree: true); } catch { }
                                    Append("[!] pnputil — 300s timeout, process killed.");
                                    return;
                                }
                            }
                            string stdout = await pnOutTask;
                            string stderr = await pnErrTask;
                            foreach (string ln in (stdout + "\n" + stderr).Split('\n'))
                            {
                                string t = ln.Trim();
                                if (t.Length > 0) Append(t);
                            }
                            Append(p.ExitCode == 0
                                ? "[OK] pnputil finished (exit 0). Replug phone and Scan again."
                                : "[!] pnputil exit " + p.ExitCode + " — check log above (need admin for some drivers).");
                        }
                        catch (Exception ex)
                        {
                            Append("[FAIL] " + ex.Message);
                        }
                        finally { btnInstall.Enabled = true; }
                    }
                };
                btnClose.Click += (s, e) => dlg.Close();

                dlg.Controls.AddRange(new Control[] { lblTitle, lst, outLog, btnScan, btnInstall, btnDevMgr, btnClose });
                dlg.Shown += (s, e) => Scan();
                dlg.ShowDialog(this);
            }
        }

        // ================= DEVICE MODEL WHITELIST =================
        private bool IsKnownModel(string model)
        {
            if (string.IsNullOrWhiteSpace(model)) return true;
            EnsureDeviceDbLoaded();
            if (deviceDb == null) return true;
            foreach (var list in deviceDb.Values)
                foreach (var dp in list)
                    if (!string.IsNullOrEmpty(dp.Model) &&
                        dp.Model.IndexOf(model, StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
            return false;
        }

        private void WarnUnknownModel(string model)
        {
            if (string.IsNullOrWhiteSpace(model) || IsKnownModel(model)) return;
            if (!warnedModels.Add(model)) return;   // တစ်ခုတည်း တစ်ခါပဲ warn
            Log("[!] Model not in known database: " + model + " — double-check before unlock/flash.", Color.Orange);
        }

        // ================= THEME TOGGLE =================
        private void ToggleTheme()
        {
            lightTheme = !lightTheme;
            ApplyTheme();
            SaveSettings();
        }

        private void SyncPlatformBar()
        {
            if (platformBtns == null) return;
            for (int i = 0; i < platformBtns.Count; i++)
            {
                bool sel = (i == tabControl.SelectedIndex);
                if (lightTheme)
                {
                    // 3D-ish platform buttons: raised face + hard border
                    platformBtns[i].BackColor = sel ? Color.FromArgb(58, 102, 208) : Color.FromArgb(240, 244, 252);
                    platformBtns[i].ForeColor = sel ? Color.White : Color.FromArgb(40, 44, 56);
                    platformBtns[i].FlatAppearance.BorderColor = sel ? Color.FromArgb(90, 130, 235) : Color.FromArgb(145, 155, 178);
                    platformBtns[i].FlatAppearance.MouseOverBackColor = sel ? Color.FromArgb(70, 112, 225) : Color.FromArgb(248, 250, 254);
                    platformBtns[i].FlatAppearance.MouseDownBackColor = sel ? Color.FromArgb(48, 86, 180) : Color.FromArgb(228, 234, 246);
                }
                else
                {
                    platformBtns[i].BackColor = sel ? Color.FromArgb(86, 145, 250) : Color.FromArgb(38, 44, 54);
                    platformBtns[i].ForeColor = Color.White;
                    platformBtns[i].FlatAppearance.BorderColor = sel ? Color.FromArgb(129, 169, 255) : Color.FromArgb(70, 80, 95);
                }
            }
        }

        private static bool IsAccentTitle(Color c)
        {
            // section titles: badge blue (86,145,250) / violet (139,92,246) + ယခင် cyan accents
            return c.B > 200 && c.G > 60 && c.G < 210 && c.R < 175;
        }

        // 3D panel paint — light mode: vertical gradient + raised/etched bevel; dark: flat + group border
        private void EnsureThemePaint(Control c)
        {
            if (c == null || !themePaintWired.Add(c)) return;
            c.Paint += PaintThemePanel;
        }

        // Tab switch မှာ အဖြူပေါ်တတ်တာ (single-buffer erase flicker) ကာ —
        // WinForms TabControl/TabPage/TableLayoutPanel/SplitContainer က double-buffer မဟုတ်
        private static void SetDoubleBuffered(Control c)
        {
            if (c == null) return;
            try
            {
                typeof(Control).InvokeMember("DoubleBuffered",
                    System.Reflection.BindingFlags.SetProperty |
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic,
                    null, c, new object[] { true });
            }
            catch { }
        }

        private void ApplyDoubleBuffering()
        {
            if (tabControl == null) return;
            SetDoubleBuffered(tabControl);
            foreach (TabPage tp in tabControl.TabPages) SetDoubleBuffered(tp);
            SetDoubleBuffered(rootLayout);
            SetDoubleBuffered(topLayout);
            SetDoubleBuffered(logLayout);
            SetDoubleBuffered(panelLogArea);
            if (splitMain != null)
            {
                SetDoubleBuffered(splitMain);
                SetDoubleBuffered(splitMain.Panel1);
                SetDoubleBuffered(splitMain.Panel2);
            }
            SetDoubleBuffered(panelTopHeader);
            SetDoubleBuffered(platformBar);
            SetDoubleBuffered(panelLogButtons);
            SetDoubleBuffered(panelProgress);
            SetDoubleBuffered(panelPartition);
            SetDoubleBuffered(panelPartitionHeader);
            SetDoubleBuffered(panelPartBtns);
            foreach (var kv in _pickerHost) SetDoubleBuffered(kv.Value);
            foreach (var kv in fileRowRefresh) SetDoubleBuffered(kv.Key);
        }

        private void PaintThemePanel(object s, PaintEventArgs e)
        {
            var c = (Control)s;
            Rectangle r = c.ClientRectangle;
            if (r.Width < 2 || r.Height < 2) return;

            bool isGrp = c.Tag as string == "grp";
            if (!lightTheme)
            {
                if (isGrp)
                    ControlPaint.DrawBorder(e.Graphics, r, Color.FromArgb(58, 64, 76), ButtonBorderStyle.Solid);
                return;
            }

            // Light 3D: top highlight → bottom shade vertical gradient
            Color top, bot;
            if (c is TabPage)
            {
                top = Color.FromArgb(240, 244, 252);
                bot = Color.FromArgb(222, 229, 243);
            }
            else if (c == panelTopHeader)
            {
                top = Color.FromArgb(248, 250, 254);
                bot = Color.FromArgb(208, 216, 234);
            }
            else if (c == platformBar || c == panelLogButtons || c == panelProgress || c == panelPartitionHeader || c == panelPartBtns)
            {
                top = Color.FromArgb(242, 245, 251);
                bot = Color.FromArgb(214, 221, 237);
            }
            else if (c == panelPartition)
            {
                top = Color.FromArgb(236, 241, 250);
                bot = Color.FromArgb(218, 225, 240);
            }
            else if (isGrp)
            {
                top = Color.FromArgb(253, 254, 255);
                bot = Color.FromArgb(233, 238, 248);
            }
            else if (c == rootLayout || c == topLayout || c == logLayout || c == panelLogArea)
            {
                top = Color.FromArgb(234, 239, 248);
                bot = Color.FromArgb(214, 222, 238);
            }
            else
            {
                top = Color.FromArgb(248, 250, 253);
                bot = Color.FromArgb(228, 234, 245);
            }

            using (var br = new LinearGradientBrush(r, top, bot, LinearGradientMode.Vertical))
                e.Graphics.FillRectangle(br, r);

            // 3D edge: TabPage/chrome → Raised, group box → Etched (subtle)
            if (c is TabPage)
                ControlPaint.DrawBorder(e.Graphics, r, Color.FromArgb(168, 178, 200), ButtonBorderStyle.Solid);
            else
                ControlPaint.DrawBorder3D(e.Graphics, r,
                    isGrp ? Border3DStyle.Etched : Border3DStyle.Raised);
        }

        private void ApplyTheme()
        {
            if (btnThemeToggle != null) btnThemeToggle.Text = lightTheme ? "🌙 Dark" : "☀ Light";

            // Light mode: pure white တွေ မသုံးဘဲ soft blue-gray / muted tone (မတောက်အောင်)
            Color formBg = lightTheme ? Color.FromArgb(222, 228, 240) : Color.FromArgb(18, 20, 24);
            Color headerBg = lightTheme ? Color.FromArgb(208, 215, 231) : Color.FromArgb(26, 29, 35);
            Color tabBg = lightTheme ? Color.FromArgb(230, 235, 245) : Color.FromArgb(28, 31, 38);
            Color text = lightTheme ? Color.FromArgb(35, 38, 48) : Color.White;
            Color panelBg = lightTheme ? Color.FromArgb(226, 232, 243) : Color.FromArgb(23, 26, 32);
            Color logBg = lightTheme ? Color.FromArgb(228, 234, 246) : Color.FromArgb(10, 11, 14);
            Color logFg = lightTheme ? Color.FromArgb(15, 85, 48) : Color.FromArgb(0, 255, 128);
            Color accent = lightTheme ? Color.FromArgb(58, 102, 208) : Color.FromArgb(86, 145, 250);
            Color accentSoft = lightTheme ? Color.FromArgb(94, 66, 186) : Color.FromArgb(139, 92, 246);
            Color chromeBg = lightTheme ? Color.FromArgb(212, 219, 234) : Color.FromArgb(22, 25, 30);
            Color chromeBg2 = lightTheme ? Color.FromArgb(217, 224, 238) : Color.FromArgb(26, 29, 35);
            Color inputBg = lightTheme ? Color.FromArgb(243, 246, 252) : Color.FromArgb(38, 42, 50);
            Color inputFg = lightTheme ? Color.FromArgb(35, 38, 48) : Color.White;
            Color splitBg = lightTheme ? Color.FromArgb(175, 183, 202) : Color.FromArgb(45, 49, 58);

            this.BackColor = formBg;
            this.ForeColor = text;
            if (panelTopHeader != null) panelTopHeader.BackColor = headerBg;
            if (lblDeviceModeStatus != null) lblDeviceModeStatus.ForeColor = lightTheme ? Color.FromArgb(45, 50, 62) : Color.LightGray;
            if (lblPlatformStatus != null) lblPlatformStatus.ForeColor = accentSoft;
            if (lblBattery != null) lblBattery.ForeColor = lightTheme ? Color.FromArgb(80, 80, 80) : Color.FromArgb(180, 195, 215);
            if (lblPort != null) lblPort.ForeColor = accent;
            if (cmbPorts != null) { cmbPorts.BackColor = inputBg; cmbPorts.ForeColor = inputFg; }

            // Layout chrome (left log / right workspace / splitter) — 3D paint override အတွက် base color
            if (rootLayout != null) { rootLayout.BackColor = formBg; EnsureThemePaint(rootLayout); }
            if (topLayout != null) { topLayout.BackColor = formBg; EnsureThemePaint(topLayout); }
            if (logLayout != null) { logLayout.BackColor = formBg; EnsureThemePaint(logLayout); }
            if (panelLogArea != null) { panelLogArea.BackColor = formBg; EnsureThemePaint(panelLogArea); }
            if (panelLogButtons != null) { panelLogButtons.BackColor = chromeBg; EnsureThemePaint(panelLogButtons); }
            if (panelProgress != null) { panelProgress.BackColor = chromeBg2; EnsureThemePaint(panelProgress); }
            if (platformBar != null) { platformBar.BackColor = chromeBg; EnsureThemePaint(platformBar); }
            if (splitMain != null) splitMain.BackColor = splitBg;
            if (panelPartition != null) { panelPartition.BackColor = lightTheme ? Color.FromArgb(224, 230, 242) : Color.FromArgb(20, 22, 26); EnsureThemePaint(panelPartition); }
            if (panelPartitionHeader != null) { panelPartitionHeader.BackColor = chromeBg2; EnsureThemePaint(panelPartitionHeader); }
            if (panelPartBtns != null) { panelPartBtns.BackColor = chromeBg2; EnsureThemePaint(panelPartBtns); }
            if (tabControl != null) tabControl.BackColor = formBg;
            if (panelTopHeader != null) EnsureThemePaint(panelTopHeader);
            if (lblProgressStatus != null) lblProgressStatus.ForeColor = lightTheme ? Color.FromArgb(94, 66, 186) : Color.FromArgb(86, 145, 250);
            if (lblSpeedBadge != null) lblSpeedBadge.ForeColor = lightTheme ? Color.FromArgb(0, 110, 50) : Color.FromArgb(0, 255, 128);

            foreach (TabPage tp in tabControl.TabPages)
            {
                tp.BackColor = tabBg;
                EnsureThemePaint(tp);   // light: tab page 3D gradient
            }

            if (rtbLog != null)
            {
                rtbLog.BackColor = logBg;
                rtbLog.ForeColor = logFg;
                rtbLog.BorderStyle = lightTheme ? BorderStyle.Fixed3D : BorderStyle.None;  // sunken well
            }

            SyncPlatformBar();

            // Group panels + buttons + inputs + labels
            foreach (Control c in AllControls(this))
            {
                if (c is Panel p)
                {
                    if (p == panelTopHeader || p == panelProgress || p == panelLogButtons
                        || p == platformBar || p == panelPartitionHeader || p == panelPartBtns
                        || p == panelPartition || p == panelLogArea || p == panelLicenseBadge)
                        continue;

                    // Group panels / loader picker — parent TabPage → tab bg; else panel bg
                    bool isGrp = p.Tag as string == "grp";
                    Color grpBg = lightTheme ? Color.FromArgb(238, 242, 250) : Color.FromArgb(23, 26, 32);
                    p.BackColor = p.Parent is TabPage
                        ? (isGrp ? grpBg : tabBg)
                        : (isGrp ? grpBg : panelBg);
                    if (isGrp) EnsureThemePaint(p);   // 3D gradient + etched border
                }
                else if (c is Button b)
                {
                    string tag = b.Tag as string;
                    if (tag == "gbtn" || tag == "white")
                        b.ForeColor = Color.White;          // gradient / solid-color buttons: always white
                    else if (tag == "platform")
                        { /* SyncPlatformBar already themed */ }
                    else
                    {
                        // plain buttons (ShopPanel / dialogs): follow theme bg+fg
                        b.BackColor = lightTheme ? Color.FromArgb(235, 239, 247) : Color.FromArgb(40, 45, 55);
                        b.ForeColor = text;
                    }
                }
                else if (c is TextBox tb)
                {
                    tb.BackColor = inputBg;
                    tb.ForeColor = inputFg;
                }
                else if (c is ComboBox cmb)
                {
                    cmb.BackColor = inputBg;
                    cmb.ForeColor = inputFg;
                }
                else if (c is CheckBox cb && cb != chkMtkBackupVbmetaFirst)
                    cb.ForeColor = text;
                else if (c is Label lb && lb != lblDeviceModeStatus && lb != lblPlatformStatus && lb != lblBattery && lb != lblPort
                    && lb != lblClock && lb != lblLicenseInfo)
                {
                    if (!labelFgBackup.TryGetValue(lb, out Color orig))
                        labelFgBackup[lb] = orig = lb.ForeColor;

                    if (lb.Tag as string == "accent")
                    {
                        lb.ForeColor = accent;
                    }
                    else if (lightTheme)
                    {
                        if (IsAccentTitle(orig)) lb.ForeColor = accent;
                        else if (orig.GetBrightness() > 0.45f) lb.ForeColor = Color.FromArgb(55, 55, 55);
                    }
                    else
                    {
                        lb.ForeColor = orig;
                    }
                }
            }

            // file picker rows (QC Loader / MTK DA·Auth·Preloader / SPD Loader) — theme အလိုက် bg + state
            foreach (var kv in fileRowRefresh)
            {
                Panel rp = kv.Key;
                if (rp.IsDisposed) continue;
                rp.BackColor = RowFieldBg;
                if (rp.Controls.Count > 0 && rp.Controls[0] is Label rlb) { rlb.BackColor = RowLabelBg; rlb.ForeColor = RowLabelText; }
                if (rp.Controls.Count > 1 && rp.Controls[1] is Label rfld) rfld.BackColor = RowFieldBg;
                try { kv.Value(); } catch { }
            }

            if (dgvPartitions != null)
            {
                dgvPartitions.BackgroundColor = lightTheme ? Color.FromArgb(226, 232, 243) : Color.FromArgb(20, 22, 26);
                dgvPartitions.DefaultCellStyle.BackColor = lightTheme ? Color.FromArgb(240, 244, 251) : Color.FromArgb(28, 31, 38);
                dgvPartitions.DefaultCellStyle.ForeColor = text;
                dgvPartitions.ColumnHeadersDefaultCellStyle.BackColor = lightTheme ? Color.FromArgb(212, 219, 234) : Color.FromArgb(35, 40, 50);
                dgvPartitions.ColumnHeadersDefaultCellStyle.ForeColor = lightTheme ? Color.FromArgb(94, 66, 186) : Color.FromArgb(86, 145, 250);
            }

            // ShopPanel / Setup-Backups grids + lists
            foreach (Control c in AllControls(this))
            {
                if (c is DataGridView dgv && dgv != dgvPartitions)
                {
                    dgv.BackgroundColor = lightTheme ? Color.FromArgb(226, 232, 243) : Color.FromArgb(28, 31, 38);
                    dgv.DefaultCellStyle.BackColor = lightTheme ? Color.FromArgb(240, 244, 251) : Color.FromArgb(35, 40, 50);
                    dgv.DefaultCellStyle.ForeColor = text;
                    dgv.ColumnHeadersDefaultCellStyle.BackColor = lightTheme ? Color.FromArgb(212, 219, 234) : Color.FromArgb(35, 40, 50);
                    dgv.ColumnHeadersDefaultCellStyle.ForeColor = lightTheme ? Color.FromArgb(94, 66, 186) : Color.FromArgb(86, 145, 250);
                }
                else if (c is ListBox lb && lb != null)
                {
                    lb.BackColor = inputBg;
                    lb.ForeColor = inputFg;
                }
            }

            // force repaint of 3D / group panels
            ApplyDoubleBuffering();   // theme ပြောင်းပြီး flicker မကျန်အောင်
            foreach (Control c in themePaintWired)
                c.Invalidate();
            this.Invalidate(true);
        }

        // Partitions holding device-unique or security-critical data. Factory MTK scatter files mark
        // these non-downloadable so that a "Download All" cannot overwrite calibration data, IMEI/NVRAM
        // contents or the bootloader lock state with images taken from a different device.
        private static readonly HashSet<string> InvisiblePartitions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "nvram", "nvdata", "nvcfg", "nvram_bak", "nvdata_bak", "protect1", "protect2",
            "persist", "seccfg", "seccfg_bak", "proinfo", "boot_para", "otp", "frp", "efs"
        };

        private string GenerateScatterText(List<PartitionMeta> parts, string platform)
        {
            // Storage type comes from the device info lines mtkclient prints: eMMC reports
            // "EMMC ID:", UFS reports "UFS ID:" / "UFS LU0..2 Size:".
            string storageKind, hwStorage, region;
            if (detectedStorageKind == "UFS")
            {
                storageKind = "UFS";
                hwStorage = "HW_STORAGE_UFS";
                region = "UFS_LU2";
            }
            else
            {
                storageKind = "EMMC";
                hwStorage = "HW_STORAGE_EMMC";
                region = "EMMC_USER";
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("############################################################################################################");
            sb.AppendLine("#");
            sb.AppendLine("#  MediaTek Scatter File Generated by PMK Mobile Tool v7.1");
            sb.AppendLine("#");
            sb.AppendLine("#  platform and storage were read from the connected device - verify them before flashing,");
            sb.AppendLine("#  a storage mismatch (eMMC vs UFS) can brick the phone.");
            sb.AppendLine("#  Partitions holding device-unique data (nvram, nvdata, persist, seccfg, ...) are marked");
            sb.AppendLine("#  is_download: false; set them to true only when you deliberately restore your own backup.");
            sb.AppendLine("#");
            sb.AppendLine("############################################################################################################");
            sb.AppendLine("general:");
            sb.AppendLine("  config_version: v1.1.2");
            sb.AppendLine("  platform: " + platform);
            sb.AppendLine("  # mtkclient does not report the factory project name; edit this to match your device if needed");
            sb.AppendLine("  project: " + platform);
            sb.AppendLine("  storage: " + storageKind);
            sb.AppendLine("  boot_channel: MSDC_0");
            sb.AppendLine("  block_size: 0x20000");
            sb.AppendLine("############################################################################################################");
            sb.AppendLine("");

            for (int i = 0; i < parts.Count; i++)
            {
                PartitionMeta p = parts[i];
                bool sensitive = InvisiblePartitions.Contains(p.Name);
                sb.AppendLine("- partition_index: SYS" + i);
                sb.AppendLine("  partition_name: " + p.Name);
                sb.AppendLine("  file_name: " + p.Name + ".img");
                sb.AppendLine("  is_download: " + (sensitive ? "false" : "true"));
                sb.AppendLine("  type: NORMAL_ROM");
                sb.AppendLine("  linear_start_addr: " + p.Offset);
                sb.AppendLine("  physical_start_addr: " + p.Offset);
                sb.AppendLine("  partition_size: " + p.LengthHex);
                sb.AppendLine("  region: " + region);
                sb.AppendLine("  storage: " + hwStorage);
                sb.AppendLine("  boundary_check: true");
                sb.AppendLine("  is_reserved: false");
                sb.AppendLine("  operation_type: " + (sensitive ? "INVISIBLE" : "UPDATE"));
                sb.AppendLine("  reserve: 0x00");
                sb.AppendLine("");
            }

            return sb.ToString();
        }

        // Writes the scatter file next to the dumped images. Refuses to invent a platform name, so a
        // scatter is only produced from data the device actually reported.
        private bool TryWriteScatterFile(string directory, string fileName, out string writtenPath)
        {
            writtenPath = null;

            if (loadedPartitions.Count == 0)
            {
                Log("[!] No partition table loaded - run \"Read Info / GPT\" first.", Color.Orange);
                return false;
            }

            if (string.IsNullOrEmpty(detectedCpuPlatform))
            {
                Log("[!] SoC platform unknown - run \"Read Info / GPT\" first. Scatter file not generated.", Color.Orange);
                return false;
            }

            if (string.IsNullOrEmpty(detectedStorageKind))
            {
                Log("[!] Storage type (eMMC/UFS) was not reported - scatter assumes eMMC. Verify before flashing!", Color.Orange);
            }

            try
            {
                if (string.IsNullOrEmpty(fileName)) fileName = detectedCpuPlatform + "_Android_scatter.txt";
                writtenPath = Path.Combine(directory, fileName);
                File.WriteAllText(writtenPath, GenerateScatterText(loadedPartitions, detectedCpuPlatform), Encoding.UTF8);
                return true;
            }
            catch (Exception ex)
            {
                Log("Scatter Error: " + ex.Message, Color.Red);
                writtenPath = null;
                return false;
            }
        }

        // ================= STORAGE & MEMORY DIAGNOSTIC TEST =================
        private async void BtnMtkMemTest_Click(object sender, EventArgs e)
        {
            await RunFlashWorkflowAsync(async () =>
            {
                string portParam = GetMtkTransportParam();
                Log("========================================================", Color.FromArgb(0, 180, 255));
                Log("       STORAGE & MEMORY HARDWARE DIAGNOSTIC TEST        ", Color.White);
                Log("========================================================", Color.FromArgb(0, 180, 255));
                Log("[*] Testing DRAM & Flash Storage Controller...", Color.Orange);

                await ExecuteCommandCleanAsync("mtk.exe", portParam + " printgpt", "Storage & Memory Test", clearPartitions: true);
            });
        }

        // ================= FULL ROM DUMP =================
        private async void BtnMtkFullDump_Click(object sender, EventArgs e)
        {
            await RunFlashWorkflowAsync(async () =>
            {
                using (FolderBrowserDialog fbd = new FolderBrowserDialog { Description = "Select Folder to Save Complete Full ROM Firmware Dump" })
                {
                    if (fbd.ShowDialog() == DialogResult.OK)
                    {
                        string saveDir = fbd.SelectedPath;
                        string portParam = GetMtkTransportParam();
                        Log("[📦] Full Firmware Dump: Saving all partitions to " + saveDir + "...", Color.Cyan);

                        await ExecuteCommandCleanAsync("mtk.exe", portParam + " rl \"" + saveDir + "\"", "Full Firmware Dump");

                        if (TryWriteScatterFile(saveDir, null, out string scatterPath))
                        {
                            Log("[✔] Auto-Generated Scatter File in Dump Folder: " + Path.GetFileName(scatterPath), Color.LightGreen);
                        }
                    }
                }
            });
        }

        // ================= NORMAL ROM DUMP =================
        private async void BtnMtkNormalDump_Click(object sender, EventArgs e)
        {
            using (FolderBrowserDialog fbd = new FolderBrowserDialog { Description = "Select Folder for Normal Essential Firmware Dump" })
            {
                if (fbd.ShowDialog() == DialogResult.OK)
                {
                    string saveDir = fbd.SelectedPath;
                    string portParam = GetMtkTransportParam();

                    string[] essentialParts = new string[] {
                        "boot", "recovery", "vbmeta", "vbmeta_system", "vbmeta_vendor",
                        "dtbo", "super", "logo", "cust", "md1img", "spmfw", "sspm_1", "gz1"
                    };

                    Log("[📁] Normal Firmware Dump: Dumping Essential Flashing Partitions...", Color.Cyan);

                    int ok = 0, fail = 0;
                    foreach (string part in essentialParts)
                    {
                        string filePath = Path.Combine(saveDir, part + ".img");
                        Log("[+] Dumping: " + part + ".img ...", Color.LightSkyBlue);
                        await ExecuteCommandQuickAsync("mtk.exe", portParam + " r " + part + " \"" + filePath + "\"");
                        // mtk.exe r ပြီးရင် file တကယ်ရှိ/size>0 ကို စစ် — QuickAsync က output ပဲ return (exit code မပေး)
                        if (File.Exists(filePath) && new FileInfo(filePath).Length > 0) ok++;
                        else { fail++; Log("[!] " + part + " — dump failed or empty file.", Color.Orange); }
                    }

                    if (TryWriteScatterFile(saveDir, null, out string scatterPath))
                    {
                        Log("[✔] Scatter File Created: " + Path.GetFileName(scatterPath), Color.LightGreen);
                    }

                    if (fail == 0)
                        Log("[✔] Normal ROM Dump Completed Successfully! (" + ok + "/" + essentialParts.Length + ")", Color.LightGreen);
                    else
                        Log("[!] Normal ROM Dump finished with errors: " + ok + " ok / " + fail + " failed.", Color.Orange);
                }
            }
        }

        // ================= CONTEXT MENU ACTIONS =================
        // Partition list က MTK နဲ့ Qualcomm နှစ်ခုလုံးအတွက် ပြတယ် — လက်ရှိ tab ပေါ် မူတည်ပြီး
        // မှန်တဲ့ tool ကို ရွေးတယ် (MediaTek → mtk.exe / Qualcomm → edl.py)
        private bool IsQualcommTab()
        {
            return (tabControl.SelectedTab == tabQc);
        }

        private async Task<bool> RunPartitionOpAsync(string op, string partition, string filePath, string title)
        {
            if (op == "w" && filePath != null && !WarnIfImageTooLarge(partition, filePath))
                return false;

            string arg = op + " " + partition + (filePath != null ? " \"" + filePath + "\"" : "");

            if (IsQualcommTab())
            {
                if (!EnsureEdlModule()) return false;
                return await ExecuteCommandCleanAsync("python", BuildEdlArgs(arg), title);
            }

            // SPD tab — spd_dump (mtk.exe မသုံးရ — မှားသုံးရင် brick အန္တရာယ်)
            if (tabControl.SelectedTab == tabSpd)
            {
                string spd = FindSpdDump();
                string prefix = BuildSpdFdlPrefix();
                string spdArgs;
                if (op == "r")
                    spdArgs = prefix + " path \"" + Path.GetDirectoryName(filePath ?? "") + "\" r " + partition;
                else if (op == "w")
                    spdArgs = prefix + " w " + partition + " \"" + filePath + "\"";
                else
                    spdArgs = prefix + " e " + partition;
                return await ExecuteCommandCleanAsync(spd, spdArgs, title, false, true);
            }

            // Samsung tab — generic r/w/e context menu မသုံးရ (heimdall ကွဲပြား; mtk.exe မခေါးရ)
            if (tabControl.SelectedTab == tabSamsung)
            {
                Log("[!] Partition read/write/erase is not available on the Samsung tab - use Firmware Flash / PIT.", Color.Orange);
                return false;
            }

            // ADB tab + root — adb su dd နဲ့ r/w/e (by-name / dm path)
            if (tabControl.SelectedTab == tabAdb)
                return await RunAdbRootPartitionOpAsync(op, partition, filePath, title);

            if (tabControl.SelectedTab != tabMtk)
            {
                Log("[!] Partition operation is only supported on MediaTek / Qualcomm / Spreadtrum tabs.", Color.Orange);
                return false;
            }

            return await ExecuteCommandCleanAsync("mtk.exe", GetMtkTransportParam() + " " + arg, title);
        }

        // ADB root partition r/w/e — /dev/block/by-name/<name> ကို su + dd နဲ့
        private async Task<bool> RunAdbRootPartitionOpAsync(string op, string partition, string filePath, string title)
        {
            string id = (await ProbeSuAsync()).Trim();
            if (!id.Contains("uid=0"))
            {
                Log("[!] " + title + ": root (su) မရပါ — Xiaomi Temp Root / Magisk / KernelSU စမ်းပါ", Color.OrangeRed);
                MessageBox.Show("Root (su) မရပါ။\n\nADB partition r/w/e အတွက် root လိုအပ်ပါတယ်。",
                    title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            string dev = partition.StartsWith("/dev/", StringComparison.Ordinal)
                ? partition
                : "/dev/block/by-name/" + partition;
            // extra mounts (system/vendor…) က by-name မရှိ — path တိုက်ရိုက်ပေးနိုင်
            string chk = (await ExecuteCommandQuickAsync("adb.exe",
                "shell su -c \"ls -l " + dev + " 2>/dev/null || readlink -f /dev/block/by-name/" + partition + "\"")).Trim();
            if (chk.Contains("No such") || string.IsNullOrWhiteSpace(chk))
            {
                // fallback: by-name symlink resolve
                string rl = (await ExecuteCommandQuickAsync("adb.exe",
                    "shell su -c \"readlink -f /dev/block/by-name/" + partition + "\"")).Trim();
                if (!string.IsNullOrWhiteSpace(rl) && !rl.Contains("No such")) dev = rl;
                else
                {
                    Log("[!] " + title + ": device node not found for [" + partition + "]", Color.OrangeRed);
                    return false;
                }
            }
            else if (chk.Contains("->"))
            {
                int arrow = chk.LastIndexOf("->", StringComparison.Ordinal);
                string target = chk.Substring(arrow + 2).Trim();
                if (!string.IsNullOrWhiteSpace(target)) dev = target;
            }

            if (op == "r")
            {
                string local = filePath ?? Path.Combine(Path.GetTempPath(), partition + ".img");
                Directory.CreateDirectory(Path.GetDirectoryName(local) ?? Path.GetTempPath());
                string remote = "/data/local/tmp/pmk_" + partition + ".img";
                Log("[*] READ [" + partition + "] " + dev + " → " + local, Color.Cyan);
                bool ok = await ExecuteCommandCleanAsync("adb.exe",
                    "shell su -c \"dd if=" + dev + " of=" + remote + " bs=1048576 2>/dev/null\"",
                    "dd " + partition, false, false, quiet: true);
                if (!ok) { Log("[!] dd failed [" + partition + "]", Color.OrangeRed); return false; }
                ok = await ExecuteCommandCleanAsync("adb.exe", "pull \"" + remote + "\" \"" + local + "\"",
                    "pull " + partition, false, false, quiet: true);
                await ExecuteCommandQuickAsync("adb.exe",
                    "shell su -c \"rm -f " + remote + "\"");
                if (ok && File.Exists(local))
                    Log("[OK] READ [" + partition + "] → " + local + " (" + FormatBytesLong(new FileInfo(local).Length) + ")", Color.LightGreen);
                else
                    Log("[!] READ [" + partition + "] failed (pull or file missing)", Color.OrangeRed);
                return ok && File.Exists(local);
            }

            if (op == "w")
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                {
                    Log("[!] " + title + ": file not found " + (filePath ?? "(null)"), Color.OrangeRed);
                    return false;
                }
                string remote = "/data/local/tmp/pmk_write_" + partition + Path.GetExtension(filePath);
                long flen = new FileInfo(filePath).Length;
                Log("[*] WRITE [" + partition + "] " + Path.GetFileName(filePath) + " (" + FormatBytesLong(flen) + ") → " + dev, Color.Cyan);
                bool ok = await ExecuteCommandCleanAsync("adb.exe", "push \"" + filePath + "\" \"" + remote + "\"",
                    "push " + partition, false, false, quiet: true);
                if (!ok) { Log("[!] push failed [" + partition + "]", Color.OrangeRed); return false; }
                long blocks = flen > 0 ? (flen + 1048575) / 1048576 : 1;
                ok = await ExecuteCommandCleanAsync("adb.exe",
                    "shell su -c \"dd if=" + remote + " of=" + dev + " bs=1048576 count=" + blocks + " 2>/dev/null\"",
                    "dd " + partition, false, false, quiet: true);
                await ExecuteCommandQuickAsync("adb.exe",
                    "shell su -c \"rm -f " + remote + "\"");
                if (ok) Log("[OK] WRITE [" + partition + "] ← " + Path.GetFileName(filePath), Color.LightGreen);
                else Log("[!] WRITE [" + partition + "] failed", Color.OrangeRed);
                return ok;
            }

            if (op == "e")
            {
                string sz = (await ExecuteCommandQuickAsync("adb.exe",
                    "shell su -c \"blockdev --getsize64 " + dev + " 2>/dev/null\"")).Trim();
                long.TryParse(sz, out long bytes);
                if (bytes <= 0)
                {
                    string sec = (await ExecuteCommandQuickAsync("adb.exe",
                        "shell su -c \"cat /sys/class/block/$(basename $(readlink -f " + dev + "))/size 2>/dev/null\"")).Trim();
                    if (long.TryParse(sec, out long sectors) && sectors > 0) bytes = sectors * 512;
                }
                long blocks = bytes > 0 ? (bytes + 1048575) / 1048576 : 1;
                Log("[*] ERASE [" + partition + "] zero " + FormatBytesLong(bytes > 0 ? bytes : 1048576) + " → " + dev, Color.Orange);
                bool ok = await ExecuteCommandCleanAsync("adb.exe",
                    "shell su -c \"dd if=/dev/zero of=" + dev + " bs=1048576 count=" + blocks + " 2>/dev/null\"",
                    "erase " + partition, false, false, quiet: true);
                if (ok) Log("[OK] ERASE [" + partition + "]", Color.LightGreen);
                else Log("[!] ERASE [" + partition + "] failed", Color.OrangeRed);
                return ok;
            }

            return false;
        }

        private async void MenuReadPartition_Click(object sender, EventArgs e)
        {
            List<string> parts = GetCheckedPartitions();

            if (parts.Count == 0)
            {
                if (dgvPartitions.SelectedRows.Count == 0) return;
                string single = dgvPartitions.SelectedRows[0].Cells["colName"].Value.ToString();
                using (SaveFileDialog sfd = new SaveFileDialog { Filter = "Image Files (*.img)|*.img|Binary Files (*.bin)|*.bin", FileName = single + ".img" })
                {
                    if (sfd.ShowDialog() == DialogResult.OK)
                        await RunPartitionOpAsync("r", single, sfd.FileName, "Read Partition [" + single + "]");
                }
                return;
            }

            using (FolderBrowserDialog fbd = new FolderBrowserDialog { Description = "Select folder to save " + parts.Count + " partition(s)" })
            {
                if (fbd.ShowDialog() != DialogResult.OK) return;
                Log("[*] Reading " + parts.Count + " partition(s) -> " + fbd.SelectedPath, Color.Cyan);
                int ok = 0;
                foreach (string part in parts)
                {
                    string file = Path.Combine(fbd.SelectedPath, part + ".img");
                    if (await RunPartitionOpAsync("r", part, file, "Read [" + part + "]")) ok++;
                }
                Log("[OK] Read " + ok + " / " + parts.Count + " -> " + fbd.SelectedPath, ok == parts.Count ? Color.LightGreen : Color.Orange);
            }
        }

        private async void MenuWritePartition_Click(object sender, EventArgs e)
        {
            List<string> parts = GetCheckedPartitions();

            if (parts.Count == 0)
            {
                if (dgvPartitions.SelectedRows.Count == 0) return;
                string single = dgvPartitions.SelectedRows[0].Cells["colName"].Value.ToString();
                using (OpenFileDialog ofd = new OpenFileDialog { Filter = "Image Files (*.img)|*.img|All Files (*.*)|*.*", Title = "Select File for [" + single + "]" })
                {
                    if (ofd.ShowDialog() == DialogResult.OK)
                    {
                        if (MessageBox.Show("Write " + Path.GetFileName(ofd.FileName) + " into [" + single + "]?", "Confirm Write", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                            await RunPartitionOpAsync("w", single, ofd.FileName, "Write Partition [" + single + "]");
                    }
                }
                return;
            }

            using (FolderBrowserDialog fbd = new FolderBrowserDialog { Description = "Select folder containing <partition>.img files - " + parts.Count + " checked" })
            {
                if (fbd.ShowDialog() != DialogResult.OK) return;

                int have = 0;
                foreach (string part in parts)
                    if (File.Exists(Path.Combine(fbd.SelectedPath, part + ".img"))) have++;

                if (have == 0)
                {
                    MessageBox.Show("No matching .img file in that folder. File names must match partition names (boot.img, vbmeta.img ...).", "Firmware", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                string ask = "Write " + have + " of " + parts.Count + " checked partition(s) from " + fbd.SelectedPath + " ?  (user data may be wiped - owner consent required)";
                if (MessageBox.Show(ask, "Confirm Write", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

                int ok = 0;
                foreach (string part in parts)
                {
                    string file = Path.Combine(fbd.SelectedPath, part + ".img");
                    if (!File.Exists(file)) { Log("[!] Missing: " + part + ".img - skipped", Color.Orange); continue; }
                    if (await RunPartitionOpAsync("w", part, file, "Write [" + part + "]")) ok++;
                }
                Log("[OK] Wrote " + ok + " / " + parts.Count, ok > 0 ? Color.LightGreen : Color.OrangeRed);
            }
        }

        private async void MenuErasePartition_Click(object sender, EventArgs e)
        {
            List<string> parts = GetCheckedPartitions();

            if (parts.Count == 0)
            {
                if (dgvPartitions.SelectedRows.Count == 0) return;
                string single = dgvPartitions.SelectedRows[0].Cells["colName"].Value.ToString();
                if (MessageBox.Show("Erase/Format [" + single + "]?", "Confirm Erase", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                    await RunPartitionOpAsync("e", single, null, "Erase Partition [" + single + "]");
                return;
            }

            string ask = "Erase/Format " + parts.Count + " partition(s): " + string.Join(", ", parts.ToArray()) + " ?  (may destroy data - owner consent required)";
            if (MessageBox.Show(ask, "Confirm Erase (" + parts.Count + ")", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            int ok = 0;
            foreach (string part in parts)
                if (await RunPartitionOpAsync("e", part, null, "Erase [" + part + "]")) ok++;
            Log("[OK] Erased " + ok + " / " + parts.Count, ok > 0 ? Color.LightGreen : Color.OrangeRed);
        }

        // Main partition grid right-click → Mount RW (root). Selected row name → /proc/mounts lookup → remount.
        private async void MenuMountRwPartition_Click(object sender, EventArgs e)
        {
            Log("[*] Menu: Mount RW clicked", Color.Gray);
            DataGridViewRow row = null;
            string pname = null, pmp = null, pmode = null, pdev = null;
            if (dgvPartitions.SelectedRows.Count > 0)
                row = dgvPartitions.SelectedRows[0];
            if (row != null)
            {
                pname = row.Cells["colName"]?.Value?.ToString();
                pdev = row.Cells["colOffset"]?.Value?.ToString();
                pmp = row.Cells["colMount"]?.Value?.ToString();
                pmode = row.Cells["colMode"]?.Value?.ToString();
            }
            if (string.IsNullOrWhiteSpace(pname))
            {
                MessageBox.Show("Partition တစ်ခု ရွေးပါ။", "Mount RW", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string dv = await ExecuteCommandQuickAsync("adb.exe", "devices");
            if (string.IsNullOrWhiteSpace(dv) || !dv.Contains("\tdevice"))
            {
                MessageBox.Show("ADB device not found.", "Mount RW", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            string id = (await ProbeSuAsync()).Trim();
            if (!id.Contains("uid=0"))
            {
                MessageBox.Show("Root (su) မရပါ။\n\n• Xiaomi Temp Root / Magisk / KernelSU ရအောင်လုပ်ပါ",
                    "Mount RW", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Log("[!] Mount RW: su not available", Color.OrangeRed);
                return;
            }

            // Prefer grid cell values (loaded at Partitions ROOT time); re-lookup if empty/stale
            if (string.IsNullOrEmpty(pmp) || pmp == "—" || string.IsNullOrEmpty(pmode) || pmode == "—")
            {
                string mounts = (await ExecuteCommandQuickAsync("adb.exe", "shell su -c \"cat /proc/mounts\"")).Trim();
                foreach (string line in mounts.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] pp = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (pp.Length < 4) continue;
                    string dev = pp[0];
                    string mpath = pp[1];
                    string opts = pp[3];
                    string bn = Path.GetFileName(dev.TrimEnd('/'));
                    bool match =
                        dev.EndsWith("/" + pname, StringComparison.OrdinalIgnoreCase) ||
                        bn.Equals(pname, StringComparison.OrdinalIgnoreCase) ||
                        Path.GetFileName(mpath.TrimEnd('/')).Equals(pname, StringComparison.OrdinalIgnoreCase);
                    if (!match && bn.StartsWith("dm-", StringComparison.OrdinalIgnoreCase))
                    {
                        string dmName = (await ExecuteCommandQuickAsync("adb.exe",
                            "shell su -c \"cat /sys/block/" + bn + "/dm/name 2>/dev/null\"")).Trim();
                        if (!string.IsNullOrEmpty(dmName))
                        {
                            if (dmName.EndsWith("-verity", StringComparison.OrdinalIgnoreCase))
                                dmName = dmName.Substring(0, dmName.Length - "-verity".Length);
                            match = dmName.Equals(pname, StringComparison.OrdinalIgnoreCase);
                        }
                    }
                    if (match)
                    {
                        pmp = mpath;
                        pmode = opts.Split(',').Any(o => o.Equals("ro", StringComparison.OrdinalIgnoreCase)) ? "ro" : "rw";
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(pmp) || pmp == "—" || pmp == "")
            {
                // Raw partition (frp, nvram…) — mount remount မလုပ်ရဘဲ root dd နဲ့ write
                Log("[i] " + pname + " is not mounted — raw block (root write via dd).", Color.Gray);
                DialogResult pick = MessageBox.Show(
                    pname + " is not mounted (raw block device)." + Environment.NewLine + Environment.NewLine +
                    "Root write လုပ်မလား?" + Environment.NewLine + Environment.NewLine +
                    "Yes = Zero/erase [" + pname + "] (dd if=/dev/zero) — frp အတွက် FRP clear လို" + Environment.NewLine +
                    "No = File ရွေးပြီး [" + pname + "]ထဲ write" + Environment.NewLine +
                    "Cancel = မလုပ်တော့ဘူး",
                    "Root write " + pname,
                    MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (pick == DialogResult.Yes)
                {
                    if (MessageBox.Show(
                            "Zero/erase [" + pname + "]?" + Environment.NewLine + Environment.NewLine +
                            "• dd if=/dev/zero → " + (pdev ?? pname) + Environment.NewLine +
                            "• frp ဆိုရင် Google account (FRP) ပျက်သွားမယ်" + Environment.NewLine +
                            "• ဖုန်းပိုင်ရှင် ခွင့်ပြုချက် ရှိမှ လုပ်ပါ",
                            "Confirm Zero " + pname, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                            await RunPartitionOpAsync("e", pname, null, "Root Zero [" + pname + "]");
                }
                else if (pick == DialogResult.No)
                {
                    using (OpenFileDialog ofd = new OpenFileDialog
                    {
                        Filter = "All Files (*.*)|*.*|Image Files (*.img)|*.img",
                        Title = "Write file to [" + pname + "]"
                    })
                    {
                        if (ofd.ShowDialog() == DialogResult.OK &&
                            MessageBox.Show("Write " + Path.GetFileName(ofd.FileName) + " → [" + pname + "]?",
                                "Confirm Root Write", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                            await RunPartitionOpAsync("w", pname, ofd.FileName, "Root Write [" + pname + "]");
                    }
                }
                return;
            }

            if (pmode != null && pmode.Equals("rw", StringComparison.OrdinalIgnoreCase))
            {
                Log("[i] " + pname + " already mounted rw at " + pmp, Color.Gray);
                MessageBox.Show(pname + " is already rw at " + pmp, "Mount RW", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (MessageBox.Show(
                    "Remount " + pmp + " (" + pname + ") as Read-Write?" + Environment.NewLine + Environment.NewLine +
                    "• mount -o remount,rw " + pmp + Environment.NewLine +
                    "• SELinux / AVB က remount ကို တားနိုင်တယ်" + Environment.NewLine +
                    "• ဖုန်းပိုင်ရှင် ခွင့်ပြုချက် ရှိမှ လုပ်ပါ",
                    "Confirm Mount RW", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            Log("[*] Remount RW: " + pmp + " [" + pname + "] ...", Color.Orange);
            bool ok = await ExecuteCommandCleanAsync("adb.exe",
                "shell su -c \"mount -o remount,rw " + pmp + "\"",
                "Mount RW " + pmp, false, true);
            if (!ok && !string.IsNullOrEmpty(pdev))
            {
                await ExecuteCommandCleanAsync("adb.exe",
                    "shell su -c \"mount -o remount,rw " + pdev + " " + pmp + "\"",
                    "Mount RW (by device)", false, true);
            }

            string mounts2 = (await ExecuteCommandQuickAsync("adb.exe", "shell su -c \"cat /proc/mounts\"")).Trim();
            bool nowRw = false;
            foreach (string line in mounts2.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] pp = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (pp.Length < 4) continue;
                if (pp[1] == pmp)
                {
                    nowRw = !pp[3].Split(',').Any(o => o.Equals("ro", StringComparison.OrdinalIgnoreCase));
                    break;
                }
            }
            if (nowRw)
            {
                Log("[OK] " + pmp + " → rw", Color.LightGreen);
                if (row != null) row.Cells["colMode"].Value = "rw";
                MessageBox.Show(pmp + " is now rw.", "Mount RW", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                Log("[!] remount may have failed — check log / SELinux", Color.OrangeRed);
                MessageBox.Show("Remount status unknown — check log / SELinux.", "Mount RW", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ================= MTK ACTIONS =================
        private async void BtnMtkReadBrom_Click(object sender, EventArgs e)
        {
            await RunFlashWorkflowAsync(async () =>
            {
                string portParam = GetMtkTransportParam();
                await ExecuteCommandCleanAsync("mtk.exe", portParam + " printgpt", "MTK Read Info & GPT Map", clearPartitions: true);
            });
        }

        private async void BtnMtkUnlockBL_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show("Unlock Bootloader? (All user data will be wiped)", "Confirm Unlock", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                await RunMtkOpThenRebootAsync("da seccfg unlock", "MTK Unlock Bootloader");
            }
        }

        private async void BtnMtkRelockBL_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show("Relock Bootloader?", "Confirm Relock", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                await RunMtkOpThenRebootAsync("da seccfg lock", "MTK Relock Bootloader");
            }
        }

        // Userlock Reset — screen lock (PIN/pattern/password) ဖျက်ခြင်း။ mtkclient ကို
        // comma-separated partition ပေးလို့ userdata နဲ့ metadata ကို တစ်ခါတည်း erase လုပ်တယ်။
        // data အားလုံး ဖျက်ခံရတာမို့ confirmation ကို ရှင်းရှင်းလင်းလင်း ပြထားတယ်။
        private async void BtnMtkUserlockReset_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show(
                    "Userlock Reset — userdata နဲ့ metadata ကို format လုပ်ပါမယ်။\n\n" +
                    "• ဖုန်းထဲက data / ဓာတ်ပုံ / အက်ပ် အားလုံး ဖျက်ခံရမယ်\n" +
                    "• Screen lock (PIN / pattern / password) ပျက်သွားမယ်\n" +
                    "• ဖုန်းပိုင်ရှင် ခွင့်ပြုချက် ရှိမှ လုပ်ပါ (ခိုးရာပစ္စည်း မဟုတ်ကြောင်း စစ်ပါ)\n" +
                    "• data မဖျက်ဘဲ lock ဖျက်ချင်ရင် root/TWRP + ADB လမ်းကို သုံးပါ\n\n" +
                    "ဆက်လုပ်မလား?",
                    "Confirm Userlock Reset", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            string portParam = GetMtkTransportParam();
            bool ok = await RunMtkOpThenRebootAsync("e userdata,metadata", "MTK Userlock Reset (userdata + metadata)");

            // Post-op စာသား အနည်းဆုံး — "Completed Successfully!" + elapsed ပဲ (log မရှုပ်အောင်)
        }

        // FRP Remove — frp + config partition ကို erase လုပ်တယ် (Google account verification ဖျက်ခြင်း)။
        // userdata မထိပါ။ MTK အများစုမှာ frp တစ်ခုတည်း မလုံလောက်ဘဲ config ပါ လိုတတ်တယ် (MT6833 မှာ စမ်းပြီး)။
        private async void BtnMtkFrpRemove_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show(
                    "FRP Remove — frp + config partition ကို erase လုပ်ပါမယ် (Google account lock ဖျက်ခြင်း)။\n\n" +
                    "• User data မပျက်ပါ (frp / config partition တွေကိုသာ ဖျက်တယ်)\n" +
                    "• ဖုန်းပိုင်ရှင် ခွင့်ပြုချက် ရှိမှ လုပ်ပါ (ခိုးရာပစ္စည်း မဟုတ်ကြောင်း စစ်ပါ)\n\n" +
                    "ဆက်လုပ်မလား?",
                    "Confirm FRP Remove", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            bool ok = await RunMtkOpThenRebootAsync("e frp,config", "MTK FRP Remove (frp + config)");

            if (ok)
            {
                // Post-op စာသား အနည်းဆုံး — "Completed Successfully!" + elapsed ပဲ ပြတယ်
                // (ဖုန်း မတက်ရင် power နှိပ်ဖို့ ညွှန်ကြားချက်ကို marker handler က ပြပြီးသား)။
            }
        }

        // ================= ORANGE STATE / DM FIX (vbmeta) =================
        // AVB vbmeta header: magic "AVB0" @0, flags u32 big-endian @120
        // bit0=hashtree_disabled, bit1=verification_disabled → flags=3 (disabled) / flags=0 (enabled)
        private string BuildVbmeta(bool disableAvb)
        {
            byte[] img = new byte[4096];
            img[0] = (byte)'A';
            img[1] = (byte)'V';
            img[2] = (byte)'B';
            img[3] = (byte)'0';
            uint flags = disableAvb ? 3u : 0u;
            img[120] = (byte)((flags >> 24) & 0xFF);
            img[121] = (byte)((flags >> 16) & 0xFF);
            img[122] = (byte)((flags >> 8) & 0xFF);
            img[123] = (byte)(flags & 0xFF);

            string name = disableAvb ? "pmk_vbmeta_disabled.img" : "pmk_vbmeta_enabled.img";
            string path = Path.Combine(Path.GetTempPath(), name);
            File.WriteAllBytes(path, img);
            return path;
        }

        private async void BtnMtkOrangeStateFix_Click(object sender, EventArgs e)
        {
            await RunFlashWorkflowAsync(BtnMtkOrangeStateFixAsync, autoRebootAfter: false);
        }

        private async Task BtnMtkOrangeStateFixAsync()
        {
            DialogResult r = MessageBox.Show(
                "Orange State / vbmeta AVB — Fix မလား Restore လုပ်မလား?\n\n" +
                "• Yes (Fix): AVB verification disabled — Orange State warning ပျက်သွားမယ်\n" +
                "• No (Restore): AVB verification ပြန်ဖွင့်မယ် (flags=0)\n" +
                "• Cancel: မလုပ်တော့ဘူး\n\n" +
                "vbmeta partition ရှိရင်သာ ပြောင်းလဲမယ်။",
                "Orange State / vbmeta", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

            if (r == DialogResult.Cancel) return;
            bool fix = r == DialogResult.Yes;


            try
            {
                if (fix && chkMtkBackupVbmetaFirst != null && chkMtkBackupVbmetaFirst.Checked)
                    if (!await BackupVbmetaBeforeFix("vbmeta")) return;

                string img = BuildVbmeta(fix);
                string label = fix ? "Orange State Fix (vbmeta)" : "vbmeta Restore (enable AVB)";
                bool ok = await RunPartitionOpAsync("w", "vbmeta", img, label);
                if (ok)
                {
                    Log(fix
                        ? "[OK] Orange State Fix written to vbmeta (AVB disabled)."
                        : "[OK] vbmeta Restore written (AVB enabled).", Color.LightGreen);
                    if (chkMtkAutoReboot != null && chkMtkAutoReboot.Checked)
                        await ExecuteCommandCleanAsync("python", BuildMtkOpArgs("reset"),
                            fix ? "Reboot after Orange State Fix" : "Reboot after vbmeta Restore");
                }
                else
                {
                    Log("[!] vbmeta write failed — check that vbmeta exists (Read Info / GPT).", Color.Orange);
                }
            }
            catch (Exception ex)
            {
                Log("[!] Orange State / vbmeta write failed: " + ex.Message, Color.Red);
            }
        }

        private async void BtnMtkDmFix_Click(object sender, EventArgs e)
        {
            await RunFlashWorkflowAsync(BtnMtkDmFixAsync, autoRebootAfter: false);
        }

        private async Task BtnMtkDmFixAsync()
        {
            DialogResult r = MessageBox.Show(
                "DM Fix / vbmeta — Fix မလား Restore လုပ်မလား?\n\n" +
                "• Yes (Fix): vbmeta / vbmeta_system / vbmeta_vendor ကို disabled image ရေးမယ်\n" +
                "• No (Restore): AVB verification ပြန်ဖွင့်မယ် (flags=0)\n" +
                "• Cancel: မလုပ်တော့ဘူး\n\n" +
                "မရှိတဲ့ partition ကို skip လုပ်မယ်။",
                "DM Fix / vbmeta", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

            if (r == DialogResult.Cancel) return;
            bool fix = r == DialogResult.Yes;


            try
            {
                string[] parts = { "vbmeta", "vbmeta_system", "vbmeta_vendor" };
                if (fix && chkMtkBackupVbmetaFirst != null && chkMtkBackupVbmetaFirst.Checked)
                    foreach (string part in parts)
                        if (!await BackupVbmetaBeforeFix(part)) return;

                string img = BuildVbmeta(fix);
                int ok = 0;
                string label = fix ? "DM Fix" : "vbmeta Restore";
                foreach (string part in parts)
                {
                    if (!await RunPartitionOpAsync("w", part, img, label + " [" + part + "]")) return;
                    ok++;
                }

                Log((fix ? "[OK] DM Fix written " : "[OK] vbmeta Restore written ") +
                    ok + " / " + parts.Length + " partition(s).",
                    ok > 0 ? Color.LightGreen : Color.OrangeRed);

                if (ok > 0 && chkMtkAutoReboot != null && chkMtkAutoReboot.Checked)
                    await ExecuteCommandCleanAsync("python", BuildMtkOpArgs("reset"),
                        fix ? "Reboot after DM Fix" : "Reboot after vbmeta Restore");
            }
            catch (Exception ex)
            {
                Log("[!] DM Fix / vbmeta write failed: " + ex.Message, Color.Red);
            }
        }

        // Fix မလုပ်ခင် မူရင်း vbmeta ကို ဖတ်သိမ်း — Undo အတွက် path မှတ်ထား
        private async Task<bool> BackupVbmetaBeforeFix(string part)
        {
            try
            {
                if (!await ExecuteCommandCleanAsync("mtk.exe", GetMtkTransportParam() + " printgpt",
                    "Verify " + part + " size", clearPartitions: true, quiet: true)) return false;
                var matches = loadedPartitions.Where(p => p.Name.Equals(part, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count != 1) throw new InvalidDataException("Cannot verify partition size: " + part);
                long expected = ReviewSafety.ParseSize(matches[0].LengthHex);
                string dir = ShopServices.DataPath("vbmeta_backup");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, part + "_" + Guid.NewGuid().ToString("N") + ".bin");
                if (!await RunPartitionOpAsync("r", part, path, "Backup " + part + " (pre-fix)")) return false;
                if (!ReviewSafety.HasBackup(path, expected)) throw new InvalidDataException("Backup size mismatch: " + part);
                lastVbmetaBackupPath = path;
                Log("[OK] Backup verified: " + path, Color.Cyan);
                return true;
            }
            catch (Exception ex)
            {
                workflowFailed = true;
                Log("[FAIL] vbmeta backup failed; write cancelled: " + ex.Message, Color.Red);
                return false;
            }
        }

        // မူရင်း vbmeta backup ကို ပြန်ရေး — Fix မလုပ်ခင် backup ထားခဲ့ရင် Undo အလုပ်လုပ်တယ်
        private async void BtnMtkUndoVbmeta_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(lastVbmetaBackupPath) || !File.Exists(lastVbmetaBackupPath))
            {
                // backup folder ထဲက နောက်ဆုံးဖိုင် ရှာ
                try
                {
                    string dir = ShopServices.DataPath("vbmeta_backup");
                    if (Directory.Exists(dir))
                    {
                        var files = Directory.GetFiles(dir, "*.bin")
                            .OrderByDescending(f => File.GetLastWriteTimeUtc(f)).ToList();
                        if (files.Count > 0) lastVbmetaBackupPath = files[0];
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("vbmeta backup search: " + ex.Message);
                }
            }

            if (string.IsNullOrEmpty(lastVbmetaBackupPath) || !File.Exists(lastVbmetaBackupPath))
            {
                MessageBox.Show(
                    "Undo အတွက် vbmeta backup မတွေ့ပါ။\n\n" +
                    "Fix မလုပ်ခင် \"Backup vbmeta before Fix\" ကို enable ထားဖို့ လိုတယ် — " +
                    "ဒါမှမဟုတ် Restore (flags=0) ကို Orange State Fix / DM Fix ခလုတ်ကနေ ရွေးပါ။",
                    "Undo Vbmeta", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string part = Path.GetFileNameWithoutExtension(lastVbmetaBackupPath);
            int underscore = part.IndexOf('_');
            if (underscore > 0) part = part.Substring(0, underscore);

            if (MessageBox.Show(
                    "မူရင်း " + part + " backup ကို ပြန်ရေးမလား?\n\n" +
                    "• File: " + Path.GetFileName(lastVbmetaBackupPath) + "\n" +
                    "• Fix အတွက် disabled flags တွေ ပြန် original အတိုင်း ဖြစ်သွားမယ်",
                    "Undo Vbmeta Fix", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            bool ok = await RunPartitionOpAsync("w", part, lastVbmetaBackupPath,
                "Undo Vbmeta Fix [" + part + "]");
            if (ok)
            {
                Log("[OK] Restored original " + part + " from backup.", Color.LightGreen);
                if (chkMtkAutoReboot != null && chkMtkAutoReboot.Checked)
                    await ExecuteCommandCleanAsync("python", BuildMtkOpArgs("reset"), "Reboot after Undo Vbmeta");
            }
            else
            {
                Log("[!] Undo failed — check device connection.", Color.Orange);
            }
        }

        // ================= FIRMWARE FLASH (Qualcomm / Samsung / SPD) =================
        // MTK panel နဲ့ တူတူပုံစံ — folder/file ရွေး → START FLASH
        private string FindRawprogram(string dir)
        {
            try
            {
                string[] files = Directory.GetFiles(dir, "rawprogram*.xml").Where(f => !Path.GetFileName(f).Equals("rawprogram_skip_userdata.xml", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (files.Length > 0) return files[0];

                // "xxx_rawprogram.xml" / "_selected_rawprogram.xml" စတဲ့ ပုံစံတွေလည် ရှာ
                files = Directory.GetFiles(dir, "*rawprogram*.xml").Where(f => !Path.GetFileName(f).Equals("rawprogram_skip_userdata.xml", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (files.Length > 0) return files[0];
            }
            catch { }
            return "";
        }

        // rawprogram0.xml ကို ဖတ်ပြီး partition list ကို ဖြည့် (label / size_in_KB / start_sector)
        private void LoadRawprogramXml(string xmlPath)
        {
            try
            {
                ClearPartitionData();
                System.Xml.Linq.XDocument doc = System.Xml.Linq.XDocument.Load(xmlPath);
                int count = 0;

                foreach (System.Xml.Linq.XElement el in doc.Descendants("program"))
                {
                    string label = (string)el.Attribute("label");
                    if (string.IsNullOrEmpty(label)) continue;

                    string sizeKb = (string)el.Attribute("size_in_KB");
                    string startSector = (string)el.Attribute("start_sector");

                    string lengthHex = "0x0";
                    if (!string.IsNullOrEmpty(sizeKb) &&
                        double.TryParse(sizeKb, NumberStyles.Float, CultureInfo.InvariantCulture, out double kb) && kb > 0)
                    {
                        lengthHex = "0x" + ((long)(kb * 1024)).ToString("x");
                    }

                    string offset = "-";
                    if (!string.IsNullOrEmpty(startSector) && long.TryParse(startSector, out long sec))
                        offset = "0x" + (sec * 512).ToString("x");

                    AddPartitionRow(label, offset, lengthHex);
                    count++;
                }

                Log("  • Partitions : " + count + " rows (read from rawprogram XML)", Color.LightGreen);
                if (count == 0)
                    Log("[!] No <program> entries found in the XML.", Color.Orange);
            }
            catch (Exception ex)
            {
                Log("[!] Could not read the rawprogram XML: " + ex.Message, Color.Orange);
            }
        }

        // XML မရှိရင် folder ထဲက .img/.bin ဖိုင်နာမည်တွေကနေ partition list ဖြည့်
        private void LoadFirmwareFolderImages(string dir)
        {
            try
            {
                ClearPartitionData();
                int count = 0;
                string[] files = Directory.GetFiles(dir, "*.img");
                string[] bins = Directory.GetFiles(dir, "*.bin");
                foreach (string f in files) { AddPartitionRow(Path.GetFileNameWithoutExtension(f), "-", "0x" + new FileInfo(f).Length.ToString("x")); count++; }
                foreach (string f in bins) { AddPartitionRow(Path.GetFileNameWithoutExtension(f), "-", "0x" + new FileInfo(f).Length.ToString("x")); count++; }

                Log("  • Partitions : " + count + " rows (from .img/.bin file names)", Color.LightGreen);
            }
            catch (Exception ex)
            {
                Log("[!] Could not read the folder: " + ex.Message, Color.Orange);
            }
        }

        private string FindFileInToolFolders(params string[] names)
        {
            foreach (string name in names)
            {
                string file = ShopServices.FindTool(Application.StartupPath, name);
                if (!string.IsNullOrEmpty(file)) return file;
            }
            return "";
        }

        private string ResolveToolPath(string fileName)
        {
            if (Path.IsPathRooted(fileName) || fileName.Contains(Path.DirectorySeparatorChar) ||
                fileName.Contains(Path.AltDirectorySeparatorChar)) return fileName;
            string logicalName = fileName == "python" ? "python.exe" : fileName == "heimdall" ? "heimdall.exe" : fileName;
            string local = FindFileInToolFolders(logicalName);
            if (!string.IsNullOrEmpty(local)) return local;
            if (shopToolNames.Contains(logicalName, StringComparer.OrdinalIgnoreCase))
                throw new FileNotFoundException("Configure " + logicalName + " in Setup / Backups > Tool setup.");
            foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(folder)) continue;
                    string candidate = Path.Combine(folder.Trim().Trim('"'), fileName);
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
                catch (ArgumentException) { }
            }
            return fileName;
        }

        // ---- Qualcomm: edl.py နဲ့ အစစ် flash ----
        private void BtnQcPickFirmware_Click(object sender, EventArgs e)
        {
            using (FolderBrowserDialog fbd = new FolderBrowserDialog
            {
                Description = "Qualcomm firmware folder ရွေးပါ (rawprogram XML သို့ .img ဖိုင်များ)"
            })
            {
                if (fbd.ShowDialog() != DialogResult.OK) return;
                qcFirmwareFolder = fbd.SelectedPath;
                txtQcFirmware.Text = qcFirmwareFolder;
                SaveSettings();

                string xml = FindRawprogram(qcFirmwareFolder);
                int imgs = 0;
                try { imgs = Directory.GetFiles(qcFirmwareFolder, "*.img").Length + Directory.GetFiles(qcFirmwareFolder, "*.bin").Length; } catch { }

                // Partition list ကို ဖြည့် — XML ရှိရင် XML အတိုင်း၊ မရှိရင် ဖိုင်နာမည်တွေကနေ
                if (!string.IsNullOrEmpty(xml)) LoadRawprogramXml(xml);
                else LoadFirmwareFolderImages(qcFirmwareFolder);

                Log("========================================================", Color.FromArgb(0, 180, 255));
                Log("   QUALCOMM FIRMWARE LOADED", Color.White);
                Log("========================================================", Color.FromArgb(0, 180, 255));
                Log("  • Folder : " + qcFirmwareFolder, Color.Cyan);
                Log("  • XML    : " + (string.IsNullOrEmpty(xml) ? "(none - will use folder-based wl)" : Path.GetFileName(xml)), Color.White);
                Log("  • Images : " + imgs + " files (.img/.bin)", Color.White);
                Log("[*] Pressing START FLASH will " + (string.IsNullOrEmpty(xml) ? "write partitions matching the file names in the folder." : "write partitions as listed in the rawprogram XML."), Color.Orange);
            }
        }

        private async void BtnQcStartFlash_Click(object sender, EventArgs e)
        {
            await RunFlashWorkflowAsync(BtnQcStartFlashAsync);
        }

        private async Task BtnQcStartFlashAsync()
        {
            if (string.IsNullOrEmpty(qcFirmwareFolder) || !Directory.Exists(qcFirmwareFolder))
            {
                Log("[!] No Qualcomm firmware folder selected - pick one first.", Color.OrangeRed);
                return;
            }

            string port = cmbPorts.SelectedItem?.ToString();
            if (string.IsNullOrEmpty(port) || !port.StartsWith("COM"))
            {
                Log("[!] No COM port selected - pick the Qualcomm 9008 port first.", Color.OrangeRed);
                return;
            }

            string xmlFile = FindRawprogram(qcFirmwareFolder);
            if (!File.Exists(xmlFile)) throw new FileNotFoundException("Rawprogram XML not found.");
            string memoryType = detectedStorageKind == "UFS" ? "ufs" : "emmc";
            string patchFile = Directory.GetFiles(qcFirmwareFolder, "patch*.xml").Select(Path.GetFileName).FirstOrDefault();

            Log("Operation : QUALCOMM FLASH PARTITION\r\n", Color.FromArgb(0, 200, 255));
            Log($" Waiting Qualcomm 9008 Port... {port}", Color.White);
            Log(" Connecting to SAHARA Protocol... Ok", Color.White);
            Log(" Identifying Device Mode... Ok ( SAHARA MODE )", Color.White);

            // ၁။ Sahara Handshake ကို အရင်ဆုံး မဖြစ်မနေ လုပ်ရပါမည်
            string loaderFile = !string.IsNullOrEmpty(edlLoaderPath) ? edlLoaderPath : Path.Combine(qcFirmwareFolder, "prog_firehose_lite.elf");
            Log(" Sending Firehose Programmer Loader... Ok", Color.White);

            bool saharaOk = await ExecuteCommandCleanAsync("QSaharaServer.exe", $"-p \\\\.\\{port} -s 13:\"{loaderFile}\"", "Sahara", false, false);
            if (!saharaOk)
            {
                Log(" [FAIL] Sahara Handshake Failed!", Color.Red);
                return;
            }

            Log(" Connecting to firehose mode... Ok", Color.White);
            await Task.Delay(1000);
            if (stopRequested) return;

            // ၂။ EDL Auth Bypass (MiFlash NonAuth လို) — auth device တွေမှာ fh_loader မတိုင်မီ sig ပို့
            // Sahara loader တင်ပြီးမို့ loaderPath ပြန်မတင် — firehose session ပေါ်မှာ sig သာ
            {
                Log(" Bypassing EDL authentication (sig)...", Color.Orange);
                SetProgress(35, "Auth Bypass");
                var authRes = await EdlAuth.BypassAsync(port, null, msg =>
                {
                    if (msg.StartsWith("[+]")) Log(" " + msg, Color.LightGreen);
                    else if (msg.StartsWith("[!]")) Log(" " + msg, Color.Orange);
                    else if (msg.StartsWith("[*]") || msg.StartsWith("    ")) Log(" " + msg, Color.White);
                }, default, string.IsNullOrEmpty(edlSigPath) ? null : edlSigPath);
                if (authRes.Ok)
                {
                    qcAuthed = true;
                    qcAuthedPort = port;
                    Log(" Auth Bypass... Ok" + (string.IsNullOrEmpty(authRes.TargetName) ? "" : " (TargetName=" + authRes.TargetName + ")"), Color.LightGreen);
                }
                else
                {
                    qcAuthed = false;
                    qcAuthedPort = "";
                    // Auth မလိုတဲ့ device တွေမှာ fail ဖြစ်နိုင် — hard stop မလုပ်ဘဲ warning သာ
                    Log(" [!] Auth bypass: " + authRes.Message + " — continuing (device may not require auth).", Color.Orange);
                }
                if (stopRequested) return;
                await Task.Delay(300);
            }

            // ၃။ [FEATURE 1] Flash မတိုင်မီ EFS Backup အမှန်တကယ် ယူခြင်း (Sahara အောင်မြင်ပြီးမှ)
            if (chkQcBackupEfsFirst.Checked)
            {
                string efsDir = Path.Combine(qcFirmwareFolder, "efs_backup_" + Guid.NewGuid().ToString("N"));
                try { Directory.CreateDirectory(efsDir); }
                catch (Exception ex) { Log("[!] EFS backup dir create failed: " + ex.Message, Color.Red); }
                Log($"[*] [Option] Backing up EFS (persist/modemst) to {Path.GetFileName(efsDir)}...", Color.Orange);

                // fh_loader ဖြင့် persist, modemst1, modemst2 များကို ဖတ်ယူ Backup ပြုလုပ်ခြင်း
                var backupFiles = ReviewSafety.PrepareEfsBackup(xmlFile, efsDir);
                bool backupOk = await ExecuteCommandCleanAsync("fh_loader.exe", $"--port=\\\\.\\{port} --search_path=\"{efsDir}\" --sendxml=efs_read.xml --convertprogram2read --memoryname={memoryType} --noprompt", "EFS Backup", false, false);
                if (!backupOk || !backupFiles.All(file => ReviewSafety.HasBackup(file.Key, file.Value)))
                {
                    workflowFailed = true;
                    Log("[FAIL] EFS backup is incomplete; flash cancelled.", Color.Red);
                    return;
                }
                Log(" Backing up EFS... Ok", Color.LightGreen);
            }

            // ၃။ [FEATURE 2] Skip Userdata - XML ထဲမှ userdata လိုင်းများကို ဖယ်ထုတ်ခြင်း
            string finalXmlFile = xmlFile;
            if (chkQcSkipUserdata.Checked)
            {
                Log("[*] [Option] Preparing XML (Skipping Userdata Partitions)...", Color.Orange);
                try
                {
                    string tempXmlPath = Path.Combine(qcFirmwareFolder, "rawprogram_skip_userdata.xml");
                    ReviewSafety.FilterUserdata(xmlFile, tempXmlPath);
                    finalXmlFile = tempXmlPath;
                    Log(" Skip Userdata Filter Applied... Ok", Color.LightGreen);
                }
                catch (Exception ex)
                {
                    workflowFailed = true;
                    Log("[FAIL] XML filter failed; flash cancelled: " + ex.Message, Color.Red);
                    return;
                }
            }

            Log(" Loading Partition Table... Ok", Color.White);

            // ၃။။ fh_loader.exe က 2GB ကျော် image ကို "Read 0 bytes" error နဲ့ မရေးနိုင်လို့ —
            // system.img စလိုက် (>2GB) တွေကို chunk ခွဲပြီး program entry အများကြီးထုတ်မယ်။
            string flashXmlFile = finalXmlFile;
            try
            {
                string splitXmlPath = Path.Combine(qcFirmwareFolder,
                    Path.GetFileNameWithoutExtension(finalXmlFile) + ReviewSafety.SplitXmlSuffix);
                int added = ReviewSafety.SplitOversizedImages(finalXmlFile, qcFirmwareFolder, splitXmlPath,
                    msg => Log(msg, Color.Orange));
                if (added > 0)
                {
                    flashXmlFile = splitXmlPath;
                    Log($" Large image split applied ({added} program entries)... Ok", Color.LightGreen);
                }
            }
            catch (Exception ex)
            {
                workflowFailed = true;
                Log("[FAIL] Large image split failed; flash cancelled: " + ex.Message, Color.Red);
                return;
            }

            // ၄။ Main Flash Execution
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string sendXmlParam = Path.GetFileName(flashXmlFile) + (!string.IsNullOrEmpty(patchFile) ? $",{patchFile}" : "");

            // Flash ပြီးမှ FRP Reset/Reboot လုပ်မည်ဖြစ်၍ reset flag ကို နောက်ဆုံးမှ သီးသန့်ပို့ပါမည်
            string fhArgs = $"--port=\\\\.\\{port} --sendxml=\"{sendXmlParam}\" --search_path=\"{qcFirmwareFolder}\" --noprompt --showpercentagecomplete --zlpawarehost=1 --memoryname={memoryType}";

            bool flashOk = await ExecuteCommandCleanAsync("fh_loader.exe", fhArgs, "Firehose Flash", false, false);

            // ၅။ [FEATURE 3] Reset FRP after Flash - Flash ပြီးချိန်တွင် FRP Erase ပြုလုပ်ခြင်း
            if (flashOk && chkQcResetFrpAfter.Checked)
            {
                Log(" Resetting Factory Reset Protection Lock (FRP)...", Color.Orange);
                // Firehose ဖြင့် frp နှင့် config partition ကို Erase သို့မဟုတ် format လုပ်ခြင်း
                if (!await ExecuteCommandCleanAsync("fh_loader.exe", $"--port=\\\\.\\{port} --erase=frp --memoryname={memoryType} --noprompt", "Erase FRP", false, false)) return;
                if (!await ExecuteCommandCleanAsync("fh_loader.exe", $"--port=\\\\.\\{port} --erase=config --memoryname={memoryType} --noprompt", "Erase Config", false, false)) return;
                Log(" Resetting Factory Reset Protection Lock... Ok", Color.LightGreen);
            }

            // Auto reboot — RunFlashWorkflowAsync က workflow ပြီးမှ တစ်ခါတည်း ပြန်ပို့တယ်
            // (ဤနေရာမှာ ထပ်မလုပ် — double reboot ကာကွယ်)။
            sw.Stop();
            if (flashOk)
            {
                Log(" Patching patch0.xml ... Ok", Color.LightGreen);
                TimeSpan t = sw.Elapsed;
                Log($" Elapsed Time... {t.Minutes} minutes {t.Seconds} seconds", Color.Gainsboro);
                Log($" Finished at local time: [{DateTime.Now:yy.MM.dd HH:mm:ss}]", Color.Cyan);
            }
        }

        // ================= SAMSUNG — DIRECT HEIMDALL CLI ENGINE =================
        // Odin3.exe ကို လုံးဝ မသုံးတော့ဘူး — heimdall.exe ကို CLI backend အနေနဲ့ run ပြီး
        // output/progress (0-100% + speed) ကို ExecuteCommandCleanAsync ကတဆင့် တိုက်ရိုက် ပြတယ်။
        //   • .img          → heimdall flash --<PARTITION> "<file>"
        //   • .tar/.tar.md5 → archive ထဲက .img တွေကို ဖြည်ပြီး တစ်ခုချင်း flash
        //   • Download PIT  → heimdall download-pit --output "<file>" → binary PIT ဖတ်ပြီး grid ဖြည့်

        private const int PitEntrySize = 132;        // 9×u32 + 3×32-byte (UTF-16) name field
        private const long SamsungBlockSize = 512;   // PIT block unit

        private string FindHeimdall()
        {
            string h = FindFileInToolFolders("heimdall.exe");
            return string.IsNullOrEmpty(h) ? "heimdall" : h;
        }

        // ---- Samsung: firmware/image ရွေး (.img / .tar / .tar.md5 — အားလုံး heimdall နဲ့ တိုက်ရိုက်) ----
        private void BtnSamPickFirmware_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog ofd = new OpenFileDialog
            {
                Filter = "Samsung firmware (*.img;*.tar;*.tar.md5)|*.img;*.tar;*.tar.md5|All Files (*.*)|*.*",
                Title = "Samsung firmware/image ကို ရွေးပါ"
            })
            {
                if (ofd.ShowDialog() != DialogResult.OK) return;
                samFirmwareFile = ofd.FileName;
                txtSamFirmware.Text = samFirmwareFile;

                ClearPartitionData(); // Partition ဇယားဟောင်းကို ရှင်းထုတ်ခြင်း

                bool isTar = samFirmwareFile.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)
                          || samFirmwareFile.EndsWith(".tar.md5", StringComparison.OrdinalIgnoreCase);

                int count = 0;

                if (isTar)
                {
                    try
                    {
                        // .tar / .tar.md5 archive ထဲက partition ဖိုင်များကို ဖတ်ပြီး Grid ထဲ ထည့်ခြင်း
                        using (FileStream fs = File.OpenRead(samFirmwareFile))
                        using (TarReader reader = new TarReader(fs))
                        {
                            TarEntry entry;
                            while ((entry = reader.GetNextEntry()) != null)
                            {
                                if (entry.EntryType != TarEntryType.RegularFile &&
                                    entry.EntryType != TarEntryType.V7RegularFile) continue;

                                string name = Path.GetFileName(entry.Name);
                                if (string.IsNullOrEmpty(name)) continue;

                                string partName = MapSamsungPartition(name);
                                if (string.IsNullOrEmpty(partName)) partName = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();

                                string lenHex = "0x" + entry.Length.ToString("X");
                                AddPartitionRow(partName, "-", lenHex);
                                count++;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("[!] Error reading archive: " + ex.Message, Color.Orange);
                    }
                }
                else
                {
                    // .img ဖိုင်တစ်ခုတည်း ရွေးထားပါက
                    string partName = MapSamsungPartition(samFirmwareFile);
                    if (string.IsNullOrEmpty(partName)) partName = Path.GetFileNameWithoutExtension(samFirmwareFile).ToUpperInvariant();
                    long fileLen = new FileInfo(samFirmwareFile).Length;
                    AddPartitionRow(partName, "-", "0x" + fileLen.ToString("X"));
                    count = 1;
                }

                // Checkbox အားလုံးကို Default အနေဖြင့် အမှန်ခြစ်ပေးခြင်း
                SetAllPartitionChecks(true);

                Log("========================================================", Color.FromArgb(0, 180, 255));
                Log("   SAMSUNG FIRMWARE LOADED", Color.White);
                Log("========================================================", Color.FromArgb(0, 180, 255));
                Log("  • File       : " + Path.GetFileName(samFirmwareFile), Color.Cyan);
                Log("  • Size       : " + (new FileInfo(samFirmwareFile).Length / (1024.0 * 1024.0)).ToString("F1") + " MB", Color.White);
                Log("  • Partitions : " + count + " partitions loaded into list", Color.LightGreen);
            }
        }

        private static string ReadUtf16Fixed(byte[] data, int offset, int byteLen)
        {
            if (offset < 0 || offset + byteLen > data.Length) return "";
            string s = Encoding.Unicode.GetString(data, offset, byteLen);
            int nul = s.IndexOf('\0');
            if (nul >= 0) s = s.Substring(0, nul);
            return s.Trim();
        }

        // Samsung PIT (binary): header 28 bytes (magic 0x12349876 + entry count + reserved),
        // entry တစ်ခု 132 bytes၊ နာမည် field တွေက UTF-16LE (32 bytes = 16 chars)။
        private List<PitEntry> ParsePit(string path)
        {
            List<PitEntry> list = new List<PitEntry>();
            try
            {
                byte[] data = File.ReadAllBytes(path);
                if (data.Length < 28 + PitEntrySize) return list;
                if (BitConverter.ToUInt32(data, 0) != 0x12349876) return list;

                int count = (int)BitConverter.ToUInt32(data, 4);
                int maxPossible = (data.Length - 28) / PitEntrySize;
                if (count <= 0 || count > maxPossible) count = maxPossible;

                for (int i = 0, off = 28; i < count && off + PitEntrySize <= data.Length; i++, off += PitEntrySize)
                {
                    PitEntry e = new PitEntry
                    {
                        BlockOffset = BitConverter.ToUInt32(data, off + 20),
                        BlockCount = BitConverter.ToUInt32(data, off + 24),
                        Name = ReadUtf16Fixed(data, off + 36, 32),
                        FlashFilename = ReadUtf16Fixed(data, off + 68, 32)
                    };
                    if (!string.IsNullOrEmpty(e.Name)) list.Add(e);
                }
            }
            catch (Exception ex) { Log("[x] PIT parse error: " + ex.Message, Color.Red); }
            return list;
        }

        // heimdall download-pit → binary PIT ကို သိမ်းပြီး ဖတ် → partitions grid ဖြည့်
        private async Task<bool> DownloadPitAsync(string heimdall, bool silent)
        {
            string outPath;
            if (silent)
            {
                outPath = Path.Combine(Application.StartupPath,
                    "samsung_pit_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".pit");
            }
            else
            {
                using (SaveFileDialog sfd = new SaveFileDialog
                {
                    Filter = "PIT file (*.pit)|*.pit|All Files (*.*)|*.*",
                    FileName = "samsung_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".pit",
                    Title = "Save Samsung PIT table"
                })
                {
                    if (sfd.ShowDialog() != DialogResult.OK) return false;
                    outPath = sfd.FileName;
                }
            }

            bool ok = await ExecuteCommandCleanAsync(heimdall, "download-pit --output \"" + outPath + "\"",
                                                     "Samsung - Download PIT");
            if (!ok || !File.Exists(outPath))
            {
                Log("[x] PIT download failed - phone must be in Download Mode (Vol- + Home + Power / adb reboot download).", Color.Red);
                return false;
            }

            List<PitEntry> entries = ParsePit(outPath);
            if (entries.Count == 0)
            {
                Log("[x] Cannot read the PIT file (bad binary format) - " + Path.GetFileName(outPath), Color.Red);
                return false;
            }

            samPitEntries = entries;
            ClearPartitionData();
            foreach (PitEntry e in entries)
            {
                string offHex = "0x" + (e.BlockOffset * SamsungBlockSize).ToString("X");
                string lenHex = "0x" + (e.BlockCount * SamsungBlockSize).ToString("X");
                AddPartitionRow(e.Name, offHex, lenHex);
            }

            Log("[OK] PIT: " + entries.Count + " partitions (" + Path.GetFileName(outPath) + ")", Color.LightGreen);
            foreach (PitEntry e in entries)
            {
                if (!string.IsNullOrEmpty(e.FlashFilename))
                    Log("   " + e.Name.PadRight(20) + " <- " + e.FlashFilename, Color.Gainsboro);
            }
            return true;
        }

        private async void BtnSamDownloadPit_Click(object sender, EventArgs e)
        {
            string heimdall = FindHeimdall();
            if (string.IsNullOrWhiteSpace(await ExecuteCommandQuickAsync(heimdall, "version")))
            {
                MessageBox.Show("heimdall.exe မတွေ့ပါ။\n\nPIT download / flash အတွက် heimdall.exe လိုပါတယ် —\n" +
                                "• ဒီ tool folder ထဲ (သို့) PATH မှာ ထည့်ပါ",
                    "heimdall required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            await DownloadPitAsync(heimdall, silent: false);
        }

        // .tar / .tar.md5 → .img/.bin တွေကို temp folder ထဲ ဖြည်တယ် (.tar.md5 ရဲ့ အဆုံး MD5 ကို ဖြတ်တယ်)။
        private List<string> ExtractTarImages(string tarPath, string outDir)
        {
            try { return ReviewSafety.ExtractImages(tarPath, outDir); }
            catch (Exception ex)
            {
                Log("[x] Cannot extract the archive: " + ex.Message, Color.Red);
                return new List<string>();
            }
        }
        // ဖိုင်နာမည် → Samsung partition နာမည် (၁) PIT Flash Filename (၂) PIT partition name (၃) Odin alias
        private string MapSamsungPartition(string file)
        {
            string fname = Path.GetFileName(file).ToLowerInvariant();
            foreach (PitEntry e in samPitEntries)
            {
                if (!string.IsNullOrEmpty(e.FlashFilename) &&
                    string.Equals(e.FlashFilename.ToLowerInvariant(), fname, StringComparison.OrdinalIgnoreCase))
                    return e.Name.ToUpperInvariant();
            }

            string baseName = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            foreach (PitEntry e in samPitEntries)
            {
                if (string.Equals(e.Name.ToLowerInvariant(), baseName, StringComparison.OrdinalIgnoreCase))
                    return e.Name.ToUpperInvariant();
            }

            switch (baseName)
            {
                case "boot": return "BOOT";
                case "recovery": return "RECOVERY";
                case "system": return "SYSTEM";
                case "vendor": return "VENDOR";
                case "super": return "SUPER";
                case "vbmeta": return "VBMETA";
                case "dtbo": return "DTBO";
                case "modem": return "RADIO";
                case "radio": return "RADIO";
                case "cache": return "CACHE";
                case "userdata": return "USERDATA";
                case "hidden": return "HIDDEN";
                case "sboot": return "BOOTLOADER";
                case "cm": return "CM";
                case "param": return "PARAM";
                case "ldfw": return "LDFW";
                case "tzsw": return "TZSW";
                case "efs": return "EFS";
                case "persist": return "PERSIST";
                case "omr": return "OMR";
                case "prism": return "PRISM";
                case "optics": return "OPTICS";
                case "keydata": return "KEYDATA";
                case "keystorage": return "KEYSTORAGE";
                case "misc": return "MISC";
                case "up_param": return "UP_PARAM";
                case "steady": return "STEADY";
                default: return "";
            }
        }

        private async void BtnSamStartFlash_Click(object sender, EventArgs e)
        {
            await RunFlashWorkflowAsync(BtnSamStartFlashAsync);
        }

        private async Task BtnSamStartFlashAsync()
        {
            if (string.IsNullOrEmpty(samFirmwareFile) || !File.Exists(samFirmwareFile))
            {
                MessageBox.Show("Firmware ဖိုင် မရွေးရသေးပါ — 📂 Browse နဲ့ အရင် ရွေးပါ။", "Firmware",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string heimdall = FindHeimdall();
            if (string.IsNullOrWhiteSpace(await ExecuteCommandQuickAsync(heimdall, "version")))
            {
                MessageBox.Show("heimdall.exe မတွေ့ပါ။\n\n• ဒီ tool folder ထဲ (သို့) PATH မှာ ထည့်ပါ",
                    "heimdall required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // [Option] Backup PIT first (PIT ရှိရင် partition mapping ကပိုတိကျတယ်)
            if (chkSamBackupPit.Checked)
                await DownloadPitAsync(heimdall, silent: true);

            bool isTar = samFirmwareFile.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)
                      || samFirmwareFile.EndsWith(".tar.md5", StringComparison.OrdinalIgnoreCase);

            List<string> parts = new List<string>();
            List<string> images = new List<string>();
            string tempDir = null;

            if (isTar)
            {
                tempDir = Path.Combine(Path.GetTempPath(), "pmk_samfw_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                try { Directory.CreateDirectory(tempDir); }
                catch (Exception ex) { Log("[!] Samsung extract dir create failed: " + ex.Message, Color.Red); }

                Log("[*] Extracting " + Path.GetFileName(samFirmwareFile) + " ...", Color.Cyan);
                List<string> extracted = ExtractTarImages(samFirmwareFile, tempDir);
                if (extracted.Count == 0)
                {
                    MessageBox.Show("Archive ထဲမှာ .img/.bin ဖိုင် မတွေ့ပါ။", "Samsung",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                foreach (string f in extracted)
                {
                    string p = MapSamsungPartition(f);
                    if (string.IsNullOrEmpty(p))
                    {
                        Log("[!] Skip (no PIT / alias match): " + Path.GetFileName(f), Color.Orange);
                        continue;
                    }
                    parts.Add(p);
                    images.Add(f);
                }
            }
            else
            {
                string p = MapSamsungPartition(samFirmwareFile);
                if (string.IsNullOrEmpty(p))
                {
                    p = PromptInput("Samsung Partition", "Partition နာမည် ရိုက်ပါ (ဥပမာ BOOT / RECOVERY / SYSTEM / SUPER)");
                    if (string.IsNullOrWhiteSpace(p)) return;
                    p = p.Trim().ToUpperInvariant();
                    if (!IsValidPartitionName(p))
                    {
                        MessageBox.Show("Partition နာမည် မမှန်ပါ (a-z, 0-9, _ , - သာ သုံးပါ)။", "Samsung",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                }
                parts.Add(p);
                images.Add(samFirmwareFile);
            }

            if (parts.Count == 0)
            {
                MessageBox.Show("Flash လုပ်စရာ partition မရှိပါ (PIT download လုပ်ပြီး ပြန်စမ်းပါ)။", "Samsung",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string plan = "";
            for (int i = 0; i < parts.Count; i++)
                plan += "• " + parts[i].PadRight(16) + " <- " + Path.GetFileName(images[i]) + "\n";

            if (MessageBox.Show("heimdall CLI နဲ့ flash လုပ်မလား?\n\n" + plan + "\n" +
                                "• ဖုန်းက Download Mode မှာ ရှိရမယ်\n" +
                                "• ဖုန်းပိုင်ရှင် ခွင့်ပြုချက် ရှိမှ လုပ်ပါ\n\nဆက်လုပ်မလား?",
                    "Confirm Flash (heimdall)", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            int done = await ReviewSafety.RunSequenceAsync(parts.Count, () => stopRequested, async i =>
            {
                string args = "flash --" + parts[i] + " \"" + images[i] + "\"";
                if (i < parts.Count - 1 || !chkSamAutoReboot.Checked) args += " --no-reboot";
                return await ExecuteCommandCleanAsync(heimdall, args, "Samsung Flash [" + parts[i] + "]");
            });

            bool completed = done == parts.Count && !stopRequested;
            Log((completed ? "[OK]" : stopRequested ? "[STOPPED]" : "[FAIL]") +
                " Samsung flash: " + done + " / " + parts.Count + " partition(s).",
                completed ? Color.LightGreen : Color.OrangeRed);

            try { if (tempDir != null && Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }

        // ---- Spreadtrum: PAC → unpac (extract) + spd_dump (flash) ----
        // spd_dump ရဲ့ တကယ့် CLI (prebuilt/stable build ရဲ့ `-h` အတိုင်း):
        //   spd_dump [OPTIONS] [COMMANDS] [EXIT COMMANDS]
        //     --wait <sec>                              device ကို စောင့်တဲ့ အချိန်
        //     fdl <FILE> <addr>                         FDL1/FDL2 ကို memory ထဲ တင်
        //     exec                                      တင်ထားတဲ့ FDL ကို run (fdl2 prompt ရောက်)
        //     w|write_part <part> <FILE>                partition ရေး
        //     r <part>                                  partition ဖတ် (path <dir> ထဲ သိမ်း)
        //     e|erase_part <part>                       partition ဖျက်
        //     p|print                                   partition list ပြ
        //     reset / reboot-fastboot ...               exit commands
        //   • ဒီ build မှာ --serialport / --pac flag မရှိဘူး — port ကို driver/libusb က auto ရှာတယ်။
        //   • PAC ကို အရင် unpac.exe နဲ့ ဖြည်ရတယ် (FDL1/FDL2 + partition images ရမယ်)။
        //   • FDL load address တွေက chip အလိုက် ကွာတတ်တာမို့ env နဲ့ ချိန်လို့ရတယ်:
        //       PMK_SPD_FDL1_ADDR (default 0x5000), PMK_SPD_FDL2_ADDR (default 0x9efffe00),
        //       PMK_SPD_WAIT (default 300), PMK_SPD_EXTRA (command အစမှာ ထပ်ထည့်)

        private string spdExtractDir = "";
        private string spdPacExtractedFrom = "";

        private string FindSpdDump()
        {
            var tools = ShopServices.LoadTools();
            if (tools.TryGetValue("spd_dump.exe", out string configured) && File.Exists(configured)) return configured;
            string sprd = Path.Combine(Application.StartupPath, "spd", "sprd", "spd_dump.exe");
            if (File.Exists(sprd)) return sprd;
            string s = FindFileInToolFolders("spd_dump.exe");
            return string.IsNullOrEmpty(s) ? "spd_dump" : s;
        }

        private string FindUnpac()
        {
            string u = FindFileInToolFolders("unpac.exe");
            return string.IsNullOrEmpty(u) ? "unpac" : u;
        }

        // PAC ကို unpac.exe နဲ့ ဖြည်တယ် (session တစ်ခုအတွင်း cache — ထပ်ဖြည်စရာ မလို)
        private async Task<bool> EnsurePacExtractedAsync()
        {
            if (string.IsNullOrEmpty(spdFirmwareFile) || !File.Exists(spdFirmwareFile))
            {
                MessageBox.Show("PAC firmware ဖိုင် မရွေးရသေးပါ — 📂 PAC နဲ့ အရင် ရွေးပါ။", "PAC",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            if (!string.IsNullOrEmpty(spdPacExtractedFrom) &&
                string.Equals(spdPacExtractedFrom, spdFirmwareFile, StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(spdExtractDir))
                return true;

            string dir = Path.Combine(Path.GetTempPath(),
                "pmk_spd_" + Path.GetFileNameWithoutExtension(spdFirmwareFile));
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex) { Log("[!] SPD extract dir create failed: " + ex.Message, Color.Red); }

            Log("[*] Extracting PAC (unpac): " + Path.GetFileName(spdFirmwareFile), Color.Cyan);
            bool ok = await ExecuteCommandCleanAsync(FindUnpac(),
                "-d \"" + dir + "\" extract \"" + spdFirmwareFile + "\"", "SPD - Unpack PAC", false, true);
            if (!ok)
            {
                Log("[x] PAC extract failed - check unpac.exe and the PAC file, then retry.", Color.Red);
                return false;
            }

            spdExtractDir = dir;
            spdPacExtractedFrom = spdFirmwareFile;
            Log("[OK] PAC extracted -> " + dir, Color.LightGreen);
            return true;
        }

        // FDL Prefix တည်ဆောက်ခြင်း (PAC ထဲမှဖြစ်စေ၊ spd folder ထဲမှဖြစ်စေ Auto ရှာယူခြင်း)
        private string BuildSpdFdlPrefix()
        {
            string fdl1 = "", fdl2 = "";

            // ၀။ SPD Loader row (user pick) — filename ထဲ fdl2 ပါရင် fdl2 အဖြစ်၊ မဟုတ်ရင် fdl1 override
            if (!string.IsNullOrEmpty(spdLoaderPath) && File.Exists(spdLoaderPath))
            {
                string ln = Path.GetFileName(spdLoaderPath).ToLowerInvariant();
                if (ln.Contains("fdl2")) fdl2 = spdLoaderPath;
                else fdl1 = spdLoaderPath;
            }

            // ၁။ PAC ဖြည်ထားတာ ရှိရင် အရင်သုံးပါ (user pick ရှိရင် အဲဒါပဲ ကျန်)
            if (Directory.Exists(spdExtractDir))
            {
                foreach (string f in Directory.GetFiles(spdExtractDir))
                {
                    string n = Path.GetFileName(f).ToLowerInvariant();
                    if (n.Contains("fdl1") && string.IsNullOrEmpty(fdl1)) fdl1 = f;
                    else if (n.Contains("fdl2") && string.IsNullOrEmpty(fdl2)) fdl2 = f;
                }
            }

            // ၂။ PAC မရှိပါက spd/loaders folder ထဲက FDL များကို အလိုအလျောက် ရှာဖွေပါ
            if (string.IsNullOrEmpty(fdl1) || string.IsNullOrEmpty(fdl2))
            {
                string loadersRoot = Path.Combine(Application.StartupPath, "spd", "loaders");
                if (Directory.Exists(loadersRoot))
                {
                    // ရွေးထားတဲ့ chipset ကို loader folder နဲ့ တိုက်စစ်ပါ (SpdModelMatchesLoader နဲ့ တူညီစွာ)
                    string selModel = _spdSelectedModel.Trim();
                    string matchedDir = "";

                    if (!string.IsNullOrEmpty(selModel))
                    {
                        foreach (string dir in Directory.GetDirectories(loadersRoot))
                        {
                            string dn = Path.GetFileName(dir).ToLowerInvariant();
                            int u = dn.IndexOf('_');
                            string chip = new string((u > 0 ? dn.Substring(0, u) : dn)
                                .Where(c => char.IsLetterOrDigit(c)).ToArray());
                            if (chip.Length > 0 && SpdModelMatchesLoader(selModel, new List<string> { chip }))
                            {
                                matchedDir = dir;
                                break;
                            }
                        }
                    }

                    // တိုက်မတွေ့ရင် ပထမဆုံး folder ကို သုံး
                    if (string.IsNullOrEmpty(matchedDir))
                    {
                        var dirs = Directory.GetDirectories(loadersRoot);
                        if (dirs.Length > 0)
                        {
                            matchedDir = dirs[0];
                            if (!string.IsNullOrEmpty(selModel))
                                Log("[!] FDL loader မရှိတဲ့ chipset: " + selModel +
                                    " — အရန်အဖြင့် " + Path.GetFileName(matchedDir) + " သုံးမှာ မှားနိုင်", Color.Orange);
                        }
                    }

                    // matched folder ထဲက fdl1/fdl2 ကို ရှာ — "-sign" ကို ဦးစွာယူ (secure boot device များအတွက်)
                    if (!string.IsNullOrEmpty(matchedDir))
                    {
                        foreach (string file in Directory.GetFiles(matchedDir, "*.bin")
                            .OrderByDescending(f => f.ToLowerInvariant().Contains("-sign")))
                        {
                            string n = Path.GetFileName(file).ToLowerInvariant();
                            if (n.Contains("fdl1") && string.IsNullOrEmpty(fdl1)) fdl1 = file;
                            else if (n.Contains("fdl2") && string.IsNullOrEmpty(fdl2)) fdl2 = file;
                        }
                    }

                    // ၄။ မတွေ့ရင် အကုန်ရှာ (fallback)
                    if (string.IsNullOrEmpty(fdl1) || string.IsNullOrEmpty(fdl2))
                    {
                        foreach (string file in Directory.GetFiles(loadersRoot, "*.bin", SearchOption.AllDirectories))
                        {
                            string n = Path.GetFileName(file).ToLowerInvariant();
                            if (n.Contains("fdl1") && string.IsNullOrEmpty(fdl1)) fdl1 = file;
                            else if (n.Contains("fdl2") && string.IsNullOrEmpty(fdl2)) fdl2 = file;
                            if (!string.IsNullOrEmpty(fdl1) && !string.IsNullOrEmpty(fdl2)) break;
                        }
                    }
                }
            }

            // ၃။ တိုက်ရိုက် spd folder ထဲတွင် ရှိနေပါက ထပ်မံရှာဖွေပါ
            if (string.IsNullOrEmpty(fdl1) || string.IsNullOrEmpty(fdl2))
            {
                string spdDir = Path.Combine(Application.StartupPath, "spd");
                if (Directory.Exists(spdDir))
                {
                    foreach (string file in Directory.GetFiles(spdDir, "*.bin", SearchOption.AllDirectories))
                    {
                        string n = Path.GetFileName(file).ToLowerInvariant();
                        if (n.Contains("fdl1") && string.IsNullOrEmpty(fdl1)) fdl1 = file;
                        else if (n.Contains("fdl2") && string.IsNullOrEmpty(fdl2)) fdl2 = file;
                    }
                }
            }

            ToolIntegrity.Verify(fdl1);
            ToolIntegrity.Verify(fdl2);
            string a1 = Environment.GetEnvironmentVariable("PMK_SPD_FDL1_ADDR") ?? "0x5000";
            string a2 = Environment.GetEnvironmentVariable("PMK_SPD_FDL2_ADDR") ?? "0x9efffe00";

            string prefix = "--wait 300";
            if (!string.IsNullOrEmpty(fdl1)) prefix += " fdl \"" + fdl1 + "\" " + a1;
            if (!string.IsNullOrEmpty(fdl2)) prefix += " fdl \"" + fdl2 + "\" " + a2;
            prefix += " exec";

            return prefix;
        }

        // ဖြည်ထားတဲ့ folder ထဲက partition images (.bin/.img, FDL မဟုတ်တာ)
        private List<string> SpdImageFiles(out List<string> parts)
        {
            List<string> files = new List<string>();
            parts = new List<string>();
            try
            {
                if (!Directory.Exists(spdExtractDir)) return files;
                foreach (string f in Directory.GetFiles(spdExtractDir))
                {
                    string name = Path.GetFileName(f);
                    string low = name.ToLowerInvariant();
                    string ext = Path.GetExtension(low);
                    if (ext != ".bin" && ext != ".img") continue;
                    if (low.Contains("fdl1") || low.Contains("fdl2")) continue;
                    if (low.Contains("xml") || low.Contains("nvitem") || low.Contains("pac")) continue;
                    files.Add(f);
                    parts.Add(Path.GetFileNameWithoutExtension(name));
                }
            }
            catch { }
            return files;
        }

        private async void BtnSpdPickFirmware_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog ofd = new OpenFileDialog
            {
                Filter = "Spreadtrum PAC (*.pac)|*.pac|All Files (*.*)|*.*",
                Title = "Spreadtrum PAC firmware ကို ရွေးပါ"
            })
            {
                if (ofd.ShowDialog() != DialogResult.OK) return;
                spdFirmwareFile = ofd.FileName;
                txtSpdFirmware.Text = spdFirmwareFile;
                spdPacExtractedFrom = ""; // cache reset

                ClearPartitionData(); // ဇယားဟောင်း ရှင်းထုတ်ခြင်း

                Log("========================================================", Color.FromArgb(0, 180, 255));
                Log("   SPREADTRUM PAC LOADED", Color.White);
                Log("========================================================", Color.FromArgb(0, 180, 255));
                Log("  • PAC  : " + Path.GetFileName(spdFirmwareFile), Color.Cyan);
                Log("  • Size : " + (new FileInfo(spdFirmwareFile).Length / (1024.0 * 1024.0)).ToString("F1") + " MB", Color.White);

                // PAC ဖိုင်ကို ဖြည်ပြီး Partition များကို ဇယားထဲ ထည့်သွင်းခြင်း
                if (await EnsurePacExtractedAsync())
                {
                    List<string> parts;
                    List<string> files = SpdImageFiles(out parts);
                    for (int i = 0; i < files.Count; i++)
                    {
                        long sz = new FileInfo(files[i]).Length;
                        AddPartitionRow(parts[i], "-", "0x" + sz.ToString("X"));
                    }
                    SetAllPartitionChecks(true);
                    Log("  • Partitions : " + parts.Count + " partitions loaded into list", Color.LightGreen);
                }
            }
        }

        private async void BtnSpdDirectFlash_Click(object sender, EventArgs e)
        {
            await RunFlashWorkflowAsync(BtnSpdDirectFlashAsync);
        }

        private async Task BtnSpdDirectFlashAsync()
        {
            if (!await EnsurePacExtractedAsync()) return;

            string spd = FindSpdDump();
            List<string> parts;
            List<string> files = SpdImageFiles(out parts);
            if (files.Count == 0)
            {
                MessageBox.Show("ဖြည်ထားတဲ့ folder ထဲမှာ partition image (.bin/.img) မတွေ့ပါ။", "SPD",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string plan = "";
            for (int i = 0; i < files.Count && i < 20; i++)
                plan += "• " + parts[i].PadRight(16) + " <- " + Path.GetFileName(files[i]) + "\n";
            if (files.Count > 20) plan += "• ... (" + (files.Count - 20) + " more)\n";

            if (MessageBox.Show("spd_dump (CLI) နဲ့ PAC flash လုပ်မလား?\n\n" + plan + "\n" +
                                "• ဖုန်းကို Download/FDL mode မှာ ထားပါ\n" +
                                "• ဖုန်းပိုင်ရှင် ခွင့်ပြုချက် ရှိမှ လုပ်ပါ\n\nဆက်လုပ်မလား?",
                    "Confirm PAC Flash", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            string args = BuildSpdFdlPrefix();
            for (int i = 0; i < files.Count; i++)
                args += " w " + parts[i] + " \"" + files[i] + "\"";
            args += " reset";

            await ExecuteCommandCleanAsync(spd, args, "Spreadtrum PAC Flash (spd_dump)", false, true);
        }

        private async void BtnSpdReadGpt_Click(object sender, EventArgs e)
        {
            // အကယ်၍ PAC ရွေးထားခဲ့ရင်တော့ Auto ဖြည်ပေးမည်
            if (!string.IsNullOrEmpty(spdFirmwareFile) && File.Exists(spdFirmwareFile))
            {
                await EnsurePacExtractedAsync();
            }

            string spd = FindSpdDump();
            ClearPartitionData(); // Partition ဇယားဟောင်း ရှင်းထုတ်ခြင်း

            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("   SPREADTRUM / UNISOC - READ GPT PARTITIONS", Color.White);
            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("[*] Waiting for device... (Power off, hold Vol- or Vol+ and connect USB)", Color.Orange);

            // spd_dump p (print partition list) ခေါ်ယူခြင်း
            bool ok = await ExecuteCommandCleanAsync(spd, BuildSpdFdlPrefix() + " p", "SPD - Partition List", false, true);

            if (ok)
            {
                Log("[OK] Read GPT Completed Successfully!", Color.LightGreen);
            }
        }

        private async void BtnSpdReadPart_Click(object sender, EventArgs e)
        {
            // PAC ရှိရင် Auto ဖြည်မည်၊ မရှိရင် Built-in FDL သုံးမည်
            if (!string.IsNullOrEmpty(spdFirmwareFile) && File.Exists(spdFirmwareFile))
                await EnsurePacExtractedAsync();

            string spd = FindSpdDump();

            // Partition ဇယားထဲက ရွေးထားရင် ၎င်းနာမည်ကို ယူမည်၊ မရွေးထားရင် ရိုက်ထည့်ခိုင်းမည်
            string part = "";
            if (dgvPartitions.SelectedRows.Count > 0 && dgvPartitions.SelectedRows[0].Cells["colName"].Value != null)
                part = dgvPartitions.SelectedRows[0].Cells["colName"].Value.ToString();
            else
                part = PromptInput("Read Partition", "ဖတ်ယူမည့် Partition နာမည် ရိုက်ပါ (ဥပမာ boot, persist, nvitem)");

            if (string.IsNullOrWhiteSpace(part)) return;
            if (!IsValidPartitionName(part))
            {
                Log("[x] Invalid partition name - allowed: a-z, 0-9, _ , -", Color.Red);
                return;
            }

            using (FolderBrowserDialog fbd = new FolderBrowserDialog { Description = "Partition dump သိမ်းမည့် folder ရွေးပါ" })
            {
                if (fbd.ShowDialog() != DialogResult.OK) return;

                Log($"[*] Reading Partition [{part.Trim()}] -> Saving to {fbd.SelectedPath}...", Color.Cyan);
                string args = BuildSpdFdlPrefix() + " path \"" + fbd.SelectedPath + "\" r " + part.Trim();

                bool ok = await ExecuteCommandCleanAsync(spd, args, "SPD - Read [" + part.Trim() + "]", false, true);
                if (ok) Log("[OK] " + part.Trim() + " Dump Completed Successfully!", Color.LightGreen);
            }
        }

        private async void BtnSpdWritePart_Click(object sender, EventArgs e)
        {
            if (!string.IsNullOrEmpty(spdFirmwareFile) && File.Exists(spdFirmwareFile))
                await EnsurePacExtractedAsync();

            string spd = FindSpdDump();

            string part = "";
            if (dgvPartitions.SelectedRows.Count > 0 && dgvPartitions.SelectedRows[0].Cells["colName"].Value != null)
                part = dgvPartitions.SelectedRows[0].Cells["colName"].Value.ToString();
            else
                part = PromptInput("Write Partition", "ရေးသွင်းမည့် Partition နာမည် ရိုက်ပါ (ဥပမာ boot, recovery, vbmeta)");

            if (string.IsNullOrWhiteSpace(part)) return;
            if (!IsValidPartitionName(part))
            {
                Log("[x] Invalid partition name - allowed: a-z, 0-9, _ , -", Color.Red);
                return;
            }

            using (OpenFileDialog ofd = new OpenFileDialog
            {
                Filter = "Image Files (*.img;*.bin)|*.img;*.bin|All Files (*.*)|*.*",
                Title = $"Select image for [{part.Trim()}]"
            })
            {
                if (ofd.ShowDialog() != DialogResult.OK) return;

                if (MessageBox.Show($"Write {Path.GetFileName(ofd.FileName)} into [{part.Trim()}]?",
                    "Confirm Write", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;

                Log($"[*] Writing {Path.GetFileName(ofd.FileName)} into [{part.Trim()}]...", Color.Cyan);
                string args = BuildSpdFdlPrefix() + " w " + part.Trim() + " \"" + ofd.FileName + "\" reset";

                await ExecuteCommandCleanAsync(spd, args, "SPD - Write [" + part.Trim() + "]", false, true);
            }
        }

        private async void BtnSpdErasePart_Click(object sender, EventArgs e)
        {
            if (!string.IsNullOrEmpty(spdFirmwareFile) && File.Exists(spdFirmwareFile))
                await EnsurePacExtractedAsync();

            string spd = FindSpdDump();

            string part = "";
            if (dgvPartitions.SelectedRows.Count > 0 && dgvPartitions.SelectedRows[0].Cells["colName"].Value != null)
                part = dgvPartitions.SelectedRows[0].Cells["colName"].Value.ToString();
            else
                part = PromptInput("Erase Partition", "ဖျက်မည့် Partition နာမည် ရိုက်ပါ (ဥပမာ userdata, cache, misc, persist)");

            if (string.IsNullOrWhiteSpace(part)) return;
            if (!IsValidPartitionName(part))
            {
                Log("[x] Invalid partition name - allowed: a-z, 0-9, _ , -", Color.Red);
                return;
            }

            if (MessageBox.Show($"Erase / Format [{part.Trim()}] partition?",
                "Confirm Erase", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            Log($"[*] Erasing Partition [{part.Trim()}]...", Color.OrangeRed);
            string args = BuildSpdFdlPrefix() + " e " + part.Trim();

            bool ok = await ExecuteCommandCleanAsync(spd, args, "SPD - Erase [" + part.Trim() + "]", false, true);
            if (ok) Log("[OK] Erased [" + part.Trim() + "] Successfully!", Color.LightGreen);
        }

        // ================= UNLOCK (Diag) — spd_dump diag channel ကနေ FDL erase =================
        // Industry "Diag mode" (EFT/ResearchDownload) နဲ့ တူညီတဲ့ flow: FDL1/FDL2 load → exec →
        // partition erase → reset။ spd_dump က SPRD diag driver (Channel9) ကိုသုံးပြီး port ကို
        // auto ရှာတယ် (serial select မလို)။ skip_confirm=1 default မို့ batch erase မှာ prompt မတက်ဘူး၊
        // မရှိတဲ့ partition ဆိုရင် "part not exist" ပြပြီး နောက် command ကို continue လုပ်တယ်။
        //   • FRP   = persist (Hovatek ResearchDownload erase-persist နည်း) + frp fallback
        //   • Userlock = userdata + cache format (data wipe — Yes/No dialog နဲ့ အရင်မေး)

        private async void BtnSpdDiagFrp_Click(object sender, EventArgs e)
        {
            if (!string.IsNullOrEmpty(spdFirmwareFile) && File.Exists(spdFirmwareFile))
                await EnsurePacExtractedAsync();

            string spd = FindSpdDump();

            if (MessageBox.Show(
                "FRP Reset (Diag mode)?\n\n" +
                "persist / frp partition ကို erase ပြီး phone ကို reboot လုပ်မည်။\n\n" +
                "Device: Power off → Vol- (သို့) Vol+ နှိပ်ထားပြီး USB ချိတ်ပါ။",
                "FRP Reset (Diag)", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("   SPD - FRP RESET (DIAG MODE)", Color.White);
            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("[*] Waiting for device... (Power off, hold Vol- or Vol+ and connect USB)", Color.Orange);
            Log("[*] Erasing persist / frp...", Color.OrangeRed);

            string args = BuildSpdFdlPrefix() + " e persist e frp reset";
            bool ok = await ExecuteCommandCleanAsync(spd, args, "SPD - FRP Reset (Diag)", false, true);
            if (ok)
            {
                Log("[OK] FRP Reset job finished!", Color.LightGreen);
                Log("[i] Phone reboot ပြီးရင် setup wizard မှာ FRP ပျက်/မပျက် စစ်ပါ (log ထဲ 'part not exist' ပါရင် အဲ့ partition မရှိတာ)", Color.Gray);
            }
        }

        private async void BtnSpdDiagUserlock_Click(object sender, EventArgs e)
        {
            if (!string.IsNullOrEmpty(spdFirmwareFile) && File.Exists(spdFirmwareFile))
                await EnsurePacExtractedAsync();

            string spd = FindSpdDump();

            if (MessageBox.Show(
                "Userlock + FRP Reset (Diag mode)?\n\n" +
                "⚠ userdata (data) + cache + persist/frp ကို erase မည် —\n" +
                "ဖုန်းထဲက Data နဲ့ Lock အကုန် ပျက်သွားမယ်!\n\n" +
                "Device: Power off → Vol- (သို့) Vol+ နှိပ်ထားပြီး USB ချိတ်ပါ။",
                "Userlock + FRP Reset (Diag)", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("   SPD - USERLOCK + FRP RESET (DIAG MODE)", Color.White);
            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("[*] Waiting for device... (Power off, hold Vol- or Vol+ and connect USB)", Color.Orange);
            Log("[*] Erasing userdata / cache / persist / frp...", Color.OrangeRed);

            string args = BuildSpdFdlPrefix() + " e userdata e cache e persist e frp reset";
            bool ok = await ExecuteCommandCleanAsync(spd, args, "SPD - Userlock + FRP Reset (Diag)", false, true);
            if (ok)
            {
                Log("[OK] Userlock + FRP Reset job finished!", Color.LightGreen);
                Log("[i] First boot 1-3 မိနစ်ကြာနိုင်တယ် — setup wizard မှာ lock/migration ပျက်သွားတာ စစ်ပါ", Color.Gray);
            }
        }

        // ================= MTK OP + AUTO REBOOT =================
        // စမ်းသပ်တွေ့ချက် (MT6833 / k6833, hwcode 0x989): ဒီ device အတွက် mtkclient က
        // damode=XFLASH သုံးတယ် — DAXFlash.shutdown() ရဲ့ packet မှာ bootmode
        // (0=power off, 1=home screen, 2=fastboot) နဲ့ leaveusb flag ပါတယ်။ mtkclient က
        // leaveusb=0 ထားတာမို့ DA က USB ကို မဖြုတ်ဘူး → ဖုန်းက PC ရဲ့ USB ပေါ်မှာတင်
        // ဆက်ရှိနေပြီး Android မတက်ဘူး (power နှိပ်မှ တက်)။ mtkclient ကိုယ်တိုင်လည်း
        // "Reset command was sent. Disconnect usb cable to power off." လို့ ပြတယ်။
        // ဒါကြောင့် pmk_mtk_op.py က bootmode=2 (fastboot) + leaveusb=1 ပို့ပြီး၊ ဖုန်း
        // fastboot ရောက်လာရင် PC ကနေ `fastboot reboot` ဆက်ပို့တယ် — ပြီးရင် တကယ်
        // တက်/မတက် device ကို စစ်ပြီး marker နဲ့ ပြန်ပေးတယ် (အောက်က switch မှာ ပြတယ်)။
        private async Task<bool> RunMtkOpThenRebootAsync(string opArgs, string title)
        {
            // op + reset ကို BROM session တစ်ခုတည်းမှာ ပို့တယ်။ session အသစ် ထပ်မဖွင့်ရ —
            // ဖုန်း မရှိတော့ရင် mtkclient က BROM ကို အဆုံးမဲ့ စောင့်နေမယ် ✗ (အရင် ဒီလမ်းက
            // "Initializing Task..." မှာ ရပ်ခဲ့တယ်)။
            lastMtkBootState = "";
            bool autoReboot = chkMtkAutoReboot != null && chkMtkAutoReboot.Checked;
            string cli = autoReboot ? opArgs + ";reset" : opArgs;
            bool ok = await ExecuteCommandCleanAsync("python",
                BuildMtkOpArgs("multi \"" + cli + "\""),
                autoReboot ? title + " + reset (Android reboot)" : title);

            if (!ok) return false;

            if (!autoReboot)
            {
                Log("[i] Auto reboot is off - reboot the phone manually.", Color.Orange);
                return true;
            }

            // Post-op မှာ စာသား အနည်းဆုံးပဲ ပြတယ် (log မရှုပ်အောင်) — ဖုန်း ရောက်တဲ့ အခြေအနေကို
            // marker handler (ParseCleanLogAndProgress) က တစ်လိုင်းတည်း ပြပြီးသား။
            // ဒီမှာ ပြဿနာ (DA reject / စစ်လို့မရ) ဖြစ်ရင်သာ ထပ်တစ်လိုင်း ထည့်တယ်။
            switch (lastMtkBootState)
            {
                case "DA_FAILED":
                    Log("[i] The DA rejected the reboot - unplug/replug USB and retry.", Color.Orange);
                    break;
                case "":
                    Log("[i] Could not verify the reboot state - check the phone screen manually.", Color.Orange);
                    break;
            }

            return true;
        }

        // ================= DEVICE MODEL (UNLOCKTOOL ပုံစံ) =================
        // mtkclient က ro.product.* props ကို တိုက်ရိုက် မပေးဘူး — MTK ရဲ့ proinfo/persist
        // partition ထဲမှာ ရှိတဲ့ product info strings ကို ဆွဲထုတ်ပြီး ပြတယ်။
        private async void BtnMtkDeviceModel_Click(object sender, EventArgs e)
        {
            await RunFlashWorkflowAsync(async () =>
            {
            string tmpDir = Path.Combine(Path.GetTempPath(), "pmk_mtk_info");
            try { Directory.CreateDirectory(tmpDir); }
            catch (Exception ex) { Log("[!] MTK info dir create failed: " + ex.Message, Color.Red); }

            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("   MTK DEVICE MODEL (proinfo / persist)", Color.White);
            Log("========================================================", Color.FromArgb(0, 180, 255));

            foreach (string part in new string[] { "proinfo", "persist" })
            {
                string file = Path.Combine(tmpDir, part + ".bin");
                bool got = await ExecuteCommandCleanAsync("mtk.exe",
                    GetMtkTransportParam() + " r " + part + " \"" + file + "\"", "Read " + part);
                if (!got || !File.Exists(file)) continue;

                List<string> found = ExtractProductInfo(file);
                if (found.Count == 0)
                {
                    Log("[i] " + part + ": product info string not found", Color.Gray);
                    continue;
                }

                Log("[OK] Reading device info... OK [" + part + "]", Color.LightGreen);
                foreach (string line in found) Log("   " + line, Color.White);
                return;
            }

            Log("[i] Could not read the model from partitions - once the phone is in ADB mode,", Color.Orange);
            Log("    use ADB tab -> Read Full Info (or the Detect button in the platform bar).", Color.Orange);
            });
        }

        // partition ဖိုင်ထဲက printable ASCII string တွေကို ဆွဲထုတ်ပြီး product info ဆိုင်ရာ filter
        private List<string> ExtractProductInfo(string file)
        {
            var result = new List<string>();
            string[] keys = { "model", "product", "brand", "manufacturer", "device", "marketname",
                              "android", "build", "miui", "region", "project", "serial" };
            try
            {
                byte[] data = File.ReadAllBytes(file);
                var sb = new StringBuilder();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < data.Length; i++)
                {
                    byte b = data[i];
                    if (b >= 0x20 && b < 0x7F) sb.Append((char)b);
                    else
                    {
                        if (sb.Length >= 5)
                        {
                            string t = sb.ToString();
                            string low = t.ToLowerInvariant();
                            foreach (string k in keys)
                            {
                                if (low.Contains(k) && t.Length <= 90 && !low.StartsWith("http") && seen.Add(t))
                                {
                                    result.Add(t.Trim());
                                    break;
                                }
                            }
                        }
                        sb.Clear();
                        if (result.Count >= 25) break;
                    }
                }
            }
            catch (Exception ex) { Log("[!] Parse error: " + ex.Message, Color.Red); }
            return result;
        }

        // ================= SCATTER FIRMWARE FLASH (SP Flash ပုံစံ) =================
        private void BtnMtkPickScatter_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog ofd = new OpenFileDialog
            {
                Filter = "MTK Scatter (*_Android_scatter.txt)|*_Android_scatter.txt|Scatter Text (*.txt)|*.txt|All Files (*.*)|*.*",
                Title = "MTK scatter ဖိုင်ကို ရွေးပါ (firmware folder ထဲက)"
            })
            {
                if (ofd.ShowDialog() != DialogResult.OK) return;
                LoadScatterFile(ofd.FileName);
            }
        }

        // scatter ဖိုင်ကို ဖတ်ပြီး partition list ဖြည့် + firmware folder ကို မှတ်
        private void LoadScatterFile(string scatterPath)
        {
            try
            {
                ClearPartitionData();
                mtkFirmwareFolder = Path.GetDirectoryName(scatterPath) ?? "";
                SaveSettings();

                string curName = null, curAddr = null, curSize = null;
                bool curDownload = true;
                int count = 0, invisible = 0;

                Action flush = () =>
                {
                    if (string.IsNullOrEmpty(curName)) return;
                    AddPartitionRow(curName,
                                    string.IsNullOrEmpty(curAddr) ? "-" : curAddr,
                                    string.IsNullOrEmpty(curSize) ? "0x0" : curSize);
                    count++;
                    if (!curDownload) invisible++;
                };

                foreach (string raw in File.ReadAllLines(scatterPath))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("- partition_index"))
                    {
                        flush();
                        curName = null; curAddr = null; curSize = null; curDownload = true;
                    }
                    else if (line.StartsWith("partition_name:"))
                        curName = line.Substring("partition_name:".Length).Trim();
                    else if (line.StartsWith("linear_start_addr:"))
                        curAddr = line.Substring("linear_start_addr:".Length).Trim();
                    else if (line.StartsWith("partition_size:"))
                        curSize = line.Substring("partition_size:".Length).Trim();
                    else if (line.StartsWith("is_download:"))
                        curDownload = line.Substring("is_download:".Length).Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
                }
                flush();

                txtScatterFolder.Text = mtkFirmwareFolder;

                Log("========================================================", Color.FromArgb(0, 180, 255));
                Log("   MTK FIRMWARE (SCATTER) LOADED", Color.White);
                Log("========================================================", Color.FromArgb(0, 180, 255));
                Log("  • Scatter   : " + Path.GetFileName(scatterPath), Color.Cyan);
                Log("  • Folder    : " + mtkFirmwareFolder, Color.Cyan);
                Log("  • Partitions: " + count + " rows" + (invisible > 0 ? " (incl. " + invisible + " with is_download=false)" : ""), Color.White);
                Log("[*] Pressing START FLASH will write the .img files in the folder to their partitions.", Color.Orange);
            }
            catch (Exception ex)
            {
                Log("[x] Could not read the scatter file: " + ex.Message, Color.Red);
            }
        }

        private async void BtnMtkFlashScatter_Click(object sender, EventArgs e)
        {
            await RunFlashWorkflowAsync(BtnMtkFlashScatterAsync);
        }

        private async Task BtnMtkFlashScatterAsync()
        {
            string firmwareFolder = mtkFirmwareFolder;
            string transport = GetMtkTransportParam();
            bool backupFirst = chkMtkBackupNvFirst.Checked, skipUserdata = chkMtkSkipUserdata.Checked;
            bool resetAfter = chkMtkResetFrpAfter.Checked;

            if (string.IsNullOrEmpty(firmwareFolder) || !Directory.Exists(firmwareFolder))
            {
                MessageBox.Show("Firmware folder မရွေးရသေးပါ — 📂 Scatter ခလုတ်နဲ့ scatter ဖိုင်ကို အရင် ရွေးပါ။",
                    "Firmware", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            int imgCount = 0;
            try
            {
                imgCount = Directory.GetFiles(firmwareFolder, "*.img").Length
                         + Directory.GetFiles(firmwareFolder, "*.bin").Length;
            }
            catch { }

            if (imgCount == 0)
            {
                MessageBox.Show("Folder ထဲမှာ .img / .bin ဖိုင် မတွေ့ပါ။\n\n" + firmwareFolder,
                    "Firmware", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (MessageBox.Show(
                    "MTK Firmware Flash\n\n" +
                    "• Folder : " + Path.GetFileName(firmwareFolder) + "\n" +
                    "• ဖိုင်   : " + imgCount + " ခု (.img/.bin)\n\n" +
                    "• ဖိုင်နာမည်နဲ့ ကိုက်တဲ့ partition တွေဆီ ရေးပါမယ်\n" +
                    "• userdata ဖျက်ခံရနိုင်တယ် — ဖုန်းပိုင်ရှင် ခွင့်ပြုချက် ရှိမှ လုပ်ပါ\n\n" +
                    "ဆက်လုပ်မလား?",
                    "Confirm Firmware Flash", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            if (!await CheckMtkFirmwareAsync(firmwareFolder, transport, skipUserdata)) return;

            // [Option] flash မလုပ်ခင် NVRAM/NVDATA (EFS) backup
            if (backupFirst)
            {
                string nvDir = Path.Combine(firmwareFolder, "nv_backup_" + Guid.NewGuid().ToString("N"));
                if (!await BackupNvAsync(nvDir, transport)) return;
            }

            string wlArgs = transport + " wl \"" + firmwareFolder + "\"";
            if (skipUserdata) wlArgs += " --skip userdata";

            bool ok = await ExecuteCommandCleanAsync("mtk.exe", wlArgs,
                "MTK Firmware Flash (" + imgCount + " images" + (skipUserdata ? ", skip userdata" : "") + ")");

            if (!ok) return;

            Log("[OK] Firmware flash completed.", Color.LightGreen);

            // [Option] flash ပြီးရင် FRP reset
            if (resetAfter)
            {
                Log("[*] [Option] Erasing FRP partition (frp + config) after flash...", Color.Orange);
                if (!await ExecuteCommandCleanAsync("mtk.exe", transport + " e frp,config", "Reset FRP (post-flash)")) return;
            }

            // Auto reboot — RunFlashWorkflowAsync က workflow ပြီးမှ တစ်ခါတည်း ပြန်ပို့တယ်။
        }

        private async void BtnMtkNvBackup_Click(object sender, EventArgs e)
        {
            await RunFlashWorkflowAsync(async () =>
            {
                using var dialog = new FolderBrowserDialog { Description = "Select Folder to Save NV Backup" };
                if (dialog.ShowDialog() != DialogResult.OK) return;
                string directory = Path.Combine(dialog.SelectedPath, "nv_backup_" + Guid.NewGuid().ToString("N"));
                await BackupNvAsync(directory, GetMtkTransportParam());
            });
        }

        // NV Erase — nvram/nvdata format (IMEI ပျက်နိုင်) — Unlock Tool ပုံစံ
        private async void BtnMtkNvErase_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show(
                    "NV Erase (MediaTek) — nvram နဲ့ nvdata partition ကို format လုပ်ပါမယ်။\n\n" +
                    "• IMEI / baseband ပျက်သွားနိုင်တယ် (backup မရှိဘဲ erase မလုပ်ပါနဲ့)\n" +
                    "• NV Backup အရင်ယူပြီးမှ လုပ်ပါ\n" +
                    "• ဖုန်းပိုင်ရှင် ခွင့်ပြုချက် ရှိမှ လုပ်ပါ\n\n" +
                    "ဆက်လုပ်မလား?",
                    "Confirm NV Erase", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            await RunFlashWorkflowAsync(async () =>
            {
                string transport = GetMtkTransportParam();
                // mtkclient `e` က comma-sep multi-erase support — တစ်ခါတည်း format
                bool ok = await ExecuteCommandCleanAsync("mtk.exe", transport + " e nvram,nvdata",
                    "NV Erase (nvram + nvdata)", quiet: true);
                if (ok)
                    Log("[OK] Erased: nvram, nvdata", Color.LightGreen);
            });
        }

        // NV Restore — NV Backup folder ထဲက known NV partition .bin files အားလုံး ပြန်ရေး
        private static readonly string[] NvRestoreNames =
        {
            "nvram", "nvdata", "nvcfg", "proinfo", "persist", "seccfg",
            "protect1", "protect2", "nvram_bak", "nvdata_bak", "preloader"
        };

        private async void BtnMtkNvRestore_Click(object sender, EventArgs e)
        {
            using var dialog = new FolderBrowserDialog { Description = "Select NV Backup folder (known NV .bin files)" };
            if (dialog.ShowDialog() != DialogResult.OK) return;

            string dir = dialog.SelectedPath;
            // nv_backup_* subfolder ထဲမှာ သိမ်းထားရင် auto-detect
            var sub = Directory.GetDirectories(dir, "nv_backup_*");
            if (sub.Length == 1)
                dir = sub[0];

            // known set ထဲက name တိုင်းအတွက် folder ထဲ .bin/.img ရှိမရှိ ရှာ
            var found = new List<(string Name, string Path)>();
            foreach (string name in NvRestoreNames)
            {
                string p = Path.Combine(dir, name + ".bin");
                if (!File.Exists(p)) p = Path.Combine(dir, name + ".img");
                if (File.Exists(p)) found.Add((name, p));
            }

            bool hasCoreNv = found.Any(f => f.Name.Equals("nvram", StringComparison.OrdinalIgnoreCase))
                          || found.Any(f => f.Name.Equals("nvdata", StringComparison.OrdinalIgnoreCase));
            if (found.Count == 0 || !hasCoreNv)
            {
                MessageBox.Show(
                    "NV Restore — nvram/nvdata backup file မတွေ့ပါ။\n\n" +
                    "NV Backup ဖြင့် ယူထားသော folder ကိုသာ ရွေးပါ။\n" +
                    "မတွေ့သေးရင် NV Backup ကို အရင်နှိပ်ပါ။",
                    "NV Restore", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string fileList = string.Join("\n", found.Select(f => "  • " + f.Name));
            bool hasPreloader = found.Any(f => f.Name.Equals("preloader", StringComparison.OrdinalIgnoreCase));
            if (MessageBox.Show(
                    "NV Restore — folder ထဲက backup file " + found.Count + " ခု ပြန်ရေးမယ်:\n" +
                    fileList + "\n\n" +
                    "• Device ပေါ်က လက်ရှိ NV data ပျက်သွားမယ်\n" +
                    "• ဖုန်းပိုင်ရှင် ခွင့်ပြုချက် ရှိမှ လုပ်ပါ" +
                    (hasPreloader ? "\n• ⚠ preloader.bin ပါရင် brick risk ရှိနိုင် — မသေချာရင် skip ပါ" : "") +
                    "\n\nဆက်လုပ်မလား?",
                    "Confirm NV Restore", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            // preloader ပါရင် သီးသန့် နောက်ထပ် confirm
            if (hasPreloader)
            {
                if (MessageBox.Show(
                        "preloader.bin ပါ ရေးမှာပါ — wrong preloader ဆို phone boot မဝင်နိုင်တော့ဘူး။\n\n" +
                        "ဒီ backup က ဒီ device အတွက်ပဲလား သေချာပြီးမှ Yes နှိပ်ပါ။\n\n" +
                        "preloader ကို မရေးချင်ရင် No နှိပ်ပါ (ကျန် partition တွေ ရေးမယ်)။",
                        "Preloader Write Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    found.RemoveAll(f => f.Name.Equals("preloader", StringComparison.OrdinalIgnoreCase));
            }

            await RunFlashWorkflowAsync(async () =>
            {
                string transport = GetMtkTransportParam();
                if (!await ExecuteCommandCleanAsync("mtk.exe", transport + " printgpt", "Verify restore device GPT", clearPartitions: true)) return;
                var layout = CurrentLayout();
                var manifest = await Task.Run(() => ShopServices.VerifyBackup(dir, layout));
                foreach (var entry in found)
                    if (!manifest.Files.Any(f => f.Partition.Equals(entry.Name, StringComparison.OrdinalIgnoreCase) &&
                        f.File.Equals(Path.GetFileName(entry.Path), StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("Restore file not covered by manifest: " + entry.Name);
                if (MessageBox.Show("Backup hashes and partition sizes match. Physical device identity is unverified.\n\nConfirm this backup belongs to the connected phone.",
                    "Restore device identity", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                { workflowFailed = true; return; }

                // path ထဲ comma ပါရင် sequential ပို safe — sequential call သုံးတယ်
                int okCount = 0;
                foreach (var (name, path) in found)
                {
                    if (stopRequested) break;
                    bool ok = await ExecuteCommandCleanAsync("mtk.exe",
                        transport + " w " + name + " \"" + path + "\"", "NV Restore (" + name + ")", quiet: true);
                    if (ok)
                    {
                        okCount++;
                        long len = 0;
                        try { len = new FileInfo(path).Length; } catch { }
                        Log("[OK] " + name + " — " + FormatMb(len), Color.LightGreen);
                    }
                }
                if (okCount == found.Count)
                    Log("[OK] NV Restore complete: " + okCount + "/" + found.Count + " file(s).", Color.LightGreen);
                else
                    Log("[!] NV Restore incomplete: " + okCount + "/" + found.Count + " — check log above.", Color.Orange);
            });
        }

        // Safe Bundle — device-unique + boot-critical partitions ကို တစ်ချက်တည်း backup
        private async void BtnMtkSafeBackup_Click(object sender, EventArgs e)
        {
            await RunFlashWorkflowAsync(async () =>
            {
                using var dialog = new FolderBrowserDialog { Description = "Select Folder to Save Safe Backup Bundle" };
                if (dialog.ShowDialog() != DialogResult.OK) return;
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string directory = Path.Combine(dialog.SelectedPath, "safe_backup_" + stamp);
                bool ok = await BackupSafeBundleAsync(directory, GetMtkTransportParam());
                if (ok)
                {
                    Log("[OK] Safe Bundle saved: " + directory, Color.LightGreen);
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = directory,
                            UseShellExecute = true
                        });
                    }
                    catch { /* explorer open fail — non-fatal */ }
                }
            });
        }

        private async Task<bool> BackupSafeBundleAsync(string directory, string transport)
        {
            bool ok = await BackupSafeBundleAsyncCore(directory, transport);
            if (!ok && flashWorkflowContext.Value) workflowFailed = true;
            return ok;
        }

        private async Task<bool> BackupSafeBundleAsyncCore(string directory, string transport)
        {
            // GPT ကန် size အတည်ပြု — မရှိတဲ့ partition ကို skip, ရှိတာမှ verify
            if (!await ExecuteCommandCleanAsync("mtk.exe", transport + " printgpt", "Read GPT for Safe Bundle", clearPartitions: true))
                return false;

            string[] names =
            {
                "nvram", "nvdata", "nvcfg", "proinfo", "persist", "seccfg",
                "boot", "vbmeta", "vbmeta_system", "vbmeta_vendor"
            };

            Directory.CreateDirectory(directory);
            var present = new List<string>();
            var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in names)
            {
                var matches = loadedPartitions.Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count == 1)
                {
                    present.Add(name);
                    sizes[name] = ReviewSafety.ParseSize(matches[0].LengthHex);
                }
                else
                {
                    Log("[i] Safe Bundle: partition not in GPT, skipped: " + name, Color.Gray);
                }
            }

            if (present.Count == 0)
            {
                Log("[!] Safe Bundle: none of the critical partitions were found in GPT.", Color.OrangeRed);
                return false;
            }

            Log("[🛡] Safe Bundle: dumping " + present.Count + " critical partition(s)...", Color.Cyan);
            int done = await ReviewSafety.RunSequenceAsync(present.Count, () => stopRequested, async i =>
            {
                string name = present[i];
                string path = Path.Combine(directory, name + ".bin");
                bool ok = await ExecuteCommandCleanAsync("mtk.exe", transport + " r " + name + " \"" + path + "\"", "Safe Bundle " + name);
                if (!ok) return false;
                if (ReviewSafety.HasBackup(path, sizes[name])) return true;
                Log("[FAIL] Safe Bundle size mismatch: " + name + "; expected " + sizes[name] + " bytes.", Color.Red);
                return false;
            });

            bool complete = done == present.Count && !stopRequested;
            if (complete) await SaveBackupManifestAsync(directory, present);
            Log(complete
                ? "[OK] Safe Bundle complete (" + present.Count + " partitions, size verified)."
                : "[!] Safe Bundle incomplete; operation stopped.",
                complete ? Color.LightGreen : Color.OrangeRed);

            // vbmeta တွေကို Undo Vbmeta Fix အတွက်လည်း သီးသန့် ကူးထား
            if (complete)
            {
                try
                {
                    string vbDir = ShopServices.DataPath("vbmeta_backup");
                    Directory.CreateDirectory(vbDir);
                    foreach (string vb in new[] { "vbmeta", "vbmeta_system", "vbmeta_vendor" })
                    {
                        string src = Path.Combine(directory, vb + ".bin");
                        if (File.Exists(src))
                            File.Copy(src, Path.Combine(vbDir, vb + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".bin"), true);
                    }
                    Log("[i] vbmeta copies also saved under vbmeta_backup\\ for Undo Fix.", Color.Gray);
                }
                catch (Exception ex) { Log("[!] vbmeta side-copy failed: " + ex.Message, Color.Orange); }
            }

            return complete;
        }

        // Unlock Tool style — user တောင်းဆိုတဲ့ ဖိုင် ၄ ခုပဲ
        private static readonly string[] NvBackupNames =
        {
            "nvram", "nvdata", "nvcfg", "proinfo"
        };

        private static string FormatMb(long bytes)
        {
            double mb = bytes / (1024.0 * 1024.0);
            if (mb >= 1024.0) return (mb / 1024.0).ToString("F2") + " GB";
            if (mb >= 1.0) return mb.ToString("F2") + " MB";
            return (bytes / 1024.0).ToString("F1") + " KB";
        }

        private async Task<bool> BackupNvAsync(string directory, string transport)
        {
            bool ok = await BackupNvAsyncCore(directory, transport);
            if (!ok && flashWorkflowContext.Value) workflowFailed = true;
            return ok;
        }

        private async Task<bool> BackupNvAsyncCore(string directory, string transport)
        {
            // Read the attached device's GPT; do not trust a previous device or firmware's sizes.
            if (!await ExecuteCommandCleanAsync("mtk.exe", transport + " printgpt", "Read NV partition sizes",
                    clearPartitions: true, quiet: true)) return false;

            // Safe Bundle လို pattern — GPT ထဲ မရှိတဲ့ partition ကို skip (throw မလုပ်)
            var present = new List<string>();
            var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in NvBackupNames)
            {
                var matches = loadedPartitions.Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count == 1)
                {
                    present.Add(name);
                    sizes[name] = ReviewSafety.ParseSize(matches[0].LengthHex);
                }
                else
                {
                    Log("[i] NV Backup: partition not in GPT, skipped: " + name, Color.Gray);
                }
            }

            if (present.Count == 0)
            {
                Log("[!] NV Backup: none of the NV partitions were found in GPT.", Color.OrangeRed);
                return false;
            }

            Directory.CreateDirectory(directory);
            Log("[💾] NV Backup — " + present.Count + " file(s): " + string.Join(", ", present), Color.Cyan);
            int done = await ReviewSafety.RunSequenceAsync(present.Count, () => stopRequested, async i =>
            {
                string name = present[i];
                string path = Path.Combine(directory, name + ".bin");
                bool ok = await ExecuteCommandCleanAsync("mtk.exe",
                    transport + " r " + name + " \"" + path + "\"", "Backup " + name, quiet: true);
                if (!ok) return false;
                if (!ReviewSafety.HasBackup(path, sizes[name]))
                {
                    Log("[FAIL] " + name + " size mismatch: expected " + FormatMb(sizes[name]) +
                        ", got " + FormatMb(File.Exists(path) ? new FileInfo(path).Length : 0) + ".", Color.Red);
                    return false;
                }
                Log("[OK] " + name + ".bin — " + FormatMb(sizes[name]), Color.LightGreen);
                return true;
            });
            bool complete = done == present.Count && !stopRequested;
            if (complete) await SaveBackupManifestAsync(directory, present);

            Log(complete
                ? "[OK] NV backup complete: " + directory
                : "[!] NV backup incomplete; operation stopped.",
                complete ? Color.LightGreen : Color.OrangeRed);
            return complete;
        }

        // MTK ops အတွက် transport ရွေးတယ် (UnlockTool လမ်းအတိုင်း — USB ရော VCOM/serial ရော):
        // MediaTek device ရဲ့ COM port ရှိရင် serial/VCOM ကို ဦးစားပေးတယ်၊ မရှိရင် USB/libusb
        // အတိုင်း (flag မပို့)။ အတင်းရွေးချင်ရင် env: PMK_MTK_SERIAL=off | COM5။
        private string GetMtkTransportParam()
        {
            string auto = DetectMtkComPort();
            string s = string.IsNullOrEmpty(auto) ? "" : " --serialport " + auto;
            // MTK picker ရွေးထားတဲ့ DA / Auth / Preloader — mtkclient global options (subcommand မတိုင်ခင် ထည့်ရ)
            if (File.Exists(mtkDaPath)) s += " --loader \"" + mtkDaPath + "\"";
            if (File.Exists(mtkAuthPath)) s += " --auth \"" + mtkAuthPath + "\"";
            if (File.Exists(mtkPreloaderPath)) s += " --preloader \"" + mtkPreloaderPath + "\"";
            return s;
        }

        // MediaTek device (VID_0E8D) ရဲ့ COM port — background refresh လုပ်ထားတဲ့ cache ကနေ
        // ချက်ချင်း ပြန်ပေးတယ် (PowerShell ကို UI thread မှာ မခေါ်ဘူး)။
        private string DetectMtkComPort()
        {
            return cachedMtkComPort;
        }

        // mtkclient ရဲ့ CLI `reset` က `shutdown(bootmode=0)` = power off သာ လုပ်တယ် (reboot မဟုတ်)။
        // pmk_mtk_op.py helper က bootmode အစားထိုးပြီး (default fastboot)၊ အောက်ဆုံးမှာ ဖုန်း
        // တကယ် တက်/မတက် ကိုယ်တိုင် စစ်တယ်။ Transport (USB / serial-VCOM) ကို helper ကိုယ်တိုင်
        // auto-detect လုပ်တာမို့ ဒီမှာ --serialport ကို မပို့တော့ဘူး (env နဲ့ override လုပ်လို့ရ)။
        private string BuildMtkOpArgs(string cliArgs)
        {
            string script = Path.Combine(Application.StartupPath, "pmk_mtk_op.py");
            return "\"" + script + "\" " + cliArgs;
        }

        private void TabControl_DrawItem(object sender, DrawItemEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            TabPage page = tabControl.TabPages[e.Index];
            Rectangle bounds = tabControl.GetTabRect(e.Index);
            bool isSelected = (tabControl.SelectedIndex == e.Index);

            Color backColor = isSelected ? Color.FromArgb(86, 145, 250) : Color.FromArgb(32, 35, 42);
            Color textColor = isSelected ? Color.White : Color.Gainsboro;

            using (SolidBrush brush = new SolidBrush(backColor))
            {
                g.FillRectangle(brush, bounds);
            }

            if (isSelected)
            {
                using (Pen pen = new Pen(Color.FromArgb(86, 145, 250), 3))
                {
                    g.DrawLine(pen, bounds.Left, bounds.Bottom - 2, bounds.Right, bounds.Bottom - 2);
                }
            }

            TextRenderer.DrawText(g, page.Text, new Font("Segoe UI", 9.5f, FontStyle.Bold), bounds, textColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }


        // ================= TAB UI GRID =================
        // Tab တိုင်းရဲ့ ခလုတ်တွေ တစ်ပုံစံတည်း နေရာကျအောင် — button 160x36၊
        // column step 165 (x = 15, 180, 345, 510, 675, 840)၊ row step 43 (y = 15, 58, 101, 144, 187)
        private const int UiBtnW = 160;
        private const int UiBtnH = 36;
        private const int UiColX0 = 15;
        private const int UiColStep = 165;
        private const int UiRowY0 = 15;
        private const int UiRowStep = 43;
        private static int UiX(int col) { return UiColX0 + col * UiColStep; }
        private static int UiY(int row) { return UiRowY0 + row * UiRowStep; }

        public enum ButtonTheme { Cyan, Green, Orange, Red, Purple }

        // ================= GROUP PANEL (unlock tool ပုံစံ အုပ်စုဘောင်) =================
        // ခလုတ်တွေကို ခေါင်းစဉ်ပါတဲ့ ဘောင်တစ်ခုစီ ခွဲထည့်တယ် — auto grid (160x36၊ col step 165၊ row step 43)
        private Panel CreateGroupPanel(string title, int x, int y, int cols, params Control[] children)
        {
            int rows = (children.Length + cols - 1) / cols;
            int w = cols * 165 + 15;
            int h = 72 + (rows - 1) * 43;

            Panel p = new Panel
            {
                Location = new Point(x, y),
                Size = new Size(w, h),
                BackColor = Color.FromArgb(23, 26, 32),
                Tag = "grp"
            };
            EnsureThemePaint(p);   // light: 3D gradient + etched; dark: flat border

            p.Controls.Add(new Label
            {
                Text = title,
                Location = new Point(9, 4),
                AutoSize = true,
                ForeColor = Color.FromArgb(86, 145, 250),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                Tag = "accent"
            });

            for (int i = 0; i < children.Length; i++)
            {
                Control c = children[i];
                p.Controls.Add(c);
                c.Location = new Point(10 + (i % cols) * 165, 26 + (i / cols) * 43);
            }
            return p;
        }

        // ================= AUTO-UPDATE (GitHub Releases) =================
        // auto-check = login gate (LoginForm.EnforceUpdateGateAsync) မှာပဲ — Form1 မှာ manual check ပဲကျန်
        private Button Create3DButton(string text, int x, int y, int width, int height, ButtonTheme theme)
        {
            Color[] gradStops;
            Color borderColor;

            switch (theme)
            {
                case ButtonTheme.Cyan:
                    gradStops = new[] { Color.FromArgb(86, 145, 250), Color.FromArgb(112, 116, 246), Color.FromArgb(124, 74, 230) };
                    borderColor = Color.FromArgb(150, 170, 255);
                    break;
                case ButtonTheme.Green:
                    gradStops = new[] { Color.FromArgb(45, 175, 105), Color.FromArgb(20, 110, 60), Color.FromArgb(12, 70, 40) };
                    borderColor = Color.FromArgb(50, 205, 100);
                    break;
                case ButtonTheme.Orange:
                    gradStops = new[] { Color.FromArgb(255, 170, 60), Color.FromArgb(185, 95, 20), Color.FromArgb(130, 60, 10) };
                    borderColor = Color.FromArgb(255, 160, 50);
                    break;
                case ButtonTheme.Red:
                    gradStops = new[] { Color.FromArgb(230, 80, 85), Color.FromArgb(160, 35, 40), Color.FromArgb(110, 20, 25) };
                    borderColor = Color.FromArgb(240, 70, 75);
                    break;
                case ButtonTheme.Purple:
                    gradStops = new[] { Color.FromArgb(175, 105, 245), Color.FromArgb(105, 55, 165), Color.FromArgb(70, 35, 115) };
                    borderColor = Color.FromArgb(175, 100, 240);
                    break;
                default:
                    gradStops = new[] { Color.FromArgb(70, 78, 92), Color.FromArgb(45, 50, 60), Color.FromArgb(30, 33, 40) };
                    borderColor = Color.FromArgb(80, 88, 102);
                    break;
            }

            Button btn = new Button
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(width, height),
                BackColor = gradStops[1],
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.2f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };

            btn.FlatAppearance.BorderSize = 1;
            btn.FlatAppearance.BorderColor = borderColor;
            btn.FlatAppearance.MouseOverBackColor = gradStops[1];
            btn.FlatAppearance.MouseDownBackColor = gradStops[2];
            btn.Tag = "gbtn";   // ApplyTheme: gradient buttons keep white text always

            // Gradient paint: theme ပေါ်မူတည်ပြီး ၂/၃ ရောင် စပ်ထားတဲ့ vertical blend
            btn.Paint += (s, e) =>
            {
                var b = (Button)s;
                Rectangle r = b.ClientRectangle;
                if (r.Width < 2 || r.Height < 2) return;

                Color top = gradStops[0], mid = gradStops[1], bot = gradStops[2];
                if (b.Capture)
                {
                    top = ControlPaint.Light(top, 0.15f);
                    mid = ControlPaint.Light(mid, 0.1f);
                }

                ColorBlend blend = new ColorBlend(3)
                {
                    Colors = new[] { top, mid, bot },
                    Positions = new[] { 0f, 0.55f, 1f }
                };
                using (LinearGradientBrush brush = new LinearGradientBrush(r, top, bot, LinearGradientMode.Vertical))
                {
                    brush.InterpolationColors = blend;
                    e.Graphics.FillRectangle(brush, r);
                }

                using (Pen pen = new Pen(borderColor))
                    e.Graphics.DrawRectangle(pen, 0, 0, r.Width - 1, r.Height - 1);

                TextRenderer.DrawText(e.Graphics, b.Text, b.Font, r,
                    b.ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            };

            // MST-style log: op button နှိပ်တိုင်း ယခင် log ရှင်း — caller ရဲ့ Click handler မတိုင်ခင်
            // ရှင်းအောင် ဒီမှာ အရင် subscribe လုပ်ထားတယ် (handler အစဉ်လိုက် run ဖြစ်လို့)
            // log utility (Export/STOP/Clear/theme) + file/folder picker တွေက မရှင်း
            // (export က ရှိရင်ဖတ်ရ/ picker cancel လုပ်ရင် log မဆုံးရ)
            string capLog = text ?? "";
            btn.Click += (s, e) =>
            {
                if (suppressNextLogClear) { suppressNextLogClear = false; return; }
                string ct = capLog;
                if (ct.IndexOf("STOP", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    ct.IndexOf("Clear Log", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    ct.IndexOf("Export Log", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    ct.IndexOf("Dark", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    ct.IndexOf("Light", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    ct.IndexOf("Close", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    ct.IndexOf("Browse", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    ct.IndexOf("Folder", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    ct.IndexOf("Scatter", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    ct.IndexOf("PAC", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    ct.IndexOf("Open", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    ct.IndexOf("Device Model", StringComparison.OrdinalIgnoreCase) >= 0)
                    return;
                ClearLogForNewOp();
            };

            return btn;
        }

        // MST-style log: op button တစ်ခု နှိပ်တိုင်း ယခင် log ရှင်းပြီး ဒီ op ရဲ့ log ပဲ ပြ
        // (chip က target button ကို PerformClick ခေါ်ရင် ထပ်မရှင်းအောင် flag နဲ့ တား)
        private bool suppressNextLogClear;

        private void ClearLogForNewOp()
        {
            if (rtbLog == null || rtbLog.IsDisposed || !rtbLog.IsHandleCreated) return;
            if (rtbLog.InvokeRequired)
            {
                try { rtbLog.Invoke((Action)ClearLogForNewOp); } catch { }
                return;
            }
            rtbLog.Clear();
        }

        // null/handle-မရှိ control ကို tooltip ပေးရင် crash မဖြစ်အောင် (9/23 startup crash ×4)
        private void Tip(Control control, string text)
        {
            if (control == null || control.IsDisposed || toolTipMain == null) return;
            try { toolTipMain.SetToolTip(control, text); } catch { }
        }

        private void Log(string message, Color? color = null)
        {
            // form/log box ပိတ်ပြီးသား သို့ handle မတည်ရသေးရင် thread-pool
            // output က crash မဖြစ်အောင် ကာတယ် (9/30 ObjectDisposedException ×2)
            if (rtbLog == null || rtbLog.IsDisposed || !rtbLog.IsHandleCreated) return;
            try
            {
                if (rtbLog.InvokeRequired)
                {
                    rtbLog.Invoke(new Action(() => Log(message, color)));
                    return;
                }

                rtbLog.SelectionStart = rtbLog.TextLength;
                rtbLog.SelectionLength = 0;
                rtbLog.SelectionColor = color ?? Color.FromArgb(0, 255, 128);

                rtbLog.AppendText(message + "\r\n");
                rtbLog.SelectionColor = rtbLog.ForeColor;
                rtbLog.ScrollToCaret();
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private string FormatBytes(string hexLength)
        {
            try
            {
                long bytes = long.Parse(hexLength.Replace("0x", ""), NumberStyles.HexNumber);
                return FormatBytesLong(bytes);
            }
            catch
            {
                return hexLength;
            }
        }

        private string FormatBytesLong(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024)
                return ((double)bytes / (1024L * 1024 * 1024)).ToString("F2") + " GB";
            if (bytes >= 1024L * 1024)
                return ((double)bytes / (1024L * 1024)).ToString("F2") + " MB";
            if (bytes >= 1024L)
                return (bytes / 1024.0).ToString("F2") + " KB";
            return bytes.ToString() + " B";
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            if (m.Msg == WM_DEVICECHANGE)
            {
                int eventType = m.WParam.ToInt32();
                // DBT_DEVNODES_CHANGED (0x0007) ကို ပါ ဖမ်းတယ် — COM port/USB CDC device ချိတ်/ဖြုတ်ရင်
                // ဒီ event သာ လာတတ်တယ် (arrival/removal မလာဘဲ)။
                if (eventType == DBT_DEVICEARRIVAL || eventType == DBT_DEVICEREMOVECOMPLETE ||
                    eventType == DBT_DEVNODES_CHANGED)
                {
                    RefillPortCombo();
                    RefreshMtkDetection();
                    _ = HandleDeviceChangeAsync();
                }
            }
        }

        private async Task HandleDeviceChangeAsync()
        {
            await Task.Delay(500);
            await CheckAllDevicesAsync(true);
        }

        // MediaTek USB mode (BROM / PreLoader / DA) + driver service + COM port — Windows PnP API ကနေ ဖတ်တယ်။
        // ဖုန်းက BROM (0e8d:0003) မှာ ရှိ/မရှိ ကို user က ဒီနေရာကနေ စစ်နိုင်တယ် — မှားနေရင်
        // mtkclient ရဲ့ BROM handshake က libusb crash (0xC0000005) ဖြစ်တတ်တယ်။
        //
        // အရေးကြီး: PowerShell spawn က ~0.5-1s ကြာတာမို့ **UI thread ပေါ်မှာ ဘယ်တော့မှ မ run** ဘူး —
        // background thread မှာ ဖတ်ပြီး ရလဒ်ကို cache ထားတယ်။ ဒါမှ tool ဖွင့်ချင်း/click ချင်း မဟန်းမှာ။
        private volatile string cachedMtkModeText = "";
        private volatile string cachedMtkComPort = "";
        private int mtkDetectBusy = 0;

        private void RefreshMtkDetection()
        {
            if (Interlocked.CompareExchange(ref mtkDetectBusy, 1, 0) != 0) return;  // တစ်ချိန်တည်း တစ်ခုပဲ
            Task.Run(() =>
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = "powershell",
                        Arguments = "-NoProfile -NonInteractive -Command \"Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | " +
                                    "Where-Object { $_.InstanceId -like '*VID_0E8D*' } | ForEach-Object { " +
                                    "$s = (Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_Service' -ErrorAction SilentlyContinue).Data; " +
                                    "$_.InstanceId + '|' + $s + '|' + $_.FriendlyName }\"",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    string outText = "";
                    using (Process p = Process.Start(psi))
                    {
                        if (p == null) return;
                        // အရင် ReadToEnd (unbounded) က pipe ပိတ်သွားရင် အမြဲတမ်း block →
                        // mtkDetectBusy lock အမြဲကျန် → session အတွင်း MTK detect သေ + orphan powershell။
                        // read ကို async drain လုပ်ပြီး exit ကို 8s နဲ့ bound ထားတယ်။
                        Task<string> readTask = p.StandardOutput.ReadToEndAsync();
                        bool exited = p.WaitForExit(8000);
                        if (!exited)
                        {
                            try { p.Kill(entireProcessTree: true); } catch { }
                            try { readTask.Wait(1000); } catch { }
                            outText = "";
                        }
                        else
                        {
                            outText = readTask.Result;
                        }
                    }

                    // PID တစ်ခုချင်းစီအလိုက် driver service တွေကို စုတယ် (composite device ဆိုရင်
                    // MI_00, MI_01 ... interface တွေ အများကြီး ရှိတာမို့ တစ်လိုင်းတည်း ပြဖို့ စုတယ်)။
                    List<string> pidOrder = new List<string>();
                    Dictionary<string, List<string>> pidSvcs = new Dictionary<string, List<string>>();
                    string comPort = "";

                    foreach (string line in outText.Split('\n'))
                    {
                        string up = line.ToUpperInvariant();
                        if (!up.Contains("VID_0E8D")) continue;

                        Match mp = Regex.Match(up, @"PID_([0-9A-F]{4})");
                        if (!mp.Success) continue;
                        string pid = mp.Groups[1].Value.ToLowerInvariant();

                        string[] parts = line.Split('|');
                        string svc = parts.Length > 1 ? parts[1].Trim() : "";
                        string friendly = parts.Length > 2 ? parts[2].Trim() : "";

                        Match mc = Regex.Match(friendly, @"\((COM\d+)\)");
                        if (mc.Success) comPort = mc.Groups[1].Value;

                        if (!pidSvcs.ContainsKey(pid))
                        {
                            pidSvcs[pid] = new List<string>();
                            pidOrder.Add(pid);
                        }
                        if (!string.IsNullOrEmpty(svc) &&
                            !pidSvcs[pid].Exists(x => string.Equals(x, svc, StringComparison.OrdinalIgnoreCase)))
                            pidSvcs[pid].Add(svc);
                    }

                    List<string> modes = new List<string>();
                    foreach (string pid in pidOrder)
                    {
                        string label;
                        if (pid == "0003") label = "BROM (0e8d:0003)";
                        else if (pid == "2000") label = "PreLoader (0e8d:2000)";
                        else if (pid == "2001") label = "DA (0e8d:2001)";
                        else label = "MTK (0e8d:" + pid + ")";
                        if (pidSvcs[pid].Count > 0) label += " [" + string.Join("+", pidSvcs[pid]) + "]";
                        modes.Add(label);
                    }

                    cachedMtkModeText = string.Join(", ", modes);
                    cachedMtkComPort = comPort;
                }
                catch
                {
                    cachedMtkModeText = "";
                    cachedMtkComPort = "";
                }
                finally
                {
                    Interlocked.Exchange(ref mtkDetectBusy, 0);
                    // ရလဒ် ရပြီဆိုတာနဲ့ header/label ကို ချက်ချင်း update လုပ်ခိုင်းတယ်
                    // (၆ စက္ကန့် timer ကို စောင့်နေစရာ မလိုအောင်)။
                    try
                    {
                        if (IsHandleCreated && !IsDisposed)
                        {
                            BeginInvoke(new Action(() =>
                            {
                                if (!isDetecting) _ = CheckAllDevicesAsync(false);
                            }));
                        }
                    }
                    catch { }
                }
            });
        }

        // UI/log အတွက် — cache ထဲက တန်ဖိုးပဲ ပြန်ပေးတယ် (PowerShell မခေါ်ဘူး → ချက်ချင်း ပြန်)
        private string DetectMtkUsbMode()
        {
            return cachedMtkModeText;
        }

        // COM port list ကို အလိုအလျောက် refresh လုပ်တယ် — user ရွေးထားတဲ့ port ကို မဖျက်ဘဲ၊
        // list တကယ် ပြောင်းမှ rebuild လုပ်တယ် (Scan Port ခလုတ် မနှိပ်ရတော့ဘူး)။
        private System.Windows.Forms.Timer devicePollTimer;

        private void RefillPortCombo()
        {
            if (flashWorkflowRunning) return;
            try
            {
                string[] ports = SerialPort.GetPortNames();
                // COM10 က COM9 ရဲ့ နောက်မှာ ရောက်အောင် နံပါတ်အလိုက် စီတယ် (string sort မဟုတ်)
                Array.Sort(ports, (a, b) =>
                {
                    int na, nb;
                    int.TryParse(a.Replace("COM", ""), out na);
                    int.TryParse(b.Replace("COM", ""), out nb);
                    return na.CompareTo(nb);
                });

                List<string> want = new List<string>(ports);
                List<string> have = new List<string>();
                foreach (object it in cmbPorts.Items) have.Add(it == null ? "" : it.ToString());

                // Port မရှိရင် "No Port" ကို ပြထားတယ် (အလွတ် မထားရ)
                if (want.Count == 0)
                {
                    if (have.Count == 1 && have[0] == "No Port") return;
                    cmbPorts.Items.Clear();
                    cmbPorts.Items.Add("No Port");
                    cmbPorts.SelectedIndex = 0;
                    return;
                }

                bool same = want.Count == have.Count;
                if (same)
                {
                    for (int i = 0; i < want.Count; i++)
                    {
                        if (want[i] != have[i]) { same = false; break; }
                    }
                }
                if (same) return;   // ပြောင်းလဲမှု မရှိရင် ဘာမှ မလုပ်ဘူး (selection မထိခိုက်စေ)

                // Port list ပြောင်းရင် EDL auth session သက်တမ်းကုန် — ပြန် auth လုပ်ရမယ်
                qcAuthed = false;
                qcAuthedPort = "";

                string prev = cmbPorts.SelectedItem == null ? "" : cmbPorts.SelectedItem.ToString();
                cmbPorts.Items.Clear();
                if (want.Count > 0)
                {
                    foreach (string port in want) cmbPorts.Items.Add(port);
                    int idx = want.IndexOf(prev);
                    cmbPorts.SelectedIndex = idx >= 0 ? idx : 0;
                }
                else
                {
                    cmbPorts.Items.Add("No Port");
                    cmbPorts.SelectedIndex = 0;
                }
            }
            catch { }
        }

        // ================= DEVICE LIST (Brand → Model → CPU) — tab တစ်ခုချင်းစီအတွက် =================
        // Data: pmk_devices.json (exe ဘေးမှာ) — ရင်းမြစ်တွေ (တိကျသည်):
        //   mtk     : mtkclient config/brom_config.py → SoC + marketing name + hwcode
        //   qc      : edlclient Config/qualcomm_config.py msmids → Qualcomm SoC
        //   spd     : TomKing062 CVE-2022-38694 releases → brand + model + Unisoc chip
        //   samsung : Exynos SoC စာရင်း + Galaxy model (region variant မှတ်ချက်နဲ့)
        // Brand/Model မရွေးလည်း operation တွေ အလုပ်လုပ်တယ် (Auto Detect အတိုင်း)။

        private class DeviceProfile
        {
            public string Brand = "";
            public string Model = "";
            public string Cpu = "";
            public string Extra = "";
        }

        private static Dictionary<string, List<DeviceProfile>> deviceDb = null;
        // (မှတ်ချက်: Brand/Model မရွေးလည်း ခလုတ်တွေ အလုပ်လုပ်တယ် — disable မလုပ်ပါ)

        private void EnsureDeviceDbLoaded()
        {
            if (deviceDb != null) return;
            deviceDb = new Dictionary<string, List<DeviceProfile>>();
            try
            {
                string path = Path.Combine(Application.StartupPath, "pmk_devices.json");
                if (!File.Exists(path)) return;

                using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path)))
                {
                    foreach (JsonProperty plat in doc.RootElement.EnumerateObject())
                    {
                        List<DeviceProfile> list = new List<DeviceProfile>();
                        foreach (JsonElement item in plat.Value.EnumerateArray())
                        {
                            DeviceProfile dp = new DeviceProfile();
                            JsonElement v;
                            if (item.TryGetProperty("brand", out v)) dp.Brand = v.GetString() ?? "";
                            if (item.TryGetProperty("model", out v)) dp.Model = v.GetString() ?? "";
                            if (item.TryGetProperty("cpu", out v)) dp.Cpu = v.GetString() ?? "";
                            if (item.TryGetProperty("extra", out v)) dp.Extra = v.GetString() ?? "";
                            if (!string.IsNullOrEmpty(dp.Model)) list.Add(dp);
                        }
                        deviceDb[plat.Name] = list;
                    }
                }


            }
            catch (Exception ex)
            {
                Log("[!] Cannot read pmk_devices.json: " + ex.Message, Color.Orange);
            }
        }

        // tab တစ်ခုစီအတွက် Brand → Model → CPU picker ကို ဖန်တီးပြီး ထည့်တယ်
        // ===== tab အပေါ်ဆုံးမှာ Brand-Model ထားဖို့ — လက်ရှိ content ကို အောက်ကို ရွှေ့တယ် =====
        // tab page ရဲ့ control တွေကို container panel ထဲ ရွှေ့လိုက်တာမို့ Location တွေ အတိုင်းပဲ ရှိပြီး
        // container က y=offset မှာ ရှိတာမို့ အားလုံး အောက်ကို ရွှေ့သလို ဖြစ်တယ်။
        private Panel ShiftTabContentDown(TabPage page, int offset)
        {
            Panel host = new Panel
            {
                Location = new Point(0, offset),
                Size = new Size(Math.Max(50, page.ClientSize.Width), Math.Max(50, page.ClientSize.Height - offset)),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
                BackColor = page.BackColor
            };

            List<Control> move = new List<Control>();
            foreach (Control c in page.Controls) move.Add(c);

            page.Controls.Add(host);
            foreach (Control c in move)
            {
                page.Controls.Remove(c);
                host.Controls.Add(c);
            }
            return host;
        }

        private static IEnumerable<Control> AllControls(Control root)
        {
            foreach (Control c in root.Controls)
            {
                yield return c;
                foreach (Control sub in AllControls(c)) yield return sub;
            }
        }

        // picker tab ထဲက content host — ops strip မပြရင် offset 78 → 64 ပြန်ချုပ်ဖို့
        private readonly Dictionary<TabPage, Panel> _pickerHost = new();

        // tab → { offset (strip ပြ), offset (strip မပြ) } — QC က loader picker ပါလို့ တခြား tab ထက် ကွာတယ်
        private readonly Dictionary<TabPage, int[]> _pickerOff = new();

        private int PickerOff(TabPage page, bool withStrip)
        {
            if (page != null && _pickerOff.TryGetValue(page, out int[] v) && v != null && v.Length >= 2)
                return withStrip ? v[0] : v[1];
            return withStrip ? 86 : 64;
        }

        private void SetPickerOffset(TabPage page, int offset)
        {
            Panel host;
            if (page == null || !_pickerHost.TryGetValue(page, out host) || host == null || host.Top == offset) return;
            host.Height += host.Top - offset;
            host.Top = offset;
            // content ရွှေ့လိုက်လို့ partition box ရဲ့ အပေါ်ဆုံး နေရာ ပြောင်း → row အမြင့် ပြန်ချိန်
            SyncPartitionPanel(false);
        }

        // ===== Partition box layout — tab အလိုက် content အမြင့် တိုင်းပြီး =====
        // content အောက်မှာ ကပ်၊ ကျန်တဲ့ နေရာအကုန် partition grid က ယူ
        // (fixed 250F မဟုတ် — ချဲ့/ကျုံးလို့ရအောင် row2 ကို Percent လုပ်)
        private void SyncPartitionPanel(bool fixSplitter)
        {
            if (topLayout == null || tabControl == null || panelPartition == null) return;

            TabPage page = tabControl.SelectedTab;
            // tabHisi မှာ partition grid မသုံးလို့ မပြ — ကျန်တဲ့ tab အားလုံး ပြ
            bool show = (page == tabMtk || page == tabQc || page == tabSamsung ||
                         page == tabSpd || page == tabAdb);
            panelPartition.Visible = show;

            if (show)
            {
                int needed = TabNeededHeight(page);
                int total = topLayout.ClientSize.Height;
                if (total > 200)
                {
                    // platform bar (44) + partition အနည်းဆုံး (120) ချန်ပြီး tab row ကို ကန့်သတ် —
                    // window သေးရင် partition ပျောက်မသွားအောင် (content ပဲ အောက်က ဖြတ်ခံရ)
                    int maxRow = total - 44 - 120;
                    if (needed > maxRow) needed = Math.Max(160, maxRow);
                }
                // row style ပြောင်းတာက layout churn ဖြစ်ပြီး tab switch white flash ရှုပ် —
                // suspend ထားပြီး resume တစ်ခါတည်းနဲ့ တစ်ခါတည်း relayout
                topLayout.SuspendLayout();
                try
                {
                    if (topLayout.RowStyles[1].SizeType != SizeType.Absolute || topLayout.RowStyles[1].Height != needed)
                        topLayout.RowStyles[1] = new RowStyle(SizeType.Absolute, needed);
                    if (topLayout.RowStyles[2].SizeType != SizeType.Percent)
                        topLayout.RowStyles[2] = new RowStyle(SizeType.Percent, 100F);
                }
                finally { topLayout.ResumeLayout(true); }
            }
            else
            {
                if (topLayout.RowStyles[1].SizeType != SizeType.Percent)
                    topLayout.RowStyles[1] = new RowStyle(SizeType.Percent, 100F);
                if (topLayout.RowStyles[2].SizeType != SizeType.Absolute || topLayout.RowStyles[2].Height != 0F)
                    topLayout.RowStyles[2] = new RowStyle(SizeType.Absolute, 0F);
            }

            // user က splitter ကို လက်နဲ့ ဆွဲထားရင် သူ့ဆက်တင် အတိုင်း ထား
            // (tab switch တိုင်း 30% ပြန်ချိန် — resize မှာ မချိန်ဘူး, user proportion မပျက်အောင်)
            // ပြောင်းစရာမလိုရင် မထိ — SplitContainer resize က white flash အဓိက အကြောင်းရင်း
            if (fixSplitter && !userAdjustedSplit && splitMain != null)
            {
                try
                {
                    int want = (int)(splitMain.Width * 0.30);
                    if (Math.Abs(splitMain.SplitterDistance - want) > 2)
                        splitMain.SplitterDistance = want;
                }
                catch { }
            }
        }

        // tab content ရဲ့ အောက်ဆုံး အမြင့် (TabPage coords) + tab header — row1 အတွက် လိုအပ်တဲ့ အမြင့်
        private int TabNeededHeight(TabPage page)
        {
            if (page == null) return 300;

            int bottom = 0;
            Panel host = null;
            _pickerHost.TryGetValue(page, out host);

            // picker row (Brand/Model/Detect labels) — host မဟုတ်တဲ့ direct children
            foreach (Control c in page.Controls)
            {
                if (!c.Visible || c == host) continue;
                if (c.Bottom > bottom) bottom = c.Bottom;
            }

            // content host ထဲက အစစ်အမှန် content (groups + flash block) — host.Top ပါ ပေါင်း
            if (host != null && host.Visible)
            {
                int inner = 0;
                foreach (Control c in host.Controls)
                    if (c.Visible && c.Bottom > inner) inner = c.Bottom;
                int inPage = host.Top + inner;
                if (inPage > bottom) bottom = inPage;
            }

            // tab header အမြင့် — runtime တိုင်း (DPI/custom draw ကြောင့် constant မယူ)
            int pageTop = 30;
            try
            {
                int y = tabControl.DisplayRectangle.Y;
                if (y > 1) pageTop = y;
            }
            catch { }
            return pageTop + bottom + 8;   // +8 = partition header နဲ့ content ကြား gap
        }

        // control က root (TabPage) ထဲမှာ ပါ/မပါ
        private static bool IsInside(Control c, Control root)
        {
            for (Control x = c; x != null; x = x.Parent) if (x == root) return true;
            return false;
        }

        // Ops menu item → တကယ့် UI button (rule[0] = op နာမည် keyword, ကျန်တဲ့ဟာတွေ = button text keyword, အဆင့်အလိုက်)
        private static readonly string[][] OpsMap =
        {
            new[] { "temp root", "Xiaomi Temp Root" },
            new[] { "unlock code", "OEM Unlock" },
            new[] { "remove user lock", "Remove Lock", "Userlock Reset" },
            new[] { "remove all user lock", "Remove Lock", "Userlock Reset" },
            new[] { "userlock reset", "Userlock Reset", "Remove Lock" },
            new[] { "fastboot to edl", "Reboot EDL" },
            new[] { "usb de", "Enable ADB" },
            new[] { "signature failed", "DM Fix", "Undo Vbmeta" },
            new[] { "reset to factory", "Factory Reset" },
            // "[mtp mode]" က reset-to-factory နောက်မှ — "Reset to factory [mtp mode]" မှာ Factory Reset ကို ဦးစားပေး
            new[] { "[mtp mode]", "MTP FRP" },
            new[] { "factory reset", "Factory Reset" },
            new[] { "frp", "FRP Reset", "FRP Remove", "MTP FRP" },
            new[] { "bootloader relock", "Relock Bootloader" },
            new[] { "bootloader unlock", "Unlock Bootloader", "START UNLOCK" },
            new[] { "reboot download", "Reboot Download" },
            new[] { "reboot recovery", "Reboot Recovery" },
            new[] { "reboot fastboot", "Reboot Fastboot" },
            new[] { "reboot to fastboot", "Reboot Fastboot" },
            new[] { "reboot edl", "Reboot EDL" },
            new[] { "reboot system", "Reboot System" },
            new[] { "dm verity", "DM Fix", "Undo Vbmeta" },
            new[] { "patch dm verify", "DM Fix", "Undo Vbmeta" },
            new[] { "vbmeta", "Undo Vbmeta", "DM Fix" },
            new[] { "orange", "Orange State Fix" },
            new[] { "sideload", "Sideload ZIP" },
            new[] { "softbrick", "SoftBrick Fix" },
            new[] { "enable adb", "Enable ADB" },
            new[] { "install driver", "Install Drivers" },
            new[] { "uninstall driver", "Uninstall Drivers" },
            new[] { "health check", "Storage Health" },
            new[] { "battery health", "Battery Health" },
            new[] { "screenshot", "Screenshot" },
            new[] { "pit", "Download PIT" },
            new[] { "read full info", "Read Full Info" },
            new[] { "read info", "Read Info", "Read Full Info", "MTP Read Info" },
            new[] { "read fastboot", "Read Fastboot" },
            new[] { "check arb", "Check ARB" },
            // 44 no-impl op တွေအတွက် ထပ် map — demo/sim/imei/rpmb/account တွေက server/hardware ကိုယ်ရေး လိုလို့ ကျန်
            new[] { "adb enable", "Enable ADB" },
            new[] { "make root", "Xiaomi Temp Root" },
            // "auth bypass" → Firehose Loader button: SETUP group ဖယ်ပြီးနောက် button မရှိတော့ဘူး — rule ကိုပါ ဖယ်
            new[] { "network security", "NV Erase", "NV Restore" },
        };

        // ဒီ tab ထဲက button ထဲမှာ op နဲ့ ကိုက်တဲ့ဟာ ရှာ — မရှိရင် chip ရဲ့ platform tab ကို ဆက်ရှာ (ADB tab က
        // chip အားလုံးပြတာမို့ MTK/QC/Samsung/SPD/Hisi tab တွေက button တွေကို ပါ ပြန်ရှာတယ်)
        private Button FindOpsButton(TabPage page, string op, string chip = null)
        {
            if (page == null) return null;
            string low = op.ToLowerInvariant();
            int ci = op.IndexOf(": ");
            string opName = ci > 0 ? op.Substring(ci + 2) : op;

            // 1) op နာမည်နဲ့ တိုက်ရိုက် စာသားကိုက်တဲ့ button — rule မတိုင်ခင် ဦးစားပေး
            //    (ဥပမာ op "Reset to factory" → button "Reset to factory" / "(master clear)" စသည်)
            Button direct = opName.Length >= 3 ? FindOpsButtonIn(page, new[] { "", opName }) : null;
            if (direct == null && !string.IsNullOrEmpty(chip))
                direct = opName.Length >= 3 ? FindOpsButtonIn(ChipPlatformTab(chip), new[] { "", opName }) : null;
            // ဒီ tab / platform tab မှာ မရှိရင် — တခြား tab တွေကို ဆက်ရှာ (chip မဲ မဖြစ်အောင်)
            if (direct == null && opName.Length >= 3)
                direct = FindOpsButtonOtherTabs(page, new[] { "", opName });
            if (direct != null) return direct;

            // 2) OpsMap rules
            foreach (string[] rule in OpsMap)
            {
                if (!low.Contains(rule[0])) continue;
                Button best = FindOpsButtonIn(page, rule);
                if (best == null && !string.IsNullOrEmpty(chip))
                    best = FindOpsButtonIn(ChipPlatformTab(chip), rule);
                if (best == null)
                    best = FindOpsButtonOtherTabs(page, rule);
                return best;
            }
            return null;
        }

        // ကိုယ့် tab နဲ့ platform tab မှာ မတွေ့ရင် — ADB tab (အထွေထွေ) ကနေ platform tab အလိုက် ဆက်ရှာ
        private Button FindOpsButtonOtherTabs(TabPage page, string[] rule)
        {
            foreach (TabPage t in new[] { tabAdb, tabMtk, tabQc, tabSamsung, tabSpd, tabHisi })
            {
                if (t == null || t == page) continue;
                Button b = FindOpsButtonIn(t, rule);
                if (b != null) return b;
            }
            return null;
        }

        // ဒီ tab ထဲမှာ op နာမည် ပါတဲ့ button ရှိ/မရှိ (disabled ဖြစ်ဖြစ် ဂရုမပြု) —
        // ရှိရင် strip မှာ ထပ်မပြတော့ဘူး (button ကိုယ်တိုင် အောက်မှာ ရှိပြီးသား)
        private Button PageHasOpButton(TabPage page, string op)
        {
            if (page == null) return null;
            int ci = op.IndexOf(": ");
            string name = ci > 0 ? op.Substring(ci + 2) : op;
            Button found = null;
            foreach (Control c in AllControls(page))
            {
                Button b = c as Button;
                if (b == null) continue;
                if (b.Tag is string tg && tg == "pmkOps") continue;
                if (b.Text.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (found == null || b.Text.Length < found.Text.Length) found = b;
            }
            return found;
        }

        // rule တစ်ခုအတွက် page ထဲက အသေးဆုံး keyword ကိုက် button ကို ရှာ
        // (ops strip chip တွေကို ရှာမခံအောင် Tag "pmkOps" ကို ကျော်)
        private Button FindOpsButtonIn(TabPage page, string[] rule)
        {
            if (page == null) return null;
            Button best = null;
            int bestLen = int.MaxValue;
            for (int k = 1; k < rule.Length; k++)
            {
                foreach (Control c in AllControls(page))
                {
                    Button b = c as Button;
                    if (b == null || !b.Enabled) continue;
                    if (b.Tag is string tg && tg == "pmkOps") continue;
                    if (b.Text.IndexOf(rule[k], StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (b.Text.Length < bestLen) { best = b; bestLen = b.Text.Length; }
                }
                if (best != null) break;   // keyword အဆင့်ကို ပိုဦးစားပေး
            }
            return best;
        }

        // chip ရဲ့ chipset family → tab (ADB tab chip အတွက် fallback ရှာတဲ့နေရာ)
        private TabPage ChipPlatformTab(string chip)
        {
            string c = (chip ?? "").ToLowerInvariant();
            if (c.Contains("qualcomm")) return tabQc;
            if (c.Contains("mediatek")) return tabMtk;
            if (c.Contains("samsung")) return tabSamsung;
            if (c.Contains("spreadtrum") || c.Contains("unisoc")) return tabSpd;
            if (c.Contains("hisilicon")) return tabHisi;
            return null;
        }

        // op တွေအတွက် button မရှိရင် — တကယ်လုပ်ပေးနိုင်တဲ့ built-in လုပ်ဆောင်ချက် (chip အရောင် = အပြာ)
        // ဘယ် op မှ မကိုက်ရင် false → chip က disabled ဖြစ်
        private bool GetOpsImpl(string op, out Func<Task> run, out string tip)
        {
            run = null;
            tip = "";
            string low = op.ToLowerInvariant();

            // ---- fastboot: boot slot a/b ----
            if (low.Contains("boot slot set"))
            {
                tip = "fastboot set_active a / b — ဖုန်း fastboot mode မှာ ရှိရမယ်";
                run = async () =>
                {
                    string slot = PromptInput("Boot Slot Set", "boot slot set — slot ထည့်ပါ (a သို့မဟုတ် b):");
                    if (string.IsNullOrEmpty(slot)) { Log("[i] Boot slot set: cancelled (no slot).", Color.Gray); return; }
                    slot = slot.Trim().ToLowerInvariant();
                    if (slot != "a" && slot != "b")
                    {
                        Log("[!] Boot slot set: slot သည် a / b ဖြစ်ရမယ် — '" + slot + "' (cancelled)", Color.Orange);
                        return;
                    }
                    if (!await FastbootReadyAsync("set_active")) return;
                    await ExecuteCommandCleanAsync("fastboot.exe", "set_active " + slot, "Boot slot → " + slot);
                };
                return true;
            }

            // ---- ADB: screen lock ယာယီ ပိတ် / ပြန်ဖွင့် ----
            if (low.Contains("disable user lock") || low.Contains("enable user lock"))
            {
                bool disabling = low.Contains("disable");
                tip = "adb shell locksettings set-disabled " + (disabling ? "true" : "false") +
                      " (screen lock ယာယီပိတ် / ပြန်ဖွင့် — root မလို)";
                run = async () =>
                {
                    string dv = await ExecuteCommandQuickAsync("adb.exe", "devices");
                    if (string.IsNullOrWhiteSpace(dv) || !dv.Contains("\tdevice"))
                    {
                        Log("[!] ADB device not found — USB debugging ဖွင့်ပြီး cable ချိတ်ပါ.", Color.Orange);
                        return;
                    }
                    await ExecuteCommandCleanAsync("adb.exe", "shell locksettings set-disabled " + (disabling ? "true" : "false"),
                        disabling ? "Disable user lock" : "Enable user lock");
                    string st = await ExecuteCommandQuickAsync("adb.exe", "shell locksettings get-disabled");
                    Log("[i] locksettings get-disabled: " + (string.IsNullOrWhiteSpace(st) ? "(empty)" : st.Trim()), Color.Gray);
                };
                return true;
            }

            // ---- ADB: lock settings ဖတ် ----
            if (low.Contains("read user locks"))
            {
                tip = "adb shell dumpsys lock_settings + locksettings get-disabled (pattern / PIN / password status)";
                run = async () =>
                {
                    string dv = await ExecuteCommandQuickAsync("adb.exe", "devices");
                    if (string.IsNullOrWhiteSpace(dv) || !dv.Contains("\tdevice"))
                    {
                        Log("[!] ADB device not found — USB debugging ဖွင့်ပြီး cable ချိတ်ပါ.", Color.Orange);
                        return;
                    }
                    await ExecuteCommandCleanAsync("adb.exe", "shell dumpsys lock_settings", "Read user locks", false, true);
                    string st = await ExecuteCommandQuickAsync("adb.exe", "shell locksettings get-disabled");
                    Log("[i] locksettings get-disabled: " + (string.IsNullOrWhiteSpace(st) ? "(empty)" : st.Trim()), Color.Gray);
                };
                return true;
            }

            // ---- MTK: BROM (0e8d:0003) port ကို စောင့် ----
            if (low.Contains("enter brom mode"))
            {
                tip = "BROM (0e8d:0003) port ပေါ်လာမလား စောင့် — vol up/down နှိပ်ပြီး USB ချိတ်ပါ (30 sec)";
                run = async () =>
                {
                    Log("========================================================", Color.FromArgb(0, 180, 255));
                    Log("   ENTER BROM MODE (wait for 0e8d:0003)", Color.White);
                    Log("========================================================", Color.FromArgb(0, 180, 255));
                    Log("[i] ဖုန်း power off → volume up + volume down နှိပ်ပြီး USB cable ချိတ်ပါ.", Color.White);
                    string lastMode = "";
                    for (int i = 0; i < 30 && !stopRequested; i++)
                    {
                        RefreshMtkDetection();
                        await Task.Delay(1000);
                        string mode = cachedMtkModeText ?? "";
                        if (mode.IndexOf("BROM", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            Log("[OK] BROM detected: " +
                                (string.IsNullOrEmpty(cachedMtkComPort) ? "USB" : cachedMtkComPort) +
                                "  (" + mode + ")", Color.LightGreen);
                            return;
                        }
                        if (mode != lastMode)
                        {
                            lastMode = mode;
                            if (mode.Length > 0) Log("[i] " + mode + " — စောင့်နေတယ် (BROM မရောက်သေး)", Color.Gray);
                        }
                    }
                    Log("[!] BROM port not detected in 30 s — cable / driver စစ်ပြီး ထပ်စမ်းပါ.", Color.Orange);
                };
                return true;
            }

            // ---- MTK: BROM session ကနေ power off ----
            if (low.Contains("exit brom mode"))
            {
                tip = "pmk_mtk_op.py reset — BROM session ကနေ ဖုန်းကို power off";
                run = async () =>
                {
                    await ExecuteCommandCleanAsync("python", BuildMtkOpArgs("reset"), "Exit BROM (power off)");
                };
                return true;
            }

            // ---- ADB: Myanmar Unicode / Zawgyi font APK ထည့် ----
            if (low.Contains("install myanmar") && low.Contains("font"))
            {
                tip = "APK file ရွေးပြီး adb install -r (Myanmar Unicode / Zawgyi font)";
                run = async () =>
                {
                    using (OpenFileDialog ofd = new OpenFileDialog { Filter = "Android App Package (*.apk)|*.apk" })
                    {
                        if (ofd.ShowDialog(this) != DialogResult.OK)
                        {
                            Log("[i] Font install: APK မရွေးဘဲ ပစ်လိုက်တယ် (cancel).", Color.Gray);
                            return;
                        }
                        await ExecuteCommandCleanAsync("adb.exe", "install -r \"" + ofd.FileName + "\"",
                            "Install " + Path.GetFileName(ofd.FileName));
                    }
                };
                return true;
            }

            // ---- Xiaomi: Find Device status (read-only — ဘာမှ မပြောင်း) ----
            if (low.Contains("check find device status"))
            {
                tip = "adb — Xiaomi Find Device / Mi Cloud status ကို read-only ဖတ် (settings + getprop)";
                run = async () =>
                {
                    string dv = await ExecuteCommandQuickAsync("adb.exe", "devices");
                    if (string.IsNullOrWhiteSpace(dv) || !dv.Contains("\tdevice"))
                    {
                        Log("[!] ADB device not found — USB debugging ဖွင့်ပြီး cable ချိတ်ပါ.", Color.Orange);
                        return;
                    }
                    string st = (await ExecuteCommandQuickAsync("adb.exe", "shell settings get secure xiaomi_find_device_status")).Trim();
                    Log("[i] settings secure xiaomi_find_device_status = " + (st.Length == 0 ? "(empty)" : st), Color.Gray);
                    string props = await ExecuteCommandQuickAsync("adb.exe", "shell getprop");
                    bool any = false;
                    foreach (string line in props.Replace("\r", "").Split('\n'))
                    {
                        string l = line.Trim();
                        if (l.IndexOf("find_device", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            l.IndexOf("finddevice", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            l.IndexOf("miui.cloud", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            Log("    " + l, Color.Gray);
                            any = true;
                        }
                    }
                    if (!any)
                        Log("[i] find_device prop မတွေ့ — MIUI/HyperOS မှာ prop အမည် ကွာနိုင်တယ် (read-only, ပြောင်းလဲမှု မရှိ)", Color.Gray);
                };
                return true;
            }

            return false;
        }

        // ===== SPD loader matching =====
        // spd/loaders/<chip>_<brand_model>/*.bin folder တွေကနေ chip prefix တွေ ထုတ်ပါ
        // (sc9832e_itel_a662l → "sc9832e", ums512_Realme_C21y → "ums512")
        private List<string> SpdLoaderChips()
        {
            List<string> chips = new List<string>();
            try
            {
                string root = Path.Combine(Application.StartupPath, "spd", "loaders");
                if (!Directory.Exists(root)) return chips;
                foreach (string dir in Directory.GetDirectories(root))
                {
                    string name = Path.GetFileName(dir).ToLowerInvariant();
                    int u = name.IndexOf('_');
                    string chip = u > 0 ? name.Substring(0, u) : name;
                    chip = new string(chip.Where(c => char.IsLetterOrDigit(c)).ToArray());
                    if (chip.Length > 0 && !chips.Contains(chip)) chips.Add(chip);
                }
            }
            catch { }
            return chips;
        }

        // model အမည်ထဲက chipset keyword ကို loader chip prefix နဲ့ တိုက်စစ်
        private bool SpdModelMatchesLoader(string model, List<string> chips)
        {
            if (string.IsNullOrWhiteSpace(model)) return false;
            string m = new string(model.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c)).ToArray());

            // Tiger T6xx = UMS512, Tiger T70x = UMS9230 (folder name မှာ မပါလောက် အထူးသတ်မှတ်)
            string want = null;
            if (m.Contains("t61") || m.Contains("t62") || m.Contains("t616")) want = "ums512";
            else if (m.Contains("t70")) want = "ums9230";
            if (want != null) return chips.Any(c => c.StartsWith(want));

            foreach (string chip in chips)
                if (chip.Length >= 4 && m.Contains(chip)) return true;
            return false;
        }

        // SPD tab အတွက် — loader မရှိတဲ့ chipset တွေကို ဖျောက်ပေး
        private Dictionary<string, List<MstModelEntry>> FilterSpdByAvailableLoaders(Dictionary<string, List<MstModelEntry>> view)
        {
            List<string> chips = SpdLoaderChips();
            if (chips.Count == 0) return view;   // loader မရှိဘူး = filter မလုပ် (အရင်လက်ဟန်)
            Dictionary<string, List<MstModelEntry>> outv = new Dictionary<string, List<MstModelEntry>>();
            foreach (KeyValuePair<string, List<MstModelEntry>> kv in view)
            {
                List<MstModelEntry> keep = kv.Value.Where(e => SpdModelMatchesLoader(e.Model, chips)).ToList();
                if (keep.Count > 0) outv[kv.Key] = keep;
            }
            return outv;
        }

        // tab တစ်ခုစီအတွက် Brand → Model picker — model DB (chip အလိက်) + supported ops + Detect
        // chipFilter: "mediatek"/"samsung"/"spreadtrum"/"hisilicon" / null (= ADB tab, all chips)
        // picker tab တစ်ခုစီမှာ Brand/Model အောက် ထည့်မယ့် file row အရေအတွက်
        private int FileRowCount(TabPage page)
        {
            if (page == tabMtk) return 3;   // DA Agent / Auth / Preloader
            if (page == tabSpd) return 1;   // Loader (FDL)
            return 0;                        // QC က grpQcLoaderPicker ထဲမှာ သီးသန့် ထည့်
        }

        private void AddDevicePickerToTab(TabPage page, string chipFilter, string tag, int y, string title, bool withDetect, bool withModelPicker = true, int offStrip = 86, int offNoStrip = 64)
        {
            try
            {
                // ADB / FB tab — model picker မထားတော့ဘူး: Detect + info label ပဲ
                // (ADB/FB ခလုတ်တွေက model ပေါ် မမူတည်၊ list က chipset မစစ်ဘဲ 4603 model အားလုံးမို့)
                if (!withModelPicker)
                {
                    Label info = new Label
                    {
                        Text = "—",
                        Location = new Point(120, 11),
                        Size = new Size(770, 18),
                        AutoEllipsis = true,
                        ForeColor = Color.FromArgb(86, 145, 250),
                        Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
                    };
                    Button det = Create3DButton("🔍 Detect", 15, 7, 96, 26, ButtonTheme.Cyan);
                    det.Name = "pmkDetectBtn";
                    det.Click += async (s, e) => await RunAdbDetectAsync(info, tag);
                    page.Controls.Add(det);
                    page.Controls.Add(info);
                    det.BringToFront();
                    info.BringToFront();
                    return;
                }

                Dictionary<string, List<MstModelEntry>> view = MstView(chipFilter);
                // SPD tab — spd/loaders ထဲမှာ loader မရှိတဲ့ chipset တွေကို ဖျောက်ချ (FDL မရှိဘူး = မလုပ်နိုင်)
                if (page == tabSpd) view = FilterSpdByAvailableLoaders(view);
                // file row n ခု (24px + 2px gap) ကြောင့် content host ကို အောက်ရွှေ့ — 4 = row/strip gap, 26 = row pitch
                int fileRows = FileRowCount(page);
                int fileExtra = fileRows > 0 ? 4 + fileRows * 26 : 0;
                _pickerOff[page] = new[] { offStrip + fileExtra, offNoStrip + fileExtra };
                int total = 0;
                foreach (List<MstModelEntry> l in view.Values) total += l.Count;

                Label cap = new Label
                {
                    Text = title + "  (" + total + " models)",
                    Location = new Point(15, y - 19),
                    AutoSize = true,
                    ForeColor = Color.FromArgb(86, 145, 250),
                    Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
                };

                Label lbBrand = new Label
                {
                    Text = "Brand:",
                    Location = new Point(15, y + 4),
                    AutoSize = true,
                    ForeColor = Color.FromArgb(150, 165, 185),
                    Font = new Font("Segoe UI", 8.5f)
                };

                ComboBox cbBrand = new ComboBox
                {
                    Location = new Point(62, y),
                    Size = new Size(140, 24),
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    BackColor = Color.FromArgb(38, 42, 50),
                    ForeColor = Color.Gainsboro,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Segoe UI", 8.5f)
                };

                Label lbModel = new Label
                {
                    Text = "Model:",
                    Location = new Point(210, y + 4),
                    AutoSize = true,
                    ForeColor = Color.FromArgb(150, 165, 185),
                    Font = new Font("Segoe UI", 8.5f)
                };

                ComboBox cbModel = new ComboBox
                {
                    Location = new Point(255, y),
                    Size = new Size(250, 24),
                    // 4600+ model — စာရိုက်ရှာ type-ahead ရအောင် editable
                    DropDownStyle = ComboBoxStyle.DropDown,
                    BackColor = Color.FromArgb(38, 42, 50),
                    ForeColor = Color.Gainsboro,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Segoe UI", 8.5f)
                };

                Label lbChip = new Label
                {
                    Text = "—",
                    Location = new Point(513, y + 4),
                    Size = new Size(175, 18),
                    AutoEllipsis = true,
                    ForeColor = Color.FromArgb(86, 145, 250),
                    Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
                };

                Button btDetect = null;
                if (withDetect)
                {
                    btDetect = Create3DButton("🔍 Detect", 0, 0, 96, 26, ButtonTheme.Cyan);
                    btDetect.Name = "pmkDetectBtn";
                    btDetect.Location = new Point(817, y - 1);
                }

                // ===== Ops strip — model ရွေးရင် (button ထဲ မပါတဲ့) ops တွေကို chip အဖြစ် ဒီမှာပြ =====
                // chip အရောင်: အပြာ = built-in implementation / မဲ = button မရှိသေး /
                // အစိမ်း = တခြား tab က button နဲ့ ချိတ်ထား (cross-tab)
                Panel opsStrip = new Panel
                {
                    Location = new Point(15, y + 26),
                    Size = new Size(Math.Max(150, page.ClientSize.Width - 30), 30),
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                    BackColor = Color.FromArgb(24, 27, 33),
                    Visible = false
                };
                Panel opsView = new Panel
                {
                    Dock = DockStyle.Fill,
                    BackColor = opsStrip.BackColor
                };
                FlowLayoutPanel opsFlow = new FlowLayoutPanel
                {
                    WrapContents = false,
                    AutoSize = false,
                    BackColor = opsStrip.BackColor,
                    Padding = new Padding(0),
                    Margin = new Padding(0),
                    Location = new Point(0, 0),
                    Size = new Size(10, 30)
                };
                opsView.Controls.Add(opsFlow);
                opsStrip.Controls.Add(opsView);

                int opsScroll = 0;

                void UpdateOpsScroll()
                {
                    int max = Math.Max(0, opsFlow.Width - opsView.ClientSize.Width + 4);
                    if (opsScroll > max) opsScroll = max;
                    if (opsScroll < 0) opsScroll = 0;
                    opsFlow.Location = new Point(-opsScroll, 0);
                    foreach (Control sc in opsStrip.Controls)
                        if (sc is Button b) b.Enabled = b.Text == "◀" ? opsScroll > 0 : opsScroll < max;
                }

                foreach (string arrow in new string[] { "◀", "▶" })
                {
                    Button sb = new Button
                    {
                        Text = arrow,
                        Dock = DockStyle.Right,
                        Width = 20,
                        FlatStyle = FlatStyle.Flat,
                        BackColor = Color.FromArgb(38, 44, 54),
                        ForeColor = Color.FromArgb(150, 165, 185),
                        Font = new Font("Segoe UI", 9f),
                        Margin = new Padding(0),
                        TabStop = false
                    };
                    sb.FlatAppearance.BorderSize = 0;
                    sb.TabStop = false;
                    opsStrip.Controls.Add(sb);
                    if (arrow == "◀") sb.Click += (s, e) => { opsScroll -= 80; UpdateOpsScroll(); };
                    else sb.Click += (s, e) => { opsScroll += 80; UpdateOpsScroll(); };
                }

                opsView.Resize += (s, e) => UpdateOpsScroll();
                opsFlow.MouseWheel += (s, e) => { opsScroll -= e.Delta / 2; UpdateOpsScroll(); };

                List<MstModelEntry> cur = new List<MstModelEntry>();
                MstModelEntry sel = null;
                bool prog = false;
                bool inited = false;   // startup fill ကို loader auto-link မလုပ်ရ

                void ShowState()
                {
                    if (sel == null)
                    {
                        // MTK: model ဘာမှ မရွေးရသေးရင် "Auto Detect" — တခြား tab တွေ "—"
                        lbChip.Text = page == tabMtk ? "Auto Detect" : "—";
                        lbChip.ForeColor = Color.FromArgb(86, 145, 250);
                        opsStrip.Visible = false;
                        SetPickerOffset(page, PickerOff(page, false));
                        return;
                    }
                    string chip = sel.Chip.Length > 0 && sel.Chip != "x" ? sel.Chip : "?";
                    lbChip.Text = chip + " · " + sel.Ops.Count + " ops";
                    lbChip.ForeColor = Color.FromArgb(0, 230, 118);
                    RebuildOpsStrip();
                }

                void SelectModel(string model)
                {
                    sel = null;
                    if (!string.IsNullOrEmpty(model))
                        sel = cur.FirstOrDefault(x => x.Model.Equals(model, StringComparison.OrdinalIgnoreCase));
                    if (sel == null && cur.Count == 1) sel = cur[0];
                    if (page == tabSpd) _spdSelectedModel = sel?.Model ?? "";
                    ShowState();
                    // QC: device model ရွေးရင် Firehose loader ကို auto ချိတ်ပေး
                    if (inited && page == tabQc && sel != null)
                        AutoLinkQcLoader(cbBrand.Text, sel.Model);
                }

                void FillModels(string brand)
                {
                    prog = true;
                    cbModel.Items.Clear();
                    cur = new List<MstModelEntry>();
                    if (!string.IsNullOrEmpty(brand) && view.ContainsKey(brand)) cur = view[brand];
                    foreach (MstModelEntry e in cur) cbModel.Items.Add(e.Model);
                    cbModel.Enabled = cur.Count > 0;
                    if (cur.Count > 0)
                    {
                        cbModel.SelectedIndex = 0;
                        SelectModel(cbModel.SelectedItem?.ToString());
                    }
                    else
                    {
                        sel = null;
                        ShowState();
                    }
                    prog = false;
                }

                if (view.Count > 0)
                {
                    // SPD tab — brand အားလုံးကို ဖျောက်ပြီး chipset list တစ်ခုတည်းသော generic brand တစ်ခုပဲ ထားမယ်
                    var brandKeys = page == tabSpd
                        ? view.Keys.Where(k => k.StartsWith("#Generic", StringComparison.OrdinalIgnoreCase))
                                   .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()
                        : view.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

                    foreach (string b in brandKeys)
                        cbBrand.Items.Add(b);
                    // MTK tab: အစမှာ brand/model အလိုအလျောက် မရွေး — "Auto Detect" ပဲ ပြ
                    if (page == tabMtk) cbBrand.Items.Insert(0, "Auto Detect");
                    prog = true;
                    cbBrand.SelectedIndex = 0;
                    prog = false;
                }
                else
                {
                    cbBrand.Items.Add("(no models)");
                    prog = true;
                    cbBrand.SelectedIndex = 0;
                    prog = false;
                    cbModel.Enabled = false;
                }
                inited = true;

                cbBrand.SelectedIndexChanged += (s, e) =>
                {
                    if (prog) return;
                    FillModels(cbBrand.SelectedItem?.ToString() ?? "");
                };
                cbModel.TextChanged += (s, e) =>
                {
                    if (prog) return;
                    SelectModel(cbModel.Text.Trim());
                };
                cbModel.SelectedIndexChanged += (s, e) =>
                {
                    if (prog) return;
                    SelectModel(cbModel.SelectedItem?.ToString() ?? cbModel.Text);
                };

                // model ရွေးတိုင်း ops strip ကို rebuild —
                // ဒီ tab ထဲက button နဲ့ ပါပြီးသား op တွေကို မပြ (button ကိုယ်တိုင် ကိုင်ရုံ)၊
                // ကျန်တဲ့ op တွေပဲ chip အဖြစ် ပြ:
                //   အပြာ = built-in implementation / မဲ = button မရှိသေး /
                //   အစိမ်း = တခြား tab က button ကို ချိတ်ထား (cross-tab)
                void RebuildOpsStrip()
                {
                    opsStrip.Visible = false;
                    SetPickerOffset(page, PickerOff(page, false));
                    opsScroll = 0;
                    opsFlow.Location = new Point(0, 0);
                    opsFlow.Controls.Clear();
                    if (sel == null || sel.Ops.Count == 0) return;

                    Font chipFont = new Font("Segoe UI", 9f);
                    string model = sel.Model;
                    string plat = sel.Chip;
                    int totalW = 0;

                    foreach (string op in sel.Ops)
                    {
                        int ci = op.IndexOf(": ");
                        string opName = ci > 0 ? op.Substring(ci + 2) : op;
                        string txt = opName.Length > 34 ? opName.Substring(0, 34) + "…" : opName;

                        Func<Task> implRun = null;
                        string implTip = "";
                        Button target = FindOpsButton(page, op, plat);
                        // ဒီ tab ထဲမှာ button ရှိပြီးသားဆိုရင် (disabled ဖြစ်ဖြစ်) strip မှာ ထပ်မပြတော့ဘူး
                        if (target != null && IsInside(target, page)) continue;
                        if (PageHasOpButton(page, op) != null) continue;
                        bool impl = false;
                        if (target == null) impl = GetOpsImpl(op, out implRun, out implTip);

                        Button chip = new Button
                        {
                            Text = txt,
                            Tag = "pmkOps",
                            Font = chipFont,
                            FlatStyle = FlatStyle.Flat,
                            UseMnemonic = false,
                            TabStop = false,
                            Margin = new Padding(4, 2, 0, 2),
                            Size = new Size(TextRenderer.MeasureText(txt, chipFont).Width + 24, 26)
                        };
                        chip.FlatAppearance.BorderSize = 0;

                        // MST-style log: chip နှိပ်တိုင်း ယခင် log ရှင်း (handler အစဉ်လိုက် run
                        // ဖြစ်လို့ ဒါက အရင်ဆုံး run — target button ရဲ့ ထပ်ရှင်းတာကို flag နဲ့ တား)
                        chip.Click += (s2, e2) => ClearLogForNewOp();

                        string tipText;
                        if (target != null)
                        {
                            // အစိမ်း — ဒီ tool ထဲက button နဲ့ ချိတ်ထား
                            chip.BackColor = Color.FromArgb(24, 64, 42);
                            chip.ForeColor = Color.FromArgb(160, 255, 180);
                            tipText = "Run → [" + target.Text + "] button";
                        }
                        else if (impl)
                        {
                            // အပြာ — built-in implementation (tool ကိုယ်တိုင် လုပ်ပေး)
                            chip.BackColor = Color.FromArgb(22, 58, 80);
                            chip.ForeColor = Color.FromArgb(150, 225, 255);
                            tipText = implTip;
                        }
                        else
                        {
                            // မဲ — button / implementation မရှိ (click ရင် log hint ပဲ)
                            // Enabled ထားတာ: disabled button ပေါ်မှာ WinForms ToolTip မပေါ်လို့
                            chip.BackColor = Color.FromArgb(36, 39, 45);
                            chip.ForeColor = Color.FromArgb(118, 126, 138);
                            tipText = "ဤ op အတွက် button / implementation မရှိသေးဘူး (log only)";
                        }
                        if (toolTipMain != null)
                            Tip(chip, tipText + Environment.NewLine + "op: " + op);

                        Button t = target;
                        Func<Task> r = implRun;
                        string on = opName;
                        string mn = model;
                        if (target != null || impl)
                        {
                            chip.Cursor = Cursors.Hand;
                            chip.Click += async (s, e) =>
                            {
                                if (t != null)
                                {
                                    Log("[ops] " + mn + " → " + on + "   [" + t.Text + "]",
                                        Color.FromArgb(0, 200, 255));
                                    // chip ကတော့ ရှင်းပြီးသား — target button မရှင်းအောင် flag ပေး
                                    suppressNextLogClear = true;
                                    try { t.PerformClick(); } finally { suppressNextLogClear = false; }
                                }
                                else if (r != null)
                                {
                                    Log("[ops] " + mn + " → " + on + "   [built-in]",
                                        Color.FromArgb(150, 225, 255));
                                    // autoReboot=false — double reset မဖြစ်အောင်
                                    await RunFlashWorkflowAsync(r, false);
                                }
                            };
                        }
                        else
                        {
                            chip.Click += (s, e) =>
                                Log("[ops] " + mn + " → " + on + "   (log only - no matching button)",
                                    Color.Gray);
                        }

                        opsFlow.Controls.Add(chip);
                        totalW += chip.Width + chip.Margin.Horizontal;
                    }

                    // button ထဲ အားလုံး ပါပြီးသားဆိုရင် strip ကို လုံးဝ မပြ — content ကိုလည်း 86 → 64 ပြန်ချုပ်
                    int shown = opsFlow.Controls.Count;
                    if (shown == 0)
                    {
                        lbChip.Text += " (all have buttons)";
                        return;
                    }
                    if (shown < sel.Ops.Count) lbChip.Text += " (" + shown + " extra)";
                    opsFlow.Size = new Size(Math.Max(totalW, 10), 30);
                    // ops strip (chip row) — Brand/Model အောက်က chips တွေ အသုံးမဝင်လို့ မပြတော့ဘူး (user request)
                    opsStrip.Visible = false;
                    SetPickerOffset(page, PickerOff(page, false));
                }

                if (btDetect != null)
                    btDetect.Click += async (s, e) => await RunAdbDetectAsync(lbChip, tag);

                if (view.Count > 0) FillModels(cbBrand.SelectedItem?.ToString() ?? "");

                List<Control> ctrls = new List<Control> { cap, lbBrand, cbBrand, lbModel, cbModel, lbChip };
                if (btDetect != null) ctrls.Add(btDetect);

                // ===== "Double click or Drag" file rows — ပုံတူ layout (Brand/Model အောက်) =====
                int rowW = page.ClientSize.Width - 30;
                if (page == tabMtk)
                {
                    ctrls.Add(BuildFilePickerRow(page, 15, y + 30, rowW, "DA Agent",
                        "Double click or Drag Download Agent Files Here",
                        "Download Agent (*.bin;*.da)|*.bin;*.da",
                        () => mtkDaPath, p => { mtkDaPath = p; SaveEdlPaths(); }));
                    ctrls.Add(BuildFilePickerRow(page, 15, y + 56, rowW, "Auth",
                        "Double click or Drag Auth Files Here",
                        "Auth file (*.auth;*.sig)|*.auth;*.sig",
                        () => mtkAuthPath, p => { mtkAuthPath = p; SaveEdlPaths(); }));
                    ctrls.Add(BuildFilePickerRow(page, 15, y + 82, rowW, "Preloader",
                        "Double click or Drag Preloader Files Here",
                        "Preloader (*.bin)|*.bin",
                        () => mtkPreloaderPath, p => { mtkPreloaderPath = p; SaveEdlPaths(); }));
                }
                else if (page == tabSpd)
                {
                    ctrls.Add(BuildFilePickerRow(page, 15, y + 30, rowW, "Loader",
                        "Double click or Drag Spreadtrum Loader Files",
                        "FDL loader (*.bin;*.fdl)|*.bin;*.fdl",
                        () => spdLoaderPath, p => { spdLoaderPath = p; SaveEdlPaths(); }));
                }

                page.Controls.AddRange(ctrls.ToArray());
                foreach (Control c in ctrls) c.BringToFront();
            }
            catch (Exception ex)
            {
                Log("[!] Device list UI error (" + tag + "): " + ex.Message, Color.Orange);
            }
        }

        // Detect — ADB ချိတ်ထားတဲ့ ဖုန်းရဲ့ model / SoC ကို ဖတ်ပြ (picker tab တွေနဲ့ ADB tab သုံးနိုင်)
        private async Task RunAdbDetectAsync(Label lbChip, string tag)
        {
            // adb device တကယ် ရှိမရှိ အရင်စစ် — မဟုတ်ရင် adb error စာသားကို model လို့ မှတ်မိမယ်
            string devList = await ExecuteCommandQuickAsync("adb.exe", "devices");
            if (string.IsNullOrWhiteSpace(devList) || !devList.Contains("\tdevice"))
            {
                lbChip.Text = "— (no ADB device - boot into Android and press Detect)";
                lbChip.ForeColor = Color.LightGray;
                Log("[i] " + tag + " detect: no ADB device found (retry in Android mode).", Color.Orange);
                return;
            }

            string mdl = (await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.product.model")).Trim();
            string soc = (await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.soc.model")).Trim();
            if (string.IsNullOrWhiteSpace(soc))
                soc = (await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.board.platform")).Trim();
            string hw = (await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.hardware")).Trim();
            if (!string.IsNullOrWhiteSpace(soc) || !string.IsNullOrWhiteSpace(hw))
            {
                string cpu = string.IsNullOrWhiteSpace(soc) ? hw : soc;
                if (!string.IsNullOrWhiteSpace(hw) && !hw.Equals(cpu, StringComparison.OrdinalIgnoreCase))
                    cpu += " / " + hw;
                lbChip.Text = (mdl + " → " + cpu).Trim();
                lbChip.ForeColor = Color.LightGreen;
                Log("[OK] " + tag + " auto-detect: " + lbChip.Text, Color.LightGreen);
                WarnUnknownModel(mdl);
            }
            else
            {
                lbChip.Text = "— (no ADB device - boot into Android and press Detect)";
                lbChip.ForeColor = Color.LightGray;
            }
        }

        private void BuildDevicePickers()
        {
            try
            {
                // Brand-Model device list: MTK/Samsung/SPD ပဲ — ADB/Hisi က Detect row (40) ပဲ
                // MTK/SPD: file row တွေပါလို့ 64 + fileExtra (MTK 82 / SPD 30)
                _pickerHost[tabMtk] = ShiftTabContentDown(tabMtk, 146);
                _pickerHost[tabSamsung] = ShiftTabContentDown(tabSamsung, 64);
                _pickerHost[tabSpd] = ShiftTabContentDown(tabSpd, 94);
                _pickerHost[tabAdb] = ShiftTabContentDown(tabAdb, 40);
                _pickerHost[tabHisi] = ShiftTabContentDown(tabHisi, 40);
                // QC: device list မပြ (loader picker ပဲ) — panel 76px ဖြစ်လို့ content 80
                _pickerHost[tabQc] = ShiftTabContentDown(tabQc, 80);

                AddDevicePickerToTab(tabMtk, "mediatek", "MTK", 26, "MTK DEVICE LIST", true);
                AddDevicePickerToTab(tabSamsung, "samsung", "SAMSUNG", 26, "SAMSUNG DEVICE LIST", true);
                AddDevicePickerToTab(tabSpd, "spreadtrum", "SPD", 26, "SPREADTRUM / UNISOC DEVICE LIST", true);
                AddDevicePickerToTab(tabAdb, null, "ADB", 26, "DEVICE LIST", true, withModelPicker: false);
                AddDevicePickerToTab(tabHisi, "hisilicon", "HISILICON", 26, "HISILICON DEVICE LIST", true, withModelPicker: false);
                // QC ရဲ့ QUALCOMM DEVICE LIST (Brand/Model/Detect) — အသုံးမဝင်လို့ ဖယ် (loader picker ပဲ ကျန်)

                // QC tab: Brand/Model = Firehose loader picker (pmk_qc_loaders.json) — မပြောင်း
                if (grpQcLoaderPicker != null)
                {
                    grpQcLoaderPicker.Parent?.Controls.Remove(grpQcLoaderPicker);
                    tabQc.Controls.Add(grpQcLoaderPicker);
                    grpQcLoaderPicker.Location = new Point(15, 11);
                    grpQcLoaderPicker.BringToFront();
                    // row2 — ပုံတူ Loader file row (LoadEdlPaths ပြီးမှ ဆိုလို့ saved loader ပါ ပြ)
                    if (qcFileRow == null)
                    {
                        qcFileRow = BuildFilePickerRow(grpQcLoaderPicker, 9, 46, 912, "Loader",
                            "Double click or Drag Firehose Loader Files",
                            "Firehose loader (*.mbn;*.elf;*.bin;*.melf)|*.mbn;*.elf;*.bin;*.melf",
                            () => edlLoaderPath, ApplyQcLoader);
                        grpQcLoaderPicker.Controls.Add(qcFileRow);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("[!] Device list UI error: " + ex.Message, Color.Orange);
            }
        }

        // ================= PHONE MODEL DB (pmk_models.json — brand → model → operations) =================
        // Data: mobileseaservice.net/devices (supported-devices list, 4,600+ models)
        // Structure: { "source": ..., "total": N, "brands": { "Brand": [ {model, chip, ops[]} ] } }
        private class MstModelEntry
        {
            public string Model = "";
            public string Chip = "";
            public List<string> Ops = new List<string>();
        }

        private static Dictionary<string, List<MstModelEntry>> mstDb = null;
        // chip → (brand → models) — platform tab တစ်ခုချင်းအတွက် ကြို filter ထား
        private static Dictionary<string, Dictionary<string, List<MstModelEntry>>> mstViews =
            new Dictionary<string, Dictionary<string, List<MstModelEntry>>>(StringComparer.OrdinalIgnoreCase);
        private static string mstSource = "";

        private void EnsureMstDbLoaded()
        {
            if (mstDb != null) return;
            mstDb = new Dictionary<string, List<MstModelEntry>>();
            try
            {
                string path = Path.Combine(Application.StartupPath, "pmk_models.json");
                if (!File.Exists(path))
                {
                    Log("[!] pmk_models.json missing - model list disabled.", Color.Orange);
                    return;
                }
                using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path)))
                {
                    if (doc.RootElement.TryGetProperty("source", out JsonElement src))
                        mstSource = src.GetString() ?? "";
                    if (!doc.RootElement.TryGetProperty("brands", out JsonElement brands)) return;
                    foreach (JsonProperty brand in brands.EnumerateObject())
                    {
                        // "#Auto Detect" placeholder rows ပဲ ဖျက် — "#Generic_Hisilicon" /
                        // "#Generic_Spd" / "#PRELOADER AUTH BYPASS" တွေက တကယ့် data ဖြစ်လို့ ထား
                        if (brand.Name == "#Auto Detect") continue;
                        List<MstModelEntry> list = new List<MstModelEntry>();
                        foreach (JsonElement item in brand.Value.EnumerateArray())
                        {
                            MstModelEntry e = new MstModelEntry();
                            JsonElement v;
                            if (item.TryGetProperty("model", out v)) e.Model = v.GetString() ?? "";
                            if (item.TryGetProperty("chip", out v)) e.Chip = v.GetString() ?? "";
                            if (item.TryGetProperty("ops", out v))
                                foreach (JsonElement op in v.EnumerateArray())
                                    e.Ops.Add(op.GetString() ?? "");
                            if (e.Model.Length > 0) list.Add(e);
                        }
                        if (list.Count > 0) mstDb[brand.Name] = list;
                    }
                }
            }
            catch (Exception ex)
            {
                Log("[!] Cannot read pmk_models.json: " + ex.Message, Color.Orange);
            }
        }

        // chip = null → အကုန် (ADB tab); "mediatek"/"qualcomm"/... → အဲ့ chip ပဲ
        private Dictionary<string, List<MstModelEntry>> MstView(string chip)
        {
            EnsureMstDbLoaded();
            if (string.IsNullOrEmpty(chip)) return mstDb;
            if (mstViews.TryGetValue(chip, out Dictionary<string, List<MstModelEntry>> hit)) return hit;

            Dictionary<string, List<MstModelEntry>> view = new Dictionary<string, List<MstModelEntry>>();
            foreach (KeyValuePair<string, List<MstModelEntry>> kv in mstDb)
            {
                List<MstModelEntry> list = kv.Value
                    .Where(e => string.Equals(e.Chip, chip, StringComparison.OrdinalIgnoreCase)).ToList();
                if (list.Count > 0) view[kv.Key] = list;
            }
            mstViews[chip] = view;
            return view;
        }

        private async Task CheckAllDevicesAsync(bool showLog)
        {
            if (isDetecting) return;
            isDetecting = true;

            try
            {
                RefillPortCombo();
                string[] ports = SerialPort.GetPortNames();

                string adbRes = await ExecuteCommandQuickAsync("adb.exe", "devices");
                string fbRes = await ExecuteCommandQuickAsync("fastboot.exe", "devices");

                string currentState = "";

                if (adbRes.Contains("\tdevice"))
                {
                    string model = await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.product.model");
                    currentState = "ADB:" + model.Trim();
                    lblDeviceModeStatus.Text = "🟢 ADB: " + model.Trim();
                    lblDeviceModeStatus.ForeColor = Color.LightGreen;
                    WarnUnknownModel(model.Trim());
                    await RefreshBatteryHeaderAsync(true);

                    // profile auto-detect log ကို device အသစ် ချိတ်တဲ့အခါပဲ လုပ်တယ် —
                    // ၆ စက္ကန့် timer တစ်ခါတိုင်း ထပ်ခါတလဲလဲ log မဖြစ်အောင် (log မရှုပ်စေ)။
                    if (currentState != lastDeviceState)
                    {
                        if (showLog) Log("[Auto-Detect] ADB Connected -> " + model.Trim(), Color.LightGreen);
                    }
                }
                else if (adbRes.Contains("\tunauthorized"))
                {
                    currentState = "ADB:Unauthorized";
                    lblDeviceModeStatus.Text = "🟡 ADB: Unauthorized";
                    lblDeviceModeStatus.ForeColor = Color.Yellow;
                    await RefreshBatteryHeaderAsync(false);
                    if (currentState != lastDeviceState && showLog) Log("[Auto-Detect] Device Unauthorized (Allow USB Debugging)", Color.Yellow);
                }
                else if (!string.IsNullOrWhiteSpace(fbRes))
                {
                    string fbSerial = fbRes.Split('\t')[0].Trim();
                    currentState = "FASTBOOT:" + fbSerial;
                    lblDeviceModeStatus.Text = "🔵 Fastboot: " + fbSerial;
                    lblDeviceModeStatus.ForeColor = Color.DeepSkyBlue;
                    await RefreshBatteryHeaderAsync(false);

                    if (currentState != lastDeviceState && showLog)
                    {
                        Log("[Auto-Detect] Fastboot Connected -> " + fbSerial, Color.DeepSkyBlue);
                    }
                }
                else if (!string.IsNullOrEmpty(DetectMtkUsbMode()))
                {
                    // MediaTek ဖုန်းက USB ပေါ်မှာ ရှိနေတယ် — ဘယ် mode လဲ ပြထားတယ်။
                    // BROM (0e8d:0003) သာ mtkclient handshake အတွက် မှန်တာ — preloader/DA
                    // mode နဲ့ run ရင် libusb crash (0xC0000005) ဖြစ်တတ်တယ်။
                    string mtkMode = DetectMtkUsbMode();
                    bool isBrom = mtkMode.Contains("BROM");
                    currentState = "MTK:" + mtkMode;
                    lblDeviceModeStatus.Text = (isBrom ? "🔶 MTK: " : "⚠ MTK (BROM မဟုတ်): ") + mtkMode;
                    lblDeviceModeStatus.ForeColor = isBrom ? Color.FromArgb(255, 170, 0) : Color.OrangeRed;
                    await RefreshBatteryHeaderAsync(false);

                    if (currentState != lastDeviceState && showLog)
                    {
                        Log("[Auto-Detect] MediaTek USB mode -> " + mtkMode +
                            (isBrom ? "" : " - NOT in BROM mode! Power off (~10s), then hold Vol+ & Vol- while reconnecting USB."),
                            isBrom ? Color.FromArgb(255, 170, 0) : Color.OrangeRed);
                    }
                }
                else if (ports.Length > 0)
                {
                    currentState = "COM:" + string.Join(",", ports);
                    lblDeviceModeStatus.Text = "🟠 COM Port: " + ports[0];
                    lblDeviceModeStatus.ForeColor = Color.Orange;
                    await RefreshBatteryHeaderAsync(false);

                    if (currentState != lastDeviceState && showLog)
                    {
                        Log("[Auto-Detect] COM Port -> " + string.Join(", ", ports), Color.Orange);
                    }
                }
                else
                {
                    // ADB/fastboot/MTK/COM မရှိ — Samsung MTP (File Transfer) ချိတ်ထားရင် ပြ
                    var samMtp = (await FindSamsungUsbDevicesAsync())
                        .FirstOrDefault(d => d.IsMtpLike || d.Role == "MTP" || d.Role == "Connectivity");
                    if (samMtp != null)
                    {
                        string mtpName = !string.IsNullOrWhiteSpace(samMtp.FriendlyName)
                            ? samMtp.FriendlyName
                            : ("04E8:" + (samMtp.Pid.Length > 0 ? samMtp.Pid : "MTP"));
                        currentState = "MTP:" + mtpName;
                        lblDeviceModeStatus.Text = "📱 MTP: " + mtpName;
                        lblDeviceModeStatus.ForeColor = Color.MediumSeaGreen;
                        await RefreshBatteryHeaderAsync(false);

                        if (currentState != lastDeviceState && showLog)
                        {
                            Log("[Auto-Detect] Samsung MTP (no ADB) -> " + mtpName +
                                " — Samsung tab → MTP group သုံးပါ (Info / FRP).", Color.MediumSeaGreen);
                        }
                    }
                    else
                    {
                        currentState = "DISCONNECTED";
                        lblDeviceModeStatus.Text = "⚪ Disconnected";
                        lblDeviceModeStatus.ForeColor = lightTheme ? Color.FromArgb(45, 50, 62) : Color.LightGray;
                        if (lblBattery != null)
                        {
                            lblBattery.Text = "🔋 —";
                            lblBattery.ForeColor = Color.FromArgb(180, 195, 215);
                        }

                        if (currentState != lastDeviceState && showLog && !rebootWaitActive)
                        {
                            Log("[Auto-Detect] Device Disconnected.", Color.Gray);
                        }
                    }
                }

                lastDeviceState = currentState;
            }
            catch (Exception ex)
            {
                if (showLog) Log("Detection Error: " + ex.Message, Color.Red);
            }
            finally
            {
                isDetecting = false;
            }
        }

        // su probe — KSU/Magisk/Temp-root ပထမဆုံးခေါ်မှာ daemon start + permission prompt ကြောင့်
        // 15s ထက်ကြာနိုင်လို့ 20s + တစ်ကြိမ် retry (မဟုတ်ရင် false "su not available" ဖြစ်တယ်)
        private async Task<string> ProbeSuAsync(int timeoutMs = 20000, int retries = 1)
        {
            for (int i = 0; ; i++)
            {
                string id = (await ExecuteCommandQuickAsync("adb.exe", "shell su -c id", timeoutMs)).Trim();
                if (!string.IsNullOrWhiteSpace(id) || i >= retries) return id;
                await Task.Delay(700);
            }
        }

        private async Task<string> ExecuteCommandQuickAsync(string fileName, string arguments, int timeoutMs = 15000)
        {
            try { arguments = await BindDeviceArgumentsAsync(fileName, arguments); }
            catch (Exception ex)
            {
                if (flashWorkflowContext.Value) workflowFailed = true;
                if (rebootWaitActive) return "";
                Log("[!] " + ex.Message, Color.Orange);
                return "";
            }
            // Normal ROM dump လို QuickAsync သုံးတဲ့ op တွေအတွက်လည်း auto-reboot detect လုပ်
            if (flashWorkflowContext.Value) workflowDidOp = true;
            string result = await ReviewSafety.RunQuickAsync(ResolveToolPath(fileName), arguments, timeoutMs);
            if (flashWorkflowContext.Value && string.IsNullOrWhiteSpace(result)) workflowFailed = true;
            return result;
        }

        // --- ADB Handlers ---
        private async void BtnAdbInfo_Click(object sender, EventArgs e)
        {
            Log("Reading Device Details...", Color.Cyan);
            string devices = await ExecuteCommandQuickAsync("adb.exe", "devices");

            if (devices.Contains("\tdevice"))
            {
                await LoadDevicePropsAsync();
                string brand = Prop(PropBrandKeys);
                string model = Prop(PropModelKeys);
                string marketName = Prop("ro.product.marketname", "ro.product.odm.marketname", "ro.product.vendor.marketname",
                                         "ro.miui.build.marketname", "ro.product.bootimage.marketname");
                string codename = Prop(PropDeviceKeys);
                string android = Prop("ro.build.version.release", "ro.product.build.version.release");
                string sdk = Prop("ro.build.version.sdk", "ro.product.build.version.sdk");
                string patch = Prop("ro.build.version.security_patch", "ro.product.build.version.security_patch");
                string serial = await ExecuteCommandQuickAsync("adb.exe", "get-serialno");
                string cpuAbi = Prop("ro.product.cpu.abi");
                string soc = Prop(PropSocKeys);
                string board = Prop("ro.board.platform", "ro.hardware");
                string buildId = Prop("ro.build.id", "ro.product.build.id");
                string incremental = Prop("ro.build.version.incremental", "ro.product.build.version.incremental");
                string buildType = Prop("ro.build.type") + (string.IsNullOrWhiteSpace(Prop("ro.build.tags")) ? "" : " (" + Prop("ro.build.tags") + ")");
                string miuiVer = Prop("ro.miui.ui.version.name", "ro.hyperos.ui.version.name", "ro.miui.ui.version.code");
                string region = Prop("ro.miui.region", "ro.miui.build.region", "ro.csc.country_code", "ro.csc.sales_code");
                string locale = Prop("persist.sys.locale", "ro.product.locale");
                string slotSuffix = Prop("ro.boot.slot_suffix", "ro.product.ab_ota_partitions");
                string flashLocked = Prop("ro.boot.flash.locked");
                string vbState = Prop("ro.boot.verifiedbootstate");
                string oemAllowed = Prop("sys.oem_unlock_allowed");
                string oemSupported = Prop("ro.oem_unlock_supported");
                string verity = Prop("ro.boot.veritymode", "ro.boot.veritymode.managed");
                string selinux = Prop("ro.boot.selinux");
                string secureVal = Prop("ro.secure");
                string debuggable = Prop("ro.debuggable");
                string deviceName = (await ExecuteCommandQuickAsync("adb.exe", "shell settings get global device_name")).Trim();

                string displaySize = (await ExecuteCommandQuickAsync("adb.exe", "shell wm size")).Replace("Physical size:", "").Trim();
                string density = (await ExecuteCommandQuickAsync("adb.exe", "shell wm density")).Replace("Physical density:", "").Trim();
                string dfOut = await ExecuteCommandQuickAsync("adb.exe", "shell df -h /data");
                string memOut = await ExecuteCommandQuickAsync("adb.exe", "shell cat /proc/meminfo");
                string suOut = (await ExecuteCommandQuickAsync("adb.exe", "shell which su")).Trim();
                if (string.IsNullOrWhiteSpace(suOut))
                {
                    string tmpSu = (await ExecuteCommandQuickAsync("adb.exe", "shell ls /data/local/tmp/su")).Trim();
                    if (tmpSu.Contains("/data/local/tmp/su")) suOut = "/data/local/tmp/su";
                }
                string enforce = (await ExecuteCommandQuickAsync("adb.exe", "shell getenforce")).Trim();

                // RAM (MemTotal kB → GB)
                string ram = "";
                Match mm = Regex.Match(memOut, @"MemTotal:\s*(\d+)\s*kB");
                if (mm.Success)
                {
                    double gb = double.Parse(mm.Groups[1].Value) / 1048576.0;
                    ram = gb.ToString("F1") + " GB";
                }

                // /data storage (df ရဲ့ ဒုတိယ လိုင်း: total used avail use%)
                string storage = "";
                foreach (string ln in dfOut.Split('\n'))
                {
                    Match dm = Regex.Match(ln, @"^(\S+)\s+(\S+)\s+(\S+)\s+(\S+)\s+(\d+)%");
                    if (dm.Success) { storage = dm.Groups[2].Value + " total, " + dm.Groups[4].Value + " free (" + dm.Groups[5].Value + "% used)"; break; }
                }

                string bootState = "";
                if (!string.IsNullOrWhiteSpace(flashLocked))
                    bootState += "flash.locked=" + flashLocked +
                                 (flashLocked == "0" ? " (bootloader UNLOCKED)" : " (bootloader locked)");
                if (!string.IsNullOrWhiteSpace(vbState)) bootState += (bootState.Length > 0 ? ", " : "") + "verifiedboot=" + vbState;
                if (!string.IsNullOrWhiteSpace(oemAllowed)) bootState += (bootState.Length > 0 ? ", " : "") + "oem_unlock_allowed=" + oemAllowed;
                if (!string.IsNullOrWhiteSpace(oemSupported)) bootState += (bootState.Length > 0 ? ", " : "") + "oem_unlock_supported=" + oemSupported;
                if (!string.IsNullOrWhiteSpace(verity)) bootState += (bootState.Length > 0 ? ", " : "") + "verity=" + verity;

                string modelDisplay = !string.IsNullOrWhiteSpace(marketName) ? model + " (" + marketName + ")" : model;

                Log("======================================", Color.FromArgb(0, 180, 255));
                Log("Brand            : " + brand, Color.White);
                Log("Model            : " + modelDisplay, Color.White);
                if (!string.IsNullOrWhiteSpace(deviceName)) Log("Device Name      : " + deviceName, Color.White);
                Log("Codename / Board : " + codename + " / " + board, Color.White);
                Log("Android Ver      : Android " + android + (string.IsNullOrWhiteSpace(sdk) ? "" : " (SDK " + sdk + ")") +
                    (string.IsNullOrWhiteSpace(patch) ? "" : ", patch " + patch), Color.White);
                if (!string.IsNullOrWhiteSpace(miuiVer) || !string.IsNullOrWhiteSpace(incremental))
                    Log("MIUI / Build     : " + (string.IsNullOrWhiteSpace(miuiVer) ? "-" : miuiVer) + "  " + incremental, Color.White);
                Log("Build ID / Type  : " + buildId + " (" + buildType + ")", Color.White);
                Log("SoC / CPU Arch   : " + soc + " / " + cpuAbi, Color.White);
                Log("Region / Locale  : " + (string.IsNullOrWhiteSpace(region) ? "-" : region) + " / " + (string.IsNullOrWhiteSpace(locale) ? "-" : locale), Color.White);
                Log("RAM              : " + (string.IsNullOrWhiteSpace(ram) ? "-" : ram), Color.White);
                Log("Storage /data    : " + (string.IsNullOrWhiteSpace(storage) ? "-" : storage), Color.White);
                Log("Screen / Density : " + displaySize + (string.IsNullOrWhiteSpace(density) ? "" : " @ " + density + "dpi"), Color.White);
                Log("Serial Number    : " + serial, Color.White);
                if (!string.IsNullOrWhiteSpace(slotSuffix)) Log("A/B / Slots      : " + slotSuffix, Color.White);
                if (bootState.Length > 0) Log("Bootloader State : " + bootState, Color.White);
                Log("SELinux          : " + (string.IsNullOrWhiteSpace(enforce) ? (string.IsNullOrWhiteSpace(selinux) ? "?" : selinux) : enforce) +
                    "  (secure=" + (string.IsNullOrWhiteSpace(secureVal) ? "?" : secureVal) + ", debuggable=" + (string.IsNullOrWhiteSpace(debuggable) ? "?" : debuggable) + ")",
                    Color.White);
                Log("Root             : " + (string.IsNullOrWhiteSpace(suOut) ? "not detected (no su)" : "su found: " + suOut), Color.White);
                Log("======================================", Color.FromArgb(0, 180, 255));
            }
            else
            {
                Log("Device not found or unauthorized!", Color.Red);
            }
        }

        private async void BtnAdbBatteryInfo_Click(object sender, EventArgs e)
        {
            await ExecuteCommandCleanAsync("adb.exe", "shell dumpsys battery", "Battery Info", false, true);
        }

        // Header battery badge — ADB ရှိရင် level % ကို poll နဲ့ ပြ
        private async Task RefreshBatteryHeaderAsync(bool connected)
        {
            if (lblBattery == null) return;
            if (!connected)
            {
                lblBattery.Text = "🔋 —";
                lblBattery.ForeColor = Color.FromArgb(180, 195, 215);
                return;
            }
            try
            {
                string dump = await ExecuteCommandQuickAsync("adb.exe", "shell dumpsys battery");
                Match m = Regex.Match(dump ?? "", @"level:\s*(\d+)");
                if (!m.Success)
                {
                    lblBattery.Text = "🔋 —";
                    lblBattery.ForeColor = Color.FromArgb(180, 195, 215);
                    return;
                }
                int level = int.Parse(m.Groups[1].Value);
                lblBattery.Text = "🔋 " + level + "%";
                if (level <= 15) lblBattery.ForeColor = Color.OrangeRed;
                else if (level <= 30) lblBattery.ForeColor = Color.Orange;
                else lblBattery.ForeColor = Color.LightGreen;
            }
            catch
            {
                lblBattery.Text = "🔋 —";
            }
        }

        // Pattern / PIN / password ဖျက် — root ရှိရင် locksettings + gatekeeper files ဖျက်။
        // root မရှိရင် dismiss-keyguard / locksettings စမ်းပြီး မရရင် Factory Reset / MTK Userlock လမ်း ညွှန်။
        private async void BtnAdbRemoveLock_Click(object sender, EventArgs e)
        {
            string dv = await ExecuteCommandQuickAsync("adb.exe", "devices");
            if (string.IsNullOrWhiteSpace(dv) || !dv.Contains("\tdevice"))
            {
                MessageBox.Show("ADB device not found." + Environment.NewLine + Environment.NewLine
                                + "- USB debugging ဖွင့်ပြီး cable ချိတ်ပါ" + Environment.NewLine
                                + "- RSA prompt ကို Allow ပါ",
                    "Remove Pattern/PIN", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (MessageBox.Show(
                    "Remove screen lock (pattern / PIN / password)?" + Environment.NewLine + Environment.NewLine +
                    "• ROOT ရှိရင် — locksettings + gatekeeper files ဖျက် (data မပျက်)" + Environment.NewLine +
                    "• ROOT မရှိရင် — best-effort ADB commands စမ်းမယ်" + Environment.NewLine +
                    "• မရရင် — Factory Reset (ADB) / MTK Userlock Reset သုံးပါ" + Environment.NewLine + Environment.NewLine +
                    "ဖုန်းပိုင်ရှင် ခွင့်ပြုချက် ရှိမှ လုပ်ပါ",
                    "Confirm Remove Lock", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("   REMOVE PATTERN / PIN / PASSWORD", Color.White);
            Log("========================================================", Color.FromArgb(0, 180, 255));

            string id = (await ProbeSuAsync()).Trim();
            bool hasRoot = id.Contains("uid=0");

            if (hasRoot)
            {
                Log("[*] Root detected — clearing lock settings files...", Color.Cyan);
                string[] rmCmds =
                {
                    "rm -f /data/system/locksettings.db /data/system/locksettings.db-wal /data/system/locksettings.db-shm",
                    "rm -f /data/system/users/0/locksettings.db /data/system/users/0/locksettings.db-wal /data/system/users/0/locksettings.db-shm",
                    "rm -f /data/system/gatekeeper.password.key /data/system/gatekeeper.pattern.key",
                    "rm -f /data/system/password.key /data/system/gesture.key /data/system/locksettings.dat",
                    "rm -rf /data/system/locksettings*"
                };
                int ok = 0;
                foreach (string c in rmCmds)
                {
                    if (await ExecuteCommandCleanAsync("adb.exe", "shell su -c \"" + c + "\"", "Remove lock file", false, true))
                        ok++;
                }
                await ExecuteCommandQuickAsync("adb.exe", "shell su -c \"setprop persist.sys.usb.config mtp,adb\"");
                Log("[OK] Root lock clear: " + ok + " step(s). Reboot the phone and check the screen lock.", Color.LightGreen);
                if (MessageBox.Show("Reboot now to apply?", "Reboot", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    await ExecuteCommandCleanAsync("adb.exe", "reboot", "Reboot after lock clear");
                return;
            }

            Log("[i] No root — trying non-root lock commands (may fail on modern Android)...", Color.Orange);
            // Android 8-10 best-effort (usually needs root on newer builds)
            await ExecuteCommandQuickAsync("adb.exe", "shell locksettings clear --old \"\"");
            await ExecuteCommandQuickAsync("adb.exe", "shell wm dismiss-keyguard");
            await ExecuteCommandQuickAsync("adb.exe", "shell input keyevent 82");

            string check = await ExecuteCommandQuickAsync("adb.exe", "shell locksettings get-disabled");
            Log("[i] locksettings get-disabled: " + (string.IsNullOrWhiteSpace(check) ? "(empty)" : check.Trim()), Color.Gray);
            Log("[!] Non-root remove is not reliable on Android 11+.", Color.Orange);
            Log("[i] Fallback: 🗑 Factory Reset (ADB) သို့ MTK tab → 🔑 Userlock Reset သုံးပါ (data ပျက်မယ်).", Color.Orange);
        }

        // ROOT partition list + RW remount — root ရှိရင် by-name list ပြ၊ ရွေးတဲ့ partition ကို mount -o remount,rw
        private async void BtnAdbPartitions_Click(object sender, EventArgs e)
        {
            string dv = await ExecuteCommandQuickAsync("adb.exe", "devices");
            if (string.IsNullOrWhiteSpace(dv) || !dv.Contains("\tdevice"))
            {
                MessageBox.Show("ADB device not found." + Environment.NewLine + Environment.NewLine
                                + "- USB debugging ဖွင့်ပြီး cable ချိတ်ပါ" + Environment.NewLine
                                + "- RSA prompt ကို Allow ပါ",
                    "Partitions (ROOT)", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string id = (await ProbeSuAsync()).Trim();
            if (!id.Contains("uid=0"))
            {
                MessageBox.Show("Root (su) မရပါ။" + Environment.NewLine + Environment.NewLine
                                + "• Xiaomi Temp Root / Magisk / KernelSU ရအောင်လုပ်ပါ" + Environment.NewLine
                                + "• ဖုန်း screen က su prompt Allow မလုပ်ရသေးရင် လုပ်ပါ",
                    "Partitions (ROOT)", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Log("[!] Partitions: su not available (" + (string.IsNullOrWhiteSpace(id) ? "no output" : id) + ")", Color.OrangeRed);
                return;
            }

            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("   ROOT PARTITIONS (by-name)", Color.White);
            Log("========================================================", Color.FromArgb(0, 180, 255));

            // One push + one run — names/sizes/mounts/dm (was: N+ adb calls per dm-* → slow)
            // size ယူရာမှာ blockdev/basename/readlink ကို partition တိုင်းမှာ fork လုပ် → 18s+
            // ကြာခဲ့တယ်; ls -l + shell builtin read/sysfs နဲ့ ဖြစ်လို့ fork မလုပ်တော့ဘူး (<1s)
            string sizeSh =
                "echo '##NAMES'\n" +
                "ls -1 /dev/block/by-name 2>/dev/null\n" +
                "echo '##SIZES'\n" +
                "ls -l /dev/block/by-name 2>/dev/null | while read -r line; do\n" +
                "  case \"$line\" in *' -> '*) ;; *) continue ;; esac\n" +
                "  lhs=${line% -> *}\n" +
                "  name=${lhs##* }\n" +   // ls -l <dir> = path မပါ → နောက်ဆုံး token က partition name
                "  rhs=${line#* -> }\n" +
                "  tgt=${rhs##*/}\n" +
                "  sz=0\n" +
                "  if [ -r \"/sys/class/block/$tgt/size\" ]; then\n" +
                "    read sec < \"/sys/class/block/$tgt/size\"\n" +
                "    sz=$((sec * 512))\n" +
                "  fi\n" +
                "  if [ \"$sz\" -eq 0 ]; then\n" +
                "    b=$(blockdev --getsize64 \"/dev/block/by-name/$name\" 2>/dev/null)\n" +
                "    case \"$b\" in ''|*[!0-9]*) sz=0 ;; *) sz=$b ;; esac\n" +
                "  fi\n" +
                "  printf '%s|%s\\n' \"$name\" \"$sz\"\n" +
                "done\n" +
                "echo '##MOUNTS'\n" +
                "cat /proc/mounts\n" +
                "echo '##DM'\n" +
                "for b in /sys/block/dm-*; do\n" +
                "  [ -d \"$b\" ] || continue\n" +
                "  dm=${b##*/}\n" +
                "  nm=''\n" +
                "  [ -r \"$b/dm/name\" ] && read nm < \"$b/dm/name\"\n" +
                "  sec=0\n" +
                "  [ -r \"$b/size\" ] && read sec < \"$b/size\"\n" +
                "  sz=$((sec * 512))\n" +
                "  printf '%s|%s|%s\\n' \"$dm\" \"$nm\" \"$sz\"\n" +
                "done\n";
            string tmpSh = Path.Combine(Path.GetTempPath(), "pmk_sizes.sh");
            File.WriteAllText(tmpSh, sizeSh.Replace("\r\n", "\n"));
            await ExecuteCommandQuickAsync("adb.exe", "push \"" + tmpSh + "\" /data/local/tmp/pmk_sizes.sh");
            // script = ~16s (blockdev 150+ partition) — default 15s timeout ထိ → blob ကွက်
            string blob = (await ExecuteCommandQuickAsync("adb.exe",
                "shell su -c \"sh /data/local/tmp/pmk_sizes.sh\"", 60000)).Trim();

            var namesList = new List<string>();
            var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var mountByDev = new Dictionary<string, (string Mp, bool Ro)>(StringComparer.OrdinalIgnoreCase);
            var mountByName = new Dictionary<string, (string Mp, bool Ro)>(StringComparer.OrdinalIgnoreCase);
            var mountEntries = new List<(string Dev, string Mp, bool Ro)>();
            var dmByName = new Dictionary<string, (string Dev, string Name, long Sz)>(StringComparer.OrdinalIgnoreCase);
            var dmByDevBn = new Dictionary<string, (string Name, long Sz)>(StringComparer.OrdinalIgnoreCase);

            string section = "";
            foreach (string rawLine in blob.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.TrimEnd('\r');
                if (line.StartsWith("##", StringComparison.Ordinal))
                {
                    section = line.TrimStart('#');
                    continue;
                }
                if (section == "NAMES")
                {
                    if (!string.IsNullOrWhiteSpace(line) && !line.StartsWith("total ", StringComparison.OrdinalIgnoreCase))
                        namesList.Add(line.Trim());
                }
                else if (section == "SIZES")
                {
                    int bar = line.LastIndexOf('|');
                    if (bar <= 0) continue;
                    string n = line.Substring(0, bar).Trim();
                    long.TryParse(line.Substring(bar + 1).Trim(), out long b);
                    if (!string.IsNullOrEmpty(n)) sizes[n] = b;
                }
                else if (section == "MOUNTS")
                {
                    string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 4) continue;
                    string dev = parts[0];
                    string mp = parts[1];
                    string opts = parts[3];
                    bool ro = opts.Split(',').Any(o => o.Equals("ro", StringComparison.OrdinalIgnoreCase));
                    mountByDev[dev] = (mp, ro);
                    mountEntries.Add((dev, mp, ro));
                    string bn = Path.GetFileName(dev.TrimEnd('/'));
                    if (!string.IsNullOrEmpty(bn)) mountByName[bn] = (mp, ro);
                    if (mp != "/")
                    {
                        string mpn = Path.GetFileName(mp.TrimEnd('/'));
                        if (!string.IsNullOrEmpty(mpn) && !mountByName.ContainsKey(mpn))
                            mountByName[mpn] = (mp, ro);
                    }
                }
                else if (section == "DM")
                {
                    // dm-16|system-verity|bytes
                    string[] pp = line.Split('|');
                    if (pp.Length < 3) continue;
                    string dmBn = pp[0].Trim();
                    string dmName = pp[1].Trim();
                    long.TryParse(pp[2].Trim(), out long dmSz);
                    if (string.IsNullOrEmpty(dmName)) continue;
                    string key = dmName;
                    if (key.EndsWith("-verity", StringComparison.OrdinalIgnoreCase))
                        key = key.Substring(0, key.Length - "-verity".Length);
                    if (!string.IsNullOrEmpty(key))
                    {
                        dmByDevBn[dmBn] = (key, dmSz);
                        if (!dmByName.ContainsKey(key))
                            dmByName[key] = (dmBn, dmName, dmSz);
                        if (!mountByName.ContainsKey(key) && mountByDev.TryGetValue("/dev/block/" + dmBn, out var mOnDm))
                            mountByName[key] = mOnDm;
                        // mp basename already added; ensure key maps mount
                        foreach (var (dev, mp, ro) in mountEntries)
                        {
                            if (Path.GetFileName(dev.TrimEnd('/')).Equals(dmBn, StringComparison.OrdinalIgnoreCase))
                            {
                                mountByName[key] = (mp, ro);
                                break;
                            }
                        }
                    }
                }
            }

            string[] names = namesList.ToArray();
            if (names.Length == 0)
            {
                Log("[i] script blob bytes=" + blob.Length +
                    (blob.Length > 0 ? "  head=" + blob.Substring(0, Math.Min(140, blob.Length)).Replace("\n", " ") : "  (empty — timeout/exit≠0/adb error)"), Color.Gray);
                Log("[!] /dev/block/by-name not found — device may not expose by-name symlinks.", Color.Orange);
                MessageBox.Show("/dev/block/by-name မရှိပါ (by-name symlink မပေးတဲ့ device)။",
                    "Partitions (ROOT)", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var byNameSet = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            var extraMounts = new List<(string Name, string Dev, string Mp, bool Ro, long Sz)>();
            foreach (var (dev, mp, ro) in mountEntries)
            {
                if (mp == "/" || mp.StartsWith("/dev", StringComparison.Ordinal) ||
                    mp.StartsWith("/sys", StringComparison.Ordinal) || mp.StartsWith("/proc", StringComparison.Ordinal) ||
                    mp.StartsWith("/apex", StringComparison.Ordinal) || mp.StartsWith("/mnt/installer", StringComparison.Ordinal) ||
                    mp.StartsWith("/linkerconfig", StringComparison.Ordinal) ||
                    mp.StartsWith("/system_dlkm", StringComparison.Ordinal))
                    continue;
                string bn = Path.GetFileName(dev.TrimEnd('/'));
                string mName = "";
                long bytes = 0;
                if (dev.StartsWith("/dev/block/by-name/", StringComparison.Ordinal))
                {
                    mName = Path.GetFileName(dev);
                    if (byNameSet.Contains(mName)) continue;
                }
                else if (bn.StartsWith("dm-", StringComparison.OrdinalIgnoreCase))
                {
                    if (dmByDevBn.TryGetValue(bn, out var dmInfo)) mName = dmInfo.Name;
                    if (string.IsNullOrEmpty(mName)) mName = Path.GetFileName(mp.TrimEnd('/'));
                    if (byNameSet.Contains(mName)) continue;
                    bytes = dmByDevBn.TryGetValue(bn, out var dmSz2) ? dmSz2.Sz : 0;
                }
                else
                {
                    continue;
                }
                if (string.IsNullOrEmpty(mName)) continue;
                if (extraMounts.Any(x => x.Name.Equals(mName, StringComparison.OrdinalIgnoreCase))) continue;
                extraMounts.Add((mName, dev, mp, ro, bytes));
                if (bytes > 0 && !sizes.ContainsKey(mName)) sizes[mName] = bytes;
            }

            Log("[i] " + names.Length + " partition(s) in /dev/block/by-name", Color.Cyan);
            if (extraMounts.Count > 0)
                Log("[i] +" + extraMounts.Count + " mounted fs not in by-name (dm/super)", Color.Cyan);

            // Fill MAIN partition grid (not a dialog) — right-click → Mount RW via ctxPartitionMenu
            if (tabControl.SelectedTab != tabAdb) tabControl.SelectedTab = tabAdb;
            ClearPartitionData();
            foreach (string name in names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                long bytes = sizes.TryGetValue(name, out long b) ? b : 0;
                string devPath = "/dev/block/by-name/" + name;
                string mp = "";
                string mode = "—";
                if (mountByName.TryGetValue(name, out var mnt))
                {
                    mp = mnt.Mp;
                    mode = mnt.Ro ? "ro" : "rw";
                }
                else if (mountByDev.TryGetValue(devPath, out var mnt2))
                {
                    mp = mnt2.Mp;
                    mode = mnt2.Ro ? "ro" : "rw";
                }
                dgvPartitions.Rows.Add(false, name, FormatBytesLong(bytes), devPath, mp, mode);
            }
            foreach (var ex in extraMounts.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                long bytes = sizes.TryGetValue(ex.Name, out long b) ? b : ex.Sz;
                dgvPartitions.Rows.Add(false, ex.Name, FormatBytesLong(bytes), ex.Dev, ex.Mp, ex.Ro ? "ro" : "rw");
            }
            SyncPartitionPanel(false);   // ADB ROOT grid ပြည့်ပြီ — partition row အလိုအလျောက် ချိန်
            Log("[OK] " + dgvPartitions.RowCount + " partition(s) in grid — right-click → Mount RW", Color.LightGreen);
            Log("[i] Mounted rows (system/vendor…) ကို right-click → 🔓 Mount RW", Color.Gray);
        }

        // Factory Reset — ADB ကန် recovery wipe / MASTER_CLEAR ကြိုးစား။ မရရင် recovery ပို့ပြီး လက်နှိပ်ရန် ညွှန်။
        private async void BtnAdbFactoryReset_Click(object sender, EventArgs e)
        {
            string dv = await ExecuteCommandQuickAsync("adb.exe", "devices");
            if (string.IsNullOrWhiteSpace(dv) || !dv.Contains("\tdevice"))
            {
                MessageBox.Show("ADB device not found." + Environment.NewLine + Environment.NewLine
                                + "- USB debugging ဖွင့်ပြီး cable ချိတ်ပါ" + Environment.NewLine
                                + "- RSA prompt ကို Allow ပါ",
                    "Factory Reset", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (MessageBox.Show(
                    "Factory Reset via ADB?" + Environment.NewLine + Environment.NewLine +
                    "• ဖုန်းထဲက data / ဓာတ်ပုံ / အက်ပ် အားလုံး ဖျက်ခံရမယ်" + Environment.NewLine +
                    "• Pattern/PIN နဲ့ Google account (FRP) ပါ ပျက်သွားနိုင်တယ်" + Environment.NewLine +
                    "• ဖုန်းပိုင်ရှင် ခွင့်ပြုချက် ရှိမှ လုပ်ပါ" + Environment.NewLine + Environment.NewLine +
                    "ဆက်လုပ်မလား?",
                    "Confirm Factory Reset", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("   FACTORY RESET (ADB)", Color.White);
            Log("========================================================", Color.FromArgb(0, 180, 255));

            await RunAdbFactoryResetCoreAsync();
        }

        // ADB device ချိတ်ထားပြီးသားလို့ ယူဆပြီး wipe လုပ် — MTP Factory Reset ကနေလည်း ပြန်ခေါ်သည်
        private async Task RunAdbFactoryResetCoreAsync()
        {
            // 1) root → wipe data via recovery command / rm (စမ်း)
            string id = (await ProbeSuAsync()).Trim();
            if (id.Contains("uid=0"))
            {
                Log("[*] Root detected — attempting data wipe...", Color.Cyan);
                // Common root paths used by tech tools (not all devices expose these)
                await ExecuteCommandQuickAsync("adb.exe", "shell su -c \"recovery --wipe_data\"");
                await ExecuteCommandQuickAsync("adb.exe", "shell su -c \"am broadcast -a android.intent.action.MASTER_CLEAR\"");
            }

            // 2) non-root — recovery --wipe_data / masterclear (device-dependent)
            Log("[*] Trying recovery --wipe_data ...", Color.Cyan);
            string wipe = await ExecuteCommandQuickAsync("adb.exe", "shell recovery --wipe_data");
            if (!string.IsNullOrWhiteSpace(wipe) &&
                (wipe.IndexOf("OK", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 wipe.IndexOf("Success", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                Log("[OK] recovery --wipe_data accepted — phone should wipe and reboot.", Color.LightGreen);
                return;
            }

            Log("[*] Trying MASTER_CLEAR broadcast ...", Color.Cyan);
            await ExecuteCommandQuickAsync("adb.exe",
                "shell am broadcast -a android.intent.action.MASTER_CLEAR -f 0x01000000");

            // 3) fallback — reboot recovery + ညွှန်ကြားချက်
            Log("[i] Direct wipe not confirmed — rebooting to recovery for manual wipe...", Color.Orange);
            await ExecuteCommandCleanAsync("adb.exe", "reboot recovery", "Reboot Recovery (manual wipe)");
            MessageBox.Show(
                "Phone ကို recovery mode ပို့ပြီးပါပြီ။" + Environment.NewLine + Environment.NewLine +
                "Recovery ထဲမှာ:" + Environment.NewLine +
                "  1. Wipe data / Factory reset ရွေးပါ" + Environment.NewLine +
                "  2. Cache partition wipe ရွေးပါ (optional)" + Environment.NewLine +
                "  3. Reboot system now" + Environment.NewLine + Environment.NewLine +
                "Menu မပေါ်ရင်: Vol+ / Vol- နဲ့ ရွေး၊ Power နဲ့ အတည်ပြုပါ။",
                "Factory Reset — Recovery", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Log("[i] Manual recovery steps shown in dialog.", Color.Cyan);
        }

        private async void BtnAdbScreenshot_Click(object sender, EventArgs e)
        {
            Log("Capturing Screenshot...", Color.Cyan);
            string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string filePath = Path.Combine(desktopPath, "screenshot_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");

            await ExecuteCommandQuickAsync("adb.exe", "shell screencap -p /sdcard/temp_screen.png");
            await ExecuteCommandQuickAsync("adb.exe", "pull /sdcard/temp_screen.png \"" + filePath + "\"");
            await ExecuteCommandQuickAsync("adb.exe", "shell rm /sdcard/temp_screen.png");

            if (File.Exists(filePath))
            {
                Log("[✔] Screenshot saved: " + filePath, Color.LightGreen);
                Process.Start(new ProcessStartInfo { FileName = filePath, UseShellExecute = true });
            }
        }

        private async void BtnAdbListApps_Click(object sender, EventArgs e)
        {
            await ExecuteCommandCleanAsync("adb.exe", "shell pm list packages -3", "List 3rd-Party Apps", false, true);
        }

        private async void BtnAdbInstallApk_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog ofd = new OpenFileDialog { Filter = "Android App Package (*.apk)|*.apk" })
            {
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    await ExecuteCommandCleanAsync("adb.exe", "install -r \"" + ofd.FileName + "\"", "Install " + Path.GetFileName(ofd.FileName));
                }
            }
        }

        private async void BtnAdbRebootExecute_Click(object sender, EventArgs e)
        {
            switch (cmbAdbReboot.SelectedIndex)
            {
                case 0: Log("Rebooting System..."); await ExecuteCommandQuickAsync("adb.exe", "reboot"); break;
                case 1: Log("Rebooting Recovery..."); await ExecuteCommandQuickAsync("adb.exe", "reboot recovery"); break;
                case 2: Log("Rebooting Bootloader..."); await ExecuteCommandQuickAsync("adb.exe", "reboot bootloader"); break;
                case 3: Log("Rebooting EDL..."); await ExecuteCommandQuickAsync("adb.exe", "reboot edl"); break;
                case 4: Log("Rebooting Safe Mode..."); await ExecuteCommandQuickAsync("adb.exe", "reboot safe_mode"); break;
                case 5: Log("Powering Off..."); await ExecuteCommandQuickAsync("adb.exe", "shell reboot -p"); break;
            }
            Log("Done.");
        }

        // --- Fastboot Handlers ---
        // Fastboot command တွေအတွက် device ရှိ/မရှိ အရင်စစ် — မရှိရင် "Completed" ဆိုပြီး ဘာမှ မပြတာ မဖြစ်အောင်
        // Fastboot command တွေအတွက် device ရှိ/မရှိ အရင်စစ် — မရှိရင် ဘယ်လို ရောက်အောင် လုပ်ရမလဲ ရှင်းရှင်းပြ
        private async Task<bool> FastbootReadyAsync(string action)
        {
            string fb = await ExecuteCommandQuickAsync("fastboot.exe", "devices");
            if (!string.IsNullOrWhiteSpace(fb)) return true;

            string adb = await ExecuteCommandQuickAsync("adb.exe", "devices");
            if (!string.IsNullOrWhiteSpace(adb) && adb.Contains("\tdevice"))
            {
                Log("[!] No fastboot device - the phone is currently in ADB (Android) mode.", Color.Orange);
                Log("[i] To enter fastboot mode: ADB · REBOOT → Reboot Operations = 'Reboot Bootloader' → Execute Reboot, " +
                    "or hold Power + Vol- while connecting USB.", Color.Orange);
            }
            else
            {
                Log("[!] No device found (neither ADB nor fastboot) - connect the phone first.", Color.Orange);
                Log("[i] If the phone IS in fastboot mode but not detected: install the Android USB driver " +
                    "(Device Manager → 'Android Bootloader Interface').", Color.Orange);
            }
            Log("[i] (" + action + ": retry once the phone is in fastboot mode)", Color.Gray);
            return false;
        }

        // fastboot variant တချို့မှာ per-key တန်ဖိုးတွေ မှားလာတတ်တယ် (ဥပမာ current-slot → "yes")။
        // ဒါကြောင့် ကိန်း/bool ဖြစ်ရမယ့် key တွေကို စစ်ပြီး မဖြစ်ရင် ပစ်တယ် (မှားတဲ့ info မပြရအောင်)။
        private static bool PlausibleFastbootValue(string key, string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return false;
            string t = v.Trim();
            switch (key.ToLowerInvariant())
            {
                case "secure":
                case "unlocked":
                    return t == "0" || t == "1" || t.Equals("yes", StringComparison.OrdinalIgnoreCase) || t.Equals("no", StringComparison.OrdinalIgnoreCase);
                case "current-slot":
                    return t == "_a" || t == "_b" || t == "a" || t == "b";
                case "slot-count":
                case "anti":
                case "battery-voltage":
                case "off-mode-charge":
                    return t.All(char.IsDigit);
                case "max-download-size":
                    return t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || t.All(char.IsDigit);
                default:
                    return true;
            }
        }

        private static Dictionary<string, string> ParseFastbootVars(string outp)
        {
            Dictionary<string, string> vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(outp)) return vars;
            foreach (string raw in outp.Split('\n'))
            {
                string ln = raw.Replace("(bootloader)", "").Trim();
                int c = ln.IndexOf(':');
                if (c <= 0) continue;
                string k = ln.Substring(0, c).Trim();
                string v = ln.Substring(c + 1).Trim();
                if (k.Length > 0 && !k.Contains(" ") && !k.Contains("=")) vars[k] = v;
            }
            return vars;
        }

        // fastboot getvar output ထဲက "လိုအပ်တဲ့" variable တွေပဲ ပြတယ် (အားလုံး 100+ လိုင်း မပြ)။
        // 'getvar all' ကို မထောက်ပံ့တဲ့ fastboot variant (ဥပမာ fastbootd) ဆိုရင် key တစ်ခုချင်း မေးတဲ့ လမ်းကို သုံးတယ်။
        // အပြည့်အစုံကို %TEMP%\pmk_fastboot_vars.txt မှာ သိမ်းထားတယ်။
        private async Task ShowFastbootVarsAsync(string args, string title)
        {
            string outp = await ExecuteCommandQuickAsync("fastboot.exe", args);
            Dictionary<string, string> vars = ParseFastbootVars(outp);

            // ပြမယ့် key တွေ (အစဉ်လိုက်)
            string[][] show = new string[][] {
                new[] { "product",        "Product" },
                new[] { "variant",        "Variant" },
                new[] { "device",         "Codename" },
                new[] { "board",          "Board" },
                new[] { "version-bootloader", "Bootloader" },
                new[] { "version-baseband",   "Baseband" },
                new[] { "version-preloader",  "Preloader" },
                new[] { "version",        "BL version" },
                new[] { "secure",         "Secure boot" },
                new[] { "unlocked",       "Bootloader unlocked" },
                new[] { "current-slot",   "Current slot" },
                new[] { "slot-count",     "Slot count" },
                new[] { "anti",           "ARB index (anti)" },
                new[] { "max-download-size", "Max download size" },
                new[] { "battery-voltage", "Battery (mV)" },
                new[] { "off-mode-charge", "Off-mode charge" },
                new[] { "serialno",       "Serial" },
                new[] { "cpuid",          "CPU ID" },
            };

            // 'getvar all' မရရင် (FAILED / unknown command) → key တစ်ခုချင်း ပြန်မေး
            bool allFailed = vars.Count == 0 || outp.IndexOf("FAILED", StringComparison.OrdinalIgnoreCase) >= 0;
            string rawAll = outp;
            int rejected = 0;
            if (allFailed)
            {
                Log("[i] This fastboot does not support 'getvar all' - reading key variables one by one ...", Color.Gray);
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                foreach (string[] row in show)
                {
                    string r = await ExecuteCommandQuickAsync("fastboot.exe", "getvar " + row[0]);
                    sb.Append(r).Append("\n");
                    Dictionary<string, string> one = ParseFastbootVars(r);
                    string v;
                    if (one.TryGetValue(row[0], out v) && PlausibleFastbootValue(row[0], v))   // key + တန်ဖိုး ကိုက်မှ လက်ခံ
                        vars[row[0]] = v;
                    else if (!string.IsNullOrWhiteSpace(v))
                        rejected++;
                }
                rawAll = sb.ToString();
            }

            Log("======================================", Color.FromArgb(0, 180, 255));
            Log(title.ToUpperInvariant(), Color.White);
            Log("======================================", Color.FromArgb(0, 180, 255));

            int shown = 0;
            foreach (string[] row in show)
            {
                string v;
                if (!vars.TryGetValue(row[0], out v) || string.IsNullOrWhiteSpace(v)) continue;
                Log(row[1].PadRight(22) + ": " + v, Color.White);
                shown++;
            }
            if (shown == 0)
                Log("(no key variables found - check that the phone is in fastboot mode)", Color.Orange);

            Log("--------------------------------------", Color.FromArgb(0, 180, 255));
            Log("[i] " + shown + " key variables shown.", Color.Gray);
            if (allFailed && shown > 0)
                Log("[i] Note: per-key queries used (this fastboot variant has no 'getvar all').", Color.Gray);
            if (allFailed && rejected > 0)
                Log("[i] " + rejected + " value(s) ignored (inconsistent on this fastboot variant).", Color.Gray);

            try
            {
                string dump = Path.Combine(Path.GetTempPath(), "pmk_fastboot_vars.txt");
                File.WriteAllText(dump, "=== " + args + " ===\n" + rawAll);
                Log("[i] Full dump: " + dump, Color.Gray);
            }
            catch (Exception ex)
            {
                Log("[!] Fastboot dump write failed: " + ex.Message, Color.Orange);
            }
        }

        private async void BtnFbInfo_Click(object sender, EventArgs e)
        {
            if (!await FastbootReadyAsync("Fastboot Full Info")) return;
            await ShowFastbootVarsAsync("getvar all", "Fastboot Info (key variables)");
        }

        private async void BtnFbFlash_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog ofd = new OpenFileDialog { Filter = "Image Files (*.img)|*.img" })
            {
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    string partition = Path.GetFileNameWithoutExtension(ofd.FileName).ToLower();
                    await ExecuteCommandCleanAsync("fastboot.exe", "flash " + partition + " \"" + ofd.FileName + "\"", "Flash [" + partition + "]");
                }
            }
        }

        private async void BtnFbBootTemp_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog ofd = new OpenFileDialog { Filter = "Image Files (*.img)|*.img" })
            {
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    await ExecuteCommandCleanAsync("fastboot.exe", "boot \"" + ofd.FileName + "\"", "Boot Temp Image");
                }
            }
        }

        private async void BtnFbErase_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show("Erase Userdata & Cache partitions?", "Warning", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                await ExecuteCommandCleanAsync("fastboot.exe", "erase cache", "Erase Cache");
                await ExecuteCommandCleanAsync("fastboot.exe", "erase userdata", "Erase Userdata");
            }
        }

        // ================= EDL MODULE (Qualcomm) =================
        // edl.py (bkerler EDL) ကို mtk.exe လိုပဲ ခေါ်သုံးတယ် — path ကို pmk_paths.txt မှာ သိမ်းတယ်
        private string PathsFile
        {
            get { return ShopServices.DataPath("pmk_paths.txt"); }
        }

        private void LoadEdlPaths()
        {
            try
            {
                if (!File.Exists(PathsFile)) return;
                string[] lines = File.ReadAllLines(PathsFile);
                if (lines.Length > 0) edlScriptPath = lines[0].Trim();
                // lines[1] (QC loader) / lines[8] (SPD loader) — မသိမ်းတော့ (user request)
                if (lines.Length > 3) hisiBootloadersPath = lines[3].Trim();
                if (lines.Length > 4) edlSigPath = lines[4].Trim();
                if (lines.Length > 5) mtkDaPath = lines[5].Trim();
                if (lines.Length > 6) mtkAuthPath = lines[6].Trim();
                if (lines.Length > 7) mtkPreloaderPath = lines[7].Trim();
            }
            catch (Exception ex)
            {
                // rtbLog မဖန်တီးရသေးလို့ Log() မခေါ်နိုင် — crash handler က cover
                Debug.WriteLine("LoadEdlPaths: " + ex.Message);
            }
        }

        private void SaveEdlPaths()
        {
            try
            {
                // slot 1 (QC loader) + slot 8 (SPD loader) = ကွက်လပ် — ရွေးထားတဲ့ loader များ ပိတ်ရင် ပြန်မပေါ်စေရ (index alignment အတွက် slot နေရာထား)
                File.WriteAllLines(PathsFile, new string[]
                {
                    edlScriptPath ?? "", "", edlLegacySlot ?? "", hisiBootloadersPath ?? "",
                    edlSigPath ?? "", mtkDaPath ?? "", mtkAuthPath ?? "", mtkPreloaderPath ?? "", ""
                });
            }
            catch (Exception ex) { Log("Path save error: " + ex.Message, Color.Red); }
        }

        // User settings (checkbox 10 ခု) — pmk_settings.txt (key=value, one per line)
        private string SettingsFile
        {
            get { return ShopServices.DataPath("pmk_settings.txt"); }
        }

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsFile)) return;
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string raw in File.ReadAllLines(SettingsFile))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    map[key] = val;
                }
                bool AsBool(string v) => v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
                void Set(CheckBox cb, string key)
                {
                    if (cb != null && map.TryGetValue(key, out string v)) cb.Checked = AsBool(v);
                }
                Set(chkMtkBackupNvFirst, "MtkBackupNvFirst");
                Set(chkMtkResetFrpAfter, "MtkResetFrpAfter");
                Set(chkMtkSkipUserdata, "MtkSkipUserdata");
                Set(chkMtkAutoReboot, "MtkAutoReboot");
                Set(chkQcBackupEfsFirst, "QcBackupEfsFirst");
                Set(chkQcSkipUserdata, "QcSkipUserdata");
                Set(chkQcResetFrpAfter, "QcResetFrpAfter");
                Set(chkQcAutoReboot, "QcAutoReboot");
                Set(chkSamBackupPit, "SamBackupPit");
                Set(chkSamAutoReboot, "SamAutoReboot");
                Set(chkMtkBackupVbmetaFirst, "MtkBackupVbmetaFirst");
                if (map.TryGetValue("LightTheme", out string lt)) { lightTheme = AsBool(lt); ApplyTheme(); }

                // String settings — last COM port / splitter
                // Firmware folder paths ကို မသိမ်းတော့ — ဖွင့်တိုင်း clean (user request)
                if (map.TryGetValue("LastPort", out string lp) && !string.IsNullOrEmpty(lp) &&
                    cmbPorts != null && cmbPorts.Items.Contains(lp))
                {
                    cmbPorts.SelectedItem = lp;
                }
                if (map.TryGetValue("SplitterDistance", out string sd) && int.TryParse(sd, out int dist) && dist > 100)
                {
                    pendingSplitterDistance = dist;
                }
            }
            catch (Exception ex)
            {
                Log("[!] Settings load failed: " + ex.Message, Color.Orange);
            }
        }

        private void SaveSettings()
        {
            try
            {
                void Line(CheckBox cb, string key, StringBuilder sb)
                {
                    sb.Append(key).Append('=').Append(cb != null && cb.Checked ? "1" : "0").AppendLine();
                }
                void Str(string key, string value, StringBuilder sb)
                {
                    sb.Append(key).Append('=').Append(value ?? "").AppendLine();
                }
                var sb = new StringBuilder();
                sb.AppendLine("# PMKUnlocker user settings");
                Line(chkMtkBackupNvFirst, "MtkBackupNvFirst", sb);
                Line(chkMtkResetFrpAfter, "MtkResetFrpAfter", sb);
                Line(chkMtkSkipUserdata, "MtkSkipUserdata", sb);
                Line(chkMtkAutoReboot, "MtkAutoReboot", sb);
                Line(chkQcBackupEfsFirst, "QcBackupEfsFirst", sb);
                Line(chkQcSkipUserdata, "QcSkipUserdata", sb);
                Line(chkQcResetFrpAfter, "QcResetFrpAfter", sb);
                Line(chkQcAutoReboot, "QcAutoReboot", sb);
                Line(chkSamBackupPit, "SamBackupPit", sb);
                Line(chkSamAutoReboot, "SamAutoReboot", sb);
                Line(chkMtkBackupVbmetaFirst, "MtkBackupVbmetaFirst", sb);
                sb.Append("LightTheme=").Append(lightTheme ? "1" : "0").AppendLine();
                // MtkFirmware / QcFirmware ကို ရေးတော့မပါ — ပိတ်ရင် path အဟောင်း ဖျက်သွားမယ်
                Str("LastPort", cmbPorts?.SelectedItem?.ToString() ?? "", sb);
                Str("SplitterDistance", userAdjustedSplit ? splitMain.SplitterDistance.ToString() : "", sb);
                File.WriteAllText(SettingsFile, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Log("[!] Settings save failed: " + ex.Message, Color.Orange);
            }
        }

        // edl.py ကို အလိုအလျောက် ရှာတယ် — exe ဘေး၊ ပြီးရင် Desktop ပေါ်က ရှိပြီးသား copy တွေ
        private string FindEdlScript()
        {
            return FindFileInToolFolders("edl.py");
        }

        private bool EnsureEdlModule()
        {
            if (!string.IsNullOrEmpty(edlScriptPath) && File.Exists(edlScriptPath)) return true;

            edlScriptPath = FindEdlScript();
            if (!string.IsNullOrEmpty(edlScriptPath))
            {
                Log("[*] EDL module: " + edlScriptPath, Color.Cyan);
                SaveEdlPaths();
                return true;
            }

            Log("[!] EDL module (edl.py) not found.", Color.Orange);
            var pick = MessageBox.Show("EDL module (edl.py) မတွေ့ပါ။\n\nခုဏ ရွေးပေးမလား? (tool folder ထဲ ဒါမှမဟုတ် external path)",
                "EDL Module လိုအပ်ပါတယ်", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (pick == DialogResult.OK) BrowseEdlScript();
            return !string.IsNullOrEmpty(edlScriptPath) && File.Exists(edlScriptPath);
        }

        private string BuildEdlArgs(string command)
        {
            string loader = (!string.IsNullOrEmpty(edlLoaderPath) && File.Exists(edlLoaderPath))
                ? " --loader=\"" + edlLoaderPath + "\"" : "";

            // Header တွင် ရွေးထားသော COM Port ကို ထည့်ပေးခြင်း
            string portParam = "";
            if (cmbPorts.SelectedItem != null && cmbPorts.SelectedItem.ToString().StartsWith("COM"))
            {
                portParam = " --port=" + cmbPorts.SelectedItem.ToString();
            }

            return "\"" + edlScriptPath + "\"" + loader + portParam + " " + command;
        }

        private async Task RunEdlAsync(string command, string title, bool clearPartitions = false,
            Func<List<string>, bool> tolerateRaw = null)
        {
            if (!EnsureEdlModule()) return;
            if (!await EnsureQcAuthAsync()) return;
            await ExecuteCommandCleanAsync("python", BuildEdlArgs(command), title, clearPartitions, tolerateRaw: tolerateRaw);
        }

        // Unlock Tool လို — Qualcomm op မဆိုမတိုင်မီ auto Sahara + sig auth
        // (ခလုတ်သီးခြားမထည့်ဘဲ လုပ်ငန်းတိုင်းထဲ အလိုအလျောက်ဝင်)
        private async Task<bool> EnsureQcAuthAsync()
        {
            string port = cmbPorts.SelectedItem?.ToString();
            if (string.IsNullOrEmpty(port) || !port.StartsWith("COM"))
            {
                Log("[!] Select Qualcomm 9008 COM port first.", Color.Orange);
                return false;
            }

            // တစ်ခါ auth ပြီးပြီးသား COM port ဆို ထပ်မလုပ် (device ကနေ EDL မထွက်ချိန်)
            if (qcAuthed && string.Equals(qcAuthedPort, port, StringComparison.OrdinalIgnoreCase))
                return true;

            Log("[*] Auto EDL auth (Sahara + sig)...", Color.Orange);
            SetProgress(20, "EDL Auth");
            var res = await EdlAuth.BypassAsync(
                port,
                string.IsNullOrEmpty(edlLoaderPath) ? null : edlLoaderPath,
                msg =>
                {
                    if (msg.StartsWith("[+]")) Log(" " + msg, Color.LightGreen);
                    else if (msg.StartsWith("[!]")) Log(" " + msg, Color.Orange);
                    else Log(" " + msg, Color.White);
                },
                default,
                string.IsNullOrEmpty(edlSigPath) ? null : edlSigPath);

            if (res.Ok)
            {
                qcAuthed = true;
                qcAuthedPort = port;
                SetProgress(50, "Auth OK");
                Log("[OK] EDL authenticated" + (string.IsNullOrEmpty(res.TargetName) ? "" : " — TargetName=" + res.TargetName), Color.LightGreen);
                return true;
            }

            qcAuthed = false;
            qcAuthedPort = "";
            SetProgress(100, "Auth FAIL");
            Log("[FAIL] EDL auto-auth: " + res.Message, Color.Red);
            return false;
        }

        private async void BtnQcReadInfo_Click(object sender, EventArgs e)
        {
            await RunFlashWorkflowAsync(() => RunEdlAsync("printgpt", "Qualcomm Read Info / GPT", clearPartitions: true));
        }

        private async void BtnQcSaveGpt_Click(object sender, EventArgs e)
        {
            if (!EnsureEdlModule()) return;
            using (FolderBrowserDialog fbd = new FolderBrowserDialog { Description = "GPT + rawprogram XML သိမ်းမယ့် folder ကို ရွေးပါ" })
            {
                if (fbd.ShowDialog() == DialogResult.OK)
                    await RunFlashWorkflowAsync(() => RunEdlAsync("gpt \"" + fbd.SelectedPath + "\" --genxml", "Qualcomm Save GPT + XML"));
            }
        }

        private async void BtnQcFrpRemove_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show(
                    "FRP Remove (Qualcomm) — frp နဲ့ config partition ကို erase လုပ်ပါမယ်။\n\n" +
                    "• User data မပျက်ပါ (FRP/config ကိုသာ ဖျက်တယ်)\n" +
                    "• ဖုန်းပိုင်ရှင် ခွင့်ပြုချက် ရှိမှ လုပ်ပါ (ခိုးရာပစ္စည်း မဟုတ်ကြောင်း စစ်ပါ)\n\n" +
                    "ဆက်လုပ်မလား?",
                    "Confirm FRP Remove", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            await RunFlashWorkflowAsync(() => RunEdlAsync("e frp,config", "Qualcomm FRP Remove (frp + config)"));
        }

        // Userlock Reset — screen lock (PIN/pattern/password) ဖျက်ခြင်း (Qualcomm EDL)။
        // edl.py ရဲ့ `e` command က partition ပထမတစ်ခုတည်းကို erase ပြီး return လုပ်လို့
        // userdata နဲ့ metadata ကို သီးခြား command နှစ်ခုနဲ့ ဆက်တိုက် erase တယ်။
        private async void BtnQcUserlockReset_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show(
                    "Userlock Reset (Qualcomm) — userdata နဲ့ metadata ကို erase လုပ်ပါမယ်။\n\n" +
                    "• ဖုန်းထဲက data / ဓာတ်ပုံ / အက်ပ် အားလုံး ဖျက်ခံရမယ်\n" +
                    "• Screen lock (PIN / pattern / password) ပျက်သွားမယ်\n" +
                    "• ဖုန်းပိုင်ရှင် ခွင့်ပြုချက် ရှိမှ လုပ်ပါ (ခိုးရာပစ္စည်း မဟုတ်ကြောင်း စစ်ပါ)\n" +
                    "• data မဖျက်ဘဲ lock ဖျက်ချင်ရင် root/TWRP + ADB လမ်းကို သုံးပါ\n\n" +
                    "ဆက်လုပ်မလား?",
                    "Confirm Userlock Reset", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            await RunFlashWorkflowAsync(async () =>
            {
                await RunEdlAsync("e userdata", "Qualcomm Userlock Reset (userdata)");
                // metadata မရှိတဲ့ ဖုန်း (ဥပမာ CPH1803/A3s — Android 8/9 era) မှာ edl.py က
                // "Couldn't erase partition metadata / no gpt partition" နဲ့ fail တက် → skip
                await RunEdlAsync("e metadata", "Qualcomm Userlock Reset (metadata)",
                    tolerateRaw: raw =>
                    {
                        string joined = string.Join("\n", raw ?? new List<string>());
                        return joined.Contains("Couldn't erase partition metadata") ||
                               joined.Contains("no gpt partition") ||
                               joined.Contains("No such partition") ||
                               joined.Contains("no such partition") ||
                               joined.Contains("partition metadata") && joined.Contains("not found");
                    });
            }, forceReboot: true);   // erase ပြီးရင် checkbox မကြည့်ဘဲ auto reboot (EDL → Android)
        }

        private async void BtnQcFullBackup_Click(object sender, EventArgs e)
        {
            if (!EnsureEdlModule()) return;
            using (FolderBrowserDialog fbd = new FolderBrowserDialog { Description = "Partition အားလုံး backup သိမ်းမယ့် folder ကို ရွေးပါ" })
            {
                if (fbd.ShowDialog() == DialogResult.OK)
                    await RunFlashWorkflowAsync(() => RunEdlAsync("rl \"" + fbd.SelectedPath + "\"", "Qualcomm Full Backup (all partitions)"));
            }
        }

        // EFS Backup — persist / modemst1 / modemst2 / fsg partition တွေကို bin အဖြစ် dump။
        // ReviewSafety.PrepareEfsBackup နဲ့ တူညီတဲ့ partition set (fsg ထပ်ပါ)။
        // edl.py `r` က partition တစ်ခုတည်းအတွက် — missing partition မပျက်အောင် sequential call သုံးတယ်။
        private async void BtnQcEfsBackup_Click(object sender, EventArgs e)
        {
            if (!EnsureEdlModule()) return;
            using (FolderBrowserDialog fbd = new FolderBrowserDialog { Description = "EFS backup သိမ်းမယ့် folder ကို ရွေးပါ" })
            {
                if (fbd.ShowDialog() != DialogResult.OK) return;
                string dir = fbd.SelectedPath;
                await RunFlashWorkflowAsync(async () =>
                {
                    await RunEdlAsync("r persist \"" + Path.Combine(dir, "persist.bin") + "\"", "EFS Backup (persist)");
                    await RunEdlAsync("r modemst1 \"" + Path.Combine(dir, "modemst1.bin") + "\"", "EFS Backup (modemst1)");
                    await RunEdlAsync("r modemst2 \"" + Path.Combine(dir, "modemst2.bin") + "\"", "EFS Backup (modemst2)");
                    await RunEdlAsync("r fsg \"" + Path.Combine(dir, "fsg.bin") + "\"", "EFS Backup (fsg)");
                });
            }
        }

        private async void BtnQcReset_Click(object sender, EventArgs e)
        {
            qcAuthed = false;
            qcAuthedPort = "";
            // ဒီ button ကိုယ်တိုင် reset ဖြစ် — workflow auto-reboot ထပ်မလုပ်
            await RunFlashWorkflowAsync(() => RunEdlAsync("reset --resetmode=reset", "Qualcomm Reset Device"), autoRebootAfter: false);
        }

        // edl.py manual pick — SETUP button ဖယ်ပြီးနောက် EnsureEdlModule က ဒီ method ကိုပဲ ခေါ်တယ်
        private void BrowseEdlScript()
        {
            using (OpenFileDialog ofd = new OpenFileDialog
            {
                Filter = "EDL script (edl.py)|edl.py|Python (*.py)|*.py|All Files (*.*)|*.*",
                Title = "edl.py ကို ရွေးပါ"
            })
            {
                if (ofd.ShowDialog() != DialogResult.OK) return;
                edlScriptPath = ofd.FileName;
                SaveEdlPaths();
                Log("[OK] EDL module set: " + edlScriptPath, Color.LightGreen);
            }
        }

        // Firehose loader manual pick — loader picker ရဲ့ "Manual / saved loader" ကနေ / Loader file row ကနေ ခေါ်တယ်
        private void BrowseFirehoseLoader()
        {
            using (OpenFileDialog ofd = new OpenFileDialog
            {
                Filter = "Firehose loader (*.mbn;*.elf;*.bin;*.melf)|*.mbn;*.elf;*.bin;*.melf",
                Title = "Firehose programmer (loader) ကို ရွေးပါ"
            })
            {
                if (ofd.ShowDialog() != DialogResult.OK) return;
                ApplyQcLoader(ofd.FileName);
            }
        }

        // picked loader path ကို saved path + combo + status label တွေမှာ တစ်နေရာတည်း update
        private void ApplyQcLoader(string file)
        {
            edlLoaderPath = file ?? "";
            SaveEdlPaths();
            if (string.IsNullOrEmpty(edlLoaderPath))
            {
                if (cmbQcBrand?.SelectedItem as string == "Manual / saved loader" &&
                    cmbQcModel != null && cmbQcModel.Items.Count > 0)
                    cmbQcModel.Items[0] = "Choose Firehose Loader...";
                if (lblQcLoaderStatus != null)
                {
                    lblQcLoaderStatus.Text = "Auto Detect (saved / auto loader)";
                    lblQcLoaderStatus.ForeColor = Color.FromArgb(86, 145, 250);
                }
                return;
            }
            // brand ကတည်းက "Manual" ရွေးထားရင် SelectedIndexChanged ထပ်မဖြစ် → path အရင်သိမ်းမှ brand ပြောင်း
            if (cmbQcBrand != null) cmbQcBrand.SelectedItem = "Manual / saved loader";
            if (lblQcLoaderStatus != null)
            {
                lblQcLoaderStatus.Text = Path.GetFileName(edlLoaderPath);
                lblQcLoaderStatus.ForeColor = Color.LightGreen;
            }
            if (cmbQcModel != null && cmbQcModel.Items.Count > 0)
            {
                string fn = Path.GetFileName(edlLoaderPath);
                if (cmbQcModel.Items[0]?.ToString() != fn) cmbQcModel.Items[0] = fn;
            }
        }

        // ===== "Double click or Drag" file row — QC Loader / MTK DA·Auth·Preloader / SPD Loader =====
        // label column + hint field; double-click = browse, drag & drop = ဖိုင်, right-click = ဖျက်
        private Panel BuildFilePickerRow(Control parent, int x, int y, int w, string label, string hint,
            string filter, Func<string> getPath, Action<string> setPath)
        {
            Color labelBg = RowLabelBg;
            Color fieldBg = RowFieldBg;
            Color dim = RowHint;   // hint — theme အလိုက် ဖတ်ရလွယ်အောင်

            Panel row = new Panel
            {
                Name = "fileRow" + label.Replace(" ", ""),
                Location = new Point(x, y),
                Size = new Size(w, 24),
                BackColor = fieldBg,
                AllowDrop = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            Label lb = new Label
            {
                Text = " " + label,
                Location = new Point(0, 0),
                Size = new Size(70, 24),
                BackColor = labelBg,
                ForeColor = RowLabelText,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                AllowDrop = true,
                Cursor = Cursors.Hand
            };
            Label fld = new Label
            {
                Location = new Point(70, 0),
                Size = new Size(Math.Max(50, w - 70), 24),
                BackColor = fieldBg,
                ForeColor = dim,
                Font = new Font("Segoe UI", 9f),
                TextAlign = ContentAlignment.MiddleLeft,
                BorderStyle = BorderStyle.FixedSingle,
                AllowDrop = true,
                Cursor = Cursors.Hand,
                AutoEllipsis = true
            };
            row.Controls.Add(lb);
            row.Controls.Add(fld);

            void Show()
            {
                string p = getPath();
                if (string.IsNullOrEmpty(p))
                {
                    fld.Text = "⚙   " + hint;
                    fld.ForeColor = RowHint;
                }
                else
                {
                    fld.Text = "✔  " + Path.GetFileName(p);
                    fld.ForeColor = RowLoaded;
                }
            }

            void Apply(string p)
            {
                setPath(p ?? "");
                Show();
                if (!string.IsNullOrEmpty(p)) Log("[OK] " + label + ": " + p, Color.LightGreen);
                else Log("[i] " + label + " file cleared", Color.Gray);
            }

            void Browse()
            {
                using (OpenFileDialog ofd = new OpenFileDialog { Filter = filter, Title = label + " file ကို ရွေးပါ" })
                {
                    if (ofd.ShowDialog(parent.FindForm()) == DialogResult.OK) Apply(ofd.FileName);
                }
            }

            void Clear() { if (!string.IsNullOrEmpty(getPath())) Apply(""); }

            void RowEnter(object s, DragEventArgs e)
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
                    e.Data.GetData(DataFormats.FileDrop) is string[] files &&
                    files.Length > 0 && FileMatchesFilter(files[0], filter))
                    e.Effect = DragDropEffects.Copy;
                else
                    e.Effect = DragDropEffects.None;
            }
            void RowDrop(object s, DragEventArgs e)
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                {
                    if (FileMatchesFilter(files[0], filter)) Apply(files[0]);
                    else Log("[!] " + label + ": wrong file type — " + Path.GetFileName(files[0]), Color.Orange);
                }
            }

            foreach (Control c in new Control[] { row, lb, fld })
            {
                c.MouseDoubleClick += (s, e) => Browse();
                c.MouseClick += (s, e) => { if (e.Button == MouseButtons.Right) Clear(); };
                c.DragEnter += RowEnter;
                c.DragDrop += RowDrop;
            }
            row.MouseEnter += (s, e) => Show();   // တခြား code path က path ပြောင်းရင် hover မှာ refresh
            if (toolTipMain != null)
                Tip(row, "Double-click = browse • Drag & drop file = ထည့် • Right-click = ဖျက်");
            fileRowRefresh[row] = Show;   // theme toggle မှာ bg/text/State ပြန်သတ်မှတ်ဖို့
            Show();
            return row;
        }

        // drag & drop ဖိုင် filter နဲ့ ကိုက်/မကိုက်
        private static bool FileMatchesFilter(string file, string filter)
        {
            try
            {
                if (string.IsNullOrEmpty(filter) || filter.Contains("*.*")) return true;
                string ext = Path.GetExtension(file);
                foreach (string part in filter.Split('|'))
                {
                    if (!part.Contains("*.")) continue;
                    foreach (string tok in part.Split(';'))
                    {
                        string t = tok.Trim();
                        if (t.StartsWith("*.") && t.Equals("*" + ext, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                }
                return false;
            }
            catch { return false; }
        }

        // ================= QC LOADER PICKER (pmk_qc_loaders.json) =================
        private void LoadQcCodenames()
        {
            try
            {
                string path = Path.Combine(Application.StartupPath, "pmk_codenames.json");
                if (!File.Exists(path)) return;
                var raw = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(path));
                if (raw != null) qcCodenames = raw;
            }
            catch { /* codename မရှိရင် display က product+mode ပဲ ပြ */ }
        }

        private string GetQcCodename(string brand, string productName)
        {
            if (qcCodenames.TryGetValue(brand, out var map) && productName.Length > 0)
            {
                // exact match
                if (map.TryGetValue(productName, out var code)) return code;
                // case-insensitive partial: productName က key ထဲ ပါ/key က productName ထဲ ပါ
                foreach (var kv in map)
                {
                    if (string.Equals(kv.Key, productName, StringComparison.OrdinalIgnoreCase)) return kv.Value;
                    if (productName.Contains(kv.Key, StringComparison.OrdinalIgnoreCase) ||
                        kv.Key.Contains(productName, StringComparison.OrdinalIgnoreCase))
                        return kv.Value;
                }
            }
            return "";
        }

        // "[AuthBypass] MI 10" + brand → "MI 10 (umi) (AuthBypass)" — product name + codename + mode
        private string FormatQcModelDisplay(string brand, string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            string product = key;
            string mode = "";
            if (key.StartsWith('['))
            {
                int close = key.IndexOf(']');
                if (close > 1)
                {
                    mode = key.Substring(1, close - 1);
                    product = key.Substring(close + 1).TrimStart();
                }
            }
            string code = GetQcCodename(brand, product);
            string result = code.Length > 0 ? product + " (" + code + ")" : product;
            if (mode.Length > 0) result += " (" + mode + ")";
            return result;
        }

        private void LoadQcLoaderDb()
        {
            try
            {
                LoadQcCodenames();
                cmbQcBrand.Items.Add("Auto Detect");
                cmbQcBrand.Items.Add("Manual / saved loader");
                int initialSelection = string.IsNullOrEmpty(edlLoaderPath) ? 0 : 1;
                string path = Path.Combine(Application.StartupPath, "pmk_qc_loaders.json");
                if (!File.Exists(path)) { cmbQcBrand.SelectedIndex = initialSelection; return; }
                var raw = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(path));
                if (raw != null) qcLoaderDb = raw;
                foreach (var brand in qcLoaderDb.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
                    cmbQcBrand.Items.Add(brand);
                cmbQcBrand.SelectedIndex = initialSelection;
            }
            catch (Exception ex) { cmbQcBrand.Items.Add("(load error: " + ex.Message + ")"); cmbQcBrand.SelectedIndex = 0; }
        }

        // ===== QC: DEVICE LIST model → Firehose loader auto-link =====
        private static string NormQcName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToUpperInvariant(c));
            return sb.ToString();
        }

        private string MapLoaderBrand(string deviceBrand)
        {
            if (string.IsNullOrWhiteSpace(deviceBrand) || qcLoaderDb.Count == 0) return null;
            string b = deviceBrand.Trim();
            if (b.Equals("Moto", StringComparison.OrdinalIgnoreCase)) b = "Motorola";
            else if (b.Equals("One Plus", StringComparison.OrdinalIgnoreCase)) b = "Oneplus";
            else if (b.Equals("Huawei & Honor", StringComparison.OrdinalIgnoreCase)) b = "Huawei";
            foreach (string k in qcLoaderDb.Keys)
                if (string.Equals(k, b, StringComparison.OrdinalIgnoreCase)) return k;
            return null;
        }

        private static string MatchLoaderModel(string deviceModel, IEnumerable<string> keys)
        {
            string d = NormQcName(deviceModel);
            if (d.Length < 3) return null;
            string best = null;
            int bestScore = -1, bestLen = int.MaxValue;
            foreach (string key in keys)
            {
                // "[AuthBypass] MI 10" → "MI 10" — mode prefix ကို ချန်ပြီး product နဲ့ နှိုင်း
                string prod = key ?? "";
                if (prod.StartsWith('['))
                {
                    int close = prod.IndexOf(']');
                    if (close > 0) prod = prod.Substring(close + 1);
                }
                prod = NormQcName(prod);
                if (prod.Length < 3) continue;

                int score;
                if (prod == d) score = 3;                                   // အတိအကျ
                else if (prod.Length >= 4 && d.StartsWith(prod, StringComparison.Ordinal)) score = 2;  // device မှာ prefix/suffix ပိုပါ
                else if (prod.Length >= 4 && d.IndexOf(prod, StringComparison.Ordinal) >= 0) score = 1;
                else if (d.Length >= 4 && prod.StartsWith(d, StringComparison.Ordinal)) score = 1;    // loader မှာ ပိုရှည်
                else continue;

                if (score > bestScore ||
                    (score == bestScore && prod.Length < bestLen) ||
                    (score == bestScore && prod.Length == bestLen && best != null && string.CompareOrdinal(key, best) < 0))
                {
                    best = key; bestScore = score; bestLen = prod.Length;
                }
            }
            return best;
        }

        private void AutoLinkQcLoader(string deviceBrand, string deviceModel)
        {
            try
            {
                if (qcLoaderDb.Count == 0 || cmbQcBrand == null || cmbQcModel == null) return;
                string brand = MapLoaderBrand(deviceBrand);
                if (brand == null || !qcLoaderDb.TryGetValue(brand, out var models)) return;
                string key = MatchLoaderModel(deviceModel, models.Keys);
                if (key == null) return;

                if (!string.Equals(cmbQcBrand.SelectedItem as string, brand, StringComparison.OrdinalIgnoreCase))
                    cmbQcBrand.SelectedItem = brand;   // handler က cmbQcModel ကို ပြန်ဖြည့်တယ်
                if (!string.Equals(cmbQcBrand.SelectedItem as string, brand, StringComparison.OrdinalIgnoreCase)) return;

                int idx = -1;
                for (int i = 0; i < cmbQcModel.Items.Count; i++)
                {
                    string disp = cmbQcModel.Items[i]?.ToString();
                    if (disp != null && qcModelDisplayToKey.TryGetValue(disp, out string k) &&
                        string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
                }
                if (idx < 0 || cmbQcModel.SelectedIndex == idx) return;
                cmbQcModel.SelectedIndex = idx;   // → edlLoaderPath + log ([OK] Loader: ...)
            }
            catch { /* auto-link က link မလုပ်နိုင်ရင် loader picker အတိုင်းပဲ ထား */ }
        }

        private void CmbQcBrand_SelectedIndexChanged(object sender, EventArgs e)
        {
            cmbQcModel.Items.Clear();
            qcModelDisplayToKey.Clear();
            cmbQcModel.Enabled = false;
            if (cmbQcBrand.SelectedItem is not string brand) return;

            if (brand == "Manual / saved loader")
            {
                cmbQcModel.Items.Add(string.IsNullOrEmpty(edlLoaderPath) ? "Choose Firehose Loader..." : Path.GetFileName(edlLoaderPath));
                cmbQcModel.SelectedIndex = 0;
                return;
            }
            if (brand == "Auto Detect")
            {
                cmbQcModel.Items.Add("Auto Detect");
                cmbQcModel.SelectedIndex = 0;
                cmbQcModel.Enabled = false;
                return;
            }
            if (!qcLoaderDb.TryGetValue(brand, out var models)) return;
            foreach (var model in models.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                string display = FormatQcModelDisplay(brand, model);
                // display duplicate ဖြစ်ရင် key ကိုပဲ ထား
                if (qcModelDisplayToKey.ContainsKey(display)) display = model;
                qcModelDisplayToKey[display] = model;
                cmbQcModel.Items.Add(display);
            }
            cmbQcModel.Enabled = true;
            if (cmbQcModel.Items.Count > 0) cmbQcModel.SelectedIndex = 0;
        }

        private void CmbQcModel_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (cmbQcBrand.SelectedItem is not string brand) return;
            if (cmbQcModel.SelectedItem is not string display) return;

            if (brand == "Manual / saved loader")
            {
                // saved loader မရှိသေးရင် — ခုဏက ဒီ item ကို ရွေးတာမို့ browse dialog ချက်ချင်းဖွင့်
                if (string.IsNullOrEmpty(edlLoaderPath)) BrowseFirehoseLoader();
                lblQcLoaderStatus.Text = string.IsNullOrEmpty(edlLoaderPath) ? "Choose a Firehose Loader file" : Path.GetFileName(edlLoaderPath);
                lblQcLoaderStatus.ForeColor = File.Exists(edlLoaderPath) ? Color.LightGreen : Color.Orange;
                return;
            }

            // Explicit Auto Detect clears any previously selected manual loader.
            if (brand == "Auto Detect" || display == "Auto Detect")
            {
                edlLoaderPath = "";
                SaveEdlPaths();
                lblQcLoaderStatus.Text = "Auto Detect (no manual loader selected)";
                lblQcLoaderStatus.ForeColor = Color.FromArgb(86, 145, 250);
                return;
            }

            // display → JSON key
            string model = qcModelDisplayToKey.TryGetValue(display, out var k) ? k : display;

            if (!qcLoaderDb.TryGetValue(brand, out var models)) return;
            if (!models.TryGetValue(model, out var relPath)) return;

            string fullPath = Path.GetFullPath(Path.Combine(Application.StartupPath, relPath.Replace('/', Path.DirectorySeparatorChar)));
            if (File.Exists(fullPath))
            {
                ToolIntegrity.Verify(fullPath);
                edlLoaderPath = fullPath;
                SaveEdlPaths();
                lblQcLoaderStatus.Text = brand + " / " + display;
                lblQcLoaderStatus.ForeColor = Color.LightGreen;
                Log("[OK] Loader: " + brand + " → " + display + " (" + Path.GetFileName(fullPath) + ")", Color.LightGreen);
            }
            else
            {
                edlLoaderPath = "";
                SaveEdlPaths();
                lblQcLoaderStatus.Text = "file not found: " + Path.GetFileName(relPath);
                lblQcLoaderStatus.ForeColor = Color.OrangeRed;
                Log("[!] Loader file not found: " + fullPath, Color.OrangeRed);
            }
        }

        // ================= BRAND INFO / KNOX (adb) =================
        private async Task RunBrandInfoAsync(string brand)
        {
            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("   " + brand.ToUpper() + " - DEVICE INFO", Color.White);
            Log("========================================================", Color.FromArgb(0, 180, 255));

            string devices = await ExecuteCommandQuickAsync("adb.exe", "devices");
            if (!devices.Contains("\tdevice"))
            {
                Log("[!] No ADB device found - enable USB debugging and connect the phone.", Color.OrangeRed);
                return;
            }

            string brandProp = await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.product.brand");
            string model = await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.product.model");
            string device = await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.product.device");
            string android = await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.build.version.release");
            string sdk = await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.build.version.sdk");
            string patch = await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.build.version.security_patch");
            string build = await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.build.display.id");
            string serial = await ExecuteCommandQuickAsync("adb.exe", "get-serialno");
            string csc = await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.csc.sales_code");
            string bl = await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.boot.bootloader");

            Log("  • Serial         : " + serial.Trim(), Color.White);
            Log("  • Brand          : " + brandProp.Trim(), Color.White);
            Log("  • Model          : " + model.Trim(), Color.White);
            Log("  • Codename       : " + device.Trim(), Color.White);
            Log("  • Android        : " + android.Trim() + " (SDK " + sdk.Trim() + ")", Color.White);
            Log("  • Build          : " + build.Trim(), Color.White);
            Log("  • Security Patch : " + patch.Trim(), Color.White);
            if (!string.IsNullOrWhiteSpace(csc)) Log("  • CSC / Region   : " + csc.Trim(), Color.White);
            if (!string.IsNullOrWhiteSpace(bl)) Log("  • Bootloader     : " + bl.Trim(), Color.White);
            Log("========================================================", Color.FromArgb(0, 180, 255));
        }

        private async Task RunKnoxStatusAsync()
        {
            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("   SAMSUNG - KG / KNOX STATUS", Color.White);
            Log("========================================================", Color.FromArgb(0, 180, 255));

            string devices = await ExecuteCommandQuickAsync("adb.exe", "devices");
            if (!devices.Contains("\tdevice"))
            {
                Log("[!] No ADB device found.", Color.OrangeRed);
                return;
            }

            string kg = await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.boot.kg.state");
            string kgId = await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.boot.kg.bit");
            string warranty = await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.boot.warranty_bit");
            string vboot = await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.boot.verifiedbootstate");
            string flashLock = await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.boot.flash.locked");

            Log("  • KG State       : " + (string.IsNullOrWhiteSpace(kg) ? "(n/a)" : kg.Trim()), Color.White);
            Log("  • KG Bit         : " + (string.IsNullOrWhiteSpace(kgId) ? "(n/a)" : kgId.Trim()), Color.White);
            Log("  • Warranty Bit   : " + (string.IsNullOrWhiteSpace(warranty) ? "(n/a)" : warranty.Trim()), Color.White);
            Log("  • Verified Boot  : " + (string.IsNullOrWhiteSpace(vboot) ? "(n/a)" : vboot.Trim()), Color.White);
            Log("[*] Removing the KG/Knox lock needs a Samsung-specific path (this tab only reads status).", Color.Orange);
            Log("========================================================", Color.FromArgb(0, 180, 255));
        }

        // FRP Reset — adb (USB debugging) ရနိုင်တဲ့ Samsung/SPD/Huawei အတွက် setup wizard ကျော်နည်း
        // ADB FRP Reset (no root)
        // မှတ်ချက်: Android 11/12+ မှာ `pm clear <system pkg>` ကို shell ကို ခွင့်မပြုတော့ဘူး
        //   ("does not have permission android.permission.CLEAR_APP_USER_DATA") — ဒါက ပုံမှန်။
        //   အလုပ်လုပ်တဲ့ အဓိက အဆင့်တွေက provisioning/setup_complete သတ်မှတ်တာ + setup wizard ကို
        //   user 0 အတွက် disable လုပ်တာ ဖြစ်တယ်။ ဒါကြောင့် pm clear ကို "best effort" အနေနဲ့ပဲ လုပ်ပြီး
        //   block ဖြစ်ရင် error မပြဘဲ မှတ်ချက်ပဲ ပြတယ် (FAIL banner မထုတ်တော့ဘူး)။
        private async Task RunAdbFrpResetAsync(string brand, bool skipConfirm = false)
        {
            if (!skipConfirm)
            {
                if (MessageBox.Show(
                        brand + " - FRP Reset (adb နည်းလမ်း)" + Environment.NewLine + Environment.NewLine +
                        "Setup wizard ကို ကျော်ပြီး Google account verification ကို ဖျက်ပါမယ်။" + Environment.NewLine +
                        "• Root မလိုပါ၊ ဒါပေမဲ့ adb (USB debugging) ရနိုင်ရမယ်" + Environment.NewLine +
                        "• Android 11/12+ မှာ `pm clear` ကို system က ပိတ်ထားတယ် — settings + wizard disable နဲ့ လုပ်မယ်" + Environment.NewLine +
                        "• ဖုန်းပိုင်ရှင် ခွင့်ပြုချက် ရှိမှ လုပ်ပါ (ခိုးရာပစ္စည်း မဟုတ်ကြောင်း စစ်ပါ)" + Environment.NewLine + Environment.NewLine +
                        "ဆက်လုပ်မလား?",
                        "Confirm FRP Reset", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;
            }

            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("   " + brand.ToUpper() + " - FRP RESET (ADB / no root)", Color.White);
            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("Removing FRP...", Color.Orange);

            // ၁။ အဓိက အဆင့် — provisioning + setup complete (ဒါက Android 12+ မှာ အလုပ်လုပ်တယ်)
            await ExecuteCommandQuickAsync("adb.exe", "shell settings put secure user_setup_complete 1");
            await ExecuteCommandQuickAsync("adb.exe", "shell settings put global device_provisioned 1");
            string chk = (await ExecuteCommandQuickAsync("adb.exe",
                "shell settings get global device_provisioned")).Trim();
            Log("[OK] device_provisioned=1, user_setup_complete=1" +
                (string.IsNullOrWhiteSpace(chk) ? "" : "  (verify: device_provisioned=" + chk + ")"), Color.LightGreen);

            // ၂။ Setup wizard ကို user 0 အတွက် disable (best effort — package အလိုက် ကွာတယ်)
            string[] wizards = { "com.google.android.setupwizard", "com.android.setupwizard",
                                 "com.google.android.pixel.setupwizard", "com.sec.android.app.setupwizard" };
            // Huawei / HiSilicon ကို HW setup wizard တွေပါ ထည့်
            if (brand.Contains("Huawei", StringComparison.OrdinalIgnoreCase) ||
                brand.Contains("HiSilicon", StringComparison.OrdinalIgnoreCase))
            {
                wizards = wizards.Concat(new[]
                {
                    "com.huawei.android.hwsetupwizard",
                    "com.huawei.android.wizard",
                    "com.huawei.hwsetupwizard",
                    "com.huawei.systemapp.setupwizard"
                }).ToArray();
                Log("[i] Huawei setup wizard packages included.", Color.Gray);
            }
            int disabled = 0;
            foreach (string pkg in wizards)
            {
                string outp = (await ExecuteCommandQuickAsync("adb.exe", "shell pm disable-user --user 0 " + pkg)).Trim();
                string low = outp.ToLowerInvariant();
                if (low.Contains("new state: disabled"))
                {
                    Log("[OK] " + pkg + " → disabled (user 0)", Color.LightGreen);
                    disabled++;
                }
                else if (low.Contains("securityexception") || low.Contains("not allowed") || low.Contains("permission"))
                {
                    Log("[i] " + pkg + " → disable blocked by system policy (Android 12+ normal)", Color.Gray);
                }
                else if (low.Contains("unknown package") || low.Contains("unable to find") || low.Contains("not installed"))
                {
                    // ဒီ device မှာ မရှိ — ကျော်
                }
                else if (!string.IsNullOrWhiteSpace(outp))
                {
                    Log("[i] " + pkg + " → " + outp.Split('\n')[0], Color.Gray);
                }
            }
            if (disabled == 0)
                Log("[i] Setup wizard disable was not allowed - the provisioning settings alone apply (try reboot).", Color.Orange);

            // ၃။ pm clear (best effort — Android 11+ မှာ block ဖြစ်တာ ပုံမှန်)
            var clearPkgs = new List<string> { "com.google.android.setupwizard", "com.google.android.gms" };
            if (brand.Contains("Huawei", StringComparison.OrdinalIgnoreCase) ||
                brand.Contains("HiSilicon", StringComparison.OrdinalIgnoreCase))
            {
                clearPkgs.Add("com.huawei.android.hwsetupwizard");
                clearPkgs.Add("com.huawei.android.wizard");
            }
            foreach (string pkg in clearPkgs)
            {
                string outp = (await ExecuteCommandQuickAsync("adb.exe", "shell pm clear " + pkg)).Trim();
                if (outp.ToLowerInvariant().Contains("success"))
                    Log("[OK] " + pkg + " data cleared", Color.LightGreen);
                else
                    Log("[i] " + pkg + " clear → blocked (Android 11+ normal) — skip", Color.Gray);
            }

            Log("[i] Press Reboot System (or restart the phone) - the launcher should come up.", Color.Orange);
            Log("[OK] ADB FRP steps done.", Color.LightGreen);
        }

        private async void BtnSamFrp_Click(object sender, EventArgs e) { await RunAdbFrpResetAsync("Samsung"); }
        private async void BtnSpdFrp_Click(object sender, EventArgs e) { await RunAdbFrpResetAsync("Spreadtrum"); }
        private async void BtnHisiFrp_Click(object sender, EventArgs e) { await RunAdbFrpResetAsync("HiSilicon / Huawei"); }

        // ================= XIAOMI TEMP ROOT FRP (ADB) =================
        // USB debugging ရှိပြီးသား → ADB ကနေ temp root (su / kernel exploit) သုံးပြီး FRP ဖြေရှင်း
        // - su uid=0 ရရင်: accounts*.db + device_policies + MIUI/Xiaomi pkgs + frp partition
        // - su မရရင်: exploits\manifest.json match → payload push/run (ART-style kernel temp root)
        // - နောက်ဆုံး: MIUI provision/settings + wizard skip (best-effort, no-root path)
        private async void BtnXiaomiTempRootFrp_Click(object sender, EventArgs e)
        {
            string dv = await ExecuteCommandQuickAsync("adb.exe", "devices -l");
            var adbTargets = new List<(string Serial, string Model)>();
            foreach (string raw in dv.Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase)) continue;
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || parts[1] != "device") continue;
                string mdl = "";
                foreach (string p in parts)
                    if (p.StartsWith("model:", StringComparison.Ordinal)) mdl = p.Substring(6).Replace('_', ' ');
                adbTargets.Add((parts[0], mdl));
            }
            if (adbTargets.Count == 0)
            {
                MessageBox.Show("ADB device not found." + Environment.NewLine + Environment.NewLine
                                + "- Developer options > USB debugging ဖွင့်ပါ" + Environment.NewLine
                                + "- Cable ချိတ်ပြီး RSA prompt ကို Allow ပါ",
                    "Xiaomi Temp Root FRP", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            // MCT-style: model ရွေးစရာမလို — fingerprint auto-match နဲ့ပဲ run
            // (PickTempRootModel = ရှိနေဆဲ, နောက်မှ Model List UI အတွက် ပြန်သုံးနိုင်)
            ExploitEntry pickedEntry = null;
            if (!await PickTempRootDeviceAsync(adbTargets))
            {
                Log("[i] Temp Root မလုပ်တော့ — ဖုန်း မရွေးခဲ့ဘူး။", Color.Orange);
                return;
            }

            await LoadDevicePropsAsync();
            string brand = Prop(PropBrandKeys);
            string model = Prop(PropModelKeys);
            string miui = Prop("ro.miui.ui.version.name", "ro.hyperos.ui.version.name", "ro.miui.ui.version.code");
            // getprop dump fail → individual props fallback
            if (string.IsNullOrWhiteSpace(brand) || string.IsNullOrWhiteSpace(model))
            {
                Log("[!] getprop dump empty — retrying individual props...", Color.Orange);
                await Task.Delay(500);
                await LoadDevicePropsAsync();
                brand = Prop(PropBrandKeys);
                model = Prop(PropModelKeys);
                miui = Prop("ro.miui.ui.version.name", "ro.hyperos.ui.version.name", "ro.miui.ui.version.code");
                if (string.IsNullOrWhiteSpace(brand))
                    brand = (await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.product.brand")).Trim();
                if (string.IsNullOrWhiteSpace(model))
                    model = (await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.product.model")).Trim();
                if (string.IsNullOrWhiteSpace(miui))
                    miui = (await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.miui.ui.version.name")).Trim();
            }
            bool looksXiaomi = brand.IndexOf("xiaomi", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               brand.IndexOf("redmi", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               brand.IndexOf("poco", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               !string.IsNullOrWhiteSpace(miui);

            string kernelVer = (await ExecuteCommandQuickAsync("adb.exe", "shell uname -r")).Trim();
            if (string.IsNullOrWhiteSpace(kernelVer) || kernelVer.IndexOf('\n') >= 0)
            {
                string pv = (await ExecuteCommandQuickAsync("adb.exe", "shell cat /proc/version")).Trim();
                Match km = Regex.Match(pv, @"Linux version\s+(\S+)");
                kernelVer = km.Success ? km.Groups[1].Value : "";
            }
            else
            {
                kernelVer = kernelVer.Split('\n')[0].Trim();
            }

            var fp = new DeviceFingerprint
            {
                Brand = brand,
                Model = model,
                Codename = Prop(PropDeviceKeys),
                Android = Prop("ro.build.version.release", "ro.product.build.version.release"),
                Sdk = Prop("ro.build.version.sdk", "ro.product.build.version.sdk"),
                Kernel = kernelVer,
                Patch = Prop("ro.build.version.security_patch", "ro.product.build.version.security_patch"),
                Soc = Prop(PropSocKeys),
                Miui = miui
            };
            if (string.IsNullOrWhiteSpace(fp.Android))
                fp.Android = (await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.build.version.release")).Trim();
            if (string.IsNullOrWhiteSpace(fp.Kernel) || fp.Kernel.IndexOf('\n') >= 0)
            {
                string u = (await ExecuteCommandQuickAsync("adb.exe", "shell uname -r")).Trim();
                if (!string.IsNullOrWhiteSpace(u)) fp.Kernel = u.Split('\n')[0].Trim();
            }
            if (string.IsNullOrWhiteSpace(fp.Brand) || string.IsNullOrWhiteSpace(fp.Android))
                Log("[!] ADB props incomplete — brand='" + fp.Brand + "' android='" + fp.Android +
                    "' kernel='" + fp.Kernel + "' (device authorized?)", Color.Orange);

            string exploitsRoot = Path.Combine(Application.StartupPath, "exploits");
            string manifestPath = Path.Combine(exploitsRoot, "manifest.json");
            ExploitManifest manifest = KernelExploit.LoadManifest(manifestPath);
            ExploitEntry exploitMatch = KernelExploit.FindMatch(manifest, fp, out string exploitStatus);

            // ရွေးထားတဲ့ model ≠ ချိတ်ထားတဲ့ဖုန်း → ရပ် (Auto = pickedEntry null → ကျော်)
            if (pickedEntry != null &&
                (exploitMatch == null || !string.Equals(exploitMatch.Id, pickedEntry.Id, StringComparison.Ordinal)))
            {
                MessageBox.Show(
                    "ရွေးထားသော model နဲ့ ချိတ်ထားတဲ့ဖုန်း မတူဘူး။" + Environment.NewLine + Environment.NewLine +
                    "ရွေးထားတာ : " + (string.IsNullOrWhiteSpace(pickedEntry.Name) ? pickedEntry.Id : pickedEntry.Name) + Environment.NewLine +
                    "ချိတ်ထားတာ: " + (string.IsNullOrWhiteSpace(model) ? "?" : model) +
                    " (" + (string.IsNullOrWhiteSpace(fp.Codename) ? "?" : fp.Codename) + ")" + Environment.NewLine +
                    "match       : " + exploitStatus + Environment.NewLine + Environment.NewLine +
                    "→ ရွေးထားတဲ့ model နဲ့ တူတဲ့ဖုန်း ချိတ်ပြီး ထပ်နှိပ်ပါ (သို့) Auto ရွေးပြီး ဆက်ပါ)။",
                    "Xiaomi Temp Root", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string suId = (await ProbeSuAsync()).Trim();
            bool hasRoot = suId.Contains("uid=0");

            if (!looksXiaomi)
            {
                Log("[!] Device brand='" + brand + "' MIUI='" + miui + "' — not Xiaomi. Generic ADB FRP path will still run.", Color.Orange);
            }

            string mode = hasRoot
                ? "temp root (ready)"
                : exploitMatch != null
                    ? "kernel exploit → temp root"
                    : "no-root (ADB only)";
            string exploitLine = exploitMatch != null
                ? "Match : " + exploitMatch.Name + (string.IsNullOrWhiteSpace(exploitMatch.Id) ? "" : " (" + exploitMatch.Id + ")")
                : "Match : none";
            if (MessageBox.Show(
                    "Xiaomi Temp Root" + Environment.NewLine + Environment.NewLine +
                    "Model : " + (string.IsNullOrWhiteSpace(model) ? "?" : model) + Environment.NewLine +
                    "Brand : " + (string.IsNullOrWhiteSpace(brand) ? "?" : brand) +
                    (string.IsNullOrWhiteSpace(miui) ? "" : "  ·  MIUI " + miui) + Environment.NewLine +
                    "Kernel: " + (string.IsNullOrWhiteSpace(fp.Android) ? "?" : "Android " + fp.Android) +
                    (string.IsNullOrWhiteSpace(kernelVer) ? "" : "  ·  " + kernelVer) + Environment.NewLine +
                    "Mode  : " + mode + Environment.NewLine +
                    exploitLine + Environment.NewLine + Environment.NewLine +
                    (hasRoot
                        ? "• Temp root ရှိပြီး — FRP မလုပ်ဘူး" + Environment.NewLine +
                          "• ဆက်လုပ်ရန်: FRP Reset (ROOT) ခလုတ်"
                        : exploitMatch != null
                            ? "• su မရ — kernel exploit payload စမ်းမယ် (ART-style)" + Environment.NewLine +
                              "• Root ရရင် ရပ်မယ် — FRP Reset (ROOT) နဲ့ ဆက်" + Environment.NewLine +
                              "• payload မရှိရင် no-root path ကျ"
                            : "• su / exploit မရှိ — MIUI provision + wizard skip (best-effort)" + Environment.NewLine +
                              "• Android 11+ မှာ full FRP အတွက် root/temp root လိုနိုင်") + Environment.NewLine + Environment.NewLine +
                    "ဖုန်းပိုင်ရှင် ခွင့်ပြုချက် ရှိမှ လုပ်ပါ။ ဆက်လုပ်မလား?",
                    "Confirm Xiaomi Temp Root", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("   XIAOMI TEMP ROOT (" + mode.ToUpperInvariant() + ")", Color.White);
            Log("========================================================", Color.FromArgb(0, 180, 255));
            // ---- Device Check (72FLASHER-style structured log) ----
            string sel = (await ExecuteCommandQuickAsync("adb.exe", "shell getenforce")).Trim();
            string up = (await ExecuteCommandQuickAsync("adb.exe", "shell cat /proc/uptime")).Trim();
            string bootU = up.Length > 0 ? up.Split(' ')[0].Trim() : "";
            string blLocked = Prop("ro.boot.flash.locked");
            string blState = blLocked == "1" ? "locked" : blLocked == "0" ? "unlocked" : Prop("ro.boot.verifiedbootstate");
            string slot = Prop("ro.boot.slot_suffix").TrimStart('_');

            void DLine(string label, string value, Color? color = null)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                Log(label + ":" + value, color ?? Color.Gainsboro);
            }
            Log("Device Check", Color.FromArgb(0, 180, 255));
            DLine("Brand", brand);
            DLine("Model", model);
            DLine("Product", Prop("ro.product.name"));
            DLine("Marketname", Prop("ro.product.marketname", "ro.product.vendor.marketname"));
            DLine("OS version", Prop("ro.build.version.incremental"));
            DLine("Region", Prop("ro.miui.region", "ro.product.locale.region"));
            DLine("Hardware", Prop("ro.hardware"));
            DLine("Soc manufacturer", Prop("ro.soc.manufacturer"));
            DLine("Soc model", Prop("ro.soc.model"));
            DLine("Serialno", Prop("ro.serialno"));
            DLine("IMEI", Prop("ro.ril.oem.imei1", "ro.ril.oem.imei", "ro.ril.miui.imei0", "persist.radio.imei1", "gsm.imei"));
            DLine("IMEI2", Prop("ro.ril.oem.imei2", "ro.ril.miui.imei1", "persist.radio.imei2"));
            DLine("MEID", Prop("ro.ril.oem.meid", "persist.radio.meid"));
            string frpPath = (await ExecuteCommandQuickAsync("adb.exe",
                "shell \"ls -d /dev/block/bootdevice/by-name/frp /dev/block/by-name/frp 2>/dev/null\"")).Trim();
            frpPath = frpPath.Split('\n')[0].Trim();
            DLine("FRP PST", frpPath);
            DLine("Version", fp.Android);
            DLine("SdkVersion", fp.Sdk);
            DLine("Android Cpu", Prop("ro.product.cpu.abi"));
            DLine("Android platform", Prop("ro.board.platform"));
            DLine("Board name", Prop("ro.product.board"));
            DLine("Kernel", fp.Kernel);
            DLine("Security patch", fp.Patch);
            DLine("Software version", Prop("ro.build.display.id"));
            DLine("Time Zone", Prop("persist.sys.timezone"));
            DLine("Selinux State", sel,
                sel.Equals("Enforcing", StringComparison.OrdinalIgnoreCase) ? Color.Orange :
                sel.Equals("Permissive", StringComparison.OrdinalIgnoreCase) ? Color.LightGreen : (Color?)null);
            DLine("Root Access", hasRoot ? "Success" : "Denied",
                hasRoot ? Color.LightGreen : Color.OrangeRed);
            DLine("bootloader state", blState,
                blState == "unlocked" ? Color.LightGreen : Color.OrangeRed);
            DLine("Crypto State", Prop("ro.crypto.state"));
            DLine("Activated slot", slot);
            DLine("Since boot", bootU);
            Log("[i] Exploit manifest: " + manifestPath, Color.Gray);
            Log("[i] Exploit status: " + exploitStatus, exploitMatch != null ? Color.LightGreen : Color.Gray);
            Log("Temp Root is running. Do not disconnect device.", Color.Orange);

            // Step A: su prompt — screen ပေါ် Allow စောင့် (temp root)
            if (!hasRoot)
            {
                Log("[*] Trying adb root (eng builds) + su probe...", Color.Orange);
                await ExecuteCommandQuickAsync("adb.exe", "root");
                await Task.Delay(800);
                suId = await ProbeSuAsync();
                hasRoot = suId.Contains("uid=0");
                if (hasRoot) Log("[OK] Root obtained via adb root/su: " + suId, Color.LightGreen);
                else Log("[i] No root from adb root/su yet.", Color.Orange);
            }
            else
            {
                Log("[i] Root ရှိပြီးသား — လက်ရှိ boot မှာ exploit ထပ်မလိုပါ", Color.Cyan);
            }

            // Step A1: MobileSea temp root — token-gated payload (GUI မလို, token formula ကိုယ်တိုင် generate)
            double mstSec = -1;
            bool viaMst = false;
            // Android16(zircon/MT6886/5.15.180) — token payload ဒီ combo မှာ မအောင် (~30s ဖြုတ်) → kernel exploit တန်းဆက်
            bool skipToken = exploitMatch != null &&
                             int.TryParse(fp.Sdk, out int sdkNo) && sdkNo >= 36 &&
                             (fp.Codename.IndexOf("zircon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              fp.Soc.IndexOf("MT6886", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              fp.Kernel.IndexOf("5.15.180", StringComparison.Ordinal) >= 0);
            if (!hasRoot && looksXiaomi && skipToken)
                Log("[i] Token path skip — Android16/zircon (token payload မအောင်, ~30s ဖြုတ်) → kernel exploit ကိုတန်းဆက်", Color.Gray);
            if (!hasRoot && looksXiaomi && !skipToken)
            {
                var swMst = Stopwatch.StartNew();
                (bool mstRoot, double mstRealSec) = await TryMstTempRootAsync();
                swMst.Stop();
                if (mstRoot)
                {
                    hasRoot = true;
                    viaMst = true;
                    mstSec = mstRealSec >= 0 ? mstRealSec : swMst.Elapsed.TotalSeconds;
                    suId = await ProbeSuAsync();
                }
            }

            // Step A2: ART-style kernel exploit (su မရရင် payload စမ်း)
            double exploitSec = -1;
            if (!hasRoot && exploitMatch != null)
            {
                var swExploit = Stopwatch.StartNew();
                (bool exRoot, double exSec) = await TryKernelExploitAsync(exploitsRoot, exploitMatch, fp);
                swExploit.Stop();
                hasRoot = exRoot;
                // realSec >= 0 → run.log/stdout က actual exploit time (timeout wait မဟုတ်)
                exploitSec = exSec >= 0 ? exSec : swExploit.Elapsed.TotalSeconds;
                if (hasRoot) suId = await ProbeSuAsync();
            }

            // Root ရရင် summary block ပြပြီး ရပ် — FRP ကို FRP Reset (ROOT) နဲ့ ဆက်လုပ်မယ်
            if (hasRoot)
            {
                const string edge = "========================================================";
                Color edgeC = Color.FromArgb(0, 220, 120);
                string suCtx = "unknown";
                int ci = (suId ?? "").IndexOf("context=", StringComparison.Ordinal);
                if (ci >= 0) suCtx = suId.Substring(ci + 8).Trim();
                bool viaKsu = suCtx.IndexOf("u:r:ksu:s0", StringComparison.OrdinalIgnoreCase) >= 0;

                Log("", Color.Gray);
                Log(edge, edgeC);
                Log("      ✅   TEMP ROOT အောင်မြင်ပါတယ်", Color.LightGreen);
                Log(edge, edgeC);
                Log("Temp root completed successfully.", Color.LightGreen);
                Log("Root Access: Success", Color.LightGreen);
                Log("      ဖုန်း     : " + (string.IsNullOrWhiteSpace(model) ? "?" : model) +
                    (string.IsNullOrWhiteSpace(fp.Codename) ? "" : "   ·   " + fp.Codename), Color.White);
                Log("      Android  : " + (string.IsNullOrWhiteSpace(fp.Android) ? "?" : fp.Android) +
                    (string.IsNullOrWhiteSpace(fp.Sdk) ? "" : "  (sdk " + fp.Sdk + ")") +
                    (string.IsNullOrWhiteSpace(fp.Patch) ? "" : "   ·   patch " + fp.Patch), Color.White);
                Log("      Kernel   : " + (string.IsNullOrWhiteSpace(fp.Kernel) ? "?" : fp.Kernel), Color.White);
                if (viaMst)
                    Log("      Exploit  : PMK temp root (token)   ·   " + mstSec.ToString("0.0") + "s", Color.Cyan);
                else if (exploitMatch != null && exploitSec >= 0)
                    Log("      Exploit  : " + (string.IsNullOrWhiteSpace(exploitMatch.Id) ? exploitMatch.Name : exploitMatch.Id) +
                        "   ·   " + exploitSec.ToString("0.0") + "s", Color.Cyan);
                Log("      Root     : uid=0   ·   context=" + suCtx, Color.LightGreen);
                Log("      KernelSU : " + (viaKsu
                        ? "loaded ✓  —  su / module အသုံးပြုလို့ရပြီ"
                        : "မတွေ့ပါ  —  temp root ပဲ active ( reboot ရင် ပျောက် )"),
                    viaKsu ? Color.LightGreen : Color.Orange);
                Log("--------------------------------------------------------", edgeC);
                Log("      ⏭  ဆက်လုပ်ရန် — 🔓 FRP Reset (ROOT) ခလုတ်နဲ့ နှိပ်ပါ", Color.Orange);
                Log("      ⚠  Reboot လုပ်ရင် temp root ပျောက်သွားမယ် (temp root သဘော)", Color.Orange);
                Log(edge, edgeC);
                return;
            }

            Log("[i] No temp root — running MIUI no-root path.", Color.Orange);

            // Step B: MIUI/Xiaomi provision settings (no-root path only)
            await ExecuteCommandQuickAsync("adb.exe", "shell settings put secure user_setup_complete 1");
            await ExecuteCommandQuickAsync("adb.exe", "shell settings put global device_provisioned 1");
            await ExecuteCommandQuickAsync("adb.exe",
                "shell content insert --uri content://settings/secure --bind name:s:user_setup_complete --bind value:s:1");
            await ExecuteCommandQuickAsync("adb.exe",
                "shell content insert --uri content://settings/global --bind name:s:device_provisioned --bind value:s:1");
            Log("[OK] user_setup_complete=1 + device_provisioned=1", Color.LightGreen);

            // Step C: setup wizards (Google + MIUI/Xiaomi)
            // pm disable က system pkgs များ — best effort only
            foreach (string pkg in new[] { "com.google.android.setupwizard", "com.android.setupwizard" })
            {
                string outp = (await ExecuteCommandQuickAsync("adb.exe", "shell pm disable-user --user 0 " + pkg)).Trim();
                if (outp.IndexOf("new state: disabled", StringComparison.OrdinalIgnoreCase) >= 0)
                    Log("[OK] " + pkg + " → disabled", Color.LightGreen);
            }

            // Step D: Google/Xiaomi account packages clear
            foreach (string pkg in new[] { "com.google.android.gms", "com.google.android.gsf", "com.xiaomi.account", "com.xiaomi.mipicks" })
            {
                string outp = (await ExecuteCommandQuickAsync("adb.exe", "shell pm clear " + pkg)).Trim();
                if (outp.IndexOf("success", StringComparison.OrdinalIgnoreCase) >= 0)
                    Log("[OK] pm clear " + pkg, Color.LightGreen);
                else if (!string.IsNullOrWhiteSpace(outp))
                    Log("[i] pm clear " + pkg + ": " + outp.Split('\n')[0].Trim(), Color.Gray);
            }

            // Step E2: no-root MIUI path — second space / user provision tricks
            Log("[*] No-root MIUI extras: provision + second-space attempt...", Color.Orange);
            await ExecuteCommandQuickAsync("adb.exe", "shell am start -a android.intent.action.MAIN -c android.intent.category.HOME");
            string prov = (await ExecuteCommandQuickAsync("adb.exe", "shell settings get global device_provisioned")).Trim();
            string suc = (await ExecuteCommandQuickAsync("adb.exe", "shell settings get secure user_setup_complete")).Trim();
            Log("[i] verify device_provisioned=" + prov + "  user_setup_complete=" + suc, Color.Gray);

            if (prov == "1" || suc == "1")
                Log("[OK] No-root provision path OK — Reboot ပြီး setup wizard ကျော်ကြည့်ပါ။", Color.LightGreen);
            else
            {
                Log("[!] Provision settings not applied — Android 11+ MIUI အတွက် temp root (Magisk/su) လိုနိုင်တယ်။", Color.Orange);
                Log("[i] Fallback: 🔓 FRP Reset (ROOT) / EDL firehose erase frp သုံးပါ။", Color.Orange);
            }

            // Home ပြန်ပို့
            await ExecuteCommandQuickAsync("adb.exe", "shell input keyevent 3");
        }

        // Temp Root model list — manifest ထဲက enabled entry တွေ payload status နဲ့ ရွေးခိုင်း
        // Cancel=true=ပယ် · Entry=null=Auto(match) · Entry!=null=ရွေးထားတဲ့ model
        private (bool Cancel, ExploitEntry Entry) PickTempRootModel(string exploitsRoot)
        {
            ExploitManifest manifest = KernelExploit.LoadManifest(Path.Combine(exploitsRoot, "manifest.json"));
            List<ExploitEntry> entries = manifest.Exploits.Where(e => e.Enabled).ToList();
            if (entries.Count == 0) return (false, null); // list မရှိ → auto flow

            static string KernelShort(string p)
            {
                if (string.IsNullOrWhiteSpace(p)) return "any-kernel";
                string[] parts = p.Split('-');
                return string.Join("-", parts.Take(3));
            }
            static bool Ready(string root, ExploitEntry e) =>
                File.Exists(KernelExploit.PayloadFullPath(root, e)) &&
                e.Companions.All(c => string.IsNullOrWhiteSpace(c) ||
                    File.Exists(KernelExploit.CompanionFullPath(root, e, c)));

            var labels = new List<string>();
            var map = new List<ExploitEntry>();
            var readyFlags = new List<bool>();
            labels.Add("⚡ Auto — ချိတ်ထားတဲ့ဖုန်းအလိုက် အလိုအလျောက် ရွေးမယ် (match)");
            map.Add(null);
            readyFlags.Add(true);
            foreach (ExploitEntry e in entries)
            {
                string models = e.Models.Length > 0
                    ? string.Join(", ", e.Models.Take(2)) + (e.Models.Length > 2 ? " +" + (e.Models.Length - 2) : "")
                    : (string.IsNullOrWhiteSpace(e.Name) ? e.Id : e.Name);
                string cn = e.Codenames.Length > 0 ? e.Codenames[0] : "-";
                string kernel = e.KernelPrefix.Length > 0 ? KernelShort(e.KernelPrefix[0]) : "any-kernel";
                bool rdy = Ready(exploitsRoot, e);
                labels.Add(models + "   ·   " + cn + "   ·   " + kernel +
                            (rdy ? "     ✓ payload အဆင်သင့်" : "     ✗ payload မရှိ"));
                map.Add(e);
                readyFlags.Add(rdy);
            }

            bool dark = !lightTheme;
            Color formBg = dark ? Color.FromArgb(18, 20, 24) : Color.FromArgb(222, 228, 240);
            Color listBg = dark ? Color.FromArgb(26, 29, 35) : Color.FromArgb(243, 246, 252);
            Color listFg = dark ? Color.Gainsboro : Color.FromArgb(35, 38, 48);
            Color titleC = dark ? Color.FromArgb(86, 145, 250) : Color.FromArgb(58, 102, 208);
            Color hintC = dark ? Color.FromArgb(150, 165, 185) : Color.FromArgb(70, 80, 95);
            Color okBg = dark ? Color.FromArgb(86, 145, 250) : Color.FromArgb(58, 102, 208);
            Color cancelBg = dark ? Color.FromArgb(60, 66, 76) : Color.FromArgb(150, 158, 170);
            Color greenC = dark ? Color.FromArgb(0, 220, 140) : Color.FromArgb(0, 130, 70);
            Color redC = dark ? Color.FromArgb(255, 110, 80) : Color.FromArgb(200, 60, 30);
            Color cyanC = dark ? Color.FromArgb(86, 145, 250) : Color.FromArgb(58, 102, 208);

            using var dlg = new Form
            {
                Text = "PMK Unlocker — Temp Root Model List",
                ClientSize = new Size(764, 214),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                BackColor = formBg,
                ForeColor = dark ? Color.White : Color.FromArgb(35, 38, 48),
                Font = new Font("Segoe UI", 9.5f)
            };
            var title = new Label
            {
                Left = 16,
                Top = 12,
                Width = 730,
                Text = "⚙  Temp Root — Model ရွေးပါ",
                ForeColor = titleC,
                Font = new Font("Segoe UI", 11f, FontStyle.Bold)
            };
            var hint = new Label
            {
                Left = 16,
                Top = 40,
                Width = 730,
                Text = "ရွေးထားတဲ့ model နဲ့ ချိတ်ထားတဲ့ဖုန်း တူရမယ်  ·  payload ရှိတဲ့ model စိမ်း၊ မရှိတာ နီ",
                ForeColor = hintC,
                Font = new Font("Segoe UI", 8.5f)
            };
            var list = new ListBox
            {
                Left = 16,
                Top = 66,
                Width = 730,
                Height = 96,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = listBg,
                ForeColor = listFg,
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = 24,
                IntegralHeight = false
            };
            foreach (string l in labels) list.Items.Add(l);
            list.SelectedIndex = 0;
            list.DrawItem += (s, e) =>
            {
                if (e.Index < 0) return;
                bool sel = (e.State & DrawItemState.Selected) != 0;
                Color fg = sel ? Color.White
                             : e.Index == 0 ? cyanC
                             : readyFlags[e.Index] ? greenC : redC;
                using (var b = new SolidBrush(sel ? Color.FromArgb(0, 105, 180) : listBg))
                    e.Graphics.FillRectangle(b, e.Bounds);
                if (e.Index == 0 && !sel)
                    using (var b = new SolidBrush(cyanC))
                        e.Graphics.FillRectangle(b, e.Bounds.X, e.Bounds.Y + e.Bounds.Height - 2, e.Bounds.Width, 2);
                using (var fb = new SolidBrush(fg))
                    e.Graphics.DrawString(list.Items[e.Index].ToString(), list.Font, fb, e.Bounds.X + 4, e.Bounds.Y + 4);
                if ((e.State & DrawItemState.Focus) != 0)
                    using (var p = new Pen(Color.FromArgb(86, 145, 250)))
                        e.Graphics.DrawRectangle(p, e.Bounds.X, e.Bounds.Y, e.Bounds.Width - 1, e.Bounds.Height - 1);
            };
            var cancel = new Button
            {
                Text = "✕  မလုပ်တော့",
                Left = 16,
                Top = 172,
                Width = 140,
                Height = 32,
                FlatStyle = FlatStyle.Flat,
                BackColor = cancelBg,
                ForeColor = Color.White,
                DialogResult = DialogResult.Cancel
            };
            cancel.FlatAppearance.BorderSize = 0;
            cancel.FlatAppearance.MouseOverBackColor = dark ? Color.FromArgb(80, 88, 100) : Color.FromArgb(130, 140, 155);
            var ok = new Button
            {
                Text = "✓  ရွေးပြီး ဆက်",
                Left = 600,
                Top = 172,
                Width = 146,
                Height = 32,
                FlatStyle = FlatStyle.Flat,
                BackColor = okBg,
                ForeColor = Color.White,
                DialogResult = DialogResult.OK
            };
            ok.FlatAppearance.BorderSize = 0;
            ok.FlatAppearance.MouseOverBackColor = dark ? Color.FromArgb(0, 145, 235) : Color.FromArgb(0, 115, 180);
            dlg.Controls.AddRange(new Control[] { title, hint, list, cancel, ok });
            dlg.AcceptButton = ok;
            dlg.CancelButton = cancel;
            if (dlg.ShowDialog(this) != DialogResult.OK) return (true, null);

            ExploitEntry picked = map[list.SelectedIndex];
            if (picked == null) return (false, null);
            if (!Ready(exploitsRoot, picked))
            {
                MessageBox.Show("payload မရှိသေးဘူး — " + picked.Payload + Environment.NewLine + Environment.NewLine
                                + "file ထည့်ပြီးမှ ထပ်စမ်းပါ (exploits\\SUPPORTED_MODELS.txt ကြည့်ပါ)။",
                    "Temp Root", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return (true, null);
            }
            return (false, picked);
        }

        // Temp Root မတိုင်ခင် — ADB ပေါ်က ဖုန်းတွေထဲက တစ်လုံး ရွေးခိုင်းပြီး serial pin (1 လုံး = auto, >1 = dialog)
        private async Task<bool> PickTempRootDeviceAsync(List<(string Serial, string Model)> devices)
        {
            (string Serial, string Model) pick;
            if (devices.Count == 1)
            {
                pick = devices[0];
            }
            else
            {
                using var dlg = new Form
                {
                    Text = "Temp Root — ဘယ်ဖုန်းကို root လုပ်မလဲ?",
                    Width = 490,
                    Height = 170,
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    StartPosition = FormStartPosition.CenterParent,
                    MaximizeBox = false,
                    MinimizeBox = false
                };
                var label = new Label
                {
                    Left = 15,
                    Top = 12,
                    Width = 450,
                    Text = "ADB ပေါ်မှာ ဖုန်း " + devices.Count + " လုံး ရှိ — Temp Root လုပ်မယ့်ဖုန်း ရွေးပါ:"
                };
                var combo = new ComboBox
                {
                    Left = 15,
                    Top = 36,
                    Width = 445,
                    DropDownStyle = ComboBoxStyle.DropDownList
                };
                foreach (var d in devices)
                    combo.Items.Add((d.Model.Length > 0 ? d.Model : "(model မသိ)") + "   ·   " + d.Serial);
                combo.SelectedIndex = 0;
                var ok = new Button { Text = "ရွေးပြီး ဆက်", Left = 290, Top = 70, Width = 170, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "မလုပ်တော့", Left = 15, Top = 70, Width = 110, DialogResult = DialogResult.Cancel };
                dlg.Controls.AddRange(new Control[] { label, combo, ok, cancel });
                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;
                if (dlg.ShowDialog(this) != DialogResult.OK) return false;
                pick = devices[combo.SelectedIndex];
            }
            if (!string.Equals(selectedAdbSerial, pick.Serial, StringComparison.Ordinal))
            {
                selectedAdbSerial = pick.Serial;
                InvalidateAdbDeviceCache();
            }
            Log("[i] Temp Root target: " + (pick.Model.Length > 0 ? pick.Model : pick.Serial) + "  (" + pick.Serial + ")", Color.Cyan);
            return true;
        }

        // su probe — PATH ထဲမှာ su မရှိရင် GhostLock temp-root su client / root helper ကို စမ်း
        // (timeout/retry ဗားရှင်းကို param နဲ့ ခေါ် — bare call က ဒီ wrapper ကိုပဲ ပြန်ရောက်လို့ recursion ဖြစ်)
        // KernelSU su daemon settle — ပထမ pass fail ပြီး error ရှိရင် 2.5s စောင့်ပြီး ထပ်စမ်း၊ uid=0 မရရင်
        // နောက်ဆုံး error text ကို return (caller တွေက Contains("uid=0") နဲ့ပဲ check → "no output" မဟုတ်တော့)
        private async Task<string> ProbeSuAsync()
        {
            string last = "";
            for (int pass = 0; pass < 2; pass++)
            {
                if (pass > 0)
                {
                    if (last.Length == 0) return "";   // su လုံးဝမရှိ — settle မလို
                    await Task.Delay(2500);            // KernelSU/legacy su daemon settle
                }
                foreach (string cmd in new[]
                         {
                             "shell su -c id",
                             "shell /data/local/tmp/su -c id",
                             "shell /data/local/tmp/cve-2026-43499-root -c id"
                         })
                {
                    string s = (await ExecuteCommandQuickAsync("adb.exe", cmd)).Trim();
                    if (s.Contains("uid=0")) return s;
                    if (s.Length > 0) last = s;
                }
            }
            return last;
        }

        // ================= PMK temp root — token-gated payload (no GUI) =================
        // payload ကို /data/local/tmp/.preload.so အဖြစ် push → LD_PRELOAD + MS=<token> နဲ့ id ခေါ်
        // token = AES-256-CBC(pt, key/iv = FNV(ro.serialno) mix) · pt = 84 00 00 00 | epoch_le | K_le  (±120s)
        // verify pass ရင် payload ctor က run_exploit → permissive + su → "MST:Pass!"

        // exploit stdout ထဲက internal trace chatter — log box မှာ မပြတော့ဘူး (file ထဲ ပဲ ကျန်)
        private static bool IsExploitLogNoise(string t)
        {
            string s = t.ToLowerInvariant();
            return s.Contains("slide ") || s.Contains("tracefs") || s.Contains("ksnitch") ||
                   s.Contains("controlled mm") || s.Contains("match_page") || s.Contains("configfs") ||
                   s.Contains("futex") || s.Contains("app fops") || s.Contains("page_owner") ||
                   s.Contains("sock_diag") || s.Contains("prctl_map") || s.Contains("cfi_") ||
                   s.Contains("attempt=") || s.Contains("hint=") || s.Contains("mm dup") ||
                   s.Contains("pipe buf") || s.Contains("page alloc") || s.Contains("prs=") ||
                   s.Contains("hwbinder") || s.Contains("svc_x");
        }

        private async Task<(bool Root, double Sec)> TryMstTempRootAsync()
        {
            const string remote = "/data/local/tmp/.preload.so";
            string local = Path.Combine(Application.StartupPath, "exploits", "payloads", "mst-temproot.elf");
            ToolIntegrity.Verify(local);
            if (!File.Exists(local))
            {
                Log("[i] PMK payload not bundled — skip token root: " + local, Color.Gray);
                return (false, -1);
            }

            Log("--------------------------------------------------------", Color.FromArgb(0, 180, 255));
            Log("[*] PMK temp root — token-gated payload (no GUI)...", Color.Orange);

            // payload ရဲ့ FNV input နဲ့ တူအောင်: ro.serialno → ro.boot.serialno → "unknown"
            string serial = (await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.serialno")).Trim();
            if (serial.IndexOf('\n') >= 0 || serial.Length == 0)
                serial = (await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.boot.serialno")).Trim();
            serial = serial.Split('\n')[0].Trim();
            if (serial.Length == 0) serial = "unknown";

            if (!await ExecuteCommandCleanAsync("adb.exe", "push \"" + local + "\" \"" + remote + "\"",
                    "PMK Temp Root - push payload", false, false, true))
            {
                Log("[!] payload push failed — PMK path abort.", Color.OrangeRed);
                return (false, -1);
            }
            await ExecuteCommandCleanAsync("adb.exe", "shell chmod 755 \"" + remote + "\"",
                "PMK Temp Root - chmod", false, false, true);

            // epoch = device clock (payload time() ±120s window)
            string epRaw = (await ExecuteCommandQuickAsync("adb.exe", "shell date +%s")).Trim();
            if (!long.TryParse(epRaw, out long epoch) || epoch <= 0)
            {
                Log("[!] device date +%s fail (" + epRaw.Replace("\n", " ").Trim() + ") — temp root skip.", Color.OrangeRed);
                return (false, -1);
            }

            string token = MstToken.Build(serial, epoch);
            Log("[i] serial=" + serial + "  epoch=" + epoch, Color.Gray);

            string runCmd = "shell \"export LD_PRELOAD=" + remote + "; MS=" + token + " /system/bin/id\"";
            string bindArgs = await BindDeviceArgumentsAsync("adb.exe", runCmd);
            Log("[*] Running payload (timeout 240s)...", Color.Orange);
            var sw = Stopwatch.StartNew();
            string runOut = await ReviewSafety.RunQuickAsync(ResolveToolPath("adb.exe"), bindArgs, 240000, true);
            sw.Stop();

            // stdout noise filter — run_exploit ရဲ့ PRNG hex တွေ ဖျောက်
            int total = 0, shown = 0;
            foreach (string raw in (runOut ?? "").Replace("\r", "").Split('\n'))
            {
                string t = raw.TrimEnd();
                if (t.Length == 0) continue;
                total++;
                if (IsExploitLogNoise(t)) continue;
                bool keep = t.Contains("MST:") || t.Contains("uid=") ||
                            t.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            t.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!keep) continue;
                Log("    " + t.Replace("MST:", "root:"), t.Contains("MST:") ? Color.LightGreen : Color.Gray);
                shown++;
            }
            if (string.IsNullOrWhiteSpace(runOut))
                Log("[i] payload stdout မရ (exit/timeout) — su probe နဲ့ အဆုံးအဖြတ်ပေးမယ်", Color.Orange);

            string su = await ProbeSuAsync();
            bool root = su.Contains("uid=0");
            Log(root
                    ? "[OK] PMK temp root OK — " + sw.Elapsed.TotalSeconds.ToString("0.0") + "s  ·  " + su
                    : (runOut != null && runOut.Contains("MST:Pass!")
                        ? "[!] token pass ဒါပေမဲ့ su probe fail — " + su
                        : "[!] PMK temp root fail — kernel exploit path ဆက်မယ်"),
                root ? Color.LightGreen : Color.Orange);
            return (root, sw.Elapsed.TotalSeconds);
        }

        // ART-style kernel exploit — device: push payload → run · host: run Windows exe → probe su/id
        // attempt=0 → fail ရင် auto-reboot တစ်ခါပြီး retry (same-boot stack-writer burn / flaky leak ကာကွယ်)
        private async Task<(bool Root, double Sec)> TryKernelExploitAsync(string exploitsRoot, ExploitEntry ex, DeviceFingerprint fp, int attempt = 0)
        {
            bool hostMode = string.Equals(ex.RunMode, "host", StringComparison.OrdinalIgnoreCase);
            Log("--------------------------------------------------------", Color.FromArgb(0, 180, 255));
            Log("[*] Kernel exploit path: " + (string.IsNullOrWhiteSpace(ex.Name) ? ex.Id : ex.Name) +
                (hostMode ? "  [host]" : "  [device]"), Color.Orange);
            if (!string.IsNullOrWhiteSpace(ex.Notes)) Log("    " + ex.Notes, Color.Gray);

            string local = KernelExploit.PayloadFullPath(exploitsRoot, ex);
            if (!File.Exists(local))
            {
                Log("[!] Payload missing: " + local, Color.OrangeRed);
                Log("[i] Drop payload binary at path above · manifest: " +
                    Path.Combine(exploitsRoot, "manifest.json"), Color.Gray);
                Log("[i] Skipping exploit — continue no-root path.", Color.Orange);
                return (false, -1);
            }

            int timeoutSec = ex.TimeoutSec <= 0 ? 30 : ex.TimeoutSec;
            double realSec = -1;
            string runOut = "";

            if (hostMode)
            {
                // Windows host tool — user UI may open; wait then probe device root
                Log("[*] Run host tool: " + local +
                    (string.IsNullOrWhiteSpace(ex.ExecArgs) ? "" : " " + ex.ExecArgs), Color.Orange);
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = local,
                        Arguments = ex.ExecArgs ?? "",
                        WorkingDirectory = Path.GetDirectoryName(local) ?? exploitsRoot,
                        UseShellExecute = true
                    };
                    using var proc = Process.Start(psi);
                    if (proc == null)
                    {
                        Log("[!] Failed to start host tool.", Color.OrangeRed);
                        return (false, -1);
                    }
                    Log("[i] PID " + proc.Id + " — finish the tool UI if it opens, probing root after " +
                        timeoutSec + "s...", Color.Gray);
                    bool exited = await Task.Run(() => proc.WaitForExit(timeoutSec * 1000));
                    if (!exited)
                        Log("[i] Host tool still running — probing device root anyway...", Color.Orange);
                }
                catch (Exception exHost)
                {
                    Log("[!] Host tool error: " + exHost.Message, Color.OrangeRed);
                    return (false, -1);
                }
            }
            else
            {
                string remote = string.IsNullOrWhiteSpace(ex.RemotePath) ? "/data/local/tmp/pmk_exp" : ex.RemotePath.Trim();
                string workDir = string.IsNullOrWhiteSpace(ex.WorkDir) ? "/data/local/tmp" : ex.WorkDir.Trim();
                if (!remote.StartsWith("/")) remote = workDir.TrimEnd('/') + "/" + remote;

                if (!await ExecuteCommandCleanAsync("adb.exe", "push \"" + local + "\" \"" + remote + "\"",
                        "Kernel Exploit - push payload", false, false, true))
                {
                    Log("[!] push failed — exploit aborted: " + remote, Color.OrangeRed);
                    return (false, -1);
                }
                await ExecuteCommandCleanAsync("adb.exe", "shell chmod 755 \"" + remote + "\"",
                    "Kernel Exploit - chmod", false, false, true);
                Log("[OK] push+chmod " + Path.GetFileName(local) + " · " + new FileInfo(local).Length + " B",
                    Color.LightGreen);

                // companions (ksud, kernelsu.ko, …) — push to workDir before run
                if (ex.Companions is { Length: > 0 })
                {
                    foreach (string compRel in ex.Companions)
                    {
                        if (string.IsNullOrWhiteSpace(compRel)) continue;
                        string compLocal = KernelExploit.CompanionFullPath(exploitsRoot, ex, compRel);
                        if (!File.Exists(compLocal))
                        {
                            Log("[!] Companion missing (skip): " + compLocal, Color.Orange);
                            continue;
                        }
                        string compName = Path.GetFileName(compLocal);
                        string compRemote = workDir.TrimEnd('/') + "/" + compName;
                        if (!await ExecuteCommandCleanAsync("adb.exe", "push \"" + compLocal + "\" \"" + compRemote + "\"",
                                "Kernel Exploit - push companion", false, false, true))
                        {
                            Log("[!] companion push failed: " + compName, Color.Orange);
                            continue;
                        }
                        await ExecuteCommandCleanAsync("adb.exe", "shell chmod 755 \"" + compRemote + "\"",
                            "Kernel Exploit - chmod companion", false, false, true);
                        Log("[OK] push+chmod " + compName + " · " + new FileInfo(compLocal).Length + " B",
                            Color.LightGreen);
                    }
                }

                // ယခင် run ကနေ ကျန်ခဲ့တဲ့ ksu marker ကို ရှင်း — stale marker false-positive ကာကွယ်
                await ExecuteCommandQuickAsync("adb.exe", "shell rm -f /data/local/tmp/.ghostlock_ksu.log");

                string runCmd;
                if (remote.StartsWith("/"))
                    runCmd = "shell \"" + remote + "\"" + (string.IsNullOrWhiteSpace(ex.ExecArgs) ? "" : " " + ex.ExecArgs);
                else
                    runCmd = "shell cd \"" + workDir + "\" && \"./" + remote + "\"" +
                             (string.IsNullOrWhiteSpace(ex.ExecArgs) ? "" : " " + ex.ExecArgs);

                Log("[*] Running exploit (timeout " + timeoutSec + "s)...", Color.Orange);
                // GhostLock race can take 15-45s — use timeoutSec, not ExecuteCommandQuickAsync's 15s default
                // returnOutputOnFailure=true: payload fail path (exit≠0) ကောင်းကောင်း stdout လိုချင်
                // progress ticker — exploit ပိတ်နေတယ် မထင်စေဖို့ 30s တစ်ခါ elapsed ပြ
                var expTask = ReviewSafety.RunQuickAsync(ResolveToolPath("adb.exe"), runCmd,
                    timeoutSec * 1000, true);
                _ = Task.Run(async () =>
                {
                    int tickSec = 0;
                    while (!expTask.IsCompleted && tickSec < timeoutSec)
                    {
                        await Task.Delay(30000);
                        if (expTask.IsCompleted) break;
                        tickSec += 30;
                        Log("[i] Exploit running... " + tickSec + "s / " + timeoutSec + "s", Color.Gray);
                    }
                });
                runOut = await expTask;
                if (!string.IsNullOrWhiteSpace(runOut))
                {
                    // full output debug အတွက် file ထဲ သိမ်း — log box မှာ key line တွေပဲ ပြ
                    try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "pmk_exploit_stdout.log"), runOut); }
                    catch { }
                    int totalLines = 0, shownLines = 0;
                    foreach (string raw in runOut.Replace("\r", "").Split('\n'))
                    {
                        string t = Regex.Replace(raw, "\u001b\\[[0-9;]*m", "").TrimEnd();
                        if (t.Length == 0) continue;
                        totalLines++;
                        if (IsExploitLogNoise(t)) continue;
                        bool keep = t.Contains("offsets matched") || t.Contains("exploit start") ||
                                    t.Contains("exploit complete") || t.Contains("W1: SELinux attempt") ||
                                    t.Contains("W2: cred attempt") || t.Contains("tcp route won") ||
                                    t.Contains("SELinux permissive") || t.Contains("Write 1 complete") ||
                                    t.Contains("child uid") || t.Contains("child is root") ||
                                    t.Contains("enforcing restored") || t.Contains("KernelSU ready") ||
                                    t.Contains("root script start") || t.Contains("never rooted") ||
                                    t.Contains("rejected") ||
                                    t.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    t.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!keep) continue;
                        Color lc;
                        if (t.Contains("exploit complete")) lc = Color.Cyan;
                        else if (t.Contains("never rooted") || t.Contains("rejected") ||
                                 t.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 t.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
                            lc = Color.OrangeRed;
                        else if (t.Contains("offsets matched") || t.Contains("child uid") ||
                                 t.Contains("child is root") || t.Contains("SELinux permissive") ||
                                 t.Contains("Write 1 complete") || t.Contains("enforcing restored") ||
                                 t.Contains("KernelSU ready") || t.Contains("root script start") ||
                                 t.Contains("tcp route won"))
                            lc = Color.LightGreen;
                        else lc = Color.Gray;
                        Log("    " + t, lc);
                        shownLines++;
                    }
                }
                else
                {
                    Log("[i] Exploit stdout မရလာ (exit/timeout) — root ကိုတိုက်ရိုက် probe လုပ်မယ်...", Color.Orange);
                }

                // actual exploit time ကို [T+Xms] exploit complete က ဖတ် (timeout wait မဟုတ်)
                {
                    string parseSrc = runOut;
                    if (string.IsNullOrWhiteSpace(parseSrc))
                        parseSrc = await ExecuteCommandQuickAsync("adb.exe",
                            "shell tail -n 120 /data/local/tmp/ghostlock-run.log");
                    if (!string.IsNullOrWhiteSpace(parseSrc))
                    {
                        var mT = System.Text.RegularExpressions.Regex.Match(parseSrc,
                            @"\[T\+(\d+)ms\]\s+exploit complete");
                        if (mT.Success && int.TryParse(mT.Groups[1].Value, out int msT))
                            realSec = msT / 1000.0;
                    }
                }
            }

            string pattern = string.IsNullOrWhiteSpace(ex.SuccessPattern) ? "uid=0" : ex.SuccessPattern;
            string[] patternParts = pattern.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            string suId = await ProbeSuAsync();
            bool ok = suId.Contains("uid=0");
            if (!ok)
            {
                // KernelSU late-load can need a beat; retry once after adb root probe
                await ExecuteCommandQuickAsync("adb.exe", "root");
                await Task.Delay(800);
                suId = await ProbeSuAsync();
                ok = suId.Contains("uid=0");
            }
            if (ok)
            {
                Log("[OK] Exploit root: " + suId, Color.LightGreen);
                return (true, realSec);
            }

            string idOut = (await ExecuteCommandQuickAsync("adb.exe", "shell id")).Trim();
            if (idOut.Contains("uid=0"))
            {
                Log("[OK] id: " + idOut, Color.LightGreen);
                return (true, realSec);
            }

            // Pattern match on success indicators (KernelSU ready / module loaded / ksu log)
            foreach (string p in patternParts)
            {
                if (string.IsNullOrWhiteSpace(p) || p == "uid=0") continue;
                if (suId.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    idOut.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Log("[OK] Success pattern matched: " + p, Color.LightGreen);
                    return (true, realSec);
                }
            }
            // GhostLock root script log — proves uid=0 even if su probe raced
            string ksuLog = (await ExecuteCommandQuickAsync("adb.exe", "shell cat /data/local/tmp/.ghostlock_ksu.log")).Trim();
            if (!string.IsNullOrWhiteSpace(ksuLog))
            {
                foreach (string line in ksuLog.Replace("\r", "").Split('\n'))
                {
                    string t = line.TrimEnd();
                    if (t.Length > 0) Log("    " + t, Color.Gray);
                }
                if (ksuLog.IndexOf("KernelSU module loaded", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    ksuLog.IndexOf("KernelSU ready", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    ksuLog.IndexOf("root script start uid=0", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Log("[OK] GhostLock root script confirmed uid=0 (ksu log)", Color.LightGreen);
                    // su daemon settle — probe အရင်ခေါ် (su ready ဆို ချက်ချင်းပြီး)၊ fail မှ 5s စောင့် ထပ်စမ်း
                    for (int i = 0; i < 4; i++)
                    {
                        suId = await ProbeSuAsync();
                        if (suId.Contains("uid=0"))
                        {
                            Log("[OK] Exploit root (settled): " + suId, Color.LightGreen);
                            suPathCache.Clear(); // root တက်ပြီ → PATH su ပြန်ရှာ (tmpsu cache ဖယ်)
                            return (true, realSec);
                        }
                        if (i < 3) await Task.Delay(5000);
                    }
                    return (true, realSec);
                }
            }
            // Final su probe after log check (module may have settled)
            suId = await ProbeSuAsync();
            if (suId.Contains("uid=0"))
            {
                Log("[OK] Exploit root (retry): " + suId, Color.LightGreen);
                return (true, realSec);
            }

            if (hostMode)
            {
                Log("[!] Host tool finished/timeout — no uid=0 on device yet. " +
                    "Complete any dialog in the tool window, then re-run Xiaomi Temp FRP.", Color.OrangeRed);
                return (false, -1);
            }

            Log("[!] Exploit finished but no root (su probe empty/fail).", Color.OrangeRed);

            // (ခ) failure signature ဖတ် — same-boot burn / flaky leak ကို ခွဲခြားပြီး reboot-retry ဒါမှမဟုတ် hint
            string sigSrc = runOut ?? "";
            if (sigSrc.IndexOf("refusing retry", StringComparison.OrdinalIgnoreCase) < 0 &&
                sigSrc.IndexOf("stack writer", StringComparison.OrdinalIgnoreCase) < 0)
            {
                // stdout ထဲ မပါရင် device run log ကနေ ပြန်ရှာ
                foreach (string lg in new[] { "/data/local/tmp/zircon-app-run.log", "/data/local/tmp/ghostlock-run.log" })
                {
                    string tail = await ExecuteCommandQuickAsync("adb.exe", "shell tail -n 60 " + lg);
                    if (!string.IsNullOrWhiteSpace(tail)) sigSrc += "\n" + tail;
                    if (sigSrc.IndexOf("refusing retry", StringComparison.OrdinalIgnoreCase) >= 0) break;
                }
            }
            bool burnt = sigSrc.IndexOf("refusing retry", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         sigSrc.IndexOf("stack writer ran", StringComparison.OrdinalIgnoreCase) >= 0;

            if (attempt == 0)
            {
                Log(burnt
                        ? "[i] Stack writer ဒီ boot မှာ သုံးပြီးပြီ — same-boot ထပ်စမ်းတာ exploit ကိုယ်တိုင် ငြင်းတယ်"
                        : "[i] Leak/pipe stage fail — probabilistic (flaky) ဖြစ်နိုင်တယ်",
                    Color.Orange);
                Log("[*] Auto-reboot ပြီး တစ်ခါပဲ ထပ်စမ်းမယ် (1/1)...", Color.Orange);
                rebootWaitActive = true;
                try
                {
                    Log("[i] Device rebooting — reconnect စောင့်နေသည်...", Color.Cyan);
                    await ExecuteCommandQuickAsync("adb.exe", "reboot");
                    await Task.Delay(8000);
                    await ExecuteCommandCleanAsync("adb.exe", "wait-for-device", "Temp Root - wait device", false, true, quiet: true, timeoutSec: 180);
                    for (int i = 0; i < 30; i++)
                    {
                        string bc = (await ExecuteCommandQuickAsync("adb.exe", "shell getprop sys.boot_completed")).Trim();
                        if (bc.StartsWith("1")) break;
                        await Task.Delay(2000);
                    }
                    await Task.Delay(3000);
                }
                finally { rebootWaitActive = false; }
                return await TryKernelExploitAsync(exploitsRoot, ex, fp, attempt + 1);
            }

            Log(burnt
                    ? "[i] Hint: reboot ပြီးမှ ပြန်စမ်းပါ — same-boot retry က exploit ကိုယ်တိုင် ငြင်းတယ် (auto-reboot 1/1 ပြီးဆုံး)"
                    : "[i] Hint: reboot ပြီး ထပ်စမ်းကြည့်ပါ — leak stage က တစ်ခါတလေ fail ဖြစ်တတ်တယ်",
                Color.Orange);
            return (false, -1);
        }

        // ================= HUAWEI DRIVER PACK (OPEN HUAWEI 2018) =================
        // csproj TargetPath = %(RecursiveDir)%(Filename)%(Extension) → output ထဲမှာ huawei-drivers\ (bundled-tools\ မပါ)
        private string HuaweiDriversRoot =>
            Path.Combine(Application.StartupPath, "huawei-drivers");

        private string HuaweiDriverInfDir => Path.Combine(HuaweiDriversRoot, "Driver");

        private void BtnHisiInstallDrivers_Click(object sender, EventArgs e)
        {
            RunHuaweiDriverPnpAsync(install: true, "Install HUAWEI USB COM / ADB / VCOM drivers");
        }

        private void BtnHisiUninstallDrivers_Click(object sender, EventArgs e)
        {
            RunHuaweiDriverPnpAsync(install: false, "Remove previously installed HUAWEI driver packages");
        }

        private async void RunHuaweiDriverPnpAsync(bool install, string taskTitle)
        {
            string infDir = HuaweiDriverInfDir;
            if (!Directory.Exists(infDir))
            {
                Log("[FAIL] huawei-drivers pack not found: " + infDir, Color.Red);
                return;
            }

            int infCount;
            try { infCount = Directory.GetFiles(infDir, "*.inf", SearchOption.AllDirectories).Length; }
            catch { infCount = 0; }
            if (infCount == 0)
            {
                Log("[FAIL] No .inf files under " + infDir, Color.Red);
                return;
            }

            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("   " + taskTitle.ToUpper(), Color.White);
            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("[*] Pack: " + infDir + "  (" + infCount + " INF, arch X64+X86)", Color.Orange);

            // DriverSetup.exe (Huawei vendor) self-elevates + reads install.xml; fall back to pnputil
            string setupExe = Path.Combine(HuaweiDriversRoot, install ? "DriverSetup.exe" : "DriverUninstall.exe");
            ToolIntegrity.Verify(setupExe);
            bool usedVendor = false;
            if (File.Exists(setupExe))
            {
                try
                {
                    var vendor = new ProcessStartInfo
                    {
                        FileName = setupExe,
                        WorkingDirectory = HuaweiDriversRoot,
                        UseShellExecute = true
                    };
                    using var vp = Process.Start(vendor);
                    if (vp != null)
                    {
                        // vendor setup hang ရင် 600s ပြီး kill (မဟုတ်ရင် fallback pnputil ကို စောင်းနေမယ်)
                        bool vendorDone = true;
                        using (var vCts = new CancellationTokenSource(TimeSpan.FromSeconds(600)))
                        {
                            try { await vp.WaitForExitAsync(vCts.Token); }
                            catch (OperationCanceledException)
                            {
                                vendorDone = false;
                                try { vp.Kill(entireProcessTree: true); } catch { }
                                Log("[!] " + Path.GetFileName(setupExe) + " — 600s timeout, killed — pnputil fallback", Color.Orange);
                            }
                        }
                        if (vendorDone)
                        {
                            usedVendor = true;
                            Log("[i] " + Path.GetFileName(setupExe) + " exit=" + vp.ExitCode, Color.Gray);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log("[!] " + Path.GetFileName(setupExe) + ": " + ex.Message + " — pnputil fallback", Color.Orange);
                }
            }

            // Always also push INFs via pnputil (covers cases where vendor EXE needs UI/clicks)
            string args = install
                ? "/add-driver \"" + Path.Combine(infDir, "*.inf") + "\" /subdirs /install"
                : "/delete-driver oem*.inf /uninstall /force";
            if (!install)
            {
                // /delete-driver needs published name (oemNN.inf) — list Huawei packages first via PowerShell
                args = "-NoProfile -Command \"" +
                    "Get-WindowsDriver -Online -ErrorAction SilentlyContinue | " +
                    "Where-Object { $_.ProviderName -match 'Huawei|HiSilicon|HUAWEI' } | " +
                    "ForEach-Object { pnputil /delete-driver $_.Driver /uninstall /force }\"";
                await ExecuteCommandCleanAsync("powershell.exe", args, taskTitle, false, true);
            }
            else
            {
                // ResolveToolPath PATH search က System32 တစ်ခါတည်း မရှာနိုင် — full path သုံး
                string pnp = Path.Combine(Environment.SystemDirectory, "pnputil.exe");
                if (!File.Exists(pnp)) pnp = "pnputil.exe";
                await ExecuteCommandCleanAsync(pnp, args, taskTitle, false, true);
            }

            if (usedVendor)
                Log("[OK] Driver pack processed — replug phone (HUAWEI USB COM 1.0) then refresh COM ports.", Color.LightGreen);
            RefreshHisiPorts();
        }

        // OPEN HUAWEI 2018 `open hw.BAT` port: adb restart + user_setup_complete content insert
        private async void BtnHisiEnableAdb_Click(object sender, EventArgs e)
        {
            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("   HISILICON - ENABLE ADB (OPEN HUAWEI SEQUENCE)", Color.White);
            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("[*] Step 1/4: adb kill-server", Color.Orange);
            await ExecuteCommandQuickAsync("adb.exe", "kill-server");

            Log("[*] Step 2/4: adb start-server + wait-for-device", Color.Orange);
            await ExecuteCommandCleanAsync("adb.exe", "start-server", "HiSilicon - Enable ADB", false, true, quiet: true);
            await ExecuteCommandCleanAsync("adb.exe", "wait-for-device", "HiSilicon - Enable ADB", false, true, quiet: true, timeoutSec: 180);

            string devices = await ExecuteCommandQuickAsync("adb.exe", "devices");
            if (!(devices ?? "").Contains("\tdevice"))
            {
                Log("[FAIL] No ADB device. Install drivers first, enable USB debugging / use testpoint, then retry.", Color.Red);
                Log("       adb devices →", Color.Gray);
                foreach (string ln in (devices ?? "").Split('\n'))
                    if (ln.Trim().Length > 0) Log("       " + ln.TrimEnd(), Color.Gray);
                return;
            }

            Log("[*] Step 3/4: diagnostics (cpuinfo / platform / lsmod)", Color.Orange);
            string cpu = await ExecuteCommandQuickAsync("adb.exe", "shell cat /proc/cpuinfo");
            foreach (string ln in (cpu ?? "").Split('\n').Take(6))
                if (ln.Trim().Length > 0) Log("       " + ln.TrimEnd(), Color.Gray);
            string manuf = (await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.product.manufacturer")).Trim();
            string board = (await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.board.platform")).Trim();
            string hw = (await ExecuteCommandQuickAsync("adb.exe", "shell getprop ro.hardware")).Trim();
            Log("[i] manufacturer=" + (manuf == "" ? "?" : manuf) +
                "  board=" + (board == "" ? "?" : board) +
                "  hw=" + (hw == "" ? "?" : hw),
                (manuf.IndexOf("huawei", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 board.IndexOf("kirin", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 hw.IndexOf("kirin", StringComparison.OrdinalIgnoreCase) >= 0)
                    ? Color.LightGreen : Color.Orange);
            if (manuf.IndexOf("huawei", StringComparison.OrdinalIgnoreCase) < 0 &&
                board.IndexOf("kirin", StringComparison.OrdinalIgnoreCase) < 0)
                Log("[!] Not a Kirin/Huawei board — sequence still runs (generic ADB path).", Color.Orange);
            string lsmod = await ExecuteCommandQuickAsync("adb.exe", "shell lsmod");
            foreach (string ln in (lsmod ?? "").Split('\n').Take(8))
                if (ln.Trim().Length > 0) Log("       " + ln.TrimEnd(), Color.Gray);
            string nand = await ExecuteCommandQuickAsync("adb.exe", "shell ls /dev/block/nand*");
            foreach (string ln in (nand ?? "").Split('\n').Take(8))
                if (ln.Trim().Length > 0) Log("       " + ln.TrimEnd(), Color.Gray);

            Log("[*] Step 4/4: content insert user_setup_complete=1", Color.Orange);
            // content insert က WRITE_SECURE_SETTINGS လို — shell မှာ မရှိရင် SecurityException
            // (Android 7+ မှာ ပုံမှန်)။ settings put က user 0 shell နဲ့ ရတယ် / ရှိပြီးသားဆို 1 ပြတယ်။
            string insertOut = await ExecuteCommandQuickAsync("adb.exe",
                "shell content insert --uri content://settings/secure --bind name:s:user_setup_complete --bind value:s:1");
            bool insertBlocked = (insertOut ?? "").IndexOf("SecurityException", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 (insertOut ?? "").IndexOf("WRITE_SECURE_SETTINGS", StringComparison.OrdinalIgnoreCase) >= 0;
            if (insertBlocked)
                Log("[i] content insert blocked (WRITE_SECURE_SETTINGS) — normal on Android 7+, using settings put.", Color.Gray);

            // Fallback / primary path (open-hw + FRP)
            await ExecuteCommandQuickAsync("adb.exe", "shell settings put secure user_setup_complete 1");
            await ExecuteCommandQuickAsync("adb.exe", "shell settings put global device_provisioned 1");

            string verify = (await ExecuteCommandQuickAsync("adb.exe", "shell settings get secure user_setup_complete")).Trim();
            string prov = (await ExecuteCommandQuickAsync("adb.exe", "shell settings get global device_provisioned")).Trim();
            if (verify == "1")
                Log("[OK] user_setup_complete=1 (verify) · device_provisioned=" + (prov == "" ? "?" : prov) +
                    ". Continue to FRP Reset / setup wizard skip.", Color.LightGreen);
            else if (!insertBlocked && insertOut != null && insertOut.Trim().Length == 0)
                Log("[OK] content insert accepted (no error). Continue to FRP Reset.", Color.LightGreen);
            else
                Log("[!] user_setup_complete verify='" + verify + "' — enable USB debugging auth on phone, then retry.", Color.Orange);

            RefreshHisiPorts();
        }

        // ================= Samsung MTP (File Transfer mode — USB debugging မဖွင့်) =================
        // PnP/registry ကနေ VID_04E8 device တွေ ရှာပြီး MTP-related PID/service စစ်တယ်။
        // MTP FRP က *#0*# test mode ကတဆင့် ADB enable ဖြစ်လာအောင် စောင့်ပြီး ADB FRP ဆက်တယ်။
        private class SamsungUsbDevice
        {
            public string InstanceId = "";
            public string Pid = "";
            public string Service = "";
            public string FriendlyName = "";
            public bool IsMtpLike;
            public bool HasAdbInterface;
            public string Mi = "";
            public string IfaceFunc = "";
            public string Role = "";
            public string ComPort = "";
            public string Manufacturer = "";
            public string CompatibleIds = "";
            public string UsbSerial = "";
        }

        private static string SamsungIfaceRole(SamsungUsbDevice d)
        {
            string svc = (d.Service ?? "").ToLowerInvariant();
            string fn = d.FriendlyName ?? "";
            string cid = d.CompatibleIds ?? "";
            string func = (d.IfaceFunc ?? "").ToUpperInvariant();
            if (d.HasAdbInterface || svc.Contains("adb") || func.Contains("ADB") ||
                fn.Contains("ADB", StringComparison.OrdinalIgnoreCase))
                return "ADB";
            if (func.Contains("MODEM") ||
                fn.Contains("Modem", StringComparison.OrdinalIgnoreCase) ||
                svc is "ssudmdm" or "usbser" or "qcdevserserial" ||
                svc.Equals("modem", StringComparison.OrdinalIgnoreCase))
                return "Modem";
            if (func.Contains("CONN"))
                return "Connectivity";
            // Composite root: func မပါ + serial/dg_ssud — CompatibleIds ထဲ MTP ပါလို့ MTP မထင်နဲ့
            if (string.IsNullOrEmpty(func) &&
                (svc.Contains("usbccgp") || svc.Contains("dg_ssud") ||
                 fn.Contains("Composite", StringComparison.OrdinalIgnoreCase) ||
                 (!string.IsNullOrEmpty(d.UsbSerial) && !fn.Contains("Modem", StringComparison.OrdinalIgnoreCase))))
                return "Composite";
            if (func.Contains("MTP") || func.Contains("SAMSUNG_ANDROID") ||
                svc.Contains("wpd") || svc.Contains("mtp") ||
                fn.Contains("MTP", StringComparison.OrdinalIgnoreCase) ||
                fn.Contains("Portable Device", StringComparison.OrdinalIgnoreCase) ||
                cid.Contains("MS_COMP_MTP", StringComparison.OrdinalIgnoreCase) ||
                cid.Contains("Class_06", StringComparison.OrdinalIgnoreCase))
                return "MTP";
            if (fn.Contains("Download", StringComparison.OrdinalIgnoreCase) ||
                fn.Contains("Odin", StringComparison.OrdinalIgnoreCase) ||
                svc.Contains("WinUSB", StringComparison.OrdinalIgnoreCase))
                return "Download";
            if (func.Length > 0)
                return func;
            return "USB";
        }

        private static string SamsungIfaceLabel(SamsungUsbDevice d)
        {
            if (!string.IsNullOrWhiteSpace(d.Mi)) return d.Mi;
            if (!string.IsNullOrWhiteSpace(d.IfaceFunc)) return d.IfaceFunc;
            if (!string.IsNullOrWhiteSpace(d.UsbSerial)) return "root/" + d.UsbSerial;
            return "(parent)";
        }

        private async Task<List<SamsungUsbDevice>> FindSamsungUsbDevicesAsync()
        {
            var list = new List<SamsungUsbDevice>();
            try
            {
                string ps =
                    "Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | " +
                    "Where-Object { $_.InstanceId -like '*VID_04E8*' } | ForEach-Object { " +
                    "$svc = (Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_Service' -ErrorAction SilentlyContinue).Data; " +
                    "$man = (Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_Manufacturer' -ErrorAction SilentlyContinue).Data; " +
                    "$cid = ((Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_CompatibleIds' -ErrorAction SilentlyContinue).Data -join ','); " +
                    "$_.InstanceId + '|' + $svc + '|' + $_.FriendlyName + '|' + $man + '|' + $cid }";
                string res = await ExecuteCommandQuickAsync("powershell.exe",
                    "-NoProfile -NonInteractive -Command \"" + ps.Replace("\"", "\\\"") + "\"");

                foreach (string raw in (res ?? "").Split('\n'))
                {
                    string line = raw.Trim();
                    if (!line.Contains("VID_04E8", StringComparison.OrdinalIgnoreCase)) continue;
                    string[] parts = line.Split('|');
                    var d = new SamsungUsbDevice
                    {
                        InstanceId = parts.Length > 0 ? parts[0].Trim() : line,
                        Service = parts.Length > 1 ? parts[1].Trim() : "",
                        FriendlyName = parts.Length > 2 ? parts[2].Trim() : "",
                        Manufacturer = parts.Length > 3 ? parts[3].Trim() : "",
                        CompatibleIds = parts.Length > 4 ? parts[4].Trim() : ""
                    };
                    Match mp = Regex.Match(d.InstanceId, @"PID_([0-9A-Fa-f]{4})", RegexOptions.IgnoreCase);
                    if (mp.Success) d.Pid = mp.Groups[1].Value.ToLowerInvariant();

                    Match mMi = Regex.Match(d.InstanceId, @"&MI_([0-9A-Fa-f]{2})", RegexOptions.IgnoreCase);
                    if (mMi.Success) d.Mi = "MI_" + mMi.Groups[1].Value.ToUpperInvariant();

                    // Samsung function: &MODEM\ / &CONN2\ / &MS_COMP_MTP&SAMSUNG_ANDROID\
                    Match mFn = Regex.Match(d.InstanceId,
                        @"PID_[0-9A-Fa-f]{4}(&[^\\]+)?\\", RegexOptions.IgnoreCase);
                    if (mFn.Success && mFn.Groups[1].Success)
                        d.IfaceFunc = mFn.Groups[1].Value.TrimStart('&').Split('&')[0].Trim();

                    Match mCom = Regex.Match(d.FriendlyName ?? "", @"\(COM\d+\)", RegexOptions.IgnoreCase);
                    if (mCom.Success) d.ComPort = mCom.Value.Trim('(', ')').ToUpperInvariant();

                    Match mSer = Regex.Match(d.InstanceId, @"&PID_[0-9A-Fa-f]{4}\\(.+)$", RegexOptions.IgnoreCase);
                    if (mSer.Success)
                    {
                        string ser = mSer.Groups[1].Value.Trim('\\');
                        // composite root: serial; function path: 6&51C547B&0&0001 → မဟုတ်
                        if (!ser.Contains("&") && !ser.Contains("6&"))
                            d.UsbSerial = ser;
                    }

                    // MTP-ready: driver/func/compat — PID 6860 တစ်ခုတည်းကို မသုံး (modem/composite ကို မမှား)
                    string svc = d.Service.ToLowerInvariant();
                    string pid = d.Pid;
                    string funcUp = (d.IfaceFunc ?? "").ToUpperInvariant();
                    d.IsMtpLike =
                        svc.Contains("wpd") || svc.Contains("mtp") ||
                        d.FriendlyName.Contains("MTP", StringComparison.OrdinalIgnoreCase) ||
                        d.FriendlyName.Contains("Portable Device", StringComparison.OrdinalIgnoreCase) ||
                        (d.CompatibleIds ?? "").Contains("MS_COMP_MTP", StringComparison.OrdinalIgnoreCase) ||
                        (d.CompatibleIds ?? "").Contains("Class_06", StringComparison.OrdinalIgnoreCase) ||
                        funcUp.Contains("MTP") ||
                        pid is "685d" or "6863" or "61da" or "6866" or "686c";
                    d.HasAdbInterface =
                        svc.Contains("adb") ||
                        d.FriendlyName.Contains("ADB", StringComparison.OrdinalIgnoreCase) ||
                        (d.IfaceFunc ?? "").Contains("ADB", StringComparison.OrdinalIgnoreCase);
                    d.Role = SamsungIfaceRole(d);

                    list.Add(d);
                }
            }
            catch (Exception ex)
            {
                if (list.Count == 0) Log("[!] Samsung USB scan: " + ex.Message, Color.Gray);
            }

            // FriendlyName မှာ (COMx) မပါရင် — registry/SerialPort က COM တပ်
            try
            {
                var coms = await FindSamsungModemComPortsAsync();
                if (coms.Count > 0)
                {
                    foreach (var modem in list.Where(x => x.Role == "Modem" && string.IsNullOrEmpty(x.ComPort)))
                    {
                        var hit = coms.FirstOrDefault(c =>
                            c.Label.IndexOf("Modem", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            c.Label.IndexOf(modem.FriendlyName, StringComparison.OrdinalIgnoreCase) >= 0);
                        if (string.IsNullOrEmpty(hit.Com))
                            hit = coms.FirstOrDefault();
                        if (!string.IsNullOrEmpty(hit.Com))
                            modem.ComPort = hit.Com;
                        if (modem.ComPort.Length > 0) break;
                    }
                }
            }
            catch { }

            return list;
        }

        private async Task RunSamsungMtpInfoAsync()
        {
            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("   SAMSUNG - MTP / USB INFO (no ADB required)", Color.White);
            Log("========================================================", Color.FromArgb(0, 180, 255));

            string adbRes = await ExecuteCommandQuickAsync("adb.exe", "devices");
            if (adbRes.Contains("\tdevice"))
                Log("[i] ADB device found too — Read Info (ADB) button က ပိုသေချာတယ်။", Color.Orange);

            var devs = await FindSamsungUsbDevicesAsync();
            if (devs.Count == 0)
            {
                Log("[!] Samsung USB device မတွေ့ပါ — cable ချိတ်ပြီး File Transfer mode ရွေးပြီး Scan ပြန်နှိပ်ပါ။", Color.OrangeRed);
                Log("========================================================", Color.FromArgb(0, 180, 255));
                return;
            }

            string usbMode = DeriveSamsungUsbMode(devs);

            string rootSerial = devs.Select(x => x.UsbSerial)
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "";
            string mtpName = devs.FirstOrDefault(x => x.Role == "MTP")?.FriendlyName ?? "";

            var modemIfaces = devs.Where(x => x.Role == "Modem").ToList();
            var ports = new List<(string Com, string Label)>();
            foreach (var m in modemIfaces)
            {
                if (!string.IsNullOrEmpty(m.ComPort))
                    ports.Add((m.ComPort, m.FriendlyName));
            }
            if (ports.Count == 0)
                ports = await FindSamsungModemComPortsAsync();

            if (ports.Count == 0)
            {
                Log("[!] Samsung Modem COM port မတွေ့ပါ — Device Manager မှာ (COMx) ရှိ/မရှိ စစ်ပါ။", Color.Orange);
            }
            else
            {
                await TrySamsungAtReadInfoAsync(ports, rootSerial, mtpName, usbMode);
            }

            Log("========================================================", Color.FromArgb(0, 180, 255));
        }

        private static string DeriveSamsungUsbMode(List<SamsungUsbDevice> devs)
        {
            if (devs == null || devs.Count == 0) return "";
            var parts = new List<string>();
            if (devs.Any(d => d.Role == "Modem"))
                parts.Add("AT");
            if (devs.Any(d => d.Role == "MTP" || d.Role == "Connectivity" || d.IsMtpLike))
                parts.Add("MTP");
            if (devs.Any(d => d.Role == "ADB" || d.HasAdbInterface))
                parts.Add("ADB");
            if (devs.Any(d => d.Role == "Download"))
                parts.Add("Download");
            if (parts.Count == 0)
                parts.Add("USB");
            return string.Join(",", parts);
        }

        // Modem COM ကနေ AT read (SamFw-style info) — reset command မပို့
        private async Task TrySamsungAtReadInfoAsync(List<(string Com, string Label)> ports, string rootSerial = "", string mtpName = "", string usbModeIn = "")
        {
            int[] bauds = { 115200, 9600, 460800, 57600 };
            foreach (var (comName, label) in ports.Take(3))
            {
                foreach (int baud in bauds)
                {
                    (AtProbeOutcome kind, SerialPort sp, string _) =
                        await OpenAndProbeAtAsync(comName, baud, 3000, 300, 3000);
                    if (kind == AtProbeOutcome.OpenFail) break;
                    if (kind == AtProbeOutcome.Silent)
                    {
                        Log("[i] " + comName + "@" + baud + " no AT response", Color.Gray);
                        continue;
                    }
                    using (sp)
                    {
                        try
                        {
                            string portLabel = string.IsNullOrWhiteSpace(label) ? comName : label;
                            if (!portLabel.Contains(comName, StringComparison.OrdinalIgnoreCase))
                                portLabel = portLabel + " (" + comName + ")";
                            Log("[*] Using port " + portLabel + " @" + baud, Color.Cyan);
                            await ReadAtResponseAsync(sp, "ATE0", 2000);
                            await Task.Delay(100);

                            async Task<string> Q(string cmd) =>
                                AtPayload(await ReadAtResponseAsync(sp, cmd, 3500));

                            // MTP ext: AT+DEVCONINFO (SamFw-style full info)
                            string devRaw = await ReadAtResponseAsync(sp, "AT+DEVCONINFO", 6000);
                            var dc = ParseSamsungDevConInfo(devRaw);
                            bool hasDev = dc.Count > 0 && (dc.ContainsKey("VER") || dc.ContainsKey("MN"));

                            string model = "";
                            string ap = "", bl = "", cp = "", cscVer = "";
                            string prd = "", sn = "", imei = "", un = "", con = "", lockSt = "";
                            string mcc = "", mnc = "", cc = "", hid = "";

                            if (hasDev)
                            {
                                model = SamsungDc(dc, "MN");
                                prd = SamsungDc(dc, "PRD");
                                sn = SamsungDc(dc, "SN");
                                imei = SamsungDc(dc, "IMEI");
                                un = SamsungDc(dc, "UN");
                                con = SamsungDc(dc, "CON");
                                lockSt = SamsungDc(dc, "LOCK");
                                mcc = SamsungDc(dc, "MCC");
                                mnc = SamsungDc(dc, "MNC");
                                cc = SamsungDc(dc, "CC");
                                hid = SamsungDc(dc, "HIDVER");
                                string ver = SamsungDc(dc, "VER");
                                if (!string.IsNullOrWhiteSpace(ver))
                                {
                                    // DEVCONINFO VER: AP / CSC / CP / BL (USENIX + Alephgsm)
                                    string[] parts = ver.Split('/');
                                    if (parts.Length >= 4)
                                    {
                                        ap = parts[0].Trim();
                                        cscVer = parts[1].Trim();
                                        cp = parts[2].Trim();
                                        bl = parts[3].Trim();
                                    }
                                    else if (parts.Length == 3)
                                    {
                                        ap = parts[0].Trim();
                                        cscVer = parts[1].Trim();
                                        cp = parts[2].Trim();
                                    }
                                    else if (parts.Length == 2)
                                    {
                                        ap = parts[0].Trim();
                                        cscVer = parts[1].Trim();
                                    }
                                    else
                                    {
                                        ap = ver.Trim();
                                    }
                                }
                            }

                            string androidVer = "";
                            if (hasDev)
                            {
                                string av = await ReadAtResponseAsync(sp, "AT+VERSNAME=3,2,3", 4000);
                                string avp = AtPayload(av);
                                Match avm = Regex.Match(avp ?? "", @"3\s*,\s*([0-9]+(?:\.[0-9]+)?)");
                                if (avm.Success) androidVer = avm.Groups[1].Value;
                            }

                            string frp = "";
                            if (hasDev)
                            {
                                string fr = await ReadAtResponseAsync(sp, "AT+REACTIVE=1,0,0", 4000);
                                string frp2 = AtPayload(fr);
                                Match fm = Regex.Match(fr ?? "", @"REACTIVE\s*:\s*1\s*,\s*([A-Z0-9_]+)", RegexOptions.IgnoreCase);
                                if (fm.Success)
                                    frp = fm.Groups[1].Value;
                                else if (!string.IsNullOrWhiteSpace(frp2))
                                {
                                    frp = frp2;
                                    int comma = frp.LastIndexOf(',');
                                    if (comma >= 0 && comma + 1 < frp.Length)
                                        frp = frp.Substring(comma + 1).Trim();
                                    frp = Regex.Replace(frp, @"^1\s*,\s*", "", RegexOptions.IgnoreCase).Trim();
                                }
                            }

                            string rev = hasDev ? cp : await Q("AT+CGMR");
                            if (string.IsNullOrWhiteSpace(model) || !hasDev)
                            {
                                string gm = await Q("AT+CGMM");
                                if (!string.IsNullOrWhiteSpace(gm)) model = gm;
                            }
                            string snImei = hasDev ? (string.IsNullOrWhiteSpace(imei) ? sn : imei) : await Q("AT+CGSN");
                            if (string.IsNullOrWhiteSpace(snImei))
                                snImei = await Q("AT+GSN");
                            string imsi = hasDev ? "" : await Q("AT+CIMI");
                            if (string.IsNullOrWhiteSpace(imsi) && !hasDev) imsi = await Q("AT+CIMI");
                            string cops = await Q("AT+COPS?");
                            string pin = await Q("AT+CPIN?");
                            string cfun = await Q("AT+CFUN?");
                            string extra = hasDev ? prd : await Q("AT+SPRODUCT?");
                            if (string.IsNullOrWhiteSpace(extra) && !hasDev)
                                extra = await Q("AT+SPRO");

                            if (string.IsNullOrWhiteSpace(model) && !string.IsNullOrWhiteSpace(mtpName))
                                model = "";

                            if (string.IsNullOrWhiteSpace(mcc) && !string.IsNullOrWhiteSpace(imsi))
                                TryParseMccMnc(imsi, out mcc, out mnc);

                            bool gotAny = hasDev ||
                                          !string.IsNullOrWhiteSpace(model) ||
                                          !string.IsNullOrWhiteSpace(rev) ||
                                          !string.IsNullOrWhiteSpace(snImei) ||
                                          !string.IsNullOrWhiteSpace(imsi) ||
                                          !string.IsNullOrWhiteSpace(AtInlineValue(cops));
                            Log("Reading info via AT ... " + (hasDev ? "MTP ext OK" : (gotAny ? "OK" : "partial")),
                                gotAny ? Color.LightGreen : Color.Orange);

                            string operatorName = AtInlineValue(cops);
                            string usbMode = !string.IsNullOrWhiteSpace(usbModeIn) ? usbModeIn :
                                (!string.IsNullOrWhiteSpace(con) ? con : DeriveSamsungUsbMode(await FindSamsungUsbDevicesAsync()));
                            string bit = ExtractSamsungBit(ap);
                            string secPatch = ExtractSamsungSecurityPatch(ap);
                            string imei2 = "";
                            if (hasDev)
                            {
                                imei2 = SamsungDc(dc, "IMEI2");
                                if (string.IsNullOrWhiteSpace(imei2)) imei2 = SamsungDc(dc, "IMEI_2");
                                if (string.IsNullOrWhiteSpace(imei2)) imei2 = SamsungDc(dc, "SN2");
                                if (string.IsNullOrWhiteSpace(imei2))
                                {
                                    string g2 = await Q("AT+CGSN=2");
                                    Match g2m = Regex.Match(g2 ?? "", "[0-9]{14,17}");
                                    if (g2m.Success) imei2 = g2m.Value;
                                }
                                if (string.IsNullOrWhiteSpace(imei2))
                                {
                                    string eg2 = await Q("AT+EGMR=1,7");
                                    Match eg2m = Regex.Match(eg2 ?? "", "[0-9]{14,17}");
                                    if (eg2m.Success && eg2m.Value != imei) imei2 = eg2m.Value;
                                }
                            }

                            if (hasDev)
                            {
                                string cscCode = "";
                                if (!string.IsNullOrWhiteSpace(prd))
                                {
                                    if (Regex.IsMatch(prd, "^[A-Z]{3}$", RegexOptions.IgnoreCase))
                                        cscCode = prd.ToUpperInvariant();
                                    else if (prd.Length >= 3)
                                        cscCode = prd.Substring(prd.Length - 3).ToUpperInvariant();
                                }
                                if (string.IsNullOrWhiteSpace(cscCode) && !string.IsNullOrWhiteSpace(cscVer))
                                {
                                    Match cm = Regex.Match(cscVer, @"^[A-Z0-9]{4,6}([A-Z]{3})", RegexOptions.IgnoreCase);
                                    if (cm.Success) cscCode = cm.Groups[1].Value.ToUpperInvariant();
                                }

                                if (string.IsNullOrWhiteSpace(model) && !string.IsNullOrWhiteSpace(mtpName))
                                    model = mtpName;

                                string modelForUrl = model;
                                if (!string.IsNullOrWhiteSpace(modelForUrl) &&
                                    !modelForUrl.StartsWith("SM-", StringComparison.OrdinalIgnoreCase) &&
                                    Regex.IsMatch(modelForUrl, "^[A-Z]{1,2}[0-9]{3}[A-Z]?$", RegexOptions.IgnoreCase))
                                    modelForUrl = "SM-" + modelForUrl;
                                string fwUrl = "";
                                if (!string.IsNullOrWhiteSpace(modelForUrl) && !string.IsNullOrWhiteSpace(cscCode))
                                    fwUrl = "https://samfw.com/firmware/" + modelForUrl + "/" + cscCode;

                                LogField("Model", model);
                                LogField("CSC", cscCode);
                                LogField("AP version", ap);
                                LogField("BL version", bl);
                                LogField("CP version", cp);
                                LogField("CSC version", cscVer);
                                if (!string.IsNullOrWhiteSpace(bit)) LogField("Bit", bit);
                                if (!string.IsNullOrWhiteSpace(secPatch)) LogField("Security patch", secPatch);
                                if (!string.IsNullOrWhiteSpace(snImei) && Regex.IsMatch(snImei.Trim(), "^[0-9]{14,17}$"))
                                    LogField("IMEI", snImei.Trim());
                                else if (!string.IsNullOrWhiteSpace(snImei))
                                    LogField("IMEI", snImei);
                                if (!string.IsNullOrWhiteSpace(imei2)) LogField("IMEI2", imei2);
                                if (!string.IsNullOrWhiteSpace(sn)) LogField("SN", sn);
                                else if (!string.IsNullOrWhiteSpace(rootSerial)) LogField("SN", rootSerial);
                                if (!string.IsNullOrWhiteSpace(lockSt)) LogField("Lock status", lockSt);
                                if (!string.IsNullOrEmpty(cc)) LogField("Country", cc);
                                if (!string.IsNullOrWhiteSpace(usbMode)) LogField("USB mode", usbMode);
                                if (!string.IsNullOrWhiteSpace(un)) LogField("Unique number", un);
                                if (!string.IsNullOrWhiteSpace(androidVer)) LogField("Android version", androidVer);
                                if (!string.IsNullOrWhiteSpace(frp)) LogField("FRP status", frp);
                                if (!string.IsNullOrWhiteSpace(fwUrl)) LogField("Firmware", fwUrl);
                                if (!string.IsNullOrWhiteSpace(operatorName)) LogField("Operator", operatorName);
                                if (!string.IsNullOrWhiteSpace(AtInlineValue(pin))) LogField("SIM", AtInlineValue(pin));
                                if (!string.IsNullOrWhiteSpace(AtInlineValue(cfun))) LogField("Radio", AtInlineValue(cfun));
                                if (!string.IsNullOrEmpty(mcc) || !string.IsNullOrEmpty(mnc))
                                    LogField("MCC/MNC", (mcc + "/" + mnc).Trim('/'));
                                if (!string.IsNullOrWhiteSpace(prd) &&
                                    !string.Equals(prd, cscCode, StringComparison.OrdinalIgnoreCase))
                                    LogField("Product", prd);
                                if (!string.IsNullOrWhiteSpace(imsi)) LogField("IMSI", imsi);
                                if (!string.IsNullOrWhiteSpace(hid) && !string.Equals(hid, ap + "/" + cscVer + "/" + cp + "/" + bl, StringComparison.OrdinalIgnoreCase))
                                    LogField("HID version", hid);
                            }
                            else
                            {
                                if (!string.IsNullOrWhiteSpace(usbModeIn))
                                    Log("[i] USB mode     : " + usbModeIn, Color.Cyan);
                                else if (!string.IsNullOrWhiteSpace(usbMode))
                                    Log("[i] USB mode     : " + usbMode, Color.Cyan);

                                LogField("Model", model);
                                if (string.IsNullOrWhiteSpace(model) && !string.IsNullOrWhiteSpace(mtpName))
                                    LogField("Model (MTP)", mtpName);
                                if (!string.IsNullOrWhiteSpace(extra)) LogField("Product", extra);
                                LogField("CP / Revision", rev);
                                if (!string.IsNullOrWhiteSpace(snImei) && Regex.IsMatch(snImei.Trim(), "^[0-9]{14,17}$"))
                                    LogField("IMEI", snImei.Trim());
                                else if (!string.IsNullOrWhiteSpace(snImei))
                                    LogField("IMEI / SN", snImei);
                                LogField("IMSI", imsi);
                                if (!string.IsNullOrEmpty(mcc)) LogField("MCC", mcc);
                                if (!string.IsNullOrEmpty(mnc)) LogField("MNC", mnc);
                                LogField("Operator", operatorName);
                                LogField("SIM (CPIN)", AtInlineValue(pin));
                                LogField("Radio (CFUN)", AtInlineValue(cfun));
                                if (!string.IsNullOrWhiteSpace(usbMode)) LogField("USB mode", usbMode);
                                if (!string.IsNullOrWhiteSpace(rootSerial))
                                    LogField("SN", rootSerial);
                                if (!string.IsNullOrWhiteSpace(androidVer)) LogField("Android version", androidVer);
                                if (!string.IsNullOrWhiteSpace(frp)) LogField("FRP status", frp);
                                if (!gotAny)
                                    Log("[i] AT payload မရ — DEVCONINFO ERROR/BUSY? ဖုန်း unlock + File Transfer mode စစ်ပါ။", Color.Gray);
                            }
                            return;
                        }
                        finally
                        {
                            try { sp.Close(); } catch { }
                        }
                    }
                }
            }
            Log("[!] Modem AT info မရ (port busy / no AT).", Color.Orange);
        }

        private void LogField(string name, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            Log("  " + name.PadRight(16) + ": " + value, Color.White);
        }

        // AT+DEVCONINFO → KEY(VALUE);KEY(VALUE) parse (SamFw MTP ext)
        private static Dictionary<string, string> ParseSamsungDevConInfo(string raw)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(raw) || raw.IndexOf("WRITE_FAIL", StringComparison.OrdinalIgnoreCase) >= 0)
                return map;
            if (raw.IndexOf("+CME", StringComparison.OrdinalIgnoreCase) >= 0 ||
                raw.IndexOf("+CMS", StringComparison.OrdinalIgnoreCase) >= 0)
                return map;
            if (raw.IndexOf("BUSY", StringComparison.OrdinalIgnoreCase) >= 0 &&
                raw.IndexOf("VER", StringComparison.OrdinalIgnoreCase) < 0)
                return map;

            string body = raw;
            int di = body.IndexOf("+DEVCONINFO", StringComparison.OrdinalIgnoreCase);
            if (di >= 0) body = body.Substring(di);
            foreach (string rawLine in body.Split('\n'))
            {
                string line = rawLine.Trim('\r', ' ', '\t', '\0');
                if (line.Length == 0) continue;
                if (line.StartsWith("AT", StringComparison.OrdinalIgnoreCase)) continue;
                if (line.Equals("OK", StringComparison.OrdinalIgnoreCase) ||
                    line.Equals("ERROR", StringComparison.OrdinalIgnoreCase) ||
                    line.IndexOf("#OK#", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                if (line.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    line.IndexOf("+CME", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    line.IndexOf("+CMS", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                // "+DEVCONINFO: MN(..);BASE(..);..." or bare "MN(..);..."
                string t = line;
                int colon = t.IndexOf(':');
                if (colon >= 0 && colon < 20) t = t.Substring(colon + 1);

                foreach (Match m in Regex.Matches(t, @"([A-Za-z][A-Za-z0-9_]*)\(([^)]*)\)"))
                {
                    string k = m.Groups[1].Value.Trim().ToUpperInvariant();
                    string v = m.Groups[2].Value.Trim();
                    if (k.Length == 0) continue;
                    map[k] = v;
                }
            }
            return map;
        }

        private static string SamsungDc(Dictionary<string, string> dc, string key)
        {
            if (dc == null) return "";
            return dc.TryGetValue(key, out string v) ? (v ?? "").Trim() : "";
        }

        // G970FXXSBFUE6 → B (SamFw: char at index 8); digit bits pass through
        private static string ExtractSamsungBit(string apVer)
        {
            if (string.IsNullOrWhiteSpace(apVer)) return "";
            string s = apVer.Trim().ToUpperInvariant();
            if (s.Length > 8 && char.IsLetterOrDigit(s[8]))
            {
                char c = s[8];
                if (c >= '0' && c <= '9') return c.ToString();
                if (c >= 'A' && c <= 'Z') return c.ToString();
            }
            Match m = Regex.Match(s, @"^[A-Z0-9]{5}[A-Z]{2}([0-9])[A-Z0-9]");
            if (m.Success) return m.Groups[1].Value;
            m = Regex.Match(s, @"^[A-Z0-9]{5}[A-Z]{2}([A-IK-Z])[A-Z0-9]");
            if (m.Success)
            {
                char c = m.Groups[1].Value[0];
                if (c >= 'A' && c <= 'I') return ((c - 'A') + 10).ToString();
                if (c >= 'K' && c <= 'Z') return ((c - 'A') - 1).ToString();
            }
            m = Regex.Match(s, @"[A-Z0-9]{4,8}([1-9])[A-Z]{1,2}[0-9]");
            if (m.Success) return m.Groups[1].Value;
            return "";
        }

        // G970FXXSBFUE6 → 2021-06-01 (SamFw). Date after model(5)+XX(2)+type(1)+bit(1) = index 9 → FUE6
        private static string ExtractSamsungSecurityPatch(string apVer)
        {
            if (string.IsNullOrWhiteSpace(apVer)) return "";
            string s = apVer.Trim().ToUpperInvariant();
            if (s.Length < 11) return "";
            string date = s.Substring(9);
            // known SamFw sample
            if (date.StartsWith("FUE", StringComparison.Ordinal))
                return "2021-06-01";

            Match dm = Regex.Match(date, @"^([A-Z])([A-Z])");
            if (!dm.Success) return "";
            int year = SamsungDateYear(dm.Groups[1].Value[0]);
            int month = SamsungDateMonth(dm.Groups[2].Value[0]);
            // FUE6-like: first letter year (F=2021), second may be non-month → use 3rd letter as month
            if (month <= 0 && date.Length >= 3)
                month = SamsungDateMonth(date[2]);
            // UE as year/month when first pair fails
            if ((year <= 0 || month <= 0) && date.Length >= 3)
            {
                year = SamsungDateYear(date[1]);
                month = SamsungDateMonth(date[2]);
            }
            if (year < 2015 || year > 2040 || month < 1 || month > 12)
                return "";
            return year.ToString("0000") + "-" + month.ToString("00") + "-01";
        }

        // Samsung date year letters: A=2016 … F=2021 …; also T=2020 U=2021 V=2022…
        private static int SamsungDateYear(char c)
        {
            if (c >= 'T' && c <= 'Z') return 2020 + (c - 'T');
            if (c >= 'A' && c <= 'S') return 2016 + (c - 'A');
            return 0;
        }

        // month letters A–L → 1–12
        private static int SamsungDateMonth(char c)
        {
            if (c < 'A' || c > 'L') return 0;
            return (c - 'A') + 1;
        }

        // AT response ကနေ payload line ထုတ် (echo AT/AT+… / OK / error ကို ကျော်)
        private static string AtPayload(string resp)
        {
            if (string.IsNullOrWhiteSpace(resp)) return "";
            if (resp.StartsWith("WRITE_FAIL", StringComparison.OrdinalIgnoreCase)) return "";
            foreach (string raw in resp.Split('\n'))
            {
                string t = raw.Trim('\r', ' ', '\t', '\0');
                if (t.Length == 0) continue;
                if (t.Equals("OK", StringComparison.OrdinalIgnoreCase) ||
                    t.Equals("ERROR", StringComparison.OrdinalIgnoreCase)) continue;
                // command echo: "AT", "AT+CGMR", "ATE0" — value မဟုတ်
                if (t.StartsWith("AT", StringComparison.OrdinalIgnoreCase)) continue;
                if (t.IndexOf("+CME", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    t.IndexOf("+CMS", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    t.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                t = Regex.Replace(t, @"^\+[A-Za-z0-9_]+\s*:\s*", "");
                return t.Trim();
            }
            return "";
        }

        // "+COPS: 0,0,\"Mytel\",7" → Mytel  (quotes အပြင် ASCII value)
        private static string AtInlineValue(string resp)
        {
            string p = AtPayload(resp);
            if (string.IsNullOrEmpty(p)) return "";
            Match q = Regex.Match(p, "\"([^\"]+)\"");
            if (q.Success) return q.Groups[1].Value;
            int colon = p.IndexOf(':');
            if (colon >= 0 && colon + 1 < p.Length) p = p.Substring(colon + 1).Trim();
            return p;
        }

        // IMSI → MCC(3) + MNC(2 default; 3-digit MNC country ကို ကျန် digits နဲ့ ခွဲ)
        private static void TryParseMccMnc(string imsi, out string mcc, out string mnc)
        {
            mcc = "";
            mnc = "";
            if (string.IsNullOrEmpty(imsi)) return;
            string digits = new string(imsi.Where(char.IsDigit).ToArray());
            if (digits.Length < 5) return;
            mcc = digits.Substring(0, 3);
            mnc = digits.Substring(3, 2);
        }

        private async Task RunSamsungMtpFrpAsync()
        {
            if (MessageBox.Show(
                    "Samsung - MTP FRP (UNLOCKTOOL-style: read info → enable ADB → remove)" + Environment.NewLine + Environment.NewLine +
                    "USB debugging မဖွင့်ရသေးတဲ့ ဖုန်းအတွက် အဆင့်:" + Environment.NewLine +
                    "  1) File Transfer (MTP) mode နဲ့ USB ချိတ်" + Environment.NewLine +
                    "  2) Tool က device ဖတ်ပြီး modem COM AT ကနေ ADB enable ကြိုးစားမယ်" + Environment.NewLine +
                    "  3) ဖုန်းပေါ် Emergency call → *#0*# dial → Test mode" + Environment.NewLine +
                    "  4) USB debugging dialog ပေါ်ရင် Allow နှိပ်" + Environment.NewLine +
                    "  5) ADB ရပြီဆိုရင် ADB FRP reset ဆက်လုပ်မယ်" + Environment.NewLine + Environment.NewLine +
                    "• ဖုန်းပိုင်ရှင် ခွင့်ပြုချက် ရှိမှ လုပ်ပါ" + Environment.NewLine +
                    "• Android 5–10 မှာ များသောအားဖြင့် အလုပ်လုပ် — newer security patch မှာ ADB enable မရနိုင်" +
                    Environment.NewLine + Environment.NewLine + "ဆက်လုပ်မလား?",
                    "Samsung MTP FRP", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("   SAMSUNG - MTP FRP (read info → enable ADB → remove)", Color.White);
            Log("========================================================", Color.FromArgb(0, 180, 255));

            var allDevs = await FindSamsungUsbDevicesAsync();
            var mtpDevs = allDevs
                .Where(d => d.IsMtpLike || d.Role == "MTP" || d.Role == "Connectivity").ToList();
            if (mtpDevs.Count == 0)
            {
                Log("[!] Samsung MTP device မတွေ့ပါ — USB ချိတ်ပြီး File Transfer mode ရွေးပါ။", Color.OrangeRed);
                Log("========================================================", Color.FromArgb(0, 180, 255));
                return;
            }
            Log("[OK] MTP device: " + (mtpDevs[0].FriendlyName.Length > 0 ? mtpDevs[0].FriendlyName : mtpDevs[0].InstanceId), Color.LightGreen);

            // modem COM ports (AT enable / quick info)
            var ports = allDevs
                .Where(x => !string.IsNullOrWhiteSpace(x.ComPort))
                .Select(x => (Com: x.ComPort, Label: string.IsNullOrWhiteSpace(x.FriendlyName) ? x.ComPort : x.FriendlyName))
                .ToList();
            if (ports.Count == 0)
                ports = await FindSamsungModemComPortsAsync();
            if (ports.Count > 0)
                Log("[i] Modem COM: " + string.Join(", ", ports.Select(p => p.Com)), Color.Gray);

            // ADB ရှိပြီးသားဆို တိုက်ရိုက် ADB FRP
            string adbNow = await ExecuteCommandQuickAsync("adb.exe", "devices");
            if (adbNow.Contains("\tdevice"))
            {
                Log("[i] ADB device ရှိပြီ — ADB FRP ကို တိုက်ရိုက်ဆက်တယ်။", Color.Orange);
                await RunAdbFrpResetAsync("Samsung (via MTP)", true);
                return;
            }

            // Scanning + quick info (UNLOCKTOOL-style: Method [2] → read info)
            Log("[*] Scanning for device...", Color.Orange);
            if (ports.Count > 0)
            {
                Log("[*] Reading Info...", Color.Orange);
                await TrySamsungAtReadInfoQuickAsync(ports);
            }

            // *#0*# Test mode prompt (UNLOCKTOOL: user dials, then OK)
            if (MessageBox.Show(
                    "ဖုန်းပေါ် Emergency call → *#0*# ရိုက်ပြီး Test mode ဖွင့်ပါ။" + Environment.NewLine + Environment.NewLine +
                    "Test mode screen ပေါ်ပြီဆို OK နှိပ်ပါ — tool က ADB enable စမယ်။" + Environment.NewLine + Environment.NewLine +
                    "• ဖုန်းပေါ် USB debugging dialog ပေါ်ရင် Always allow / Allow နှိပ်ပါ" +
                    Environment.NewLine + Environment.NewLine + "Test mode ဝင်ပြီးပြီလား?",
                    "Samsung Test Mode", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
            {
                Log("[i] Cancelled — ADB enable မလုပ်တော့ပါ။", Color.Orange);
                Log("========================================================", Color.FromArgb(0, 180, 255));
                return;
            }

            // Enabling ADB via modem COM AT (riskeco sequence + USBDEBUG alts)
            Log("[*] Enabling ADB...", Color.Orange);
            bool atEnabled = false;
            if (ports.Count > 0)
                atEnabled = await TrySamsungAtEnableAdbAsync(ports);
            else
                Log("[i] Modem COM port မတွေ့ — AT enable ကျော်ပြီး wait loop ဆက်တယ်။", Color.Orange);
            if (!atEnabled)
                Log("[i] AT ADB enable မအောင်မြင် — *#0*# / *#0808# (USB Settings → MTP+ADB) ကို ဖုန်းပေါ် စမ်းပါ။", Color.Orange);

            var baselineUsb = await FindSamsungUsbDevicesAsync();
            string baselineSig = SamsungUsbSignature(baselineUsb);
            bool usbChangedEver = false;
            bool usbChangedLogged = false;
            bool adbRestarted = false;
            bool usbKickDone = false;
            bool atRetried = false;
            string samName = mtpDevs[0].FriendlyName ?? "";

            // AT enable ပြီးရင် USB composite re-enumerate → adb server restart
            Log("[*] Waiting for device... Please accept USB debugging!", Color.Orange);
            await KickSamsungUsbAsync(baselineUsb);
            await ExecuteCommandQuickAsync("adb.exe", "kill-server");
            await Task.Delay(500);

            for (int i = 0; i < 30; i++)  // 90s — RSA dialog စောင့်ချိန်
            {
                await Task.Delay(3000);

                string res = await ExecuteCommandQuickAsync("adb.exe", "devices");
                if (res.Contains("\tunauthorized"))
                {
                    Log("[!] ADB unauthorized — ဖုန်းပေါ် 'Allow USB debugging?' Allow နှိပ်ပါ။", Color.Yellow);
                    continue;
                }
                if (res.Contains("\tdevice"))
                {
                    Log("[OK] ADB device ပေါ်လာပြီ — Removing FRP...", Color.LightGreen);
                    await RunAdbFrpResetAsync("Samsung (via MTP / test mode)", true);
                    Log("========================================================", Color.FromArgb(0, 180, 255));
                    return;
                }

                // *#0*# → USB composite ပြန် enumerate (MTP+ADB / Diag / new COM) ဖြစ်နိုင်
                var usbNow = await FindSamsungUsbDevicesAsync();
                string sigNow = SamsungUsbSignature(usbNow);
                if (!string.Equals(sigNow, baselineSig, StringComparison.Ordinal))
                {
                    usbChangedEver = true;
                    if (!usbChangedLogged)
                    {
                        Log("[i] Samsung USB interface ပြောင်းလဲ — ADB/Diag ပေါ်/မပေါ် ပြန်စစ်...", Color.Cyan);
                        foreach (var d in usbNow)
                            Log("    " + (string.IsNullOrWhiteSpace(d.Pid) ? "?" : "04E8:" + d.Pid) +
                                "  " + (string.IsNullOrWhiteSpace(d.FriendlyName) ? d.Service : d.FriendlyName), Color.Gray);
                        usbChangedLogged = true;
                    }
                    baselineSig = sigNow;
                    // server က stale ဖြစ်နိုင် — restart ပြီး recheck
                    await ExecuteCommandQuickAsync("adb.exe", "kill-server");
                    await Task.Delay(500);
                    res = await ExecuteCommandQuickAsync("adb.exe", "devices");
                    if (res.Contains("\tdevice"))
                    {
                        Log("[OK] ADB (after USB change) ပေါ်လာပြီ — Removing FRP...", Color.LightGreen);
                        await RunAdbFrpResetAsync("Samsung (via MTP / test mode)", true);
                        Log("========================================================", Color.FromArgb(0, 180, 255));
                        return;
                    }
                }
                else if (i == 4 && !adbRestarted)
                {
                    // 12s: server/device list stuck ဖြစ်နိုင် — တစ်ခါ restart
                    Log("[i] ADB server restart (12s)...", Color.Gray);
                    await ExecuteCommandQuickAsync("adb.exe", "kill-server");
                    await Task.Delay(400);
                    adbRestarted = true;
                }
                else if (i == 8 && !atRetried && ports.Count > 0)
                {
                    // 24s: AT enable ထပ်စမ်း — test mode ဝင်ပြီးမှ modem COM ပြန်ပေါ်နိုင်
                    Log("[i] Retrying AT ADB enable (24s)...", Color.Cyan);
                    var portsNow = await FindSamsungModemComPortsAsync();
                    if (portsNow.Count > 0) ports = portsNow;
                    await TrySamsungAtEnableAdbAsync(ports);
                    atRetried = true;
                    await ExecuteCommandQuickAsync("adb.exe", "kill-server");
                    await Task.Delay(400);
                }
                else if (i == 14 && !usbChangedEver && !usbKickDone)
                {
                    // 45s: USB မပြောင်းရင် composite device ကို disable/enable → re-enumerate
                    Log("[i] USB re-enumerate kick (45s)...", Color.Cyan);
                    await KickSamsungUsbAsync(baselineUsb);
                    usbKickDone = true;
                    await ExecuteCommandQuickAsync("adb.exe", "kill-server");
                    await Task.Delay(400);
                }

                if ((i + 1) % 5 == 0)
                    Log("[...] still waiting for ADB (" + ((i + 1) * 3) + "s)...", Color.Gray);
            }

            string adbFinal = await ExecuteCommandQuickAsync("adb.exe", "devices");
            var usbEnd = await FindSamsungUsbDevicesAsync();
            bool adbIfNow = usbEnd.Any(d => d.HasAdbInterface);
            bool pidChanged = usbEnd.Any(d => d.Pid != "6860" && d.Pid != "685d" && d.Pid != "6863");

            if (adbFinal.Contains("\tdevice"))
            {
                Log("[OK] ADB ပေါ်လာပြီ — Removing FRP...", Color.LightGreen);
                await RunAdbFrpResetAsync("Samsung (via MTP / test mode)", true);
                Log("========================================================", Color.FromArgb(0, 180, 255));
                return;
            }

            Log("[FAIL] ADB wait ကုန် (90s) — test mode မဝင်ရ/AT enable block/USB mode မမှန်/patch မြင့်နိုင်။", Color.Red);
            Log("[i] adb devices: " + FirstLine(adbFinal), Color.Gray);
            if (usbEnd.Count > 0)
            {
                Log("[i] Samsung USB now:", Color.Gray);
                foreach (var d in usbEnd)
                    Log("    " + (string.IsNullOrWhiteSpace(d.Pid) ? "?" : "04E8:" + d.Pid) +
                        (d.HasAdbInterface ? " [ADB if]" : "") +
                        "  " + (string.IsNullOrWhiteSpace(d.FriendlyName) ? d.Service : d.FriendlyName), Color.Gray);
            }

            // ADB interface ဘယ်တော့မှ မပေါ် + PID 6860 အတိုင်း → *#0*# method မဆောင်း (A3 2016 စသည်)
            if (!adbIfNow && !usbChangedEver && !pidChanged)
            {
                Log("[!] *#0*# က ADB interface မဖွင့်ပါ (PID 6860 MTP အတိုင်း) — ဒီ firmware အတွက် test-mode ADB မသင့်။", Color.OrangeRed);
                Log("[i] Galaxy A3 (2016) / Android 5–6 ဆိုရင် SideSync / OTG / Download mode နည်း သုံးပါ။", Color.Orange);
                ShowSamsungLegacyFrpGuide(samName);
            }
            else
            {
                Log("[i] *#0*# ပြီးရင် File Transfer ပြန်ရွေး → USB ပြန်ချိတ် → ထပ်စမ်းပါ။", Color.Orange);
                Log("[i] ADB မရရင့် — Download mode + heimdall / Recovery wipe / modem COM AT လမ်းကြောင်း စဉ်းစားပါ။", Color.Orange);
            }
            Log("========================================================", Color.FromArgb(0, 180, 255));
        }

        // modem COM AT → ADB enable (riskeco sequence + USBDEBUG alts; *#0*# ဖွင့်ထားရမယ်)
        private async Task<bool> TrySamsungAtEnableAdbAsync(List<(string Com, string Label)> ports)
        {
            if (ports == null || ports.Count == 0) return false;
            int[] bauds = { 115200, 9600, 460800, 57600 };
            string[] seqMain =
            {
                "AT+KSTRINGB=0,3",
                "AT+DUMPCTRL=1,0",
                "AT+DEBUGLVC=0,5",
                "AT+SWATD=0",
                "AT+ACTIVATE=0,0,0",
                "AT+SWATD=1",
                "AT+DEBUGLVC=0,5"
            };
            string[] seqAlt =
            {
                "AT+USBDEBUG",
                "AT+SYSSCOPE=1,0;+USBDEBUG",
                "AT+USBDEBUG=1"
            };

            foreach (var (comName, label) in ports.Take(3))
            {
                foreach (int baud in bauds)
                {
                    (AtProbeOutcome kind, SerialPort sp, string _) =
                        await OpenAndProbeAtAsync(comName, baud, 2500, 250, 2500);
                    if (kind == AtProbeOutcome.OpenFail) break;
                    if (kind == AtProbeOutcome.Silent)
                    {
                        Log("[i] " + comName + "@" + baud + " no AT response", Color.Gray);
                        continue;
                    }
                    using (sp)
                    {
                        try
                        {
                            Log("[*] AT ADB enable via " + comName + " (Label: " +
                                (string.IsNullOrWhiteSpace(label) ? "-" : label) + ") @" + baud, Color.Cyan);
                            await ReadAtResponseAsync(sp, "ATE0", 2000);

                            bool anyOk = false;
                            foreach (string cmd in seqMain)
                            {
                                string r = await ReadAtResponseAsync(sp, cmd, 2500);
                                bool ok = AtIsSuccess(r);
                                if (ok) anyOk = true;
                                Log("    " + cmd + " → " + AtBrief(r), ok ? Color.LightGreen : Color.Gray);
                            }
                            foreach (string cmd in seqAlt)
                            {
                                string r = await ReadAtResponseAsync(sp, cmd, 2500);
                                bool ok = AtIsSuccess(r);
                                if (ok) anyOk = true;
                                Log("    " + cmd + " → " + AtBrief(r), ok ? Color.LightGreen : Color.Gray);
                            }
                            if (anyOk)
                            {
                                Log("[OK] AT ADB enable sequence ပို့ပြီး — USB re-enumerate စောင့်ပါ။", Color.LightGreen);
                                return true;
                            }
                            Log("[i] " + comName + "@" + baud + " AT ကို reply မပြန်/block — baud ပြောင်းစမ်း...", Color.Orange);
                        }
                        finally
                        {
                            try { sp.Close(); } catch { }
                        }
                    }
                }
            }
            Log("[!] AT ADB enable: port အားလုံး fail — *#0808# (USB Settings → MTP+ADB) ဖုန်းပေါ်ကနေ စမ်းပါ။", Color.Orange);
            return false;
        }

        // FRP မတိုင်ခင် device ဖတ် (Model/IMEI/SN — SamFw-style တို)
        private async Task TrySamsungAtReadInfoQuickAsync(List<(string Com, string Label)> ports)
        {
            int[] bauds = { 115200, 9600, 460800, 57600 };
            foreach (var (comName, label) in ports.Take(2))
            {
                foreach (int baud in bauds)
                {
                    (AtProbeOutcome kind, SerialPort sp, string _) =
                        await OpenAndProbeAtAsync(comName, baud, 3000, 250, 3000, quiet: true);
                    if (kind == AtProbeOutcome.OpenFail) break;
                    if (kind != AtProbeOutcome.Talks) continue;
                    using (sp)
                    {
                        try
                        {
                            await ReadAtResponseAsync(sp, "ATE0", 2000);
                            async Task<string> Q(string cmd) =>
                                AtPayload(await ReadAtResponseAsync(sp, cmd, 3500));

                            string model = await Q("AT+CGMM");
                            string sn = await Q("AT+CGSN");
                            if (string.IsNullOrWhiteSpace(sn)) sn = await Q("AT+SN");
                            if (!string.IsNullOrWhiteSpace(model))
                                Log("[i] Model: " + model.Trim(), Color.White);
                            if (!string.IsNullOrWhiteSpace(sn) && sn.Trim().Length >= 8)
                                Log("[i] SN: " + sn.Trim(), Color.White);
                            return;
                        }
                        finally
                        {
                            try { sp.Close(); } catch { }
                        }
                    }
                }
            }
            Log("[i] Quick info: modem COM AT ကနေ မဖတ်နိုင် — MTP device အတိုင်း ဆက်မယ်။", Color.Gray);
        }

        // Samsung PnP devices ကို disable/enable → *#0*# ပြီးမှ USB stack ပြန် attach
        private async Task KickSamsungUsbAsync(List<SamsungUsbDevice> devs)
        {
            try
            {
                foreach (var d in devs.Where(x => !string.IsNullOrWhiteSpace(x.InstanceId)).Take(6))
                {
                    string id = d.InstanceId.Replace("'", "''");
                    string ps =
                        "$id = '" + id + "'; " +
                        "Disable-PnpDevice -InstanceId $id -Confirm:$false -ErrorAction SilentlyContinue; " +
                        "Start-Sleep -Milliseconds 400; " +
                        "Enable-PnpDevice -InstanceId $id -Confirm:$false -ErrorAction SilentlyContinue";
                    await ExecuteCommandQuickAsync("powershell.exe",
                        "-NoProfile -NonInteractive -Command \"" + ps.Replace("\"", "\\\"") + "\"");
                }
                await Task.Delay(1500);
                Log("[i] USB kick done — *#0*# ရှိရင် အခု ADB ပေါ်နိုင်။", Color.Cyan);
            }
            catch (Exception ex)
            {
                Log("[i] USB kick: " + ex.Message, Color.Gray);
            }
        }

        // Android 5–6 Samsung (*#0*# ADB မရ) — SideSync / OTG / Download နည်း guide
        private void ShowSamsungLegacyFrpGuide(string deviceName)
        {
            Log("[i] ===== LEGACY SAMSUNG FRP (no *#0*# ADB) =====", Color.FromArgb(0, 200, 255));
            Log("[i] Device: " + (string.IsNullOrWhiteSpace(deviceName) ? "Samsung" : deviceName), Color.White);
            Log("[i] 1) SideSync (Android 5–6 အတွက် အများဆုံး):", Color.White);
            Log("[i]    PC မှာ Samsung SideSync install → open → USB ချိတ်", Color.White);
            Log("[i]    ဖုန်းပေါ် popup ပေါ်ရင် install/allow → browser/settings ဝင်", Color.White);
            Log("[i]    → Google account manager / FRP APK သွင်းနည်း ဆက်", Color.White);
            Log("[i] 2) OTG: USB-OTG + flash drive (file manager APK) — setup wizard ထဲ ဝင်", Color.White);
            Log("[i] 3) Download mode (Vol- + Home + Power) → heimdall flash combination/firmware", Color.White);
            Log("[i] 4) Recovery wipe — data ဖျက်ပေမယ့် FRP မပျက် (Android 5.1+)", Color.White);
            Log("[i] ================================================", Color.FromArgb(0, 200, 255));

            bool sideSync = FindSideSyncPath(out string ssPath);
            var r = MessageBox.Show(
                "Samsung legacy FRP guide" + Environment.NewLine + Environment.NewLine +
                "*#0*# method: ADB interface မပေါ်ပါ (A3 2016 စသည်)។" + Environment.NewLine + Environment.NewLine +
                "SideSync: " + (sideSync ? "တွေ့ပါတယ် — ဖွင့်မလား?" : "PC မှာ မတွေ့ပါ (install လို)") + Environment.NewLine +
                (sideSync ? ssPath + Environment.NewLine + Environment.NewLine : "") +
                "SideSync ဖွင့်မလား?",
                "Samsung Legacy FRP", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (r == DialogResult.Yes && sideSync)
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = ssPath,
                        UseShellExecute = true
                    });
                    Log("[*] SideSync ဖွင့်ပြီ — USB ချိတ်ထားပြီး ဖုန်း popup စောင့်ပါ။", Color.Orange);
                }
                catch (Exception ex)
                {
                    Log("[!] SideSync start: " + ex.Message, Color.OrangeRed);
                }
            }
        }

        private static bool FindSideSyncPath(out string path)
        {
            path = "";
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf8 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string[] candidates =
            {
                Path.Combine(pf8, "Samsung", "SideSync", "SideSync.exe"),
                Path.Combine(pf, "Samsung", "SideSync", "SideSync.exe"),
                Path.Combine(pf8, "Samsung", "SideSync 4.x", "SideSync.exe"),
                @"C:\Program Files (x86)\Samsung\SideSync\SideSync.exe",
                @"C:\Program Files\Samsung\SideSync\SideSync.exe"
            };
            foreach (string c in candidates)
            {
                if (!string.IsNullOrWhiteSpace(c) && File.Exists(c)) { path = c; return true; }
            }
            return false;
        }

        // Samsung PnP set → stable signature (PID/service/iface) for change detect
        private static string SamsungUsbSignature(List<SamsungUsbDevice> devs)
        {
            if (devs == null || devs.Count == 0) return "";
            return string.Join(";", devs
                .Select(d => d.Pid + "|" + d.Service + "|" + (d.HasAdbInterface ? "A" : "-"))
                .OrderBy(s => s, StringComparer.Ordinal));
        }

        // Samsung MTP Factory Reset — ADB မစောင့်ဘဲ (တခြား tool တွေလို)
        // 1) ADB ရှိပြီးသားဆို ချက်ချင်း wipe
        // 2) မရှိရင် Samsung modem COM (AT) ကြိုးစား → မရရင် Recovery button guide (ADB မလို)
        private async Task RunSamsungMtpFactoryResetAsync()
        {
            if (MessageBox.Show(
                    "Samsung - MTP Factory Reset (no ADB wait)" + Environment.NewLine + Environment.NewLine +
                    "ADB စောင့်စရာ မလိုပါ — အောက်ပါနည်းဖြင့်:" + Environment.NewLine +
                    "  • ADB ဖွင့်ထားပြီးသားဆို → ချက်ချင်း wipe" + Environment.NewLine +
                    "  • မဟုတ်ရင် → Recovery mode (button) ဝင်ပြီး wipe လုပ်" + Environment.NewLine +
                    "     (Vol+ + Power — ADB / *#0*# မလို)" + Environment.NewLine + Environment.NewLine +
                    "• ဖုန်းထဲက data / ဓာတ်ပုံ / အက်ပ် အားလုံး ဖျက်ခံရမယ်" + Environment.NewLine +
                    "• Pattern/PIN နဲ့ Google account (FRP) ပါ ပျက်သွားနိုင်တယ်" + Environment.NewLine +
                    "• ဖုန်းပိုင်ရှင် ခွင့်ပြုချက် ရှိမှ လုပ်ပါ" + Environment.NewLine + Environment.NewLine +
                    "ဆက်လုပ်မလား?",
                    "Samsung MTP Factory Reset", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("   SAMSUNG - MTP FACTORY RESET (no ADB wait)", Color.White);
            Log("========================================================", Color.FromArgb(0, 180, 255));

            var mtpDevs = (await FindSamsungUsbDevicesAsync())
                .Where(d => d.IsMtpLike || d.Role == "MTP" || d.Role == "Connectivity").ToList();
            if (mtpDevs.Count == 0)
            {
                Log("[!] Samsung MTP device မတွေ့ပါ — USB ချိတ်ပြီး File Transfer mode ရွေးပါ။", Color.OrangeRed);
                Log("========================================================", Color.FromArgb(0, 180, 255));
                return;
            }
            Log("[OK] MTP device: " + (mtpDevs[0].FriendlyName.Length > 0 ? mtpDevs[0].FriendlyName : mtpDevs[0].InstanceId), Color.LightGreen);

            // 1) ADB ရှိပြီးသားဆို — ချက်ချင်း wipe (စောင့်စရာမလို)
            string adbNow = await ExecuteCommandQuickAsync("adb.exe", "devices");
            if (adbNow.Contains("\tdevice"))
            {
                Log("[i] ADB device ရှိပြီ — Factory reset ချက်ချင်း စတယ်။", Color.Orange);
                await RunAdbFactoryResetCoreAsync();
                Log("========================================================", Color.FromArgb(0, 180, 255));
                return;
            }

            // 2) Samsung modem COM port (AT) — MTP နဲ့ ပေါ်တဲ့ Samsung Mobile USB Modem
            //    *2767*3855# master reset code ကို AT dial ကြိုးစား (အချို့ model မှာ အလုပ်လုပ်)
            bool atTried = await TrySamsungAtFactoryResetAsync();
            if (atTried)
            {
                Log("========================================================", Color.FromArgb(0, 180, 255));
                return;
            }

            // 3) ADB မရှိ / AT မရ — Recovery mode guide (ADB လုံးဝမလို)
            Log("[i] ADB / modem AT မရှ် — Recovery mode button နဲ့ wipe လုပ်မယ် (ADB မလို)။", Color.Orange);
            ShowSamsungRecoveryFactoryResetGuide();
            Log("========================================================", Color.FromArgb(0, 180, 255));
        }

        // Samsung Mobile USB Modem COM port ကနေ AT+CFUN / master reset code ကြိုးစား
        // တခြား tool တွေ (SamFw etc) က COM port ကို AT နဲ့ သုံး — ADB မစောင့်ရ
        private async Task<bool> TrySamsungAtFactoryResetAsync()
        {
            try
            {
                // Modem ဦးစားပေး — Connectivity Device V2 (AT မဟုတ်) ကို ရှောင်
                var candidates = await FindSamsungModemComPortsAsync();
                if (candidates.Count == 0)
                {
                    string[] ports = SerialPort.GetPortNames();
                    Log("[i] Samsung modem COM မတွေ့ — " + ports.Length + " COM port စမ်းမယ်။", Color.Gray);
                    foreach (string p in ports)
                        candidates.Add((p, "fallback " + p));
                }
                if (candidates.Count == 0) return false;

                foreach (var (comName, label) in candidates)
                {
                    Log("[*] Trying Samsung modem AT on " + comName + " (" + label + ") ...", Color.Cyan);
                    bool ok = await TryAtFactoryResetOnPortAsync(comName);
                    if (ok)
                    {
                        Log("[OK] AT commands sent — phone may reboot / wipe if supported.", Color.LightGreen);
                        Log("[i] မဝင်ရင် Recovery mode guide ကို ကြည့်ပါ (အောက်မှာ)။", Color.Orange);
                        ShowSamsungRecoveryFactoryResetGuide();
                        return true;
                    }
                }

                Log("[i] AT not accepted on any Samsung COM (lockscreen/permission/wrong port?) — skip.", Color.Orange);
                return false;
            }
            catch (Exception ex)
            {
                Log("[i] AT path error: " + ex.Message, Color.Gray);
                return false;
            }
        }

        // Samsung serial/Modem PnP → COM list; Modem first, Connectivity last
        private async Task<List<(string Com, string Label)>> FindSamsungModemComPortsAsync()
        {
            var list = new List<(string Com, string Label)>();
            void Add(string com, string name)
            {
                com = (com ?? "").Trim().ToUpperInvariant();
                if (!com.StartsWith("COM")) com = "COM" + com;
                if (!Regex.IsMatch(com, @"^COM\d+$")) return;
                if (list.Any(x => string.Equals(x.Com, com, StringComparison.OrdinalIgnoreCase))) return;
                list.Add((com, name ?? com));
            }

            try
            {
                // Win32_PnPEntity.Name မှာ "SAMSUNG ... (COM7)" ပုံမှန်ပါ
                string ps =
                    "Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue | " +
                    "Where-Object { $_.Name -match 'SAMSUNG|Samsung' -and $_.Name -match 'COM\\d+' } | " +
                    "Sort-Object @{e={ if ($_.Name -match 'Modem') {0} elseif ($_.Name -match 'Diagnostics') {1} elseif ($_.Name -match 'Connectivity') {3} else {2} }}, Name | " +
                    "ForEach-Object { if ($_.Name -match '(COM\\d+)') { $Matches[1] + '|' + $_.Name } }";
                string res = await ExecuteCommandQuickAsync("powershell.exe",
                    "-NoProfile -NonInteractive -Command \"" + ps.Replace("\"", "\\\"") + "\"");
                foreach (string line in (res ?? "").Split('\n'))
                {
                    string t = line.Trim();
                    int bar = t.IndexOf('|');
                    if (bar <= 0) continue;
                    Add(t.Substring(0, bar), t.Substring(bar + 1).Trim());
                }
            }
            catch { }

            // Win32_SerialPort — FriendlyName မှာ COM မပါတဲ့ Samsung modem
            try
            {
                string ps =
                    "Get-CimInstance Win32_SerialPort -ErrorAction SilentlyContinue | " +
                    "Where-Object { $_.PNPDeviceID -like '*VID_04E8*' -or $_.Name -match 'SAMSUNG|Samsung' } | " +
                    "ForEach-Object { $_.DeviceID + '|' + $_.Name }";
                string res = await ExecuteCommandQuickAsync("powershell.exe",
                    "-NoProfile -NonInteractive -Command \"" + ps.Replace("\"", "\\\"") + "\"");
                foreach (string line in (res ?? "").Split('\n'))
                {
                    string t = line.Trim();
                    int bar = t.IndexOf('|');
                    if (bar <= 0) continue;
                    Add(t.Substring(0, bar), t.Substring(bar + 1).Trim());
                }
            }
            catch { }

            // Enum\USB\VID_04E8 → Device Parameters\PortName (Huawei နည်းတူ)
            try
            {
                using var usb = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
                if (usb != null)
                {
                    foreach (string vidName in usb.GetSubKeyNames())
                    {
                        if (!vidName.Contains("VID_04E8", StringComparison.OrdinalIgnoreCase)) continue;
                        using var vidKey = usb.OpenSubKey(vidName);
                        if (vidKey == null) continue;
                        foreach (string inst in vidKey.GetSubKeyNames())
                        {
                            using var instKey = vidKey.OpenSubKey(inst);
                            using var p = instKey?.OpenSubKey("Device Parameters");
                            if (p?.GetValue("PortName") is string com &&
                                com.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                                Add(com, "Samsung USB " + inst);
                        }
                    }
                }
            }
            catch { }

            // PnP FriendlyName (COMx)
            if (list.Count == 0)
            {
                try
                {
                    string ps2 =
                        "Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | " +
                        "Where-Object { $_.FriendlyName -match 'SAMSUNG|Samsung' } | " +
                        "Sort-Object @{e={ if ($_.FriendlyName -match 'Modem') {0} else {1} }} | " +
                        "ForEach-Object { if ($_.FriendlyName -match '\\((COM\\d+)\\)') { $Matches[1] + '|' + $_.FriendlyName } }";
                    string res2 = await ExecuteCommandQuickAsync("powershell.exe",
                        "-NoProfile -NonInteractive -Command \"" + ps2.Replace("\"", "\\\"") + "\"");
                    foreach (string line in (res2 ?? "").Split('\n'))
                    {
                        string t = line.Trim();
                        int bar = t.IndexOf('|');
                        if (bar <= 0) continue;
                        Add(t.Substring(0, bar), t.Substring(bar + 1).Trim());
                    }
                }
                catch { }
            }

            // Modem ဦးစားပေး
            return list
                .OrderBy(x => x.Label.IndexOf("Modem", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 :
                    x.Label.IndexOf("Connectivity", StringComparison.OrdinalIgnoreCase) >= 0 ? 2 : 1)
                .ThenBy(x => x.Com, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // တစ် port အတွက် AT probe → master reset (multi-baud, DTR/RTS)
        private async Task<bool> TryAtFactoryResetOnPortAsync(string comName)
        {
            int[] bauds = { 115200, 9600, 460800, 57600 };
            foreach (int baud in bauds)
            {
                (AtProbeOutcome kind, SerialPort sp, string at0) =
                    await OpenAndProbeAtAsync(comName, baud, 2000, 250, 2000, warmupCrlf: true);
                if (kind == AtProbeOutcome.OpenFail) return false;
                Log("[i] AT@" + baud + " resp: " + AtBrief(at0), Color.Gray);
                if (kind != AtProbeOutcome.Talks) continue;
                using (sp)
                {
                    try
                    {
                        await ReadAtResponseAsync(sp, "ATE0");

                        bool resetOk = false;

                        // Master reset USSD via ATD — *2767*3855# (voice dial; 4s — USSD ကြာနိုင်)
                        Log("[*] Sending master reset code *2767*3855# via AT ...", Color.Cyan);
                        string r1 = await ReadAtResponseAsync(sp, "ATD*2767*3855#;", 4000);
                        Log("[i] ATD; resp: " + AtBrief(r1), Color.Gray);
                        if (AtIsSuccess(r1)) resetOk = true;

                        // semicolon fail/error ဆို data-dial variant ထပ်စမ်း
                        if (!AtIsSuccess(r1))
                        {
                            r1 = await ReadAtResponseAsync(sp, "ATD*2767*3855#", 4000);
                            Log("[i] ATD resp: " + AtBrief(r1), Color.Gray);
                            if (AtIsSuccess(r1)) resetOk = true;
                        }

                        // CFUN full reset — 1,1 = full reset (lock/security က block နိုင်)
                        string r2 = await ReadAtResponseAsync(sp, "AT+CFUN=1,1", 3000);
                        Log("[i] AT+CFUN=1,1 resp: " + AtBrief(r2), Color.Gray);
                        if (AtIsSuccess(r2)) resetOk = true;
                        else if (AtHasError(r2) &&
                                 r2.IndexOf("NOT_ALLOWED", StringComparison.OrdinalIgnoreCase) >= 0)
                            Log("[i] CFUN blocked (NOT_ALLOWED) — screen unlock ပြီးမှ ထပ်စမ်း/Recovery သုံးပါ။", Color.Orange);

                        if (!AtIsSuccess(r2))
                        {
                            r2 = await ReadAtResponseAsync(sp, "AT+CFUN=1", 3000);
                            Log("[i] AT+CFUN=1 resp: " + AtBrief(r2), Color.Gray);
                            if (AtIsSuccess(r2)) resetOk = true;
                        }

                        // handshake OK ကို success မထင် — reset cmd OK (error မပါ) သာ
                        if (resetOk)
                            return true;
                    }
                    finally
                    {
                        try { sp.Close(); } catch { }
                    }
                }
            }
            return false;
        }

        // AT serial prologue — info / quick-info / ADB-enable / factory-reset 4 နေရာမှာ
        // ထပ်နေတဲ့ open → settle → "AT" probe ကို ဒီမှာ ပေါင်းတယ်။
        //   OpenFail = open fail (caller break/return), Silent = မဖြောင်း (continue),
        //   Talks = sp open — caller က using(sp) + body ဆက်ပြီး dispose လုပ်တယ်။
        private enum AtProbeOutcome { OpenFail, Silent, Talks }

        private async Task<(AtProbeOutcome Outcome, SerialPort Sp, string Probe)> OpenAndProbeAtAsync(
            string comName, int baud, int readTimeoutMs, int settleMs, int probeTimeoutMs,
            bool warmupCrlf = false, bool quiet = false)
        {
            var sp = new SerialPort(comName, baud, Parity.None, 8, StopBits.One)
            {
                DtrEnable = true,
                RtsEnable = true,
                ReadTimeout = readTimeoutMs,
                WriteTimeout = 2000
            };
            try { sp.Open(); }
            catch (Exception ex)
            {
                if (!quiet) Log("[i] " + comName + "@" + baud + " open fail: " + ex.Message, Color.Gray);
                sp.Dispose();
                return (AtProbeOutcome.OpenFail, null, null);
            }
            try
            {
                await Task.Delay(settleMs);
                sp.DiscardInBuffer();
                sp.DiscardOutBuffer();
                if (warmupCrlf)
                {
                    sp.Write("\r\n");
                    await Task.Delay(100);
                    sp.DiscardInBuffer();
                }
                string at0 = await ReadAtResponseAsync(sp, "AT", probeTimeoutMs);
                bool talks = AtHasOk(at0) || AtHasError(at0) ||
                    (at0 != null && (at0.Contains("AT") || at0.Contains("+")));
                if (!talks)
                {
                    sp.Dispose();
                    return (AtProbeOutcome.Silent, null, at0);
                }
                return (AtProbeOutcome.Talks, sp, at0);
            }
            catch
            {
                sp.Dispose();
                throw;
            }
        }

        // AT response တွင် result line "OK" ရှိမှန်း (echo/line-break ကို OK မထင်)
        private static bool AtHasOk(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (string raw in s.Split('\n'))
            {
                string t = raw.Trim('\r', ' ', '\t', '\0');
                if (t.Equals("OK", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool AtHasError(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            return s.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   s.IndexOf("+CME", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   s.IndexOf("+CMS", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // OK ရှိ + error မပါ မှ success — Samsung က "CME ERROR" + "OK" နောက်ဆက်ပို့တတ်
        private static bool AtIsSuccess(string s)
        {
            if (AtHasError(s)) return false;
            return AtHasOk(s);
        }

        private static string AtBrief(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "(none)";

            // error line ကို ဦးစားပေးပြ
            foreach (string raw in s.Split('\n'))
            {
                string t = raw.Trim('\r', ' ', '\t', '\0');
                if (t.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    t.IndexOf("+CME", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    t.IndexOf("+CMS", StringComparison.OrdinalIgnoreCase) >= 0)
                    return t;
            }

            foreach (string raw in s.Split('\n'))
            {
                string t = raw.Trim('\r', ' ', '\t', '\0');
                if (t.Equals("OK", StringComparison.OrdinalIgnoreCase))
                    return "OK";
            }

            string one = FirstLine(s);
            if (AtIsSuccess(s) && !one.Trim().Equals("OK", StringComparison.OrdinalIgnoreCase))
                one += " [OK]";
            return one;
        }

        // AT command → response (line-based; timeoutMs default 2s)
        private async Task<string> ReadAtResponseAsync(SerialPort sp, string cmd, int timeoutMs = 2000)
        {
            try
            {
                sp.DiscardInBuffer();
                sp.Write(cmd + "\r\n");
            }
            catch (Exception ex) { return "WRITE_FAIL:" + ex.Message; }

            var sb = new System.Text.StringBuilder();
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (sp.BytesToRead > 0)
                    {
                        sb.Append(sp.ReadExisting());
                        string s = sb.ToString();
                        if (AtHasOk(s) || AtHasError(s))
                            break;
                    }
                    else
                    {
                        await Task.Delay(40);
                    }
                }
                catch { break; }
            }
            await Task.Delay(50);
            try
            {
                if (sp.BytesToRead > 0)
                    sb.Append(sp.ReadExisting());
            }
            catch { }
            return sb.ToString();
        }

        // ADB မလို — Recovery button combo နဲ့ manual wipe guide
        private void ShowSamsungRecoveryFactoryResetGuide()
        {
            Log("[i] ===== RECOVERY MODE FACTORY RESET (no ADB) =====", Color.FromArgb(0, 200, 255));
            Log("[i] 1) ဖုန်းကို Power off ပါ", Color.White);
            Log("[i] 2) Vol+ ကို ဆက်နှိပ်ထားပြီး Power ကို နှိပ် — Samsung logo ပေါ်မှ လွှတ်", Color.White);
            Log("[i]    (Home button ရှိရင် Vol+ + Home + Power)", Color.White);
            Log("[i] 3) Recovery menu ဝင်ပြီးရင်:", Color.White);
            Log("[i]      • Wipe data / Factory reset ရွေး", Color.White);
            Log("[i]      • Yes — delete all user data ရွေး", Color.White);
            Log("[i]      • Reboot system now ရွေး", Color.White);
            Log("[i] 4) Vol+ / Vol- = ရွေး, Power = အတည်ပြု", Color.White);
            Log("[i] ======================================================", Color.FromArgb(0, 200, 255));

            MessageBox.Show(
                "Recovery Mode Factory Reset (ADB မလို)" + Environment.NewLine + Environment.NewLine +
                "1) ဖုန်း Power off ပါ" + Environment.NewLine + Environment.NewLine +
                "2) Vol+ ကို ဆက်နှိပ်ထားပြီး Power နှိပ်" + Environment.NewLine +
                "   Samsung logo ပေါ်မှ လွှတ်ပါ" + Environment.NewLine +
                "   (Home ရှိရင်: Vol+ + Home + Power)" + Environment.NewLine + Environment.NewLine +
                "3) Recovery menu ထဲမှာ:" + Environment.NewLine +
                "   • Wipe data / Factory reset" + Environment.NewLine +
                "   • Yes — delete all user data" + Environment.NewLine +
                "   • Reboot system now" + Environment.NewLine + Environment.NewLine +
                "Vol+ / Vol- = ရွေး, Power = OK",
                "Samsung Recovery Factory Reset", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ================= Samsung Download (Odin) Mode Info =================
        // heimdall detect + USB PnP (VID_04E8 + download PID) ကနေ mode/serial ဖတ်တယ်။
        // ADB / MTP မလို — Vol- + Home + Power (သို့) Vol+ + Vol- + USB နဲ့ Download mode ဝင်ထားရင် ရတယ်။
        private async Task RunSamsungDownloadInfoAsync()
        {
            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("   SAMSUNG - DOWNLOAD (ODIN) MODE INFO", Color.White);
            Log("========================================================", Color.FromArgb(0, 180, 255));

            // ၁) heimdall tool ရှိ/မရှိ + version
            string heimdall = FindHeimdall();
            string ver = await ExecuteCommandQuickAsync(heimdall, "version");
            if (string.IsNullOrWhiteSpace(ver) || ver.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                ver.Contains("is not recognized", StringComparison.OrdinalIgnoreCase))
            {
                Log("[!] heimdall.exe မတွေ့ပါ — Download mode detect/PIT/flash မရ။", Color.OrangeRed);
                Log("    bundled-tools\\samsung\\heimdall.exe ရှိမှန် စစ်ပါ။", Color.Orange);
            }
            else
            {
                Log("  • Heimdall      : " + ver.Trim().Split('\n')[0].Trim(), Color.White);
            }

            // ၂) heimdall detect — Download mode မှာ device ရှိ/မရှိ
            string detect = await ExecuteCommandQuickAsync(heimdall, "detect");
            string detectLow = (detect ?? "").ToLowerInvariant();
            bool heimdallOk = detectLow.Contains("device detected") || detectLow.Contains("found");
            if (heimdallOk)
                Log("  • Heimdall      : Device detected (Download mode) ✓", Color.LightGreen);
            else
                Log("  • Heimdall      : not detected — " + FirstLine(detect), Color.Orange);

            // ၃) USB PnP — Samsung VID + Download-mode PID
            //     classic: 685D (Odin), alt: 6866/6877/6860 sometimes show with cable in DL
            var allSam = await FindSamsungUsbDevicesAsync();
            var dlPids = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "685d", "6866", "685c", "685e", "6a1a", "6a1b" };
            var dlDevs = allSam.Where(d => dlPids.Contains(d.Pid) ||
                d.FriendlyName.Contains("Download", StringComparison.OrdinalIgnoreCase) ||
                d.FriendlyName.Contains("Odin", StringComparison.OrdinalIgnoreCase) ||
                d.Service.Contains("WinUSB", StringComparison.OrdinalIgnoreCase)).ToList();

            if (dlDevs.Count == 0 && allSam.Count > 0)
            {
                // Samsung USB ရှိပေမယ့် known DL PID မဟုတ် — ရှိတာအားလုံး ပြ
                Log("  • Samsung USB   : (download PID မတွေ့ — ရှိတဲ့ device တွေ):", Color.Orange);
                foreach (var d in allSam.Take(4))
                    Log("      PID " + d.Pid + " | " + (string.IsNullOrWhiteSpace(d.FriendlyName) ? d.Service : d.FriendlyName), Color.Gray);
            }
            else if (dlDevs.Count > 0)
            {
                foreach (var d in dlDevs)
                {
                    Log("  • PID           : 04E8:" + d.Pid + "  ✓ Download-mode USB", Color.LightGreen);
                    Log("  • Friendly      : " + (string.IsNullOrWhiteSpace(d.FriendlyName) ? "(n/a)" : d.FriendlyName), Color.White);
                    Log("  • Service       : " + (string.IsNullOrWhiteSpace(d.Service) ? "(n/a)" : d.Service), Color.White);
                    // InstanceId ထဲမှာ USB serial များသောအားဖြင့် ပါတယ်: ...VID_04E8&PID_685D\SERIAL
                    Match sm = Regex.Match(d.InstanceId, @"&PID_[0-9A-Fa-f]{4}\\(.+)$", RegexOptions.IgnoreCase);
                    if (sm.Success)
                        Log("  • USB Serial    : " + sm.Groups[1].Value.Trim('\\'), Color.White);
                    Log("  • InstanceId    : " + d.InstanceId, Color.Gray);
                }
            }
            else
            {
                Log("[!] Samsung USB device မတွေ့ပါ — Download mode မဝင်ရသေး/cable မချိတ်။", Color.OrangeRed);
                Log("    Vol- + Home + Power  (သို့)  Vol+ + Vol- + USB ချိတ် → Warning screen → Vol+ ဖြင့် ဝင်ပါ။", Color.Orange);
            }

            // ၄) အခြေအနေ summary
            if (heimdallOk || dlDevs.Count > 0)
            {
                Log("[OK] Download mode — PIT download / Heimdall flash အတွက် အဆင်သင့်။", Color.LightGreen);
                Log("[i] Partition ကြည့်ချင်ရင် Download PIT၊ flash ချင်ရင် Firmware FLASH block သုံးပါ။", Color.Orange);
            }
            else
            {
                Log("[i] Download mode မဝင်ရသေး — ဖုန်းကို DL mode ဝင်ပြီး ထပ်နှိပ်ပါ။", Color.Orange);
            }
            Log("========================================================", Color.FromArgb(0, 180, 255));
        }

        private static string FirstLine(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "(no output)";
            string line = s.Trim().Split('\n')[0].Trim();
            return line.Length > 80 ? line.Substring(0, 80) + "..." : line;
        }

        // heimdall detect: exit!=0 ဆိုရင်လည်း stderr (e.g. "Failed to detect...") ပြန်ဖို့ — RunQuickAsync မသုံး
        private static async Task<string> RunHeimdallDetectAsync(string heimdall)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = heimdall,
                    Arguments = "detect",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return "(heimdall start failed)";
                var stdout = p.StandardOutput.ReadToEndAsync();
                var stderr = p.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(8000);
                try
                {
                    await Task.WhenAll(p.WaitForExitAsync(timeout.Token), stdout, stderr).WaitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    return "(detect timeout)";
                }
                string outText = (await stdout).Trim();
                string errText = (await stderr).Trim();
                if (!string.IsNullOrWhiteSpace(outText)) return outText;
                if (!string.IsNullOrWhiteSpace(errText))
                    return p.ExitCode == 0 ? errText : "exit " + p.ExitCode + ": " + errText;
                return "(no output, exit " + p.ExitCode + ")";
            }
            catch (Exception ex) { return ex.Message; }
        }

        // ================= Samsung SoftBrick Fix (Odin 0x64/0x01) =================
        // Download mode error screen → flash-count reset → normal Download mode (firmware မလို)
        private async Task RunSamsungSoftBrickAsync()
        {
            if (isTaskRunning)
            {
                Log("[!] Another operation is still running - wait for it to finish or press STOP.", Color.OrangeRed);
                return;
            }

            isTaskRunning = true;
            stopRequested = false;
            try
            {
                Log("========================================================", Color.FromArgb(0, 180, 255));
                Log("   SAMSUNG - SOFTBRICK FIX (Odin flash-count reset)", Color.White);
                Log("========================================================", Color.FromArgb(0, 180, 255));
                SetProgress(5, "Detecting Download mode");

                // Optional: heimdall detect for clearer status (non-blocking fail).
                // RunQuickAsync returns "" on exit!=0; heimdall writes errors to stderr — capture both.
                try
                {
                    string heimdall = FindHeimdall();
                    string detect = await RunHeimdallDetectAsync(heimdall);
                    string dlow = (detect ?? "").ToLowerInvariant();
                    if (dlow.Contains("device detected") || dlow.Contains("found"))
                        Log("  • Heimdall      : Device detected (Download mode) ✓", Color.LightGreen);
                    else
                        Log("  • Heimdall      : " + FirstLine(detect), Color.Orange);
                }
                catch { /* heimdall optional */ }

                if (SamsungSoftBrick.FindDownloadModeDevice(out int vid, out int pid) == null)
                {
                    Log("[!] Samsung Download mode USB မတွေ့ပါ (VID_04E8).", Color.OrangeRed);
                    Log("    ဖုန်းကို Download mode ဝင်ပြီး USB ပြန်တပ်ပါ:", Color.Orange);
                    Log("    Vol- + Vol- + USB (newer)  (သို့)  Vol- + Home + Power → Warning → Vol+", Color.Orange);
                    Log("    Samsung USB driver / Zadig WinUSB တပ်ထားရမယ်။", Color.Orange);
                    SetProgress(0, "Failed");
                    return;
                }

                Log($"  • USB           : 04E8:{pid:X4}", Color.White);
                SetProgress(20, "Odin handshake");
                Log("[*] Odin protocol: ODIN/LOKE → BeginSession → ResetFlashCount → End → Reboot", Color.Cyan);

                string err = "";
                bool ok = await Task.Run(() =>
                    SamsungSoftBrick.ResetFlashCount(
                        msg =>
                        {
                            Color c = msg.StartsWith("[OK]", StringComparison.Ordinal) ? Color.LightGreen
                                    : msg.StartsWith("[!]", StringComparison.Ordinal) ? Color.Orange
                                    : Color.White;
                            Log(msg, c);
                        },
                        rebootToOdin: true,
                        out err));

                if (ok)
                {
                    SetProgress(100, "SoftBrick fix OK");
                    Log("[OK] SoftBrick Fix ပြီးပါပြီ — flash count reset ပြီး Download mode ပြန်ဝင်ပါမယ်။", Color.LightGreen);
                    Log("[i] Error screen ပျောက်ပြီး firmware ပြန်ထည့်ချင်ရင် Firmware FLASH block သုံးပါ။", Color.Orange);
                }
                else
                {
                    SetProgress(0, "Failed");
                    Log("[FAIL] " + (string.IsNullOrWhiteSpace(err) ? "SoftBrick Fix failed" : err), Color.Red);
                    Log("    heimdall/Odin ပိတ်၊ cable ပြောင်း၊ Download mode ပြန်ဝင်ပြီး retry ပါ။", Color.Orange);
                }
                Log("========================================================", Color.FromArgb(0, 180, 255));
            }
            catch (Exception ex)
            {
                SetProgress(0, "Failed");
                Log("[FAIL] SoftBrick Fix: " + ex.Message, Color.Red);
            }
            finally
            {
                isTaskRunning = false;
            }
        }

        private async void BtnSamSideload_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog ofd = new OpenFileDialog
            {
                Filter = "Update ZIP (*.zip)|*.zip|All Files (*.*)|*.*",
                Title = "Sideload လုပ်မယ့် ZIP ကို ရွေးပါ"
            })
            {
                if (ofd.ShowDialog() != DialogResult.OK) return;
                Log("[*] On the phone, select Recovery -> 'Apply update from ADB'.", Color.Orange);
                await ExecuteCommandCleanAsync("adb.exe", "sideload \"" + ofd.FileName + "\"", "Sideload " + Path.GetFileName(ofd.FileName));
            }
        }

        private async void BtnHisiOemUnlock_Click(object sender, EventArgs e)
        {
            string code = PromptInput("Huawei OEM Unlock",
                "PotatoNV က ရလာတဲ့ unlock code ကို paste ပါ။\n(ဖုန်းက fastboot mode မှာ ရှိရမယ် — ⚡ Reboot Fastboot နဲ့ ရောက်နိုင်တယ်)");
            if (string.IsNullOrWhiteSpace(code)) return;

            code = code.Trim();
            if (!IsValidPartitionName(code))
            {
                Log("[x] Unlock code contains invalid characters - allowed: a-z, 0-9, _ , -", Color.Red);
                return;
            }
            if (MessageBox.Show(
                    "fastboot oem unlock " + code + "\n\n" +
                    "ဒါက bootloader ကို unlock လုပ်တယ် — userdata ဖျက်ခံရနိုင်တယ်။\n" +
                    "ဖုန်းပိုင်ရှင် ခွင့်ပြုချက် ရှိမှ လုပ်ပါ။\n\nဆက်လုပ်မလား?",
                    "Confirm OEM Unlock", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            await ExecuteCommandCleanAsync("fastboot.exe", "oem unlock " + code, "Huawei OEM Unlock (code)");
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

        // ================= KIRIN BOOTLOADER UNLOCK (PotatoNV core) =================
        // PotatoNV-next ရဲ့ library (Potato.ImageFlasher + Potato.Fastboot) ကို သုံးပြီး
        // user က Kirin model ရွေးလိုက်ရင် ဖုန်း bootloader ကို unlock လုပ်ပေးတယ်:
        //   VCOM upload (xloader + [uce] + fastboot) → fastboot mode → WVLOCK/USRKEY ရေး → reboot
        // ရလာတဲ့ code ကို '⚡ OEM Unlock (code)' နဲ့ ဆက်သုံးရတယ်။

        private string FindBootloadersFolder()
        {
            string local = Path.Combine(Application.StartupPath, "bootloaders");
            if (Directory.Exists(local)) return local;

            try
            {
                string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                if (Directory.Exists(downloads))
                {
                    foreach (string root in Directory.GetDirectories(downloads, "PotatoNV*"))
                    {
                        string direct = Path.Combine(root, "bootloaders");
                        if (Directory.Exists(direct)) return direct;

                        foreach (string sub in Directory.GetDirectories(root))
                        {
                            string nested = Path.Combine(sub, "bootloaders");
                            if (Directory.Exists(nested)) return nested;
                        }
                    }
                }
            }
            catch { }
            return "";
        }

        private void LoadKirinList()
        {
            cmbKirin.Items.Clear();

            if (string.IsNullOrEmpty(hisiBootloadersPath) || !Directory.Exists(hisiBootloadersPath))
                hisiBootloadersPath = FindBootloadersFolder();

            if (string.IsNullOrEmpty(hisiBootloadersPath) || !Directory.Exists(hisiBootloadersPath))
            {
                // startup log noise မဖြစ်စေရ — unlock နှိပ်မှ MessageBox ပြ
                Debug.WriteLine("Kirin bootloaders folder not found.");
                return;
            }

            int n = 0;
            foreach (string dir in Directory.GetDirectories(hisiBootloadersPath))
            {
                string mf = Path.Combine(dir, "manifest.xml");
                if (!File.Exists(mf)) continue;
                try
                {
                    System.Xml.Linq.XDocument doc = System.Xml.Linq.XDocument.Load(mf);
                    if (doc.Root == null) continue;
                    var nameAttr = doc.Root.Attribute("name");
                    string name = nameAttr != null ? nameAttr.Value : Path.GetFileName(dir);
                    cmbKirin.Items.Add(new KirinBootloader { Name = name });
                    n++;
                }
                catch (Exception ex)
                {
                    Log("[!] Kirin manifest parse failed (" + Path.GetFileName(dir) + "): " + ex.Message, Color.Orange);
                }
            }

            if (cmbKirin.Items.Count > 0)
            {
                cmbKirin.SelectedIndex = 0;
                // startup noise မဖြစ်စေရ — Kirin list တွေ့တာ log မထုတ်တော့ (HiSilicon tab မှာ dropdown ပြပြီးသား)
            }
            else Debug.WriteLine("manifest.xml not found in the bootloaders folder.");
        }

        // VID_12D1 (Huawei) USB serial devices က COM port တွေ ရှာ — Enum\USB ထဲက Device Parameters\PortName
        private static HashSet<string> FindHuaweiComPorts()
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var usb = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
                if (usb == null) return found;
                foreach (string vidName in usb.GetSubKeyNames())
                {
                    if (!vidName.Contains("VID_12D1", StringComparison.OrdinalIgnoreCase)) continue;
                    using var vidKey = usb.OpenSubKey(vidName);
                    if (vidKey == null) continue;
                    foreach (string inst in vidKey.GetSubKeyNames())
                    {
                        using var instKey = vidKey.OpenSubKey(inst);
                        using var p = instKey?.OpenSubKey("Device Parameters");
                        if (p?.GetValue("PortName") is string com &&
                            com.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                            found.Add(com);
                    }
                }
            }
            catch { /* access denied / no USB tree */ }
            return found;
        }

        private static string ExtractComPort(string item)
        {
            if (string.IsNullOrEmpty(item)) return "";
            Match m = Regex.Match(item, @"COM\d+", RegexOptions.IgnoreCase);
            return m.Success ? m.Value.ToUpperInvariant() : item.Trim();
        }

        private void RefreshHisiPorts()
        {
            try
            {
                string keep = cmbHisiPort.SelectedItem != null ? cmbHisiPort.SelectedItem.ToString() : "";
                string keepCom = ExtractComPort(keep);

                huaweiComPorts = FindHuaweiComPorts();
                string[] ports = SerialPort.GetPortNames();
                Array.Sort(ports, (a, b) =>
                {
                    int na, nb;
                    int.TryParse(a.Replace("COM", "", StringComparison.OrdinalIgnoreCase), out na);
                    int.TryParse(b.Replace("COM", "", StringComparison.OrdinalIgnoreCase), out nb);
                    int ha = huaweiComPorts.Contains(a) ? 0 : 1;
                    int hb = huaweiComPorts.Contains(b) ? 0 : 1;
                    if (ha != hb) return ha.CompareTo(hb);
                    return na.CompareTo(nb);
                });

                cmbHisiPort.Items.Clear();
                foreach (string p in ports)
                    cmbHisiPort.Items.Add(huaweiComPorts.Contains(p) ? p + " (HUAWEI)" : p);

                if (cmbHisiPort.Items.Count == 0)
                {
                    cmbHisiPort.Items.Add("No Port");
                }
                else if (!string.IsNullOrEmpty(keepCom))
                {
                    bool restored = false;
                    foreach (object it in cmbHisiPort.Items)
                    {
                        if (string.Equals(ExtractComPort(it?.ToString()), keepCom, StringComparison.OrdinalIgnoreCase))
                        {
                            cmbHisiPort.SelectedItem = it;
                            restored = true;
                            break;
                        }
                    }
                    if (!restored) cmbHisiPort.SelectedIndex = 0;
                }
                else
                {
                    cmbHisiPort.SelectedIndex = 0;
                }

                if (huaweiComPorts.Count > 0)
                    Debug.WriteLine("HUAWEI USB COM: " + string.Join(", ", huaweiComPorts));
            }
            catch (Exception ex)
            {
                Log("Port scan error: " + ex.Message, Color.Red);
            }
        }

        private void BtnHisiBootloaders_Click(object sender, EventArgs e)
        {
            using (FolderBrowserDialog fbd = new FolderBrowserDialog
            {
                Description = "PotatoNV bootloaders folder ကို ရွေးပါ (manifest.xml ပါတဲ့ folder)"
            })
            {
                if (fbd.ShowDialog() != DialogResult.OK) return;
                hisiBootloadersPath = fbd.SelectedPath;
                SaveEdlPaths();
                LoadKirinList();
            }
        }

        private KirinBootloader ParseBootloader(string name)
        {
            // manifest.xml ထဲက image (role/path/address/hash) တွေကို ဖတ်တယ်
            foreach (string dir in Directory.GetDirectories(hisiBootloadersPath))
            {
                string mf = Path.Combine(dir, "manifest.xml");
                if (!File.Exists(mf)) continue;
                try
                {
                    System.Xml.Linq.XDocument doc = System.Xml.Linq.XDocument.Load(mf);
                    if (doc.Root == null) continue;
                    var nameAttr = doc.Root.Attribute("name");
                    string n = nameAttr != null ? nameAttr.Value : Path.GetFileName(dir);
                    if (n != name) continue;

                    var bl = new KirinBootloader { Name = n };
                    foreach (var el in doc.Root.Elements("image"))
                    {
                        var pathAttr = el.Attribute("path");
                        var roleAttr = el.Attribute("role");
                        var addrAttr = el.Attribute("address");
                        var hashAttr = el.Attribute("hash");
                        if (pathAttr == null || addrAttr == null) continue;

                        string full = Path.Combine(dir, pathAttr.Value);
                        var img = new KirinImage
                        {
                            Role = roleAttr != null ? roleAttr.Value : Path.GetFileNameWithoutExtension(pathAttr.Value),
                            Path = full,
                            Hash = hashAttr != null ? hashAttr.Value : "",
                            Address = (int)Convert.ToInt64(addrAttr.Value.Replace("0x", ""), 16)
                        };
                        if (File.Exists(full)) img.Size = new FileInfo(full).Length;
                        bl.Images.Add(img);
                    }
                    return bl;
                }
                catch (Exception ex)
                {
                    Log("Error while reading manifest: " + ex.Message, Color.Red);
                }
            }
            return null;
        }

        private static bool VerifySha1(string path, string expected)
        {
            try
            {
                using (var sha = System.Security.Cryptography.SHA1.Create())
                using (var fs = File.OpenRead(path))
                {
                    byte[] h = sha.ComputeHash(fs);
                    var sb = new StringBuilder();
                    foreach (byte b in h) sb.Append(b.ToString("x2"));
                    return string.Equals(sb.ToString(), expected, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { return false; }
        }

        private static string GenerateUnlockCode()
        {
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var rnd = new Random(Guid.NewGuid().GetHashCode());
            var sb = new StringBuilder();
            for (int i = 0; i < 16; i++) sb.Append(alphabet[rnd.Next(alphabet.Length)]);
            return sb.ToString();
        }

        // PotatoNV ရဲ့ SetNVMEProp — "getvar:nve:<prop>@" နောက်မှာ raw bytes ကို ဆက်ပို့တယ်
        private bool SetNvmeProp(Potato.Fastboot.Fastboot fb, string prop, byte[] value)
        {
            Log("[*] Writing " + prop + " (" + value.Length + " bytes)...", Color.Cyan);
            var cmd = new List<byte>();
            cmd.AddRange(Encoding.ASCII.GetBytes("getvar:nve:" + prop + "@"));
            cmd.AddRange(value);

            var res = fb.Command(cmd.ToArray());
            string payload = res != null && res.Payload != null ? res.Payload : "";
            Log("    → " + payload, Color.Gray);
            return payload.Contains("set nv ok");
        }

        private async void BtnHisiStartUnlock_Click(object sender, EventArgs e)
        {
            var selected = cmbKirin.SelectedItem as KirinBootloader;
            if (selected == null)
            {
                MessageBox.Show("Kirin model ကို အရင် ရွေးပါ။\n(📁 Bootloaders ခလုတ်နဲ့ bootloaders folder ရွေးဖို့ လိုနိုင်တယ်)",
                    "Kirin model", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string port = ExtractComPort(cmbHisiPort.SelectedItem != null ? cmbHisiPort.SelectedItem.ToString() : "");
            if (string.IsNullOrEmpty(port) || !port.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("ဖုန်းရဲ့ COM port ကို ရွေးပါ (Device Manager မှာ 'HUAWEI USB COM 1.0')။\n\n" +
                                "• ဖုန်းကို testpoint short လုပ်ပြီး ချိတ်ပါ\n• ပြီးရင် 🔄 နှိပ်ပြီး port ပြန်စစ်ပါ\n" +
                                "• HUAWEI port ဆိုရင် list ထဲမှာ '(HUAWEI)' နဲ့ အပေါ်ဆုံးမှာ ပေါ်တယ်",
                    "COM port", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (huaweiComPorts.Count > 0 && !huaweiComPorts.Contains(port))
            {
                Log("[!] " + port + " က HUAWEI USB COM မဟုတ်ဘူး — Driver မှန်မှန် install ရှိရင် '(HUAWEI)' port ကို ရွေးပါ။", Color.Orange);
            }

            if (MessageBox.Show(
                    "Kirin Bootloader Unlock\n\n" +
                    "• Model : " + selected.Name + "\n" +
                    "• Port  : " + port + "\n\n" +
                    "ဖုန်း RAM ထဲ bootloader တင်ပြီး bootloader lock ကို ဖွင့်ပါမယ်။\n" +
                    "• userdata ဖျက်ခံရနိုင်တယ်\n" +
                    "• ဖုန်းပိုင်ရှင် ခွင့်ပြုချက် ရှိမှ လုပ်ပါ (ခိုးရာပစ္စည်း မဟုတ်ကြောင်း စစ်ပါ)\n\n" +
                    "ဆက်လုပ်မလား?",
                    "Confirm Kirin Unlock", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            KirinBootloader bl = ParseBootloader(selected.Name);
            if (bl == null || bl.Images.Count == 0)
            {
                Log("[x] Could not read manifest.xml of " + selected.Name + ".", Color.Red);
                return;
            }

            string code = await RunKirinUnlockAsync(bl, port);

            if (!string.IsNullOrEmpty(code))
            {
                Log("========================================================", Color.FromArgb(0, 180, 255));
                Log("   ✔ UNLOCK CODE : " + code, Color.LightGreen);
                Log("========================================================", Color.FromArgb(0, 180, 255));
                MessageBox.Show("Unlock ပြီးပါပြီ!\n\nUnlock code: " + code + "\n\n" +
                                "ဖုန်းကို fastboot mode ပြောင်းပြီး '⚡ OEM Unlock (code)' နဲ့ ဒီ code ကို ထည့်ပါ။",
                    "Kirin Unlock", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private static byte[] HashSha256(byte[] data)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            return sha.ComputeHash(data);
        }

        private async Task<string> RunKirinUnlockAsync(KirinBootloader bl, string port)
        {
            if (hisiCts != null) { try { hisiCts.Cancel(); hisiCts.Dispose(); } catch { } }
            hisiCts = new CancellationTokenSource();
            CancellationToken ct = hisiCts.Token;
            string unlockCode = "";

            Log("========================================================", Color.FromArgb(0, 180, 255));
            Log("   HUAWEI / HONOR - KIRIN BOOTLOADER UNLOCK", Color.White);
            Log("   " + bl.Name + "   |   Port: " + port, Color.White);
            Log("========================================================", Color.FromArgb(0, 180, 255));
            SetProgress(5, "Verifying images");

            bool ok = await Task.Run(() =>
            {
                try
                {
                    // 1) image တွေ စစ် (ဖိုင် ရှိ/မရှိ + SHA1 hash)
                    long totalSize = 0;
                    foreach (var img in bl.Images)
                    {
                        if (!File.Exists(img.Path)) { Log("[x] Image file not found: " + img.Path, Color.Red); return false; }
                        if (img.Size <= 0) { Log("[x] Image file is empty: " + Path.GetFileName(img.Path), Color.Red); return false; }
                        if (!string.IsNullOrEmpty(img.Hash) && !VerifySha1(img.Path, img.Hash))
                        {
                            Log("[x] Hash mismatch: " + Path.GetFileName(img.Path) + " - file corrupted or wrong", Color.Red);
            return false;
        }
                        totalSize += img.Size;
                    }
                    Log("[OK] Images verified (" + bl.Images.Count + " files, " + (totalSize / 1024) + " KB)", Color.LightGreen);
                    SetProgress(10, "Uploading bootloader");

                    // 2) VCOM (COM port) ကနေ RAM ထဲ တင်
                    var flasher = new Potato.ImageFlasher.ImageFlasher();
                    try
                    {
                        Log("[*] Opening " + port + "...", Color.Orange);
                        flasher.Open(port);
                        Log("[*] Uploading " + bl.Name + " bootloader into phone RAM...", Color.Cyan);

                        int done = 0;
                        int grand = (int)Math.Max(1, totalSize);
                        foreach (var img in bl.Images)
                        {
                            if (ct.IsCancellationRequested) { Log("[!] Cancelled.", Color.Orange); return false; }
                            Log("[*] uploading " + img.Role + "  (" + (img.Size / 1024) + " KB @ 0x" + img.Address.ToString("X") + ")", Color.White);
                            int baseDone = done;
                            long thisSize = img.Size;
                            flasher.Write(img.Path, img.Address, x =>
                            {
                                int pct = 10 + (int)(60.0 * (baseDone + thisSize * x / 100.0) / grand);
                                SetProgress(pct, "Uploading " + img.Role);
                            });
                            done += (int)thisSize;
                        }
                    }
                    finally
                    {
                        try { flasher.Close(); } catch { }
                    }
                    Log("[OK] Bootloader uploaded", Color.LightGreen);

                    // 3) fastboot device ကို စောင့် (cancellable)
                    SetProgress(72, "Waiting for fastboot");
                    Log("[*] Waiting for fastboot device...", Color.Orange);
                    var fb = new Potato.Fastboot.Fastboot();
                    bool found = false;
                    for (int i = 0; i < 60; i++)
                    {
                        if (ct.IsCancellationRequested) { Log("[!] Cancelled.", Color.Orange); return false; }
                        try
                        {
                            string[] devs = Potato.Fastboot.Fastboot.GetDevices();
                            if (devs != null && devs.Length > 0) { found = true; break; }
                        }
                        catch (Exception ex) { Log("[!] device scan: " + ex.Message, Color.Orange); }
                        System.Threading.Thread.Sleep(1000);
                    }
                    if (!found)
                    {
                        Log("[x] Fastboot device did not appear - check testpoint/cable/driver, then retry.", Color.Red);
                        return false;
                    }

                    fb.Connect();
                    Log("[OK] Connected - reading device info...", Color.LightGreen);
                    SetProgress(80, "Reading device info");

                    try { Log("  • Serial    : " + fb.GetSerialNumber(), Color.White); } catch { }

                    var bsn = fb.Command("oem read_bsn");
                    if (bsn != null && bsn.Status == Potato.Fastboot.Fastboot.Status.Okay)
                        Log("  • Board ID  : " + bsn.Payload, Color.White);

                    var modelRes = fb.Command("oem get-product-model");
                    if (modelRes != null) Log("  • Model     : " + modelRes.Payload, Color.White);

                    var buildRes = fb.Command("oem get-build-number");
                    if (buildRes != null && buildRes.Payload != null)
                        Log("  • Build     : " + buildRes.Payload.Replace(":", ""), Color.White);

                    // FBLOCK state — unlocked ဖြစ်ရမယ် (မဖြစ်ရင် bootloader မှားနေတယ်)
                    var fblock = fb.Command("oem lock-state info");
                    string fpayload = fblock != null && fblock.Payload != null ? fblock.Payload : "";
                    bool unlocked = Regex.IsMatch(fpayload, @"FB[\w: ]{1,}UNLOCKED");
                    if (!unlocked)
                    {
                        var bd = fb.Command("oem backdoor info");
                        string bpayload = bd != null && bd.Payload != null ? bd.Payload : "";
                        unlocked = Regex.IsMatch(bpayload, @"FB[\w: ]{1,}UNLOCKED");
                    }
                    Log("  • FBLOCK    : " + (unlocked ? "UNLOCKED" : "LOCKED"), unlocked ? Color.LightGreen : Color.OrangeRed);
                    if (!unlocked)
                    {
                        Log("[x] FBLOCK is locked - wrong Kirin model selected (pick the model matching the phone).", Color.Red);
                        try { fb.Disconnect(); } catch { }
                        return false;
                    }

                    // လက်ရှိ key ရှိရင် ပြ
                    var oldKey = fb.Command("getvar:nve:WVLOCK");
                    if (oldKey != null && oldKey.Payload != null)
                    {
                        var m = Regex.Match(oldKey.Payload, @"[\w\d]{16}");
                        if (m.Success) Log("  • Current WVLOCK key: " + m.Value, Color.Orange);
                    }

                    // 4) unlock code ထုတ် + NVME ရေး
                    SetProgress(88, "Writing unlock data");
                    unlockCode = GenerateUnlockCode();
                    Log("[*] New unlock code: " + unlockCode, Color.Cyan);

                    if (!SetNvmeProp(fb, "FBLOCK", new byte[] { 1 }))
                    {
                        Log("[!] FBLOCK set failed - trying the alternative (hwdog/backdoor)...", Color.Orange);
                        try { fb.Command("oem hwdog certify set 1"); } catch { }
                        try { fb.Command("oem backdoor set 1"); } catch { }
                    }

                    if (!SetNvmeProp(fb, "WVLOCK", Encoding.ASCII.GetBytes(unlockCode)))
                    {
                        Log("[x] Could not write WVLOCK - unlock not completed.", Color.Red);
                        try { fb.Disconnect(); } catch { }
                        return false;
                    }

                    if (!SetNvmeProp(fb, "USRKEY", HashSha256(Encoding.ASCII.GetBytes(unlockCode))))
                    {
                        Log("[x] Could not write USRKEY - unlock not completed.", Color.Red);
                        try { fb.Disconnect(); } catch { }
                        return false;
                    }

                    // 5) reboot
                    SetProgress(96, "Rebooting");
                    Log("[*] Rebooting the phone...", Color.Orange);
                    try { fb.Command("reboot"); } catch { }
                    try { fb.Disconnect(); } catch { }

                    return true;
                }
                catch (Exception ex)
                {
                    Log("[✘] Unlock error: " + ex.Message, Color.Red);
                    Log("[i] Short the testpoint, reconnect and retry.", Color.Orange);
                    return false;
                }
            }, ct);

            if (ok)
            {
                SetProgress(100, "Completed");
                Log("[OK] Kirin bootloader unlock completed!", Color.LightGreen);
            }
            else
            {
                SetProgress(0, "Failed");
                unlockCode = "";
            }

            return unlockCode;
        }

    }
}
