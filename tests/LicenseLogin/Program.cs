using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PMKUnlocker;

// License server + login flow checks.
// The server is the real PMKLicenseServer process with a temporary DataFile and a fixed admin key;
// the client half is the real LicenseClient/LocalLogin linked from the desktop project (DPAPI and all).

const string AdminKey = "test-admin-key-0123456789abcdef0123456789abcdef";

int passed = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    Console.WriteLine("PASS: " + description);
    passed++;
}

string repoRoot = FindRepoRoot();
string serverDll = Path.Combine(repoRoot, "PMKLicenseServer", "bin", "Debug", "net10.0", "PMKLicenseServer.dll");
if (!File.Exists(serverDll))
{
    Console.Error.WriteLine("PMKLicenseServer.dll not found at " + serverDll + " — build the server first.");
    return 1;
}
string dotnet = FindDotnet();
string temp = Path.Combine(Path.GetTempPath(), "pmk-license-login-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
ShopServices.DataRootOverride = Path.Combine(temp, "appdata");
string dataFile = Path.Combine(temp, "licenses.json");

ServerProcess? server = null;
HttpClient? http = null;
try
{
    // ---- remember-me storage: a token, never a password ----
    string sessionFile = ShopServices.DataPath("pmk_session.bin");
    string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    LocalLogin.SaveRememberedToken("owner@pmk.local", token);
    Check(LocalLogin.TryRemembered(out string savedEmail, out string savedSecret, out bool savedIsToken) &&
          savedEmail == "owner@pmk.local" && savedSecret == token && savedIsToken,
        "remember-me round-trips the email and the server token");
    byte[] blob = File.ReadAllBytes(sessionFile);
    string blobText = Encoding.UTF8.GetString(blob);
    Check(!blobText.Contains("owner@pmk.local") && !blobText.Contains(token),
        "the DPAPI blob carries neither the email nor the token in clear text");
    string plain = Encoding.UTF8.GetString(ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser));
    Check(plain == "owner@pmk.local\n2:" + token,
        "remember-me stores exactly email + 2:token — no password field");
    LocalLogin.SaveRemembered("owner@pmk.local", "LegacyPass#1");
    Check(LocalLogin.TryRemembered(out _, out string legacySecret, out bool legacyIsToken) &&
          !legacyIsToken && legacySecret == "LegacyPass#1",
        "a legacy password blob still parses as a non-token secret");
    Check(LocalLogin.SavedRememberedEmail() == "owner@pmk.local", "the saved email is readable without the secret");
    LocalLogin.ClearRemembered();
    Check(!LocalLogin.TryRemembered(out _, out _, out _) && !File.Exists(sessionFile),
        "ClearRemembered removes the blob");
    File.WriteAllBytes(sessionFile, new byte[] { 1, 2, 3 });
    Check(!LocalLogin.TryRemembered(out _, out _, out _) && !File.Exists(sessionFile),
        "a corrupt blob is dropped instead of throwing");

    // ---- server URL policy ----
    bool rejectedRemote = false;
    try { LicenseClient.BaseUrl = "http://example.com"; } catch (ArgumentException) { rejectedRemote = true; }
    Check(rejectedRemote && !LicenseClient.IsServerUrl("http://example.com") &&
          LicenseClient.IsServerUrl("https://lic.example.com") &&
          !LicenseClient.IsServerUrl("http://127.0.0.1:5260/?x=1"),
        "plain HTTP is loopback-only and query strings are refused");

    // ---- real server process on a throwaway DataFile ----
    server = ServerProcess.Start(dotnet, serverDll, Path.Combine(repoRoot, "PMKLicenseServer"), dataFile, AdminKey, out string baseUrl);
    bool ready = server.WaitReady(baseUrl, TimeSpan.FromSeconds(30));
    if (!ready)
    {
        Console.Error.WriteLine(server.Tail(20));
        throw new Exception("license server did not answer on " + baseUrl);
    }
    Check(true, "license server starts on the throwaway DataFile");
    LicenseClient.BaseUrl = baseUrl;
    http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(10) };

    async Task<(HttpStatusCode Status, JsonNode? Body)> Call(HttpMethod method, string path, object? payload = null, string? key = null)
    {
        using var req = new HttpRequestMessage(method, path);
        if (payload != null)
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        if (key != null) req.Headers.Add("X-Admin-Key", key);
        using var resp = await http!.SendAsync(req);
        string text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text));
    }

    string health = await http.GetStringAsync("/");
    Check(health.Contains("PMK License Server OK"), "the server health root answers");

    Check((await Call(HttpMethod.Get, "/api/admin/list")).Status == HttpStatusCode.Unauthorized,
        "admin list without the key is 401");
    Check((await Call(HttpMethod.Get, "/api/admin/list", null, new string('x', AdminKey.Length))).Status == HttpStatusCode.Unauthorized,
        "admin list with a wrong key is 401");
    var emptyList = await Call(HttpMethod.Get, "/api/admin/list", null, AdminKey);
    Check(emptyList.Status == HttpStatusCode.OK && emptyList.Body is JsonArray { Count: 0 },
        "admin list with the right key starts empty");

    // ---- registration: pending until approved ----
    var badEmail = await LicenseClient.RegisterAsync("not-an-email", "AlicePass#1");
    Check(!badEmail.Ok && badEmail.Message.Contains("Email"), "the client refuses an invalid registration email");
    var shortPassword = await LicenseClient.RegisterAsync("alice@test.local", "short");
    Check(!shortPassword.Ok && shortPassword.Message.Contains("8"), "the client refuses a short registration password");
    var registered = await LicenseClient.RegisterAsync("alice@test.local", "AlicePass#1");
    Check(registered.Ok, "self-registration creates the account");
    var pendingLogin = await LicenseClient.LoginAsync("alice@test.local", "AlicePass#1");
    Check(!pendingLogin.Ok && pendingLogin.Message.Contains("Admin approval"), "a pending account cannot log in");
    var approved = await Call(HttpMethod.Post, "/api/admin/approve", new { email = "alice@test.local", plan = "3m" }, AdminKey);
    Check(approved.Status == HttpStatusCode.OK && approved.Body!["ok"]!.GetValue<bool>(),
        "admin approve activates the pending account");
    var wrongPassword = await LicenseClient.LoginAsync("alice@test.local", "WrongPass#1");
    Check(!wrongPassword.Ok && wrongPassword.Message.Contains("Password"), "a wrong password is refused");

    var plainLogin = await LicenseClient.LoginAsync("alice@test.local", "AlicePass#1");
    Check(plainLogin.Ok && plainLogin.Plan == "quarterly" && plainLogin.AutoToken.Length == 0,
        "password login without remember returns the plan and no token");
    var rememberLogin = await LicenseClient.LoginAsync("alice@test.local", "AlicePass#1", remember: true);
    Check(rememberLogin.Ok && IsHex(rememberLogin.AutoToken) && rememberLogin.AutoToken.Length == 64,
        "remember=true returns a 64-hex auto-login token");
    string aliceToken = rememberLogin.AutoToken;
    var tokenLogin = await LicenseClient.TokenLoginAsync("alice@test.local", aliceToken);
    Check(tokenLogin.Ok && tokenLogin.Plan == "quarterly", "token login succeeds without a password");
    var badToken = await LicenseClient.TokenLoginAsync("alice@test.local", new string('0', 64));
    Check(!badToken.Ok && badToken.Message.Contains("Session"), "a wrong token is refused");

    // ---- admin upsert, renew, block, password change ----
    var bobCreated = await Call(HttpMethod.Post, "/api/admin/upsert",
        new { email = "bob@test.local", password = "BobPass#2026", plan = "monthly" }, AdminKey);
    Check(bobCreated.Status == HttpStatusCode.OK && bobCreated.Body!["ok"]!.GetValue<bool>(), "admin creates an account");
    var bobRemember = await LicenseClient.LoginAsync("bob@test.local", "BobPass#2026", remember: true);
    Check(bobRemember.Ok && IsHex(bobRemember.AutoToken), "the admin-created account logs in and remembers");
    string bobToken = bobRemember.AutoToken;
    string bobExpiry = bobRemember.ExpiresAt;
    var renewed = await Call(HttpMethod.Post, "/api/admin/renew", new { email = "bob@test.local", months = 2 }, AdminKey);
    Check(renewed.Status == HttpStatusCode.OK &&
          DateTime.Parse(renewed.Body!["expiresAt"]!.GetValue<string>()) > DateTime.Parse(bobExpiry),
        "renew pushes the expiry further out");

    var blocked = await Call(HttpMethod.Post, "/api/admin/block", new { email = "bob@test.local", blocked = true }, AdminKey);
    Check(blocked.Status == HttpStatusCode.OK && blocked.Body!["ok"]!.GetValue<bool>(), "admin blocks the account");
    var blockedToken = await LicenseClient.TokenLoginAsync("bob@test.local", bobToken);
    Check(!blockedToken.Ok && blockedToken.Message.Contains("blocked"), "blocking refuses the remember-me token");
    var blockedLogin = await LicenseClient.LoginAsync("bob@test.local", "BobPass#2026");
    Check(!blockedLogin.Ok && blockedLogin.Message.Contains("blocked"), "a blocked account cannot log in");
    await Call(HttpMethod.Post, "/api/admin/block", new { email = "bob@test.local", blocked = false }, AdminKey);
    Check(!(await LicenseClient.TokenLoginAsync("bob@test.local", bobToken)).Ok,
        "the token cleared at block time stays dead after unblocking");
    Check((await LicenseClient.LoginAsync("bob@test.local", "BobPass#2026")).Ok, "unblocking restores password login");

    var passwordChanged = await Call(HttpMethod.Post, "/api/admin/upsert",
        new { email = "bob@test.local", password = "BobNew#2026" }, AdminKey);
    Check(passwordChanged.Status == HttpStatusCode.OK && passwordChanged.Body!["ok"]!.GetValue<bool>(), "admin changes the password");
    Check(!(await LicenseClient.TokenLoginAsync("bob@test.local", bobToken)).Ok,
        "a password change revokes the old remember-me token");
    Check((await LicenseClient.LoginAsync("bob@test.local", "BobNew#2026")).Ok, "the new password works");

    var approveMissing = await Call(HttpMethod.Post, "/api/admin/approve", new { email = "nobody@test.local" }, AdminKey);
    Check(approveMissing.Status == HttpStatusCode.OK && !approveMissing.Body!["ok"]!.GetValue<bool>(),
        "approving an unknown email reports a miss");

    // ---- the DataFile is the temporary one ----
    string dbText = File.ReadAllText(dataFile);
    Check(File.Exists(dataFile) && dbText.Contains("\"Accounts\"") &&
          dbText.Contains("alice@test.local") && dbText.Contains("bob@test.local"),
        "accounts persist to the configured DataFile in PascalCase");

    // ---- expiry and legacy-hash upgrade ----
    JsonObject account = FindAccount(dataFile, "alice@test.local");
    account["ExpiresAt"] = DateTime.UtcNow.AddDays(-1).ToString("o");
    SaveDb(dataFile, account);
    var expiredLogin = await LicenseClient.LoginAsync("alice@test.local", "AlicePass#1");
    Check(!expiredLogin.Ok && expiredLogin.Message.Contains("သက်တမ်းကုန်ပြီ"), "an expired subscription is refused");

    JsonObject bob = FindAccount(dataFile, "bob@test.local");
    string bobSalt = bob["Salt"]!.GetValue<string>();
    bob["PassHash"] = LegacyHash(bobSalt, "Legacy#12345");
    SaveDb(dataFile, bob);
    var legacyLogin = await LicenseClient.LoginAsync("bob@test.local", "Legacy#12345");
    Check(legacyLogin.Ok, "a legacy SHA-256 password still logs in");
    Check(FindAccount(dataFile, "bob@test.local")["PassHash"]!.GetValue<string>().StartsWith("P2$100000$"),
        "the legacy hash is upgraded to PBKDF2 on that login");

    // ---- 30-day token window ----
    var legacyRemember = await LicenseClient.LoginAsync("bob@test.local", "Legacy#12345", remember: true);
    Check(legacyRemember.Ok && IsHex(legacyRemember.AutoToken), "remember works after the hash upgrade");
    JsonObject stale = FindAccount(dataFile, "bob@test.local");
    stale["AutoTokenAt"] = DateTime.UtcNow.AddDays(-31).ToString("o");
    SaveDb(dataFile, stale);
    var staleToken = await LicenseClient.TokenLoginAsync("bob@test.local", legacyRemember.AutoToken);
    Check(!staleToken.Ok && staleToken.Message.Contains("Session"), "a token older than 30 days is refused");

    // ---- one owner per DataFile ----
    var carol = await Call(HttpMethod.Post, "/api/admin/upsert",
        new { email = "carol@test.local", password = "CarolPass#2026", plan = "yearly" }, AdminKey);
    Check(carol.Status == HttpStatusCode.OK && carol.Body!["ok"]!.GetValue<bool>(), "a third account is created");
    var carolRemember = await LicenseClient.LoginAsync("carol@test.local", "CarolPass#2026", remember: true);
    Check(carolRemember.Ok && IsHex(carolRemember.AutoToken), "the third account remembers its login");
    string carolToken = carolRemember.AutoToken;

    using (var second = ServerProcess.Start(dotnet, serverDll, Path.Combine(repoRoot, "PMKLicenseServer"), dataFile, AdminKey, out _))
    {
        bool refused = second.WaitForExit(TimeSpan.FromSeconds(20)) && second.ExitCode != 0;
        if (!refused) Console.Error.WriteLine(second.Tail(20));
        Check(refused, "a second server on the same DataFile exits instead of racing it");
    }

    // ---- restart keeps accounts and tokens ----
    server.Dispose();
    server = ServerProcess.Start(dotnet, serverDll, Path.Combine(repoRoot, "PMKLicenseServer"), dataFile, AdminKey, out string baseUrl2);
    bool ready2 = server.WaitReady(baseUrl2, TimeSpan.FromSeconds(30));
    if (!ready2)
    {
        Console.Error.WriteLine(server.Tail(20));
        throw new Exception("license server did not answer after restart on " + baseUrl2);
    }
    http.Dispose();
    http = new HttpClient { BaseAddress = new Uri(baseUrl2), Timeout = TimeSpan.FromSeconds(10) };
    LicenseClient.BaseUrl = baseUrl2;
    var restartedToken = await LicenseClient.TokenLoginAsync("carol@test.local", carolToken);
    Check(restartedToken.Ok, "a remember-me token survives a server restart");
    var restartedList = await Call(HttpMethod.Get, "/api/admin/list", null, AdminKey);
    Check(restartedList.Status == HttpStatusCode.OK && restartedList.Body is JsonArray { Count: >= 3 },
        "accounts survive a server restart");

    // ---- the client's auto-login path reads the same blob ----
    LocalLogin.SaveRememberedToken("carol@test.local", carolToken);
    var auto = await LicenseClient.TryAutoLoginAsync();
    Check(auto.Ok, "TryAutoLoginAsync logs in with the stored token");
    LocalLogin.SaveRemembered("carol@test.local", "CarolPass#2026");
    var legacyAuto = await LicenseClient.TryAutoLoginAsync();
    Check(legacyAuto.Ok, "a legacy password blob still auto-logs in");
    Check(LocalLogin.TryRemembered(out _, out _, out bool upgradedIsToken) && upgradedIsToken,
        "the legacy auto-login is upgraded to a token blob");

    Console.WriteLine($"{passed} login-flow checks passed.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("FAIL: " + ex.Message);
    return 1;
}
finally
{
    http?.Dispose();
    server?.Dispose();
    try { Directory.Delete(temp, true); } catch { }
}

