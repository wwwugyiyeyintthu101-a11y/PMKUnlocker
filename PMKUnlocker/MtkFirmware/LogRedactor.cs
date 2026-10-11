using System.Text.RegularExpressions;

namespace PMKUnlocker.MtkFirmware;

internal static partial class LogRedactor
{
    [GeneratedRegex(@"(?im)^.*\b(IMEI|MEID|SOC_ID|SN|Serial Number)\b\s*[:=]\s*\S+.*$")]
    private static partial Regex IdentifierLine();

    [GeneratedRegex(@"(?<![^\s])-s\s+(""[^""]+""|\S+)")]
    private static partial Regex FastbootSerialArg();

    [GeneratedRegex(@"(?i)\b[0-9a-f]{32,}\b")]
    private static partial Regex LongHex();

    [GeneratedRegex(@"(?<!\d)\d{14,17}(?!\d)")]
    private static partial Regex DigitRun();

    internal static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        string result = IdentifierLine().Replace(text, m =>
        {
            int sep = m.Value.IndexOfAny(new[] { ':', '=' });
            return sep < 0 ? "[REDACTED]" : m.Value.Substring(0, sep + 1) + " [REDACTED]";
        });
        result = FastbootSerialArg().Replace(result, "-s \"[REDACTED]\"");
        result = LongHex().Replace(result, "[REDACTED-HEX]");
        result = DigitRun().Replace(result, "[REDACTED-NUM]");
        return result;
    }

    internal static string RedactFile(string path)
    {
        string content = File.ReadAllText(path);
        string redacted = Redact(content);
        if (!string.Equals(content, redacted, StringComparison.Ordinal))
            File.WriteAllText(path, redacted);
        return path;
    }
}
