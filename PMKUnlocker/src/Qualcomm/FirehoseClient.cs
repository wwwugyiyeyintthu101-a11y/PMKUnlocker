using System.IO.Ports;

namespace PMKUnlocker.Qualcomm;

// Native Qualcomm Firehose client — replaces `python edl.py` for the QC tab.
// Speaks the Firehose protocol directly over the 9008 COM port: Sahara upload +
// ping/configure/sig auth (reusing EdlAuth helpers), then read/write/erase/reset/GPT.
//
// Framing: XML commands are ASCII with no length prefix. The device answers with
// `<log .../>` entries terminated by a `<response value="ACK|NAK" .../>` element.
// A `<read>` ACK (rawmode="true") is followed immediately by raw binary data of
// exactly num_partition_sectors * sector_size bytes.

internal sealed class FirehoseSession : IDisposable
{
    internal const int DefaultSectorSize = 512;
    private const int ChunkSize = 0x10000;

    private readonly SerialPort _sp;
    private readonly FirehoseIO _io;
    private readonly Action<string>? _log;
    private Dictionary<string, GptPartition>? _gpt;
    private int _sectorSize = DefaultSectorSize;

    public string TargetName { get; private set; } = "";
    public string MemoryName { get; private set; } = "";
    public int SectorSize => _sectorSize;

    private FirehoseSession(SerialPort sp, Action<string>? log)
    {
        _sp = sp;
        _io = new FirehoseIO((buffer, offset, count) =>
        {
            int available = _sp.BytesToRead;
            return available > 0 ? _sp.Read(buffer, offset, Math.Min(count, available)) : 0;
        });
        _log = log;
    }

    public void Dispose()
    {
        try { if (_sp.IsOpen) _sp.Close(); } catch { }
        _sp.Dispose();
    }

    internal static async Task<FirehoseSession> OpenAsync(string portName, string? loaderPath, string? sigPath,
        Action<string>? log, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(portName) || !portName.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            throw new IOException("No Qualcomm 9008 COM port selected.");

        void Say(string s) => log?.Invoke(s);

        if (!string.IsNullOrEmpty(loaderPath) && File.Exists(loaderPath))
        {
            Say("[*] Sahara: loading firehose programmer...");
            if (!await EdlAuth.RunSaharaAsync(portName, loaderPath, ct))
                throw new IOException("Sahara loader upload failed.");
            Say("[+] Sahara loader transferred.");
            await Task.Delay(500, ct);
        }

        var sp = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 2000,
            WriteTimeout = 2000
        };
        try { sp.Open(); }
        catch (Exception ex) { sp.Dispose(); throw new IOException("Cannot open " + portName + ": " + ex.Message, ex); }
        var session = new FirehoseSession(sp, log);

