#nullable disable
using System.Buffers.Binary;

namespace PMKUnlocker.MediaTek;

// MTK wire codecs for the native DA session — ROADMAP Phase 1.75 "Path B", stage B2/B3.
//
// CLEAN ROOM: mtkclient is GPL-3.0 and none of its sources were read for this file. Every id, field,
// length and ordering rule below comes from a byte-level record of real successful boots on a Redmi 9A
// (MT6765) driven with `mtk.exe ... --debugmode --loglevel 0`, i.e. from observing the bytes a working
// client put on the wire. Provenance: %TEMP%\mtk-da-spike\capture\framing-spec.md. Where something was
// NOT observed it is deliberately absent instead of guessed — a guessed MTK flash command is a bricked
// phone, not a clean error.
//
// TWO ENDIANNESS, ON PURPOSE. The BROM/preloader stage is big-endian; the DA stage is little-endian.
// A single ReadUInt32 shared by both silently corrupts the boot stage, so the two codecs are separate
// types and are never called from the same helper.

/// <summary>Unexpected frame, wrong magic, non-zero status, or a timeout. Always fail closed.</summary>
internal sealed class MtkDaProtocolException : IOException
{
    internal MtkDaProtocolException(string message) : base(message) { }
    internal MtkDaProtocolException(string message, Exception inner) : base(message, inner) { }

    /// <summary>
    /// Everything the face said in answer to the refused frame: the bytes the recipe expected, plus whatever followed
    /// them. Null when there was no answer at all (a timeout, a recipe that never made it onto the wire). Carried so
    /// the one retryable handshake can tell "one byte and then silence" — the only shape that a device which is not
    /// listening yet and a wrong mode share — from a live disagreement.
    /// </summary>
    internal byte[] Observed { get; init; }

    /// <summary>
    /// True when the byte never left the host, or the pipe died under it: a dead handle rather than a device
    /// answering wrongly. RUN 18 (2026-10-31) is the measured shape — six one-byte answers, then
    /// <c>libusb -9 pipe error</c> on the seventh write, and the boot refused as if the recipe had been half applied.
    /// The face proof is allowed to re-open the pipe on this; every other frame still stops the boot.
    /// </summary>
    internal bool TransportFailure { get; init; }
}

/// <summary>
/// Layer 1 — BROM / preloader. One command byte which the target echoes back, then big-endian
/// arguments, then a big-endian status. Observed exchange for MT6765 (HW code 0x0766).
/// </summary>
internal static class MtkBrom
{
    // Command bytes actually seen on the wire. Names follow the log line printed beside them.
    internal const byte GetHwCode = 0xfd;          // -> BE u16 code + BE u16 status (0766 0000)
    internal const byte WriteReg = 0xd4;           // + BE addr + BE value -> BE u16 status
    internal const byte GetTargetConfig = 0xd8;    // -> 6 bytes: BE u32 config + BE u16 status
    internal const byte GetBlVersion = 0xfe;       // echoed only, no argument and no status
    internal const byte GetBromVersion = 0xff;     // -> 1 byte (0x05 on MT6765)
    internal const byte GetHwSwVersion = 0xfc;     // -> BE u16 subcode + BE u16 hwver + BE u32 swver
    internal const byte GetMeId = 0xe1;            // -> BE u32 length + value + BE u16 status
    internal const byte GetSocId = 0xe7;           // -> BE u32 length + value + BE u16 status
    internal const byte DownloadRam = 0xda;        // BROM download 16-bit? observed with BE words
    internal const byte ReadReg = 0xd1;            // observed: BE addr + BE length, then data
    internal const byte SendDa1 = 0xd7;            // stage-1 upload: addr, length, sig length, image
    internal const byte JumpDa1 = 0xd5;            // jump: BE addr, then BE u16 status, then 0xc0

