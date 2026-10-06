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
        if (doc.Root?.Name != "data") return 0;

        long chunkLimit = ((maxImageBytes ?? FhLoaderMaxImageBytes) / 512) * 512;
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

                // ပြီးခဲ့တဲ့ flash က part files တွေ size/mtime ကိုက်နေရင် — ပြန်မဖတ်/မရေးတော့ဘူး (GB copy ~seconds ပျောက်)
                long remainLen = fi.Length;
                var expected = new List<(string Name, long Len)>();
                for (int i = 1; remainLen > 0; i++)
                {
                    long take = Math.Min(chunkLimit, remainLen);
                    expected.Add(($"{stem}__pmkpart{i}{ext}", take));
                    remainLen -= take;
                }
                bool reuse = expected.All(e =>
                {
                    string p = Path.Combine(dir, e.Name);
                    if (!File.Exists(p)) return false;
                    var pf = new FileInfo(p);
                    return pf.Length == e.Len && pf.LastWriteTimeUtc >= fi.LastWriteTimeUtc;
                });

                if (reuse)
                {
                    foreach (var e in expected) parts.Add(e.Name);
                    log?.Invoke($"[*] {Path.GetFileName(full)} — reusing {parts.Count} existing split parts (unchanged).");
                }
                else
                {
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
                    log?.Invoke($"[*] {Path.GetFileName(full)} is {fi.Length / 1024 / 1024} MB (>2GB) — split into {parts.Count} parts for fh_loader.");
                }
                cache[full] = parts;
            }

            long sectorSize = 512;
            long startSector = 0;
            long fileOffset = 0;
            var first = program;

            // template = original entry (မပြောင်း) — node ကို တစ်ပိုင်းချင်း fresh clone ယူပြီး
            // မဟုတ်ရင် in-document element ကိုပဲ ပြန်ပြင်မိပြီး part1 entry ပျောက်/order ပျက်တယ်။
            var template = new XElement(program);
            if (template.Attribute("SECTOR_SIZE_IN_BYTES") is XAttribute ss)
                long.TryParse(ss.Value, out sectorSize);
            if (sectorSize <= 0) sectorSize = 512;
            if (template.Attribute("start_sector") is XAttribute st)
                long.TryParse(st.Value, out startSector);

            foreach (string partName in parts)
            {
                long partBytes = new FileInfo(Path.Combine(Path.GetDirectoryName(full) ?? searchPath, partName)).Length;
                long sectors = partBytes / sectorSize;

                var node = new XElement(template);
                if (node.Attribute("filename") is XAttribute fn) fn.Value = partName;
                if (node.Attribute("num_partition_sectors") is XAttribute ns) ns.Value = sectors.ToString();
                if (node.Attribute("start_sector") is XAttribute ss2) ss2.Value = (startSector + fileOffset / sectorSize).ToString();
                if (node.Attribute("file_sector_offset") is XAttribute fso) fso.Value = "0";
                if (node.Attribute("size_in_KB") is XAttribute kb)
                    kb.Value = (partBytes / 1024.0).ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (node.Attribute("start_byte_hex") is XAttribute sb)
                {
                    long baseByte = 0;
                    string raw = ((string?)template.Attribute("start_byte_hex") ?? "").Trim();
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
            }

            // နောက်ဆုံး entry ကို partition စုစုပေါင်း sectors ပေးရင် နောက် partition ထဲ ရောက်သွားနိုင်လို့
            // မပေးဘူး — တစ် entry ချင်းစီရဲ့ sectors က file size နဲ့ အတိအကျ ကိုက်ပြီးသား။
        }

        doc.Save(destinationXml);
        return addedEntries;
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
            string full = Path.IsPathRooted(fileName) ? fileName : Path.Combine(searchPath, fileName);
            if (!File.Exists(full) || !IsAndroidSparse(full)) continue;

            long expanded = ReadSparseExpandedSize(full);
            string rawName = Path.GetFileNameWithoutExtension(fileName) + SparseRawSuffix;
            string rawPath = Path.Combine(searchPath, rawName);

            var rawFi = File.Exists(rawPath) ? new FileInfo(rawPath) : null;
            bool reuse = rawFi != null && rawFi.Length == expanded &&
                         rawFi.LastWriteTimeUtc >= File.GetLastWriteTimeUtc(full);
            if (reuse)
            {
                log?.Invoke($"[*] {Path.GetFileName(full)} — sparse image, reusing expanded {rawName}.");
            }
            else
            {
                log?.Invoke($"[*] {Path.GetFileName(full)} — Android sparse image, expanding to {(expanded / 1048576.0).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} MB (raw)...");
                ExpandSparseImage(full, rawPath);
                log?.Invoke($"    → {rawName} ... Ok");
            }

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
            if (fs.Length < 28) return false;
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
        br.ReadUInt16(); br.ReadUInt16();                 // major, minor version
        ushort fileHdrSz = br.ReadUInt16();
        ushort chunkHdrSz = br.ReadUInt16();
        uint blkSz = br.ReadUInt32();
        uint totalBlks = br.ReadUInt32();
        if (fileHdrSz < 28 || chunkHdrSz < 12 || blkSz == 0)
            throw new InvalidDataException("Bad sparse header: " + path);
        return (long)blkSz * totalBlks;
    }

    internal static void ExpandSparseImage(string source, string destination)
    {
        long expected = ReadSparseExpandedSize(source);
        string temp = destination + ".part";
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
                br.ReadUInt32();                            // image checksum
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
                    long dataLen = totalSz - chunkHdrSz;
                    if (dataLen < 0) throw new InvalidDataException("Sparse chunk total_sz too small: " + source);
                    long outLen = (long)chunkBlocks * blkSz;
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
                                remaining -= want;
                            }
                            break;
                        }
                        case 0xCAC3:                        // don't care → holes (NTFS reads as zeros)
                            outp.Seek(outLen, SeekOrigin.Current);
                            break;
                        case 0xCAC4:                        // crc32 — data skipped
                            break;
                        default:
                            throw new InvalidDataException($"Unknown sparse chunk type 0x{type:X4}: {source}");
                    }
                    inp.Position = chunkStart + chunkHdrSz + dataLen;
                }

                outp.SetLength(outp.Position);
                if (outp.Length != expected)
                    throw new InvalidDataException($"Sparse expand size mismatch: {outp.Length} != {expected} ({source})");
            }
            File.Move(temp, destination, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { }
            throw;
        }
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
        catch (Exception ex)
        {
            Debug.WriteLine("RunQuickAsync [" + fileName + " " + arguments + "]: " + ex.Message);
            return "";
        }
    }
}
