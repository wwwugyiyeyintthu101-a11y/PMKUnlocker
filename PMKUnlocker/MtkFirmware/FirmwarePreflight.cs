using System.Text;

namespace PMKUnlocker.MtkFirmware;

internal sealed class DeviceProbeResult
{
    public bool Connected { get; init; }
    public string? Transport { get; init; }
    public string? Chipset { get; init; }
    public string? StorageType { get; init; }
    public long? CapacityBytes { get; init; }
    public string? SerialRedacted { get; init; }
}

internal static class FirmwarePreflight
{
    internal static PreflightReport Run(
        FirmwarePackage pkg,
        IReadOnlyCollection<string> selectedPartitions,
        DeviceProbeResult? probe,
        BackendCapabilities? caps)
    {
        var report = new PreflightReport();

        report.Checks.Add(CheckStructure(pkg));
        report.Checks.Add(CheckSelectedImages(pkg, selectedPartitions));
        report.Checks.Add(CheckChipset(pkg, probe));
        report.Checks.Add(CheckStorageAndCapacity(pkg, selectedPartitions, probe));
        report.Checks.Add(CheckLoader(pkg, selectedPartitions, probe));
        report.Checks.Add(CheckAuthorization(probe));
        report.Checks.Add(CheckBackend(caps));

        report.Overall = report.Checks
            .Select(c => c.State)
            .OrderBy(Worst)
            .Last();
        report.CanFlash = report.Overall != VerifyState.Mismatch;

        var sb = new StringBuilder();
        sb.Append("Preflight: ").Append(report.Overall switch
        {
            VerifyState.VerifiedCompatible => "verified (compatible)",
            VerifyState.Mismatch => "MISMATCH — flashing blocked",
            _ => "not fully verifiable"
        });
        if (report.Overall != VerifyState.Mismatch)
            sb.Append(". Platform/charger/cosmetic match alone is not a full compatibility proof.");
        string mismatched = string.Join(", ",
            report.Checks.Where(c => c.State == VerifyState.Mismatch).Select(c => c.Name));
        if (mismatched.Length > 0) sb.Append(" Blocked by: ").Append(mismatched).Append(".");
        report.SummaryNote = sb.ToString();

        return report;
    }

    private static int Worst(VerifyState s) => s switch
    {
        VerifyState.VerifiedCompatible => 0,
        VerifyState.UnableToVerify => 1,
        _ => 2
    };

    private static readonly string[] EntryLevelCodes =
        { "image-larger-than-partition", "image-unreadable", "missing-image", "no-flashable" };

    private static PreflightCheck CheckStructure(FirmwarePackage pkg)
    {
        if (!pkg.FormatSupported)
            return new PreflightCheck
            {
                Name = "Scatter structure",
                State = VerifyState.Mismatch,
                Detail = "Scatter format unsupported or unreadable."
            };
        var errors = pkg.Issues
            .Where(i => i.Severity == IssueSeverity.Error && !EntryLevelCodes.Contains(i.Code))
            .ToList();
        if (errors.Count > 0)
            return new PreflightCheck
            {
                Name = "Scatter structure",
                State = VerifyState.Mismatch,
                Detail = errors.Count + " structural error(s): " +
                         string.Join("; ", errors.Take(3).Select(i => i.ToString())) +
                         (errors.Count > 3 ? " …" : "")
            };
        if (pkg.FlashableEntries.Count() == 0)
            return new PreflightCheck
            {
                Name = "Scatter structure",
                State = VerifyState.Mismatch,
                Detail = "No flashable partitions."
            };
        return new PreflightCheck
        {
            Name = "Scatter structure",
            State = VerifyState.VerifiedCompatible,
            Detail = pkg.Entries.Count + " partitions, format MT6765-class scatter V" +
                     (pkg.ConfigVersion ?? "?") + ", no structural errors."
        };
    }

