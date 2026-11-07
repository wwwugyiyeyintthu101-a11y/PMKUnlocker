#nullable enable
using System.Text.RegularExpressions;

namespace PMKUnlocker.MediaTek;

/// <summary>
/// ADB post-flash verification. After FLASH / Format All / NV restore the DA reboots the phone, and the only honest
/// way to say the operation did not leave it at the logo is to ask Android itself. This class is the hardware-free
/// half: it parses <c>adb shell getprop</c>, decides what the answer proves, masks the IMEI so a support log never
/// carries a full identity, and exposes a polling loop that takes the ADB call as a delegate — so every rule here is
/// assertable without a phone.
/// <para>
/// What it proves: the phone reached Android (<c>sys.boot_completed=1</c>) and which build it came up on.
/// What it does NOT prove: the FRP state (Android does not report it over ADB), or that the IMEI is intact when the
/// build hides <c>persist.radio.*</c> from a non-root shell — in that case the report says the property is missing
/// instead of pretending the identity was checked.
/// </para>
/// </summary>
internal static class MtkPostFlashCheck
{
    /// <summary>The single ADB call the whole check costs: one <c>getprop</c> dump, parsed on the PC.</summary>
    internal const string GetPropCommand = "shell getprop";

    // `adb shell getprop` prints one line per property: [ro.product.model]: [Redmi 9A]
    private static readonly Regex PropLine =
        new(@"^\[(?<key>[^\]]+)\]:\s?\[(?<value>.*)\]\s*$", RegexOptions.Compiled);

    /// <summary>Newest Android first: a dual-SIM build exposes imei1/imei2, an older one a single imei.</summary>
    internal static readonly string[] ImeiKeys =
        { "persist.radio.imei1", "persist.radio.imei", "persist.radio.imei2", "ril.gsm.imei" };

    /// <summary>
    /// The one label the report uses when none of <see cref="ImeiKeys"/> was returned. It is a label rather than
    /// the four keys joined by a pipe: the IMEI already has its own line, and the generic "not reported" line
    /// should read like a sentence, not like a property dump.
    /// </summary>
    internal const string ImeiMissingLabel = "IMEI (persist.radio.imei1 / imei / imei2, ril.gsm.imei)";

