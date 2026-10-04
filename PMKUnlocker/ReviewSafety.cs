using System.Diagnostics;
using System.Xml.Linq;

namespace PMKUnlocker;

internal static class ReviewSafety
{
    internal static bool ShouldReboot(bool enabled, bool didRun, bool failed, bool stopped) =>
        enabled && didRun && !failed && !stopped;

    internal static List<string> ExtractImages(string archive, string directory)
    {
        var files = new List<string>();
        using var stream = File.OpenRead(archive);
        using var reader = new System.Formats.Tar.TarReader(stream);
        System.Formats.Tar.TarEntry? entry;
        while ((entry = reader.GetNextEntry()) != null)
        {
            if (entry.EntryType != System.Formats.Tar.TarEntryType.RegularFile &&
                entry.EntryType != System.Formats.Tar.TarEntryType.V7RegularFile) continue;
            string name = Path.GetFileName(entry.Name);
            string ext = Path.GetExtension(name).ToLowerInvariant();
            if (ext != ".img" && ext != ".bin") continue;
            string destination = Path.Combine(directory, name);
            entry.ExtractToFile(destination, overwrite: true);
            files.Add(destination);
        }
        return files;
    }

    internal static void FilterUserdata(string source, string destination)
    {
        var doc = XDocument.Load(source);
        if (doc.Root?.Name != "data" || !doc.Descendants("program").Any())
            throw new InvalidDataException("Unrecognized rawprogram XML; flash cancelled.");
        doc.Descendants("program").Where(x =>
            ((string?)x.Attribute("label") ?? "").Contains("userdata", StringComparison.OrdinalIgnoreCase) ||
            ((string?)x.Attribute("filename") ?? "").Contains("userdata", StringComparison.OrdinalIgnoreCase)).Remove();
        if (!doc.Descendants("program").Any())
            throw new InvalidDataException("No partitions remain after skipping userdata.");
        doc.Save(destination);
    }

    // fh_loader.exe က file size ကို 32-bit long နဲ့ယူလို့ 2GB ကျော် image တွေကို "Read 0 bytes" error နဲ့ မရေးနိုင်ဘူး။
    // ကြီးတဲ့ image တွေကို chunk ခွဲပြီး program entry တွေကို start_sector ရှိတဲ့အတိုင်း ပြန်ထုတ်ပေးတယ်။
    internal const long FhLoaderMaxImageBytes = 1_900_000_000;   // 2GB အောက်လောက် (sector size နဲ့ align ဖြစ်ရန် 512 နဲ့ round)
    internal const string SplitXmlSuffix = "_pmk.xml";

