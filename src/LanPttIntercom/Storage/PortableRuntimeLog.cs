using System;
using System.IO;
using System.Text;

namespace LanPttIntercom.Storage;

/// <summary>
/// Best-effort runtime diagnostics. It only writes beside the executable and
/// never falls back to profile/AppData locations.
/// </summary>
public static class PortableRuntimeLog
{
    public const string FileName = "LanPttIntercom.log";

    public static string GetPath(string baseDirectory) => Path.Combine(baseDirectory, FileName);

    public static void Write(string baseDirectory, string message)
    {
        try
        {
            var path = GetPath(baseDirectory);
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message + Environment.NewLine;
            File.AppendAllText(path, line, Encoding.UTF8);
        }
        catch
        {
            // The UI reports the original failure. Logging must not write elsewhere.
        }
    }
}
