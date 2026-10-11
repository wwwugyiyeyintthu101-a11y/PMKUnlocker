namespace PMKUnlocker.MtkFirmware;

internal enum StorageRegion { Unknown, EmmcUser, EmmcBoot, UserData, Unsupported }

internal enum IssueSeverity { Info, Warning, Error }

internal enum RiskLevel { Normal, HighRisk }

internal enum VerifyState { UnableToVerify, VerifiedCompatible, Mismatch }

internal sealed class FirmwareIssue
{
    public IssueSeverity Severity { get; init; }
    public string Code { get; init; } = "";
    public string? Partition { get; init; }
    public string Message { get; init; } = "";
    public override string ToString() =>
        (Severity == IssueSeverity.Error ? "[x] " : Severity == IssueSeverity.Warning ? "[!] " : "[i] ") +
        (Partition != null ? Partition + ": " : "") + Message;
}

internal sealed class ScatterEntry
{
    public string? Index { get; init; }
    public string Name { get; init; } = "";
    public string? FileNameRaw { get; init; }
    public string? Type { get; init; }
    public string? OperationType { get; init; }
    public string? RegionRaw { get; init; }
    public string? StorageRaw { get; init; }
    public long LinearStart { get; init; }
    public long PhysicalStart { get; init; }
    public long Size { get; init; }
    public bool NumbersValid { get; init; }
    public bool IsDownload { get; init; } = true;
    public StorageRegion Region { get; init; } = StorageRegion.Unknown;

    public string FileName => string.IsNullOrWhiteSpace(FileNameRaw) || FileNameRaw.Equals("NONE", StringComparison.OrdinalIgnoreCase)
        ? "" : FileNameRaw;
    public bool HasFile => FileName.Length > 0;
    public long End => NumbersValid ? LinearStart + Size : 0;

    public string? ResolvedPath { get; set; }
    public bool ImageExists { get; set; }
    public long? EffectiveImageSize { get; set; }
    public string? ImageError { get; set; }
    public string? LoadError { get; set; }

    public bool Selectable => IsDownload && HasFile && NumbersValid && Size > 0 &&
        Name.Length > 0 && Region != StorageRegion.Unsupported && LoadError == null;

    public bool BromRegionSupported => string.Equals(RegionRaw, "EMMC_USER", StringComparison.OrdinalIgnoreCase) ||
        (Name.Equals("preloader", StringComparison.OrdinalIgnoreCase) && PhysicalStart == 0 &&
         new[] { "EMMC_BOOT1", "EMMC_BOOT2", "EMMC_BOOT1_BOOT2" }
             .Contains(RegionRaw, StringComparer.OrdinalIgnoreCase));

    public string StartHex => "0x" + LinearStart.ToString("x");
    public string SizeHex => "0x" + Size.ToString("x");
}

internal sealed class FirmwarePackage
{
    public string ScatterPath { get; init; } = "";
    public string Folder { get; init; } = "";
    public string? Platform { get; set; }
    public string? Project { get; set; }
    public string? ConfigVersion { get; set; }
    public string? Storage { get; set; }
    public List<ScatterEntry> Entries { get; } = new();
    public List<FirmwareIssue> Issues { get; } = new();
    public bool FormatSupported { get; set; }

    public IEnumerable<ScatterEntry> FlashableEntries => Entries.Where(e => e.Selectable);

    public bool HasBlockingErrors =>
        !FormatSupported ||
        FlashableEntries.Count() == 0 ||
        Issues.Any(i => i.Severity == IssueSeverity.Error);

    public bool HasErrorsFor(IEnumerable<string> partitionNames)
    {
        var set = new HashSet<string>(partitionNames, StringComparer.OrdinalIgnoreCase);
        return Issues.Any(i => i.Severity == IssueSeverity.Error && i.Partition != null && set.Contains(i.Partition));
    }

    public ScatterEntry? Find(string name) =>
        Entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

internal static class FlashRisk
{
    private static readonly string[] TableParts = { "pgpt", "sgpt", "gpt", "flashinfo", "otp" };
    private static readonly string[] DataParts = { "userdata", "metadata" };
    private static readonly string[] CalibrationParts =
    {
        "nvram", "nvdata", "nvcfg", "protect1", "protect2", "proinfo",
        "expdb", "secro", "seccfg", "frp", "persistent", "metadata" 
    };
    private static readonly string[] BootloaderParts = { "preloader", "lk", "lk2" };

    internal static RiskLevel Classify(ScatterEntry entry)
    {
        if (entry.Region == StorageRegion.UserData) return RiskLevel.HighRisk;
        string n = entry.Name.ToLowerInvariant();
        if (TableParts.Contains(n)) return RiskLevel.HighRisk;
        if (DataParts.Contains(n)) return RiskLevel.HighRisk;
        if (CalibrationParts.Contains(n)) return RiskLevel.HighRisk;
        if (BootloaderParts.Contains(n)) return RiskLevel.HighRisk;
        if (entry.OperationType != null &&
            entry.OperationType.Equals("PROTECTED", StringComparison.OrdinalIgnoreCase))
            return RiskLevel.HighRisk;
        return RiskLevel.Normal;
    }

    internal static string? Reason(ScatterEntry entry)
    {
        if (Classify(entry) == RiskLevel.Normal) return null;
        string n = entry.Name.ToLowerInvariant();
        if (TableParts.Contains(n)) return "partition table / device layout area";
        if (DataParts.Contains(n) || entry.Region == StorageRegion.UserData) return "user data erasure";
        if (BootloaderParts.Contains(n)) return "bootloader — wrong image can prevent boot";
        if (CalibrationParts.Contains(n)) return "device calibration / security data — damage can break IMEI/Wi-Fi/DRM";
        if (entry.OperationType != null && entry.OperationType.Equals("PROTECTED", StringComparison.OrdinalIgnoreCase))
            return "protected area";
        return "high-risk area";
    }

    internal static bool DefaultSelected(ScatterEntry entry) =>
        entry.IsDownload && entry.HasFile && entry.NumbersValid && Classify(entry) == RiskLevel.Normal;
}

internal sealed class PreflightCheck
{
    public string Name { get; init; } = "";
    public VerifyState State { get; init; }
    public string Detail { get; init; } = "";
    public string Marker => State == VerifyState.VerifiedCompatible ? "[OK]"
        : State == VerifyState.Mismatch ? "[X]" : "[??]";
}

internal sealed class PreflightReport
{
    public List<PreflightCheck> Checks { get; } = new();
    public VerifyState Overall { get; set; } = VerifyState.UnableToVerify;
    public bool CanFlash { get; set; }
    public string SummaryNote { get; set; } = "";
}
