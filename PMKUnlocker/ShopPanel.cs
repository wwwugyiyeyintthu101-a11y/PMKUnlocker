#nullable disable
using System.Text.Json;

namespace PMKUnlocker;

public partial class Form1
{
    private string selectedAdbSerial = "", selectedFastbootSerial = "";

    private void InvalidateAdbDeviceCache()
    {
        deviceProps = null;
        suPathCache.Clear();
        if (dgvPartitions.Rows.Count > 0 || loadedPartitions.Count > 0) ClearPartitionData();
    }

    private readonly Dictionary<string, string> suPathCache = new(StringComparer.Ordinal);

    // device မှာ PATH su ရှိ/မရှိ စစ် — မရှိရင် GhostLock temp-root client (/data/local/tmp/su) ကို သုံး (serial cache)
    private async Task<string> ResolveDeviceSuPathAsync(string serial)
    {
        // PATH su ("") = တည်ငြိမ် → cache ချက်ချင်းပြန်သုံး။ tmpsu ကတော့ root မရခိုက် cache ဖြစ်ထားနိုင် →
        // အမြဲ revalidate (command -v 1 ခု) — Enforcing မှာ tmpsu EACCES ဖြစ်တာ ကာကွယ်ဖို့
        if (suPathCache.TryGetValue(serial, out string cached) && cached.Length == 0) return cached;
        string path = "";
        try
        {
            string real = (await ReviewSafety.RunQuickAsync(ResolveToolPath("adb.exe"),
                "-s \"" + serial + "\" shell command -v su 2>/dev/null || echo NO_SU")).Trim();
            if (!real.Contains("NO_SU") && real.Contains("/"))
            {
                path = ""; // PATH ထဲမှာ စစ် su ရှိ
            }
            else
            {
                string tmp = (await ReviewSafety.RunQuickAsync(ResolveToolPath("adb.exe"),
                    "-s \"" + serial + "\" shell ls /data/local/tmp/su 2>/dev/null")).Trim();
                if (tmp.Contains("/data/local/tmp/su")) path = "/data/local/tmp/su";
            }
        }
        catch { }
        suPathCache[serial] = path;
        return path;
    }

    private async Task<string> BindDeviceArgumentsAsync(string fileName, string arguments)
    {
        string tool = Path.GetFileName(fileName).ToLowerInvariant();
        if (tool != "adb.exe" && tool != "fastboot.exe") return arguments;
        string command = arguments.Trim();
        // server-level adb/fb commands က device serial မလို — binding ကျော်
        if (command == "devices" || command == "devices -l" || command == "version" || command == "--version" ||
            command == "kill-server" || command == "start-server" || command == "wait-for-device" ||
            command == "reconnect" || command == "reconnect device" || command == "usb")
            return arguments;
        bool adb = tool == "adb.exe";
        string output = await ReviewSafety.RunQuickAsync(ResolveToolPath(fileName), "devices");
        string preferred = adb ? selectedAdbSerial : selectedFastbootSerial;
        preferred = ShopServices.ResolvePreferred(output, adb, preferred);
        string serial = ShopServices.SelectSerial(output, adb, preferred);
        if (adb)
        {
            if (!string.Equals(serial, selectedAdbSerial, StringComparison.Ordinal))
            {
                string prev = selectedAdbSerial;
                selectedAdbSerial = serial;
                InvalidateAdbDeviceCache();
                if (prev.Length > 0) Log("[i] ADB device: " + prev + " -> " + serial, Color.Orange);
            }
        }
        else selectedFastbootSerial = serial;
        // PATH ထဲမှာ su မရှိရင် (GhostLock temp root) — `shell su -c` ကို absolute path ပြောင်းပေး။
        // multi-word -c value ကို outer quotes + escaped inner quotes ပေး — adb join ပြီးတဲ့အခါ
        // remote shell က quote ပြန် parse လုပ်နိုင်အောင် (single-token ကျ ဘာမှ မပြောင်း)
        if (adb && command.Contains("shell su -c"))
        {
            string suPath = await ResolveDeviceSuPathAsync(serial);
            if (suPath.Length > 0)
            {
                string marker = "shell su -c ";
                int idx = arguments.IndexOf(marker, StringComparison.Ordinal);
                string head = idx >= 0 ? arguments.Substring(0, idx) : arguments;
                string rest = idx >= 0 ? arguments.Substring(idx + marker.Length) : "";
                if (rest.IndexOf(' ') >= 0)
                {
                    string inner = rest.Contains("\"") ? rest.Replace("\"", "\\\"") : "\"" + rest + "\"";
                    arguments = head + "shell \"" + suPath + " -c " + inner + "\"";
                }
                else
                {
                    arguments = head + "shell " + suPath + " -c " + rest;
                }
            }
        }
        return "-s \"" + serial + "\" " + arguments;
    }
    private readonly string[] shopToolNames = { "adb.exe", "fastboot.exe", "heimdall.exe", "fh_loader.exe", "QSaharaServer.exe", "python.exe", "mtk.exe", "edl.py", "spd_dump.exe", "unpac.exe" };