    // ---- the USB handshake that has to come before any command --------------------------------------
    //
    // Measured 2026-10-31 (run P, Redmi 9A / MT6765). A face on 0E8D:0003 that has not completed this exchange
    // answers EVERY byte with (b + 1) & 0xFF — 0xFD -> 0xFE, 0xFC -> 0xFD, 0x00 -> 0x01, 0x7F -> 0x80, 0xFF -> 0x00 —
    // and never executes a command. That is the shape RUN 10..19 kept refusing: the reply was never a refusal of
    // 0xFD, it was the face saying "we have not shaken hands". The four bytes below are answered by their bitwise
    // complement, and only after all four does 0xFD come back as 0xFD followed by the four-byte HW code
    // (07 66 00 00 on this phone). mtkclient's 197 "Handshake failed, retrying..." lines are the same state seen
    // from its side, which is why it retries the whole thing rather than the command.
    //
    // Pair 1 alone proves nothing: a preloader face carries the same VID/PID and answers 0xA0 with its complement
    // too. The gate is the whole sequence, all four complements in order.
    internal static readonly byte[] HandshakeSequence = { 0xA0, 0x0A, 0x50, 0x05 };

    /// <summary>What the BROM answers each handshake byte with: its bitwise complement.</summary>
    internal static byte HandshakeReply(byte sent) => (byte)~sent;

    /// <summary>The measured exchange in one line, for the log and for refusals ("A0->5F 0A->F5 50->AF 05->FA").</summary>
    internal static string HandshakeTranscript()
    {
        var text = new System.Text.StringBuilder();
        foreach (byte sent in HandshakeSequence)
        {
            if (text.Length > 0) text.Append(' ');
            text.Append(sent.ToString("X2")).Append("->").Append(HandshakeReply(sent).ToString("X2"));
        }
        return text.ToString();
    }

    // Target config bit meanings, as mtkclient decoded the same 0xE7 byte in its own log.
    internal const uint ConfigSbc = 0x01;
    internal const uint ConfigSla = 0x02;
    internal const uint ConfigDaa = 0x04;
    internal const uint ConfigSwjtag = 0x08;
    internal const uint ConfigEppParam = 0x10;
    internal const uint ConfigRootCert = 0x20;
    internal const uint ConfigMemReadAuth = 0x40;
    internal const uint ConfigMemWriteAuth = 0x80;
    internal const uint ConfigCmdC8Blocked = 0x100;

    internal static byte[] Be(uint value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        return b;
    }

    internal static byte[] Be(ushort value)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, value);
        return b;
    }

    internal static uint ReadUInt32Be(ReadOnlySpan<byte> data, int offset = 0) =>
        BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));

    internal static ushort ReadUInt16Be(ReadOnlySpan<byte> data, int offset = 0) =>
        BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));

    /// <summary>
    /// Decodes the six-byte answer to <see cref="GetTargetConfig"/>: BE u32 config word + BE u16
    /// status. The status is checked here because every other BROM reply carries one and a non-zero
    /// value must stop the boot, not be parked in a field nobody reads.
    /// </summary>
    internal static uint ParseTargetConfig(ReadOnlySpan<byte> reply)
    {
        if (reply.Length != 6)
            throw new MtkDaProtocolException("BROM target-config reply must be 6 bytes, got " + reply.Length + ".");
        ushort status = ReadUInt16Be(reply, 4);
        if (status != 0)
            throw new MtkDaProtocolException("BROM refused the target-config query with status 0x" + status.ToString("X4") + ".");
        return ReadUInt32Be(reply, 0);
    }

    /// <summary>
    /// Decodes the four-byte answer to <see cref="GetHwCode"/> (BE u16 code + BE u16 status).
    /// The HW code is what maps the phone to a chipset boot profile, so it is validated, not trusted.
    /// </summary>
    internal static ushort ParseHwCode(ReadOnlySpan<byte> reply)
    {
        if (reply.Length != 4)
            throw new MtkDaProtocolException("BROM HW-code reply must be 4 bytes, got " + reply.Length + ".");
        ushort status = ReadUInt16Be(reply, 2);
        if (status != 0)
            throw new MtkDaProtocolException("BROM refused the HW-code query with status 0x" + status.ToString("X4") + ".");
        return ReadUInt16Be(reply, 0);
    }

    internal static string DescribeConfig(uint config)
    {
        // Each print sense here is pinned to one observed pair: the target-config word 0xE7 and the nine
        // flag lines the working client printed beside it (SBC/SLA/DAA/SWJTAG/mem-read/mem-write/0xC8-blocked
        // True, EPP_PARAM and root-cert False). 0xE7 leaves bits 3 and 4 clear and sets bits 5..7, so SWJTAG,
        // RootCert and CmdC8Blocked are printed inverted and EPP_PARAM is printed straight. Getting this
        // backwards reads as "SBC off" on a phone that is locked — which is the wrong comfort to give an operator.
        var parts = new List<string>(9);
        void Flag(uint mask, string name, bool inverted = false)
        {
            bool set = (config & mask) != 0;
            parts.Add(name + "=" + (inverted ? !set : set));
        }
        Flag(ConfigSbc, "SBC"); Flag(ConfigSla, "SLA"); Flag(ConfigDaa, "DAA");
        Flag(ConfigSwjtag, "SWJTAG", inverted: true);
        Flag(ConfigEppParam, "EPP_PARAM"); Flag(ConfigRootCert, "RootCert", inverted: true);
        Flag(ConfigMemReadAuth, "MemReadAuth"); Flag(ConfigMemWriteAuth, "MemWriteAuth");
        Flag(ConfigCmdC8Blocked, "CmdC8Blocked", inverted: true);
        return string.Join(" ", parts);
    }
}

