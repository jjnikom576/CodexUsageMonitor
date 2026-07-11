using System;
using System.Windows.Forms;
using CodexUsageMonitor.UI;

namespace CodexUsageMonitor
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new OverlayForm());
        }
    }
}