    private Dictionary<string, long> CurrentLayout()
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in loadedPartitions)
            if (!result.TryAdd(part.Name, ReviewSafety.ParseSize(part.LengthHex)))
                throw new InvalidDataException("Duplicate GPT partition: " + part.Name);
        if (result.Count == 0) throw new InvalidDataException("Device GPT was not read. Operation stopped.");
        return result;
    }

    private async Task SaveBackupManifestAsync(string directory, IEnumerable<string> partitions)
    {
        var layout = CurrentLayout();
        var names = partitions.ToArray();
        await Task.Run(() => ShopServices.WriteBackup(directory, "MTK", layout, names, Application.ProductVersion));
        RegisterBackup(directory);
        Log("[OK] Backup manifest and SHA-256 saved. Device identity: unverified.", Color.Cyan);
    }

    private void RegisterBackup(string directory)
    {
        string index = ShopServices.DataPath("backups.json");
        var paths = File.Exists(index) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(index)) ?? new() : new List<string>();
        if (!paths.Contains(directory, StringComparer.OrdinalIgnoreCase)) paths.Add(directory);
        File.WriteAllText(index, JsonSerializer.Serialize(paths));
    }

    private async Task<bool> CheckMtkFirmwareAsync(string directory, string transport, bool skipUserdata)
    {
        if (!await ExecuteCommandCleanAsync("mtk.exe", transport + " printgpt", "Firmware preflight: read device GPT", clearPartitions: true)) return false;
        var layout = CurrentLayout();
        var problems = await Task.Run(() => ShopServices.CheckImages(directory, layout, skipUserdata));
        if (problems.Count > 0)
        {
            workflowFailed = true;
            Log("[FAIL] Firmware preflight:\n" + string.Join("\n", problems), Color.Red);
            return false;
        }
        // Matching sizes do not establish model or boot-chain compatibility.
        bool approved = MessageBox.Show("Partition names and image sizes match the device GPT.\n\nModel / firmware compatibility is not verified. Confirm the firmware is for this device before continuing.",
            "Firmware preflight", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
        if (!approved) workflowFailed = true;
        return approved;
    }

    private void BuildShopPanel()
    {
        var page = new TabPage("Setup / Backups");
        var tabs = new TabControl { Dock = DockStyle.Fill };
        page.Controls.Add(tabs);
        tabControl.TabPages.Add(page);

        var devices = new TabPage("Device selection");
        var deviceLayout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(12) };
        var adbList = new ComboBox { Width = 400, DropDownStyle = ComboBoxStyle.DropDownList };
        var fastbootList = new ComboBox { Width = 400, DropDownStyle = ComboBoxStyle.DropDownList };
        var scanDevices = new Button { Text = "Scan ADB / fastboot devices", AutoSize = true };
        scanDevices.Click += async (_, _) =>
        {
            scanDevices.Enabled = false;
            try
            {
                foreach (var item in new[] { (Tool: "adb.exe", Adb: true, List: adbList), (Tool: "fastboot.exe", Adb: false, List: fastbootList) })
                {
                    string output = await ReviewSafety.RunQuickAsync(ResolveToolPath(item.Tool), "devices");
                    item.List.Items.Clear();
                    item.List.Items.AddRange(ShopServices.DeviceSerials(output, item.Adb));
                    if (item.List.Items.Count == 1) item.List.SelectedIndex = 0;
                }
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Device selection"); }
            finally { scanDevices.Enabled = true; }
        };
        adbList.SelectedIndexChanged += (_, _) =>
        {
            if (adbList.SelectedItem is string serial && !string.Equals(serial, selectedAdbSerial, StringComparison.Ordinal))
            {
                selectedAdbSerial = serial;
                InvalidateAdbDeviceCache();
            }
        };
        fastbootList.SelectedIndexChanged += (_, _) => { if (fastbootList.SelectedItem is string serial) selectedFastbootSerial = serial; };
        deviceLayout.Controls.AddRange(new Control[] { new Label { AutoSize = true, Text = "ADB (USB debugging authorized)" }, adbList,
            new Label { AutoSize = true, Text = "Fastboot" }, fastbootList, scanDevices,
            new Label { AutoSize = true, MaximumSize = new Size(650, 0), Text = "Commands are pinned to the selected serial. A missing selected device stops the command instead of switching to another phone. MTK/EDL operations use their own transport; this selector applies to ADB/fastboot commands." } });
        devices.Controls.Add(deviceLayout); tabs.TabPages.Add(devices);

        var setup = new TabPage("Tool setup");
        var grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false };
        grid.Columns.Add("Tool", "Tool"); grid.Columns.Add("Status", "Readiness"); grid.Columns.Add("Path", "Location");
        void RefreshTools()
        {
            grid.Rows.Clear();
            foreach (string name in shopToolNames)
            {
                string path = FindFileInToolFolders(name);
                grid.Rows.Add(name, path.Length == 0 ? "Missing - configure tool" : "File found - runtime/device untested", path);
            }
        }
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44 };
        var choose = new Button { Text = "Choose tool...", AutoSize = true };
        choose.Click += (_, _) =>
        {
            if (grid.CurrentRow == null) return;
            string name = (string)grid.CurrentRow.Cells[0].Value;
            using var picker = new OpenFileDialog { Title = "Select " + name, Filter = name + "|" + name };
            if (picker.ShowDialog() != DialogResult.OK) return;
            try
            {
                ShopServices.SaveTool(name, picker.FileName);
                if (name == "edl.py") { edlScriptPath = picker.FileName; SaveEdlPaths(); }
                RefreshTools();
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Tool setup"); }
        };
        var refresh = new Button { Text = "Refresh", AutoSize = true };
        refresh.Click += (_, _) => RefreshTools();
        var report = new Button { Text = "Export readiness report", AutoSize = true };
        report.Click += (_, _) =>
        {
            using var save = new SaveFileDialog { Filter = "JSON|*.json", FileName = "pmk-readiness.json" };
            if (save.ShowDialog() != DialogResult.OK) return;
            try
            {
                var data = new { version = Application.ProductVersion, hardwareTested = false,
                    tools = shopToolNames.Select(n => new { name = n, fileFound = FindFileInToolFolders(n).Length > 0 }).ToArray() };
                File.WriteAllText(save.FileName, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
                MessageBox.Show("Report saved. No device serials, customer data or local paths included.");
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        };
        actions.Controls.AddRange(new Control[] { choose, refresh, report });
        setup.Controls.Add(grid); setup.Controls.Add(actions); tabs.TabPages.Add(setup);
        RefreshTools();

        var backups = new TabPage("Backup Manager");
        var list = new ListBox { Dock = DockStyle.Fill, HorizontalScrollbar = true };
        void RefreshBackups()
        {
            list.Items.Clear();
            try
            {
                string index = ShopServices.DataPath("backups.json");
                if (File.Exists(index)) foreach (var path in JsonSerializer.Deserialize<List<string>>(File.ReadAllText(index)) ?? new()) list.Items.Add(path);
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }
        var backupActions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44 };
        var import = new Button { Text = "Import backup folder...", AutoSize = true };
        import.Click += async (_, _) =>
        {
            using var pick = new FolderBrowserDialog();
            if (pick.ShowDialog() != DialogResult.OK) return;
            import.Enabled = false;
            try { await Task.Run(() => ShopServices.VerifyBackup(pick.SelectedPath)); RegisterBackup(pick.SelectedPath); RefreshBackups(); }
            catch (Exception ex) { MessageBox.Show("Backup not registered: " + ex.Message); }
            finally { import.Enabled = true; }
        };
        var verify = new Button { Text = "Verify selected", AutoSize = true };
        verify.Click += async (_, _) =>
        {
            if (list.SelectedItem is not string directory) return;
            verify.Enabled = false;
            try
            {
                var manifest = await Task.Run(() => ShopServices.VerifyBackup(directory));
                MessageBox.Show($"{manifest.Files.Count} files: sizes and SHA-256 verified.\nCreated: {manifest.Created:u}\nDevice: {manifest.DeviceIdentity}\nRestore from the MediaTek tab after device checks.");
            }
            catch (Exception ex) { MessageBox.Show("Verification failed: " + ex.Message); }
            finally { verify.Enabled = true; }
        };
        var reload = new Button { Text = "Refresh", AutoSize = true };
        reload.Click += (_, _) => RefreshBackups();
        backupActions.Controls.AddRange(new Control[] { import, verify, reload });
        backups.Controls.Add(list); backups.Controls.Add(backupActions); tabs.TabPages.Add(backups); RefreshBackups();

        var support = new TabPage("Device test records");
        var records = new DataGridView { Dock = DockStyle.Fill, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
        foreach (string title in new[] { "Platform", "Model", "Firmware", "Operation", "Result", "Test date", "Evidence / notes" }) records.Columns.Add(title, title);
        string recordPath = ShopServices.DataPath("device-tests.json");
        try { if (File.Exists(recordPath)) foreach (var row in JsonSerializer.Deserialize<List<string[]>>(File.ReadAllText(recordPath)) ?? new()) records.Rows.Add(row.Cast<object>().ToArray()); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Test records"); }
        var saveRecords = new Button { Dock = DockStyle.Top, Height = 36, Text = "Save operator test records (catalog entries are not verified support)" };
        saveRecords.Click += (_, _) =>
        {
            try
            {
                records.EndEdit();
                var rows = records.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow)
                    .Select(r => r.Cells.Cast<DataGridViewCell>().Select(c => c.Value?.ToString() ?? "").ToArray()).ToList();
                File.WriteAllText(recordPath, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
                MessageBox.Show("Operator records saved. They do not certify untested models.");
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        };
        support.Controls.Add(records); support.Controls.Add(saveRecords); tabs.TabPages.Add(support);
    }
}
