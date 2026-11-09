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
        // Windows က device descriptor ဖတ်မရတဲ့ node တွေ (VID/PID မရ → MediaTek VCOM driver
        // မတွဲနိုင် → COM port မပေါ်) — present ဖြစ်နေရင် instance id တွေ သိမ်းထားတယ်
        private volatile string cachedUsbFailedIds = "";
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
                                    "Where-Object { $_.InstanceId -like '*VID_0E8D*' -or $_.FriendlyName -like '*Device Descriptor Request Failed*' -or $_.FriendlyName -like '*UsbDk*' } | ForEach-Object { " +
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
                    List<string> usbFailed = new List<string>();

                    foreach (string line in outText.Split('\n'))
                    {
                        string up = line.ToUpperInvariant();
                        // descriptor ဖတ်မရတဲ့ node — VID/PID မရလို့ driver မတွဲနိုင်ဘဲ ကျန်ခဲ့တာ
                        if (up.Contains("DEVICE DESCRIPTOR REQUEST FAILED"))
                        {
                            string fid = line.Split('|')[0].Trim();
                            if (fid.Length > 0) usbFailed.Add(fid);
                            continue;
                        }
                        // UsbDk redirect node (DA session) — instance id ထဲ VID_0E8D မပါနိုင်
                        // (UsbDk namespace) → MTK device အဖြစ် သတ်မှတ်ပြီး header ပြဖို့
                        if (up.Contains("USBDK"))
                        {
                            string[] uparts = line.Split('|');
                            string usvc = uparts.Length > 1 ? uparts[1].Trim() : "";
                            if (string.IsNullOrEmpty(usvc)) usvc = "UsbDk";
                            if (!pidSvcs.ContainsKey("2001"))
                            {
                                pidSvcs["2001"] = new List<string>();
                                pidOrder.Add("2001");
                            }
                            if (!pidSvcs["2001"].Exists(x => string.Equals(x, usvc, StringComparison.OrdinalIgnoreCase)))
                                pidSvcs["2001"].Add(usvc);
                            continue;
                        }
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

                    cachedUsbFailedIds = string.Join("\n", usbFailed);
                    cachedMtkModeText = string.Join(", ", modes);
                    cachedMtkComPort = comPort;
                }
                catch
                {
                    cachedMtkModeText = "";
                    cachedMtkComPort = "";
                    cachedUsbFailedIds = "";
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

        // ============ USB DESCRIPTOR RECOVERY (COM port ပြန်ပေါ်အောင်) ============
        // op ပြီးတဲ့အခါ phone က USB ကနေ ထွက်တဲ့အခါ Windows က device descriptor ဖတ်မရတတ် —
        // ဒါဆို VID/PID မရ → MediaTek VCOM driver မတွဲနိုင် → "Unknown USB Device" ပဲ ကျန်ပြီး
        // COM port မပေါ်တော့ဘူး (user မြင်ရတဲ့ "No Port")။ App က admin ဖြစ်လို့ ဒီ failed node ကို
        // disable/enable ပြီး Windows ကို ပြန် enumerate ခိုင်းတယ် — phone ချိတ်ထားရင် COMx ပြန်ပေါ်မယ်။
        private DateTime usbRecoveryBurstUtc = DateTime.MinValue; // လက်ရှိ error တစ်ကြောင်း စချိန်

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
            // tabHisi — partition grid မလို (user request); ကျန် tab အားလုံး ပြ
            bool show = (page == tabQc || page == tabSamsung ||
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
            // op စာသားက "FRP / account lock: Xiaomi account bypass (no need vpn)" — "frp" rule ရှေ့မှာ ထားမှ
            // မဟုတ်ရင် frp rule က အရင် match ဖြစ်ပြီး Mi Account button ရောက်မသွားဘူး
            new[] { "account bypass", "Mi Account Bypass" },
            new[] { "account remove", "Mi Account Bypass" },
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
                        Log("[!] Boot slot set: slot must be a or b — '" + slot + "' (cancelled)", Color.Orange);
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
                        Log("[!] ADB device not found — enable USB debugging and connect the cable.", Color.Orange);
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
                        Log("[!] ADB device not found — enable USB debugging and connect the cable.", Color.Orange);
                        return;
                    }
                    await ExecuteCommandCleanAsync("adb.exe", "shell dumpsys lock_settings", "Read user locks", false, true);
                    string st = await ExecuteCommandQuickAsync("adb.exe", "shell locksettings get-disabled");
                    Log("[i] locksettings get-disabled: " + (string.IsNullOrWhiteSpace(st) ? "(empty)" : st.Trim()), Color.Gray);
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
                            Log("[i] Font install: skipped without picking an APK (cancel).", Color.Gray);
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
                        Log("[!] ADB device not found — enable USB debugging and connect the cable.", Color.Orange);
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
                        Log("[i] find_device prop not found — prop name may differ on MIUI/HyperOS (read-only, no change made)", Color.Gray);
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
                    // x = width-relative — file row ရဲ့ ညာဘက်အဆုံးနဲ့ ညီ (fixed 817 က window ချုံ့ရင် cut)
                    btDetect.Location = new Point(Math.Max(500, page.ClientSize.Width - 111), y - 1);
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
                if (page == tabSpd)
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
                // MTK SoC token ကို DA catalog နဲ့ ချိတ် — Read Info/Detect ကနေ BROM မတိုင်ခင် loader သိထားအောင်
                NoteMediaTekChipFromAdb(soc, "");
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
                _pickerHost[tabSamsung] = ShiftTabContentDown(tabSamsung, 64);
                _pickerHost[tabSpd] = ShiftTabContentDown(tabSpd, 94);
                _pickerHost[tabAdb] = ShiftTabContentDown(tabAdb, 40);
                _pickerHost[tabHisi] = ShiftTabContentDown(tabHisi, 40);
                // QC: device list မပြ (loader picker ပဲ) — panel 76px ဖြစ်လို့ content 80
                _pickerHost[tabQc] = ShiftTabContentDown(tabQc, 80);

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
                    // tab အကျယ်အလိုက် width — construction-time 930 hardcode က window ချုံ့ရင် cut ခံရမယ်
                    int qw = Math.Max(600, tabQc.ClientSize.Width - 30);
                    grpQcLoaderPicker.Size = new Size(qw, 76);
                    grpQcLoaderPicker.BringToFront();
                    // row2 — ပုံတူ Loader file row (LoadEdlPaths ပြီးမှ ဆိုလို့ saved loader ပါ ပြ)
                    if (qcFileRow == null)
                    {
                        qcFileRow = BuildFilePickerRow(grpQcLoaderPicker, 9, 46, qw - 18, "Loader",
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

    }
}
