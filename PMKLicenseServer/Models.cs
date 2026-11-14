namespace PMKLicenseServer;

sealed class LicenseDb
{
    public List<Account> Accounts { get; set; } = new();
}

sealed class Account
{
    public string Email { get; set; } = "";
    public string PassHash { get; set; } = "";
    public string Salt { get; set; } = "";
    public string Plan { get; set; } = "monthly";
    public string ExpiresAt { get; set; } = "";
    public bool Blocked { get; set; }
    public bool Pending { get; set; } // self-register → admin approve မှ active
    public string Note { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    // Remember-me auto-login token (password ကို client မှာ မသိမ်းတော့ပါ)
    public string AutoToken { get; set; } = "";
    public string AutoTokenAt { get; set; } = "";
    // Password-reset token — SHA-256 hash only (link ထဲက token ကို ဖိုင်ထဲ မသိမ်း)
    public string ResetToken { get; set; } = "";
    public string ResetTokenAt { get; set; } = "";
}

sealed class ForgotReq
{
    public string? Email { get; set; }
}

sealed class ResetReq
{
    public string? Token { get; set; }
    public string? Password { get; set; }
}

sealed class ResetLinkReq
{
    public string? Email { get; set; }
}

// Optional mailer: smtp.json ကို licenses.json ဘေးမှာ ထားမှ password-reset email ပို့မယ်
sealed class SmtpConfig
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool UseStartTls { get; set; } = true;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string From { get; set; } = "";
    public string PublicUrl { get; set; } = "";
}

sealed class RegisterReq
{
    public string? Email { get; set; }
    public string? Password { get; set; }
}

sealed class ApproveReq
{
    public string? Email { get; set; }
    public string Plan { get; set; } = "monthly";
}

sealed class LoginReq
{
    public string? Email { get; set; }
    public string? Password { get; set; }
    public string? Token { get; set; }   // remember-me auto-login (password အစား)
    public bool Remember { get; set; }   // password login + remember → auto-token ပြန်ထုတ်
}

sealed class LoginRes
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public string Plan { get; set; } = "";
    public string ExpiresAt { get; set; } = "";
    public int DaysLeft { get; set; }
    public string AutoToken { get; set; } = ""; // remember=true ဖြင့် password login အောင်မြင်ရင်
}

sealed class UpsertReq
{
    public string? Email { get; set; }
    public string? Password { get; set; }
    public string Plan { get; set; } = "monthly";
    public string? Note { get; set; }
}

sealed class RenewReq
{
    public string? Email { get; set; }
    public int Months { get; set; }
}

sealed class BlockReq
{
    public string? Email { get; set; }
    public bool Blocked { get; set; }
}

sealed class AdminRow
{
    public string Email { get; set; } = "";
    public string Plan { get; set; } = "";
    public string ExpiresAt { get; set; } = "";
    public bool Blocked { get; set; }
    public bool Pending { get; set; }
    public string Note { get; set; } = "";
    public bool Expired { get; set; }
    public int DaysLeft { get; set; }
}
