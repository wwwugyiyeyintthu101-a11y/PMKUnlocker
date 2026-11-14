using System.Text;
using System.Text.RegularExpressions;

namespace PMKUnlocker.MediaTek;

/// <summary>
/// The FLASH route over the bundled mtk.exe. That client has no scatter verb (its command list ends at
/// <c>multi</c>): a scatter package becomes a <c>w</c> per selected partition inside one multi script, aimed by
/// NAME at the device's own GPT. This type builds that script and judges what came back — no UI, no device state,
/// so both halves are assertable offline.
/// </summary>
internal static class MtkFlashRoute
{
    /// <summary>One partition and the exact path string the client will be handed for it.</summary>
    internal sealed record WriteTarget(string Name, string Token);

    /// <summary>What the client's own output proves, per selected partition.</summary>
    internal sealed record Outcome(
        IReadOnlyList<string> Written,
        IReadOnlyList<string> Skipped,
        IReadOnlyList<string> Failed);

    /// <summary>
    /// multi splits its script on ';' and then each command on ' ' before argparse runs — a token carrying any of
    /// those separators becomes extra argv words and argparse exits the WHOLE run where it stands, mid-flash. A file
    /// whose path has them is copied into the session directory by the caller and passed by its bare, generated
    /// name instead.
    /// </summary>
    internal static bool TokenNeedsStaging(string token) =>
        string.IsNullOrWhiteSpace(token) ||
        token.IndexOfAny(new[] { ' ', '\t', ',', ';', '\r', '\n', '"' }) >= 0;

    /// <summary>
    /// <c>printgpt;w &lt;name&gt; &lt;file&gt;;…;r &lt;name&gt; &lt;file&gt;;…;reset</c>. printgpt is the proof
    /// that a name the client did not write is absent from the live table rather than a write that failed.
    /// </summary>
    internal static string BuildScript(
        IReadOnlyList<WriteTarget> writes,
        IReadOnlyList<(string Name, string SessionFile)> readbacks)
    {
        if (writes == null || writes.Count == 0)
            throw new InvalidDataException("No partition was selected for the write list. Nothing was written.");
        var script = new StringBuilder("printgpt");
        foreach (WriteTarget w in writes)
        {
            if (w == null || !Regex.IsMatch(w.Name ?? "", @"^[A-Za-z0-9_-]+$"))
                throw new InvalidDataException("Unsafe partition name in the write list: '" + w?.Name + "'.");
            if (TokenNeedsStaging(w.Token))
                throw new InvalidDataException("The image for " + w.Name + " cannot ride in a multi script — its " +
                    "path carries a separator the client splits on. Nothing was written.");
            script.Append(";w ").Append(w.Name).Append(' ').Append(w.Token);
        }
        foreach (var rb in readbacks ?? Array.Empty<(string Name, string SessionFile)>())
        {
            if (!Regex.IsMatch(rb.Name ?? "", @"^[A-Za-z0-9_-]+$"))
                throw new InvalidDataException("Unsafe partition name in the readback list: '" + rb.Name + "'.");
            if (TokenNeedsStaging(rb.SessionFile))
                throw new InvalidDataException("The readback file for " + rb.Name + " cannot ride in a multi script.");
            script.Append(";r ").Append(rb.Name).Append(' ').Append(rb.SessionFile);
        }
        return script.Append(";reset").ToString();
    }

    /// <summary>
    /// The client prints <c>Wrote &lt;token&gt; to sector …</c> once per partition it actually programmed. A selected
    /// name with no such line is either absent from the live GPT — the <c>w</c> verb can only aim at names the table
    /// has, so it was never written — or a write that did not complete. Those two are kept apart: the first is a
    /// skip the operator must see, the second is corruption and stops the run.
    /// </summary>
    internal static Outcome Classify(
        IReadOnlyList<string> lines,
        IReadOnlyList<WriteTarget> writes,
        IEnumerable<string>? gptNames)
    {
        ArgumentNullException.ThrowIfNull(writes);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines ?? Array.Empty<string>())
        {
            string t = (line ?? "").TrimStart();
            if (!t.StartsWith("Wrote ", StringComparison.Ordinal)) continue;
            int c = t.IndexOf(" to sector ", StringComparison.Ordinal);
            if (c <= 6) continue;
            string token = Normalize(t.Substring(6, c - 6));
            counts[token] = counts.GetValueOrDefault(token) + 1;
        }
        var table = new HashSet<string>(gptNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var written = new List<string>();
        var skipped = new List<string>();
        var failed = new List<string>();
        foreach (WriteTarget w in writes)
        {
            string token = Normalize(w.Token);
            if (counts.TryGetValue(token, out int n) && n > 0)
            {
                counts[token] = n - 1;
                written.Add(w.Name);
            }
            else if (table.Count > 0 && !table.Contains(w.Name)) skipped.Add(w.Name);
            else failed.Add(w.Name);
        }
        return new Outcome(written, skipped, failed);
    }

    private static string Normalize(string token) => (token ?? "").Trim().Replace('\\', '/');
}
