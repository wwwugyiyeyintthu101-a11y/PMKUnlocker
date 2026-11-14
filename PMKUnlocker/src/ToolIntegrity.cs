using System.Text.Json;

namespace PMKUnlocker;

internal static class ToolIntegrity
{
    internal static void Verify(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("Tool path is empty.");
        string full = Path.GetFullPath(path);
        string root = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        // Explicit external tools remain operator-managed. Packaged files fail closed.
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;
        string key = Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "integrity.json")));
            var matches = doc.RootElement.EnumerateArray().Where(e =>
                string.Equals(e.GetProperty("k").GetString(), key, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("Missing or duplicate tool integrity entry: " + key);
            string expected = matches[0].GetProperty("h").GetString() ?? "";
            if (expected.Length != 64 || !string.Equals(ShopServices.Hash(full), expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Tool integrity check failed: " + key + ". Reinstall a verified package.");
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new InvalidDataException("Cannot verify bundled tool: " + key + ". Reinstall a verified package.", ex);
        }
    }
}
