using System.Text;

namespace PMKUnlocker.Qualcomm;

// Native Qualcomm GPT parsing — replaces edl.py's `printgpt` / `gpt --genxml`.
// GPT is read from the device as raw sectors (see FirehoseSession.ReadGptAsync) and
// parsed here into partitions + a fh_loader-compatible rawprogram XML.

internal sealed record GptPartition(string Name, long FirstLba, long LastLba, string TypeGuid)
{
    public long SectorCount => LastLba - FirstLba + 1;
    public long ByteOffset(int sectorSize) => checked(FirstLba * sectorSize);
    public long ByteLength(int sectorSize) => checked(SectorCount * sectorSize);
}

internal sealed record GptLayout(long FirstUsableLba, long LastUsableLba, IReadOnlyList<GptPartition> Partitions);

internal static class GptParser
{
    // GPT header (LBA 1) — UEFI 2.x fixed field offsets.
    internal static (int EntryCount, int EntrySize, long EntryLba) HeaderInfo(byte[] header)
    {
        if (header.Length < 92) throw new InvalidDataException("GPT header too short.");
        if (!HasSignature(header)) throw new InvalidDataException("Not a GPT disk (missing \"EFI PART\" signature).");
        long entryLba = BitConverter.ToInt64(header, 72);
        int entryCount = BitConverter.ToInt32(header, 80);
        int entrySize = BitConverter.ToInt32(header, 84);
        if (entryLba < 2 || entryCount <= 0 || entrySize < 128)
            throw new InvalidDataException("Invalid GPT header fields.");
        return (entryCount, entrySize, entryLba);
    }

    internal static bool HasSignature(byte[] header) =>
        header.Length >= 8 &&
        header[0] == (byte)'E' && header[1] == (byte)'F' && header[2] == (byte)'I' && header[3] == (byte)' ' &&
        header[4] == (byte)'P' && header[5] == (byte)'A' && header[6] == (byte)'R' && header[7] == (byte)'T';

    internal static GptLayout ParseLayout(byte[] header, byte[] entries, int sectorSize)
    {
        var (count, size, entryLba) = HeaderInfo(header);
        if (sectorSize < 512 || sectorSize > 65536 || (sectorSize & (sectorSize - 1)) != 0)
            throw new InvalidDataException("Invalid GPT sector size.");
        long entryBytes = checked((long)count * size);
        if (entries.LongLength < entryBytes)
            throw new InvalidDataException("GPT entry array is truncated.");
        long entrySectors = (entryBytes + sectorSize - 1) / sectorSize;
        long first = BitConverter.ToInt64(header, 40);
        long last = BitConverter.ToInt64(header, 48);
        long backup = BitConverter.ToInt64(header, 32);
        if (BitConverter.ToInt64(header, 24) != 1 || first < checked(entryLba + entrySectors) ||
            last < first || backup <= last || backup - entrySectors <= last)
            throw new InvalidDataException("Invalid GPT usable-LBA boundaries.");
        var partitions = ParseEntries(header, entries);
        if (partitions.Any(p => p.FirstLba < first || p.LastLba > last))
            throw new InvalidDataException("Partition extends outside the GPT usable-LBA boundaries.");
        return new GptLayout(first, last, partitions);
    }

    internal static List<GptPartition> ParseEntries(byte[] header, byte[] entries)
    {
        var (count, size, _) = HeaderInfo(header);
        var parts = new List<GptPartition>();
        for (int i = 0; i < count; i++)
        {
            int off = i * size;
            if (off + 128 > entries.Length) break;
            if (IsZero(entries, off, 16)) continue;   // unused entry (zero type GUID)
            string typeGuid = new Guid(entries.AsSpan(off, 16)).ToString();
            long firstLba = BitConverter.ToInt64(entries, off + 32);
            long lastLba = BitConverter.ToInt64(entries, off + 40);
            string name = ReadUtf16Name(entries, off + 56, 72);
            if (lastLba < firstLba)
                throw new InvalidDataException("Invalid GPT partition range: " + name);
            parts.Add(new GptPartition(name, firstLba, lastLba, typeGuid));
        }
        return parts;
    }

    private static bool IsZero(byte[] data, int off, int len)
    {
        for (int i = 0; i < len; i++) if (data[off + i] != 0) return false;
        return true;
    }

    private static string ReadUtf16Name(byte[] data, int off, int len)
    {
        int end = off + len;
        int cut = off;
        while (cut + 1 < end && !(data[cut] == 0 && data[cut + 1] == 0)) cut += 2;
        return Encoding.Unicode.GetString(data, off, cut - off);
    }

    // fh_loader / edl.py --genxml style rawprogram map. Files are placeholder <label>.bin —
    // the XML is a partition map the operator fills with real images (or uses for readback).
    internal static string BuildRawProgramXml(IReadOnlyList<GptPartition> parts, int sectorSize)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" ?>");
        sb.AppendLine("<data>");
        foreach (var p in parts)
        {
            long startSector = p.FirstLba;
            long numSectors = p.SectorCount;
            long startByte = p.ByteOffset(sectorSize);
            double sizeKb = p.ByteLength(sectorSize) / 1024.0;
            sb.Append("  <program SECTOR_SIZE_IN_BYTES=\"")
              .Append(sectorSize)
              .Append("\" file_sector_offset=\"0\" filename=\"")
              .Append(XmlEscape(p.Name)).Append(".bin")
              .Append("\" label=\"").Append(XmlEscape(p.Name))
              .Append("\" num_partition_sectors=\"").Append(numSectors)
              .Append("\" physical_partition_number=\"0\" size_in_KB=\"")
              .Append(sizeKb.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))
              .Append("\" sparse=\"false\" start_byte_hex=\"0x").Append(startByte.ToString("x"))
              .Append("\" start_sector=\"").Append(startSector)
              .AppendLine("\"/>");
        }
        sb.AppendLine("</data>");
        return sb.ToString();
    }

    private static string XmlEscape(string value)
    {
        if (value.IndexOfAny(new[] { '&', '<', '>', '"', '\'' }) < 0) return value;
        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }
}

// A restore writes several partitions through short-lived sessions. Re-checking the live GPT
// before each write proves the same phone is still on the port: a swapped unit or a changed
// layout produces a different fingerprint and the run stops instead of writing into a stranger.
internal static class QcDeviceIdentity
{
    internal static string Fingerprint(IEnumerable<GptPartition> partitions) =>
        string.Join("\n", partitions.Select(p => p.Name + "|" + p.FirstLba + "|" + p.LastLba));
}
