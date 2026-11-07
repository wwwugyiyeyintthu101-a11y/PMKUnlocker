using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PMKUnlocker.Spreadtrum;

/// <summary>FDL1 + FDL2 taken from one folder. Mixing halves from two chipsets bricks an SPD phone,
/// so nothing in here ever returns a one-sided pair.</summary>
public sealed class FdlPair
{
    public FdlPair(string folder, string fdl1, string fdl2)
    {
        Folder = folder;
        Fdl1 = fdl1;
        Fdl2 = fdl2;
    }

    public string Folder { get; }
    public string Fdl1 { get; }
    public string Fdl2 { get; }
}

public static class SpdLoaderPair
{
    public static bool IsFdl1(string path) =>
        Path.GetFileName(path).ToLowerInvariant().Contains("fdl1");

    public static bool IsFdl2(string path) =>
        Path.GetFileName(path).ToLowerInvariant().Contains("fdl2");

    /// <summary>A signed half is what secure-boot devices need, so it outranks the unsigned "-dl" copy.</summary>
    public static IEnumerable<string> RankedBinaries(string folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return Enumerable.Empty<string>();
        return Directory.GetFiles(folder, "*.bin")
            .OrderByDescending(f => Path.GetFileName(f).ToLowerInvariant().Contains("-sign"))
            .ThenBy(f => Path.GetFileName(f).ToLowerInvariant(), StringComparer.Ordinal);
    }

    /// <summary>Both halves from this one folder only — top level, so a stray file in a nested folder cannot
    /// complete the pair.</summary>
    public static FdlPair? FromFolder(string? folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;
        string? fdl1 = null, fdl2 = null;
        foreach (string file in RankedBinaries(folder))
        {
            if (fdl1 == null && IsFdl1(file)) fdl1 = file;
            else if (fdl2 == null && IsFdl2(file)) fdl2 = file;
            if (fdl1 != null && fdl2 != null) return new FdlPair(folder!, fdl1, fdl2);
        }
        return null;
    }

    /// <summary>The first folder that carries a complete pair. A folder holding only FDL1 is skipped
    /// instead of contributing a half.</summary>
    public static FdlPair? FirstFromFolders(IEnumerable<string>? folders)
    {
        if (folders == null) return null;
        foreach (string folder in folders)
        {
            FdlPair? pair = FromFolder(folder);
            if (pair != null) return pair;
        }
        return null;
    }

    public static bool SameFolder(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        string? da = Path.GetDirectoryName(a);
        string? db = Path.GetDirectoryName(b);
        if (string.IsNullOrEmpty(da) || string.IsNullOrEmpty(db)) return false;
        return string.Equals(Path.GetFullPath(da).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(db).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }
}
