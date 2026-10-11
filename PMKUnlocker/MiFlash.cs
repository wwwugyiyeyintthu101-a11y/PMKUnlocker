using System.Text.RegularExpressions;

namespace PMKUnlocker;

// MST style "XIAOMI FASTBOOT FLASHING" — flash_all.bat / flash_all_lock.bat parser.
// bat ထဲက fastboot command တွေကို op list အဖြစ် ပြောင်းပြီး run
// (flash / erase / reboot / set_active / raw) — echo/rem/labels/timeout line တွေ ကျော်တယ်။
internal sealed class MiFlashOp
{
    public string Kind = "";            // flash | erase | reboot | set_active | raw
    public string Partition = "";       // flash / erase
    public string File = "";            // flash: absolute image path
    public string Raw = "";             // set_active / raw: fastboot args (verb အပါအဝင်)
    public string Cond = "";            // bat guard: "partition-type:NAME" | "crc" — device မပြရင် ကျော်
    public string ExpectedProduct = "";
    public string ExpectedAnti = "";
    public string Options = "";

    public string Label => Kind == "flash" ? Path.GetFileName(File) :
        Kind == "erase" ? "erase " + Partition :
        Kind == "reboot" ? "reboot" :
        Kind == "set_active" ? Raw : "fastboot " + Raw;
}

internal static class MiFlash
{
    // MST erase-first list — flash မတိုင်ခင် ဖျက်
    // (missing partition ကို Failed ပြပြီး ဆက်တယ် — MST log အတိုင်း)
    internal static readonly string[] EraseFirst = { "opcust", "opconfig", "boot_a", "boot_b", "expdb", "metadata" };

