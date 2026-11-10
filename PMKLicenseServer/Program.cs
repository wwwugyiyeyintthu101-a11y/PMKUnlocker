using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PMKLicenseServer;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16 * 1024);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("api", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
            AutoReplenishment = true
        }));
});

// Behind a reverse proxy every request looks like one IP, so all shops share a single 60/min
// bucket. Only headers from explicitly listed proxies are honoured.
string[] trustedProxies = builder.Configuration.GetSection("TrustedProxies").Get<string[]>() ?? Array.Empty<string>();
var knownProxies = trustedProxies.Select(IPAddress.Parse).ToArray();
if (knownProxies.Length > 0)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        foreach (IPAddress address in knownProxies) options.KnownProxies.Add(address);
    });
}

var app = builder.Build();

// The API carries passwords, remember-me tokens and the admin key. Plain HTTP is only
// acceptable on this machine; exposing it on a LAN address needs an explicit opt-in.
string configuredUrls = builder.Configuration["urls"] ?? "";
if (!string.Equals(builder.Configuration["AllowInsecureHttp"], "true", StringComparison.OrdinalIgnoreCase))
{
    foreach (string raw in configuredUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out Uri? uri)) continue;
        bool loopback = uri.IsLoopback ||
            uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host == "::1";
        if (uri.Scheme == Uri.UriSchemeHttps || loopback) continue;
        Console.Error.WriteLine(
            "Refusing to bind the license API over plain HTTP at " + raw + ". Use HTTPS, " +
            "terminate TLS in front of loopback, or set AllowInsecureHttp=true deliberately.");
        Environment.Exit(2);
    }
}

if (knownProxies.Length > 0) app.UseForwardedHeaders();

// Admin key: only its SHA-256 hash is kept at rest, so reading adminkey.txt never yields a
// working admin credential. The generated key is printed once; save it then. A legacy
// plaintext file is adopted and rewritten as a hash on the next start.
const string LegacyDefaultKey = "pmk-admin-2026"; // ယခင် hardcode — ယခု အသုံးမပြုတော့
const string KeyHashPrefix = "sha256:";

string Sha256Hex(string value) =>
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

void TightenAcl(string path, bool directory)
{
    // Only managed deployments (ProgramData) are hardened. A dev/test data folder in %TEMP%
    // must stay usable by the non-elevated account that created it.
    string commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    string full = Path.GetFullPath(path);
    if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(commonData) ||
        !full.StartsWith(Path.GetFullPath(commonData) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        return;
    if (!File.Exists(full) && !Directory.Exists(full)) return;
    try
    {
        var psi = new ProcessStartInfo("icacls.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        psi.ArgumentList.Add(full);
        psi.ArgumentList.Add("/inheritance:r");
        psi.ArgumentList.Add("/grant:r");
        psi.ArgumentList.Add(directory ? "*S-1-5-18:(OI)(CI)F" : "*S-1-5-18:F");
        psi.ArgumentList.Add(directory ? "*S-1-5-32-544:(OI)(CI)F" : "*S-1-5-32-544:F");
        using Process? p = Process.Start(psi);
        if (p == null) return;
        if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } Console.WriteLine("Warning: icacls timed out for " + full); return; }
        if (p.ExitCode != 0) Console.WriteLine("Warning: icacls could not tighten " + full);
    }
    catch (Exception ex)
    {
        Console.WriteLine("Warning: could not tighten ACL on " + full + ": " + ex.Message);
    }
}

string adminKeyHash = "";
string configuredKey = builder.Configuration["AdminKey"] ?? "";
string keyFile = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(builder.Configuration["DataFile"] ?? "licenses.json"))!, "adminkey.txt");
Directory.CreateDirectory(Path.GetDirectoryName(keyFile)!);
TightenAcl(Path.GetDirectoryName(keyFile)!, directory: true);

if (!string.IsNullOrWhiteSpace(configuredKey) && configuredKey != LegacyDefaultKey)
    adminKeyHash = Sha256Hex(configuredKey.Trim());
