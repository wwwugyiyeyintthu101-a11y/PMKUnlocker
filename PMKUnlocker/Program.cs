using System.Diagnostics;
using System.Net.Sockets;

namespace PMKUnlocker
{
    internal static class Program
    {
        private static readonly string CrashLogPath =
            ShopServices.DataPath("pmk_crash.log");

        [STAThread]
        static void Main()
        {
            Application.ThreadException += (s, e) => LogCrash(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                LogCrash(e.ExceptionObject as Exception);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            ApplicationConfiguration.Initialize();

            // License server ကို tool ကိုယ်တိုင် နောက်ကွယ်က auto-start —
            // login က HTTP နဲ့ 127.0.0.1:5260 ကို စစ်တာမို့ ဖွင့်ချိန်မတိုင်ခင် server အဆင်သင့်ရမယ်
            EnsureLicenseServer();

            // PMK-style login gate — every launch shows login; Cancel/✕ ဆို app မဖွင့်ဘဲ ထွက်
            string licEmail = "", licPlan = "", licExp = "";
            int licDays = 0;
            using (var login = new LoginForm())
            {
                if (login.ShowDialog() != DialogResult.OK) return;
                licEmail = login.LoginEmail;
                licPlan = login.LoginPlan;
                licExp = login.LoginExpiresAt;
                licDays = login.LoginDaysLeft;
            }

            Application.Run(new Form1(licEmail, licPlan, licExp, licDays));
        }

        // PMKLicenseServer — လက်ရှိ run နေရင် မထောင် (manual start ကို မထိ)၊ remote URL
        // ဆိုလည်း မထောင်။ ထောင်ရင် hidden (console window မပြ)။ tool ထွက်လည်း server
        // ဆက်ရှိ — နောက် tool ဖွင့်ရင် port ဖွင့်ထားလို့ ချက်ချင်း reuse။
        private static void EnsureLicenseServer()
        {
            try
            {
                string url = LicenseClient.BaseUrl;
                if (!url.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
                    !url.Contains("localhost", StringComparison.OrdinalIgnoreCase)) return;

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
