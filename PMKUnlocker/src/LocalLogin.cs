using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PMKUnlocker;

// Remember-me storage for the license sign-in. Passwords are never kept; the server token
// (or a legacy pre-token password blob) is encrypted with DPAPI for the current user.
internal static class LocalLogin
{
    private sealed class LegacyAccount
    {
        public string Email { get; set; } = "";
    }

    private static string AccountPath => ShopServices.DataPath("pmk_account.json");
    private static string SessionPath => ShopServices.DataPath("pmk_session.bin");

    // Email prefill only, from a legacy offline account file if one exists.
    internal static string? SavedEmail()
    {
        try
        {
            if (!File.Exists(AccountPath)) return null;
            var acc = JsonSerializer.Deserialize<LegacyAccount>(File.ReadAllText(AccountPath));
            return string.IsNullOrWhiteSpace(acc?.Email) ? null : acc.Email;
        }
        catch { return null; }
    }

    // Remember-me blob (DPAPI) → email + secret.
    // secret ပုံစံ: "2:<token>" (server ထုတ်သော auto-login token — လက်ရှိ) သို့ မဟုတ် password (ယခင် version)
    internal static bool TryRemembered(out string email, out string secret, out bool isToken)
    {
        email = "";
        secret = "";
        isToken = false;
        try
        {
            if (!File.Exists(SessionPath)) return false;
            string plain = Encoding.UTF8.GetString(ProtectedData.Unprotect(
                File.ReadAllBytes(SessionPath), null, DataProtectionScope.CurrentUser));
            int nl = plain.IndexOf('\n');
            if (nl <= 0) return false;
            email = plain[..nl];
            secret = plain[(nl + 1)..];
            if (secret.StartsWith("2:", StringComparison.Ordinal))
            {
                isToken = true;
                secret = secret[2..];
            }
            return email.Length > 0 && secret.Length > 0;
        }
        catch { ClearRemembered(); return false; }
    }

    // Password (legacy auto-login / local login) — အသစ်မှာ SaveRememberedToken သုံးပါ
    internal static void SaveRemembered(string email, string password)
    {
        string plain = email.Trim() + "\n" + password;
        byte[] blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(SessionPath, blob);
    }

    // လက်ရှိ remember-me — password မသိမ်းတော့ဘဲ server token ကိုပဲ DPAPI နဲ့သိမ်း
    internal static void SaveRememberedToken(string email, string token)
    {
        string plain = email.Trim() + "\n2:" + token;
        byte[] blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(SessionPath, blob);
    }

    internal static void ClearRemembered()
    {
        try { if (File.Exists(SessionPath)) File.Delete(SessionPath); } catch { }
    }

    internal static string? SavedRememberedEmail()
    {
        return TryRemembered(out string email, out _, out _) ? email : null;
    }

    internal static bool IsValidEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;
        int at = email.IndexOf('@');
        return at > 0 && at < email.Length - 1 && email.IndexOf('.', at) > at + 1;
    }
}
