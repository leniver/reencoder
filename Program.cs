using System;
using System.Threading;
using System.Windows.Forms;

namespace Reencoder;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Single-instance guard (equivalent to flock on the lock file in the bash script).
        using var mutex = new Mutex(true, @"Global\Reencoder_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Another instance is already running.", "Reencoder",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());

        GC.KeepAlive(mutex);
    }
}
