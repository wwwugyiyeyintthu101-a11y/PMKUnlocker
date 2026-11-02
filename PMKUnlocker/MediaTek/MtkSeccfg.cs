#nullable disable
using System.Buffers.Binary;
using System.Text;

namespace PMKUnlocker.MediaTek;

// B5 — the seccfg config block: how the bootloader lock STATE is read off a live DA session.
//
// WHAT THIS FILE IS. A decoder for the first words of the seccfg partition, and nothing else. Every field
// offset and every byte of the reference block is measured, not assumed:
//   * RUN 48 (2026-11-02, Redmi 9A / MT6765) — the oracle answered its own seccfg read with a DA data frame
//     whose head is the config block, byte for byte:
//       4d4d4d4d 04000000 3c000000 03000000 00000000 00000000 45454545 ace82370
//       da27d32c 6c068424 b679a223 9ba49310 dc43f62b f5d171e6 e1d13612 00000000
//     printed beside it, in the same session, are the oracle's own two verdicts: "Detected V4 Lockstate"
//     (the version word at +0x04) and "Device is already unlocked" (the state word at +0x0C = 0x03).
//     Provenance: capture\r48-reboot-20261102-143959.pcap.mtk.out.txt line 19621 (the RX frame),
//     and the head extracted by tools\r50-seccfg-head.py.
//   * the local seccfg backups taken through the mtkclient path hold the same head bytes, so the layout is
//     not a one-device reading.
//   * the state values themselves (0x00 = LOCKED, 0x03 = UNLOCKED) are this product's own prior empirical
//     rule, recorded in MediaTekPanel's seccfg patch path long before this session existed.
//
// CLEAN ROOM: mtkclient is GPL-3.0 and no source of it was read for this file. The record is the bytes a
// working client put on the wire and the log lines it printed beside them.
//
// WHAT IS DELIBERATELY NOT HERE.
//   * No lock/unlock write. That half has never been measured: RUN 48 ran on an already-unlocked unit, so
//     the oracle sent the STATE reads and no unlock payload, and all seven archived seccfg dumps in the
//     local backup folder are byte-identical to each other — there is no locked reference on this bench
//     either. A guessed payload here does not fail loudly, it changes the bootloader's lock policy, so the
//     write path stays unimplemented until a locked unit is captured.
//   * No decode of the bytes at +0x20..+0x3B. In the measured block they are 28 bytes that are bound to
//     this individual phone (the region the checksum covers), and this product does not carry device
//     identity into a log, a record or an exception message. They are deliberately not exposed as a field,
//     so nothing downstream can print them by accident — see MtkSeccfgConfig.Describe.
//   * No DA-side HACC/AES verify chain. The register traffic the oracle ran around this read
//     (0x000F0002 / 0x000F0004 over 0x1000a0xx) belongs to the unlock path, not to reading a word.

/// <summary>
/// The seccfg config block as the DA serves it. Only the fields that decide the lock state are kept, and
/// they are kept as numbers and names — never as the bytes they came from.
/// </summary>
internal sealed record MtkSeccfgConfig(uint Version, uint HeaderField, uint LockState, string Marker,
    uint Checksum, ulong ReadOffset, uint ReadLength)
{
    /// <summary>
    /// 0x03, and nothing else, is called UNLOCKED. Any other value is reported with its raw number so an
    /// operator can compare it against a capture instead of being told a confident wrong word.
    /// </summary>
    internal string LockStateName => LockState switch
    {
        MtkSeccfg.StateUnlocked => "UNLOCKED",
        MtkSeccfg.StateLocked => "LOCKED",
        _ => "unknown (raw 0x" + LockState.ToString("x8") + ")"
    };

    internal bool IsUnlocked => LockState == MtkSeccfg.StateUnlocked;

    /// <summary>
    /// The second marker word the measured block carries at +0x18. A block whose magic is right and whose
    /// marker is not is a format this decoder has not seen, so it is shown rather than reinterpreted.
    /// </summary>
    internal bool MarkerExpected => Marker == "EEEE";

    /// <summary>
    /// Field names and numbers only. The device-bound region is not a field on this record, so it cannot
    /// appear here — and this string is what the UI logs.
    /// </summary>
    internal string Describe => "seccfg v" + Version + " @0x" + ReadOffset.ToString("x") + " +0x" +
        ReadLength.ToString("x") + " — state 0x" + LockState.ToString("x8") + " = " + LockStateName +
        ", header 0x" + HeaderField.ToString("x") + ", marker '" + Marker + "'" +
        (MarkerExpected ? "" : " (unexpected)") + ", checksum 0x" + Checksum.ToString("x8");
}

