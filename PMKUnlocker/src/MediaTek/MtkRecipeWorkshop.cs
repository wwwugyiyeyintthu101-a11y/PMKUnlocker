#nullable disable
using System.Text.RegularExpressions;

namespace PMKUnlocker.MediaTek;

// ================= WHAT IS MISSING FOR THE NEXT CPU (payload-free) =================
//
// P2.0 answers "which CPUs can this build boot": the ones with a recipe. This file answers the follow-up an
// operator actually asks — "which CPU can I add next, and what is already on disk for it?".
//
// The two halves of a recipe have different sources. The DA/EMI images come from the loader packs under
// mtk/loaders (already installed for two dozen chipsets on this machine), and the BROM prelude comes from one
// recorded boot on that chipset. This scan joins the two, so the answer is a list instead of a guess: a chipset
// that has images but no recipe is one capture away, and a chipset with a recipe needs nothing.
//
// It reads names and sizes only. No blob is opened, hashed or copied, and nothing is written anywhere — a
// "ready" line here is a shopping list, not a boot authorisation.

/// <summary>One chipset that has at least one .bin/.auth image under mtk/loaders.</summary>
internal sealed record MtkLoaderPack(string Chipset, int Images, int AuthFiles, string Folder)
{
    internal int Files => Images + AuthFiles;
    internal string Label => Chipset + " (" + Files + " file(s) under " + Folder + ")";
}

internal static class MtkRecipeWorkshop
{
    internal static string DefaultLoadersDirectory => Path.Combine(AppContext.BaseDirectory, "mtk", "loaders");

