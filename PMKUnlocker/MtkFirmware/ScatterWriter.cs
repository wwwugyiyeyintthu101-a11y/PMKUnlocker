using System.Text;

namespace PMKUnlocker.MtkFirmware;

/// <summary>
/// One row of a scatter this tool writes itself: a partition the device reported, the image file that dump of it
/// sits next to the scatter, and where it lives in the storage area.
/// </summary>
/// <param name="ByteOffset">Bytes into the area, not LBAs — a scatter addresses in bytes, so the caller does the
/// block-size multiplication (the same conversion the live DA session uses for its extent structs).</param>
internal sealed record ScatterRow(string Name, string FileName, long ByteOffset, long ByteLength,
    string Region, bool IsDownload);

/// <summary>
/// The scatter <see cref="ScatterParser"/> reads, in the shape the FLASH grid and <c>wfscatter</c> accept. A dump
/// without this file is a pile of images; with it the folder is a firmware package the tool can re-flash.
///
/// The output is constrained to exactly what our own parser will accept, and nothing is guessed: an unusable name,
/// a file that could escape the folder, a duplicated partition or two rows that overlap is refused at build time,
/// because a scatter that parses wrong is a write list that lands at the wrong offset.
/// </summary>
internal static class ScatterWriter
{
    /// <summary>The magic line <see cref="ScatterParser"/> fixtures carry. Real vendor scatters start with it.</summary>
    internal const string Header = "MTK_SCATTER_V11_LIO";

    /// <summary>The config version our parser's fixtures use; it is only echoed back to the operator.</summary>
    internal const string ConfigVersion = "V1.1.5";

    /// <summary>The one region every extent of a GPT user area belongs to — and the one the FLASH grid selects.
    /// It is the reader's constant, not a second copy: a writer that emits a word the reader does not know produces
    /// a package that cannot be flashed.</summary>
    internal static string EmmcUserRegion => ScatterParser.EmmcUserRegion;

    /// <summary>Regions the parser knows and the FLASH grid will select a row in. Anything else is refused here.</summary>
    private static readonly string[] SupportedRegions =
        { EmmcUserRegion, "EMMC_BOOT1", "EMMC_BOOT2", "EMMC_BOOT1_BOOT2" };

    /// <summary>
    /// A device-dump package names itself after the chipset, which is also what makes the FLASH tab's open dialog
    /// see it — that filter is <c>*scatter*.txt</c>, so "firmware.scatter" would be invisible to the very flow it
    /// is meant to feed.
    /// </summary>
    internal static string DefaultFileName(string platform)
    {
        var sb = new StringBuilder();
        foreach (char c in (platform ?? "").Trim())
            if (char.IsLetterOrDigit(c) || c is '_' or '-') sb.Append(c);
        return (sb.Length == 0 ? "PMK" : sb.ToString()) + "_scatter.txt";
    }

