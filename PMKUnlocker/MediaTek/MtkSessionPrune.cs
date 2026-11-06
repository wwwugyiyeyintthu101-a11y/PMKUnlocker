namespace PMKUnlocker.MediaTek;

/// <summary>
/// The mtkclient adapter deletes its session folder after a successful run, so the folders that stay behind are
/// the failures and the crashes — and the staged `.bin` dumps inside them are what once grew this tree to 52 GB.
/// This prune keeps only the newest few for post-mortem and deletes the rest at startup, before any engine
/// exists (so no live session can be in one).
///
/// Only GUID-named folders are candidates. <c>seccfg-backup</c>, its <c>archive-20261013</c> child, the
/// <c>native-da</c> bench folder and every future hand-named folder are not GUIDs, so they are untouchable by
/// construction — a blocklist here would go stale the day a new protected name lands.
/// </summary>
internal static class MtkSessionPrune
{
    internal const int KeepNewest = 15;

    internal static string SessionsRoot(string dataDirectory) =>
        Path.Combine(dataDirectory, "MediaTekSessions");

    internal static (int Pruned, long BytesFreed) Prune(string dataDirectory, int keep = KeepNewest)
    {
        string root = SessionsRoot(dataDirectory);
        if (keep < 0 || !Directory.Exists(root)) return (0, 0);
        var stale = Directory.EnumerateDirectories(root)
            .Where(d => Guid.TryParse(Path.GetFileName(d), out _))
            .Select(d => new DirectoryInfo(d))
            .OrderByDescending(d => d.LastWriteTimeUtc)
            .Skip(keep)
            .ToList();
        int pruned = 0;
        long bytes = 0;
        foreach (DirectoryInfo dir in stale)
        {
            try
            {
                foreach (FileInfo f in dir.EnumerateFiles("*", SearchOption.AllDirectories))
                    bytes += f.Length;
            }
            catch { }
            // A file locked by a still-running child keeps its folder; the next startup retries it.
            try { dir.Delete(true); pruned++; } catch { }
        }
        return (pruned, bytes);
    }
}