// ---- helpers ----

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !(Directory.Exists(Path.Combine(dir.FullName, "PMKLicenseServer")) &&
                            Directory.Exists(Path.Combine(dir.FullName, "tests"))))
        dir = dir.Parent;
    return dir?.FullName ?? throw new InvalidOperationException("Repository root not found from " + AppContext.BaseDirectory);
}

static string FindDotnet()
{
    string? host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
    if (!string.IsNullOrEmpty(host) && File.Exists(host)) return host;
    string? root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
    if (!string.IsNullOrEmpty(root))
    {
        string candidate = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        if (File.Exists(candidate)) return candidate;
    }
    // shared/Microsoft.NETCore.App/<version> → three levels up is the SDK root.
    string? fromRuntime = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..",
        OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
    if (File.Exists(fromRuntime)) return fromRuntime;
    return "dotnet";
}

static bool IsHex(string value) =>
    value.Length > 0 && value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));

static string LegacyHash(string salt, string password) =>
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(salt + ":" + password)));

static JsonObject FindAccount(string dataFile, string email) =>
    JsonNode.Parse(File.ReadAllText(dataFile))!["Accounts"]!.AsArray()
        .Select(a => a!.AsObject())
        .First(a => string.Equals((string?)a["Email"], email, StringComparison.OrdinalIgnoreCase));

