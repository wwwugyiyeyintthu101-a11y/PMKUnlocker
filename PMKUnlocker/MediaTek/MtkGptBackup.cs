#nullable disable
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using PMKUnlocker.Qualcomm;

namespace PMKUnlocker.MediaTek;

// P1.1 — the live GPT as a folder on disk.
//
// WHAT THIS IS. The bytes the DA already handed this session when it read the partition table, plus the geometry
// those bytes report, written out as a snapshot. It is deliberately not a repair tool: nothing here frames a WRITE,
// and the GPT stays on the never-program list (`MtkDaSession.NeverProgramNames`) for the live session.
//
// THE LAYOUT IS OURS, NOT THE BATCHED CLIENT'S. `gpt-layout.json`, `gpt-primary-header.bin`,
// `gpt-primary-entries.bin` and `gpt-secondary.bin` are the file names this repo's own `pmk_gpt.py` used for the
// same four artifacts (it served the pre-native MTK path and was replaced by the client engine; its folder shape
// is what is already sitting under %LOCALAPPDATA%\PMKMobileTool\Backups). The field offsets and the CRC rule are
// UEFI 2.10 §5.3, which is a published standard, not a third party's code.
//
// WHAT IS MEASURED AND WHAT IS NOT. Every number in the layout comes from the device's own header or from the
// storage descriptor the DA reported — the block size, the entry array, the backup LBA. Two of those numbers have
// never been confirmed on a UFS unit by a second source: whether the header's own backup-LBA field and the
// `alternate_lba == last LBA of the user area` identity hold on MT6877. So neither one refuses a save. Both are
// recorded and printed, because a snapshot that says "this is what the device reported, and here is the one thing
// that does not add up" is worth more than no snapshot. The checks that DO refuse are the ones that would make the
// folder a lie: a missing signature, an entry array that does not fit what the header describes, and a secondary
// read that would run past the end of the disk.

/// <summary>
/// The geometry one device's GPT header reports, in the units the DA addresses. Read from the header the session
/// already read, so a save costs no frames beyond the secondary GPT pass.
/// </summary>
internal sealed record MtkGptLayout(uint BlockSize, int HeaderSize, long EntriesLba, int EntryCount, int EntrySize,
    int ArraySectors, long BackupHeaderLba, long LastLba, bool BackupMatchesCapacity,
    bool HeaderCrcOk, bool ArrayCrcOk, string DiskGuid)
{
    /// <summary>One block of the primary header, then the entry array, then the backup array plus its header.</summary>
    internal long SecondaryBytes => (long)(ArraySectors + 1) * BlockSize;

    internal string Describe => "block " + BlockSize + " B, " + EntryCount + " × " + EntrySize +
        "-byte entries at LBA " + EntriesLba + " (" + ArraySectors + " sector(s)), backup header at LBA " +
        BackupHeaderLba + ", header " + HeaderSize + " B";

    /// <summary>
    /// The two CRC verdicts as one parenthetical. A mismatch is stated, not hidden: it is the difference between a
    /// folder the operator can act on and one he has to re-derive.
    /// </summary>
    internal string CrcText => "header CRC " + (HeaderCrcOk ? "ok" : "MISMATCH") + ", entry array CRC " +
        (ArrayCrcOk ? "ok" : "MISMATCH");

    /// <summary>
    /// The text form of everything this layout knows about the device. Geometry only — the same rule the live-table
    /// disk cache follows, so no serial, no CID and no other device-bound identity can reach a backup folder through
    /// here by accident.
    /// </summary>
    internal string LayoutJson(string binding)
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("  \"sector_size\": ").Append(BlockSize).Append(",\n");
        sb.Append("  \"primary_header_lba\": 1,\n");
        sb.Append("  \"primary_header_size\": ").Append(HeaderSize).Append(",\n");
        sb.Append("  \"primary_entries_lba\": ").Append(EntriesLba).Append(",\n");
        sb.Append("  \"entry_count\": ").Append(EntryCount).Append(",\n");
        sb.Append("  \"entry_size\": ").Append(EntrySize).Append(",\n");
        sb.Append("  \"entry_array_sectors\": ").Append(ArraySectors).Append(",\n");
        sb.Append("  \"secondary_header_lba\": ").Append(BackupHeaderLba).Append(",\n");
        sb.Append("  \"last_lba_of_user_area\": ").Append(LastLba).Append(",\n");
        sb.Append("  \"secondary_lba_matches_capacity\": ").Append(BackupMatchesCapacity ? "true" : "false").Append(",\n");
        sb.Append("  \"header_crc_ok\": ").Append(HeaderCrcOk ? "true" : "false").Append(",\n");
        sb.Append("  \"entry_array_crc_ok\": ").Append(ArrayCrcOk ? "true" : "false").Append(",\n");
        sb.Append("  \"disk_guid\": \"").Append(DiskGuid).Append("\",\n");
        sb.Append("  \"device_binding\": \"").Append(binding).Append("\"\n");
        sb.Append("}\n");
        return sb.ToString();
    }
}

