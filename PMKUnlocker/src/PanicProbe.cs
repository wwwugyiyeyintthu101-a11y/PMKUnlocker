using System;
using System.Collections.Generic;
using System.Linq;

namespace PMKUnlocker
{
    /// <summary>What the last boot looks like once ADB is back - no phone access, pure parsing.</summary>
    public enum BootVerdict
    {
        /// <summary>Nothing readable: no pstore dump and no boot reason.</summary>
        Unknown,

        /// <summary>Root is live on this boot - the run is over, whatever happened before it.</summary>
        Rooted,

        /// <summary>The phone came back on its own and is healthy - the exploit failed but did not crash it.</summary>
        FailedAlive,

        /// <summary>The last boot ended in a kernel panic - pstore dump, panic text or a panic boot reason.</summary>
        Panic,
    }

    /// <summary>
    /// The answer to "did the exploit crash the kernel, or did the phone just come back?".
    /// Kept free of ADB so every branch is assertable without a phone.
    /// </summary>
    public sealed class BootReport
    {
        public BootVerdict Verdict { get; set; } = BootVerdict.Unknown;

        /// <summary>Lower-cased `ro.boot.bootreason` (or `sys.boot.reason`); empty when not reported.</summary>
        public string BootReason { get; set; } = "";

        /// <summary>Crash dump names under /sys/fs/pstore, e.g. "console-ramoops-0".</summary>
        public List<string> CrashFiles { get; } = new List<string>();

        /// <summary>The first panic line found in the console dump, trimmed to one log line.</summary>
        public string? PanicLine { get; set; }

        /// <summary>One sentence for the log. Never claims a clean boot when the evidence is missing.</summary>
        public string Summary
        {
            get
            {
                string why = BootReason.Length > 0 ? BootReason : "unknown";
                switch (Verdict)
                {
                    case BootVerdict.Rooted:
                        return "root is live on this boot (boot reason: " + why + ")";
                    case BootVerdict.Panic:
                        string dump = CrashFiles.Count > 0 ? ", pstore: " + string.Join(",", CrashFiles) : "";
                        return "last boot ended in a kernel panic (boot reason: " + why + dump + ")";
                    case BootVerdict.FailedAlive:
                        return "phone is alive after a failed run with no root (boot reason: " + why + ")";
                    default:
                        return "boot outcome unknown (no pstore dump, boot reason not reported)";
                }
            }
        }

        /// <summary>What the user should do next; null only when there is nothing useful to add.</summary>
        public string? RecoveryNote
        {
            get
            {
                switch (Verdict)
                {
                    case BootVerdict.Rooted:
                        return "do not reboot and do not run the exploit again";
                    case BootVerdict.Panic:
                        return "the exploit most likely crashed the kernel - let the phone settle, then retry once; " +
                               "a second panic means this profile does not fit this phone";
                    case BootVerdict.FailedAlive:
                        return "the phone stayed up, so one retry with the same profile is safe";
                    default:
                        return "read /sys/fs/pstore and ro.boot.bootreason by hand before retrying";
                }
            }
        }
    }

    public static class PanicProbe
    {
        /// <summary>pstore entries that only exist when a boot wrote a crash dump.</summary>
        public static readonly string[] CrashFilePrefixes =
        {
            "console-ramoops", "dmesg-ramoops", "pmsg-ramoops", "ftrace-ramoops", "misc-ramoops",
            "console-rtc", "pmsg-rtc", "last_kmsg",
        };

        /// <summary>Lines a real kernel panic writes; a WARN_ON splat alone does not match.</summary>
        private static readonly string[] PanicMarkers =
        {
            "kernel panic",
            "panic - not syncing",
            "not syncing:",
            "end kernel panic",
            "panic_on_oops",
            "unable to handle kernel",
            "bug: unable to handle",
            "internal error: oops",
            "fatal exception",
            "watchdog bite",
            "hard lockup",
            "soft lockup",
            "sysrq : trigger a crash",
        };