static void SaveDb(string dataFile, JsonObject account)
{
    // Write the whole store back; the account node belongs to the parsed document.
    JsonNode? root = account;
    while (root?.Parent != null) root = root.Parent;
    File.WriteAllText(dataFile, root!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
}

internal sealed class ServerProcess : IDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _log = new();

    private ServerProcess(Process process) => _process = process;

    internal static ServerProcess Start(string dotnet, string dll, string workingDir, string dataFile, string adminKey, out string baseUrl)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        baseUrl = "http://127.0.0.1:" + port;

        var psi = new ProcessStartInfo(dotnet)
        {
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(dll);
        // appsettings.json pins Urls; a command-line value outranks it.
        psi.ArgumentList.Add("--urls");
        psi.ArgumentList.Add(baseUrl);
        psi.Environment["ASPNETCORE_URLS"] = baseUrl;
        psi.Environment["DataFile"] = dataFile;
        psi.Environment["AdminKey"] = adminKey;

        var process = new Process { StartInfo = psi };
        var server = new ServerProcess(process);
        process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (server._log) server._log.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (server._log) server._log.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return server;
    }

    internal bool WaitReady(string baseUrl, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited) return false;
            try
            {
                using var resp = probe.GetAsync(baseUrl + "/").GetAwaiter().GetResult();
                if (resp.IsSuccessStatusCode) return true;
            }
            catch { }
            Thread.Sleep(250);
        }
        return false;
    }

    internal bool WaitForExit(TimeSpan timeout) => _process.WaitForExit((int)timeout.TotalMilliseconds);

    internal int ExitCode => _process.ExitCode;

    internal string Tail(int lines)
    {
        lock (_log)
            return string.Join('\n', _log.ToString().Split('\n').TakeLast(lines));
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5000);
        }
        catch { }
        _process.Dispose();
    }
}
