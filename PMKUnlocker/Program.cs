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
        static void Main()
        {
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

            // PMK-style login gate — remember-me token ရှိရင် အရင်ကြိုးစား၊ မရမှ login screen ပြ
            // (password ကို ဘယ်တော့မှ မသိမ်းဘူး — DPAPI-encrypted server token ပဲ ရှိတယ်)
            string licEmail = "", licPlan = "", licExp = "";
            int licDays = 0;
            bool signedIn = false;
            if (LocalLogin.TryRemembered(out string rememberedEmail, out _, out bool rememberedIsToken) && rememberedIsToken)
            {
                try
                {
                    // server နှေးရင် 5s ပဲ စောင့် — မရမှ login screen (login form က email ကို prefill လုပ်မယ်)
                    var auto = Task.Run(() => LicenseClient.TryAutoLoginAsync());
                    if (auto.Wait(TimeSpan.FromSeconds(5)) && auto.Result.Ok)
                    {
                        signedIn = true;
                        licEmail = rememberedEmail;
                        licPlan = auto.Result.Plan;
                        licExp = auto.Result.ExpiresAt;
                        licDays = auto.Result.DaysLeft;
                        StartupMark("token auto-login ok");
                    }
                    else StartupMark("token auto-login not accepted");
                }
                catch (Exception ex) { StartupMark("token auto-login error " + ex.GetType().Name); }
            }

            if (!signedIn)
            {
                using (var login = new LoginForm())
                {
                    StartupMark("login UI tree built");
                    if (login.ShowDialog() != DialogResult.OK) return;
                    licEmail = login.LoginEmail;
                    licPlan = login.LoginPlan;
                    licExp = login.LoginExpiresAt;
                    licDays = login.LoginDaysLeft;
                }
            }
            else if (!string.IsNullOrWhiteSpace(UpdateChecker.Repo))
            {
                // token auto-login က login screen ကို ကျော်တာမို့ update gate ကို ဒီမှာ ဖြတ်
                // (login form လမ်းကြောင်းဆို gate ကို login form ထဲမှာ ဖြတ်ပြီးသား)
                using var gateUi = new UpdateProgressForm();
                gateUi.Shown += async (_, _) =>
                {
                    await UpdateChecker.EnforceGateAsync(gateUi, gateUi.SetStatus, gateUi.SetProgress);
                    gateUi.DialogResult = DialogResult.OK;
                    gateUi.Close();
                };
                if (gateUi.ShowDialog() != DialogResult.OK) return;
                StartupMark("update gate passed");
            }

            Form1 main = new Form1(licEmail, licPlan, licExp, licDays);
            StartupMark("main form constructed");
            main.Shown += (_, _) => StartupMark("main window shown");
            Application.Run(main);

            // libusb-1.0 (the MTK raw-USB backend) runs a foreground event thread: once a native MTK session has
            // opened it, the window closes but the process would never exit. Measured on a probe, not assumed.
            MediaTek.MtkUsbTransport.ReleaseLibUsbSession();
        }

        // PMKLicenseServer — လက်ရှိ run နေရင် မထောင် (manual start ကို မထိ)၊ remote URL
        // ဆိုလည်း မထောင်။ ထောင်ရင် hidden (console window မပြ)။ tool ထွက်လည်း server
        // ဆက်ရှိ — နောက် tool ဖွင့်ရင် port ဖွင့်ထားလို့ ချက်ချင်း reuse။
        private static void EnsureLicenseServer()
        {
#if DEBUG
            try
            {
                string url = LicenseClient.BaseUrl;
                if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint) || !endpoint.IsLoopback) return;

                int port = 5260;
                var m = System.Text.RegularExpressions.Regex.Match(url, @":(\d+)");
                if (m.Success && int.TryParse(m.Groups[1].Value, out int p) && p > 0) port = p;

                if (IsPortOpen(port)) return;   // ဆွဲပြီးသား run နေ

                string baseDir = AppContext.BaseDirectory;
                string[] candidates =
                {
                    Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\PMKLicenseServer\bin\Release\net10.0\PMKLicenseServer.exe")),
                    Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\PMKLicenseServer\bin\Debug\net10.0\PMKLicenseServer.exe")),
                };
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
            }
            catch { }
#endif
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