    private static PreflightCheck CheckSelectedImages(FirmwarePackage pkg, IReadOnlyCollection<string> selected)
    {
        if (selected.Count == 0)
            return new PreflightCheck
            {
                Name = "Selected image files",
                State = VerifyState.UnableToVerify,
                Detail = "No partitions selected."
            };
        var problems = new List<string>();
        foreach (string name in selected)
        {
            ScatterEntry? e = pkg.Find(name);
            if (e == null) { problems.Add(name + ": not in scatter"); continue; }
            if (e.LoadError != null) { problems.Add(name + ": " + e.LoadError); continue; }
            if (!e.ImageExists) { problems.Add(name + ": file missing (" + e.FileName + ")"); continue; }
            if (e.EffectiveImageSize == null) { problems.Add(name + ": image size not verified"); continue; }
            if (e.EffectiveImageSize > e.Size)
                problems.Add(name + ": image larger than partition");
        }
        if (problems.Count > 0)
            return new PreflightCheck
            {
                Name = "Selected image files",
                State = VerifyState.Mismatch,
                Detail = string.Join("; ", problems.Take(4)) + (problems.Count > 4 ? " …" : "")
            };
        return new PreflightCheck
        {
            Name = "Selected image files",
            State = VerifyState.VerifiedCompatible,
            Detail = selected.Count + " image file(s) present, inside package, fit their partitions."
        };
    }

    private static PreflightCheck CheckChipset(FirmwarePackage pkg, DeviceProbeResult? probe)
    {
        if (probe == null || !probe.Connected)
            return new PreflightCheck
            {
                Name = "Chipset platform",
                State = VerifyState.UnableToVerify,
                Detail = "Device not connected — cannot confirm chipset matches scatter platform (" +
                         (pkg.Platform ?? "?") + ")."
            };
        if (probe.Chipset == null)
            return new PreflightCheck
            {
                Name = "Chipset platform",
                State = VerifyState.UnableToVerify,
                Detail = "Device present (" + probe.Transport + ") but chipset id not reported."
            };
        bool match = pkg.Platform != null &&
                     probe.Chipset.Equals(pkg.Platform, StringComparison.OrdinalIgnoreCase);
        return new PreflightCheck
        {
            Name = "Chipset platform",
            State = match ? VerifyState.VerifiedCompatible : VerifyState.Mismatch,
            Detail = match
                ? probe.Chipset + " matches scatter platform (match alone is not a full compatibility proof)."
                : "Device " + probe.Chipset + " does not match scatter platform " + pkg.Platform + "."
        };
    }

    private static PreflightCheck CheckStorageAndCapacity(
        FirmwarePackage pkg, IReadOnlyCollection<string> selected, DeviceProbeResult? probe)
    {
        long need = selected
            .Select(n => pkg.Find(n))
            .Where(e => e != null && e.EffectiveImageSize != null)
            .Sum(e => e!.EffectiveImageSize!.Value);

        if (probe == null || !probe.Connected)
            return new PreflightCheck
            {
                Name = "Storage / capacity",
                State = VerifyState.UnableToVerify,
                Detail = "Device not connected — storage type and free capacity not verified (need ≈" +
                         FormatBytes(need) + " for selection)."
            };
        var problems = new List<string>();
        if (probe.StorageType != null && pkg.Storage != null &&
            !StorageMatches(pkg.Storage, probe.StorageType))
            problems.Add("storage " + probe.StorageType + " vs scatter " + pkg.Storage);
        if (probe.CapacityBytes is long cap && cap < need)
            problems.Add("need " + FormatBytes(need) + " > device " + FormatBytes(cap));
        if (problems.Count > 0)
            return new PreflightCheck
            {
                Name = "Storage / capacity",
                State = VerifyState.Mismatch,
                Detail = string.Join("; ", problems)
            };
        if (probe.StorageType == null || probe.CapacityBytes == null)
            return new PreflightCheck
            {
                Name = "Storage / capacity", State = VerifyState.UnableToVerify,
                Detail = "Live storage type or capacity is unavailable."
            };
        return new PreflightCheck
        {
            Name = "Storage / capacity",
            State = VerifyState.VerifiedCompatible,
            Detail = (probe.StorageType ?? "storage") + " " +
                     (probe.CapacityBytes is long c ? FormatBytes(c) : "capacity?") +
                     " fits selection (" + FormatBytes(need) + ")."
        };
    }

