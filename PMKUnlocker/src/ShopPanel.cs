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
            command == "kill-server" || command == "start-server" || command == "wait-for-device")
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
}
