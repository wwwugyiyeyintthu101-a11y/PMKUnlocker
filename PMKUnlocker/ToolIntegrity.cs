using System.Text.Json;

namespace PMKUnlocker;

// bundled-tools SHA-256 integrity — manifest = scripts\make_tool_integrity.ps1 output
// (bundled-tools\integrity.json → output root ကို csproj glob က copy တယ်)။
// Manifest ထဲ ပါတဲ့ file တွေသာ စစ်တယ် — မပါတဲ့ (user-configured / အသစ်ထည့်)
// တွေကို ကျော်တယ်။ မကိုက်ရင် tool run / payload push မတိုင်ခင် throw နဲ့ block။
internal static class ToolIntegrity
{
    private static Dictionary<string, string>? _manifest;
    private static readonly HashSet<string> _verified = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _mismatch = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _gate = new();

    private static Dictionary<string, string> Manifest
    {
        get
        {
            var cached = _manifest;
            if (cached != null) return cached;
            lock (_gate)
            {
                cached = _manifest;
                if (cached != null) return cached;
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    string path = Path.Combine(AppContext.BaseDirectory, "integrity.json");
                    if (File.Exists(path))
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(path));
                        foreach (var el in doc.RootElement.EnumerateArray())
                        {
                            string k = el.GetProperty("k").GetString() ?? "";
                            string h = el.GetProperty("h").GetString() ?? "";
                            if (k.Length > 0 && h.Length > 0) map[k] = h;
                        }
                    }
                }
                catch { }
                _manifest = map;
                return map;
            }
        }
    }

    // path က install folder အောက် + manifest ထဲ ပါရင် hash တိုက် — ကိုက်ရင် cache,
    // မကိုက်ရင် InvalidDataException။ manifest မရှိ/မပါ/user-configured → pass (fail-open)။
    internal static void Verify(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string full = Path.GetFullPath(path);
            string root = AppContext.BaseDirectory;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;
            string key = full.Substring(root.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace(Path.DirectorySeparatorChar, '/');
            var manifest = Manifest;
            if (!manifest.TryGetValue(key, out string? expected) || expected == null) return;
            if (_verified.Contains(key)) return;
            if (_mismatch.Contains(key)) throw Mismatch(key);
            string actual = ShopServices.Hash(full);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                _mismatch.Add(key);
                throw Mismatch(key);
            }
            _verified.Add(key);
        }
        catch (InvalidDataException) { throw; }
        catch { /* ပျက်/locked ဖြစ်တဲ့ transient I/O က run မတားစေနဲ့ */ }
    }

    private static InvalidDataException Mismatch(string key) =>
        new("Tool integrity check failed: " + key + " SHA-256 mismatch — tool file was modified. Reinstall / re-download.");
}
