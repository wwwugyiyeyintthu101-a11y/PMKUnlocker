using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PMKUnlocker.MediaTek;

// All device commands run through this adapter. No shell, hidden helpers,
// global process cleanup, automatic erase, or retry of writes.
internal sealed partial class MtkClientEngine
{
    internal static string Executable => Path.Combine(AppContext.BaseDirectory, "tools", "mtkclient", "mtk.exe");
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly string sessionDirectory = Path.Combine(ShopServices.DataDirectory, "MediaTekSessions", Guid.NewGuid().ToString("N"));
    // multi ရဲ့ split(" ") က space မခံလို့ relative paths အတွက် caller က session dir ထဲ stage လုပ်တယ်
    internal string SessionDirectory => sessionDirectory;
    internal async Task<MtkCommandResult> RunAsync(MtkConnection connection, IEnumerable<string> command,
        Action<string> output, CancellationToken cancel, bool writes = false)
    {
        if (!await Gate.WaitAsync(0, cancel)) throw new InvalidOperationException("Another MediaTek command is running.");
        try
        {
            ToolIntegrity.Verify(Executable);
            using (var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "integrity.json"))))
            {
                foreach (var entry in manifest.RootElement.EnumerateArray())
                {
                    string key = entry.GetProperty("k").GetString() ?? "";
                    if (key.StartsWith("tools/mtkclient/", StringComparison.OrdinalIgnoreCase))
                        ToolIntegrity.Verify(Path.Combine(AppContext.BaseDirectory, key));
                }
            }
            var psi = new ProcessStartInfo(Executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = sessionDirectory
            };
            // argparse trap: subparsers with parents=[base] (w/e/r/reset/...) overwrite
            // top-level flags with their own defaults. "multi" has no [base], so it keeps
            // top-level flags. Base flags are therefore passed both before the subcommand
            // (for multi) and after the deepest subcommand token (for every other command).
            string[] cmdArgs = command as string[] ?? command.ToArray();
            bool isMulti = cmdArgs.Length > 0 &&
                string.Equals(cmdArgs[0], "multi", StringComparison.OrdinalIgnoreCase);
            // da subcommands (imei/seccfg/...) are two-level: both "da" and its subcommand
            // carry parents=[base], so base flags placed after "da" are overwritten by the
            // subcommand's own defaults — they must sit after the subcommand token instead.
            int baseFlagAt = BaseFlagInsertIndex(cmdArgs);
            AddFile("--loader", connection.Loader);
            AddFile("--auth", connection.Auth);
            AddFile("--preloader", connection.Preloader);
            for (int i = 0; i < cmdArgs.Length; i++)
            {
                if (i == baseFlagAt)
                {
                    AddFile("--loader", connection.Loader);
                    AddFile("--auth", connection.Auth);
                    AddFile("--preloader", connection.Preloader);
                }
                string arg = cmdArgs[i];
                // multi script (i==1) ထဲက csv partition lists ("e a,b,c") ကို ခွင့်ပြု —
                // comma block က file path args အတွက်ပဲ (path comma = argv split break)
                bool multiScriptArg = isMulti && i == 1;
                if (arg.IndexOfAny(new[] { '\r', '\n' }) >= 0 ||
                    (!multiScriptArg && !IsImeiWriteListArg(cmdArgs, i) && arg.IndexOf(',') >= 0))
                    throw new InvalidDataException("mtkclient does not support commas or line breaks in these file paths.");
                psi.ArgumentList.Add(arg);
            }
            void AddFile(string flag, string path)
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                if (!File.Exists(path)) throw new FileNotFoundException("Selected " + flag + " file is missing.", path);
                psi.ArgumentList.Add(flag); psi.ArgumentList.Add(Path.GetFullPath(path));
            }
            Directory.CreateDirectory(psi.WorkingDirectory);
            using var process = new Process { StartInfo = psi };
            var lines = new List<string>();
            object sync = new();
            bool daPhase = false; // writes: DA/boot/flash begun — never force-kill after this
            async Task Drain(StreamReader stream)
            {
                while (await stream.ReadLineAsync() is { } line)
                {
                    lock (sync)
                    {
                        lines.Add(line);
                        if (IsProtectedPhaseLine(line)) daPhase = true;
                        // raw transcript — display mapper ကျောက်ထားတဲ့ \r progress စတာတွေ ပြန်စစ်လို့ရအောင်
                        try { File.AppendAllText(Path.Combine(sessionDirectory, "last-run.raw.log"), line + "\n"); } catch { }
                    }
                    output(line);
                }
            }
            cancel.ThrowIfCancellationRequested();
            process.Start();
            Task stdout = Drain(process.StandardOutput), stderr = Drain(process.StandardError);
            if (!writes)
            {
                // Non-write ops: STOP kills immediately.
                try
                {
                    await process.WaitForExitAsync(cancel);
                }
                catch (OperationCanceledException)
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                    await Task.WhenAll(stdout, stderr);
                    throw;
                }
            }
            else
            {
                // Writes: STOP works only while the device is still being searched/handshaked
                // (nothing attached, nothing written). Once DA/boot/flash lines appear,
                // the process runs to completion — mid-write kills corrupt partitions.
                using var stopGate = new CancellationTokenSource();
                var watch = Task.Run(async () =>
                {
                    while (true)
                    {
                        try
                        {
                            if (process.HasExited) return;
                            bool protectedPhase;
                            lock (sync) protectedPhase = daPhase;
                            if (protectedPhase) return;
                            if (cancel.IsCancellationRequested) { stopGate.Cancel(); return; }
                        }
                        catch (Exception) { return; }
                        try { await Task.Delay(250); } catch (Exception) { return; }
                    }
                });
                try
                {
                    await process.WaitForExitAsync(stopGate.Token);
                }
                catch (OperationCanceledException)
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                    await Task.WhenAll(stdout, stderr);
                    throw;
                }
            }
            await Task.WhenAll(stdout, stderr);
            return new MtkCommandResult(process.ExitCode, lines);
        }
        finally { Gate.Release(); }

        static bool IsProtectedPhaseLine(string line) =>
            line.Contains("Dumping Preloader", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Loading Download-Agent", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Sending Download-Agent", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Checking Download-Agent", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Booting device into", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Executing Mediatek BROM Exploit", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Sending payload", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Wrote ", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Formatted", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Powering off", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Reset command", StringComparison.OrdinalIgnoreCase);
    }

}
