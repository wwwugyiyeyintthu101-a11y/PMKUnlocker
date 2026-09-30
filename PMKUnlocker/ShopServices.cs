using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PMKUnlocker;

internal static class ShopServices
{
    internal static string[] DeviceSerials(string output, bool adb) => output.Split('\n')
        .Select(line => line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        .Where(parts => parts.Length >= 2 && parts[1] == (adb ? "device" : "fastboot"))
        .Select(parts => parts[0]).Distinct(StringComparer.Ordinal).ToArray();

    internal static string SelectSerial(string output, bool adb, string? preferred)
    {
        var serials = DeviceSerials(output, adb);
        string selected = string.IsNullOrEmpty(preferred)
            ? serials.Length == 1 ? serials[0] : throw new InvalidOperationException("Select one connected device in Setup / Backups > Device selection.")
            : serials.Contains(preferred) ? preferred : throw new InvalidOperationException("Selected device is not connected. No command sent.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(selected, @"^[A-Za-z0-9_.:\-\[\]]+$"))
            throw new InvalidDataException("Unsupported device serial format.");
        return selected;
    }

    // Device swap: pinned serial gone + exactly one phone left → clear pin (rebind).
    // 0 or 2+ phones → keep pin so SelectSerial still rejects (no silent switch).
    internal static string ResolvePreferred(string output, bool adb, string? preferred)
    {
        if (string.IsNullOrEmpty(preferred)) return "";
        var serials = DeviceSerials(output, adb);
        if (serials.Contains(preferred)) return preferred;
        return serials.Length == 1 ? "" : preferred;
    }

    internal static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PMKMobileTool");
    internal static string DataPath(string name)
    {
        Directory.CreateDirectory(DataDirectory);
        return Path.Combine(DataDirectory, name);
    }

    internal static void MigrateSettings(string source)
    {
        foreach (string name in new[] { "pmk_paths.txt", "pmk_settings.txt" })
        {
            string old = Path.Combine(source, name), target = DataPath(name);
            if (File.Exists(old) && !File.Exists(target)) File.Copy(old, target, false);
        }
    }

    internal static Dictionary<string, string> LoadTools()
    {
        string path = DataPath("tools.json");
        if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
        return new(JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new(), StringComparer.OrdinalIgnoreCase);
    }

    internal static void SaveTool(string name, string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Selected tool does not exist.", path);
        var tools = LoadTools();
        tools[name] = Path.GetFullPath(path);
        File.WriteAllText(DataPath("tools.json"), JsonSerializer.Serialize(tools, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static string FindTool(string root, string name)
    {
        var tools = LoadTools();
        if (tools.TryGetValue(name, out string? configured) && File.Exists(configured)) return configured;
        foreach (string folder in new[] { "", "tools", "samsung", "spd", "python", "mtk", "edl" })
        {
            string candidate = Path.Combine(root, folder, name);
            if (File.Exists(candidate)) return candidate;
        }
        return "";
    }

    internal sealed record BackupFile(string Partition, string File, long Bytes, string Sha256);
    internal sealed record BackupManifest(int Schema, string Platform, string DeviceIdentity, string LayoutHash,
        DateTimeOffset Created, string AppVersion, List<BackupFile> Files);

    internal static string Hash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal static string LayoutHash(IReadOnlyDictionary<string, long> layout) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",
            layout.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Key.ToLowerInvariant() + ":" + p.Value)))));

    internal static void WriteBackup(string directory, string platform, IReadOnlyDictionary<string, long> layout,
        IEnumerable<string> partitions, string version)
    {
        var files = new List<BackupFile>();
        foreach (string name in partitions)
        {
            string filename = name + ".bin";
            string path = SafeChild(directory, filename);
            if (!layout.TryGetValue(name, out long size) || !ReviewSafety.HasBackup(path, size))
                throw new InvalidDataException("Incomplete backup: " + name);
            files.Add(new(name, filename, size, Hash(path)));
        }
        if (files.Count == 0) throw new InvalidDataException("Empty backup.");
        // GPT identifies a layout, not an individual phone. Never infer a serial number from it.
        var manifest = new BackupManifest(1, platform, "Unknown - physical device not verified", LayoutHash(layout),
            DateTimeOffset.UtcNow, version, files);
        string destination = Path.Combine(directory, "backup-manifest.json");
        string temp = destination + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, destination, true);
    }

    internal static string SafeChild(string directory, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || relative == "..")
            throw new InvalidDataException("Invalid backup filename.");
        return Path.Combine(directory, relative);
    }

    internal static BackupManifest VerifyBackup(string directory, IReadOnlyDictionary<string, long>? deviceLayout = null)
    {
        var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(Path.Combine(directory, "backup-manifest.json")))
            ?? throw new InvalidDataException("Missing backup manifest.");
        if (manifest.Schema != 1 || manifest.Platform != "MTK" || manifest.Files == null || manifest.Files.Count == 0)
            throw new InvalidDataException("Unsupported or empty backup manifest.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Files)
        {
            string path = SafeChild(directory, entry.File);
            if (!names.Add(entry.Partition) || !paths.Add(entry.File)) throw new InvalidDataException("Duplicate backup entry.");
            if (!ReviewSafety.HasBackup(path, entry.Bytes) || !Hash(path).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Backup changed or incomplete: " + entry.File);
            if (deviceLayout != null && (!deviceLayout.TryGetValue(entry.Partition, out long size) || size != entry.Bytes))
                throw new InvalidDataException("Device partition mismatch: " + entry.Partition);
        }
        if (deviceLayout != null && manifest.LayoutHash != LayoutHash(deviceLayout))
            throw new InvalidDataException("Device partition layout differs from this backup.");
        return manifest;
    }

    internal static long ExpandedImageSize(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        long length = reader.BaseStream.Length;
        if (length < 4 || reader.ReadUInt32() != 0xed26ff3a) return length;
        if (length < 28) throw new InvalidDataException("Truncated sparse image.");
        if (reader.ReadUInt16() != 1) throw new InvalidDataException("Unsupported sparse image version.");
        reader.ReadUInt16();
        ushort header = reader.ReadUInt16(), chunkHeader = reader.ReadUInt16();
        uint block = reader.ReadUInt32(), blocks = reader.ReadUInt32();
        if (header < 28 || header > length || chunkHeader < 12 || block == 0 || block % 4 != 0 || blocks == 0)
            throw new InvalidDataException("Invalid sparse image header.");
        return checked((long)block * blocks);
    }

    internal static List<string> CheckImages(string directory, IReadOnlyDictionary<string, long> layout, bool skipUserdata)
    {
        var problems = new List<string>();
        int count = 0;
        foreach (string path in Directory.EnumerateFiles(directory).Where(p => new[] { ".img", ".bin" }.Contains(Path.GetExtension(p).ToLowerInvariant())))
        {
            string partition = Path.GetFileNameWithoutExtension(path);
            if (skipUserdata && partition.Equals("userdata", StringComparison.OrdinalIgnoreCase)) continue;
            count++;
            if (!layout.TryGetValue(partition, out long capacity)) { problems.Add("Partition not in device GPT: " + partition); continue; }
            try
            {
                long size = ExpandedImageSize(path);
                if (size <= 0 || size > capacity) problems.Add("Invalid image size: " + Path.GetFileName(path));
            }
            catch (Exception ex) { problems.Add(Path.GetFileName(path) + ": " + ex.Message); }
        }
        if (count == 0) problems.Add("No images selected.");
        return problems;
    }
}
