#nullable disable
using System.Globalization;
using System.Text.Json;

namespace PMKUnlocker.MediaTek;

// ================= MTK CHIPSET COVERAGE (payload-free) =================
//
// The native DA session's command layer is chipset-generic — READ/WRITE/ERASE/GPT are the same bytes on an MT6765
// and an MT6877. What differs per chipset is the BROM prelude and the DA/EMI images it uploads, and those are
// recorded, never derived. So "does this build support CPU X" is exactly one question: is there a recipe whose
// HW code matches X?
//
// This file answers that question offline. It lists the installed recipes (chipset + HW code) so the combo and the
// log name the CPUs this build can boot, it flags the ways a recipe tree goes wrong before a phone is plugged in
// (a recipe that does not parse, images that are missing or the wrong size, and two recipes fighting over one HW
// code or one name), and it turns a boot refusal on an unknown HW code into the sentence that says what to add and
// where.
//
// It deliberately does not hash blobs in the listing path: Peek is cheap, Load is the gate. Verify() runs Load for
// an operator who wants the full hash-checked audit without a phone on the bench.

/// <summary>
/// A recipe as listed without booting anything: identity plus whether its images are on disk at the recorded size.
/// <see cref="Readable"/> is false only when the JSON itself could not be read as a recipe.
/// </summary>
internal sealed record MtkRecipeHeader(
    string Path, string Chipset, ushort HwCode, int BlobCount, long BlobBytes, bool BlobsOnDisk, string Problem)
{
    internal bool Readable => Chipset.Length > 0;

    /// <summary>"MT6765 (HW 0x0766)" — the HW code is the number that actually decides which recipe runs, so it is
    /// shown beside the name instead of being left in the file.</summary>
    internal string Label => Readable
        ? Chipset + " (HW 0x" + HwCode.ToString("X4", CultureInfo.InvariantCulture) + ")"
        : System.IO.Path.GetFileName(Path);
}

/// <summary>One recipe after the full gate: what <see cref="MtkBootProfile.Load"/> proved about it.</summary>
internal sealed record MtkRecipeCheck(
    string Path, string Chipset, ushort HwCode, int BlobCount, long BlobBytes, int BromSteps, int DaSteps,
    bool Loaded, string Problem)
{
    internal string Label => Chipset.Length > 0
        ? Chipset + " (HW 0x" + HwCode.ToString("X4", CultureInfo.InvariantCulture) + ")"
        : System.IO.Path.GetFileName(Path);
}

