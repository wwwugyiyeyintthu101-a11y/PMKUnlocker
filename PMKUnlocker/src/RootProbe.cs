using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace PMKUnlocker
{
    /// <summary>What the device says about root, without trusting one `su` binary.</summary>
    public enum RootVerdict
    {
        /// <summary>No manager, no root module, no root shell.</summary>
        None,

        /// <summary>A manager app is installed but nothing is loaded — a stock, unrooted kernel.</summary>
        ManagerOnly,

        /// <summary>A root module is live, so the kernel grants root to apps the manager allows.</summary>
        ModuleLoaded,

        /// <summary>`su -c id` from adb shell itself returned uid 0.</summary>
        ShellRoot,
    }

    /// <summary>
    /// The parsed answer to "is this phone rooted", kept free of ADB so it can be asserted offline.
    /// </summary>
    public sealed class RootProbeState
    {
        /// <summary>Friendly manager labels, e.g. "KernelSU".</summary>
        public List<string> Managers { get; } = new List<string>();

        /// <summary>Module lines as shown, e.g. "kernelsu (212992 bytes, Live)".</summary>
        public List<string> Modules { get; } = new List<string>();

        /// <summary>Bare module names, e.g. "kernelsu".</summary>
        public List<string> ModuleNames { get; } = new List<string>();

        /// <summary>`which su` result (or /data/local/tmp/su), when one exists.</summary>
        public string? SuPath { get; set; }

        /// <summary>uid parsed out of `su -c id`; null when the command never answered.</summary>
        public int? ShellUid { get; set; }

        public RootVerdict Verdict
        {
            get
            {
                if (ShellUid == 0) return RootVerdict.ShellRoot;
                if (Modules.Count > 0) return RootVerdict.ModuleLoaded;
                if (Managers.Count > 0) return RootVerdict.ManagerOnly;
                return RootVerdict.None;
            }
        }

        /// <summary>
        /// True when root exists on the device, even if this adb session cannot use it.
        /// KernelSU in LKM / jailbreak mode answers exactly this way (measured on garnet, Android 16).
        /// </summary>
        public bool HasRoot => Verdict == RootVerdict.ShellRoot || Verdict == RootVerdict.ModuleLoaded;

        /// <summary>One line for the device-details panel. Never says "no su" when a module is loaded.</summary>
        public string Summary
        {
            get
            {
                string who = string.Join(" + ", Managers);
                switch (Verdict)
                {
                    case RootVerdict.ShellRoot:
                        return "root via adb shell" + (string.IsNullOrWhiteSpace(SuPath) ? "" : " (su " + SuPath + ")");
                    case RootVerdict.ModuleLoaded:
                        return (who.Length > 0 ? who + " · " : "") + ModuleNames[0] +
                               " module loaded · no su on PATH (adb shell not authorized)";
                    case RootVerdict.ManagerOnly:
                        return (who.Length > 0 ? who : "root manager") +
                               " installed · no root module loaded — not rooted";
                    default:
                        return "not detected (no su, no root manager, no root module)";
                }
            }
        }

        /// <summary>The one sentence a user needs when root exists but this shell does not have it.</summary>
        public string? Hint =>
            Verdict == RootVerdict.ModuleLoaded
                ? "rooted — grant root to the app that needs it in the manager; adb shell stays unrooted"
                : null;
    }

    public static class RootProbe
    {
        /// <summary>Manager package -> label. Only packages that a manager really ships.</summary>
        public static readonly IReadOnlyDictionary<string, string> ManagerPackages =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "me.weishu.kernelsu", "KernelSU" },
                { "com.rifsxd.ksunext", "KernelSU Next" },
                { "com.sukisu.ultra", "SukiSU Ultra" },
                { "com.topjohnwu.magisk", "Magisk" },
                { "io.github.vvb2060.magisk", "Magisk Alpha" },
                { "me.bmax.apatch", "APatch" },
            };

        /// <summary>Module names in /proc/modules that mean a root provider is loaded.</summary>
        public static readonly string[] RootModuleNames =
            { "kernelsu", "kernelsu_next", "ksu", "apatch", "magisk" };

        private static readonly Regex UidPattern =
            new Regex(@"uid=(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>Reads `pm list packages` output. Unknown lines are ignored, duplicates collapse.</summary>
        public static List<string> ManagersFromPackages(string? text)
        {
            var found = new List<string>();
            foreach (string raw in (text ?? "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                string name = line.StartsWith("package:", StringComparison.Ordinal) ? line.Substring(8).Trim() : line;
                if (ManagerPackages.TryGetValue(name, out string? label) && !found.Contains(label))
                    found.Add(label);
            }
            return found;
        }

        /// <summary>Reads `cat /proc/modules` output as "name (bytes, state)" for root providers only.</summary>
        public static List<string> RootModules(string? text)
            => ParseModules(text).Select(entry => entry.Label).ToList();

        /// <summary>Parses `uid=0(root)` out of `id`. Error text yields null, never 0.</summary>
        public static int? UidFromId(string? text)
        {
            Match match = UidPattern.Match(text ?? "");
            return match.Success && int.TryParse(match.Groups[1].Value, out int uid) ? uid : null;
        }

        /// <summary>Combines the four probes into one verdict.</summary>
        public static RootProbeState Evaluate(string? packages, string? modules, string? suPath, string? idOutput)
        {
            var state = new RootProbeState
            {
                SuPath = string.IsNullOrWhiteSpace(suPath) ? null : suPath!.Trim(),
                ShellUid = UidFromId(idOutput),
            };
            state.Managers.AddRange(ManagersFromPackages(packages));
            foreach (var entry in ParseModules(modules))
            {
                state.ModuleNames.Add(entry.Name);
                state.Modules.Add(entry.Label);
            }
            return state;
        }

        private static List<(string Name, string Label)> ParseModules(string? text)
        {
            var found = new List<(string Name, string Label)>();
            foreach (string raw in (text ?? "").Split('\n'))
            {
                string[] parts = raw.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;
                string name = parts[0];
                if (!RootModuleNames.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                // name size refcount deps state address taint
                string size = parts.Length > 1 ? parts[1] : "?";
                string moduleState = parts.Length > 4 ? parts[4] : "?";
                string label = name + " (" + size + " bytes, " + moduleState + ")";
                if (!found.Any(entry => entry.Label == label)) found.Add((name, label));
            }
            return found;
        }
    }
}
