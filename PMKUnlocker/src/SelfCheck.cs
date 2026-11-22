using System.Globalization;
using System.Text.Json;
using PMKUnlocker.MediaTek;

namespace PMKUnlocker;

/// <summary>
/// Headless proof that the package on disk is the package that was built: <c>PMKUnlocker.exe --self-check</c>.
/// <para>
/// Build success and regression success describe the source tree. The thing an operator runs is a published
/// folder, and the ways that folder goes wrong are invisible until a phone is on the bench: a manifest that
/// lost an entry, a tool replaced after packing, a catalog truncated by a bad copy, or a recipe set whose blobs
/// do not hash. Every one of those is answerable without a phone and without opening a window, so this mode
/// answers them and exits with 0 (clean) or 1 (something is wrong, with the reason on the line).
/// </para>
/// <para>
/// It is deliberately not a boot and not a flash. It never opens a device, never writes to a phone, and it
/// reuses the same gates the app uses (<see cref="MtkRecipeAudit.Verify"/> is the real hash-and-refuse path in
/// <see cref="MtkBootProfile.Load"/>), so a green self-check cannot pass something the app would refuse.
/// </para>
/// </summary>
internal static class SelfCheck
{
    internal const string Flag = "--self-check";

    // Present in every package check_release.ps1 accepts. A missing one is a packaging fault, not a licence
    // question, so it fails here rather than waiting for an operator to notice the button does nothing.
    private static readonly (string Name, string[] Places)[] RequiredTools =
    {
        ("adb.exe", new[] { "adb.exe", "tools/adb.exe" }),
        ("fastboot.exe", new[] { "fastboot.exe", "tools/fastboot.exe" }),
        ("fh_loader.exe", new[] { "fh_loader.exe", "tools/fh_loader.exe" }),
        ("QSaharaServer.exe", new[] { "QSaharaServer.exe", "tools/QSaharaServer.exe" }),
        ("heimdall.exe", new[] { "samsung/heimdall.exe", "heimdall.exe" }),
        ("libusb-1.0.dll", new[] { "libusb-1.0.dll", "samsung/libusb-1.0.dll" }),
        ("mtk.exe", new[] { "tools/mtkclient/mtk.exe" }),
    };

    // Reported but never failed: a shop that only does MTK work does not need these, and the tab says what is
    // missing when it is opened. Failing here would make the package look broken for a deliberate setup.
    private static readonly (string Name, string[] Places)[] OptionalTools =
    {
        ("python.exe", new[] { "python.exe", "tools/python.exe", "python/python.exe" }),
        ("edl.py", new[] { "edl.py", "tools/edl.py", "edl/edl.py" }),
        ("spd_dump.exe", new[] { "spd_dump.exe", "spd/spd_dump.exe", "tools/spd_dump.exe" }),
        ("unpac.exe", new[] { "unpac.exe", "spd/unpac.exe", "tools/unpac.exe" }),
    };

    internal static int Run(string[] args)
    {
        string root = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var report = new List<string>
        {
            "PMK Mobile Tool " + Version + " self-check",
            "root: " + root,
        };

        int failed = 0;
        void Result(string label, string detail, bool ok)
        {
            if (!ok) failed++;
            report.Add((ok ? "OK   " : "FAIL ") + label + ": " + detail);
        }

        Integrity(report, root, Result);
        Tools(report, root, Result);
        Catalogs(report, root, Result);
        Recipes(report, Result);
        Optional(report, root);

        report.Add(failed == 0
            ? "self-check: PASS (" + report.Count(l => l.StartsWith("OK   ", StringComparison.Ordinal)) + " checks, 0 failed)"
            : "self-check: FAIL (" + failed + " check(s) failed)");

        Publish(report);
        return failed == 0 ? 0 : 1;
    }

    private static string Version =>
        typeof(SelfCheck).Assembly.GetName().Version is { } v
            ? v.Major + "." + v.Minor + "." + v.Build
            : "unknown";

    /// <summary>Every entry in <c>integrity.json</c> re-hashed against the bytes on disk. This is the same
    /// manifest the app verifies one tool at a time; here it is verified whole, because a packaged folder is
    /// supposed to be complete and nothing else is going to look at the entries no button happens to touch.</summary>
    private static void Integrity(List<string> report, string root, Action<string, string, bool> result)
    {
        const string Manifest = "integrity.json";
        string manifestPath = Path.Combine(root, Manifest);
        if (!File.Exists(manifestPath))
        {
            result("integrity", Manifest + " is missing, so no packed file can be trusted. Reinstall a verified package.", false);
            return;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var bad = new List<string>();
            int counted = 0;
            foreach (JsonElement entry in doc.RootElement.EnumerateArray())
            {
                string key = entry.TryGetProperty("k", out JsonElement k) ? k.GetString() ?? "" : "";
                string expected = entry.TryGetProperty("h", out JsonElement h) ? h.GetString() ?? "" : "";
                if (key.Length == 0 || expected.Length != 64)
                {
                    bad.Add("malformed entry");
                    continue;
                }

                string file = Path.GetFullPath(Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar)));
                if (!file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    bad.Add("unsafe path " + key);
                    continue;
                }
                if (!File.Exists(file))
                {
                    bad.Add("missing " + key);
                    continue;
                }

                if (!string.Equals(ShopServices.Hash(file), expected, StringComparison.OrdinalIgnoreCase))
                {
                    bad.Add("changed " + key);
                    continue;
                }
                counted++;
            }

