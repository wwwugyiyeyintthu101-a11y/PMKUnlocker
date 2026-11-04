#nullable disable
using System.Buffers.Binary;
using PMKUnlocker.Qualcomm;

namespace PMKUnlocker.MediaTek;

// B3 — the DA command layer: READ, WRITE, ERASE and the queries they need, on top of the session that B2
// booted (ROADMAP Phase 1.75 "Path B", the "Boot Device" promise: boot once, then work any partition
// without touching the cable again).
//
// WHAT THIS FILE IS. Every sequence here is a transcription of a measured cycle on a Redmi 9A (MT6765):
//   READ   — RUN 6 (15 GPT reads) and RUN 8 (one 8 MiB read-back, 128 frames of 65536 B)
//   WRITE  — RUN 7 and RUN 9 (byte-identical; RUN 8 proved the written bytes landed)
//   ERASE  — RUN 9 (accepted, "Successfully formatted", partition restored from the same boot)
//   REBOOT — RUN 48 (bare 0x10008 upload of a 56-byte TOPRGU watchdog stub to 0x4fff0000; the phone resets itself)
//   SECCFG — RUN 48's state half (B5.2): the same 0x10005 READ over the head of the extent the live GPT names
//            seccfg, decoded by <see cref="MtkSeccfg"/>. Read-only — see the note on that method.
// Frame-accurate transcripts: %TEMP%\mtk-da-spike\capture\{logoverify-read,logo-write,erase3}-frames.txt
// and the write-up in capture\framing-spec.md. CLEAN ROOM: mtkclient is GPL-3.0 and no source of it was
// read for this file — the record is the bytes that a working client put on the wire and the log lines it
// printed beside them.
//
// WHAT IS DELIBERATELY NOT HERE.
//   * No generic "send command, get reply" wrapper. The queries do not share one shape: 0x40007 and the
//     other 0x4000x queries answer status/data/status, 0x40011 answers with data only, and 0x40016 answers
//     with three zero words that cannot be told apart from statuses at all. A single abstraction would
//     have to guess which, and a wrong guess desynchronises the port mid-operation.
//   * No per-op handshake generalisation. Read and write both start with 0x10009 + 0x40007; ERASE starts
//     bare and answers with report words instead of data. The struct is shared, the framing around it is not.
//   * No status-code table. In four captures the DA never once refused a command, so the error vocabulary
//     is unobserved: failures here are timeouts, unexpected frame lengths, and unexpected words.
//   * No generic "eject to system" flow control. The one reboot here is RUN 48's measured TOPRGU reset,
//     and it says nothing about the DA state afterwards — the session is declared spent the moment the
//     stub is out, rather than guessed back into service.
//
// ONE RULE THE UI MUST NOT LOSE: a write is not success until it has been read back. mtkclient printed its
// success line from status words alone and never re-read the partition. The verify below is the product's
// existing readback rule, on by default.

/// <summary>The written bytes did not come back the same. Distinct from a protocol desync: the DA answered fine.</summary>
internal sealed class MtkDataVerifyException : IOException
{
    internal MtkDataVerifyException(string message) : base(message) { }
}

/// <summary>
/// One partition of the running device, in the units the DA actually addresses: bytes into the eMMC user
/// area. The captures show <c>logo</c> resolved to 0x5b280000 + 0x800000, which is first-LBA × 512 — so the
/// DA takes bytes and the host does the LBA maths, exactly as the working client did.
/// </summary>
internal sealed record MtkDaExtent(string Name, long FirstLba, long LastLba, uint BlockSize)
{
    internal long SectorCount => LastLba - FirstLba + 1;
    internal ulong ByteOffset => checked((ulong)(FirstLba * (long)BlockSize));
    internal ulong ByteLength => checked((ulong)(SectorCount * (long)BlockSize));
    internal string Describe => Name + "  LBA " + FirstLba + ".." + LastLba + "  byte 0x" + ByteOffset.ToString("x") +
        " + 0x" + ByteLength.ToString("x") + " (" + (ByteLength / 1024 / 1024) + " MiB)";
}

/// <summary>A GPT read from the live device, kept on the session so the cable is touched once.</summary>
internal sealed class MtkDaPartitionTable
{
    private readonly Dictionary<string, MtkDaExtent> _byName;

    internal MtkDaPartitionTable(uint blockSize, IReadOnlyList<MtkDaExtent> extents)
    {
        BlockSize = blockSize;
        Extents = extents;
        _byName = new Dictionary<string, MtkDaExtent>(StringComparer.OrdinalIgnoreCase);
        foreach (MtkDaExtent extent in extents)
            if (!_byName.ContainsKey(extent.Name)) _byName[extent.Name] = extent;
    }