else
{
    try
    {
        if (File.Exists(keyFile))
        {
            string stored = File.ReadAllText(keyFile).Trim();
            if (stored.StartsWith(KeyHashPrefix, StringComparison.OrdinalIgnoreCase) && stored.Length == KeyHashPrefix.Length + 64)
                adminKeyHash = stored[KeyHashPrefix.Length..].ToUpperInvariant();
            else if (stored.Length >= 32 && stored != LegacyDefaultKey)
            {
                adminKeyHash = Sha256Hex(stored);
                File.WriteAllText(keyFile, KeyHashPrefix + adminKeyHash);
                TightenAcl(keyFile, directory: false);
                Console.WriteLine("Admin key file upgraded to a hash: " + keyFile);
            }
        }
    }
    catch { }
    if (adminKeyHash.Length == 0)
    {
        string generated = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        adminKeyHash = Sha256Hex(generated);
        File.WriteAllText(keyFile, KeyHashPrefix + adminKeyHash);
        TightenAcl(keyFile, directory: false);
        Console.WriteLine("=== Admin key generated (only its hash is stored in " + keyFile + ") ===");
        Console.WriteLine("Admin key (save it now — it cannot be read back): " + generated);
    }
}
string dataFile = Path.GetFullPath(builder.Configuration["DataFile"] ?? "licenses.json");
Directory.CreateDirectory(Path.GetDirectoryName(dataFile)!);
TightenAcl(dataFile, directory: false);
// Exactly one server process may own this JSON store. A second instance fails
// startup rather than racing a separate in-memory lock against the same file.
using var databaseLease = new FileStream(dataFile + ".lock", FileMode.OpenOrCreate,
    FileAccess.ReadWrite, FileShare.None);
using var transactions = new SemaphoreSlim(1, 1);
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/api")) { await next(context); return; }
    await transactions.WaitAsync(context.RequestAborted);
    try { await next(context); }
    catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
    {
        app.Logger.LogError(ex, "License storage unavailable; request rejected without resetting accounts.");
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new { ok = false, message = "License storage unavailable. Contact the administrator." });
    }
    finally { transactions.Release(); }
});
var jsonOpts = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = null // keep PascalCase for file DB
};
var apiJsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
var lockObj = new object();

// PBKDF2-SHA256 (100k) — ရှေးဟောင်း SHA-256 hash များကို verify ပြီး login အောင်မြင်ရင် auto-upgrade
const int Pbkdf2Iterations = 100_000;

string HashPassword(string password, string salt)
{
    byte[] dk = Rfc2898DeriveBytes.Pbkdf2(
        password, Encoding.UTF8.GetBytes(salt + ":"), Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
    return "P2$" + Pbkdf2Iterations + "$" + Convert.ToHexString(dk);
}

bool FixedHexEq(string a, string b)
{
    if (a.Length != b.Length) return false;
    // hex case ကွာနိုင် (legacy hand-edited DB) → case မသတ်မှတ်ဘဲ နှိုင်း
    return CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(a.ToUpperInvariant()),
        Encoding.UTF8.GetBytes(b.ToUpperInvariant()));
}

// returns (ok, needsUpgrade) — needsUpgrade = ရှေး format (plain SHA-256) ဖြင့် ဝင်မိခြင်း
(bool Ok, bool NeedsUpgrade) VerifyPassword(string password, string salt, string stored)
{
    if (stored.StartsWith("P2$", StringComparison.Ordinal))
    {
        string[] parts = stored.Split('$');
        if (parts.Length != 3 || !int.TryParse(parts[1], out int iter) || iter <= 0 || iter > 1_000_000)
            return (false, false);
        byte[] dk = Rfc2898DeriveBytes.Pbkdf2(
            password, Encoding.UTF8.GetBytes(salt + ":"), iter, HashAlgorithmName.SHA256, 32);
        return (FixedHexEq(Convert.ToHexString(dk), parts[2]), false);
    }

    // legacy: SHA256(salt + ":" + password)
    string legacy = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(salt + ":" + password)));
    bool ok = FixedHexEq(legacy, stored);
    return (ok, ok);
}

bool IsValidEmail(string email)
{
    if (string.IsNullOrWhiteSpace(email) || email.Length > 254 ||
        email.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || "<>\"'".Contains(c))) return false;
    return System.Net.Mail.MailAddress.TryCreate(email, out var parsed) &&
        parsed.Address == email && parsed.Host.Contains('.');
}

LicenseDb LoadDb()
{
    lock (lockObj)
    {
        if (!File.Exists(dataFile)) return new LicenseDb();
        using var document = JsonDocument.Parse(File.ReadAllText(dataFile));
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("Accounts", out var accounts) || accounts.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("License database has no Accounts array.");
        var db = document.RootElement.Deserialize<LicenseDb>();
        if (db?.Accounts == null || db.Accounts.Any(a => a == null || string.IsNullOrEmpty(a.Email) ||
            a.Salt == null || a.PassHash == null) ||
            db.Accounts.Select(a => a.Email).Distinct(StringComparer.OrdinalIgnoreCase).Count() != db.Accounts.Count)
            throw new InvalidDataException("Invalid license database; restore a verified backup.");
        return db;
    }
}

