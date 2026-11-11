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

/// <summary>
/// Why a BROM session stopped before a DA ran, when the reason was vendor authorisation (SBC/SLA/DAA).
/// A marker alone never triggers this: a successful bypass prints "Auth file is required" on every DAA unit,
/// and "SLA required" is announced before the challenge is answered. Only a run that then failed — or a refusal
/// the client itself names — counts, so the notice cannot tell an operator to go hunting for a file a boot did
/// not need.
/// </summary>
internal sealed record MtkBromLock(string Flags, string Reason)
{
    private static readonly string[] ConfigFlags = { "SBC", "SLA", "DAA" };

    internal static MtkBromLock? Detect(MtkCommandResult result)
    {
        if (result?.Lines == null || result.Lines.Count == 0) return null;
        bool authMissing = Has(result, "Auth file is required");
        bool slaAsked = Has(result, "SLA required");
        bool slaRefused = Has(result, "Bad sla challenge") || Has(result, "Send auth error") ||
                          Has(result, "Send sla challenge response len error") ||
                          Has(result, "Error on DA_Send") || Has(result, "DA_Send status error");
        if (!slaRefused && !((authMissing || slaAsked) && result.HasError)) return null;

        var flags = ConfigFlags
            .Where(f => result.Lines.Any(l =>
                Regex.IsMatch(Plain(l), @"\b" + f + @"\s+enabled:\s*True\b", RegexOptions.IgnoreCase)))
            .ToList();
        string reason = slaRefused
            ? "the DA upload was refused after the SLA challenge"
            : authMissing
                ? "this unit requires its own vendor auth file (DAA)"
                : "the BROM asked for an SLA challenge";
        return new MtkBromLock(flags.Count > 0 ? string.Join("/", flags) : "SBC/SLA/DAA", reason);
    }

    private static bool Has(MtkCommandResult result, string token) =>
        result.Lines.Any(l => Plain(l).Contains(token, StringComparison.OrdinalIgnoreCase));

    private static string Plain(string line) => Regex.Replace(line, @"\x1B\[[0-9;]*[a-zA-Z]", "");
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

    // da imei ရဲ့ positional arg = "imei1,imei2" — file-path comma guard ကို ဒီ slot မှာ မသုံး
    internal static bool IsImeiWriteListArg(string[] cmdArgs, int index) =>
        index == 2 && cmdArgs.Length > 2 &&
        string.Equals(cmdArgs[0], "da", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(cmdArgs[1], "imei", StringComparison.OrdinalIgnoreCase);

    // Where --loader/--auth/--preloader must be re-inserted (argparse parents=[base]
    // defaults overwrite anything placed at an ancestor subparser level).
    internal static int BaseFlagInsertIndex(string[] cmdArgs)
    {
        if (cmdArgs.Length > 0 && string.Equals(cmdArgs[0], "multi", StringComparison.OrdinalIgnoreCase))
            return -1;
        if (cmdArgs.Length >= 2 && string.Equals(cmdArgs[0], "da", StringComparison.OrdinalIgnoreCase))
            return 2;
        return 1;
    }

    internal static bool IsImeiWriteSuccess(MtkCommandResult result) =>
        result.Lines.Any(l => l.Contains("IMEIs were written successfully", StringComparison.OrdinalIgnoreCase));

    internal static bool IsImeiWriteFailed(MtkCommandResult result) =>
        result.HasError || result.Lines.Any(l => l.Contains("Failed to write IMEIs.", StringComparison.OrdinalIgnoreCase));

    // IMEI input guard — 15 digits, Luhn checksum (same algorithm mtkclient recomputes on write)
    internal static bool LuhnValid(string digits)
    {
        if (digits.Length != 15) return false;
        int sum = 0;
        for (int i = 0; i < 15; i++)
        {
            char c = digits[i];
            if (c < '0' || c > '9') return false;
            int d = c - '0';
            if ((i & 1) == 1) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
        }
        return sum % 10 == 0;
    }

    internal static void RequireWriteEvidence(MtkCommandResult result, string partition, string imagePath)
    {
        result.RequireSuccess();
        // Upstream can return exit 0 after a failed write. Require its positive
        // per-partition completion message, not a percentage or process exit.
        if (!HasWriteEvidence(result, imagePath))
            throw new IOException("Write completion was not confirmed for " + partition + ". It may be partially written; remaining writes stopped.");
    }
}
