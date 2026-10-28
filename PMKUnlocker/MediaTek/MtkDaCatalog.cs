using System.Text.Json;

namespace PMKUnlocker.MediaTek;

internal sealed record MtkDeviceInfo(string Da, string Auth, string Preloader);

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
}
