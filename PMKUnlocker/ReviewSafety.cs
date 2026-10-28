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
        Action<string>? log = null, Func<string, string, bool>? writeChunk = null, long? maxImageBytes = null)
    {
        var doc = XDocument.Load(sourceXml);
        if (doc.Root?.Name != "data") throw new InvalidDataException("Unrecognized rawprogram XML.");
        int addedEntries = 0, entryIndex = 0;
        foreach (var program in doc.Descendants("program").ToList())
        {
            entryIndex++;
            string filename = (string?)program.Attribute("filename") ?? "";
            if (filename.Length == 0) continue;
            string full = PackageImagePath(searchPath, filename);
            var info = new FileInfo(full);
            if (!info.Exists || info.Length == 0) throw new InvalidDataException("Missing or empty image: " + filename);
            if (IsAndroidSparse(full)) throw new InvalidDataException("Expand sparse images before splitting.");
            long limit = maxImageBytes ?? FhLoaderMaxImageBytes;
            if (info.Length <= limit) continue;
            long Number(string name, long? fallback = null)
            {
                string? raw = (string?)program.Attribute(name);
                if (raw == null && fallback.HasValue) return fallback.Value;
                if (!long.TryParse(raw, out long n) || n < 0)
                    throw new InvalidDataException("Unsupported or missing " + name + " in " + filename);
                return n;
            }
            long sector = Number("SECTOR_SIZE_IN_BYTES");
            if (sector != 512 && sector != 4096) throw new InvalidDataException("Unsupported sector size.");
            long chunk = limit / sector * sector;
            if (chunk <= 0) throw new InvalidDataException("Split limit is smaller than a sector.");
            long start = Number("start_sector"), offset = checked(Number("file_sector_offset", 0) * sector);
            long bytes = checked(Number("num_partition_sectors") * sector);
            if (bytes <= 0 || offset > info.Length || bytes > info.Length - offset)
                throw new InvalidDataException("XML image range exceeds file: " + filename);
            using var input = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            input.Position = offset;
            long copied = 0;
            int index = 0;
            XElement previous = program;
            while (copied < bytes)
            {
                long take = Math.Min(chunk, bytes - copied);
                // Separate names for repeated references to different ranges of one image.
                string suffix = entryIndex == 1 ? "" : "_entry" + entryIndex;
                string name = Path.GetFileNameWithoutExtension(full) + suffix + "__pmkpart" + (++index) + Path.GetExtension(full);
                string target = Path.Combine(Path.GetDirectoryName(full)!, name);
                string temp = target + "." + Guid.NewGuid().ToString("N") + ".part";
                try
                {
                    using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        byte[] buffer = new byte[1024 * 1024];
                        long remaining = take;
                        while (remaining > 0)
                        {
                            if (writeChunk != null && !writeChunk(target, name)) throw new OperationCanceledException();
                            int count = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                            if (count == 0) throw new EndOfStreamException("Image changed during split.");
                            output.Write(buffer, 0, count);
                            remaining -= count;
                        }
                        output.Flush(true);
                    }
                    File.Move(temp, target, true);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
                var node = new XElement(program);
                node.SetAttributeValue("filename", Path.GetRelativePath(searchPath, target));
                node.SetAttributeValue("num_partition_sectors", take / sector);
                node.SetAttributeValue("start_sector", checked(start + copied / sector));
                node.SetAttributeValue("file_sector_offset", 0);
                node.SetAttributeValue("size_in_KB", (take / 1024.0).ToString(System.Globalization.CultureInfo.InvariantCulture));
                node.SetAttributeValue("start_byte_hex", "0x" + checked(start * sector + copied).ToString("x"));
                if (copied == 0) program.ReplaceWith(node); else previous.AddAfterSelf(node);
                previous = node;
                copied += take;
                addedEntries++;
            }
            log?.Invoke("[*] Rebuilt " + index + " split parts from verified XML range: " + filename);
        }
        doc.Save(destinationXml);
        return addedEntries;
    }

    private static string PackageImagePath(string directory, string filename)
    {
        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(root, filename));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Image path escapes firmware folder: " + filename);
        return full;
    }

    // Android sparse image (magic 0xED26FF3A) — fh_loader 15.06 က expand မလုပ်ဘဲ
    // container bytes ကို raw အနေတိုက်ရိုက်ရေးလို့ vendor/cust partition ပျက်ပြီး logo bootloop ဖြစ်တယ်။
    // ရေးမတိုင်ခင် raw အဖြစ် ကြိုတင်ဖြည့် (xxx_pmk_raw.img) ပြီး XML filename/sparse flag ပြောင်းပေးတယ်။
    internal const string SparseRawSuffix = "_pmk_raw.img";

    internal static int ConvertSparseImages(string xmlPath, string searchPath, Action<string>? log = null)
    {
        var doc = XDocument.Load(xmlPath);
        if (doc.Root?.Name != "data") return 0;
        int converted = 0;

        foreach (var program in doc.Descendants("program").ToList())
        {
            string fileName = (string?)program.Attribute("filename") ?? "";
            if (fileName.Length == 0) continue;
            string full = PackageImagePath(searchPath, fileName);
            if (!File.Exists(full)) throw new FileNotFoundException("Missing firmware image.", full);
            if (!IsAndroidSparse(full)) continue;

            long expanded = ReadSparseExpandedSize(full);
            string rawName = Path.GetFileNameWithoutExtension(fileName) + SparseRawSuffix;
            string rawPath = Path.Combine(Path.GetDirectoryName(full)!, rawName);
            rawName = Path.GetRelativePath(searchPath, rawPath);

            // Rebuild from the source; length/mtime are not evidence of identical content.
            log?.Invoke("[*] Expanding and validating " + fileName);
            ExpandSparseImage(full, rawPath);

            if (program.Attribute("filename") is XAttribute fnA) fnA.Value = rawName;
            program.Attribute("sparse")?.SetValue("false");
            if (program.Attribute("size_in_KB") is XAttribute kbA)
                kbA.Value = (expanded / 1024.0).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            converted++;
        }

        if (converted > 0) doc.Save(xmlPath);
        return converted;
    }

    internal static bool IsAndroidSparse(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            if (fs.Length < 4) return false;
            Span<byte> b = stackalloc byte[4];
            fs.ReadExactly(b);
            return b[0] == 0x3A && b[1] == 0xFF && b[2] == 0x26 && b[3] == 0xED;
        }
        catch { return false; }
    }

    internal static long ReadSparseExpandedSize(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        uint magic = br.ReadUInt32();
        if (magic != 0xED26FF3A) throw new InvalidDataException("Not a sparse image: " + path);
        if (br.ReadUInt16() != 1) throw new InvalidDataException("Unsupported sparse major version.");
        br.ReadUInt16(); // minor
        ushort fileHdrSz = br.ReadUInt16();
        ushort chunkHdrSz = br.ReadUInt16();
        uint blkSz = br.ReadUInt32();
        uint totalBlks = br.ReadUInt32();
        if (fileHdrSz < 28 || chunkHdrSz < 12 || blkSz == 0 || blkSz % 4 != 0 || totalBlks == 0)
            throw new InvalidDataException("Bad sparse header: " + path);
        return checked((long)blkSz * totalBlks);
    }

    internal static void ExpandSparseImage(string source, string destination)
    {
        long expected = ReadSparseExpandedSize(source);
        string temp = destination + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            using (var inp = File.OpenRead(source))
            using (var br = new BinaryReader(inp))
            {
                br.ReadUInt32();                            // magic (validated above)
                br.ReadUInt16(); br.ReadUInt16();           // version
                ushort fileHdrSz = br.ReadUInt16();
                ushort chunkHdrSz = br.ReadUInt16();
                uint blkSz = br.ReadUInt32();
                br.ReadUInt32();                            // total blocks
                uint totalChunks = br.ReadUInt32();
                uint imageChecksum = br.ReadUInt32();
                uint crc = uint.MaxValue;
                if (fileHdrSz > 28) inp.Seek(fileHdrSz - 28, SeekOrigin.Current);

                using var outp = File.Create(temp);
                byte[] buf = new byte[1 << 20];

                for (uint i = 0; i < totalChunks; i++)
                {
                    long chunkStart = inp.Position;
                    ushort type = br.ReadUInt16();
                    br.ReadUInt16();                        // reserved
                    uint chunkBlocks = br.ReadUInt32();
                    uint totalSz = br.ReadUInt32();
                    long dataLen = (long)totalSz - chunkHdrSz;
                    if (dataLen < 0) throw new InvalidDataException("Sparse chunk total_sz too small: " + source);
                    long outLen = checked((long)chunkBlocks * blkSz);
                    if (chunkStart + totalSz > inp.Length || outLen > expected - outp.Position)
                        throw new InvalidDataException("Sparse chunk exceeds declared bounds.");
                    if ((type == 0xCAC1 && dataLen != outLen) ||
                        (type == 0xCAC2 && dataLen != 4) || (type == 0xCAC3 && dataLen != 0) ||
                        (type == 0xCAC4 && (dataLen != 4 || chunkBlocks != 0)))
                        throw new InvalidDataException("Invalid sparse chunk length.");
                    inp.Position = chunkStart + chunkHdrSz;

                    switch (type)
                    {
                        case 0xCAC1:                        // raw data
                        {
                            long remaining = dataLen;
                            while (remaining > 0)
                            {
                                int want = (int)Math.Min(buf.Length, remaining);
                                int got = inp.Read(buf, 0, want);
                                if (got <= 0) throw new InvalidDataException("Sparse raw chunk truncated: " + source);
                                outp.Write(buf, 0, got);
                                crc = UpdateCrc(crc, buf, got);
                                remaining -= got;
                            }
                            break;
                        }
                        case 0xCAC2:                        // fill (4-byte pattern)
                        {
                            byte[] pat = br.ReadBytes(4);
                            if (pat.Length != 4) throw new InvalidDataException("Sparse fill chunk truncated: " + source);
                            byte[] fillBuf = new byte[1 << 16];
                            for (int k = 0; k < fillBuf.Length; k += 4)
                            {
                                fillBuf[k] = pat[0]; fillBuf[k + 1] = pat[1];
                                fillBuf[k + 2] = pat[2]; fillBuf[k + 3] = pat[3];
                            }
                            long remaining = outLen;
                            while (remaining > 0)
                            {
                                int want = (int)Math.Min(fillBuf.Length, remaining);
                                outp.Write(fillBuf, 0, want);
                                crc = UpdateCrc(crc, fillBuf, want);
                                remaining -= want;
                            }
                            break;
                        }
                        case 0xCAC3:                        // don't care → holes; checksum includes zeros
                            Array.Clear(buf);
                            for (long left = outLen; left > 0;)
                            {
                                int count = (int)Math.Min(buf.Length, left);
                                crc = UpdateCrc(crc, buf, count);
                                left -= count;
                            }
                            outp.Seek(outLen, SeekOrigin.Current);
                            break;
                        case 0xCAC4:
                            if (br.ReadUInt32() != ~crc) throw new InvalidDataException("Sparse CRC32 chunk mismatch.");
                            break;
                        default:
                            throw new InvalidDataException($"Unknown sparse chunk type 0x{type:X4}: {source}");
                    }
                    inp.Position = chunkStart + chunkHdrSz + dataLen;
                }

                if (inp.Position != inp.Length) throw new InvalidDataException("Trailing sparse data.");
                outp.SetLength(outp.Position);
                if (outp.Length != expected)
                    throw new InvalidDataException($"Sparse expand size mismatch: {outp.Length} != {expected} ({source})");
                if (imageChecksum != 0 && imageChecksum != ~crc)
                    throw new InvalidDataException("Sparse image CRC32 mismatch.");
            }
            File.Move(temp, destination, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { }
            throw;
        }
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(i =>
    {
        uint value = (uint)i;
        for (int bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) != 0 ? 0xedb88320U : 0);
        return value;
    }).ToArray();

    private static uint UpdateCrc(uint crc, byte[] data, int count)
    {
        for (int i = 0; i < count; i++) crc = (crc >> 8) ^ CrcTable[(crc ^ data[i]) & 0xff];
        return crc;
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

    // Qualcomm EFS/IMEI-critical partition set (fsg + fsc carry IMEI/EFS alongside persist + modemst).
    internal static readonly string[] QcEfsPartitions = { "persist", "modemst1", "modemst2", "fsg", "fsc" };

    // EFS restore plan — for each known EFS partition, skip missing files and files whose size
    // does not match the live GPT (wrong device/firmware backup guard), and return the write list.
    internal static (List<(string Name, string Path)> Plan, List<string> Skipped) BuildQcEfsRestorePlan(
        string directory, IReadOnlyList<string> partitions, IReadOnlyDictionary<string, long> liveSizes)
    {
        var plan = new List<(string Name, string Path)>();
        var skipped = new List<string>();
        foreach (string name in partitions)
        {
            string path = Path.Combine(directory, name + ".bin");
            if (!File.Exists(path))
            {
                skipped.Add(name + " (no " + name + ".bin)");
                continue;
            }
            if (liveSizes.TryGetValue(name, out long len))
            {
                long actual = new FileInfo(path).Length;
                if (actual != len)
                {
                    skipped.Add($"{name} (file {actual} B ≠ partition {len} B)");
                    continue;
                }
            }
            plan.Add((name, path));
        }
        return (plan, skipped);
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
        catch (Exception ex)
        {
            Debug.WriteLine("RunQuickAsync [" + fileName + " " + arguments + "]: " + ex.Message);
            return "";
        }
    }
}