        /// <summary>Boot reasons that mean the last boot died instead of ending normally.</summary>
        private static readonly string[] PanicReasons =
        {
            "panic", "kernel_panic", "watchdog", "oops", "fatal", "crash", "ramdump",
        };

        /// <summary>
        /// Reads `ls /sys/fs/pstore` (plain or long format) and keeps the crash dump names.
        /// Error text, "total 0" and "." entries never become dump names.
        /// </summary>
        public static List<string> PstoreCrashFiles(string? listing)
        {
            var found = new List<string>();
            foreach (string raw in (listing ?? "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;
                string name = parts[parts.Length - 1].TrimEnd('/');
                int slash = name.LastIndexOf('/');
                if (slash >= 0) name = name.Substring(slash + 1);
                if (name.Length == 0 || name == "." || name == ".." || name == "total") continue;
                if (CrashFilePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)) &&
                    !found.Contains(name))
                    found.Add(name);
            }
            return found;
        }

        /// <summary>The first panic line in a console dump, one line long; null when the dump is clean or empty.</summary>
        public static string? FirstPanicLine(string? consoleText)
        {
            foreach (string raw in (consoleText ?? "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (!PanicMarkers.Any(marker => line.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0))
                    continue;
                return line.Length <= 200 ? line : line.Substring(0, 197) + "...";
            }
            return null;
        }

        /// <summary>True only when the console dump carries a real panic marker.</summary>
        public static bool LooksLikePanic(string? consoleText) => FirstPanicLine(consoleText) != null;

        /// <summary>Stable lowercase key for the ledger; empty for "no information".</summary>
        public static string Key(BootVerdict verdict)
        {
            switch (verdict)
            {
                case BootVerdict.Rooted: return "rooted";
                case BootVerdict.Panic: return "panic";
                case BootVerdict.FailedAlive: return "failed-alive";
                default: return "";
            }
        }

        /// <summary>
        /// Parses `ro.boot.bootreason`. "0" is the normal value several vendors write, "unknown"
        /// is no answer at all, and soft/hard reset reasons are NOT panics.
        /// </summary>
        public static string NormalizeBootReason(string? bootReason)
        {
            string reason = (bootReason ?? "").Trim().ToLowerInvariant();
            if (reason == "0") return "normal";
            if (reason == "unknown" || reason == "null" || reason == "-") return "";
            return reason;
        }

        public static bool BootReasonIsPanic(string? bootReason)
        {
            string reason = NormalizeBootReason(bootReason);
            return reason.Length > 0 && PanicReasons.Any(token => reason.IndexOf(token, StringComparison.Ordinal) >= 0);
        }

        /// <summary>
        /// Turns the three cheap probes into one verdict. Root wins over everything: a live root
        /// means the run is over even if an older panic dump is still lying in pstore.
        /// </summary>
        public static BootReport Classify(string? pstoreListing, string? consoleText, string? bootReason, bool hasRoot)
        {
            var report = new BootReport
            {
                BootReason = NormalizeBootReason(bootReason),
                PanicLine = FirstPanicLine(consoleText),
            };
            report.CrashFiles.AddRange(PstoreCrashFiles(pstoreListing));

            if (hasRoot)
                report.Verdict = BootVerdict.Rooted;
            else if (report.PanicLine != null || BootReasonIsPanic(report.BootReason))
                report.Verdict = BootVerdict.Panic;
            else if (report.CrashFiles.Count > 0)
                // A dump without readable console text is still a crash dump; say "panic" but the
                // Summary and the missing PanicLine show the weaker evidence.
                report.Verdict = BootVerdict.Panic;
            else if (report.BootReason.Length > 0)
                report.Verdict = BootVerdict.FailedAlive;
            else
                report.Verdict = BootVerdict.Unknown;

            return report;
        }
    }
}
