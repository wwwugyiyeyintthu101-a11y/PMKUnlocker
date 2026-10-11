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

// GitHub Releases auto-update — login gate (update မလုပ်ဘဲ အထဲမဝင်ရ)
//   default repo: wwwugyiyeyintthu101-a11y/PMKUnlocker (file မရှိရင် ဒါသုံး)
//   override: %LocalAppData%\PMKMobileTool\pmk_update_repo.txt ထဲ "owner/repo" (ဒါမှမဟုတ် full https://github.com/owner/repo)
//   "off" / "none" / "disable" → check ပိတ် (dev only — gate လည်း ပိတ်သွားမယ်)
//   Release တစ်ခုမှာ asset ၂ ခု တင်ရမယ်:
//     - PMKMobileTool-<ver>-Setup.exe
//     - update.json  = { "version": "7.3.0", "url": "...Setup.exe", "sha256": "<hex>", "notes": "..." }
internal static class UpdateChecker
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    internal const string DefaultRepo = "wwwugyiyeyintthu101-a11y/PMKUnlocker";

    // login stage မှာ စတင်တဲ့ check — gate က ဒါကိုပဲ စောင့်ယူ (HTTP call တစ်ခါပဲ)
    internal static Task<UpdateInfo?>? PendingCheck;

    // login gate — update ရှိရင် UpdateInfo; နောက်ဆုံး version (သို့) repo off → null (ဝင်ခွင့်ပေး)
    // check fail / timeout / offline → exception (caller က strict block)
    internal static async Task<UpdateInfo?> AwaitCheckForGateAsync(int maxWaitMs)
    {
        Task<UpdateInfo?>? t = PendingCheck;
        if (t == null)
        {
            if (string.IsNullOrWhiteSpace(Repo)) return null;   // update source off
            t = CheckAsync();
        }
        PendingCheck = null;
        if (await Task.WhenAny(t, Task.Delay(maxWaitMs)) != t)
            throw new TimeoutException("update check timed out (" + (maxWaitMs / 1000) + "s)");
        return await t;   // fault (offline / HTTP error) → propagates
    }

    internal static string RepoFile => ShopServices.DataPath("pmk_update_repo.txt");

    internal static string Repo
    {
        get
        {
            try
            {
                if (File.Exists(RepoFile))
                {
                    string s = File.ReadAllText(RepoFile).Trim();
                    if (s.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                        s.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                        s.Equals("disable", StringComparison.OrdinalIgnoreCase))
                        return "";
                    if (s.Length > 0) return s;
                }
            }
            catch { }
            return DefaultRepo;
        }
    }

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
        if (string.IsNullOrWhiteSpace(repo)) return null;

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
