namespace PMKUnlocker;

/// <summary>
/// The decisions behind "install an APK on a phone that fights back", kept out of the adb call so they can be
/// asserted without a device.
/// <para>
/// The install turns two <c>settings</c> keys off because a MIUI/HyperOS build refuses a streaming
/// <c>adb install</c> while its verifier is on, and it must put the phone back exactly as it found it. The two
/// ways that restore goes wrong are silent: writing the literal string <c>null</c> over a key that was never set
/// (the matching <c>settings get</c> answer for "unset"), and echoing a value the device handed us straight into
/// a shell command. Both are decided here, where a test can see them.
/// </para>
/// </summary>
internal static class ApkInstallPolicy
{
    /// <summary>The keys the install turns off for its own duration. Verbatim <c>settings</c> names.</summary>
    internal static readonly string[] VerifierKeys = { "verifier_verify_adb_installs", "package_verifier_enable" };

    /// <summary>
    /// What <c>adb shell settings get global &lt;key&gt;</c> answered, as the value to put back. adb hands back
    /// the shell's whole output — a daemon banner, a CRLF, a trailing blank line — so the last non-empty line is
    /// the answer, and the shell's own "null" means the key was never set (empty string here).
    /// </summary>
    internal static string ParseSettingValue(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        string[] lines = raw.Replace("\r", "").Split('\n');
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            string value = lines[i].Trim();
            if (value.Length == 0) continue;
            return value.Equals("null", StringComparison.OrdinalIgnoreCase) ? "" : value;
        }
        return "";
    }

    /// <summary>
    /// The command that puts the key back: the value it had, or a delete when it had none. A value that carries
    /// anything a shell would interpret is not written back either — deleting returns the key to the platform
    /// default instead of injecting what a ROM chose to print.
    /// </summary>
    internal static string RestoreCommand(string key, string previous)
    {
        if (string.IsNullOrEmpty(previous) || !IsRestorableValue(previous))
            return "shell settings delete global " + key;
        return "shell settings put global " + key + " " + previous;
    }

    /// <summary>
    /// Whether a value read from the phone may be echoed into a shell command. Real answers are short tokens
    /// ("0", "1", "en-US"); anything with whitespace, a quote, a separator or a redirect is not something this
    /// code wrote and is not something it will pass through.
    /// </summary>
    internal static bool IsRestorableValue(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64) return false;
        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-' || c == ':' || c == '/' || c == '@')
                continue;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Where the fallback copies the APK before <c>pm install</c>. The name is the local file name only: the push
    /// target is a shell word, so a path with spaces or quotes must never reach it whole, and every staged file is
    /// removed on the way out.
    /// </summary>
    internal static string RemoteStagingPath(string localPath) =>
        "/data/local/tmp/pmk_" + Path.GetFileName(localPath ?? "");
}
