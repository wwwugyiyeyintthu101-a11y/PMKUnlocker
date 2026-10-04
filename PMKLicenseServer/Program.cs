using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PMKLicenseServer;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Admin key: config မှာ ပေးထားမှ သုံးမယ် — မပေး/known-default ဆို random 256-bit ထုတ်ပြီး
// adminkey.txt (source control မပါ) မှာ သိမ်းမယ်၊ console မှာလည်း ပြမယ်။
const string LegacyDefaultKey = "pmk-admin-2026"; // ယခင် hardcode — ယခု အသုံးမပြုတော့
string adminKey = builder.Configuration["AdminKey"] ?? "";
string keyFile = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(builder.Configuration["DataFile"] ?? "licenses.json"))!, "adminkey.txt");
if (string.IsNullOrWhiteSpace(adminKey) || adminKey == LegacyDefaultKey)
{
    try
    {
        if (File.Exists(keyFile))
        {
            string k = File.ReadAllText(keyFile).Trim();
            if (k.Length >= 32) adminKey = k;
        }
    }
    catch { }
    if (string.IsNullOrWhiteSpace(adminKey) || adminKey == LegacyDefaultKey)
    {
        adminKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        try { File.WriteAllText(keyFile, adminKey); } catch { }
        Console.WriteLine("=== Admin key generated (stored in " + keyFile + ") ===");
        Console.WriteLine(adminKey);
    }
}
string dataFile = Path.GetFullPath(builder.Configuration["DataFile"] ?? "licenses.json");
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
        if (parts.Length != 3 || !int.TryParse(parts[1], out int iter) || iter <= 0)
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
    if (string.IsNullOrWhiteSpace(email)) return false;
    int at = email.IndexOf('@');
    return at > 0 && at < email.Length - 1 && email.IndexOf('.', at) > at + 1;
}

LicenseDb LoadDb()
{
    lock (lockObj)
    {
        if (!File.Exists(dataFile)) return new LicenseDb();
        try
        {
            return JsonSerializer.Deserialize<LicenseDb>(File.ReadAllText(dataFile)) ?? new LicenseDb();
        }
        catch { return new LicenseDb(); }
    }
}

void SaveDb(LicenseDb db)
{
    lock (lockObj)
    {
        string dir = Path.GetDirectoryName(dataFile)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(dataFile, JsonSerializer.Serialize(db, jsonOpts));
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
    if (string.IsNullOrWhiteSpace(adminKey)) return false; // key မရှိ → လုံးဝ ပိတ်
    string got = ctx.Request.Headers["X-Admin-Key"].ToString();
    if (got.Length == 0 || got.Length != adminKey.Length) return false;
    return CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(got), Encoding.UTF8.GetBytes(adminKey));
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
    if (password.Length < 4)
        return J(new { ok = false, message = "Password အနည်းဆုံး 4 လုံး" });

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
});

// ---- customer login (client tool) ----
app.MapPost("/api/login", (LoginReq req) =>
{
    string email = (req.Email ?? "").Trim();
    string password = req.Password ?? "";
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
        if (string.IsNullOrEmpty(acc.AutoToken) || !FixedHexEq(req.Token!, acc.AutoToken))
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

    var expires = DateTime.Parse(acc.ExpiresAt, null, System.Globalization.DateTimeStyles.RoundtripKind);
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
});

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
                DaysLeft = expired ? 0 : (int)Math.Ceiling((exp.ToUniversalTime() - DateTime.UtcNow).TotalDays)
            };
        });
    return J(rows);
});
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
        if (password.Length < 4)
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
        if (req.Password.Length < 4)
            return J(new { ok = false, message = "Password အနည်းဆုံး 4 လုံး" });
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
});

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
});

// ---- admin: renew (+N months from max(now, expires)) ----
app.MapPost("/api/admin/renew", (HttpContext ctx, RenewReq req) =>
{
    if (!IsAdmin(ctx)) return Results.Unauthorized();
    string email = (req.Email ?? "").Trim();
    var db = LoadDb();
    var acc = db.Accounts.FirstOrDefault(a =>
        string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase));
    if (acc == null) return J(new { ok = false, message = "Email မတွေ့ပါ" });

    var exp = DateTime.Parse(acc.ExpiresAt, null, System.Globalization.DateTimeStyles.RoundtripKind);
    var baseDate = exp.ToUniversalTime() > DateTime.UtcNow ? exp.ToUniversalTime() : DateTime.UtcNow;
    int months = req.Months <= 0 ? MonthsForPlan(acc.Plan) : req.Months;
    var next = baseDate.AddMonths(months);
    acc.ExpiresAt = next.ToString("o");
    SaveDb(db);
    return J(new
    {
        ok = true,
        message = $"Renewed {email} +{months}m → {next:yyyy-MM-dd}",
        expiresAt = acc.ExpiresAt
    });
});

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
});

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
});

// ---- admin UI ----
app.MapGet("/ui", () => Results.Text(AdminHtml.Fallback, "text/html; charset=utf-8"));
app.MapGet("/admin", () => Results.Text(AdminHtml.Fallback, "text/html; charset=utf-8"));

app.Run();
