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

            // MST-style login gate — every launch shows login; Cancel/✕ ဆို app မဖွင့်ဘဲ ထွက်
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
