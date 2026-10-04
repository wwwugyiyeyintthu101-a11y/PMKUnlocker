using System.Diagnostics;
using System.IO.Ports;
using System.Text;

namespace PMKUnlocker;

// Qualcomm EDL (9008) Firehose auth bypass — MiFlash NonAuth ရဲ့ sig command flow ကို
// System.IO.Ports နဲ့ native ပြန်ရေးတာ (external tool မသုံး)။
//
// Flow (COM port log အတိုင်း):
//   1) Sahara loader ပို့ပြီးမှ (QSaharaServer) firehose ချိတ်
//   2) nop/ping → configure
//   3) auth လိုရင် sig XML → "INFO: EDL Authenticated"
//   4) configure ပြန်ပို့ → ACK (TargetName) ရမှ success
internal static class EdlAuth
{
    internal sealed class Result
    {
        public bool Ok;
        public string Message = "";
        public string TargetName = "";
        public string MemoryName = "";
    }

    internal static async Task<Result> BypassAsync(
        string portName,
        string? loaderPath,
        Action<string>? log = null,
        CancellationToken ct = default,
        string? sigPath = null)
    {
        var result = new Result();
        void Say(string s) => log?.Invoke(s);

        if (string.IsNullOrEmpty(portName) || !portName.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
        {
            result.Message = "No COM port selected.";
            return result;
        }

        // Loader မရှိရင် firehose မဝင်နိုင် — Sahara ကနေ loader တင်ရမယ်
        if (!string.IsNullOrEmpty(loaderPath) && File.Exists(loaderPath))
        {
            Say("[*] Sahara: loading firehose programmer...");
            bool saharaOk = await RunSaharaAsync(portName, loaderPath, ct);
            if (!saharaOk)
            {
                result.Message = "Sahara loader upload failed.";
                return result;
            }
            Say("[+] Sahara loader transferred.");
            await Task.Delay(500, ct);
        }

        try
        {
            using var sp = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 2000,
                WriteTimeout = 2000
            };
            sp.Open();
            sp.DiscardInBuffer();
            sp.DiscardOutBuffer();

            // 1) ping / nop
            Say("[*] Firehose: ping...");
            string pingXml = "<?xml version=\"1.0\" ?><data><nop verbose=\"0\" value=\"ping\"/></data>";
            await WriteAsciiAsync(sp, pingXml, ct);
            string pingResp = await ReadUntilAsync(sp, new[] { "ACK", "NAK", "ERROR" }, 3000, ct);
            SayResp(pingResp, Say);
            if (pingResp.Contains("NAK") || pingResp.Contains("ERROR"))
            {
                // ping မအောင်မြင်လည်း configure/sig ဆက်ကြည့်ရမယ် (loader state မတူနိုင်)
                Say("[!] Ping NAK — continuing with configure/sig.");
            }
            else if (pingResp.Contains("ACK"))
            {
                Say("[+] Firehose ping ACK.");
            }

            // 2) configure — auth မတိုင်မီ ပထမ configure (MiFlash အတိုင်း)
            string configureXml =
                "<?xml version=\"1.0\" ?><data><configure verbose=\"0\" AlwaysValidate=\"0\" " +
                "ZlpAwareHost=\"1\" MaxPayloadSizeToTargetInBytes=\"131072\" " +
                "MemoryName=\"emmc\" SkipStorageInit=\"0\"/></data>";

            Say("[*] Firehose: configure...");
            await WriteAsciiAsync(sp, configureXml, ct);
            string cfgResp = await ReadUntilAsync(sp, new[] { "Authenticated", "ACK", "NAK", "ERROR" }, 4000, ct);
            SayResp(cfgResp, Say);

            bool alreadyAuth = cfgResp.Contains("Authenticated", StringComparison.OrdinalIgnoreCase)
                || (cfgResp.Contains("ACK") && cfgResp.Contains("TargetName"));

            if (!alreadyAuth)
            {
                // 3) Auth bypass — sig command + real SIG payload (zeros မနဲ့ — stat is 7)
                var sigCandidates = LoadSigCandidates(sigPath, Say);
                if (sigCandidates.Count == 0)
                {
                    result.Message = "No SIG file found — set SIG path or place SIG #1.bin.";
                    return result;
                }

                Say($"[*] Bypassing authentication ({sigCandidates.Count} SIG candidate(s))...");
                bool sigOk = false;
                string lastFail = "";

                foreach (var (sigName, sigPayload) in sigCandidates)
                {
                    ct.ThrowIfCancellationRequested();
                    Say("[*] Trying SIG: " + sigName + " (" + sigPayload.Length + " bytes)...");

                    await WriteAsciiAsync(sp, configureXml, ct);
                    string c = await ReadUntilAsync(sp, new[] { "Authenticated", "ACK", "NAK", "ERROR" }, 4000, ct);
                    if (c.Contains("Authenticated", StringComparison.OrdinalIgnoreCase)
                        || (c.Contains("ACK") && c.Contains("TargetName")))
                    {
                        Say("[+] Already authenticated before sig.");
                        cfgResp = c;
                        sigOk = true;
                        break;
                    }

                    string sigXml =
                        "<?xml version=\"1.0\" ?><data><sig TargetName=\"sig\" " +
                        "size_in_bytes=\"" + sigPayload.Length + "\" verbose=\"1\"/></data>";
                    await WriteAsciiAsync(sp, sigXml, ct);

                    string sigAck = await ReadUntilAsync(sp, new[] { "ACK", "NAK", "ERROR" }, 5000, ct);
                    SayResp(sigAck, Say);
                    if (!sigAck.Contains("ACK"))
                    {
                        lastFail = sigAck.Contains("NAK") ? "Sig command NAK before payload." : "Sig ACK timeout.";
                        try { sp.DiscardInBuffer(); } catch { }
                        continue;
                    }

                    await Task.Run(() => sp.Write(sigPayload, 0, sigPayload.Length), ct);

                    string authResp = await ReadUntilAsync(
                        sp,
                        new[] { "Authenticated", "NAK", "ERROR" },
                        10000,
                        ct);
                    SayResp(authResp, Say);

                    if (authResp.Contains("Authenticated", StringComparison.OrdinalIgnoreCase)
                        || (authResp.Contains("ACK") && !authResp.Contains("NAK")))
                    {
                        Say("[+] EDL Authenticated with " + sigName);
                        sigOk = true;
                        break;
                    }

                    lastFail = authResp.Contains("NAK")
                        ? "SIG rejected: " + TrimForLog(authResp)
                        : "Auth timeout after " + sigName;
                    Say("[!] " + lastFail);
                    try { sp.DiscardInBuffer(); } catch { }
                    await Task.Delay(400, ct);
                }

                if (!sigOk)
                {
                    result.Message = string.IsNullOrEmpty(lastFail) ? "All SIG candidates failed." : lastFail;
                    return result;
                }

                await Task.Delay(300, ct);
                try { sp.DiscardInBuffer(); } catch { }
            }

            // 4) Auth ပြီးမှ configure ပြန် — full ACK + TargetName ရမယ်
            // တစ်ခါ retry — post-auth configure မကြာခဏ no-response ဖြစ်နိုင်
            string cfg2 = "";
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                Say($"[*] Firehose: configure (post-auth, try {attempt})...");
                await WriteAsciiAsync(sp, configureXml, ct);
                cfg2 = await ReadUntilAsync(
                    sp,
                    new[] { "TargetName", "Authenticated", "NAK", "ERROR" },
                    5000,
                    ct);

                // TargetName မလာဘဲ ACK ပဲ ရင် — နောက်ထပ် buffer ဖတ် (log+ACK ခွဲလာနိုင်)
                if (!cfg2.Contains("TargetName") && cfg2.Contains("ACK"))
                {
                    string more = await ReadUntilAsync(sp, new[] { "TargetName", "NAK", "ERROR" }, 2000, ct);
                    cfg2 += more;
                }

                SayResp(cfg2, Say);
                if (cfg2.Contains("TargetName") || (cfg2.Contains("ACK") && !cfg2.Contains("NAK")))
                    break;
                if (attempt < 2) await Task.Delay(500, ct);
            }

            result.TargetName = ExtractAttr(cfg2, "TargetName");
            result.MemoryName = ExtractAttr(cfg2, "MemoryName");

            bool ready = cfg2.Contains("ACK") && !cfg2.Contains("NAK");
            if (ready)
            {
                result.Ok = true;
                result.Message = string.IsNullOrEmpty(result.TargetName)
                    ? "Firehose ready (ACK)."
                    : "Firehose ready — TargetName=" + result.TargetName;
                Say("[+] " + result.Message);
            }
            else if (cfg2.Contains("Authenticated", StringComparison.OrdinalIgnoreCase))
            {
                string cfg3 = await ReadUntilAsync(sp, new[] { "ACK", "NAK", "ERROR" }, 3000, ct);
                SayResp(cfg3, Say);
                result.Ok = cfg3.Contains("ACK");
                result.Message = result.Ok
                    ? "Firehose ready after auth."
                    : "Configure did not return ACK after auth.";
                if (result.Ok) Say("[+] " + result.Message);
            }
            else if (string.IsNullOrEmpty(result.Message))
            {
                result.Message = "Configure failed after auth attempt.";
            }

            sp.Close();
        }
        catch (UnauthorizedAccessException ex)
        {
            result.Message = "Port in use: " + ex.Message;
        }
        catch (Exception ex)
        {
            result.Message = ex.Message;
        }

