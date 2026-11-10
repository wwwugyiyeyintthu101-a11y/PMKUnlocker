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

    // A disconnected selection never rebinds implicitly to a different phone.
    // The operator can select a new device while no operation is running.
    internal static string ResolvePreferred(string output, bool adb, string? preferred) => preferred ?? "";

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
        string full = VerifyConfiguredTool(name, path);
        var tools = LoadTools();
        tools[name] = full;
        File.WriteAllText(DataPath("tools.json"), JsonSerializer.Serialize(tools, new JsonSerializerOptions { WriteIndented = true }));
    }

    // tools.json is user-writable and this app runs elevated, so a configured path is executable code.
    // Only the install folder and admin-only locations are accepted; bundled lookups are hash-checked.
    private static readonly string[] TrustedToolRoots = BuildTrustedToolRoots();

    private static string[] BuildTrustedToolRoots()
    {
        var roots = new List<string> { AppContext.BaseDirectory };
        foreach (Environment.SpecialFolder folder in new[]
                 {
                     Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86
                 })
        {
            string path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrWhiteSpace(path)) roots.Add(path);
        }
        string programW6432 = Environment.GetEnvironmentVariable("ProgramW6432") ?? "";
        if (!string.IsNullOrWhiteSpace(programW6432)) roots.Add(programW6432);
        var full = new List<string>();
        foreach (string root in roots)
        {
            try { full.Add(Path.GetFullPath(root)); }
            catch (Exception) { }
        }
        return full.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsUnder(string full, string root)
    {
        string prefix = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    // null = accepted; otherwise the reason the path is refused. No file I/O, so tests can assert it.
    internal static string? ConfiguredToolProblem(string name, string path)
    {
        if (string.IsNullOrWhiteSpace(name)) return "empty tool name";
        if (string.IsNullOrWhiteSpace(path)) return "empty path";
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception) { return "invalid path"; }
        if (!string.Equals(Path.GetFileName(full), name, StringComparison.OrdinalIgnoreCase))
            return "file name does not match the requested tool";
        foreach (string root in TrustedToolRoots)
            if (IsUnder(full, root)) return null;
        return "outside the tool folder and Program Files";
    }

    internal static string VerifyConfiguredTool(string name, string path)
    {
        string? problem = ConfiguredToolProblem(name, path);
        if (problem != null)
            throw new InvalidDataException(
                "Configured tool '" + name + "' was refused (" + problem + "): " + path +
                ". Move it into the installed tool folder and choose it again.");
        return Path.GetFullPath(path);
    }

    // Scripts and other files that are interpreted on the host follow the same location rule.
    internal static string VerifyToolFile(string path)
    {
        string full = Path.GetFullPath(path);
        if (!TrustedToolRoots.Any(root => IsUnder(full, root)))
            throw new InvalidDataException(
                "Tool file is outside the installed tool folder and Program Files: " + full);
        return full;
    }

    internal static string FindTool(string root, string name)
    {
        var tools = LoadTools();
        if (tools.TryGetValue(name, out string? configured) && File.Exists(configured))
            return VerifyConfiguredTool(name, configured);
        foreach (string folder in new[] { "", "tools", "samsung", "spd", "python", "mtk", "edl" })
        {
            string candidate = Path.Combine(root, folder, name);
            if (File.Exists(candidate))
            {
                ToolIntegrity.Verify(candidate);
                return candidate;
            }
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

    internal static bool IsSparseImage(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> head = stackalloc byte[4];
        return stream.Read(head) == 4 && BitConverter.ToUInt32(head) == 0xed26ff3a;
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

    internal static List<string> CheckImages(string directory, IReadOnlyDictionary<string, long> layout, bool skipUserdata, out List<string> skipped)
    {
        skipped = new List<string>();
        var problems = new List<string>();
        int matched = 0;
        foreach (string path in Directory.EnumerateFiles(directory).Where(p => new[] { ".img", ".bin" }.Contains(Path.GetExtension(p).ToLowerInvariant())))
        {
            string partition = Path.GetFileNameWithoutExtension(path);
            if (skipUserdata && partition.Equals("userdata", StringComparison.OrdinalIgnoreCase)) continue;
            if (!layout.TryGetValue(partition, out long capacity))
            {
                // MIUI firmware ထဲမှာ partition နာမည် + suffix ထပ်ပါတဲ့ ဖိုင်တွေ ရှိတယ်
                // (preloader_dandelion_*.bin → preloader) — prefix match ကြိုးစား၊
                // မကိုက်ရင် mtkclient wl ကလည်း skip မှာမို့ fatal မလုပ်ဘဲ note ပဲထား
                string? prefix = layout.Keys.FirstOrDefault(k =>
                    partition.StartsWith(k + "_", StringComparison.OrdinalIgnoreCase));
                if (prefix == null) { skipped.Add(Path.GetFileName(path)); continue; }
                partition = prefix;
                capacity = layout[prefix];
            }
            matched++;
            try
            {
                long size = ExpandedImageSize(path);
                if (size <= 0 || size > capacity) problems.Add("Invalid image size: " + Path.GetFileName(path));
            }
            catch (Exception ex) { problems.Add(Path.GetFileName(path) + ": " + ex.Message); }
        }
        if (matched == 0) problems.Add("No images matched device partitions.");
        return problems;
    }
}
