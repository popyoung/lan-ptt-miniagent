using System;
using System.Threading;
using System.Windows.Forms;
using LanPttIntercom.Storage;

namespace LanPttIntercom;

/// <summary>
/// Application entry point. Configures global WinForms settings and shows
/// <see cref="MainForm"/>.
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Capture audio + WinForms controls + UDP are all fine on a single STA thread;
        // long-running IO happens on background threads inside the audio/network classes.
        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        // Hidden smoke test mode used by build/CI to validate startup without
        // requiring an interactive desktop. Activated with --smoketest.
        if (Array.Exists(args, a => string.Equals(a, "--smoketest", StringComparison.OrdinalIgnoreCase)))
        {
            RunSmokeTest();
            return;
        }

        Application.Run(new MainForm());
    }

    private static void RunSmokeTest()
    {
        try
        {
            var store = new SettingsStore();
            store.Save(store.Load());
            Console.WriteLine("SMOKETEST SETTINGS PATH: " + store.FilePath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("SMOKETEST SETTINGS FAILED: " + ex);
            Environment.ExitCode = 1;
            return;
        }

        // We use the real message loop so the tray/startup path actually runs.
        // A timer exits through the same explicit-exit path as the tray menu,
        // avoiding the close-to-tray behavior used for normal window close.
        var form = new MainForm();
        int exitCode = 0;
        var closeTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        closeTimer.Tick += (_, __) =>
        {
            closeTimer.Stop();
            try
            {
                form.ExitApplication();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("SMOKETEST CLOSE FAILED: " + ex);
                exitCode = 1;
            }
        };
        form.FormClosed += (_, __) =>
        {
            try { closeTimer.Dispose(); } catch { /* ignore */ }
            Application.Exit();
        };
        closeTimer.Start();
        Application.Run(form);
        Environment.ExitCode = exitCode;
    }
}
