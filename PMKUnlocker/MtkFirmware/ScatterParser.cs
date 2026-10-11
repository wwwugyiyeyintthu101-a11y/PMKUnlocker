using System.Globalization;

namespace PMKUnlocker.MtkFirmware;

internal static class ScatterParser
{
    private static readonly HashSet<string> KnownRegions = new(StringComparer.OrdinalIgnoreCase)
    {
        "EMMC_USER", "EMMC_BOOT1_BOOT2", "EMMC_BOOT1", "EMMC_BOOT2", "USERDATA", "EMMC_USERDATA"
    };

    private static readonly HashSet<string> KnownStorage = new(StringComparer.OrdinalIgnoreCase)
    {
        "HW_STORAGE_EMMC"
    };

    internal static FirmwarePackage Load(string scatterPath)
    {
        string full = Path.GetFullPath(scatterPath);
        var pkg = new FirmwarePackage
        {
            ScatterPath = full,
            Folder = Path.GetDirectoryName(full) ?? ""
        };

        string[] lines;
        try { lines = File.ReadAllLines(full); }
        catch (Exception ex)
        {
            pkg.FormatSupported = false;
            pkg.Issues.Add(new FirmwareIssue
            {
                Severity = IssueSeverity.Error, Code = "unreadable",
                Message = "Scatter file cannot be read: " + ex.Message
            });
            return pkg;
        }

        if (!lines.Any(l => l.TrimStart().StartsWith("- partition_index", StringComparison.Ordinal)))
        {
            pkg.FormatSupported = false;
            pkg.Issues.Add(new FirmwareIssue
            {
                Severity = IssueSeverity.Error, Code = "unsupported-format",
                Message = "Not a supported MTK scatter (no '- partition_index' blocks). Refusing to guess."
            });
            return pkg;
        }

        pkg.FormatSupported = true;

        var general = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var blocks = new List<Dictionary<string, string>>();
        Dictionary<string, string>? current = null;

        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
            if (line.StartsWith("- general", StringComparison.Ordinal))
            {
                current = null;
                general["__root"] = "1";
                continue;
            }
            if (line.StartsWith("- partition_index", StringComparison.Ordinal))
            {
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                blocks.Add(current);
                AddPair(current, line.Substring(1).Trim());
                continue;
            }
            if (current != null) AddPair(current, line.StartsWith("- ", StringComparison.Ordinal) ? line.Substring(2).Trim() : line);
            else AddPair(general, line.StartsWith("- ", StringComparison.Ordinal) ? line.Substring(2).Trim() : line);
        }

        pkg.Platform = Get(general, "platform");
        pkg.Project = Get(general, "project");
        pkg.ConfigVersion = Get(general, "config_version");
        pkg.Storage = Get(general, "storage");

        if (pkg.Platform == null || blocks.Count == 0)
        {
            pkg.FormatSupported = false;
            pkg.Issues.Add(new FirmwareIssue
            {
                Severity = IssueSeverity.Error, Code = "unsupported-format",
                Message = "Scatter header is incomplete (platform or partition list missing)."
            });
            return pkg;
        }

        if (pkg.Storage != null && !pkg.Storage.Equals("EMMC", StringComparison.OrdinalIgnoreCase) &&
            !pkg.Storage.Equals("UFS", StringComparison.OrdinalIgnoreCase))
        {
            pkg.Issues.Add(new FirmwareIssue
            {
                Severity = IssueSeverity.Error, Code = "unsupported-storage",
                Message = "Unsupported storage type: " + pkg.Storage
            });
        }

        foreach (var block in blocks)
            pkg.Entries.Add(BuildEntry(block, pkg));