/// <summary>
/// DA command ids. Only ids that were seen in a capture are listed; each entry states which run
/// proved it. An id that is not here has not been observed and must not be sent.
/// </summary>
internal static class MtkDaCommand
{
    internal const uint Preamble = 0x00010009;         // no args, always status 0; precedes read and write groups
    internal const uint Erase = 0x00010003;            // RUN 9: 56-byte extent struct, NO preamble, NO limits query
    internal const uint Write = 0x00010004;            // RUN 7/9: 56-byte extent struct, then chunked data frames
    internal const uint Read = 0x00010005;             // RUN 6/8: 56-byte extent struct, then streamed data frames
    internal const uint Upload = 0x00010008;           // upload + jump: u64le addr, u64le length, then the image
    internal const uint SendEmi = 0x0001000A;          // u32le length, then the EMI/DRAM settings blob
    internal const uint WriteCommit = 0x00080005;      // RUN 7/9: no args, finalises a write
    internal const uint SyncSetup = 0x00010100;        // RUN 6: part of the post-0xc0 sync, args 2,1,1,0,0
    internal const uint SyncSetupArg = 0x00010101;     // RUN 6: part of the post-0xc0 sync, arg 0

    internal const uint Limits = 0x00040007;           // -> 8 bytes: u32le write chunk, u32le transfer packet
    internal const uint DaVersion = 0x00040011;        // -> 4 bytes (0xC0010004 on the capture DA)
    internal const uint StorageDescriptor = 0x00040001;// -> 104 bytes: type, block size, boot areas, u64 user size
    internal const uint BootModeTag = 0x0004000A;      // -> 4 bytes ASCII "brom"
    internal const uint UsbSpeed = 0x0004000B;         // -> 10 bytes ASCII "high-speed"
    internal const uint SlaState = 0x00040016;         // -> 4 bytes; 0 meant "DA SLA is disabled"
    internal const uint QuerySetupSize = 0x00020004;   // seen once with arg 0x68, purpose unidentified
    internal const uint QuerySetupZero = 0x00020003;   // seen once with arg 0, purpose unidentified

    // DA-extension group, only valid after the extension blob has been uploaded to 0x4FFF0000.
    internal const uint ExtSentinel = 0x000F0000;      // -> 4 bytes 0xA1A2A3A4
    internal const uint ExtCall = 0x000F0005;          // seen with arg 0

    /// <summary>
    /// The DA's own handshake words. "SYNC" (53594e43) is sent by the host twice around the setup
    /// commands and is also the reply that proves the DA is listening — the one signal in this whole
    /// protocol that distinguishes a live DA from a stale one still sitting in RAM.
    /// </summary>
    internal static readonly byte[] Sync = { 0x53, 0x59, 0x4E, 0x43 };

    /// <summary>Erase report words (RUN 9). The first non-zero words ever seen on this wire.</summary>
    internal const uint EraseReportStart = 0x40040004;
    internal const uint EraseReportDone = 0x40040005;

    internal static string Hex(uint id) => "0x" + id.ToString("X8");
}

/// <summary>
/// Layer 2 — the DA protocol. Every frame on the port is
///   [0..4) magic EF EE EE FE | [4..8) type u32le == 1 | [8..12) payload length u32le | payload
/// </summary>
internal static class MtkDaFrame
{
    internal const int HeaderSize = 12;
    internal static readonly byte[] Magic = { 0xEF, 0xEE, 0xEE, 0xFE };
    internal const uint TypeCommand = 1;