    private static readonly IReadOnlyDictionary<string, string> NoProps =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Parse a whole <c>getprop</c> dump. Lines that are not <c>[key]: [value]</c> — the adb daemon banner, an error
    /// on stderr that leaked into stdout — are skipped rather than treated as a property.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ParseGetProp(string? output)
    {
        var props = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(output)) return props;
        foreach (string raw in output.Split('\n'))
        {
            Match match = PropLine.Match(raw.TrimEnd('\r'));
            if (!match.Success) continue;
            props[match.Groups["key"].Value] = match.Groups["value"].Value;
        }
        return props;
    }

    internal sealed record BootEvidence(
        bool AdbAnswered,
        bool BootCompleted,
        bool DevBootCompleted,
        string Manufacturer,
        string Model,
        string Serial,
        string AndroidVersion,
        string BuildId,
        string VerifiedBootState,
        string FlashLocked,
        string Imei,
        string ImeiKey,
        IReadOnlyList<string> Missing,
        int Polls);

    /// <summary>How a fact reads in the log: a pass, something the operator must look at, or plain context.</summary>
    internal enum PostFlashLevel { Ok, Warn, Info }

    /// <summary>One row of the report. The panel prints these as one aligned block, not as twelve sentences.</summary>
    internal sealed record PostFlashFact(string Label, string Value, PostFlashLevel Level);

    /// <summary>
    /// Turn one property dump into a verdict. <paramref name="polls"/> is how many getprop calls it took, so the
    /// report can say "answered on poll 3" instead of implying the phone was up the whole time.
    /// </summary>
    internal static BootEvidence Read(IReadOnlyDictionary<string, string> props, bool adbAnswered = true, int polls = 1)
    {
        ArgumentNullException.ThrowIfNull(props);
        string Get(string key) => props.TryGetValue(key, out string? value) ? (value ?? "").Trim() : "";

        var missing = new List<string>();
        string Needed(string key)
        {
            string value = Get(key);
            if (value.Length == 0) missing.Add(key);
            return value;
        }

        string manufacturer = Needed("ro.product.manufacturer");
        string model = Needed("ro.product.model");
        string serial = Get("ro.serialno");
        if (serial.Length == 0) serial = Needed("ro.boot.serialno");
        string android = Needed("ro.build.version.release");
        string build = Needed("ro.build.id");
        string verifiedBoot = Needed("ro.boot.verifiedbootstate");
        string flashLocked = Needed("ro.boot.flash.locked");

        string imei = "";
        string imeiKey = "";
        foreach (string key in ImeiKeys)
        {
            string value = Get(key);
            if (value.Length == 0) continue;
            imei = value;
            imeiKey = key;
            break;
        }
        if (imei.Length == 0) missing.Add(ImeiMissingLabel);

        return new BootEvidence(
            adbAnswered,
            Get("sys.boot_completed") == "1",
            Get("dev.bootcomplete") == "1",
            manufacturer,
            model,
            serial,
            android,
            build,
            verifiedBoot,
            flashLocked,
            imei,
            imeiKey,
            missing,
            polls);
    }

    /// <summary>
    /// The operator-facing report as label/value rows. The panel prints them as one aligned block under a single
    /// header, so a run reads as one answer instead of twelve timestamped sentences. The IMEI is masked here, not
    /// at the call site, so no caller can accidentally log the full identity; the "cannot prove" row stays under
    /// the verdict even on success.
    /// </summary>
    internal static IReadOnlyList<PostFlashFact> Facts(BootEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var facts = new List<PostFlashFact>();
        if (!evidence.AdbAnswered)
        {
            facts.Add(new("Tried", evidence.Polls + " poll(s) — adb never answered", PostFlashLevel.Warn));
            facts.Add(new("Fix", "plug the phone in, unlock the screen, enable USB debugging, then run again",
                PostFlashLevel.Info));
            facts.Add(new("Note", "not a flash verdict — the DA readback already proved the bytes",
                PostFlashLevel.Info));
            return facts;
        }

        string boot;
        if (evidence.BootCompleted)
            boot = "sys.boot_completed=1 — Android finished booting (poll " + evidence.Polls + ")" +
                (evidence.DevBootCompleted ? "" : "; this build does not set dev.bootcomplete");
        else if (evidence.DevBootCompleted)
            boot = "dev.bootcomplete=1 but sys.boot_completed is not 1 — the framework is not up (poll " +
                evidence.Polls + ")";
        else
            boot = "sys.boot_completed is not 1 — still booting, or stuck at the logo (poll " + evidence.Polls + ")";
        facts.Add(new("Boot", boot, evidence.BootCompleted ? PostFlashLevel.Ok : PostFlashLevel.Warn));

        string device = string.Join(" ", new[] { evidence.Manufacturer, evidence.Model }.Where(s => s.Length > 0));
        if (device.Length > 0)
            facts.Add(new("Device", device +
                (evidence.AndroidVersion.Length > 0 ? " · Android " + evidence.AndroidVersion : "") +
                (evidence.BuildId.Length > 0 ? " · " + evidence.BuildId : ""), PostFlashLevel.Info));
        if (evidence.Serial.Length > 0)
            facts.Add(new("Serial", evidence.Serial, PostFlashLevel.Info));

        if (evidence.VerifiedBootState.Length > 0 || evidence.FlashLocked.Length > 0)
        {
            string state = evidence.VerifiedBootState.Length > 0 ? evidence.VerifiedBootState : "not reported";
            facts.Add(new("Verified", state +
                (string.Equals(evidence.VerifiedBootState, "orange", StringComparison.OrdinalIgnoreCase)
                    ? " — bootloader unlocked" : "") +
                " · flash.locked=" + (evidence.FlashLocked.Length > 0 ? evidence.FlashLocked : "not reported"),
                PostFlashLevel.Info));
        }

        if (evidence.Imei.Length > 0)
            facts.Add(new("IMEI", MaskImei(evidence.Imei) + " (from " + evidence.ImeiKey + ")", PostFlashLevel.Ok));
        else
            facts.Add(new("IMEI", "not readable — this build hides persist.radio.* from a non-root shell; check *#06#",
                PostFlashLevel.Warn));

        facts.Add(new("Note", "ADB proves the boot; it cannot prove the FRP state" +
            (evidence.Imei.Length > 0 ? " or that the IMEI survives the next wipe" : " or that the IMEI is intact"),
            PostFlashLevel.Info));
        return facts;
    }

    /// <summary>One word for the badge/summary: BOOTED, STILL BOOTING, or NO ADB.</summary>
    internal static string Verdict(BootEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (!evidence.AdbAnswered) return "NO ADB";
        return evidence.BootCompleted ? "BOOTED" : "STILL BOOTING";
    }

    /// <summary>Keep the last four digits — enough to compare with the dialer, not enough to be an identity leak.</summary>
    internal static string MaskImei(string? imei)
    {
        if (string.IsNullOrWhiteSpace(imei)) return "";
        string value = imei.Trim();
        if (value.Length <= 4) return new string('*', value.Length);
        return new string('*', value.Length - 4) + value[^4..];
    }

    /// <summary>
    /// Poll ADB until <c>sys.boot_completed=1</c> or the window runs out. The ADB call and the delay are delegates so
    /// a scripted run costs no real time and no phone; <paramref name="timeout"/> divided by
    /// <paramref name="interval"/> is the poll budget (capped at 1000 so a tiny interval cannot spin forever).
    /// A timeout inside ADB itself is a poll that answered nothing, not a cancelled run — only the caller's
    /// <paramref name="ct"/> aborts.
    /// </summary>
    internal static async Task<BootEvidence> WaitForBootAsync(
        Func<string, Task<string>> runAdb,
        TimeSpan timeout,
        TimeSpan interval,
        Func<TimeSpan, Task> delay,
        CancellationToken ct = default,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(runAdb);
        ArgumentNullException.ThrowIfNull(delay);
        if (timeout < TimeSpan.Zero) timeout = TimeSpan.Zero;
        if (interval <= TimeSpan.Zero) interval = TimeSpan.FromSeconds(1);
        int attempts = (int)Math.Min(1000,
            Math.Max(1, Math.Ceiling(timeout.TotalMilliseconds / interval.TotalMilliseconds)));

        BootEvidence evidence = Read(NoProps, adbAnswered: false, polls: 0);
        bool? lastAnswered = null;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            string output = "";
            bool answered = false;
            try
            {
                output = await runAdb(GetPropCommand).ConfigureAwait(false) ?? "";
                answered = !string.IsNullOrWhiteSpace(output);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                answered = false;   // adb's own timeout, not the operator's cancel
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or
                System.ComponentModel.Win32Exception or UnauthorizedAccessException)
            {
                answered = false;
            }

            evidence = Read(ParseGetProp(output), answered, attempt);
            if (evidence.BootCompleted) return evidence;
            if (attempt < attempts)
            {
                // One line at the start, one when the phone starts answering, and a heartbeat every 10 polls — a
                // three-minute wait must not fill the log with 45 copies of the same sentence.
                if (attempt == 1 || lastAnswered != answered || attempt % 10 == 0)
                    log?.Invoke("[*] POST-FLASH: poll " + attempt + "/" + attempts + " — " +
                        (answered ? "adb answered, still booting." : "adb has not answered yet."));
                lastAnswered = answered;
                await delay(interval).ConfigureAwait(false);
            }
        }
        return evidence;
    }
}
