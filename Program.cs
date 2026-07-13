using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;
using CodexUsageMonitor.UI;

namespace CodexUsageMonitor
{
    internal static class Program
    {
        private const string ExitEventName = @"Global\CodexUsageMonitor.Exit";
        private const string MutexName = @"Global\CodexUsageMonitor.SingleInstance";

        [STAThread]
        private static void Main(string[] args)
        {
            if (HasExitArg(args))
            {
                SignalExit();
                return;
            }

            using (var mutex = new Mutex(true, MutexName, out var created))
            {
                if (!created)
                {
                    SignalExit();
                    return;
                }

                CloseOtherInstances();

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new OverlayForm());
            }
        }

        private static bool HasExitArg(string[] args)
        {
            if (args == null)
                return false;

            foreach (var arg in args)
            {
                if (string.Equals(arg, "--exit", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "/exit", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static void SignalExit()
        {
            try
            {
                using (var exitEvent = EventWaitHandle.OpenExisting(ExitEventName))
                    exitEvent.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // widget not running
            }
        }

        private static void CloseOtherInstances()
        {
            SignalExit();
            Thread.Sleep(400);

            var currentId = Process.GetCurrentProcess().Id;
            foreach (var process in Process.GetProcessesByName("CodexUsageMonitor"))
            {
                if (process.Id == currentId)
                    continue;

                try
                {
                    if (process.MainWindowHandle != IntPtr.Zero)
                        process.CloseMainWindow();

                    if (!process.WaitForExit(500))
                        process.Kill();
                }
                catch
                {
                    // ignore processes we cannot terminate
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
    }
}