internal static class MtkGptBackup
{
    internal const string LayoutFile = "gpt-layout.json";
    internal const string HeaderFile = "gpt-primary-header.bin";
    internal const string EntriesFile = "gpt-primary-entries.bin";
    internal const string SecondaryFile = "gpt-secondary.bin";
    internal const string HashFile = "gpt-sha256.txt";

    // UEFI 2.10 §5.3.2 fixed field offsets into the GPT header.
    private const int OffsetSignature = 0;
    private const int OffsetHeaderSize = 12;
    private const int OffsetHeaderCrc = 16;
    private const int OffsetMyLba = 24;
    private const int OffsetAlternateLba = 32;
    private const int OffsetDiskGuid = 56;
    private const int OffsetEntriesLba = 72;
    private const int OffsetEntryCount = 80;
    private const int OffsetEntrySize = 84;
    private const int OffsetArrayCrc = 88;
    private const int MinHeaderSize = 92;

    /// <summary>
    /// Read and validate the geometry out of the header and entry array the session already read.
    ///
    /// The two CRC fields are measured and returned, never enforced. On the 9A they hold (the pre-native path
    /// refused a refresh on either mismatch), and UEFI says a conformant disk must maintain them, but this tool has
    /// never read a UFS GPT through its own session — so a save is not refused on an assumption. The checks that do
    /// refuse are the ones that would make the folder wrong rather than merely interesting: no signature, a header
    /// that does not fit the block it was read as, an entry array the header cannot describe, and a backup header
    /// that sits where the primary entries already are.
    /// </summary>
    internal static MtkGptLayout Inspect(byte[] header, byte[] entries, uint block, ulong userAreaBytes)
    {
        if (!GptParser.HasSignature(header))
            throw new InvalidDataException("The block the session read at LBA 1 is not a GPT header (no \"EFI PART\" " +
                "signature), so there is no table to save. Nothing was written.");
        if (header.Length < MinHeaderSize || (ulong)header.Length < block)
            throw new InvalidDataException("A " + header.Length + "-byte header is not one " + block +
                "-byte sector. Nothing was written.");

        int headerSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(OffsetHeaderSize));
        if (headerSize < MinHeaderSize || headerSize > header.Length)
            throw new InvalidDataException("The GPT header declares itself " + headerSize +
                " bytes, outside the 92.." + header.Length + " this block can hold. Nothing was written.");