    /// <summary>Header only — payload is written as a separate buffer (the wire shows two writes).</summary>
    internal static byte[] Header(uint payloadLength)
    {
        var h = new byte[HeaderSize];
        Magic.CopyTo(h, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), TypeCommand);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(8), payloadLength);
        return h;
    }

    /// <summary>Builds header+payload in one buffer — for small frames, where a single write is simpler.</summary>
    internal static byte[] Pack(ReadOnlySpan<byte> payload)
    {
        var frame = new byte[HeaderSize + payload.Length];
        Header((uint)payload.Length).CopyTo(frame, 0);
        payload.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

    /// <summary>A command frame is a 4-byte payload carrying only the u32le id.</summary>
    internal static byte[] Command(uint id)
    {
        var p = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(p, id);
        return Pack(p);
    }

    /// <summary>u32le argument, e.g. the length word of <see cref="MtkDaCommand.SendEmi"/>.</summary>
    internal static byte[] Argument(uint value)
    {
        var p = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(p, value);
        return Pack(p);
    }

    internal static byte[] Argument(ReadOnlySpan<byte> value) => Pack(value);

    /// <summary>The host ack that must follow every DA data frame, one ack per frame.</summary>
    internal static byte[] Ack() => Argument(0);

    /// <summary>Validates a 12-byte header. Rejects anything that is not magic + type 1.</summary>
    internal static bool TryParseHeader(ReadOnlySpan<byte> header, out uint length)
    {
        length = 0;
        if (header.Length < HeaderSize) return false;
        for (int i = 0; i < 4; i++) if (header[i] != Magic[i]) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4, 4)) != TypeCommand) return false;
        length = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(8, 4));
        return true;
    }

    internal static uint ReadUInt32Little(ReadOnlySpan<byte> data, int offset = 0) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));

    internal static ulong ReadUInt64Little(ReadOnlySpan<byte> data, int offset = 0) =>
        BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8));
}

/// <summary>
/// The 56-byte extent struct shared by READ (0x10005), WRITE (0x10004) and ERASE (0x10003).
///   0  u32le region   (1 = eMMC user area in every observed op)
///   4  u32le field    (constant 8 in all 15 reads of RUN 6 and in every write and erase; unknown
///                      meaning — send what was observed, do not invent a value)
///   8  u64le offset   byte offset into the storage
///  16  u64le length   byte count
///  24  32 bytes       reserved, zero in every observed struct
/// </summary>
internal static class MtkOpStruct
{
    internal const int Size = 56;
    internal const uint RegionEmmcUser = 1;
    internal const uint ObservedField4 = 8;

    /// <summary>
    /// The length field is a u64, so a 4.5 GiB <c>super</c> read is representable on the wire and is served
    /// by streaming frames rather than one buffer. The cap below is only a "that is not a flash extent"
    /// sanity line; anything the callers actually allocate is guarded where the allocation happens.
    /// </summary>
    internal const ulong MaxExtentBytes = 1024UL * 1024UL * 1024UL * 1024UL;   // 1 TiB

    internal static byte[] Create(uint region, uint field4, ulong offset, ulong length)
    {
        if (length == 0)
            throw new ArgumentOutOfRangeException(nameof(length), "A zero-length DA extent was never observed on this wire.");
        if (length > MaxExtentBytes)
            throw new ArgumentOutOfRangeException(nameof(length), "DA extent of " + length + " bytes exceeds the sane extent width.");
        if (offset + length < offset)
            throw new ArgumentOutOfRangeException(nameof(offset), "DA extent runs past the end of the address space.");
        var b = new byte[Size];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0), region);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), field4);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), offset);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(16), length);
        // bytes 24..56 stay zero, as in every struct seen on the wire
        return b;
    }

    internal static byte[] Create(ulong offset, ulong length) => Create(RegionEmmcUser, ObservedField4, offset, length);

    internal static bool TryParse(ReadOnlySpan<byte> payload, out uint region, out uint field4, out ulong offset, out ulong length)
    {
        region = field4 = 0; offset = length = 0;
        if (payload.Length != Size) return false;
        for (int i = 24; i < Size; i++) if (payload[i] != 0) return false;
        region = MtkDaFrame.ReadUInt32Little(payload);
        field4 = MtkDaFrame.ReadUInt32Little(payload, 4);
        offset = MtkDaFrame.ReadUInt64Little(payload, 8);
        length = MtkDaFrame.ReadUInt64Little(payload, 16);
        return true;
    }
}