    // "MT6768", the "DA_PL_M6768_HONOR" spelling, and the "MTK_AllInOne_DA_6761" spelling all name a chipset.
    // A bare four-digit number is a model number far more often than a chipset, so the M must be attached the way
    // a chipset name attaches it: MT####, _M####, or MTK…_####. The trailing (?!\d) keeps a model code like
    // "MTK_AllInOne_DA-S96616AA1" from reading as "MT9661".
    private static readonly Regex MtkToken = new(@"(?:MT|_M|MTK[A-Za-z0-9_\-]*?_)(\d{4})(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Group the loader packs by the chipset their path names. One row per chipset, not per folder: a chipset
    /// installed under three brands is still one CPU to add. A file whose path names no MT code is skipped
    /// rather than attributed to a neighbour.
    /// </summary>
    internal static IReadOnlyList<MtkLoaderPack> ScanPacks(string loadersRoot)
    {
        var rows = new List<MtkLoaderPack>();
        if (string.IsNullOrWhiteSpace(loadersRoot) || !Directory.Exists(loadersRoot)) return rows;

        var byChipset = new Dictionary<string, (int images, int auth, string folder)>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(loadersRoot, "*", SearchOption.AllDirectories); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return rows;
        }

        foreach (string file in files)
        {
            string ext = Path.GetExtension(file);
            bool image = string.Equals(ext, ".bin", StringComparison.OrdinalIgnoreCase);
            bool auth = string.Equals(ext, ".auth", StringComparison.OrdinalIgnoreCase);
            if (!image && !auth) continue;

            string relative = Path.GetRelativePath(loadersRoot, file);
            string chipset = ChipsetToken(relative);
            if (chipset.Length == 0) continue;

            string folder = Path.GetDirectoryName(relative) ?? "";
            if (folder.Length == 0) folder = ".";
            if (!byChipset.TryGetValue(chipset, out var row))
                row = (0, 0, folder);
            row = (row.images + (image ? 1 : 0), row.auth + (auth ? 1 : 0), row.folder);
            byChipset[chipset] = row;
        }

        foreach (var pair in byChipset.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            rows.Add(new MtkLoaderPack(pair.Key, pair.Value.images, pair.Value.auth, pair.Value.folder));
        return rows;
    }

    /// <summary>"MT6768" out of "...\Huawei\DA_PL_M6768_HONOR\x.bin", or "" when the path names no chipset.</summary>
    internal static string ChipsetToken(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return "";
        Match match = MtkToken.Match(relativePath);
        return match.Success ? "MT" + match.Groups[1].Value : "";
    }

    /// <summary>Packs whose chipset no installed recipe claims — the CPUs one recorded boot away.</summary>
    internal static IReadOnlyList<MtkLoaderPack> WithoutRecipe(IReadOnlyList<MtkLoaderPack> packs,
        IEnumerable<MtkRecipeHeader> recipes)
    {
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (recipes != null)
            foreach (MtkRecipeHeader recipe in recipes)
                if (recipe != null && recipe.Readable) installed.Add(recipe.Chipset);

        var missing = new List<MtkLoaderPack>();
        if (packs == null) return missing;
        foreach (MtkLoaderPack pack in packs)
            if (!installed.Contains(pack.Chipset)) missing.Add(pack);
        return missing;
    }

    /// <summary>
    /// One line for the NATIVE tab: the CPUs that already have images but no recipe, and which one to do next.
    /// Colons and semicolons only — the screen's concise pass trims a line at " — ".
    /// </summary>
    internal static string NextCpuLine(IReadOnlyList<MtkLoaderPack> packs, IEnumerable<MtkRecipeHeader> recipes)
    {
        if (packs == null || packs.Count == 0)
            return "no loader pack is installed either, so no CPU is one capture away (add DA/EMI images under mtk\\loaders)";

        IReadOnlyList<MtkLoaderPack> missing = WithoutRecipe(packs, recipes);
        if (missing.Count == 0)
            return "every CPU with a loader pack (" + packs.Count + ") already has a recipe";

        const int shown = 8;
        var names = missing.Take(shown).Select(p => p.Chipset).ToList();
        string list = string.Join(", ", names);
        if (missing.Count > shown) list += ", +" + (missing.Count - shown) + " more";

        // "Next" is the pack with the most images on disk: it is the CPU whose DA/EMI side is most complete, so it is
        // the cheapest capture to turn into a recipe. Ties go to the lower code, so the line is stable.
        MtkLoaderPack next = missing
            .OrderByDescending(p => p.Files)
            .ThenBy(p => p.Chipset, StringComparer.OrdinalIgnoreCase)
            .First();
        return missing.Count + " CPU(s) have loader packs but no recipe yet: " + list +
            "; next is " + next.Label + " and what it still needs is one recorded BROM-to-DA boot";
    }

    // ================= PER-CPU COVERAGE (two engines, one table) =================
    //
    // "Does my phone work in this tool?" has two honest answers, because the tool has two MTK engines. The
    // standard engine shells out to mtkclient and picks a DA from mtk/loaders by chipset; the native session
    // replays a recorded recipe. They cover different CPUs, and an operator deciding what to do with a phone on
    // the bench needs to see both columns at once — not discover the second engine's limit after a failed boot.
    //
    // The third column is the one the standard engine actually turns on: mtkclient needs a BROM exploit payload
    // named for the chip before it can upload any DA. A chipset with a catalog DA but no dedicated payload may
    // still boot through mtkclient's generic heapbait, but that is a fallback, not a supported pack.

    /// <summary>One CPU with both engines' verdicts.</summary>
    internal sealed record MtkCpuCoverage(string Chipset, string Engine, bool DedicatedPayload, bool NativeRecipe)
    {
        internal bool EngineServed => Engine.Length > 0;
    }

    internal static string DefaultPayloadsDirectory =>
        Path.Combine(AppContext.BaseDirectory, "tools", "mtkclient", "payloads");

    /// <summary>The chipsets mtkclient ships a dedicated BROM exploit payload for ("mt6765_payload.bin").</summary>
    internal static IReadOnlyList<string> ScanPayloads(string payloadsRoot)
    {
        var chips = new List<string>();
        if (string.IsNullOrWhiteSpace(payloadsRoot) || !Directory.Exists(payloadsRoot)) return chips;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(payloadsRoot, "*_payload.bin", SearchOption.TopDirectoryOnly); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return chips;
        }
        foreach (string file in files)
        {
            Match match = Regex.Match(Path.GetFileName(file), @"^mt(\d{4})_payload\.bin$", RegexOptions.IgnoreCase);
            if (match.Success) chips.Add("MT" + match.Groups[1].Value);
        }
        chips.Sort(StringComparer.OrdinalIgnoreCase);
        return chips;
    }