        try
        {
            sp.DiscardInBuffer();
            sp.DiscardOutBuffer();

            // 1) ping / nop
            Say("[*] Firehose: ping...");
            await EdlAuth.WriteAsciiAsync(sp,
                "<?xml version=\"1.0\" ?><data><nop verbose=\"0\" value=\"ping\"/></data>", ct);
            string pingResp = (await session._io.ReadResponseAsync(3000, ct)).Xml;
            if (pingResp.Contains("NAK") || pingResp.Contains("ERROR"))
                Say("[!] Ping NAK — continuing with configure/sig.");
            else if (pingResp.Contains("ACK"))
                Say("[+] Firehose ping ACK.");

            const string configureXml =
                "<?xml version=\"1.0\" ?><data><configure verbose=\"0\" AlwaysValidate=\"0\" " +
                "ZlpAwareHost=\"1\" MaxPayloadSizeToTargetInBytes=\"131072\" " +
                "MemoryName=\"emmc\" SkipStorageInit=\"0\"/></data>";

            // 2) configure (pre-auth)
            Say("[*] Firehose: configure...");
            await EdlAuth.WriteAsciiAsync(sp, configureXml, ct);
            string cfgResp = (await session._io.ReadResponseAsync(4000, ct)).Xml;
            bool alreadyAuth = cfgResp.Contains("Authenticated", StringComparison.OrdinalIgnoreCase)
                || (cfgResp.Contains("ACK") && cfgResp.Contains("TargetName"));

            // 3) auth bypass (sig) — only when the loader demands it
            if (!alreadyAuth)
            {
                var sigCandidates = EdlAuth.LoadSigCandidates(sigPath, Say);
                if (sigCandidates.Count == 0)
                    throw new IOException("No SIG file found — set SIG path or place SIG #1.bin.");
                Say($"[*] Bypassing authentication ({sigCandidates.Count} SIG candidate(s))...");
                bool sigOk = false;
                string lastFail = "";
                foreach (var (sigName, sigPayload) in sigCandidates)
                {
                    ct.ThrowIfCancellationRequested();
                    Say("[*] Trying SIG: " + sigName + " (" + sigPayload.Length + " bytes)...");
                    await EdlAuth.WriteAsciiAsync(sp, configureXml, ct);
                    string c = (await session._io.ReadResponseAsync(4000, ct)).Xml;
                    if (c.Contains("Authenticated", StringComparison.OrdinalIgnoreCase) ||
                        (c.Contains("ACK") && c.Contains("TargetName")))
                    {
                        Say("[+] Already authenticated before sig.");
                        cfgResp = c;
                        sigOk = true;
                        break;
                    }
                    await EdlAuth.WriteAsciiAsync(sp,
                        "<?xml version=\"1.0\" ?><data><sig TargetName=\"sig\" size_in_bytes=\"" +
                        sigPayload.Length + "\" verbose=\"1\"/></data>", ct);
                    string sigAck = (await session._io.ReadResponseAsync(5000, ct)).Xml;
                    if (!sigAck.Contains("ACK"))
                    {
                        lastFail = sigAck.Contains("NAK") ? "Sig command NAK before payload." : "Sig ACK timeout.";
                        try { sp.DiscardInBuffer(); } catch { }
                        session._io.DiscardBuffered();
                        continue;
                    }
                    await Task.Run(() => sp.Write(sigPayload, 0, sigPayload.Length), ct);
                    string authResp = (await session._io.ReadResponseAsync(10000, ct)).Xml;
                    if (authResp.Contains("Authenticated", StringComparison.OrdinalIgnoreCase) ||
                        (authResp.Contains("ACK") && !authResp.Contains("NAK")))
                    {
                        Say("[+] EDL Authenticated with " + sigName);
                        sigOk = true;
                        break;
                    }
                    lastFail = authResp.Contains("NAK")
                        ? "SIG rejected: " + EdlAuth.TrimForLog(authResp)
                        : "Auth timeout after " + sigName;
                    Say("[!] " + lastFail);
                    try { sp.DiscardInBuffer(); } catch { }
                    session._io.DiscardBuffered();
                    await Task.Delay(400, ct);
                }
                if (!sigOk)
                    throw new IOException(string.IsNullOrEmpty(lastFail) ? "All SIG candidates failed." : lastFail);
                await Task.Delay(300, ct);
                try { sp.DiscardInBuffer(); } catch { }
                session._io.DiscardBuffered();
            }

            // 4) post-auth configure — full ACK + TargetName
            string cfg2 = "";
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                Say($"[*] Firehose: configure (post-auth, try {attempt})...");
                await EdlAuth.WriteAsciiAsync(sp, configureXml, ct);
                cfg2 = (await session._io.ReadResponseAsync(5000, ct)).Xml;
                if (!cfg2.Contains("TargetName") && cfg2.Contains("ACK"))
                    cfg2 += (await session._io.ReadResponseAsync(2000, ct)).Xml;
                if (cfg2.Contains("TargetName") || (cfg2.Contains("ACK") && !cfg2.Contains("NAK")))
                    break;
                if (attempt < 2) await Task.Delay(500, ct);
            }