/// <summary>
/// Upload sizing and the one field on the write path that a plausible guess would corrupt silently.
/// Chunk sizes are READ FROM THE DA (command 0x40007), never hard-coded: a DA advertising different
/// numbers has to be served differently.
/// </summary>
internal sealed record MtkLimits(uint WriteChunkSize, uint TransferPacketSize)
{
    /// <summary>Parses the 8-byte reply to 0x40007. Observed: 0x200000 (2 MiB) and 0x10000 (64 KiB).</summary>
    internal static MtkLimits Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 8)
            throw new MtkDaProtocolException("DA transfer-limit reply must be 8 bytes, got " + payload.Length + ".");
        var limits = new MtkLimits(MtkDaFrame.ReadUInt32Little(payload), MtkDaFrame.ReadUInt32Little(payload, 4));
        if (limits.WriteChunkSize == 0 || limits.TransferPacketSize == 0)
            throw new MtkDaProtocolException("DA reported a zero transfer limit.");
        if (limits.WriteChunkSize > 64u * 1024u * 1024u)
            throw new MtkDaProtocolException("DA reported an implausible write chunk size of " + limits.WriteChunkSize + ".");
        return limits;
    }
}

/// <summary>
/// The 104-byte answer to <see cref="MtkDaCommand.StorageDescriptor"/> — how big the storage is and in what
/// units it is addressed. Field order is measured, not assumed: one MT6765 unit printed
/// "Boot1 0x400000 / Boot2 0x400000 / GP1-4 0x0 / RPMB 0x400000 / USER 0xe8f800000" beside this payload
///   01000000 00020000 00004000 00000000 00004000 00000000 00004000 00000000
///   00000000 00000000 (x4) 0000808f 0e000000 ...
/// and the only slot order that reproduces all eight printed numbers is type, block size, boot1, boot2,
/// RPMB, GP1..GP4, USER. The 16 bytes after USER are the eMMC CID; they are deliberately NOT decoded here —
/// a card serial is device identity and this product does not carry identity values into its records.
/// </summary>
internal sealed record MtkStorageInfo(uint StorageType, uint BlockSize, ulong Boot1Size, ulong Boot2Size,
    ulong RpmbSize, ulong GeneralPurpose1, ulong GeneralPurpose2, ulong GeneralPurpose3, ulong GeneralPurpose4,
    ulong UserAreaSize)
{
    /// <summary>1 in every observed reply, printed as "EMMC". Anything else is a storage we have not measured.</summary>
    internal bool IsEmmcUserAddressed => StorageType == 1;

    /// <summary>
    /// Every DA extent in the captures is a byte offset into the user area, and the GPT is read in units of
    /// this block size — so this is the one number that turns LBA into offset, which is why it is validated.
    /// </summary>
    internal static MtkStorageInfo Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 104)
            throw new MtkDaProtocolException("DA storage descriptor must be 104 bytes, got " + payload.Length +
                ". Extents cannot be addressed without it, so the query was refused.");
        var info = new MtkStorageInfo(
            MtkDaFrame.ReadUInt32Little(payload, 0),
            MtkDaFrame.ReadUInt32Little(payload, 4),
            MtkDaFrame.ReadUInt64Little(payload, 8),
            MtkDaFrame.ReadUInt64Little(payload, 16),
            MtkDaFrame.ReadUInt64Little(payload, 24),
            MtkDaFrame.ReadUInt64Little(payload, 32),
            MtkDaFrame.ReadUInt64Little(payload, 40),
            MtkDaFrame.ReadUInt64Little(payload, 48),
            MtkDaFrame.ReadUInt64Little(payload, 56),
            MtkDaFrame.ReadUInt64Little(payload, 64));

        // A block size that is not a sane power of two would silently move every partition by the wrong
        // factor, which is a mis-flash rather than an error — so this is refused here, at the last point
        // where it is still only a number.
        if (info.BlockSize < 512 || info.BlockSize > 65536 || (info.BlockSize & (info.BlockSize - 1)) != 0)
            throw new MtkDaProtocolException("DA reported a storage block size of " + info.BlockSize +
                ", which is not a 512..65536 power of two. Partition extents cannot be derived from it.");
        if (info.UserAreaSize < info.BlockSize)
            throw new MtkDaProtocolException("DA reported a user area of " + info.UserAreaSize + " bytes, smaller than one block.");
        return info;
    }

    internal string Describe => "type " + StorageType + ", block " + BlockSize + ", user " + UserAreaSize + " B (" +
                (UserAreaSize / 1024 / 1024 / 1024) + " GiB), boot1/2 " + Boot1Size + "/" + Boot2Size + ", rpmb " + RpmbSize;
}

