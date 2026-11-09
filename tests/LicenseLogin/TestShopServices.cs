namespace PMKUnlocker;

// Test seam for LicenseClient/LocalLogin: the real ShopServices resolves %LOCALAPPDATA%\PMKMobileTool,
// so running the login-flow checks must never touch the operator's account or session files.
internal static class ShopServices
{
    internal static string? DataRootOverride;

    internal static string DataPath(string name)
    {
        string root = DataRootOverride ?? throw new InvalidOperationException("Test data root is not set.");
        Directory.CreateDirectory(root);
        return Path.Combine(root, name);
    }
}