            session.TargetName = EdlAuth.ExtractAttr(cfg2, "TargetName");
            session.MemoryName = EdlAuth.ExtractAttr(cfg2, "MemoryName");
            if (cfg2.Contains("ACK") && !cfg2.Contains("NAK"))
            {
                Say("[+] Firehose ready" + (string.IsNullOrEmpty(session.TargetName) ? " (ACK)." : " — TargetName=" + session.TargetName));
                await session.DetectSectorSizeAsync(ct);
                return session;
            }
            if (cfg2.Contains("Authenticated", StringComparison.OrdinalIgnoreCase))
            {
                string cfg3 = (await session._io.ReadResponseAsync(3000, ct)).Xml;
                if (!cfg3.Contains("ACK")) throw new IOException("Configure did not return ACK after auth.");
                Say("[+] Firehose ready after auth.");
                await session.DetectSectorSizeAsync(ct);
                return session;
            }
            throw new IOException("Configure failed after auth attempt.");
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    // Storage logical block size (512 eMMC / 4096 UFS) — GPT + read/write/erase are addressed in these units.
    private async Task DetectSectorSizeAsync(CancellationToken ct)
    {
        try
        {
            await EdlAuth.WriteAsciiAsync(_sp,
                "<?xml version=\"1.0\" ?><data><getstorageinfo physical_partition_number=\"0\"/></data>", ct);
            string xml = (await _io.ReadResponseAsync(4000, ct)).Xml;
            foreach (string attr in new[] { "logical_block_size", "block_size_in_bytes", "block_size" })
            {
                string v = EdlAuth.ExtractAttr(xml, attr);
                if (int.TryParse(v, out int size) && size >= 512 && size <= 65536 && (size & (size - 1)) == 0)
                {
                    _sectorSize = size;
                    return;
                }
            }
        }
        catch { }
        _sectorSize = DefaultSectorSize;
    }

    internal async Task<IReadOnlyList<GptPartition>> ReadGptAsync(CancellationToken ct = default) =>
        (await ReadGptLayoutAsync(ct)).Partitions;

    internal async Task<GptLayout> ReadGptLayoutAsync(CancellationToken ct = default)
    {
        // LBA 1 = GPT header (first 512 bytes of that block); entries start at header.EntryLba.
        byte[] headerBlock = await ReadSectorsAsync(1, 1, ct);
        byte[] header = new byte[512];
        Array.Copy(headerBlock, header, Math.Min(512, headerBlock.Length));
        var (count, size, entryLba) = GptParser.HeaderInfo(header);
        long entryBytes = checked((long)count * size);
        long entrySectors = (entryBytes + _sectorSize - 1) / _sectorSize;
        byte[] entries = await ReadSectorsAsync(entryLba, entrySectors, ct);
        return GptParser.ParseLayout(header, entries, _sectorSize);
    }

