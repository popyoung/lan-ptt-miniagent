using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using LanPttIntercom.Storage;

namespace LanPttIntercom;

/// <summary>
/// Application entry point. Configures global WinForms settings and starts
/// the tray application context.
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
            TryAttachParentConsole();
            RunSmokeTest();
            return;
        }

        Application.Run(new TrayApplicationContext());
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
        var context = new TrayApplicationContext();
        var form = context.Form;
        int exitCode = 0;
        var smokeTimer = new System.Windows.Forms.Timer { Interval = 100 };
        smokeTimer.Tick += (_, __) =>
        {
            smokeTimer.Stop();
            try
            {
                AssertSmoke(!form.ShowInTaskbar, "hidden startup ShowInTaskbar should be false");
                AssertSmoke(!form.Visible, "hidden startup Visible should be false");
                AssertSmoke(form.Handle != IntPtr.Zero, "hidden startup should have a non-zero HWND");
                AssertSmoke(!IsWindowVisible(form.Handle), "hidden startup HWND should not be OS-visible");

                form.WindowState = FormWindowState.Normal;
                form.Bounds = new Rectangle(-50000, -50000, Math.Max(form.Width, 760), Math.Max(form.Height, 780));
                form.WindowState = FormWindowState.Minimized;
                form.ShowMainWindow();
                AssertSmoke(form.Visible, "ShowMainWindow should make form visible");
                AssertSmoke(form.ShowInTaskbar, "ShowMainWindow should show taskbar button");
                AssertSmoke(IsWindowVisible(form.Handle), "ShowMainWindow HWND should be OS-visible");
                AssertSmoke(form.WindowState == FormWindowState.Normal, "normal restore should use WindowState.Normal");
                AssertSmoke(IntersectsAnyScreen(form.Bounds), "normal restore bounds should intersect a visible working area: " + form.Bounds);

                form.WindowState = FormWindowState.Normal;
                form.WindowState = FormWindowState.Maximized;
                AssertSmoke(form.CapturedRestoreWindowStateForSmokeTest == FormWindowState.Maximized,
                    "maximize should update captured restore state");
                form.Close();
                AssertSmoke(!form.Visible, "user close should hide form to tray");
                AssertSmoke(!form.ShowInTaskbar, "user close should remove taskbar button");
                AssertSmoke(form.Handle != IntPtr.Zero, "close-to-tray should keep a non-zero HWND");
                AssertSmoke(!IsWindowVisible(form.Handle), "close-to-tray HWND should not be OS-visible");
                AssertSmoke(form.CapturedRestoreWindowStateForSmokeTest == FormWindowState.Maximized,
                    "close-to-tray should preserve captured maximized restore state");
                form.ShowMainWindow();
                AssertSmoke(form.Visible, "maximized restore should make form visible");
                AssertSmoke(form.ShowInTaskbar, "maximized restore should show taskbar button");
                AssertSmoke(IsWindowVisible(form.Handle), "maximized restore HWND should be OS-visible");
                AssertSmoke(form.WindowState == FormWindowState.Maximized, "maximized restore should preserve WindowState.Maximized");

                form.ExitApplication();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("SMOKETEST LIFECYCLE FAILED: " + ex);
                exitCode = 1;
                Environment.ExitCode = 1;
                try { form.ExitApplication(); } catch { context.ExitThread(); }
            }
        };
        form.FormClosed += (_, __) =>
        {
            try { smokeTimer.Dispose(); } catch { /* ignore */ }
            context.ExitThread();
        };
        smokeTimer.Start();
        Application.Run(context);
        Environment.ExitCode = exitCode;
        if (exitCode != 0)
        {
            Environment.Exit(exitCode);
        }
    }

    private sealed class TrayApplicationContext : ApplicationContext
    {
        public MainForm Form { get; }

        public TrayApplicationContext()
        {
            Form = new MainForm();
            Form.FormClosed += (_, __) => ExitThread();
            Form.StartHiddenToTray();
        }
    }

    private static void AssertSmoke(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static bool IntersectsAnyScreen(Rectangle bounds)
    {
        foreach (var screen in Screen.AllScreens)
        {
            var intersection = Rectangle.Intersect(bounds, screen.WorkingArea);
            if (intersection.Width > 0 && intersection.Height > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static void TryAttachParentConsole()
    {
        try { AttachConsole(AttachParentProcess); } catch { /* smoke output remains best-effort for WinExe */ }
    }

    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);
}