void SaveDb(LicenseDb db)
{
    lock (lockObj)
    {
        string dir = Path.GetDirectoryName(dataFile)!;
        Directory.CreateDirectory(dir);
        string temp = dataFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, db, jsonOpts);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(dataFile)) File.Replace(temp, dataFile, dataFile + ".bak");
            else File.Move(temp, dataFile);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

// plan: monthly=1, quarterly=3, halfyearly=6, yearly=12 (also accept "3m","6m","12m")
int MonthsForPlan(string plan)
{
    plan = (plan ?? "").Trim().ToLowerInvariant();
    return plan switch
    {
        "quarterly" or "3m" or "3" => 3,
        "halfyearly" or "6m" or "6" => 6,
        "yearly" or "12m" or "12" => 12,
        _ => 1
    };
}

string NormalizePlan(string plan)
{
    int m = MonthsForPlan(plan);
    return m switch { 3 => "quarterly", 6 => "halfyearly", 12 => "yearly", _ => "monthly" };
}

DateTime ExpiryFrom(DateTime from, string plan) =>
    from.AddMonths(MonthsForPlan(plan));

bool IsAdmin(HttpContext ctx)
{
    if (adminKeyHash.Length == 0) return false; // key မရှိ → လုံးဝ ပိတ်
    string got = ctx.Request.Headers["X-Admin-Key"].ToString();
    if (got.Length == 0) return false;
    string gotHash = Sha256Hex(got);
    return CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(gotHash), Encoding.UTF8.GetBytes(adminKeyHash));
}

IResult J(object o) => Results.Json(o, apiJsonOpts);

app.MapGet("/", () => Results.Text("PMK License Server OK", "text/plain"));

// ---- self-register (pending until admin approves) ----
app.MapPost("/api/register", (RegisterReq req) =>
{
    string email = (req.Email ?? "").Trim();
    string password = req.Password ?? "";
    if (!IsValidEmail(email))
        return J(new { ok = false, message = "Email မှားနေသည် (user@gmail.com)" });
    if (password.Length < 8 || password.Length > 256)
        return J(new { ok = false, message = "Password 8–256 လုံး" });

    var db = LoadDb();
    var existing = db.Accounts.FirstOrDefault(a =>
        string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase));
    if (existing != null)
        return J(new { ok = false, message = "Email ရှိပြီးသား — Login ဝင်ပါ" });

    string salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    db.Accounts.Add(new Account
    {
        Email = email,
        Salt = salt,
        PassHash = HashPassword(password, salt),
        Plan = "monthly",
        ExpiresAt = "", // admin approve မှ set
        Blocked = false,
        Pending = true,
        Note = "self-register",
        CreatedAt = DateTime.UtcNow.ToString("o")
    });
    SaveDb(db);
    return J(new
    {
        ok = true,
        message = "Registered — Admin approval စောင့်ဆိုင်းပါ"
    });
}).RequireRateLimiting("api");