    internal async Task<GptPartition?> FindPartitionAsync(string name, CancellationToken ct = default)
    {
        _gpt ??= (await ReadGptAsync(ct)).ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);
        return _gpt.TryGetValue(name, out var p) ? p : null;
    }

    internal async Task ReadToFileAsync(long startSector, long numSectors, string destPath, Action<int>? progress, CancellationToken ct = default, Func<bool>? stopped = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destPath))!);
        string temp = destPath + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await StreamReadAsync(startSector, numSectors, fs, progress, ct, stopped);
            File.Move(temp, destPath, true);
        }
        catch { try { File.Delete(temp); } catch { } throw; }
    }

    internal async Task ProgramAsync(long startSector, string srcPath, Action<int>? progress, CancellationToken ct = default, Func<bool>? stopped = null)
    {
        long fileLen = new FileInfo(srcPath).Length;
        if (fileLen <= 0) throw new IOException("Empty image: " + srcPath);
        long numSectors = (fileLen + _sectorSize - 1) / _sectorSize;
        await EdlAuth.WriteAsciiAsync(_sp,
            "<?xml version=\"1.0\" ?><data><program SECTOR_SIZE_IN_BYTES=\"" + _sectorSize +
            "\" num_partition_sectors=\"" + numSectors + "\" physical_partition_number=\"0\" start_sector=\"" + startSector + "\"/></data>", ct);
        var resp = await _io.ReadResponseAsync(30000, ct, stopped);
        if (!resp.IsAck)
            throw new IOException("Program NAK: " + EdlAuth.TrimForLog(resp.Xml));

        byte[] chunk = new byte[ChunkSize];
        long sent = 0, remaining = fileLen;
        using (var fs = File.OpenRead(srcPath))
        {
            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                if (stopped?.Invoke() == true) throw new OperationCanceledException("STOP");
                int want = (int)Math.Min(ChunkSize, remaining);
                int n = await fs.ReadAsync(chunk.AsMemory(0, want), ct);
                if (n <= 0) throw new EndOfStreamException("Image changed during write.");
                int writeLen = n;
                if (remaining == n && n % _sectorSize != 0)
                {
                    writeLen = (n + _sectorSize - 1) / _sectorSize * _sectorSize;
                    Array.Clear(chunk, n, writeLen - n);
                }
                await Task.Run(() => _sp.Write(chunk, 0, writeLen), ct);
                remaining -= n;
                sent += n;
                progress?.Invoke((int)(sent * 100 / fileLen));
            }
        }

        var fin = await _io.ReadResponseAsync(10000, ct, stopped);
        if (!fin.IsAck)
            throw new IOException("Program failed: " + EdlAuth.TrimForLog(fin.Xml));
    }

    internal async Task EraseAsync(long startSector, long numSectors, CancellationToken ct = default)
    {
        await EdlAuth.WriteAsciiAsync(_sp,
            "<?xml version=\"1.0\" ?><data><erase PAGES_PER_BLOCK=\"32\" SECTOR_SIZE_IN_BYTES=\"" + _sectorSize +
            "\" num_partition_sectors=\"" + numSectors + "\" physical_partition_number=\"0\" start_sector=\"" + startSector + "\"/></data>", ct);
        var resp = await _io.ReadResponseAsync(30000, ct);
        if (!resp.IsAck)
            throw new IOException("Erase NAK: " + EdlAuth.TrimForLog(resp.Xml));
    }

    internal async Task ResetAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await EdlAuth.WriteAsciiAsync(_sp,
            "<?xml version=\"1.0\" ?><data><power value=\"reset\"/></data>", ct);
        FirehoseResponse response;
        try
        {
            response = await _io.ReadResponseAsync(3000, ct);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            ct.ThrowIfCancellationRequested();
            _log?.Invoke("[*] Reset sent; the port went quiet before acknowledgement. Device boot is not confirmed.");
            return;
        }
        if (!response.IsAck)
            throw new IOException("Reset rejected: " + EdlAuth.TrimForLog(response.Xml));
        _log?.Invoke("[+] Reset acknowledged.");
    }

    private async Task<byte[]> ReadSectorsAsync(long startSector, long numSectors, CancellationToken ct)
    {
        long total = checked(numSectors * _sectorSize);
        if (total > 64 * 1024 * 1024) throw new InvalidOperationException("Read too large for an in-memory buffer.");
        using var ms = new MemoryStream((int)total);
        await StreamReadAsync(startSector, numSectors, ms, null, ct);
        return ms.ToArray();
    }

    private async Task StreamReadAsync(long startSector, long numSectors, Stream sink, Action<int>? progress, CancellationToken ct, Func<bool>? stopped = null)
    {
        long total = checked(numSectors * _sectorSize);
        await EdlAuth.WriteAsciiAsync(_sp,
            "<?xml version=\"1.0\" ?><data><read SECTOR_SIZE_IN_BYTES=\"" + _sectorSize +
            "\" num_partition_sectors=\"" + numSectors + "\" physical_partition_number=\"0\" start_sector=\"" + startSector + "\"/></data>", ct);
        await _io.ReadPayloadAsync(total, sink, progress, ct, stopped);
    }
}