    internal static string? FindFlashAllBat(string folder)
    {
        try
        {
            foreach (string name in new[] { "flash_all.bat", "flash_all_lock.bat", "flash_all_except_storage.bat" })
            {
                string p = Path.Combine(folder, name);
                if (File.Exists(p)) return Path.GetFullPath(p);
            }
            foreach (string p in Directory.GetFiles(folder, "*.bat"))
            {
                try
                {
                    if (File.ReadAllText(p).Contains("fastboot")) return Path.GetFullPath(p);
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    internal static List<MiFlashOp> ParseBat(string batPath) =>
        ParseLines(File.ReadAllLines(batPath), Path.GetDirectoryName(Path.GetFullPath(batPath)) ?? "");

    internal static List<MiFlashOp> ParseLines(IEnumerable<string> lines, string baseDir)
    {
        var ops = new List<MiFlashOp>();
        string fastbootVar = "";
        string condVar = "";
        string expectedProduct = "", expectedAnti = "";
        bool inBlock = false;
        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("@")) { line = line.Substring(1).TrimStart(); if (line.Length == 0) continue; }
            if (line == ")")
            {
                if (!inBlock) throw new InvalidDataException("Unmatched BAT block end.");
                inBlock = false; condVar = ""; continue;
            }
            if (Regex.IsMatch(line, @"^(rem\b|::|echo\b|color\b|timeout\b|pause\b)", RegexOptions.IgnoreCase)) continue;
            if (Regex.IsMatch(line, @"^if\s+%errorlevel%\s+equ\s+0\s*\($", RegexOptions.IgnoreCase) && condVar.Length > 0 && !inBlock)
            { inBlock = true; continue; }

            Match setM = Regex.Match(line, @"^set\s+""?FASTBOOT\s*=\s*([^""]*)""?\s*$", RegexOptions.IgnoreCase);
            if (setM.Success) { fastbootVar = setM.Groups[1].Value.Trim(); continue; }

            // `fastboot args` | `fastboot.exe args` | `"...\fastboot.exe" args`
            Match fm = Regex.Match(line, @"^""[^""]*fastboot\.exe""\s+(.*)$", RegexOptions.IgnoreCase);
            if (!fm.Success) fm = Regex.Match(line, @"^fastboot(?:\.exe)?\s+(.*)$", RegexOptions.IgnoreCase);
            if (!fm.Success) throw new InvalidDataException("Unsupported BAT control flow; no commands will run: " + line);

            string args = fm.Groups[1].Value;
            // Only known failure-reporting tails are safe to omit: host execution
            // always aborts on a failed command. Never discard another command.
            string tailCheck = Regex.Replace(args, @"2(?:\^?>)\s*\^?&\s*1", " ");
            int failureTail = tailCheck.IndexOf("||", StringComparison.Ordinal);
            if (failureTail >= 0)
            {
                string tail = tailCheck.Substring(failureTail + 2).Trim();
                if (!Regex.IsMatch(tail, @"^@?echo\s+[^&|<>]*(?:&&\s*exit\s+/B\s+1)?$", RegexOptions.IgnoreCase))
                    throw new InvalidDataException("Unsupported BAT failure control flow.");
                tailCheck = tailCheck.Substring(0, failureTail);
            }
            if (!Regex.IsMatch(args, @"\bgetvar\b", RegexOptions.IgnoreCase) && tailCheck.IndexOfAny(new[] { '&', '|', '<', '>' }) >= 0)
                throw new InvalidDataException("Unsupported chained BAT command.");
            if (args.Contains("getvar product", StringComparison.OrdinalIgnoreCase) ||
                args.Contains("getvar anti", StringComparison.OrdinalIgnoreCase))
            {
                bool product = args.Contains("getvar product", StringComparison.OrdinalIgnoreCase);
                string key = product ? "product" : "anti";
                // Support only a literal findstr equality guard, not arbitrary batch expressions.
                var guard = Regex.Match(args, @"\|\s*findstr\s+/r\s+/c:""\^?" + key + @":\s*\*?([A-Za-z0-9_.-]+)\$?""", RegexOptions.IgnoreCase);
                if (!guard.Success || inBlock) throw new InvalidDataException("Unsupported " + key + " guard. Use the vendor's supported flashing workflow.");
                string value = guard.Groups[1].Value;
                if (!product && !uint.TryParse(value, out _)) throw new InvalidDataException("Invalid rollback guard.");
                if (product)
                {
                    if (expectedProduct.Length > 0 && expectedProduct != value) throw new InvalidDataException("Conflicting product guards.");
                    expectedProduct = value;
                }
                else
                {
                    if (expectedAnti.Length > 0 && expectedAnti != value) throw new InvalidDataException("Conflicting rollback guards.");
                    expectedAnti = value;
                }
                continue;
            }
            if (Regex.IsMatch(args, @"\b(getvar\s+anti|CURRENT_ANTI_VER|ANTI_VER)\b", RegexOptions.IgnoreCase))
                throw new InvalidDataException("Unsupported rollback check; flashing blocked.");
            // pipe / conditional tail တွေကို ဖြုတ် — getvar guard (| findstr), || @echo, && exit /B 1
            int cut = args.IndexOf("||", StringComparison.Ordinal);
            if (cut >= 0) args = args.Substring(0, cut);
            cut = args.IndexOf('|');
            if (cut >= 0) args = args.Substring(0, cut);
            cut = args.IndexOf("&&", StringComparison.Ordinal);
            if (cut >= 0) args = args.Substring(0, cut);
            args = Regex.Replace(args, @"2>\s*&\s*1", " ");
            args = Regex.Replace(args, @"2\^>\^&1", " ");
            args = Regex.Replace(args, "%FASTBOOT%", fastbootVar, RegexOptions.IgnoreCase);
            args = Regex.Replace(args, "%~dp0", baseDir.TrimEnd('\\') + "\\", RegexOptions.IgnoreCase);
            args = args.Trim();
            if (args.Length == 0) continue;

            List<string> tokens = Tokenize(args);
            if (tokens.Count == 0) continue;

            // %* / %VAR% placeholder နဲ့ option tokens (-s X / --disable-verity ...) ကို ကျော်ပြီး verb ရှာ
            int vi = 0;
            var options = new List<string>();
            while (vi < tokens.Count)
            {
                string t = tokens[vi];
                if (t.StartsWith("--set-active", StringComparison.OrdinalIgnoreCase)) break;
                if (t == "%*") { vi++; continue; }
                if (t.Contains('%')) throw new InvalidDataException("Unresolved BAT variable: " + t);
                if (t.StartsWith("-"))
                {
                    if (!Regex.IsMatch(t, @"^--slot=(all|a|b)$", RegexOptions.IgnoreCase))
                        throw new InvalidDataException("Unsupported fastboot option: " + t);
                    options.Add(t); vi++;
                    continue;
                }
                break;
            }
            if (vi >= tokens.Count) continue;
            string verb = tokens[vi].ToLowerInvariant();
            string rest = string.Join(" ", tokens.Skip(vi));

            if (verb == "flash" && tokens.Count > vi + 2)
            {
                // file path ထဲမှာ space ပါနိုင်တဲ့အတွက် remaining tokens အားလုံး join
                string fileRaw = string.Join(" ", tokens.Skip(vi + 2));
                string file = fileRaw.Trim().Trim('"').Trim();
                if (!Regex.IsMatch(tokens[vi + 1], @"^[A-Za-z0-9_-]+$") || file.IndexOfAny(new[] { '%', '|', '&', '<', '>', '\r', '\n', '"' }) >= 0)
                    throw new InvalidDataException("Unsupported image or partition name.");
                if (file.Length > 0 && !Path.IsPathRooted(file)) file = Path.Combine(baseDir, file);
                file = Path.GetFullPath(file);
                if (!file.StartsWith(Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Image escapes firmware directory.");
                ops.Add(new MiFlashOp { Kind = "flash", Partition = tokens[vi + 1], File = file, Cond = condVar });
            }
            else if (verb == "erase" && tokens.Count > vi + 1)
            {
                if (tokens.Count != vi + 2 || !Regex.IsMatch(tokens[vi + 1], @"^[A-Za-z0-9_-]+$")) throw new InvalidDataException("Invalid erase command.");
                ops.Add(new MiFlashOp { Kind = "erase", Partition = tokens[vi + 1], Cond = condVar });
            }
            else if (verb == "reboot" && tokens.Count == vi + 1)
            {
                ops.Add(new MiFlashOp { Kind = "reboot", Raw = rest, Cond = condVar });
            }
            else if (verb == "set_active" || verb.StartsWith("--set-active"))
            {
                if (!Regex.IsMatch(rest, @"^(set_active [ab]|--set-active=[ab])$", RegexOptions.IgnoreCase)) throw new InvalidDataException("Unsupported slot command.");
                ops.Add(new MiFlashOp { Kind = "set_active", Raw = rest, Cond = condVar });
            }
            else if (verb == "getvar")
            {
                // guard ခေါင်းစဉ် — နောက် if-block ထဲက op တွေအတွက် condition သတ်မှတ်
                string g = tokens.Count > vi + 1 ? tokens[vi + 1] : "";
                if (g.StartsWith("partition-type:", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Regex.IsMatch(g, @"^partition-type:[A-Za-z0-9_-]+$") || !line.Contains(g + ": raw", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Unsupported partition guard.");
                    condVar = g;
                }
                else if (g.Equals("crc", StringComparison.OrdinalIgnoreCase))
                {
                    if (!line.Contains("^crc: 1\"", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsupported CRC guard.");
                    condVar = "crc";
                }
                else
                    throw new InvalidDataException("Unsupported getvar guard: " + g);
                if (line.Contains("&& if %errorlevel% equ 0 (", StringComparison.OrdinalIgnoreCase)) inBlock = true;
                continue;           // table ထဲ မထည့်
            }
            else
            {
                if (rest != "oem cdms") throw new InvalidDataException("Unsupported fastboot operation: " + rest);
                ops.Add(new MiFlashOp { Kind = "raw", Raw = rest, Cond = condVar });
            }
            ops[^1].Options = string.Join(" ", options);
        }
        if (inBlock) throw new InvalidDataException("Unclosed BAT conditional block.");
        foreach (var op in ops) { op.ExpectedProduct = expectedProduct; op.ExpectedAnti = expectedAnti; }
        return ops;
    }

    internal static void ValidateDevice(IEnumerable<MiFlashOp> operations, string variables)
    {
        string Value(string key)
        {
            var match = Regex.Match(variables, @"(?im)^\s*(?:\(bootloader\)\s*)?" + Regex.Escape(key) + @":\s*(\S+)\s*$");
            return match.Success ? match.Groups[1].Value : "";
        }
        var ops = operations.ToList();
        if (ops.Count == 0) throw new InvalidDataException("No selected operations.");
        foreach (var op in ops)
        {
            if (op.ExpectedProduct.Length == 0 || !Value("product").Equals(op.ExpectedProduct, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Firmware product guard missing or device model mismatch; nothing written.");
            if (op.ExpectedAnti.Length > 0 && Value("anti") != op.ExpectedAnti)
                throw new InvalidDataException("Rollback guard mismatch; nothing written.");
        }
    }

    internal static bool GuardMatches(string condition, string variables) => condition.Length == 0 ||
        Regex.IsMatch(variables, @"(?im)^\s*(?:\(bootloader\)\s*)?" + Regex.Escape(condition) +
            (condition == "crc" ? @":\s*1\s*$" : @":\s*raw\s*$"));

    internal static Dictionary<string, string> ValidateImages(IEnumerable<MiFlashOp> operations)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var op in operations.Where(o => o.Kind == "flash"))
        {
            using var file = File.OpenRead(op.File);
            if (file.Length == 0) throw new InvalidDataException("Empty image: " + op.File);
            hashes[op.File] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file));
        }
        return hashes;
    }

    internal sealed class ImageLease : IDisposable
    {
        private readonly List<FileStream> streams = new();
        internal ImageLease(IEnumerable<MiFlashOp> operations)
        {
            try
            {
                foreach (string path in operations.Where(o => o.Kind == "flash").Select(o => o.File).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    streams.Add(stream);
                    if (stream.Length == 0) throw new InvalidDataException("Empty firmware image: " + path);
                    // Read every byte before any erase, and retain the handle to prevent changes.
                    System.Security.Cryptography.SHA256.HashData(stream);
                }
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() { foreach (var stream in streams) stream.Dispose(); streams.Clear(); }
    }

    private static List<string> Tokenize(string s)
    {
        var list = new List<string>();
        foreach (Match m in Regex.Matches(s, "\"[^\"]*\"|\\S+"))
            list.Add(m.Value);
        return list;
    }
}
