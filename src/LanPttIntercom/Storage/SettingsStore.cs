using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LanPttIntercom.Models;

namespace LanPttIntercom.Storage;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON beside the executable.
/// Failures fall back to defaults rather than crashing the app.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _filePath;
    private readonly string _baseDirectory;
    private readonly object _lock = new();
    private string? _lastLoadWarning;

    public SettingsStore()
        : this(AppContext.BaseDirectory)
    {
    }

    public SettingsStore(string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            throw new ArgumentException("Base directory cannot be empty.", nameof(baseDirectory));
        }

        _baseDirectory = Path.GetFullPath(baseDirectory);
        Directory.CreateDirectory(_baseDirectory);
        _filePath = Path.Combine(_baseDirectory, "settings.json");
    }

    public string FilePath => _filePath;
    public string BaseDirectory => _baseDirectory;
    public string? LastLoadWarning => _lastLoadWarning;

    public AppSettings Load()
    {
        lock (_lock)
        {
            _lastLoadWarning = null;
            try
            {
                if (!File.Exists(_filePath))
                {
                    return SeedDefaults();
                }

                using var stream = File.OpenRead(_filePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(stream, s_jsonOptions);
                if (loaded == null)
                {
                    var backupPath = TryBackupUnreadableSettings();
                    _lastLoadWarning = backupPath == null
                        ? "设置文件读取失败,已使用默认设置;程序已尝试备份原文件但未成功。原因:文件内容为空或格式不是有效设置。"
                        : "设置文件读取失败,已使用默认设置;原设置文件已尽量备份到 " + backupPath + "。原因:文件内容为空或格式不是有效设置。";
                    PortableRuntimeLog.Write(_baseDirectory, _lastLoadWarning);
                    return SeedDefaults();
                }

                // Make sure nested objects exist if the file was hand-edited.
                loaded.Audio ??= new AudioSettings();
                loaded.Audio.Enhancement ??= new AudioEnhancementSettings();
                loaded.Ui ??= new UiSettings();
                loaded.Endpoints ??= new List<SavedEndpoint>();

                // Clamp audio values to safe ranges.
                loaded.Audio.SampleRate = Clamp(loaded.Audio.SampleRate, 8000, 48000);
                loaded.Audio.FrameMilliseconds = Clamp(loaded.Audio.FrameMilliseconds, 10, 60);
                loaded.ListenPort = Clamp(loaded.ListenPort, 1024, 65535);
                loaded.Ui.OutputVolume = Clamp(loaded.Ui.OutputVolume, 0, 100);
                loaded.Audio.Enhancement.Strength = Clamp(loaded.Audio.Enhancement.Strength, 0, 100);
                return loaded;
            }
            catch (Exception ex)
            {
                var backupPath = TryBackupUnreadableSettings();
                _lastLoadWarning = backupPath == null
                    ? "设置文件读取失败,已使用默认设置;程序已尝试备份原文件但未成功。原因:" + ex.Message
                    : "设置文件读取失败,已使用默认设置;原设置文件已尽量备份到 " + backupPath + "。原因:" + ex.Message;
                PortableRuntimeLog.Write(_baseDirectory, _lastLoadWarning);
                return SeedDefaults();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_lock)
        {
            var tmp = _filePath + ".tmp";
            var committed = false;
            try
            {
                using (var stream = File.Create(tmp))
                {
                    JsonSerializer.Serialize(stream, settings, s_jsonOptions);
                }
                // Atomic-ish replace.
                if (File.Exists(_filePath))
                {
                    File.Replace(tmp, _filePath, null);
                }
                else
                {
                    File.Move(tmp, _filePath);
                }
                committed = true;
            }
            catch
            {
                if (!committed)
                {
                    TryDeleteFile(tmp);
                }
                throw;
            }
        }
    }

    private string? TryBackupUnreadableSettings()
    {
        try
        {
            if (!File.Exists(_filePath)) return null;
            var dir = Path.GetDirectoryName(_filePath);
            if (string.IsNullOrEmpty(dir)) return null;
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            var backupPath = Path.Combine(dir, "settings.failed-" + stamp + ".json");
            // Avoid replacing an existing backup; the caller's warning already reports backup failure.
            File.Copy(_filePath, backupPath, overwrite: false);
            return backupPath;
        }
        catch
        {
            return null;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Preserve the original save exception; cleanup is best-effort.
        }
    }

    private static AppSettings SeedDefaults()
    {
        return new AppSettings
        {
            ListenPort = 41000,
            Endpoints = new List<SavedEndpoint>(),
            Audio = new AudioSettings { Enhancement = new AudioEnhancementSettings() },
            Ui = new UiSettings()
        };
    }

    private static int Clamp(int value, int min, int max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    /// <summary>Read the raw file as text (for diagnostics / debug). Returns empty on failure.</summary>
    public string ReadRaw()
    {
        try
        {
            if (!File.Exists(_filePath)) return string.Empty;
            return File.ReadAllText(_filePath, Encoding.UTF8);
        }
        catch
        {
            return string.Empty;
        }
    }
}