    internal uint BlockSize { get; }
    internal IReadOnlyList<MtkDaExtent> Extents { get; }
    internal bool TryFind(string name, out MtkDaExtent extent) => _byName.TryGetValue(name ?? "", out extent);
}

internal sealed partial class MtkDaSession
{
    /// <summary>An extent this big is only ever served to a file; a partition dump is not a RAM buffer.</summary>
    private const long MaxInMemoryExtentBytes = 64 * 1024 * 1024;

    /// <summary>
    /// Report words accepted after 0x10003: the two markers that were seen plus any small number, which is
    /// what the one measured value (0x64 = 100, beside "Successfully formatted") looks like — a percent.
    /// Anything else stops the loop instead of being reinterpreted, because a long erase is unmeasured.
    /// </summary>
    private const uint MaxReportPercent = 100;
    private const int MaxReportWords = 4096;

    /// <summary>Table-level names that must never be written or erased through a live session, same list the scatter path refuses.</summary>
    internal static readonly IReadOnlyCollection<string> NeverProgramNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "pgpt", "sgpt", "gpt", "otp", "flashinfo" };

    /// <summary>
    /// Names the live session may read but never program. seccfg carries the bootloader lock policy, and the only
    /// bytes ever measured on it are a READ of its config-block head — an unlock WRITE has never been captured,
    /// because the unit that produced the recipe was already unlocked. Writing unmeasured bytes here does not fail
    /// loudly, it changes lock policy, so the partition stays read-only until a capture names the payload.
    /// </summary>
    internal static readonly IReadOnlyCollection<string> ReadOnlyPayloadNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { MtkSeccfg.PartitionName };

    private bool _opInFlight;
    private string _currentOp;

    internal MtkLimits Limits { get; private set; }
    internal MtkStorageInfo Storage { get; private set; }
    internal MtkDaPartitionTable Partitions { get; private set; }

    // ---- queries -------------------------------------------------------------------------------------

    /// <summary>
    /// 0x10009 + 0x40007: the two sizes the DA serves transfers in. Read from the device, never hard-coded —
    /// a DA that advertises 1 MiB chunks has to be written differently, and the write checksum is only
    /// meaningful for the chunk size the DA is actually validating.
    /// </summary>
    internal async Task<MtkLimits> QueryLimitsAsync(CancellationToken ct, bool refresh = false)
    {
        RequireReady();
        if (Limits != null && !refresh) return Limits;
        await RunPreambleAsync("limits", ct).ConfigureAwait(false);
        WriteCommand(MtkDaCommand.Limits);
        await ReadStatusAsync("LIMITS accept", ct).ConfigureAwait(false);
        DaReply data = await ReadFrameAsync("LIMITS reply", ct).ConfigureAwait(false);
        await ReadStatusAsync("LIMITS close", ct).ConfigureAwait(false);
        Limits = MtkLimits.Parse(data.Payload);
        Say("    DA limits: write chunk 0x" + Limits.WriteChunkSize.ToString("x") + ", transfer 0x" +
            Limits.TransferPacketSize.ToString("x"));
        return Limits;
    }

    /// <summary>
    /// 0x10009 + 0x40001: storage type, block size and area sizes. The block size is what turns GPT LBAs
    /// into the byte offsets the DA wants, so nothing here works without it.
    ///
    /// A DA behind a UFS disk answers 0x40001 with 104 zero bytes (RUN B6 measured it, and our own live session
    /// on the MT6877 got the same). The working client does not stop there: it asks 0x40004 and reads the
    /// geometry out of that. Which reply is which is decided by the reply's own shape — 104 bytes opening with
    /// the eMMC region word, or 208 bytes opening with the UFS one — never by a chipset table, so an
    /// unrecognised reply is still refused rather than decoded into something plausible.
    /// </summary>
    internal async Task<MtkStorageInfo> QueryStorageAsync(CancellationToken ct, bool refresh = false)
    {
        RequireReady();
        if (Storage != null && !refresh) return Storage;
        await RunPreambleAsync("storage", ct).ConfigureAwait(false);
        WriteCommand(MtkDaCommand.StorageDescriptor);
        await ReadStatusAsync("STORAGE accept", ct).ConfigureAwait(false);
        DaReply data = await ReadFrameAsync("STORAGE reply", ct).ConfigureAwait(false);
        await ReadStatusAsync("STORAGE close", ct).ConfigureAwait(false);

        Storage = MtkStorageInfo.IsDescriptorShape(data.Payload)
            ? MtkStorageInfo.Parse(data.Payload)
            : await QueryUfsGeometryAsync(ct, data.Payload.Length).ConfigureAwait(false);
        Say("    DA storage: " + Storage.Describe);
        return Storage;
    }

    /// <summary>
    /// The second geometry query, sent only when 0x40001 answered in a shape that is not the eMMC descriptor:
    /// the same preamble + command + status + frame + status shape, with 0x40004 in place of 0x40001.
    /// </summary>
    private async Task<MtkStorageInfo> QueryUfsGeometryAsync(CancellationToken ct, int descriptorLength)
    {
        Say("    0x40001 answered " + descriptorLength + " byte(s) in a shape that is not the eMMC descriptor — " +
            "asking 0x40004, the geometry reply the UFS capture answers with.");
        await RunPreambleAsync("ufs geometry", ct).ConfigureAwait(false);
        WriteCommand(MtkDaCommand.UfsGeometry);
        await ReadStatusAsync("UFS GEOMETRY accept", ct).ConfigureAwait(false);
        DaReply data = await ReadFrameAsync("UFS GEOMETRY reply", ct).ConfigureAwait(false);
        await ReadStatusAsync("UFS GEOMETRY close", ct).ConfigureAwait(false);
        if (!MtkStorageInfo.IsUfsGeometryShape(data.Payload))
            throw new MtkDaProtocolException("Neither geometry reply this session can read: 0x40001 sent " +
                descriptorLength + " byte(s) that is not the 104-byte eMMC descriptor, and 0x40004 sent " +
                data.Payload.Length + " byte(s) that is not the 208-byte UFS geometry. No offset is derived from " +
                "a layout that has not been measured.");
        return MtkStorageInfo.ParseUfs(data.Payload);
    }

    /// <summary>The 0x10009 preamble: no arguments, always answered with status 0, opens every read and write group.</summary>
    private async Task RunPreambleAsync(string forWhat, CancellationToken ct)
    {
        WriteCommand(MtkDaCommand.Preamble);
        await ReadStatusAsync("PREAMPE before " + forWhat, ct).ConfigureAwait(false);
    }

    // ---- extents: read -------------------------------------------------------------------------------

    /// <summary>Read an extent into memory. Refuses anything above <see cref="MaxInMemoryExtentBytes"/>.</summary>
    internal async Task<byte[]> ReadExtentAsync(ulong byteOffset, ulong byteLength, Action<int> progress = null, CancellationToken ct = default)
    {
        if (byteLength > MaxInMemoryExtentBytes)
            throw new InvalidOperationException("A " + byteLength + " byte read does not belong in memory — read it to a file.");
        EnterOperation("read 0x" + byteOffset.ToString("x") + " + 0x" + byteLength.ToString("x"));
        try
        {
            using var buffer = new MemoryStream((int)byteLength);
            await ReadExtentToStreamAsync(byteOffset, byteLength, buffer, null, progress, ct).ConfigureAwait(false);
            return buffer.ToArray();
        }
        finally { LeaveOperation(); }
    }

    /// <summary>Read an extent straight to a file, written to a temporary name and moved only when the byte count is complete.</summary>
    internal async Task ReadExtentToFileAsync(ulong byteOffset, ulong byteLength, string destPath,
        Action<int> progress = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destPath);
        EnterOperation("read to " + Path.GetFileName(destPath));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destPath))!);
        string temp = destPath + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await ReadExtentToStreamAsync(byteOffset, byteLength, file, null, progress, ct).ConfigureAwait(false);
            File.Move(temp, destPath, true);
        }
        catch
        {
            try { File.Delete(temp); } catch { /* the partial file is the least interesting thing that went wrong */ }
            throw;
        }
        finally { LeaveOperation(); }
    }

    /// <summary>
    /// The read loop, and the only place a read is turned into bytes. <paramref name="compareTo"/> is what
    /// makes a write verification possible without a second copy of the image in RAM: each frame that comes
    /// back is checked against the same position of the source before it is acked. Callers own the
    /// single-operation guard — a write verifies through here while it still holds it.
    /// </summary>
    private async Task ReadExtentToStreamAsync(ulong byteOffset, ulong byteLength, Stream sink, Stream compareTo,
        Action<int> progress, CancellationToken ct)
    {
        RequireReady();
        MtkStorageInfo storage = await CheckExtentAsync(byteOffset, byteLength, ct).ConfigureAwait(false);
        MtkLimits limits = await QueryLimitsAsync(ct).ConfigureAwait(false);

        WriteCommand(MtkDaCommand.Read);
        await ReadStatusAsync("READ accept", ct).ConfigureAwait(false);
        WriteFrame(MtkOpStruct.Create(storage, byteOffset, byteLength));
        await ReadStatusAsync("READ extent", ct).ConfigureAwait(false);
        // Both captures carry a second status word between the accepted extent and the first data frame.
        await ReadStatusAsync("READ extent (second status)", ct).ConfigureAwait(false);

        ulong remaining = byteLength;
        long received = 0;
        int frame = 0;
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            DaReply data = await ReadFrameAsync("READ frame " + (frame + 1), ct).ConfigureAwait(false);
            if (data.Payload.Length == 0)
                throw new MtkDaProtocolException("READ: frame " + (frame + 1) + " carries no data with " + remaining +
                    " byte(s) still outstanding; the DA went out of step mid-extent.");
            if ((ulong)data.Payload.Length > remaining)
                throw new MtkDaProtocolException("READ: frame " + (frame + 1) + " declares " + data.Payload.Length +
                    " byte(s) but only " + remaining + " remain of the requested extent — the wire and the request disagree.");
            if (data.Payload.Length > limits.TransferPacketSize)
                throw new MtkDaProtocolException("READ: frame " + (frame + 1) + " is " + data.Payload.Length +
                    " byte(s), above the DA's own advertised transfer size of " + limits.TransferPacketSize + ".");

            sink.Write(data.Payload, 0, data.Payload.Length);
            if (compareTo != null) CompareFrame(compareTo, data.Payload, received);
            WriteAck();
            await ReadStatusAsync("READ frame " + (frame + 1) + " ack", ct).ConfigureAwait(false);

            remaining -= (ulong)data.Payload.Length;
            received += data.Payload.Length;
            frame++;
            Report(progress, received, (long)byteLength);
        }
        sink.Flush();
        Say("[+] READ 0x" + byteOffset.ToString("x") + " + 0x" + byteLength.ToString("x") + " — " + received +
            " byte(s) in " + frame + " frame(s).");
    }

    private static void CompareFrame(Stream source, byte[] frame, long positionSoFar)
    {
        var expected = new byte[frame.Length];
        int got = 0;
        while (got < expected.Length)
        {
            int n = source.Read(expected, got, expected.Length - got);
            if (n <= 0)
                throw new MtkDataVerifyException("Verify stopped at byte " + (positionSoFar + got) +
                    ": the source image is shorter than the extent that was read back.");
            got += n;
        }
        for (int i = 0; i < frame.Length; i++)
        {
            if (frame[i] != expected[i])
                throw new MtkDataVerifyException("Verify failed at byte " + (positionSoFar + i) + ": device has 0x" +
                    frame[i].ToString("x2") + ", image has 0x" + expected[i].ToString("x2") +
                    ". The write was accepted by the DA and the bytes are still wrong, so this is a real corruption, not a timeout.");
        }
    }

    // ---- extents: write ------------------------------------------------------------------------------

    /// <summary>Write a buffer at a byte offset. Reads the range back and compares before reporting success.</summary>
    internal Task WriteExtentAsync(ulong byteOffset, byte[] data, Action<int> progress = null,
        bool verify = true, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        return WriteRangeCoreAsync(byteOffset, () => new MemoryStream(data, writable: false), data.Length, progress, verify, ct);
    }

    /// <summary>
    /// Write a file at a byte offset. <paramref name="length"/> lets a caller push the head of a larger image;
    /// omitting it writes the whole file. The image is streamed a chunk at a time, so a 4.5 GiB super does not
    /// become a 4.5 GiB allocation.
    /// </summary>
    internal async Task WriteExtentFromFileAsync(ulong byteOffset, string srcPath, long? length = null,
        Action<int> progress = null, bool verify = true, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(srcPath);
        if (!File.Exists(srcPath)) throw new FileNotFoundException("Write image is missing: " + srcPath);
        long fileLength = new FileInfo(srcPath).Length;
        if (fileLength <= 0) throw new IOException("Write image is empty: " + srcPath);
        long wanted = length ?? fileLength;
        if (wanted > fileLength)
            throw new IOException("Asked to write " + wanted + " byte(s) from a file that is " + fileLength + " long.");
        string full = Path.GetFullPath(srcPath);
        await WriteRangeCoreAsync(byteOffset, () => File.OpenRead(full), wanted, progress, verify, ct).ConfigureAwait(false);
    }

    private async Task WriteRangeCoreAsync(ulong byteOffset, Func<Stream> openImage, long byteLength,
        Action<int> progress, bool verify, CancellationToken ct)
    {
        RequireReady();
        EnterOperation("write 0x" + byteOffset.ToString("x") + " + 0x" + byteLength.ToString("x"));
        try
        {
            if (byteLength <= 0) throw new IOException("A zero-length write was never observed on this wire.");
            MtkStorageInfo storage = await CheckExtentAsync(byteOffset, (ulong)byteLength, ct).ConfigureAwait(false);
            MtkLimits limits = await QueryLimitsAsync(ct).ConfigureAwait(false);

            WriteCommand(MtkDaCommand.Write);
            await ReadStatusAsync("WRITE accept", ct).ConfigureAwait(false);
            WriteFrame(MtkOpStruct.Create(storage, byteOffset, (ulong)byteLength));
            await ReadStatusAsync("WRITE extent", ct).ConfigureAwait(false);

            int chunkSize = (int)limits.WriteChunkSize;
            var chunk = new byte[chunkSize];
            using (Stream image = openImage())
            {
                long sent = 0;
                int index = 0;
                while (sent < byteLength)
                {
                    ct.ThrowIfCancellationRequested();
                    int want = (int)Math.Min((long)chunkSize, byteLength - sent);
                    FillFrom(image, chunk, want, sent, byteLength);

                    // The two words before every data frame: a zero the DA ignores (never anything but 0 in
                    // eight measured chunks) and the low 16 bits of the sum of exactly this frame's bytes.
                    WriteWord(0);
                    WriteWord(MtkChecksum.ChunkByteSum(chunk, 0, want));
                    WriteDataFrame(chunk, 0, want);
                    await ReadStatusAsync("WRITE chunk " + (index + 1), ct).ConfigureAwait(false);

                    sent += want;
                    index++;
                    Report(progress, sent, byteLength);
                }
                // Both write captures answer one more status after the final chunk, before the commit preamble.
                await ReadStatusAsync("WRITE data accepted (final status)", ct).ConfigureAwait(false);
            }

            await RunPreambleAsync("WRITE commit", ct).ConfigureAwait(false);
            WriteCommand(MtkDaCommand.WriteCommit);
            await ReadStatusAsync("WRITE commit", ct).ConfigureAwait(false);
            await ReadStatusAsync("WRITE commit (second status)", ct).ConfigureAwait(false);
            Say("[+] WRITE 0x" + byteOffset.ToString("x") + " + 0x" + byteLength.ToString("x") + " committed.");

            if (verify)
            {
                Say("[*] Verifying the write by reading the same range back...");
                using Stream source = openImage();
                await ReadExtentToStreamAsync(byteOffset, (ulong)byteLength, Stream.Null, source, progress, ct).ConfigureAwait(false);
                Say("[+] VERIFY OK — the device answers with the same bytes for the whole range.");
            }
        }
        finally { LeaveOperation(); }
    }

    private static void FillFrom(Stream source, byte[] target, int count, long alreadySent, long total)
    {
        int got = 0;
        while (got < count)
        {
            int n = source.Read(target, got, count - got);
            if (n <= 0)
                throw new EndOfStreamException("Image ended after " + (alreadySent + got) + " of " + total +
                    " byte(s). Refusing to send a short chunk — the DA would write past the end of the file.");
            got += n;
        }
    }

    private void WriteWord(uint value)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, value);
        WriteFrame(payload);
    }

    // ---- extents: erase ------------------------------------------------------------------------------

    /// <summary>
    /// 0x10003 + the same 56-byte struct — the one operation that is sent bare, with no 0x10009 preamble and
    /// no 0x40007 limits query, and that answers with report words instead of data. The measured cycle is
    /// status 0, 0x40040004, 0x64, host ack, 0x40040005. Whether a longer erase emits more progress words is
    /// unmeasured, so this loops on the vocabulary rather than on a fixed count, and stops on a word that is
    /// none of the four shapes instead of inventing a meaning for it.
    /// </summary>
    internal async Task EraseExtentAsync(ulong byteOffset, ulong byteLength, CancellationToken ct = default)
    {
        RequireReady();
        EnterOperation("erase 0x" + byteOffset.ToString("x") + " + 0x" + byteLength.ToString("x"));
        try
        {
            MtkStorageInfo storage = await CheckExtentAsync(byteOffset, byteLength, ct).ConfigureAwait(false);

            WriteCommand(MtkDaCommand.Erase);
            await ReadStatusAsync("ERASE accept", ct).ConfigureAwait(false);
            WriteFrame(MtkOpStruct.Create(storage, byteOffset, byteLength));
            await ReadStatusAsync("ERASE extent", ct).ConfigureAwait(false);

            Say("[*] ERASE 0x" + byteOffset.ToString("x") + " + 0x" + byteLength.ToString("x") + " — waiting for the DA's report words.");
            int words = 0;
            bool acked = false;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (++words > MaxReportWords)
                    throw new MtkDaProtocolException("ERASE: " + MaxReportWords + " report words and no end marker. The DA is still " +
                        "talking, so the session is left alone rather than guessed at — do not unplug the phone.");
                uint word = await ReadWordAsync("ERASE report " + words, ct).ConfigureAwait(false);
                if (word == MtkDaCommand.EraseReportDone) break;
                if (word == MtkDaCommand.EraseReportStart) continue;
                if (word <= MaxReportPercent)
                {
                    // Exactly one ack, at the measured position: after the first progress word, before the end marker.
                    // A DA that wants one ack per report word would stall us into a timeout, which is loud; guessing
                    // extra acks could instead interleave into the next command, which is not.
                    if (!acked) { WriteAck(); acked = true; }
                    continue;
                }
                throw new MtkDaProtocolException("ERASE: unexpected report word 0x" + word.ToString("x8") + " (word " + words +
                    "). Only 0x40040004, a percent up to " + MaxReportPercent + " and 0x40040005 were ever measured, so this shape " +
                    "is refused instead of reinterpreted mid-erase.");
            }
            Say("[+] ERASE 0x" + byteOffset.ToString("x") + " + 0x" + byteLength.ToString("x") + " — the DA reported completion (" +
                words + " word(s)). Note: the DA accepts an erase without proving zeroes; verify by reading if it matters.");
        }
        finally { LeaveOperation(); }
    }

    // ---- the DA-extension reboot --------------------------------------------------------------------

    /// <summary>
    /// Reboot the phone through the DA extension's TOPRGU watchdog stub — RUN 48's measured sequence,
    /// byte for byte: a bare 0x10008 upload (no 0x10009 preamble, no 0x40007 limits query), the 16-byte
    /// address/length args, then the 56-byte stub as one data frame. The extension itself needs nothing
    /// sent first: the boot recipe uploads it to 0x4fff0000 as its final DA steps, so a ready session
    /// already carries the machinery this rides on.
    ///
    /// The reply is allowed to be silence. The measured run was answered status 0 and then the port died
    /// (USBError(5) on the oracle's next read) while it still printed success — once the WDT_SWRST store
    /// is on the wire, a dead or quiet pipe is the expected shape, not a failure. Only a live refusal
    /// (a non-zero status word the DA was alive enough to frame) is raised, and even that cannot unsend
    /// the stub: the session is spent on every post-write path.
    /// </summary>
    internal async Task RebootAsync(CancellationToken ct = default)
    {
        RequireReady();
        EnterOperation("DA-extension reboot (TOPRGU stub at 0x" + MtkDaReset.ExtBase.ToString("x") + ")");
        try
        {
            WriteCommand(MtkDaCommand.Upload);   // measured bare: no 0x10009 preamble, no limits query
            await ReadStatusAsync("RESET accept", ct).ConfigureAwait(false);
            WriteFrame(MtkDaReset.UploadArgs(MtkDaReset.ExtBase, (uint)MtkDaReset.ToprguStub.Length));
            WriteDataFrame(MtkDaReset.ToprguStub, 0, MtkDaReset.ToprguStub.Length);
            try
            {
                await ReadStatusAsync("RESET stub", ct).ConfigureAwait(false);
                Say("[+] Reset command was sent — the DA answered status 0 and the phone is rebooting. Unplug the USB cable.");
            }
            catch (MtkDaProtocolException ex)
            {
                // The measured post-stub shape: status 0, then the port died; silence reads the same from our
                // side. The detail is logged, not raised — after the stub is out the phone is resetting
                // whatever the pipe says, and an exception here would call a fired reset a failure.
                Say("[+] Reset command was sent — the stub is on the wire but the port " +
                    (ex.TransportFailure ? "died" : "went quiet") + " before the reply. The phone is rebooting; unplug the USB cable. (" +
                    ex.Message.Trim() + ")");
            }
            finally
            {
                DaIsReady = false;   // the stub is out — the session serves no further command on any post-write path
            }
        }
        finally { LeaveOperation(); }
    }

    // ---- the live partition table ---------------------------------------------------------------------

    /// <summary>
    /// Read the GPT once through the session and cache it. This is where "Boot Device" gets its speed: the
    /// batched tool re-reads fifteen GPT extents per sub-command, and a live session needs none of that.
    /// </summary>
    internal async Task<MtkDaPartitionTable> ReadPartitionTableAsync(CancellationToken ct = default, bool refresh = false)
    {
        RequireReady();
        if (Partitions != null && !refresh) return Partitions;
        EnterOperation("partition table");
        try
        {
            MtkStorageInfo storage = await QueryStorageAsync(ct).ConfigureAwait(false);
            uint block = storage.BlockSize;

            byte[] header = await ReadExtentToBufferAsync(block, block, ct).ConfigureAwait(false);
            var (entryCount, entrySize, entryLba) = GptParser.HeaderInfo(header);
            long entryBytes = checked((long)entryCount * entrySize);
            ulong entrySpan = (ulong)((entryBytes + block - 1) / block * block);
            byte[] entries = await ReadExtentToBufferAsync((ulong)entryLba * block, entrySpan, ct).ConfigureAwait(false);

            var extents = new List<MtkDaExtent>();
            foreach (GptPartition p in GptParser.ParseEntries(header, entries))
                extents.Add(new MtkDaExtent(p.Name, p.FirstLba, p.LastLba, block));
            if (extents.Count == 0)
                throw new InvalidDataException("The device GPT parsed but named no partition, so no extent can be resolved. Nothing was written.");

            Partitions = new MtkDaPartitionTable(block, extents);
            Say("[+] Live GPT: " + extents.Count + " partition(s), block " + block + " B — resolved once, no further replug needed.");
            return Partitions;
        }
        finally { LeaveOperation(); }
    }

    /// <summary>Read an extent sized for memory (the GPT pass and anything the UI shows inline).</summary>
    private async Task<byte[]> ReadExtentToBufferAsync(ulong byteOffset, ulong byteLength, CancellationToken ct)
    {
        if (byteLength > MaxInMemoryExtentBytes)
            throw new InvalidOperationException("A " + byteLength + " byte table read does not belong in memory.");
        using var buffer = new MemoryStream((int)byteLength);
        await ReadExtentToStreamAsync(byteOffset, byteLength, buffer, null, null, ct).ConfigureAwait(false);
        return buffer.ToArray();
    }

    /// <summary>Resolve one partition name against the live table, reading the table first if it is not cached.</summary>
    internal async Task<MtkDaExtent> RequirePartitionAsync(string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        MtkDaPartitionTable table = await ReadPartitionTableAsync(ct).ConfigureAwait(false);
        if (!table.TryFind(name, out MtkDaExtent extent))
            throw new InvalidOperationException("This device's GPT has no partition named '" + name + "' (" +
                table.Extents.Count + " entries read). Nothing was sent to the device.");
        return extent;
    }

    internal async Task ReadPartitionToFileAsync(string name, string destPath, Action<int> progress = null, CancellationToken ct = default)
    {
        MtkDaExtent extent = await RequirePartitionAsync(name, ct).ConfigureAwait(false);
        Say("[*] " + extent.Describe);
        await ReadExtentToFileAsync(extent.ByteOffset, extent.ByteLength, destPath, progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Write an image back to a named partition. The image may not be bigger than the partition — the scatter
    /// path already refuses that, and on a live session an overrun silently writes into the next partition.
    /// </summary>
    internal async Task WritePartitionAsync(string name, string imagePath, Action<int> progress = null,
        bool verify = true, CancellationToken ct = default)
    {
        MtkDaExtent extent = await RequirePartitionAsync(name, ct).ConfigureAwait(false);
        GuardProgramName(name, "written");
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        long imageLength = new FileInfo(imagePath).Length;
        if (imageLength > (long)extent.ByteLength)
            throw new IOException(name + " holds " + extent.ByteLength + " byte(s) and " + imagePath + " is " + imageLength +
                ". Writing it here would run into the next partition, so it was refused before the port was touched.");
        Say("[*] " + extent.Describe);
        await WriteExtentFromFileAsync(extent.ByteOffset, imagePath, imageLength, progress, verify, ct).ConfigureAwait(false);
    }

    internal async Task ErasePartitionAsync(string name, CancellationToken ct = default)
    {
        MtkDaExtent extent = await RequirePartitionAsync(name, ct).ConfigureAwait(false);
        GuardProgramName(name, "erased");
        Say("[*] " + extent.Describe);
        await EraseExtentAsync(extent.ByteOffset, extent.ByteLength, ct).ConfigureAwait(false);
    }

    // ---- seccfg: the lock state, read-only ------------------------------------------------------------

    /// <summary>
    /// Read the seccfg config block head and decode the bootloader lock state. This is the RUN 48 state half
    /// replayed on our own proven reader: the oracle's seccfg reads are all plain 0x10005 READs over raw
    /// offsets (B5.1 pinned every one of the 16 measured structs against <see cref="MtkOpStruct.Create"/>),
    /// and the config block it got back starts at the head of the extent the device's own GPT names
    /// <c>seccfg</c> — so there is no new wire knowledge in here, only a name resolved through the live table
    /// and the proven read path. The extent head is read instead of the whole partition because a 8 MiB
    /// config dump is not a state word and would go to a file, not to the screen.
    ///
    /// READ-ONLY BY CONSTRUCTION: nothing here frames a WRITE, an ERASE or a register poke. The unlock half
    /// of the oracle's flow was never measured — RUN 48 ran on a unit that answered "Device is already
    /// unlocked" and sent no unlock payload — so locking and unlocking stay unimplemented until a locked
    /// unit is captured. Guessing that payload does not fail loudly; it changes a lock policy.
    /// </summary>
    internal async Task<MtkSeccfgConfig> ReadSeccfgConfigAsync(CancellationToken ct = default)
    {
        RequireReady();
        // Resolved before the read so the GPT is still the only thing cached here, and so a device without a
        // seccfg partition is refused by name before the port carries a data request.
        MtkDaExtent seccfg = await RequirePartitionAsync(MtkSeccfg.PartitionName, ct).ConfigureAwait(false);
        uint block = (await QueryStorageAsync(ct).ConfigureAwait(false)).BlockSize;
        uint headLength = MtkSeccfg.HeadReadLength(block);
        if (headLength > seccfg.ByteLength)
            throw new InvalidOperationException("seccfg is " + seccfg.ByteLength + " byte(s) on this device, under the " +
                headLength + " byte(s) the config block needs at its block size of " + block + ". A head that does not " +
                "fit is not a seccfg layout this decoder knows, so the state is not being claimed.");
        Say("[*] " + seccfg.Describe + " — reading the config block head (0x" + headLength.ToString("x") + " B)...");
        byte[] head = await ReadExtentToBufferAsync(seccfg.ByteOffset, headLength, ct).ConfigureAwait(false);
        MtkSeccfgConfig config = MtkSeccfg.Parse(head, seccfg.ByteOffset, headLength);
        Say("[+] seccfg: " + config.Describe);
        return config;
    }

    /// <summary>
    /// The never-program list, enforced here rather than in the UI: on a live DA session an erased or rewritten
    /// partition table is not a repair job, it is a brick with no BROM-side recovery to fall back on.
    /// </summary>
    private static void GuardProgramName(string name, string pastParticiple)
    {
        if (NeverProgramNames.Contains(name))
            throw new IOException("'" + name + "' is table or chip identity — never " + pastParticiple +
                " through a live session. Nothing was sent to the device.");
        if (ReadOnlyPayloadNames.Contains(name))
            throw new IOException("'" + name + "' holds the bootloader lock policy and its program payload has " +
                "never been measured on this bench — " + pastParticiple + " through a live session is refused, " +
                "because unmeasured bytes change the lock state silently instead of failing. Nothing was sent to " +
                "the device. Seccfg State still reads it, and the mtkclient seccfg flows are unaffected.");
    }

    // ---- shared guards -------------------------------------------------------------------------------

    /// <summary>
    /// Block alignment and capacity, checked against what the DA itself reported. An offset that is not a
    /// whole block would be silently rounded by the device, and an extent past the end of the user area would
    /// read as garbage or land in a hardware partition — neither is worth discovering after the port is open.
    /// The geometry is returned because the extent struct is framed with this unit's own region/field pair.
    /// </summary>
    private async Task<MtkStorageInfo> CheckExtentAsync(ulong byteOffset, ulong byteLength, CancellationToken ct)
    {
        if (byteLength == 0) throw new IOException("A zero-length extent was never observed on this wire.");
        MtkStorageInfo storage = await QueryStorageAsync(ct).ConfigureAwait(false);
        uint block = storage.BlockSize;
        if (byteOffset % block != 0)
            throw new IOException("Offset 0x" + byteOffset.ToString("x") + " is not a multiple of the device block size " + block + ".");
        if (byteLength % block != 0)
            throw new IOException("Length 0x" + byteLength.ToString("x") + " is not a multiple of the device block size " + block + ".");
        if (byteOffset + byteLength > storage.UserAreaSize)
            throw new IOException("Extent 0x" + byteOffset.ToString("x") + " + 0x" + byteLength.ToString("x") +
                " runs past the end of the user area (" + storage.UserAreaSize + " byte(s)).");
        return storage;
    }

    private void EnterOperation(string what)
    {
        if (_opInFlight)
            throw new InvalidOperationException("This session is busy with '" + _currentOp +
                "'. One port carries one operation — a second writer would interleave frames into the first.");
        _opInFlight = true;
        _currentOp = what;
    }

    private void LeaveOperation()
    {
        _opInFlight = false;
        _currentOp = null;
    }

    private static void Report(Action<int> progress, long done, long total)
    {
        if (progress == null || total <= 0) return;
        progress.Invoke((int)Math.Min(100, done * 100 / total));
    }
}
