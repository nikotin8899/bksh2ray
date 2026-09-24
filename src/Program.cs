using System;
using System.Windows.Forms;

namespace bksh2ray
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                {
                    File.AppendAllText("crash.log", $"[AppDomain] {e.ExceptionObject}\n");
                };
                Application.ThreadException += (s, e) =>
                {
                    File.AppendAllText("crash.log", $"[Thread] {e.Exception}\n");
                };

                ApplicationConfiguration.Initialize();
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                File.AppendAllText("crash.log", $"[Main] {ex}\n");
                MessageBox.Show($"Ошибка запуска: {ex.Message}", "bksh2ray Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}