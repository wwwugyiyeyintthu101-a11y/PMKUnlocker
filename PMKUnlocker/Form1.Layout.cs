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
    public partial class Form1
    {
        private void SetupProfessionalUI()
        {
            this.Text = "PMK Mobile Tool V" + UpdateChecker.CurrentVersion;
            this.Size = new Size(1310, 900);
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
            btnRefreshPorts.Click += async (s, e) => { RefreshMtkDetection(); await CheckAllDevicesAsync(true); };

            btnOpenDeviceManager = Create3DButton("⚙ Dev Manager", 395, 12, 130, 34, ButtonTheme.Purple);
            btnOpenDeviceManager.Click += (s, e) => { Process.Start(new ProcessStartInfo { FileName = "devmgmt.msc", UseShellExecute = true }); };

            btnDrivers = Create3DButton("🛠 Drivers", 530, 12, 100, 34, ButtonTheme.Cyan);
            btnDrivers.Click += (s, e) => ShowDriverHelper();

            toolTipMain = new ToolTip();
            Tip(btnOpenDeviceManager, "Open Windows Device Manager. Select the COM port in MediaTek.");
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
                Text = "Platform: ADB",
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
                Padding = new Padding(0, 10, 10, 6)
            };
            // badge စာတန်း အပြည့်ပေါ်ဖို့: panel ကို text အရှည်နဲ့ တိုင်းချိန် (AutoEllipsis "…" မကျအောင်)
            // email ရှည်တဲ့ account အတွက်ပဲ — window design width 1310 က ဘယ်ဘက် cluster 758 ကို နှုတ်ထားတဲ့ အများဆုံး
            int badgeNeed = TextRenderer.MeasureText(lblLicenseInfo.Text, lblLicenseInfo.Font).Width + 26;
            int badgeMax = 1310 - 758;   // 552
            if (badgeNeed > panelRightInfo.Width)
                panelRightInfo.Width = Math.Max(panelRightInfo.Width, Math.Min(badgeNeed, badgeMax));
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

            tabAdb = new TabPage("ADB") { BackColor = Color.FromArgb(28, 31, 38) };
            tabFb = new TabPage("Fastboot") { BackColor = Color.FromArgb(28, 31, 38) };
            tabMtk = new TabPage("MediaTek") { BackColor = Color.FromArgb(28, 31, 38) };
            tabQc = new TabPage("Qualcomm") { BackColor = Color.FromArgb(28, 31, 38) };
            tabSamsung = new TabPage("Samsung") { BackColor = Color.FromArgb(28, 31, 38) };
            tabSpd = new TabPage("Spreadtrum") { BackColor = Color.FromArgb(28, 31, 38) };
            tabHisi = new TabPage("HiSilicon") { BackColor = Color.FromArgb(28, 31, 38) };

            tabControl.TabPages.AddRange(new TabPage[] { tabAdb, tabFb, tabMtk, tabQc, tabSamsung, tabSpd, tabHisi });

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

            // Current slot တစ်ချက်ချင်း toggle — A ဆို B, B ဆို A (set_active)
            btnFbSwitchSlot = Create3DButton("🔁 Switch Slot (A⇄B)", UiX(4), UiY(1), UiBtnW, UiBtnH, ButtonTheme.Cyan);
            btnFbSwitchSlot.Click += BtnFbSwitchSlot_Click;
            if (toolTipMain != null)
                Tip(btnFbSwitchSlot, "Current slot ဖတ်ပြီး A↔B တစ်ချက်ချင်း ပြောင်းပေးတယ် (getvar current-slot + set_active).");

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
            grpAdbApps.Height = 104;                                  // row ၂ ခု (Install APK အောက် တစ်တန်း)
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

            Panel grpFbInfo = CreateGroupPanel("FASTBOOT · INFO & FLASH", UiX(0), UiY(0), 4, btnFbInfo, btnFbArb, btnFbFlash, btnFbBootTemp);
            Panel grpFbOps = CreateGroupPanel("FASTBOOT · UNLOCK & REBOOT", UiX(0), UiY(2), 5, btnFbErase, btnFbRebootSystem, btnFbRebootRecovery, btnFbRebootEdl, btnFbSwitchSlot);
            Panel grpAdbReboot = CreateGroupPanel("ADB · REBOOT", UiX(0), UiY(3), 3, lblAdbReboot, cmbAdbReboot, btnAdbRebootExecute);

            // --- MI FLASH · Xiaomi fastboot (MST style, flash_all.bat) ---
            grpMiFlash = new Panel
            {
                Location = new Point(UiX(0), UiY(4)),
                Size = new Size(840, 500),
                BackColor = Color.FromArgb(23, 26, 32),
                Tag = "grp"
            };
            EnsureThemePaint(grpMiFlash);
            grpMiFlash.Controls.Add(new Label
            {
                Text = "MI FLASH · XIAOMI FASTBOOT FLASHING",
                Location = new Point(9, 4),
                AutoSize = true,
                ForeColor = Color.FromArgb(86, 145, 250),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                Tag = "accent"
            });

            btnMiRomFolder = Create3DButton("📄 flash_all.bat", 0, 0, 150, UiBtnH, ButtonTheme.Cyan);
            btnMiRomFolder.Click += BtnMiRomFolder_Click;
            btnMiRomFolder.Location = new Point(10, 26);
            grpMiFlash.Controls.Add(btnMiRomFolder);

            lblMiRomPath = new Label
            {
                Location = new Point(168, 34),
                Size = new Size(330, 30),
                AutoEllipsis = true,
                ForeColor = Color.FromArgb(150, 162, 180),
                Font = new Font("Segoe UI", 8.5f),
                Text = "(flash_all.bat ကို ရွေးပါ)"
            };
            grpMiFlash.Controls.Add(lblMiRomPath);

            chkMiEraseFirst = new CheckBox
            {
                Text = "Erase first",
                Location = new Point(505, 34),
                AutoSize = true,
                Checked = true,
                ForeColor = Color.FromArgb(255, 200, 90),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            };
            grpMiFlash.Controls.Add(chkMiEraseFirst);

            chkMiAutoReboot = new CheckBox
            {
                Text = "Auto Reboot",
                Location = new Point(620, 34),
                AutoSize = true,
                Checked = true,
                ForeColor = Color.FromArgb(0, 230, 118),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            };
            grpMiFlash.Controls.Add(chkMiAutoReboot);

            btnMiFlashGo = Create3DButton("⚡ FLASH", 0, 0, 115, UiBtnH, ButtonTheme.Green);
            btnMiFlashGo.Click += BtnMiFlash_Click;
            btnMiFlashGo.Location = new Point(715, 26);
            grpMiFlash.Controls.Add(btnMiFlashGo);

            dgvMiParts = new DataGridView
            {
                Location = new Point(10, 68),
                Size = new Size(820, 422),
                BackColor = Color.FromArgb(30, 33, 40),
                ForeColor = Color.FromArgb(220, 226, 236),
                BackgroundColor = Color.FromArgb(30, 33, 40),
                BorderStyle = BorderStyle.FixedSingle,
                GridColor = Color.FromArgb(45, 50, 60),
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                ReadOnly = true,
                RowHeadersVisible = false,
                MultiSelect = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                EnableHeadersVisualStyles = false,
                Font = new Font("Segoe UI", 8f),
                ColumnHeadersHeight = 21,
                RowTemplate = { Height = 19 }
            };
            dgvMiParts.DefaultCellStyle.BackColor = Color.FromArgb(30, 33, 40);
            dgvMiParts.DefaultCellStyle.SelectionBackColor = Color.FromArgb(60, 84, 130);
            dgvMiParts.DefaultCellStyle.SelectionForeColor = Color.White;
            dgvMiParts.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(38, 44, 54);
            dgvMiParts.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
            dgvMiParts.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 8f, FontStyle.Bold);
            var miColChk = new DataGridViewCheckBoxColumn
            {
                HeaderText = "",
                Width = 28,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                ReadOnly = false
            };
            var miColPart = new DataGridViewTextBoxColumn
            {
                HeaderText = "Partition Name",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                FillWeight = 26,
                SortMode = DataGridViewColumnSortMode.NotSortable
            };
            var miColFile = new DataGridViewTextBoxColumn
            {
                HeaderText = "File Name",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                FillWeight = 24,
                SortMode = DataGridViewColumnSortMode.NotSortable
            };
            var miColOffset = new DataGridViewTextBoxColumn
            {
                HeaderText = "Offset",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                FillWeight = 16,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleRight }
            };
            var miColLength = new DataGridViewTextBoxColumn
            {
                HeaderText = "Length",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                FillWeight = 15,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleRight }
            };
            var miColSize = new DataGridViewTextBoxColumn
            {
                HeaderText = "Size",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                FillWeight = 9,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleRight }
            };
            var miColType = new DataGridViewTextBoxColumn
            {
                HeaderText = "Phys",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                FillWeight = 10,
                SortMode = DataGridViewColumnSortMode.NotSortable
            };
            dgvMiParts.Columns.AddRange(miColChk, miColPart, miColFile, miColOffset, miColLength, miColSize, miColType);
            dgvMiParts.ColumnHeaderMouseClick += (s2, e2) =>
            {
                if (e2.ColumnIndex != 0 || dgvMiParts.Rows.Count == 0) return;
                bool all = true;
                foreach (DataGridViewRow r in dgvMiParts.Rows)
                    if (!(r.Cells[0].Value is bool b && b)) { all = false; break; }
                foreach (DataGridViewRow r in dgvMiParts.Rows) r.Cells[0].Value = !all;
            };
            grpMiFlash.Controls.Add(dgvMiParts);
            if (toolTipMain != null)
                Tip(btnMiFlashGo, "flash_all.bat အတိုင်း tick ထားတဲ့ ops တွေကို fastboot နဲ့ flash — MST style log။");
            if (toolTipMain != null)
                Tip(btnMiRomFolder, "MIUI Fastboot ROM ရဲ့ flash_all.bat (flash_all_lock.bat) ကို ရွေး — op list ပြောင်းပြီး flash။");

            tabAdb.Controls.AddRange(new Control[] { grpAdbInfo, grpAdbApps, grpAdbLock, grpAdbReboot });
            tabFb.Controls.AddRange(new Control[] { grpFbInfo, grpFbOps, grpMiFlash });


            
            BuildMediaTekTab();

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

            // Firmware flash panel (edl.py) — title label ဖယ် (user request)
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

            // ---- Flash Option row — UNLOCK ညာဘက် pocket ထဲ (flash block အပေါ် 55px တက်ပြီး) ----
            chkQcBackupEfsFirst = new CheckBox
            {
                Text = "Backup EFS (persist/modemst) first",
                Location = new Point(375, 100),
                AutoSize = true,
                ForeColor = Color.FromArgb(255, 200, 90),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            };

            chkQcSkipUserdata = new CheckBox
            {
                Text = "Skip userdata",
                Location = new Point(610, 100),
                AutoSize = true,
                ForeColor = Color.FromArgb(255, 200, 90),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            };

            chkQcResetFrpAfter = new CheckBox
            {
                Text = "Reset FRP after flash",
                Location = new Point(375, 126),
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

            // RESTORE — backup folder (rl dump / EFS dump) ထဲက <partition>.bin တွေကို ပြန်ရေး
            btnQcRestoreBackup = Create3DButton("♻ Restore Backup", 0, 0, UiBtnW, UiBtnH, ButtonTheme.Green);
            btnQcRestoreBackup.Click += BtnQcRestoreBackup_Click;
            if (toolTipMain != null)
                Tip(btnQcRestoreBackup,
                    "Backup folder ထဲက <partition>.bin ဖိုင်တွေကို edl.py w နဲ့ ပြန်ရေးမယ်။\r\n" +
                    "Partition size ကို Read Info / GPT နဲ့ တိုက်စစ်ပြီး မကိုက်ရင် ကျော်မယ်။\r\n" +
                    "ဖုန်းပိုင်ရှင် ခွင့်ပြုချက် ရှိမှ လုပ်ပါ။");

            // EFS Restore (IMEI repair) — EFS backup folder ထဲက persist/modemst1/modemst2/fsg/fsc ကို ပြန်ရေး
            btnQcEfsRestore = Create3DButton("♻ EFS Restore (IMEI)", 0, 0, UiBtnW, UiBtnH, ButtonTheme.Green);
            btnQcEfsRestore.Click += BtnQcEfsRestore_Click;
            if (toolTipMain != null)
                Tip(btnQcEfsRestore,
                    "EFS backup folder (persist/modemst1/modemst2/fsg/fsc) ကို edl.py w နဲ့ ပြန်ရေးပြီး\r\n" +
                    "IMEI/baseband ပျက်တာ ပြန်ကောင်းစေမယ်။ Partition size မကိုက်ရင် ကျော်မယ်။\r\n" +
                    "ဒီ backup က ဒီဖုန်းနဲ့ မကိုက်ရင် brick ဖြစ်နိုင်လို့ ဖုန်းပိုင်ရှင်ခွင့်ပြုချက်နဲ့ပဲ လုပ်ပါ။");

            // IMEI Read (EFS) — EFS backup ဖိုင်တွေထဲက IMEI ကို heuristic scan
            btnQcImeiReadEfs = Create3DButton("📱 IMEI Read (EFS)", 0, 0, UiBtnW, UiBtnH, ButtonTheme.Cyan);
            btnQcImeiReadEfs.Click += BtnQcImeiReadEfs_Click;
            if (toolTipMain != null)
                Tip(btnQcImeiReadEfs,
                    "EFS backup folder (persist/modemst1/modemst2/fsg/fsc .bin) ထဲက IMEI ကို\r\n" +
                    "heuristic scan (plain + hex-encoded ASCII, Luhn) နဲ့ ရှာမယ်။ sticker/About phone နဲ့ ပြန်တိုက်ပါ။");

            Panel grpQcRestore = CreateGroupPanel("RESTORE / IMEI REPAIR", UiX(0), UiY(5), 3, btnQcRestoreBackup, btnQcEfsRestore, btnQcImeiReadEfs);
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
            cmbQcModel = new ComboBox { Location = new Point(455, 10), Size = new Size(190, 26), DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(38, 42, 50), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9f) };
            lblQcLoaderStatus = new Label { Text = "Auto Detect (saved / auto loader)", Location = new Point(655, 14), AutoSize = false, Size = new Size(195, 20), ForeColor = Color.FromArgb(86, 145, 250), Font = new Font("Segoe UI", 7.5f), AutoEllipsis = true };

            grpQcLoaderPicker.Controls.AddRange(new Control[] { lblQcLoaderTitle, lblBrandTag, cmbQcBrand, lblModelTag, cmbQcModel, lblQcLoaderStatus });
            cmbQcBrand.SelectedIndexChanged += CmbQcBrand_SelectedIndexChanged;
            cmbQcModel.SelectedIndexChanged += CmbQcModel_SelectedIndexChanged;
            LoadQcLoaderDb();   // event wire ပြီးမှ — Auto Detect default select က model combo ပါ fill အောင်

            // flash block — UNLOCK (UiY(2)+64=159) အောက် 16px gap အထိ 55px တက်
            txtQcFirmware.Location = new Point(UiX(0), UiY(4) + 22);
            btnQcPickFirmware.Location = new Point(UiX(0) + 400, UiY(4) + 20);
            btnQcStartFlash.Location = new Point(UiX(0) + 510, UiY(4) + 15);
            chkQcAutoReboot.Location = new Point(UiX(0) + 697, UiY(4) + 23);

            tabQc.Controls.AddRange(new Control[] {
    grpQcInfo, grpQcBackup, grpQcUnlock, grpQcLoaderPicker, grpQcRestore,
                txtQcFirmware, btnQcPickFirmware, btnQcStartFlash, chkQcAutoReboot,
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

            // EFS / NV backup + restore (ADB root) — heimdall က download-mode ဖတ်လို့မရလို့
            // efs/modem/persist/param ကို su + dd နဲ့ ဖတ်/ရေးတယ်
            btnSamEfsBackup = Create3DButton("💾 EFS Backup", 0, 0, UiBtnW, UiBtnH, ButtonTheme.Cyan);
            btnSamEfsBackup.Click += BtnSamEfsBackup_Click;
            Tip(btnSamEfsBackup, "ADB + root: efs / modem / persist / param partitions ကို .bin အဖြစ် dump (IMEI/NV backup).");
            btnSamEfsRestore = Create3DButton("♻ EFS Restore", 0, 0, UiBtnW, UiBtnH, ButtonTheme.Orange);
            btnSamEfsRestore.Click += BtnSamEfsRestore_Click;
            Tip(btnSamEfsRestore, "ADB + root: backup folder ထဲက efs/modem/persist/param .bin တွေကို ပြန်ရေး (size တိုက်စစ်ပြီးမှ).");
            Panel grpSamEfs = CreateGroupPanel("EFS BACKUP / RESTORE (ADB ROOT)", UiX(0), UiY(4), 2, btnSamEfsBackup, btnSamEfsRestore);

            txtSamFirmware.Location = new Point(UiX(0), UiY(4) + 77);
            btnSamPickFirmware.Location = new Point(UiX(0) + 400, UiY(4) + 75);
            btnSamStartFlash.Location = new Point(UiX(0) + 510, UiY(4) + 70);
            btnSamDownloadPit.Location = new Point(UiX(0) + 697, UiY(4) + 70);
            lblSamHint.Location = new Point(UiX(0), UiY(8) + 4);

            tabSamsung.Controls.AddRange(new Control[] {
    grpSamInfo, grpSamUnlock, grpSamMtp, grpSamReboot, grpSamDl, grpSamEfs,
                txtSamFirmware, btnSamPickFirmware, btnSamStartFlash, btnSamDownloadPit,
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

            // row0 — ၃ group ကို အညီအမျှ ခွဲ (gap 100/100) — ညာဘက် pocket 135px ပပျောက်
            Panel grpSpdInfo = CreateGroupPanel("INFO", UiX(0), UiY(0), 1, btnSpdInfo);
            Panel grpSpdUnlock = CreateGroupPanel("UNLOCK", UiX(0) + 270, UiY(0), 1, btnSpdFrp);
            Panel grpSpdReboot = CreateGroupPanel("REBOOT", UiX(0) + 540, UiY(0), 2, btnSpdReboot, btnSpdFastboot);
            // row4 — Diag ကို REBOOT အောက် column-align (x=555)၊ PARTITIONS 3col နဲ့ တန်းညီ
            Panel grpSpdDiag = CreateGroupPanel("UNLOCK (Diag)", UiX(0) + 540, UiY(4), 2, btnSpdDiagFrp, btnSpdDiagUserlock);
            Panel grpSpdPart = CreateGroupPanel("PARTITIONS (spd_dump)", UiX(0), UiY(4), 3,
                                                btnSpdReadGpt, btnSpdReadPart, btnSpdWritePart, btnSpdErasePart);

            txtSpdFirmware.Location = new Point(UiX(0), UiY(2) + 13);
            btnSpdPickFirmware.Location = new Point(UiX(0) + 400, UiY(2) + 11);
            btnSpdDirectFlash.Location = new Point(UiX(0) + 540, UiY(2) + 6);   // x=555 — REBOOT/Diag column နဲ့ တန်းညီ
            lblSpdHint.Location = new Point(UiX(0), UiY(2) + 50);

            tabSpd.Controls.AddRange(new Control[] {
    grpSpdInfo, grpSpdUnlock, grpSpdReboot, grpSpdPart, grpSpdDiag,
                txtSpdFirmware, btnSpdPickFirmware, btnSpdDirectFlash
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
                Location = new Point(UiX(0), UiY(5) + 72 + 4),
                AutoSize = true,
                // tab အကျယ်ထက် ကျယ်ရင် ညာဘက် cut — wrap ပြောင်း (TabNeededHeight က အမြင့် ချိန်ပေးတယ်)
                MaximumSize = new Size(850, 0),
                ForeColor = Color.FromArgb(150, 165, 185),
                Font = new Font("Segoe UI", 8.5f)
            };

            // cols=5 — content နဲ့ ပြည့်အောင် (cols4 က tab ညာဘက် pocket 215px ကျန်)
            Panel grpHisiUnlock = CreateGroupPanel("KIRIN UNLOCK", UiX(0), UiY(0), 5,
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

            // MST style: title → percent → rounded bar → Status | ticks | Speed
            lblProgressTitle = new Label
            {
                Text = "Operation Progress",
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = Color.FromArgb(150, 162, 180),
                TextAlign = ContentAlignment.MiddleCenter
            };

            lblProgressPercent = new Label
            {
                Text = "0%",
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                ForeColor = Color.White,
                TextAlign = ContentAlignment.MiddleCenter
            };

            pbarTrack = new Panel
            {
                BackColor = Color.FromArgb(38, 42, 50)
            };
            pbarTrack.Paint += PbarTrack_Paint;

            lblStatusCaption = new Label
            {
                Text = "Status",
                AutoSize = true,
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = Color.FromArgb(130, 140, 156)
            };

            lblProgressStatus = new Label
            {
                Text = "Ready",
                AutoSize = true,
                Font = new Font("Segoe UI", 9.2f, FontStyle.Bold),
                ForeColor = Color.FromArgb(86, 145, 250)
            };

            pbarTicks = new Panel
            {
                BackColor = Color.FromArgb(26, 29, 35)
            };
            pbarTicks.Paint += PbarTicks_Paint;

            lblSpeedBadge = new Label
            {
                Text = "Speed: 0.0000 MB/s",
                Font = new Font("Segoe UI", 9.2f, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 255, 128),
                TextAlign = ContentAlignment.MiddleRight
            };

            panelProgress.Controls.AddRange(new Control[] {
                lblProgressTitle, lblProgressPercent, pbarTrack,
                lblStatusCaption, lblProgressStatus, pbarTicks, lblSpeedBadge });
            panelProgress.Resize += (s, e) => LayoutProgressSection();
            LayoutProgressSection();

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
                BorderStyle = BorderStyle.None,
                HideSelection = false,   // search match ကို focus မရှိလည်း highlight မြင်ရအောင်
                Margin = new Padding(8, 6, 8, 4)   // table cell အနားသတ် — စာသားက ဘောင်နဲ့ ကပ်မနေရ
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
            // နောက်ဆုံး "Setup" က ShopPanel က ထည့်တဲ့ "Setup / Backups" tab (index 7) —
            // platform bar မရှိခဲ့ရင် အဲဒီ tab ကို ဘယ်ကမှ ရောက်လို့မရဘူး
            string[] platformNames = { "ADB", "Fastboot", "MediaTek", "Qualcomm", "Samsung", "Spreadtrum", "HiSilicon", "⚙ Setup" };
            platformBtns = new List<Button>();
            for (int i = 0; i < platformNames.Length; i++)
            {
                int idx = i;
                Button pb = new Button
                {
                    Text = platformNames[i],
                    // 8 button ဆိုတာ ညာဘက် panel (~900px) ထဲ ဆံ့အောင် width/margin ကို ချိန်ထားတယ်
                    Size = new Size(106, 32),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(38, 44, 54),
                    ForeColor = Color.White,
                    Font = new Font("Segoe UI", 8.75f, FontStyle.Bold),
                    Margin = new Padding(0, 0, 3, 2),
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
                        RefreshMtkDetection();   // descriptor fail ကျန်ရင် port ပြန်ပေါ်အောင်
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
                RowCount = 2,
                BackColor = Color.FromArgb(18, 20, 24)
            };
            logLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));   // STOP / Clear Log (ခလုတ် အပြည့်မြင်ရဖို့)
            logLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));   // LOG

            logLayout.Controls.Add(panelLogButtons, 0, 0);
            logLayout.Controls.Add(rtbLog, 0, 1);
            splitMain.Panel1.Controls.Add(logLayout);   // ဘယ်ဘက်တိုင် — LOG (UNLOCKTOOL)

            // Root layout — header (80px = panelTopHeader နဲ့ တူရမည်; 60F ဆိုရင် row2 labels ဖြတ်) + split
            rootLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                BackColor = Color.FromArgb(18, 20, 24)
            };
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 80F));
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 80F));   // progress strip
            panelTopHeader.Dock = DockStyle.Fill;
            rootLayout.Controls.Add(panelTopHeader, 0, 0);
            rootLayout.Controls.Add(splitMain, 0, 1);
            rootLayout.Controls.Add(panelProgress, 0, 2);   // bottom strip — spans both columns

            this.Controls.Add(rootLayout);
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
            SetDoubleBuffered(pbarTrack);
            SetDoubleBuffered(pbarTicks);
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
            if (lblProgressTitle != null) lblProgressTitle.ForeColor = lightTheme ? Color.FromArgb(92, 100, 118) : Color.FromArgb(150, 162, 180);
            if (lblProgressPercent != null) lblProgressPercent.ForeColor = lightTheme ? Color.FromArgb(35, 38, 48) : Color.White;
            if (lblStatusCaption != null) lblStatusCaption.ForeColor = lightTheme ? Color.FromArgb(100, 108, 124) : Color.FromArgb(130, 140, 156);
            if (pbarTicks != null) { pbarTicks.BackColor = chromeBg2; pbarTicks.Invalidate(); }
            if (pbarTrack != null) pbarTrack.Invalidate();

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
                else if (c is CheckBox cb)
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

        // ================= CONTEXT MENU ACTIONS =================
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
        // Tab တိုင်းရဲ့ ခလုတ်တွေ တစ်ပုံစံတည်း နေရာကျအောင် — button 150x32 (MST density)၊
        // column step 155 (x = 15, 170, 325, 480, 635, 790)၊ row step 40 (y = 15, 55, 95, 135, 175)
        private const int UiBtnW = 150;
        private const int UiBtnH = 32;
        private const int UiColX0 = 15;
        private const int UiColStep = 155;
        private const int UiRowY0 = 15;
        private const int UiRowStep = 40;
        private static int UiX(int col) { return UiColX0 + col * UiColStep; }
        private static int UiY(int row) { return UiRowY0 + row * UiRowStep; }

        public enum ButtonTheme { Cyan, Green, Orange, Red, Purple }

        // ================= GROUP PANEL (unlock tool ပုံစံ အုပ်စုဘောင်) =================
        // ခလုတ်တွေကို ခေါင်းစဉ်ပါတဲ့ ဘောင်တစ်ခုစီ ခွဲထည့်တယ် — auto grid (150x32၊ col step 155၊ row step 40)
        private Panel CreateGroupPanel(string title, int x, int y, int cols, params Control[] children)
        {
            int rows = (children.Length + cols - 1) / cols;
            int w = cols * UiColStep + 15;
            int h = 64 + (rows - 1) * UiRowStep;

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
                c.Location = new Point(10 + (i % cols) * UiColStep, 26 + (i / cols) * UiRowStep);
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

    }
}