// ---- customer login (client tool) ----
app.MapPost("/api/login", (LoginReq req) =>
{
    string email = (req.Email ?? "").Trim();
    string password = req.Password ?? "";
    if (password.Length > 256 || (req.Token?.Length ?? 0) > 128)
        return J(new LoginRes { Ok = false, Message = "Invalid credentials." });
    bool viaTokenLogin = !string.IsNullOrEmpty(req.Token);
    // token login = password မလို; password login = password လို
    if (!IsValidEmail(email) || (!viaTokenLogin && string.IsNullOrEmpty(password)))
        return J(new LoginRes { Ok = false, Message = "Email/Password မှန်မှန် ထည့်ပါ" });

    var db = LoadDb();
    var acc = db.Accounts.FirstOrDefault(a =>
        string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase));
    if (acc == null)
        return J(new LoginRes { Ok = false, Message = "Email မှတ်ပုံတင်ထားခြင်း မရှိပါ — admin ကို ဆက်သွယ်ပါ" });
    if (acc.Pending)
        return J(new LoginRes { Ok = false, Message = "Admin approval မပေးရသေးပါ — စောင့်ဆိုင်းပါ" });
    if (acc.Blocked)
        return J(new LoginRes { Ok = false, Message = "Account ပိတ်ထားပါ (blocked)" });

    string newToken = "";
    bool viaToken = viaTokenLogin;
    if (viaToken)
    {
        // remember-me token login — password မလို
        if (string.IsNullOrEmpty(acc.AutoToken) || !FixedHexEq(req.Token!, acc.AutoToken) ||
            !DateTimeOffset.TryParse(acc.AutoTokenAt, out var issued) ||
            issued > DateTimeOffset.UtcNow || issued.AddDays(30) <= DateTimeOffset.UtcNow)
            return J(new LoginRes { Ok = false, Message = "Session သက်တမ်းကုန်ပြီ — password ဖြင့် ပြန်ဝင်ပါ" });
    }
    else
    {
        var (okPwd, needsUpgrade) = VerifyPassword(password, acc.Salt, acc.PassHash);
        if (!okPwd)
            return J(new LoginRes { Ok = false, Message = "Password မမှန်ပါ" });
        if (needsUpgrade)
        {
            // ရှေး SHA-256 format → PBKDF2 အသစ်ပြောင်း (password အသစ်ထည့းစရာမလို)
            acc.PassHash = HashPassword(password, acc.Salt);
            SaveDb(db);
        }
        if (req.Remember)
        {
            newToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            acc.AutoToken = newToken;
            acc.AutoTokenAt = DateTime.UtcNow.ToString("o");
            SaveDb(db);
        }
    }

    if (string.IsNullOrEmpty(acc.ExpiresAt))
        return J(new LoginRes { Ok = false, Message = "Expiry မသတ်မှတ်ရသေးပါ — admin approve လုပ်ပါ" });

    if (!DateTime.TryParse(acc.ExpiresAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var expires))
        return J(new LoginRes { Ok = false, Message = "Invalid subscription expiry. Contact the administrator." });
    if (expires.ToUniversalTime() <= DateTime.UtcNow)
        return J(new LoginRes
        {
            Ok = false,
            Message = "Subscription သက်တမ်းကုန်ပြီ (" + expires.ToString("yyyy-MM-dd") + ") — ကြေးပေး/admin renew လုပ်ပါ",
            Plan = acc.Plan,
            ExpiresAt = acc.ExpiresAt
        });

    int daysLeft = (int)Math.Ceiling((expires.ToUniversalTime() - DateTime.UtcNow).TotalDays);
    return J(new LoginRes
    {
        Ok = true,
        Message = "OK",
        Plan = acc.Plan,
        ExpiresAt = acc.ExpiresAt,
        DaysLeft = daysLeft,
        AutoToken = newToken
    });
}).RequireRateLimiting("api");

// ---- admin: list ----
app.MapGet("/api/admin/list", (HttpContext ctx) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    var db = LoadDb();
    var rows = db.Accounts
        .OrderBy(a => a.Email, StringComparer.OrdinalIgnoreCase)
        .Select(a =>
        {
            var exp = DateTime.TryParse(a.ExpiresAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var expDt)
                ? expDt : DateTime.MinValue;
            bool expired = a.Pending ? false : (a.ExpiresAt == "" || exp.ToUniversalTime() <= DateTime.UtcNow);
            return new AdminRow
            {
                Email = a.Email,
                Plan = a.Plan,
                ExpiresAt = a.ExpiresAt,
                Blocked = a.Blocked,
                Pending = a.Pending,
                Note = a.Note,
                Expired = expired,
                DaysLeft = a.Pending || expired ? 0 : (int)Math.Ceiling((exp.ToUniversalTime() - DateTime.UtcNow).TotalDays)
            };
        });
    return J(rows);
}).RequireRateLimiting("api");
// ---- admin: create / update password / set plan ----
app.MapPost("/api/admin/upsert", (HttpContext ctx, UpsertReq req) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    string email = (req.Email ?? "").Trim();
    if (!IsValidEmail(email)) return J(new { ok = false, message = "Email မှားနေသည်" });

    string plan = NormalizePlan(req.Plan);
    var db = LoadDb();
    var acc = db.Accounts.FirstOrDefault(a =>
        string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase));

    if (acc == null)
    {
        string password = req.Password ?? "";
        if (password.Length < 8 || password.Length > 256)
            return J(new { ok = false, message = "New account: password အနည်းဆုံး 4 လုံး" });
        string salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var exp = ExpiryFrom(DateTime.UtcNow, plan);
        db.Accounts.Add(new Account
        {
            Email = email,
            Salt = salt,
            PassHash = HashPassword(password, salt),
            Plan = plan,
            ExpiresAt = exp.ToString("o"),
            Blocked = false,
            Pending = false, // admin ကိုယ်တိုင်ဖန်တီး → approve မလို
            Note = req.Note ?? "",
            CreatedAt = DateTime.UtcNow.ToString("o")
        });
        SaveDb(db);
        return J(new
        {
            ok = true,
            message = $"Created {email} ({plan}) expires {exp:yyyy-MM-dd}"
        });
    }

    if (!string.IsNullOrEmpty(req.Password))
    {
        if (req.Password.Length < 8 || req.Password.Length > 256)
            return J(new { ok = false, message = "Password 8–256 လုံး" });
        acc.Salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        acc.PassHash = HashPassword(req.Password, acc.Salt);
        acc.AutoToken = ""; // password ပြောင်း → remember-me session အဟောင်း ဖျက်
        acc.AutoTokenAt = "";
    }
    if (!string.IsNullOrEmpty(req.Plan))
        acc.Plan = plan;
    if (!string.IsNullOrEmpty(req.Note))
        acc.Note = req.Note!;
    SaveDb(db);
    return J(new { ok = true, message = $"Updated {email}" });
}).RequireRateLimiting("api");