    /// <summary>
    /// Render the file. Rows keep the order given (a GPT order reads top-to-bottom like the device's own table),
    /// and every value is written in hex the way the parser's own number reader expects.
    /// </summary>
    internal static string Build(string platform, string project, string storage, IReadOnlyList<ScatterRow> rows)
    {
        if (string.IsNullOrWhiteSpace(platform))
            throw new InvalidDataException("Scatter needs the chipset the session booted on (platform is empty).");
        GuardToken(platform, "platform");
        if (rows == null || rows.Count == 0)
            throw new InvalidDataException("No partition to write — the scatter would describe nothing.");
        if (string.IsNullOrWhiteSpace(storage))
            throw new InvalidDataException("Scatter needs the storage type read from the device.");
        // Our reader accepts only EMMC or UFS in the general block, and the per-entry word must be HW_STORAGE_EMMC.
        // A caller that passes the entry word ("HW_STORAGE_EMMC") or a chipset word would produce a package this
        // tool itself refuses, so the one storage this writer can describe is normalised here and nothing else.
        string storageName = storage.Trim();
        if (!storageName.Equals("EMMC", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Scatter storage '" + storage + "' is not the eMMC word our own reader " +
                "accepts. A UFS package has never been read off a real one on this bench, so it is not invented here.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ScatterRow row in rows)
        {
            GuardToken(row.Name, "partition_name");
            GuardToken(row.FileName, "file_name");
            if (row.ByteLength <= 0)
                throw new InvalidDataException(row.Name + ": a zero-length extent cannot go in a scatter.");
            if (row.ByteOffset < 0)
                throw new InvalidDataException(row.Name + ": negative start byte.");
            if (!SupportedRegions.Contains(row.Region, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException(row.Name + ": region '" + (row.Region ?? "?") +
                    "' is not a region our own scatter reader knows, so this package could not be re-flashed.");
            if (!names.Add(row.Name))
                throw new InvalidDataException(row.Name + ": appears twice — a scatter with a duplicate partition " +
                    "name has its second row excluded by the reader, so the dump it points at would never be flashed.");
        }
        DetectOverlap(rows);

        var sb = new StringBuilder();
        Line(sb, Header);
        Line(sb, "- general:");
        Line(sb, "  - platform: " + platform);
        Line(sb, "  - project: " + (string.IsNullOrWhiteSpace(project) ? "PMK-device-dump" : project));
        Line(sb, "  - config_version: " + ConfigVersion);
        Line(sb, "  - storage: " + storageName.ToUpperInvariant());
        int index = 0;
        foreach (ScatterRow row in rows)
        {
            Line(sb, "- partition_index: SYS" + index);
            Line(sb, "  - partition_name: " + row.Name);
            Line(sb, "  - file_name: " + row.FileName);
            Line(sb, "  - linear_start_addr: 0x" + row.ByteOffset.ToString("x"));
            Line(sb, "  - physical_start_addr: 0x" + row.ByteOffset.ToString("x"));
            Line(sb, "  - partition_size: 0x" + row.ByteLength.ToString("x"));
            Line(sb, "  - region: " + row.Region);
            Line(sb, "  - storage: HW_STORAGE_EMMC");
            Line(sb, "  - is_download: " + (row.IsDownload ? "true" : "false"));
            Line(sb, "  - operation_type: FLASH");
            Line(sb, "  - type: IMAGE");
            index++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// A key or value is one token: no colon (the reader splits on the first one), no newline (each line is a
    /// record), no path separator (a file_name must stay inside the package folder), no control characters.
    /// Device names come off the GPT as UTF-16 and a hostile or corrupt table can carry any of these.
    /// </summary>
    private static void GuardToken(string value, string what)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException(what + " is empty — a scatter row cannot be written.");
        if (value != value.Trim())
            throw new InvalidDataException(what + " '" + value + "' has leading or trailing space.");
        foreach (char c in value)
            if (c is ':' or '/' or '\\' or '\r' or '\n' or '\0' || char.IsControl(c))
                throw new InvalidDataException(what + " '" + value + "' carries '" + (c == '\r' ? "\\r" :
                    c == '\n' ? "\\n" : c == '\0' ? "\\0" : c.ToString()) +
                    "', which would break the scatter's line/key structure.");
        if (value.Contains("..", StringComparison.Ordinal))
            throw new InvalidDataException(what + " '" + value + "' carries a '..' segment.");
    }

    private static void DetectOverlap(IReadOnlyList<ScatterRow> rows)
    {
        foreach (var group in rows.GroupBy(r => r.Region, StringComparer.OrdinalIgnoreCase))
        {
            ScatterRow? previous = null;
            foreach (ScatterRow row in group.OrderBy(r => r.ByteOffset).ThenBy(r => r.ByteLength))
            {
                if (previous != null && row.ByteOffset < previous.ByteOffset + previous.ByteLength)
                    throw new InvalidDataException(row.Name + " starts at 0x" + row.ByteOffset.ToString("x") +
                        " inside " + previous.Name + " (0x" + previous.ByteOffset.ToString("x") + " + 0x" +
                        previous.ByteLength.ToString("x") + ") — two partitions cannot share those bytes.");
                previous = row;
            }
        }
    }

    private static void Line(StringBuilder sb, string text) => sb.Append(text).Append("\r\n");
}
