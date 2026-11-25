using System.Diagnostics;
using System.Net.Sockets;

namespace PMKUnlocker
{
    internal static class Program
    {
        private static readonly string CrashLogPath =
            ShopServices.DataPath("pmk_crash.log");

        // Startup probe: "the tool is slow to open" cannot be fixed from a feel, and the phases it could be —
        // runtime start, the login UI tree, the first paint of the card, the main window build — are seconds
        // apart or milliseconds apart depending on which one it is. One line each, elapsed from process entry.
        private static readonly Stopwatch StartupClock = Stopwatch.StartNew();

        internal static void StartupMark(string phase)
        {
            try
            {
                File.AppendAllText(ShopServices.DataPath("pmk_startup.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  +" + StartupClock.ElapsedMilliseconds + " ms  " +
                    phase + Environment.NewLine);
            }
            catch { }   // a diagnostic that throws must never take the tool down
        }

        [STAThread]
        static int Main(string[] args)
        {
            // A packaged folder is checked without opening a window or touching a phone — see SelfCheck.
            // It runs before the login gate on purpose: a broken package should say so, not ask for a password.
            if (args != null && args.Any(a => string.Equals(a, SelfCheck.Flag, StringComparison.OrdinalIgnoreCase)))
                return SelfCheck.Run(args);

            StartupMark("process entered");
            Application.ThreadException += (s, e) => LogCrash(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                LogCrash(e.ExceptionObject as Exception);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            ApplicationConfiguration.Initialize();
            StartupMark("winforms configured");

            // License server ကို tool ကိုယ်တိုင် နောက်ကွယ်က auto-start —
            // login က HTTP နဲ့ 127.0.0.1:5260 ကို စစ်တာမို့ ဖွင့်ချိန်မတိုင်ခင် server အဆင်သင့်ရမယ်
            EnsureLicenseServer();
            StartupMark("license server gate passed");

            // PMK-style login gate — login screen ကို အမြဲပြ (operator request)။ Remember-me token
            // က login screen ကို မကျော်တော့ဘူး — Remember me က password prefill အတွက်သာ။ (password ကို
            // DPAPI-encrypted နဲ့သာ မြင်နိုင် — Program ထဲ ဘယ်တော့မှ ဖတ်မထား)
            string licEmail = "", licPlan = "", licExp = "";
            int licDays = 0;
            using (var login = new LoginForm())
            {
                StartupMark("login UI tree built");
                if (login.ShowDialog() != DialogResult.OK) return 0;
                licEmail = login.LoginEmail;
                licPlan = login.LoginPlan;
                licExp = login.LoginExpiresAt;
                licDays = login.LoginDaysLeft;
            }

            Form1 main = new Form1(licEmail, licPlan, licExp, licDays);
            StartupMark("main form constructed");
            main.Shown += (_, _) => StartupMark("main window shown");
            Application.Run(main);

            // libusb-1.0 (the MTK raw-USB backend) runs a foreground event thread: once a native MTK session has
            // opened it, the window closes but the process would never exit. Measured on a probe, not assumed.
            MediaTek.MtkUsbTransport.ReleaseLibUsbSession();
            return 0;
        }

        // PMKLicenseServer — လက်ရှိ run နေရင် မထောင် (manual start ကို မထိ)၊ remote URL
        // ဆိုလည်း မထောင်။ ထောင်ရင် hidden (console window မပြ)။ tool ထွက်လည်း server
        // ဆက်ရှိ — နောက် tool ဖွင့်ရင် port ဖွင့်ထားလို့ ချက်ချင်း reuse။
        private static void EnsureLicenseServer()
        {
            try
            {
                string url = LicenseClient.BaseUrl;
                if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint) || !endpoint.IsLoopback) return;

                int port = 5260;
                var m = System.Text.RegularExpressions.Regex.Match(url, @":(\d+)");
                if (m.Success && int.TryParse(m.Groups[1].Value, out int p) && p > 0) port = p;

                if (IsPortOpen(port)) return;   // ဆွဲပြီးသား run နေ

                // Release: server ကို tool ဖိုလ်ဒါထဲ (သို့) server\ အောက်မှာ ထည့်ထားတာ ရှာတယ်
                string baseDir = AppContext.BaseDirectory;
                var candidates = new List<string>
                {
                    Path.Combine(baseDir, "PMKLicenseServer.exe"),
                    Path.Combine(baseDir, "server", "PMKLicenseServer.exe"),
                };
#if DEBUG
                candidates.Add(Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\PMKLicenseServer\bin\Release\net10.0\PMKLicenseServer.exe")));
                candidates.Add(Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\PMKLicenseServer\bin\Debug\net10.0\PMKLicenseServer.exe")));
#endif
                string? exe = null;
                foreach (string c in candidates) if (File.Exists(c)) { exe = c; break; }
                if (exe == null) return;   // server မရှိ → login က offline path ကို သွားမယ်

                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = Path.GetDirectoryName(exe),
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                // ASP.NET startup ~1s — login ရဲ့ 8s HTTP timeout မတိုင်ခင် port တက်အောင် 3s ထိ စောင့်
                for (int i = 0; i < 30 && !IsPortOpen(port); i++) Thread.Sleep(100);

                // Server ဖိုင်ရှိပေမဲ့ port မတက်ရင် အကြောင်းရင်းကို startup log မှာ ချရေးတယ် —
                // ဒါမရှိရင် login ကျရုံနဲ့ ဘာကြောင့်လဲ မသိနိုင်ဘူး (များသောအားဖြင့် DataFile က ရေးလို့မရတဲ့
                // folder ဖြစ်နေတာ၊ ဒါမှမဟုတ် server ဖိုင် မပြည့်စုံတာ)။
                if (!IsPortOpen(port))
                    StartupMark("license server started but port " + port + " did not open (" + exe +
                                ") - check that server\\appsettings.json names a writable data folder");
            }
            catch (Exception ex)
            {
                StartupMark("license server start failed: " + ex.Message);
            }
        }

        private static bool IsPortOpen(int port)
        {
            try
            {
                using var c = new TcpClient();
                var ar = c.BeginConnect("127.0.0.1", port, null, null);
                bool ok = ar.AsyncWaitHandle.WaitOne(200, false);
                if (ok) c.EndConnect(ar);
                return ok;
            }
            catch { return false; }
        }

        private static void LogCrash(Exception? ex)
        {
            try
            {
                File.AppendAllText(CrashLogPath,
                    "=== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===" + Environment.NewLine +
                    ex?.ToString() + Environment.NewLine + Environment.NewLine);
            }
            catch { }
            MessageBox.Show("Unexpected error:\n" + ex?.Message,
                "PMK Tool", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