internal static class MtkRecipeAudit
{
    /// <summary>Peek every installed recipe. Cheap enough to run while the NATIVE tab is being built.</summary>
    internal static IReadOnlyList<MtkRecipeHeader> Headers(IEnumerable<string> paths)
    {
        var headers = new List<MtkRecipeHeader>();
        if (paths == null) return headers;
        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            headers.Add(MtkBootProfile.Peek(path));
        }
        return headers;
    }

    /// <summary>
    /// The one line that answers "which CPUs can this build boot natively?". Colons, not em dashes: the screen's
    /// concise pass trims a line at " — ", and this sentence must survive intact.
    /// </summary>
    internal static string CoverageLine(IReadOnlyList<MtkRecipeHeader> headers)
    {
        if (headers == null || headers.Count == 0)
            return "no boot recipe is installed, so there is no CPU this build can boot natively (add one under mtk\\boot-profiles)";

        int refused = headers.Count(h => !h.Readable);
        string line = headers.Count + " recipe(s) installed: " + CoverageNames(headers);
        if (refused > 0) line += "; " + refused + " refused (see the recipe lines below)";
        return line;
    }

    /// <summary>"MT6765 (HW 0x0766), MT6877 (HW 0x0959)" — just the CPUs, for a label that already says what it is.</summary>
    internal static string CoverageNames(IReadOnlyList<MtkRecipeHeader> headers)
    {
        var readable = headers == null
            ? new List<string>()
            : headers.Where(h => h.Readable).Select(h => h.Label).ToList();
        return readable.Count > 0 ? string.Join(", ", readable) : "none readable";
    }

    /// <summary>
    /// Everything wrong with the installed recipe tree, in the order an operator should fix it. A recipe that does
    /// not parse, images that are missing or the wrong size, two recipes claiming one HW code (Auto runs the first
    /// in folder order and the other silently never boots), and one chipset name installed twice with different
    /// codes (the combo would show the same label twice).
    /// </summary>
    internal static IReadOnlyList<string> Problems(IReadOnlyList<MtkRecipeHeader> headers)
    {
        var problems = new List<string>();
        if (headers == null) return problems;

        foreach (MtkRecipeHeader header in headers)
        {
            if (!header.Readable)
                problems.Add(System.IO.Path.GetFileName(header.Path) + ": " + header.Problem);
            else if (!header.BlobsOnDisk)
                problems.Add(header.Label + ": " + header.Problem);
        }

        foreach (var group in headers.Where(h => h.Readable).GroupBy(h => h.HwCode))
        {
            if (group.Count() < 2) continue;
            var names = group.Select(h => h.Label).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string who = names.Count > 1 ? string.Join(" and ", names) : group.Count() + " recipes named " + names[0];
            problems.Add("HW 0x" + group.Key.ToString("X4", CultureInfo.InvariantCulture) + " is claimed by " + who +
                ": Auto runs the first, so only one can stay");
        }

        foreach (var group in headers.Where(h => h.Readable).GroupBy(h => h.Chipset, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() > 1 && group.Select(h => h.HwCode).Distinct().Count() > 1)
                problems.Add(group.Key + " is installed twice with different HW codes: one of them is mislabelled");
        }

        return problems;
    }

    /// <summary>
    /// Run the real gate over every recipe: parse, hash every blob, check every framed upload. No phone is touched,
    /// so this is how a recipe for a new CPU is checked before the first boot instead of during it.
    /// </summary>
    internal static IReadOnlyList<MtkRecipeCheck> Verify(IEnumerable<string> paths)
    {
        var checks = new List<MtkRecipeCheck>();
        if (paths == null) return checks;
        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            MtkRecipeHeader header = MtkBootProfile.Peek(path);
            if (!header.Readable)
            {
                checks.Add(new MtkRecipeCheck(path, "", 0, 0, 0, 0, 0, false, header.Problem));
                continue;
            }
            try
            {
                MtkBootProfile profile = MtkBootProfile.Load(path);
                checks.Add(new MtkRecipeCheck(path, profile.Chipset, profile.HwCode, profile.Blobs.Count,
                    profile.Blobs.Sum(b => (long)b.Bytes), profile.BromSteps.Count, profile.DaSteps.Count, true, ""));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                JsonException or FormatException or ArgumentException)
            {
                checks.Add(new MtkRecipeCheck(path, header.Chipset, header.HwCode, header.BlobCount, header.BlobBytes,
                    0, 0, false, ex.Message));
            }
        }
        return checks;
    }

    /// <summary>One verified recipe as a row: what it is and what Load proved about it.</summary>
    internal static string VerifyLine(MtkRecipeCheck check)
    {
        if (check == null) return "";
        if (!check.Loaded) return check.Label + ": refused: " + check.Problem;
        return check.Label + ": " + check.BlobCount + " blob(s) / " + check.BlobBytes + " B hash-verified, " +
            check.BromSteps + " BROM + " + check.DaSteps + " DA step(s)";
    }

    /// <summary>"2 recipe(s) hash-verified, 0 refused." — the headline for an offline audit.</summary>
    internal static string VerifySummary(IReadOnlyList<MtkRecipeCheck> checks)
    {
        if (checks == null || checks.Count == 0) return "no recipe to audit";
        int loaded = checks.Count(c => c.Loaded);
        int refused = checks.Count - loaded;
        return checks.Count + " recipe(s) audited: " + loaded + " hash-verified, " + refused + " refused";
    }

    /// <summary>
    /// "MT6765 on 0x0766, MT6877 on 0x0959" — the CPUs this build actually carries. Empty is a sentence, not a blank.
    /// </summary>
    internal static string InstalledList(IEnumerable<(string Chipset, ushort HwCode)> installed)
    {
        var names = new List<string>();
        if (installed != null)
        {
            foreach (var recipe in installed)
            {
                if (string.IsNullOrWhiteSpace(recipe.Chipset)) continue;
                names.Add(recipe.Chipset + " on 0x" + recipe.HwCode.ToString("X4", CultureInfo.InvariantCulture));
            }
        }
        return names.Count == 0 ? "none installed" : string.Join(", ", names);
    }

    /// <summary>
    /// The sentence a boot gets when the BROM reports a HW code no recipe claims. It names the code, the CPUs that
    /// are installed, and the one thing that would fix it — and it says out loud that the phone was not touched in a
    /// chipset-specific way, because "unsupported" and "we already sprayed it" are very different states.
    /// </summary>
    internal static string UnknownChipText(ushort hwCode, IEnumerable<(string Chipset, ushort HwCode)> installed)
    {
        string code = "0x" + hwCode.ToString("X4", CultureInfo.InvariantCulture);
        return "No installed recipe is recorded on HW " + code + " (installed: " + InstalledList(installed) + "). " +
            "The native command layer is chipset-generic, but the BROM prelude and its DA/EMI images are recorded per " +
            "chipset, so this CPU needs its own recipe: put <chipset>_boot_profile.json and its blobs under " +
            "mtk\\boot-profiles\\<chipset>\\ and Auto will pick it by HW code on the next boot. " +
            "Nothing chipset-specific was sent to this phone.";
    }
}