internal static class MtkChecksum
{
    /// <summary>
    /// The per-chunk word the DA validates on the write path: the plain sum of every byte of that
    /// chunk frame, truncated to its low 16 bits and sent as u32le after a zero u32. Proven by
    /// elimination on two independent 8 MiB writes (four chunks each): CRC-32, CRC-16/XMODEM,
    /// CRC-16/CCITT-FALSE, CRC-16/MODBUS, adler32, byte-XOR and u16 word-sum all fail this data.
    /// </summary>
    internal static uint ChunkByteSum(ReadOnlySpan<byte> data, int offset, int count)
    {
        uint sum = 0;
        for (int i = offset; i < offset + count; i++) sum += data[i];
        return sum & 0xFFFF;
    }

    internal static uint ChunkByteSum(ReadOnlySpan<byte> data) => ChunkByteSum(data, 0, data.Length);
}

/// <summary>
/// The DA-extension reboot, byte for byte as RUN 48 measured it (2026-11-02, Redmi 9A / MT6765): a bare
/// 0x10008 upload — no 0x10009 preamble, no limits query — whose payload is a 56-byte ARM stub written to
/// 0x4fff0000, the same scratch address the boot recipe's DA-extension upload already used. The stub is
/// the TOPRGU watchdog reset: r0 = 0x10007000 (TOPRGU base), the WDT_MODE key 0x22000024 stored twice
/// around a 0xfffff-cycle delay, then WDT_SWRST 0x1209 stored at [r0,#0x14], DSB, spin — and nothing in
/// it addresses memory outside the watchdog block. Provenance:
/// capture\r48-reboot-20261102-143959.pcap.mtk.out.txt lines 27753-27771 (the TX bytes) and the
/// extraction in tools\r48-extract.py. CLEAN ROOM: observed wire bytes, not mtkclient sources.
/// </summary>
internal static class MtkDaReset
{
    /// <summary>The scratch address the measured reset wrote its stub to.</summary>
    internal const uint ExtBase = 0x4FFF0000;

    /// <summary>
    /// The 56 bytes that went on the wire (sha256 1a99a3f095fc93fe32ba1929491606867c397e8035e4874f957869f87f285d13),
    /// decoded: E3070000/E3410000 r0 = 0x10007000 · E3001024/E3421200 r1 = 0x22000024 (the WDT_MODE key) ·
    /// E3012209 r2 = 0x1209 (WDT_SWRST) · E5801000 STR r1,[r0] · E30F3FFF/E340300F/E2533001/1AFFFFFD r3
    /// counted down from 0xFFFFF (the settle delay) · E5801000 again · E5802014 STR r2,[r0,#0x14] — the
    /// reset itself · F57FF04F DSB · EAFFFFFB B . (spin).
    /// </summary>
    internal static readonly byte[] ToprguStub =
    {
        0x00, 0x00, 0x07, 0xE3, 0x00, 0x00, 0x41, 0xE3, 0x24, 0x10, 0x00, 0xE3, 0x00, 0x12, 0x42, 0xE3,
        0x09, 0x22, 0x01, 0xE3, 0x00, 0x10, 0x80, 0xE5, 0xFF, 0x3F, 0x0F, 0xE3, 0x0F, 0x30, 0x40, 0xE3,
        0x01, 0x30, 0x53, 0xE2, 0xFD, 0xFF, 0xFF, 0x1A, 0x00, 0x10, 0x80, 0xE5, 0x14, 0x20, 0x80, 0xE5,
        0x4F, 0xF0, 0x7F, 0xF5, 0xFB, 0xFF, 0xFF, 0xEA
    };

    /// <summary>
    /// The 16-byte argument frame the measured run sent after the 0x10008 accept: u32le address, a zero
    /// word, u32le length, a zero word — the same {u64 addr, u64 len} shape the boot's ext upload carried.
    /// </summary>
    internal static byte[] UploadArgs(uint address, uint length)
    {
        var b = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(b, address);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), length);
        return b;
    }
}
