using System.Text;
using System.Text.Json;

namespace PMKUnlocker;

// Online license login — PMKLicenseServer (/api/login).
// Server URL: %LocalAppData%\PMKMobileTool\pmk_license_url.txt (default http://127.0.0.1:5260)
internal static class LicenseClient
{
    internal sealed class Result
    {
        public bool Ok;
        public string Message = "";
        public string Plan = "";
        public string ExpiresAt = "";
        public int DaysLeft;
        public bool Offline; // server မရောက်
    }

    private static string UrlFile => ShopServices.DataPath("pmk_license_url.txt");

    internal static string BaseUrl
    {
        get
        {
            try
            {
                if (File.Exists(UrlFile))
                {
                    string u = File.ReadAllText(UrlFile).Trim();
                    if (u.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return u.TrimEnd('/');
                }
            }
            catch { }
            return "http://127.0.0.1:5260";
        }
        set
        {
            try { File.WriteAllText(UrlFile, value.Trim()); } catch { }
        }
    }

    internal static async Task<Result> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        email = (email ?? "").Trim();
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
            return new Result { Message = "Email/Password ထည့်ပါ" };
        if (!LocalLogin.IsValidEmail(email))
            return new Result { Message = "Email မှားနေသည် (user@gmail.com)" };

        return await PostAsync("/api/login", email, password, ct);
    }

    // Self-register → server creates pending account → admin approve မှ login ဝင်ရ
    internal static async Task<Result> RegisterAsync(string email, string password, CancellationToken ct = default)
    {
        email = (email ?? "").Trim();
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
            return new Result { Message = "Email/Password ထည့်ပါ" };
        if (!LocalLogin.IsValidEmail(email))
            return new Result { Message = "Email မှားနေသည် (user@gmail.com)" };
        if (password.Length < 4)
            return new Result { Message = "Password အနည်းဆုံး 4 လုံး" };

        return await PostAsync("/api/register", email, password, ct);
    }

    private static async Task<Result> PostAsync(string path, string email, string password, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var payload = JsonSerializer.Serialize(new { email, password });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync(BaseUrl + path, content, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return new Result { Offline = true, Message = "License server HTTP " + (int)resp.StatusCode };

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            bool ok = root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
            string msg = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            string plan = root.TryGetProperty("plan", out var p) ? p.GetString() ?? "" : "";
            string exp = root.TryGetProperty("expiresAt", out var e) ? e.GetString() ?? "" : "";
            int days = root.TryGetProperty("daysLeft", out var d) ? d.GetInt32() : 0;
            return new Result { Ok = ok, Message = msg, Plan = plan, ExpiresAt = exp, DaysLeft = days };
        }
        catch (Exception ex)
        {
            return new Result
            {
                Offline = true,
                Message = "License server မရောက် (" + BaseUrl + ") — " + ex.Message
            };
        }
    }

    // Remember-me auto login → still must pass server check
    internal static async Task<Result> TryAutoLoginAsync(CancellationToken ct = default)
    {
        if (!LocalLogin.TryRemembered(out string email, out string password))
            return new Result { Message = "" };
        return await LoginAsync(email, password, ct);
    }
}
