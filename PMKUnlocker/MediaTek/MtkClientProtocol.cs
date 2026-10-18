using System.Globalization;
using System.Text.RegularExpressions;

namespace PMKUnlocker.MediaTek;

internal sealed record MtkConnection(string Port, string Loader, string Auth, string Preloader);
internal sealed record MtkPartition(string Name, long Offset, long Length);
internal sealed record MtkCommandResult(int ExitCode, IReadOnlyList<string> Lines)
{
    private const string ErrorPattern =
        @"\b(error|failed|failure|traceback|exception)\b|not found|couldn't|could not|please disconnect";
    public bool HasError => ExitCode != 0 || Lines.Any(s => Regex.IsMatch(s, ErrorPattern, RegexOptions.IgnoreCase));
    public IEnumerable<string> ErrorLines =>
        Lines.Where(s => Regex.IsMatch(s, ErrorPattern, RegexOptions.IgnoreCase));
    public void RequireSuccess()
    {
        if (HasError) throw new IOException("mtkclient reported an error. Review the operation log; remaining operations were stopped.");
    }
}

internal sealed partial class MtkClientEngine
{
    internal static IReadOnlyList<MtkPartition> ParseGpt(MtkCommandResult result)
    {
        result.RequireSuccess();
        var partitions = new List<MtkPartition>();
        foreach (string line in result.Lines)
        {
            var m = Regex.Match(line, @"^\s*([\w-]+):\s+Offset\s+(0x[\da-fA-F]+),\s+Length\s+(0x[\da-fA-F]+)");
            if (!m.Success) continue;
            long offset = long.Parse(m.Groups[2].Value[2..], NumberStyles.HexNumber);
            long length = long.Parse(m.Groups[3].Value[2..], NumberStyles.HexNumber);
            if (offset < 0 || length <= 0 || offset > long.MaxValue - length)
                throw new InvalidDataException("Device returned an invalid partition range.");
            partitions.Add(new(m.Groups[1].Value, offset, length));
        }
        if (partitions.Count == 0 || partitions.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != partitions.Count)
            throw new InvalidDataException("A complete, unique device partition table was not received.");
        return partitions;
    }

    internal static bool HasWriteEvidence(MtkCommandResult result, string imagePath) =>
        result.Lines.Any(l => l.TrimStart().StartsWith("Wrote " + imagePath + " to sector ", StringComparison.OrdinalIgnoreCase));

    internal static void RequireWriteEvidence(MtkCommandResult result, string partition, string imagePath)
    {
        result.RequireSuccess();
        // Upstream can return exit 0 after a failed write. Require its positive
        // per-partition completion message, not a percentage or process exit.
        if (!HasWriteEvidence(result, imagePath))
            throw new IOException("Write completion was not confirmed for " + partition + ". It may be partially written; remaining writes stopped.");
    }
}
