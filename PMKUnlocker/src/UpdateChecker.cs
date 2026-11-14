using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace PMKUnlocker;

internal sealed class UpdateInfo
{
    public string Version { get; set; } = "";
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Notes { get; set; } = "";
}

internal enum GatePhase { Checking, CheckFailed, Downloading, Verified, Failed }

// GitHub Releases auto-update — login gate (update မလုပ်ဘဲ အထဲမဝင်ရ)
//   release source: DefaultRepo (build-time constant — no user-writable override)
//   Release တစ်ခုမှာ asset ၂ ခု တင်ရမယ်:
//     - PMKMobileTool-<ver>-Setup.exe
//     - update.json  = { "version": "7.3.0", "url": "...Setup.exe", "sha256": "<hex>", "notes": "..." }
internal static class UpdateChecker
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    internal const string DefaultRepo = "wwwugyiyeyintthu101-a11y/PMKUnlocker";

    // login stage မှာ စတင်တဲ့ check — gate က ဒါကိုပဲ စောင့်ယူ (HTTP call တစ်ခါပဲ)
    internal static Task<UpdateInfo?>? PendingCheck;

    // login gate — update ရှိရင် UpdateInfo; နောက်ဆုံး version → null (ဝင်ခွင့်ပေး)
    // check fail / timeout / offline → exception (caller က strict block)
    internal static async Task<UpdateInfo?> AwaitCheckForGateAsync(int maxWaitMs)
    {
        Task<UpdateInfo?>? t = PendingCheck;
        if (t == null) t = CheckAsync();
        PendingCheck = null;
        if (await Task.WhenAny(t, Task.Delay(maxWaitMs)) != t)
            throw new TimeoutException("update check timed out (" + (maxWaitMs / 1000) + "s)");
        return await t;   // fault (offline / HTTP error) → propagates
    }

    // Update gate — login form နဲ့ remember-me auto-login နှစ်လမ်းစလုံး ဒီကနေ ဖြတ်တယ်
    // update ရှိ → update လုပ်ရမယ် (No နှိပ်ရင် app ပိတ်); check fail/offline → continue (warning);
    // နောက်ဆုံး version ဖြစ်လျှင် → true (ဝင်ခွင့်ပေး)
    internal static async Task<bool> EnforceGateAsync(IWin32Window? owner,
        Action<GatePhase, string>? onStatus = null, Action<int>? onProgress = null)
    {
        UpdateInfo? info;
        try
        {
            onStatus?.Invoke(GatePhase.Checking, "Checking for updates...");
            info = await AwaitCheckForGateAsync(25000);
        }
        catch (Exception ex)
        {
            // offline / GitHub timeout — update check မအောင်လို့ tool ကို မပိတ်တော့
            // (customer network မကောင်းရင် အသုံးမပြုနိုင်တာ ကို ရှောင်)
            onStatus?.Invoke(GatePhase.CheckFailed, "Update check failed (offline?) — continuing");
            Ask(owner,
                "Update check failed:\n" + ex.Message + "\n\nContinuing without update verification.",
                "Update check", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return true;
        }
        if (info == null) return true;   // နောက်ဆုံး version

        DialogResult r = Ask(owner,
            "A new version (v" + info.Version + ") is required.  (current: v" + CurrentVersion + ")\n\n" +
            (string.IsNullOrWhiteSpace(info.Notes) ? "" : info.Notes + "\n\n") +
            "Download and install now?\nClick No to exit the tool.",
            "Update required", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
        if (r != DialogResult.Yes)
        {
            Environment.Exit(0);
            return false;
        }

        try
        {
            onProgress?.Invoke(0);
            onStatus?.Invoke(GatePhase.Downloading, "Downloading update... 0%");
            string setup = await DownloadAsync(info, pct =>
            {
                onProgress?.Invoke(pct);
                onStatus?.Invoke(GatePhase.Downloading, "Downloading update... " + pct + "%");
            });
            onProgress?.Invoke(100);
            onStatus?.Invoke(GatePhase.Verified, "SHA-256 verified — installing update...");
            StartInstallAndExit(setup, info.Sha256.Trim());
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            onStatus?.Invoke(GatePhase.Failed, "Update download failed");
            Ask(owner,
                "Update download failed:\n" + ex.Message + "\n\nThe tool will close — relaunch to try again.",
                "Update required", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Environment.Exit(0);
        }
        return false;
    }

    private static DialogResult Ask(IWin32Window? owner, string text, string caption,
        MessageBoxButtons buttons, MessageBoxIcon icon) =>
        owner != null
            ? MessageBox.Show(owner, text, caption, buttons, icon)
            : MessageBox.Show(text, caption, buttons, icon);

    // Pinned at build time — a file in the user profile must not redirect updates to another repo.
    internal static string Repo => DefaultRepo;

    internal static Version CurrentVersion
    {
        get
        {
            Version v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(Math.Max(v.Major, 0), Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
        }
    }

    private static string ManifestUrl(string repo)
    {
        repo = repo.Trim().TrimEnd('/');
        if (!repo.Contains("://", StringComparison.Ordinal)) repo = "https://github.com/" + repo;
        if (!Uri.TryCreate(repo, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.Host != "github.com" || uri.UserInfo.Length != 0 || !uri.IsDefaultPort ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath, @"^/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$"))
            throw new InvalidDataException("Update source must be an HTTPS GitHub owner/repository URL.");
        return repo + "/releases/latest/download/update.json";
    }

    private static bool IsHex64(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length != 64) return false;
        foreach (char c in s)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return false;
        return true;
    }

    // အသစ်ရှိရင် UpdateInfo, မရှိ/မပြင်ဆင်ထား/offline → null (manifest ပျက်ရင် silent skip)
    internal static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        string repo = Repo;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        string json = "";
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                json = await http.GetStringAsync(ManifestUrl(repo), ct);
                break;
            }
            catch (Exception ex) when (attempt < 3 &&
                (ex is HttpRequestException || ex is TaskCanceledException))
            {
                // transient GitHub hiccup (release upload ပြီးချင်း 500 / offline blip) — retry 2 ကြိမ်
                await Task.Delay(1500, ct);
            }
        }
        if (json.Length > 0 && json[0] == '\uFEFF') json = json.Substring(1);   // BOM (UTF-8-encoded manifest)
        UpdateInfo? info = JsonSerializer.Deserialize<UpdateInfo>(json, JsonOpts);
        if (info == null) throw new InvalidDataException("Empty update manifest.");

        string ver = (info.Version ?? "").Trim().TrimStart('v', 'V');
        if (!Version.TryParse(ver, out Version? v)) throw new InvalidDataException("Invalid update version.");
        if (string.IsNullOrWhiteSpace(info.Url) ||
            !Uri.TryCreate(info.Url.Trim(), UriKind.Absolute, out Uri? u) || u.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Installer URL must use HTTPS.");
        string repoUrl = ManifestUrl(repo).Replace("/releases/latest/download/update.json", "", StringComparison.Ordinal);
        if (!info.Url.StartsWith(repoUrl + "/releases/download/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Installer must belong to the configured GitHub repository.");
        if (!IsHex64((info.Sha256 ?? "").Trim())) throw new InvalidDataException("Invalid installer checksum.");

        return v > CurrentVersion ? info : null;
    }

    // download → SHA-256 verify (fail ရင် ဖိုင်ဖျက်ပြီး exception); ပြန်တဲ့ path = verified setup file
    internal static async Task<string> DownloadAsync(UpdateInfo info, Action<int> onPercent, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(info.Url, UriKind.Absolute, out var downloadUri) || downloadUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Installer download requires HTTPS.");
        string dir = Path.Combine(ShopServices.DataDirectory, "Updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "PMKUpdate-Setup.exe");

        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) })
        using (var resp = await http.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            if (resp.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("Insecure installer redirect rejected.");
            long? total = resp.Content.Headers.ContentLength;
            using Stream input = await resp.Content.ReadAsStreamAsync(ct);
            using var output = File.Create(file);
            byte[] buf = new byte[81920];
            long done = 0;
            int lastReported = 0;
            int read;
            while ((read = await input.ReadAsync(buf, ct)) > 0)
            {
                await output.WriteAsync(buf.AsMemory(0, read), ct);
                done += read;
                if (total > 0)
                {
                    int pct = (int)(done * 100 / total.Value);
                    if (pct > lastReported)
                    {
                        lastReported = pct;
                        onPercent?.Invoke(pct);
                    }
                }
            }
        }

        byte[] actual;
        using (var fs = File.OpenRead(file))
            actual = await SHA256.HashDataAsync(fs, ct);

        byte[] want = Convert.FromHexString((info.Sha256 ?? "").Trim().ToLowerInvariant());
        if (!CryptographicOperations.FixedTimeEquals(actual, want))
        {
            try { File.Delete(file); } catch { }
            throw new InvalidDataException("SHA-256 mismatch — downloaded installer rejected.");
        }
        return file;
    }

    internal static void StartInstallAndExit(string setupFile, string expectedSha256)
    {
        using var installer = new FileStream(setupFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!IsHex64(expectedSha256) || !Convert.ToHexString(SHA256.HashData(installer)).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Installer changed after download; install cancelled.");
        // Start the verified installer directly; never interpret local paths as shell commands.
        using var process = Process.Start(new ProcessStartInfo(setupFile)
        {
            Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
            UseShellExecute = true
        });
    }
}