        var byName = new Dictionary<string, ScatterEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in pkg.Entries)
        {
            if (e.Name.Length == 0)
            {
                pkg.Issues.Add(new FirmwareIssue
                {
                    Severity = IssueSeverity.Error, Code = "missing-name",
                    Message = "A partition block has no partition_name."
                });
                continue;
            }
            if (!byName.TryAdd(e.Name, e))
            {
                e.LoadError = "duplicate partition name";
                pkg.Issues.Add(new FirmwareIssue
                {
                    Severity = IssueSeverity.Error, Code = "duplicate-name", Partition = e.Name,
                    Message = "Duplicate partition name — second entry excluded."
                });
            }
        }

        foreach (var e in pkg.Entries)
        {
            if (!e.IsDownload || !e.HasFile) continue;
            if (!e.NumbersValid)
            {
                e.LoadError = "invalid start/size number";
                pkg.Issues.Add(new FirmwareIssue
                {
                    Severity = IssueSeverity.Error, Code = "invalid-number", Partition = e.Name,
                    Message = "linear_start_addr/partition_size is not a valid number."
                });
                continue;
            }
            if (e.Region == StorageRegion.Unsupported)
            {
                e.LoadError = "unsupported storage region";
                pkg.Issues.Add(new FirmwareIssue
                {
                    Severity = IssueSeverity.Error, Code = "unsupported-region", Partition = e.Name,
                    Message = "Unsupported storage region: " + (e.RegionRaw ?? "?")
                });
                continue;
            }
            ResolveImage(e, pkg);
        }

        DetectOverlaps(pkg);

        if (pkg.FlashableEntries.Count() == 0)
            pkg.Issues.Add(new FirmwareIssue
            {
                Severity = IssueSeverity.Error, Code = "no-flashable",
                Message = "No flashable partitions (is_download + file) in this scatter."
            });

        return pkg;
    }

    private static void AddPair(Dictionary<string, string> target, string line)
    {
        int idx = line.IndexOf(':');
        if (idx <= 0) return;
        string key = line.Substring(0, idx).Trim();
        string value = line.Substring(idx + 1).Trim();
        if (key.Length > 0) target[key] = value;
    }

    private static string? Get(Dictionary<string, string> map, string key) =>
        map.TryGetValue(key, out string? v) && v.Length > 0 ? v : null;

    private static bool TryNum(string? text, out long value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.Trim();
        try
        {
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                value = long.Parse(t.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            else
                value = long.Parse(t, NumberStyles.Integer, CultureInfo.InvariantCulture);
            return true;
        }
        catch { value = 0; return false; }
    }

    private static ScatterEntry BuildEntry(Dictionary<string, string> block, FirmwarePackage pkg)
    {
        string name = Get(block, "partition_name") ?? "";
        string? file = Get(block, "file_name");
        bool numbers = TryNum(Get(block, "linear_start_addr") ?? Get(block, "physical_start_addr"), out long start) &
                       TryNum(Get(block, "partition_size"), out long size);
        if (!numbers) { start = 0; size = 0; }
        bool.TryParse(Get(block, "is_download"), out bool isDownload);
        if (Get(block, "is_download") == null) isDownload = true;
        string? regionRaw = Get(block, "region");
        string? storageRaw = Get(block, "storage");

        if (regionRaw != null && !KnownRegions.Contains(regionRaw))
        {
            // region မသိရင် entry ကိုယ်တိုင် unsupported (entry-level error)
        }

        var region = regionRaw == null ? StorageRegion.Unknown
            : regionRaw.Equals("EMMC_USER", StringComparison.OrdinalIgnoreCase) ? StorageRegion.EmmcUser
            : regionRaw.StartsWith("EMMC_BOOT", StringComparison.OrdinalIgnoreCase) ? StorageRegion.EmmcBoot
            : regionRaw.IndexOf("USERDATA", StringComparison.OrdinalIgnoreCase) >= 0 ? StorageRegion.UserData
            : KnownRegions.Contains(regionRaw) ? StorageRegion.EmmcUser
            : StorageRegion.Unsupported;

        if (storageRaw != null && !KnownStorage.Contains(storageRaw) &&
            storageRaw.Equals("HW_STORAGE_NAND", StringComparison.OrdinalIgnoreCase))
            region = StorageRegion.Unsupported;

        return new ScatterEntry
        {
            Index = Get(block, "partition_index"),
            Name = name,
            FileNameRaw = file,
            Type = Get(block, "type"),
            OperationType = Get(block, "operation_type"),
            RegionRaw = regionRaw,
            StorageRaw = storageRaw,
            LinearStart = start,
            PhysicalStart = TryNum(Get(block, "physical_start_addr"), out long phys) ? phys : start,
            Size = size,
            NumbersValid = numbers,
            IsDownload = isDownload,
            Region = region
        };
    }

    private static void ResolveImage(ScatterEntry e, FirmwarePackage pkg)
    {
        try
        {
            string? resolved = ResolveInside(pkg.Folder, e.FileName);
            if (resolved == null)
            {
                e.LoadError = "path escapes firmware folder";
                pkg.Issues.Add(new FirmwareIssue
                {
                    Severity = IssueSeverity.Error, Code = "path-traversal", Partition = e.Name,
                    Message = "file_name resolves outside the firmware folder — blocked."
                });
                return;
            }
            e.ResolvedPath = resolved;
            e.ImageExists = File.Exists(resolved);
            if (!e.ImageExists)
            {
                pkg.Issues.Add(new FirmwareIssue
                {
                    Severity = IssueSeverity.Warning, Code = "missing-image", Partition = e.Name,
                    Message = "Image file not found: " + e.FileName
                });
                return;
            }
            e.EffectiveImageSize = ShopServices.ExpandedImageSize(resolved);
            if (e.EffectiveImageSize > e.Size)
            {
                pkg.Issues.Add(new FirmwareIssue
                {
                    Severity = IssueSeverity.Error, Code = "image-larger-than-partition", Partition = e.Name,
                    Message = "Image (expanded " + e.EffectiveImageSize + " bytes) exceeds partition size " + e.Size + " bytes."
                });
            }
        }
        catch (Exception ex)
        {
            e.ImageError = ex.Message;
            pkg.Issues.Add(new FirmwareIssue
            {
                Severity = IssueSeverity.Error, Code = "image-unreadable", Partition = e.Name,
                Message = "Cannot validate image: " + ex.Message
            });
        }
    }

    internal static string? ResolveInside(string folder, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        if (Path.IsPathRooted(relative)) return null;
        string combined;
        try { combined = Path.GetFullPath(Path.Combine(folder, relative)); }
        catch { return null; }
        string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return combined.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? combined : null;
    }

    private static void DetectOverlaps(FirmwarePackage pkg)
    {
        foreach (var group in pkg.Entries.Where(e => e.NumbersValid && e.Size > 0)
                     .GroupBy(e => e.RegionRaw ?? "?", StringComparer.OrdinalIgnoreCase))
        {
            var sorted = group.OrderBy(e => e.LinearStart).ThenBy(e => e.Size).ToList();
            long windowEnd = -1;
            ScatterEntry? windowOwner = null;
            foreach (var e in sorted)
            {
                if (windowOwner != null && e.LinearStart < windowEnd)
                {
                    bool bothFlashable = windowOwner.IsDownload && windowOwner.HasFile &&
                                         e.IsDownload && e.HasFile;
                    pkg.Issues.Add(new FirmwareIssue
                    {
                        Severity = bothFlashable ? IssueSeverity.Error : IssueSeverity.Warning,
                        Code = "address-overlap", Partition = e.Name,
                        Message = "Address overlap in " + group.Key + " with " + windowOwner.Name +
                                  " (" + e.StartHex + " < " + ("0x" + windowEnd.ToString("x")) + ")" +
                                  (bothFlashable ? "." : " (reserved/non-download entry — vendor layout).")
                    });
                    if (bothFlashable)
                    {
                        if (windowOwner.LoadError == null) windowOwner.LoadError = "address overlap with " + e.Name;
                        if (e.LoadError == null) e.LoadError = "address overlap with " + windowOwner.Name;
                    }
                }
                long end = e.LinearStart + e.Size;
                if (end > windowEnd) { windowEnd = end; windowOwner = e; }
            }
        }
    }
}