    private static PreflightCheck CheckLoader(
        FirmwarePackage pkg, IReadOnlyCollection<string> selected, DeviceProbeResult? probe)
    {
        ScatterEntry? pre = pkg.Find("preloader");
        bool chosen = selected.Any(s => s.Equals("preloader", StringComparison.OrdinalIgnoreCase));
        if (pre == null)
            return new PreflightCheck
            {
                Name = "Boot loader (preloader)",
                State = VerifyState.UnableToVerify,
                Detail = "Scatter has no preloader entry."
            };
        if (!chosen)
            return new PreflightCheck
            {
                Name = "Boot loader (preloader)",
                State = VerifyState.UnableToVerify,
                Detail = "Preloader not selected (risk default) — device keeps its installed bootloader."
            };
        if (pre.LoadError != null || !pre.ImageExists || pre.EffectiveImageSize == null)
            return new PreflightCheck
            {
                Name = "Boot loader (preloader)",
                State = VerifyState.Mismatch,
                Detail = pre.LoadError ?? (!pre.ImageExists
                    ? "preloader image missing"
                    : "preloader image not verified")
            };
        if (probe is { Connected: true, Chipset: not null } &&
            pkg.Platform != null &&
            !probe.Chipset.Equals(pkg.Platform, StringComparison.OrdinalIgnoreCase))
            return new PreflightCheck
            {
                Name = "Boot loader (preloader)",
                State = VerifyState.Mismatch,
                Detail = "Refusing preloader for mismatched chipset (" + probe.Chipset + ")."
            };
        return new PreflightCheck
        {
            Name = "Boot loader (preloader)",
            State = VerifyState.VerifiedCompatible,
            Detail = "Preloader image present and within partition bounds."
        };
    }

    private static PreflightCheck CheckAuthorization(DeviceProbeResult? probe)
    {
        return new PreflightCheck
        {
            Name = "Authorization / auth state",
            State = VerifyState.UnableToVerify,
            Detail = probe is { Connected: true }
                ? "Device on " + probe.Transport + " — MTK secure-boot auth state only known at connect time."
                : "Auth/secure-boot state cannot be verified before the device is connected."
        };
    }

    private static PreflightCheck CheckBackend(BackendCapabilities? caps)
    {
        if (caps == null)
            return new PreflightCheck
            {
                Name = "Flash backend",
                State = VerifyState.UnableToVerify,
                Detail = "No backend bound."
            };
        if (!caps.SupportsFlash)
            return new PreflightCheck
            {
                Name = "Flash backend",
                State = VerifyState.Mismatch,
                Detail = "Backend '" + caps.Name + "' cannot perform flash."
            };
        return new PreflightCheck
        {
            Name = "Flash backend",
            State = VerifyState.VerifiedCompatible,
            Detail = "Backend '" + caps.Name + "'" + (caps.IsMock ? " (MOCK — test only)" : "") +
                     (caps.SupportsFastbootPhase ? " supports BROM + fastboot flash." : " supports BROM/DA flash.")
        };
    }

    private static bool StorageMatches(string pkgStorage, string deviceStorage) =>
        pkgStorage.Equals(deviceStorage, StringComparison.OrdinalIgnoreCase) ||
        (pkgStorage.Equals("EMMC", StringComparison.OrdinalIgnoreCase) &&
         deviceStorage.Equals("eMMC", StringComparison.OrdinalIgnoreCase)) ||
        (pkgStorage.Equals("UFS", StringComparison.OrdinalIgnoreCase) &&
         deviceStorage.Equals("UFS", StringComparison.OrdinalIgnoreCase)) ||
        deviceStorage.IndexOf(pkgStorage, StringComparison.OrdinalIgnoreCase) >= 0;

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return (u == 0 ? v.ToString("0") : v.ToString("0.#")) + " " + units[u];
    }
}
