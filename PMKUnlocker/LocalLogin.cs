using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PMKUnlocker;

// MST-style offline login (Email / Password / Remember me).
// Account က local (%LocalAppData%\PMKMobileTool\pmk_account.json) မှာပဲ — server မလို။
// Remember me → DPAPI (CurrentUser) encrypt → auto-login next launch.
internal static class LocalLogin
{
    private sealed class Account
    {
        public string Email { get; set; } = "";
        public string PassHash { get; set; } = "";
        public string Salt { get; set; } = "";
    }

    private static string AccountPath => ShopServices.DataPath("pmk_account.json");
    private static string SessionPath => ShopServices.DataPath("pmk_session.bin");

    internal static bool HasAccount() => File.Exists(AccountPath);

    internal static string? SavedEmail()
    {
        try
        {
            if (!HasAccount()) return null;
            var acc = JsonSerializer.Deserialize<Account>(File.ReadAllText(AccountPath));
            return string.IsNullOrWhiteSpace(acc?.Email) ? null : acc.Email;
        }
        catch { return null; }
    }

    internal static bool TryRegister(string email, string password, out string error)
    {
        error = "";
        email = (email ?? "").Trim();
        if (!IsValidEmail(email)) { error = "Email မှန်မှန် ထည့်ပါ (ဥပမာ user@gmail.com)"; return false; }
        if (string.IsNullOrWhiteSpace(password) || password.Length < 4)
        {
            error = "Password အနည်းဆုံး 4 လုံး ထည့်ပါ";
            return false;
        }
        if (HasAccount())
        {
            error = "Account ရှိပြီးသား — Login ဝင်ပါ သို့ Remove ပြီး Register ပြန်လုပ်ပါ";
            return false;
        }

        string salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var acc = new Account
        {
            Email = email,
            Salt = salt,
            PassHash = HashPassword(password, salt)
        };
        File.WriteAllText(AccountPath, JsonSerializer.Serialize(acc, new JsonSerializerOptions { WriteIndented = true }));
        return true;
    }

    internal static bool TryLogin(string email, string password, bool remember, out string error)
    {
        error = "";
        email = (email ?? "").Trim();
        if (!HasAccount()) { error = "Account မရှိသေး — Register Here ကနေ အရင်ဖွင့်ပါ"; return false; }
        Account? acc;
        try { acc = JsonSerializer.Deserialize<Account>(File.ReadAllText(AccountPath)); }
        catch { error = "Account file ဖတ်မရပါ"; return false; }
        if (acc == null || string.IsNullOrEmpty(acc.Email)) { error = "Account file ပျက်နေသည်"; return false; }

        if (!string.Equals(acc.Email, email, StringComparison.OrdinalIgnoreCase))
        {
            error = "Email မမှန်ပါ";
            return false;
        }
        if (!string.Equals(acc.PassHash, HashPassword(password ?? "", acc.Salt), StringComparison.Ordinal))
        {
            error = "Password မမှန်ပါ";
            return false;
        }

        if (remember) SaveRemembered(email, password ?? "");
        else ClearRemembered();
        return true;
    }

    internal static bool TryAutoLogin(out string email)
    {
        email = "";
        try
        {
            if (!TryRemembered(out email, out string password)) return false;
            // server check is caller's job (LicenseClient)
            return !string.IsNullOrEmpty(email) && !string.IsNullOrEmpty(password);
        }
        catch { ClearRemembered(); return false; }
    }

    // Remember-me blob (DPAPI) → email + password (LicenseClient online check အတွက်)
    internal static bool TryRemembered(out string email, out string password)
    {
        email = "";
        password = "";
        try
        {
            if (!File.Exists(SessionPath)) return false;
            string plain = Encoding.UTF8.GetString(ProtectedData.Unprotect(
                File.ReadAllBytes(SessionPath), null, DataProtectionScope.CurrentUser));
            int nl = plain.IndexOf('\n');
            if (nl <= 0) return false;
            email = plain[..nl];
            password = plain[(nl + 1)..];
            return email.Length > 0 && password.Length > 0;
        }
        catch { ClearRemembered(); return false; }
    }

    internal static void SaveRemembered(string email, string password)
    {
        string plain = email.Trim() + "\n" + password;
        byte[] blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(SessionPath, blob);
    }

    internal static void ClearRemembered()
    {
        try { if (File.Exists(SessionPath)) File.Delete(SessionPath); } catch { }
    }

    internal static string? SavedRememberedEmail()
    {
        return TryRemembered(out string email, out _) ? email : null;
    }

    private static string HashPassword(string password, string salt)
    {
        byte[] data = Encoding.UTF8.GetBytes(salt + ":" + password);
        return Convert.ToHexString(SHA256.HashData(data));
    }

    internal static bool IsValidEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;
        int at = email.IndexOf('@');
        return at > 0 && at < email.Length - 1 && email.IndexOf('.', at) > at + 1;
    }
}
