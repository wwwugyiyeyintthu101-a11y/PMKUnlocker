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
}

sealed class LoginRes
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public string Plan { get; set; } = "";
    public string ExpiresAt { get; set; } = "";
    public int DaysLeft { get; set; }
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