        return result;
    }

    // Firehose raw XML response — success (ACK/Authenticated) ဆိုရင် log ရှင်းအောင် မပြတော့။
    // NAK/ERROR/response မရှိရင်ပဲ raw dump ပြ (debug အတွက်)။
    private static void SayResp(string resp, Action<string> say)
    {
        bool ok = (resp.Contains("ACK") || resp.Contains("Authenticated")) &&
                  !resp.Contains("NAK") && !resp.Contains("ERROR");
        if (ok) return;
        say("    " + (string.IsNullOrWhiteSpace(resp) ? "(no response)" : TrimForLog(resp)));
    }

    // QSaharaServer.exe — loader ကို device ထဲ တင် (Sahara imgID 13)
    private static async Task<bool> RunSaharaAsync(string portName, string loaderPath, CancellationToken ct)
    {
        try
        {
            string exe = ResolveSaharaExe();
            if (string.IsNullOrEmpty(exe))
                return false;

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"-p \\\\.\\{portName} -s 13:\"{loaderPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;

            // QSaharaServer hang (device drop/port error) အတွက် 90s cap —
            // timeout/STOP/external-cancel သုံးခုလုံးက proc ကို kill တယ် (မဟုတ်ရင် အမြဲတမ်း hang)
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(90));
            using var reg = cts.Token.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    // QSaharaServer.exe path — app base / tools.json / PATH မှာ ရှာ (bare name မသုံးတော့)
    private static string ResolveSaharaExe()
    {
        const string name = "QSaharaServer.exe";
        try
        {
            string configured = ShopServices.FindTool(AppContext.BaseDirectory, name);
            if (!string.IsNullOrEmpty(configured)) return configured;

            string local = Path.Combine(AppContext.BaseDirectory, name);
            if (File.Exists(local)) return local;

            foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(folder)) continue;
                    string candidate = Path.Combine(folder.Trim().Trim('"'), name);
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
                catch (ArgumentException) { }
            }
        }
        catch { }
        return name; // last resort — Process.Start will fail with clear error
    }

    // SIG file တွေ — preferred path ပထမ၊ ပြီးရင် Qualcomm-firehoses SIG #1 + QLM/*
    // (zeros payload = Signature Verification Failed, stat is 7)
    private static List<(string Name, byte[] Bytes)> LoadSigCandidates(string? preferredPath, Action<string> say)
    {
        var list = new List<(string Name, byte[] Bytes)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string path, string? label = null)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                byte[] raw = File.ReadAllBytes(path);
                if (raw.Length == 0) return;

                byte[] payload;
                if (raw.Length == 256)
                {
                    payload = raw;
                }
                else if (raw.Length > 256)
                {
                    payload = new byte[256];
                    Array.Copy(raw, payload, 256);
                    say("[!] " + Path.GetFileName(path) + " is " + raw.Length + "B — using first 256B.");
                }
                else
                {
                    payload = new byte[256];
                    Array.Copy(raw, payload, raw.Length);
                }

                string name = label ?? Path.GetFileName(path);
                if (seen.Add(name))
                    list.Add((name, payload));
            }
            catch
            {
                // unreadable SIG — skip
            }
        }

        if (!string.IsNullOrEmpty(preferredPath))
            TryAdd(preferredPath);

        // Known collections — app ဘေး / tools folder / user Downloads (hardcoded path မသုံးတော့)
        foreach (string root in EnumerateSigRoots())
        {
            TryAdd(Path.Combine(root, "SIG #1.bin"), "SIG #1");
            string qlm = Path.Combine(root, "QLM");
            if (Directory.Exists(qlm))
            {
                foreach (var dir in Directory.GetDirectories(qlm)
                             .OrderBy(d => int.TryParse(Path.GetFileName(d), out int n) ? n : int.MaxValue))
                {
                    foreach (var f in Directory.GetFiles(dir))
                        TryAdd(f, "QLM/" + Path.GetFileName(dir) + "/" + Path.GetFileName(f));
                }
            }
        }

        // App အနီး sig.bin (optional drop-in)
        try
        {
            string local = Path.Combine(AppContext.BaseDirectory, "sig.bin");
            TryAdd(local, "sig.bin");
        }
        catch { }

        if (list.Count == 0)
            say("[!] No SIG candidates found.");
        else
            say("[*] SIG candidates: " + string.Join(", ", list.Select(x => x.Name)));

        return list;
    }

    // SIG collection ရှာမည့် root များ — app base / tools / Downloads (အများပြည်သူ PC ပေါ် အလုပ်လုပ်ရန်)
    private static IEnumerable<string> EnumerateSigRoots()
    {
        var roots = new List<string>();
        void Add(string p)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p) && !roots.Contains(p, StringComparer.OrdinalIgnoreCase))
                    roots.Add(p);
            }
            catch { }
        }

        string baseDir = AppContext.BaseDirectory;
        Add(Path.Combine(baseDir, "sig"));
        Add(Path.Combine(baseDir, "SIG"));
        Add(Path.Combine(baseDir, "edl", "sig"));
        Add(Path.Combine(baseDir, "tools", "sig"));

        try
        {
            string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            foreach (string sub in new[] { "Qualcomm-firehoses", "Qualcomm-firehoses-main", "sig", "SIG" })
                Add(Path.Combine(downloads, sub));
            // nested Qualcomm-firehoses-main\...\Xiaomi\SIG's layout
            string nested = Path.Combine(downloads, "Qualcomm-firehoses-main", "Qualcomm-firehoses-main", "Xiaomi", "SIG's");
            Add(nested);
        }
        catch { }

        return roots;
    }

    private static async Task WriteAsciiAsync(SerialPort sp, string xml, CancellationToken ct)
    {
        byte[] data = Encoding.ASCII.GetBytes(xml);
        await Task.Run(() => sp.Write(data, 0, data.Length), ct);
    }

    // ရှာနေတဲ့ token တွေ သို့ timeout တိုင်အောင် buffer ဖတ်
    private static async Task<string> ReadUntilAsync(
        SerialPort sp,
        string[] stopTokens,
        int timeoutMs,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        var buf = new byte[4096];

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (sp.BytesToRead <= 0)
                {
                    await Task.Delay(30, ct);
                    continue;
                }

                int n = sp.Read(buf, 0, Math.Min(buf.Length, sp.BytesToRead));
                if (n > 0)
                {
                    // Firehose XML/ASCII ဟာ binary noise နဲ့ ရောနိုင် — printable ကိုသာ ဖမ်း
                    for (int i = 0; i < n; i++)
                    {
                        byte b = buf[i];
                        if (b >= 0x20 && b < 0x7F || b == (byte)'\r' || b == (byte)'\n' || b == (byte)'\t')
                            sb.Append((char)b);
                        else if (b == 0)
                            sb.Append(' ');
                    }

                    string cur = sb.ToString();
                    foreach (var tok in stopTokens)
                    {
                        if (cur.Contains(tok, StringComparison.OrdinalIgnoreCase))
                            return cur;
                    }
                }
            }
            catch (TimeoutException)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                break;
            }
        }

        return sb.ToString();
    }

    private static string ExtractAttr(string xml, string name)
    {
        try
        {
            int i = xml.IndexOf(name + "=\"", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return "";
            i += name.Length + 2;
            int j = xml.IndexOf('"', i);
            return j > i ? xml.Substring(i, j - i) : "";
        }
        catch
        {
            return "";
        }
    }

    private static string TrimForLog(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "(no response)";
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length > 220 ? s.Substring(0, 220) + "..." : s;
    }
}