internal static class MtkSeccfg
{
    /// <summary>The partition the config block lives in, spelled the way the device's own GPT spells it.</summary>
    internal const string PartitionName = "seccfg";

    /// <summary>Measured: a locked unit carries 0x00 in the state word.</summary>
    internal const uint StateLocked = 0x0000_0000;

    /// <summary>Measured on two independent sources (RUN 48's reply and this product's own seccfg patching): 0x03 is an unlocked, valid config.</summary>
    internal const uint StateUnlocked = 0x0000_0003;

    /// <summary>
    /// The oracle read 0x400 bytes for the config block; the fields decoded here end at +0x20, so this is
    /// the measured width rather than the minimum width.
    /// </summary>
    internal const int ConfigBlockBytes = 0x400;

    /// <summary>Byte offsets inside the config block, all of them measured from RUN 48's own reply.</summary>
    internal const int VersionOffset = 0x04;
    internal const int HeaderFieldOffset = 0x08;
    internal const int LockStateOffset = 0x0C;
    internal const int MarkerOffset = 0x18;
    internal const int ChecksumOffset = 0x1C;

    /// <summary>
    /// The head the DA is asked for. The DA only addresses whole blocks (the session refuses anything else),
    /// so the measured 0x400 is rounded up to the device's own block size — which is 0x400 exactly at the
    /// 512 B the Redmi 9A reports, and larger on a device that addresses in bigger blocks.
    /// </summary>
    internal static uint HeadReadLength(uint blockSize)
    {
        if (blockSize == 0) throw new ArgumentOutOfRangeException(nameof(blockSize), "A zero block size addresses nothing.");
        return checked((uint)(((ulong)ConfigBlockBytes + blockSize - 1) / blockSize * blockSize));
    }

    /// <summary>
    /// Decode the config block head. The MMMM magic is the only hard gate: it is the one field that says
    /// "this is a seccfg config at all", and a block without it is either the wrong extent or a format this
    /// decoder must not guess about. Every other field is read and shown as it came back, including a
    /// lock-state value this decoder has no name for.
    /// </summary>
    internal static MtkSeccfgConfig Parse(ReadOnlySpan<byte> block, ulong readOffset = 0, uint readLength = 0)
    {
        if (block.Length < 0x20)
            throw new InvalidDataException("A seccfg config block shorter than 0x20 byte(s) (" + block.Length +
                ") does not carry the lock-state word, so it cannot be interpreted. Nothing is claimed about this device.");
        if (block[0] != 0x4D || block[1] != 0x4D || block[2] != 0x4D || block[3] != 0x4D)
            throw new InvalidDataException("seccfg magic mismatch: expected 'MMMM' (4d4d4d4d), the block starts with " +
                HexHead(block, 4) + ". This is not a config block this decoder knows, so the lock state is not being guessed from it.");
        return new MtkSeccfgConfig(
            BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(VersionOffset)),
            BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(HeaderFieldOffset)),
            BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(LockStateOffset)),
            Encoding.ASCII.GetString(block.Slice(MarkerOffset, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(ChecksumOffset)),
            readOffset, readLength);
    }

    /// <summary>
    /// The only byte-to-text path in this file, kept to a caller-chosen length so a diagnostic can never
    /// turn into a dump of the device-bound region.
    /// </summary>
    internal static string HexHead(ReadOnlySpan<byte> data, int max)
    {
        int n = Math.Min(data.Length, max);
        var sb = new StringBuilder(n * 2);
        for (int i = 0; i < n; i++) sb.Append(data[i].ToString("x2"));
        if (data.Length > n) sb.Append("…(" + data.Length + " B)");
        return sb.Length == 0 ? "(empty)" : sb.ToString();
    }
}