    internal static int SplitOversizedImages(string sourceXml, string searchPath, string destinationXml,
        Action<string>? log = null, Func<string, string, bool>? writeChunk = null)
    {
        var doc = XDocument.Load(sourceXml);
        if (doc.Root?.Name != "data") return 0;

        long chunkLimit = (FhLoaderMaxImageBytes / 512) * 512;
        int addedEntries = 0;
        var cache = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var program in doc.Descendants("program").ToList())
        {
            string fileName = (string?)program.Attribute("filename") ?? "";
            if (fileName.Length == 0) continue;

            string full = Path.IsPathRooted(fileName) ? fileName : Path.Combine(searchPath, fileName);
            if (!File.Exists(full)) continue;

            var fi = new FileInfo(full);
            if (fi.Length <= chunkLimit) continue;

            if (!cache.TryGetValue(full, out List<string>? parts))
            {
                parts = new List<string>();
                string stem = Path.GetFileNameWithoutExtension(full);
                string ext = Path.GetExtension(full);
                string dir = Path.GetDirectoryName(full) ?? searchPath;

                using (var src = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                {
                    int index = 1;
                    while (src.Position < src.Length)
                    {
                        long remain = src.Length - src.Position;
                        int take = (int)Math.Min(chunkLimit, remain);
                        string partName = $"{stem}__pmkpart{index}{ext}";
                        string partPath = Path.Combine(dir, partName);
                        using (var dst = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                        {
                            byte[] buf = new byte[1 << 20];
                            int left = take;
                            while (left > 0)
                            {
                                int want = Math.Min(buf.Length, left);
                                int got = src.Read(buf, 0, want);
                                if (got <= 0) break;
                                if (writeChunk != null) writeChunk(partPath, partName);
                                dst.Write(buf, 0, got);
                                left -= got;
                            }
                        }
                        parts.Add(partName);
                        index++;
                    }
                }
                cache[full] = parts;
                log?.Invoke($"[*] {Path.GetFileName(full)} is {fi.Length / 1024 / 1024} MB (>2GB) — split into {parts.Count} parts for fh_loader.");
            }

            long sectorSize = 512;
            long startSector = 0;
            long partSectors = 0;
            long fileOffset = 0;

            var first = program;
            var node = new XElement(first);
            if (node.Attribute("SECTOR_SIZE_IN_BYTES") is XAttribute ss)
                long.TryParse(ss.Value, out sectorSize);
            if (sectorSize <= 0) sectorSize = 512;
            if (first.Attribute("start_sector") is XAttribute st)
                long.TryParse(st.Value, out startSector);

            foreach (string partName in parts)
            {
                long partBytes = new FileInfo(Path.Combine(Path.GetDirectoryName(full) ?? searchPath, partName)).Length;
                long sectors = partBytes / sectorSize;

                if (node.Attribute("filename") is XAttribute fn) fn.Value = partName;
                if (node.Attribute("num_partition_sectors") is XAttribute ns) ns.Value = sectors.ToString();
                if (node.Attribute("start_sector") is XAttribute ss2) ss2.Value = (startSector + fileOffset / sectorSize).ToString();
                if (node.Attribute("file_sector_offset") is XAttribute fso) fso.Value = "0";
                if (node.Attribute("size_in_KB") is XAttribute kb)
                    kb.Value = (partBytes / 1024.0).ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (node.Attribute("start_byte_hex") is XAttribute sb)
                {
                    long baseByte = 0;
                    string raw = sb.Value.Trim();
                    if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                        long.TryParse(raw[2..], System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out baseByte);
                    sb.Value = "0x" + (baseByte + fileOffset).ToString("x");
                }

                if (fileOffset == 0) first.ReplaceWith(node);
                else first.AddAfterSelf(node);
                first = node;

                fileOffset += partBytes;
                addedEntries++;
                partSectors += sectors;
            }

            if (first.Attribute("num_partition_sectors") is XAttribute lastNs) lastNs.Value = partSectors.ToString();
        }

        doc.Save(destinationXml);
        return addedEntries;
    }

    internal static long ParseSize(string value)
    {
        var text = value.Trim();
        long size = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToInt64(text[2..], 16)
            : long.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        if (size <= 0) throw new InvalidDataException("Partition size is missing or invalid.");
        return size;
    }

    internal static bool HasBackup(string path, long expectedBytes) =>
        expectedBytes > 0 && File.Exists(path) && new FileInfo(path).Length == expectedBytes;

    internal static async Task<int> RunSequenceAsync(int count, Func<bool> stopped, Func<int, Task<bool>> step)
    {
        int done = 0;
        while (done < count && !stopped())
        {
            if (!await step(done)) break;
            done++;
        }
        return done;
    }

    internal static Dictionary<string, long> PrepareEfsBackup(string source, string directory)
    {
        var doc = XDocument.Load(source);
        string[] names = { "persist", "modemst1", "modemst2" };
        var entries = names.Select(name =>
        {
            var matches = doc.Descendants("program").Where(x =>
                string.Equals((string?)x.Attribute("label"), name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1 || !long.TryParse((string?)matches[0].Attribute("num_partition_sectors"), out var sectors) || sectors <= 0)
                throw new InvalidDataException("Missing or ambiguous EFS layout: " + name);
            var entry = new XElement(matches[0]);
            entry.SetAttributeValue("filename", name + ".bin");
            entry.SetAttributeValue("file_sector_offset", "0");
            entry.SetAttributeValue("sparse", "false");
            return entry;
        }).ToArray();
        Directory.CreateDirectory(directory);
        new XDocument(new XElement("data", entries)).Save(Path.Combine(directory, "efs_read.xml"));
        return entries.ToDictionary(e => Path.Combine(directory, (string)e.Attribute("filename")!),
            e => checked(ParseSize((string?)e.Attribute("num_partition_sectors") ?? "0") *
                         ParseSize((string?)e.Attribute("SECTOR_SIZE_IN_BYTES") ?? "0")));
    }

    // STOP ခလုတ်က RunQuickAsync processes (adb/fastboot စသည်) တွေကိုပါ ရပ်နိုင်အောင်
    // လက်ရှိ run နေသူတွေကို ခြေရာခံထား — activeProcess မဟုတ်လို့ ယခင် STOP က မသတ်နိုင်ခဲ့
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, Process> liveQuick = new();

    internal static void KillAllQuick()
    {
        foreach (var kv in liveQuick)
        {
            try
            {
                if (!kv.Value.HasExited) kv.Value.Kill(entireProcessTree: true);
            }
            catch { }
            liveQuick.TryRemove(kv.Key, out _);
        }
    }

    internal static async Task<string> RunQuickAsync(string fileName, string arguments, int timeoutMs = 15000,
        bool returnOutputOnFailure = false)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        }};
        try
        {
            process.Start();
            int pid = process.Id;
            liveQuick[pid] = process;
            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(timeoutMs);
                try
                {
                    await Task.WhenAll(process.WaitForExitAsync(timeout.Token), stdout, stderr).WaitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    return "";
                }
                if (process.ExitCode != 0 && !returnOutputOnFailure) return "";
                return (string.IsNullOrWhiteSpace(stdout.Result) ? stderr.Result : stdout.Result).Trim();
            }
            finally { liveQuick.TryRemove(pid, out _); }
        }
        catch { return ""; }
    }
}
