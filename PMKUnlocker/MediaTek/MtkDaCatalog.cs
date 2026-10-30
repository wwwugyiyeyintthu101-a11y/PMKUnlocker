#nullable disable
using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PMKUnlocker.MediaTek;

internal sealed record MtkDeviceInfo(string Da, string Auth, string Preloader);

// Loader auto-detect result — which catalog entry was chosen for a chipset and why.
internal sealed record MtkChipsetMatch(string Brand, string Model, MtkDeviceInfo Info, string Chipset, string Basis);

// MTK Download-Agent (DA) catalog — brand → model → {da, auth, preloader} paths.
// JSON (pmk_mtk_devices.json) က brand/model seed; drop-in folder
// mtk/loaders/<Brand>/<Model>/ ထဲ ဖိုင် ထည့်ရင် path auto-fill (JSON edit မလို)။
internal static class MtkDaCatalog
{
    internal static Dictionary<string, Dictionary<string, MtkDeviceInfo>> Load(string jsonPath, string loadersRoot)
    {
        var db = new Dictionary<string, Dictionary<string, MtkDeviceInfo>>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(jsonPath))
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Dictionary<string, string>>>>(File.ReadAllText(jsonPath));
            if (raw != null)
            {
                foreach (var brandEntry in raw)
                {
                    var modelMap = new Dictionary<string, MtkDeviceInfo>(StringComparer.OrdinalIgnoreCase);
                    foreach (var modelEntry in brandEntry.Value)
                    {
                        var files = modelEntry.Value;
                        modelMap[modelEntry.Key] = new MtkDeviceInfo(
                            files.GetValueOrDefault("da", ""),
                            files.GetValueOrDefault("auth", ""),
                            files.GetValueOrDefault("preloader", ""));
                    }
                    db[brandEntry.Key] = modelMap;
                }
            }
        }
        Scan(db, loadersRoot);
        return db;
    }

    // Drop-in scan: <loadersRoot>/<Brand>/<Model>/{da.*, auth.*, preloader.*}
    // folder ထဲ ဖိုင် ရှိမှသာ model entry ထည့်/update — ဖိုင် မရှိရင် JSON seed အတိုင်း ထား
    internal static void Scan(Dictionary<string, Dictionary<string, MtkDeviceInfo>> db, string loadersRoot)
    {
        if (db == null || string.IsNullOrEmpty(loadersRoot) || !Directory.Exists(loadersRoot)) return;
        foreach (string brandDir in Directory.GetDirectories(loadersRoot))
        {
            string brand = Path.GetFileName(brandDir);
            if (brand.Length == 0) continue;
            if (!db.TryGetValue(brand, out var models))
                models = db[brand] = new Dictionary<string, MtkDeviceInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (string modelDir in Directory.GetDirectories(brandDir))
            {
                string model = Path.GetFileName(modelDir);
                if (model.Length == 0) continue;
                string da = FindFile(modelDir, "da", "da.bin", "da.img");
                string auth = FindFile(modelDir, "auth", "auth.auth", "auth.bin");
                string pre = FindFile(modelDir, "preloader", "preloader.bin", "preloader.img");
                if (da.Length == 0 && auth.Length == 0 && pre.Length == 0) continue;
                models.TryGetValue(model, out var existing);
                models[model] = new MtkDeviceInfo(
                    da.Length > 0 ? da : existing?.Da ?? "",
                    auth.Length > 0 ? auth : existing?.Auth ?? "",
                    pre.Length > 0 ? pre : existing?.Preloader ?? "");
            }
        }
    }

    // Exact conventional names first; else any file whose name contains the role substring
    // (DA_SWSEC.bin, MTK_AllInOne_DA_6765.bin, auth_sv5.auth, preloader_k69v1.bin, ...)
    internal static string FindFile(string dir, string role, params string[] exactNames)
    {
        foreach (string n in exactNames)
        {
            string p = Path.Combine(dir, n);
            if (File.Exists(p)) return p;
        }
        foreach (string f in Directory.EnumerateFiles(dir))
        {
            if (Path.GetFileName(f).Contains(role, StringComparison.OrdinalIgnoreCase)) return f;
        }
        return "";
    }

    internal static string Resolve(string relativeOrAbsolute, string baseDir)
    {
        if (string.IsNullOrWhiteSpace(relativeOrAbsolute)) return "";
        return Path.IsPathRooted(relativeOrAbsolute)
            ? relativeOrAbsolute
            : Path.Combine(baseDir, relativeOrAbsolute.Replace('/', Path.DirectorySeparatorChar));
    }

    // ==================== LOADER AUTO-DETECT (chipset → DA/auth) ====================
    // BROM/Preloader reports the chipset as "CPU:\tMT6765/MT8768t(Helio P35/G35)" and uploads
    // payloads/mt6765_payload.bin. Both spellings are reduced to an MT#### token here and then
    // matched against the catalog by two independent signals:
    //   1. the DA blob itself — "MTK_DOWNLOAD_AGENT" header + LE u16 chip code at 0x6E.
    //      Measured over the bundled catalog: every classic DA carries the same value as the MT
    //      token its folder is named after (0x6739/6761/6763/6765/6768/6771/6779/6781/6785/
    //      6833/6853/6873/6877/6885/6893). DAv5/AllInOne-v6 repacks keep a build field there
    //      (0x09xx/0x11xx/0x12xx/0x13xx), so they are matched by folder name instead.
    //   2. the MT#### token in the loader folder / model name.
    // Signal 1 is what catches the 130 model-named folders (Honor X5 Plus, Nothing Phone 2A …)
    // whose names carry no chipset token.

    // Shared-download-agent families (upstream mtkclient dacode 0x6761 = MT6761/MT6762/MT3369/
    // MT8766B/MT8761; dacode 0x6768 = MT6768/MT6769 — the catalog's MT6768 DAs are the ones the
    // MT6769 models were wired to). Only families proven by a bundled DA declaring the code.
    static readonly Dictionary<string, string[]> ChipsetFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MT6761"] = new[] { "MT6762" },
        ["MT6762"] = new[] { "MT6761" },
        ["MT6768"] = new[] { "MT6769" },
        ["MT6769"] = new[] { "MT6768" },
    };

    static readonly Regex ChipTokenRx = new(@"MT\d{4}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex PayloadChipRx = new(@"mt(\d{4})_payload", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "MT6765/MT8768t(Helio P35/G35)" → "MT6765" (first token wins — mtkclient prints the
    // chip it selected); anything without a token → "".
    internal static string PrimaryChipset(string text)
    {
        var m = text == null ? Match.Empty : ChipTokenRx.Match(text);
        return m.Success ? m.Value.ToUpperInvariant() : "";
    }

    // Fallback signal from a command log: the payload the preloader accepted.
    internal static string ChipsetFromPayloadLines(IEnumerable<string> lines)
    {
        if (lines == null) return "";
        foreach (string line in lines)
        {
            if (line == null) continue;
            var m = PayloadChipRx.Match(line);
            if (m.Success) return "MT" + m.Groups[1].Value.ToUpperInvariant();
        }
        return "";
    }

    static bool IsChipToken(string value) =>
        !string.IsNullOrEmpty(value) && Regex.IsMatch(value, @"^MT\d{4}$", RegexOptions.IgnoreCase);

    // Device chipset first, then the families that legitimately share its download agent.
    internal static IReadOnlyList<string> ChipsetCandidates(string chipset)
    {
        var list = new List<string>();
        string chip = (chipset ?? "").Trim().ToUpperInvariant();
        if (!IsChipToken(chip)) return list;
        list.Add(chip);
        if (ChipsetFamilies.TryGetValue(chip, out var family))
            foreach (string f in family)
                if (!list.Contains(f, StringComparer.OrdinalIgnoreCase)) list.Add(f);
        return list;
    }

    // "MT6765" → 0x6765 (MediaTek writes the dacode in hex, same digits as the marketing name)
    static int ChipCodeOf(string chipset)
    {
        if (!IsChipToken(chipset)) return -1;
        return int.TryParse(chipset.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v) ? v : -1;
    }

    static readonly Dictionary<string, int> daCodeCache = new(StringComparer.OrdinalIgnoreCase);

    // LE u16 at 0x6E of a MTK_DOWNLOAD_AGENT blob; -1 when the file is not a classic DA header.
    internal static int ReadDaChipCode(string daPath)
    {
        if (string.IsNullOrWhiteSpace(daPath)) return -1;
        string key;
        try
        {
            var fi = new FileInfo(daPath);
            if (!fi.Exists || fi.Length < 112) return -1;
            key = fi.FullName + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
        }
        catch { return -1; }
        lock (daCodeCache) if (daCodeCache.TryGetValue(key, out int cached)) return cached;
        int code = -1;
        try
        {
            using var fs = File.OpenRead(daPath);
            var head = new byte[112];
            fs.ReadExactly(head);
            if (System.Text.Encoding.ASCII.GetString(head, 0, 18) == "MTK_DOWNLOAD_AGENT")
                code = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(110));
        }
        catch { code = -1; }
        lock (daCodeCache) daCodeCache[key] = code;
        return code;
    }

    // Tier 4 = folder named for the chipset AND its DA blob declaring that code (a chipset-keyed
    // loader — the SP-Flash-Tool-style first choice). Tier 3 = one signal only: a model-named folder
    // whose DA really declares the chip, or a chipset-named folder whose blob is a DAv5/AllInOne-v6
    // repack (no chip code there). A shared-DA family member (MT6762→0x6761, MT6769→0x6768) is one
    // rung lower, so a directly matching loader always wins when the catalog has one.
    static (int Tier, string Basis) ScoreFolder(string model, string daPath, IReadOnlyList<string> candidates)
    {
        string nameToken = PrimaryChipset(model);
        int code = ReadDaChipCode(daPath);
        int tier = 0; string basis = "";
        for (int i = 0; i < candidates.Count; i++)
        {
            string c = candidates[i];
            bool named = string.Equals(nameToken, c, StringComparison.OrdinalIgnoreCase);
            int want = ChipCodeOf(c);
            bool declared = code >= 0 && want >= 0 && code == want;
            int t = named && declared ? 4 : (named || declared) ? 3 : 0;
            if (i > 0 && t > 0) t--;
            if (t == 0) continue;
            string b = named && declared ? "loader folder " + c + " + DA code 0x" + code.ToString("x4", CultureInfo.InvariantCulture)
                : named ? "loader folder " + c
                : "DA code 0x" + code.ToString("x4", CultureInfo.InvariantCulture);
            if (t > tier) { tier = t; basis = b; }
        }
        return (tier, basis);
    }

    // Best DA/auth/preloader entry for a chipset. preferredBrand only breaks ties inside the same
    // match tier, so a brand-matched folder can never beat a DA that really declares the chip.
    internal static MtkChipsetMatch FindByChipset(
        Dictionary<string, Dictionary<string, MtkDeviceInfo>> db, string baseDir, string chipset, string preferredBrand = null)
    {
        if (db == null) return null;
        var candidates = ChipsetCandidates(chipset);
        if (candidates.Count == 0) return null;
        MtkChipsetMatch best = null; int bestScore = -1; string bestName = "";
        foreach (var brandEntry in db)
        {
            bool brandWanted = !string.IsNullOrEmpty(preferredBrand) &&
                string.Equals(brandEntry.Key, preferredBrand, StringComparison.OrdinalIgnoreCase);
            foreach (var modelEntry in brandEntry.Value)
            {
                MtkDeviceInfo info = modelEntry.Value;
                string da = Resolve(info.Da, baseDir);
                if (string.IsNullOrEmpty(da) || !File.Exists(da)) continue;
                var (tier, basis) = ScoreFolder(modelEntry.Key, da, candidates);
                if (tier == 0) continue;
                int extra = (Resolve(info.Auth, baseDir) is { Length: > 0 } a && File.Exists(a) ? 4 : 0)
                          | (brandWanted ? 2 : 0)
                          | (Resolve(info.Preloader, baseDir) is { Length: > 0 } p && File.Exists(p) ? 1 : 0);
                int score = tier * 8 + extra;
                string name = brandEntry.Key + "/" + modelEntry.Key;
                if (score > bestScore || (score == bestScore &&
                    string.Compare(name, bestName, StringComparison.OrdinalIgnoreCase) < 0))
                {
                    bestScore = score; bestName = name;
                    best = new MtkChipsetMatch(brandEntry.Key, modelEntry.Key, info, candidates[0], basis);
                }
            }
        }
        return best;
    }

    // Manual-override sanity check: does a chosen DA belong to this chipset's family at all?
    internal static bool DaMatchesChipset(string modelOrPath, string daPath, string chipset)
    {
        var candidates = ChipsetCandidates(chipset);
        if (candidates.Count == 0 || string.IsNullOrWhiteSpace(daPath) || !File.Exists(daPath)) return false;
        return ScoreFolder(Path.GetFileName(Path.GetDirectoryName(daPath) ?? modelOrPath ?? ""), daPath, candidates).Tier > 0;
    }
}