        long entryLba = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(OffsetEntriesLba));
        int entryCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(OffsetEntryCount));
        int entrySize = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(OffsetEntrySize));
        if (entryLba < 2 || entryCount <= 0 || entrySize < 128 || entrySize % 128 != 0)
            throw new InvalidDataException("The GPT header fields do not describe a readable entry array (entry LBA " +
                entryLba + ", " + entryCount + " × " + entrySize + " B). Nothing was written.");

        long arrayBytes = checked((long)entryCount * entrySize);
        int arraySectors = (int)((arrayBytes + block - 1) / block);
        if ((long)entries.Length < arrayBytes)
            throw new InvalidDataException("The entry array is " + entries.Length + " byte(s) but the header describes " +
                arrayBytes + " byte(s) of it — the read did not return the whole array, so a save would be a partial " +
                "table labelled as complete. Nothing was written.");

        long backupLba = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(OffsetAlternateLba));
        long myLba = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(OffsetMyLba));
        long lastLba = checked((long)(userAreaBytes / block) - 1);
        if (myLba != 1)
            throw new InvalidDataException("The header this session read at LBA 1 calls itself LBA " + myLba +
                ". A GPT header records the LBA it lives at, and this decoder knows one primary position, so " +
                "nothing was written.");
        if (backupLba <= entryLba + arraySectors)
            throw new InvalidDataException("The header puts its backup at LBA " + backupLba + ", which is not past the " +
                "primary array ending at LBA " + (entryLba + arraySectors - 1) +
                ". A read there would be the primary table again, so nothing was written.");

        return new MtkGptLayout(block, headerSize, entryLba, entryCount, entrySize, arraySectors, backupLba, lastLba,
            backupLba == lastLba,
            HeaderCrcOk(header, headerSize), ArrayCrcOk(entries, arrayBytes, header),
            new Guid(header.AsSpan(OffsetDiskGuid, 16)).ToString("D").ToLowerInvariant());
    }

    private static bool HeaderCrcOk(byte[] header, int headerSize)
    {
        uint stored = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(OffsetHeaderCrc));
        var scratch = new byte[headerSize];
        Array.Copy(header, scratch, headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(scratch.AsSpan(OffsetHeaderCrc), 0);
        return Crc32(scratch, 0, scratch.Length) == stored;
    }

    private static bool ArrayCrcOk(byte[] entries, long arrayBytes, byte[] header)
    {
        uint stored = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(OffsetArrayCrc));
        return Crc32(entries, 0, (int)arrayBytes) == stored;
    }

    /// <summary>
    /// The four bytes-to-disk files of one save, in the shape the pre-native path already used, plus a SHA-256 sidecar
    /// over them. Each file is written through a temp name and moved into place, so a save that dies half-way leaves
    /// no file that looks complete.
    /// </summary>
    internal static IReadOnlyList<string> Write(string folder, MtkGptLayout layout, string binding, byte[] header,
        byte[] entries, byte[] secondary)
    {
        Directory.CreateDirectory(folder);
        var names = new List<string>();
        PutBinary(Path.Combine(folder, HeaderFile), header); names.Add(HeaderFile);
        PutBinary(Path.Combine(folder, EntriesFile), entries); names.Add(EntriesFile);
        PutBinary(Path.Combine(folder, SecondaryFile), secondary); names.Add(SecondaryFile);
        File.WriteAllText(Path.Combine(folder, LayoutFile), layout.LayoutJson(binding), new UTF8Encoding(false));
        names.Add(LayoutFile);

        var hashes = new StringBuilder();
        foreach (string name in names)
            hashes.Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(folder, name))))
                .ToLowerInvariant()).Append("  ").Append(name).Append('\n');
        File.WriteAllText(Path.Combine(folder, HashFile), hashes.ToString(), new UTF8Encoding(false));
        names.Add(HashFile);
        return names;
    }

    private static void PutBinary(string path, byte[] data)
    {
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, data);
        File.Move(temp, path, true);
    }

    // CRC-32 as UEFI 2.10 §5.3.2 requires it (the same polynomial zlib uses), table-driven so a 1 MiB array is one
    // pass. Implemented here rather than pulled from System.IO.Hashing because this project does not carry that
    // package and a 256-word table is not worth a dependency.
    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    internal static uint Crc32(byte[] data, int offset, int length)
    {
        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < length; i++)
            crc = CrcTable[(byte)(crc ^ data[offset + i])] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