// ---- admin: approve pending registration (set plan + start expiry) ----
app.MapPost("/api/admin/approve", (HttpContext ctx, ApproveReq req) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    string email = (req.Email ?? "").Trim();
    var db = LoadDb();
    var acc = db.Accounts.FirstOrDefault(a =>
        string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase));
    if (acc == null) return J(new { ok = false, message = "Email မတွေ့ပါ" });

    string plan = NormalizePlan(req.Plan);
    var exp = ExpiryFrom(DateTime.UtcNow, plan);
    acc.Pending = false;
    acc.Plan = plan;
    acc.ExpiresAt = exp.ToString("o");
    SaveDb(db);
    return J(new
    {
        ok = true,
        message = $"Approved {email} ({plan}) expires {exp:yyyy-MM-dd}",
        expiresAt = acc.ExpiresAt
    });
}).RequireRateLimiting("api");

// ---- admin: renew (+N months from max(now, expires)) ----
app.MapPost("/api/admin/renew", (HttpContext ctx, RenewReq req) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    string email = (req.Email ?? "").Trim();
    var db = LoadDb();
    var acc = db.Accounts.FirstOrDefault(a =>
        string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase));
    if (acc == null) return J(new { ok = false, message = "Email မတွေ့ပါ" });

    if (!DateTime.TryParse(acc.ExpiresAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var exp))
        return J(new { ok = false, message = "Set a valid expiry by approving the account first." });
    var baseDate = exp.ToUniversalTime() > DateTime.UtcNow ? exp.ToUniversalTime() : DateTime.UtcNow;
    int months = req.Months <= 0 ? MonthsForPlan(acc.Plan) : req.Months;
    if (months > 120) return J(new { ok = false, message = "Renewal must be at most 120 months." });
    var next = baseDate.AddMonths(months);
    acc.ExpiresAt = next.ToString("o");
    SaveDb(db);
    return J(new
    {
        ok = true,
        message = $"Renewed {email} +{months}m → {next:yyyy-MM-dd}",
        expiresAt = acc.ExpiresAt
    });
}).RequireRateLimiting("api");

// ---- admin: block / unblock ----
app.MapPost("/api/admin/block", (HttpContext ctx, BlockReq req) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    string email = (req.Email ?? "").Trim();
    var db = LoadDb();
    var acc = db.Accounts.FirstOrDefault(a =>
        string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase));
    if (acc == null) return J(new { ok = false, message = "Email မတွေ့ပါ" });
    acc.Blocked = req.Blocked;
    if (req.Blocked) { acc.AutoToken = ""; acc.AutoTokenAt = ""; } // block → session ဖျက်
    SaveDb(db);
    return J(new
    {
        ok = true,
        message = req.Blocked ? $"Blocked {email}" : $"Unblocked {email}"
    });
}).RequireRateLimiting("api");

// ---- admin: delete ----
app.MapPost("/api/admin/delete", (HttpContext ctx, BlockReq req) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    string email = (req.Email ?? "").Trim();
    var db = LoadDb();
    int n = db.Accounts.RemoveAll(a =>
        string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase));
    if (n == 0) return J(new { ok = false, message = "Email မတွေ့ပါ" });
    SaveDb(db);
    return J(new { ok = true, message = $"Deleted {email}" });
}).RequireRateLimiting("api");

// ---- admin UI ----
app.MapGet("/ui", () => Results.Text(AdminHtml.Fallback, "text/html; charset=utf-8"));
app.MapGet("/admin", () => Results.Text(AdminHtml.Fallback, "text/html; charset=utf-8"));

app.Run();