            int entries = doc.RootElement.GetArrayLength();
            if (bad.Count == 0)
                result("integrity", counted + "/" + entries + " entries hash-verified", entries > 0 && counted == entries);
            else
                result("integrity", Summarize(bad) + " (" + counted + "/" + entries + " verified)", false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            result("integrity", Manifest + " could not be read: " + ex.Message, false);
        }
    }

    private static void Tools(List<string> report, string root, Action<string, string, bool> result)
    {
        var missing = new List<string>();
        foreach (var (name, places) in RequiredTools)
        {
            try
            {
                string found = Locate(root, places);
                if (found.Length == 0) missing.Add(name);
                else ToolIntegrity.Verify(found);   // a packed tool that does not match its own entry fails here
            }
            catch (InvalidDataException ex)
            {
                missing.Add(name + " (" + ex.Message + ")");
            }
        }
        result("bundled tools", missing.Count == 0
            ? string.Join(", ", RequiredTools.Select(t => t.Name))
            : Summarize(missing) + " missing or unverified", missing.Count == 0);
    }

    private static void Catalogs(List<string> report, string root, Action<string, string, bool> result)
    {
        string devices = Path.Combine(root, "pmk_devices.json");
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(devices));
            var counts = new List<string>();
            bool empty = false;
            foreach (string key in new[] { "mtk", "qc", "spd", "samsung" })
            {
                int count = doc.RootElement.TryGetProperty(key, out JsonElement list) &&
                            list.ValueKind == JsonValueKind.Array
                    ? list.GetArrayLength()
                    : 0;
                if (count == 0) empty = true;
                counts.Add(key + " " + count.ToString(CultureInfo.InvariantCulture));
            }
            result("device catalog", string.Join(", ", counts), !empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            result("device catalog", "pmk_devices.json could not be read: " + ex.Message, false);
        }

        foreach (string name in new[] { "pmk_models.json", "pmk_codenames.json", "pmk_qc_loaders.json", "pmk_mtk_devices.json" })
        {
            string path = Path.Combine(root, name);
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                string detail = doc.RootElement.ValueKind switch
                {
                    JsonValueKind.Array => doc.RootElement.GetArrayLength() + " row(s)",
                    JsonValueKind.Object => doc.RootElement.EnumerateObject().Count() + " group(s)",
                    _ => "empty",
                };
                result(name, detail, detail != "empty");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                result(name, "could not be read: " + ex.Message, false);
            }
        }
    }

    /// <summary>The recipe tree is optional by design (it is not shipped), so "none installed" passes and says
    /// what that costs. When one is present, <see cref="MtkRecipeAudit.Verify"/> runs the real boot gate over
    /// it — parse, hash every blob, check both step counts — which is the only way to know before a phone.</summary>
    private static void Recipes(List<string> report, Action<string, string, bool> result)
    {
        IReadOnlyList<string> profiles;
        try
        {
            profiles = MtkNativeBench.DiscoverProfiles();
        }
        catch (Exception ex)
        {
            result("recipes", "mtk\\boot-profiles could not be listed: " + ex.Message, false);
            return;
        }

        if (profiles.Count == 0)
        {
            result("recipes", "none installed, so the NATIVE tab is absent by design (add mtk\\boot-profiles)", true);
            return;
        }

        IReadOnlyList<MtkRecipeCheck> checks = MtkRecipeAudit.Verify(profiles);
        bool ok = checks.All(c => c.Loaded);
        result("recipes", MtkRecipeAudit.VerifySummary(checks), ok);
        foreach (MtkRecipeCheck refused in checks.Where(c => !c.Loaded))
            result("recipe", MtkRecipeAudit.VerifyLine(refused), false);
    }

    private static void Optional(List<string> report, string root)
    {
        var present = new List<string>();
        var missing = new List<string>();
        foreach (var (name, places) in OptionalTools)
            (Locate(root, places).Length > 0 ? present : missing).Add(name);

        report.Add("INFO optional: " + (present.Count == 0 ? "none present" : string.Join(", ", present)) +
                   (missing.Count == 0 ? "" : "; missing " + string.Join(", ", missing) +
                       " - the QC/SPD tabs that need them say so when opened"));
    }

    private static string Locate(string root, string[] places)
    {
        foreach (string place in places)
        {
            string candidate = Path.Combine(root, place.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
        }
        return "";
    }

    private static string Summarize(IReadOnlyCollection<string> items) =>
        items.Count <= 5
            ? string.Join("; ", items)
            : string.Join("; ", items.Take(5)) + "; +" + (items.Count - 5) + " more";

    /// <summary>Stdout for a redirected run and always a file, because a WinExe started from Explorer has no
    /// console to print to and the operator who ran it needs the verdict somewhere.</summary>
    private static void Publish(List<string> report)
    {
        foreach (string line in report)
        {
            try { Console.Out.WriteLine(line); } catch { }
        }
        try
        {
            File.WriteAllLines(ShopServices.DataPath("pmk_selfcheck.log"), report);
        }
        catch { }
    }
}