    /// <summary>
    /// Whether the installed mtkclient carries a chip-named BROM exploit payload for this CPU ("MT6765" →
    /// mt6765_payload.bin). A direct file check, so the auto-detect notice can say the built-in DA can still
    /// boot a chip the catalog has no loader for, instead of implying nothing can serve it.
    /// </summary>
    internal static bool HasDedicatedPayload(string chipset)
    {
        if (string.IsNullOrWhiteSpace(chipset)) return false;
        Match match = Regex.Match(chipset.Trim(), @"^MT(\d{4})$", RegexOptions.IgnoreCase);
        if (!match.Success) return false;
        try
        {
            return File.Exists(Path.Combine(DefaultPayloadsDirectory,
                "mt" + match.Groups[1].Value.ToLowerInvariant() + "_payload.bin"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Join the installed recipes with a resolver that names the MTK-engine loader for a chipset (or "" when the
    /// catalog has none). The resolver is injected so the join is assertable without the machine's loader tree.
    /// </summary>
    internal static IReadOnlyList<MtkCpuCoverage> Coverage(IReadOnlyList<MtkLoaderPack> packs,
        IEnumerable<MtkRecipeHeader> recipes, Func<string, string> engineLoader,
        IReadOnlyCollection<string> payloadChips = null)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (packs != null)
            foreach (MtkLoaderPack pack in packs) names.Add(pack.Chipset);
        if (recipes != null)
            foreach (MtkRecipeHeader recipe in recipes)
                if (recipe != null && recipe.Readable) names.Add(recipe.Chipset);
        // A chip mtkclient ships a dedicated BROM payload for is served by the standard engine through its
        // built-in DA even when the catalog has no loader for it — that is the proven Redmi 9A path. List it
        // so the table answers "can I try this CPU?" for the whole payload set, not only the catalog subset.
        if (payloadChips != null)
            foreach (string chip in payloadChips)
                if (!string.IsNullOrWhiteSpace(chip)) names.Add(chip.Trim());

        var native = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (recipes != null)
            foreach (MtkRecipeHeader recipe in recipes)
                if (recipe != null && recipe.Readable) native.Add(recipe.Chipset);
        var payloads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (payloadChips != null)
            foreach (string chip in payloadChips) payloads.Add(chip);

        var rows = new List<MtkCpuCoverage>();
        foreach (string name in names)
        {
            string loader = "";
            try { loader = engineLoader?.Invoke(name) ?? ""; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { loader = ""; }
            rows.Add(new MtkCpuCoverage(name, loader, payloads.Contains(name), native.Contains(name)));
        }
        return rows;
    }

    /// <summary>"49 CPU(s): 49 served by the MTK engine (26 with a catalog DA, 49 with a dedicated BROM payload), 2 also have a native recipe, 0 with neither."</summary>
    internal static string CoverageSummary(IReadOnlyList<MtkCpuCoverage> rows)
    {
        if (rows == null || rows.Count == 0) return "no CPU is installed under mtk\\loaders, mtk\\boot-profiles or tools\\mtkclient\\payloads";
        int catalog = rows.Count(r => r.EngineServed);
        int payload = rows.Count(r => r.DedicatedPayload);
        int served = rows.Count(r => r.EngineServed || r.DedicatedPayload);
        int both = rows.Count(r => (r.EngineServed || r.DedicatedPayload) && r.NativeRecipe);
        int neither = rows.Count(r => !r.EngineServed && !r.DedicatedPayload && !r.NativeRecipe);
        return rows.Count + " CPU(s): " + served + " served by the MTK engine (" + catalog +
            " with a catalog DA, " + payload + " with a dedicated BROM payload), " + both +
            " also have a native recipe, " + neither + " with neither";
    }

    /// <summary>The full table as markdown, for a file the shop can keep beside the phone list.</summary>
    internal static string CoverageMarkdown(IReadOnlyList<MtkCpuCoverage> rows, string when)
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine("# PMKUnlocker - MTK CPU coverage");
        text.AppendLine();
        text.AppendLine("Generated " + when + " from `mtk\\loaders`, `mtk\\boot-profiles` and `tools\\mtkclient\\payloads`.");
        text.AppendLine();
        text.AppendLine("| CPU | MTK engine (mtkclient + catalog DA) | BROM payload | Native DA session (recorded recipe) |");
        text.AppendLine("|---|---|---|---|");
        if (rows != null)
            foreach (MtkCpuCoverage row in rows)
                text.AppendLine("| " + row.Chipset + " | " +
                    (row.EngineServed ? row.Engine : row.DedicatedPayload ? "mtkclient built-in DA" : "no loader") + " | " +
                    (row.DedicatedPayload ? "dedicated" : "generic fallback") + " | " +
                    (row.NativeRecipe ? "yes" : "no - needs one recorded boot") + " |");
        text.AppendLine();
        text.AppendLine("The MTK engine column is the standard flash path. A dedicated BROM payload is the chip-named");
        text.AppendLine("`mt<code>_payload.bin` mtkclient exploits before it can upload a DA; without one it falls back to");
        text.AppendLine("the generic heapbait path, which is unproven on that chip. A native recipe is a recorded BROM-to-DA");
        text.AppendLine("boot for that chipset plus its DA/EMI images, and is not derivable from another chipset's recipe.");
        return text.ToString();
    }
}
