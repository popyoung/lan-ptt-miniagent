using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LanPttIntercom.Audio;
using LanPttIntercom.Models;

namespace LanPttAudioLab;

public sealed class AudioLabPresetSet
{
    public AudioLabRecordingSettings Recording { get; init; } = new();
    public List<AudioLabPreset> Presets { get; init; } = new();

    public static AudioLabPresetSet LoadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            var defaults = CreateDefault();
            defaults.Save(path);
            return defaults;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8), new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });
        return AudioLabPresetParser.ParsePresetSet(document.RootElement, path);
    }

    public void Save(string path)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        using var stream = File.Create(path);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

        writer.WriteStartObject();
        writer.WritePropertyName("recording");
        JsonSerializer.Serialize(writer, Recording, options);
        writer.WritePropertyName("presets");
        writer.WriteStartArray();
        foreach (var preset in Presets)
        {
            writer.WriteStartObject();
            writer.WriteString("name", preset.Name);
            writer.WriteNumber("strength", preset.Strength);
            writer.WriteNumber("maxGainMultiplier", preset.MaxGainMultiplier);
            writer.WritePropertyName("profile");
            writer.WriteRawValue(preset.RawProfileJson);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    public static AudioLabPresetSet CreateDefault()
    {
        return new AudioLabPresetSet
        {
            Recording = new AudioLabRecordingSettings(),
            Presets = new List<AudioLabPreset>
            {
                new("default-50-8", 50, 8, AudioEnhancementProfile.Default, "default", "\"default\""),
                new("strong-75-30", 75, 30, AudioEnhancementProfile.Default, "default", "\"default\""),
                new("max-100-100", 100, 100, AudioEnhancementProfile.Default, "default", "\"default\"")
            }
        };
    }
}

public sealed class AudioLabRecordingSettings
{
    public int SampleRate { get; set; } = 16000;
    public int BitsPerSample { get; set; } = 16;
    public int Channels { get; set; } = 1;
    public int FrameMilliseconds { get; set; } = 20;
    public int InputDeviceId { get; set; } = -1;

    public int ResolveFrameMillisecondsForCapture(int uiFrameMilliseconds, bool uiEdited)
    {
        return uiEdited ? uiFrameMilliseconds : FrameMilliseconds;
    }

    public AudioSettings ToAudioSettings(int strength, int maxGainMultiplier)
    {
        return new AudioSettings
        {
            SampleRate = SampleRate,
            BitsPerSample = BitsPerSample,
            Channels = Channels,
            FrameMilliseconds = FrameMilliseconds,
            InputDeviceId = InputDeviceId,
            Enhancement = new AudioEnhancementSettings
            {
                Enabled = true,
                Strength = strength,
                MaxGainMultiplier = maxGainMultiplier
            }
        };
    }
}

public sealed record AudioLabPreset(
    string Name,
    int Strength,
    int MaxGainMultiplier,
    AudioEnhancementProfile Profile,
    string ProfileSummary,
    string RawProfileJson)
{
    public string SafeFileName => AudioLabPresetParser.SanitizeFileName(Name);
}

public static class AudioLabPresetParser
{
    private static readonly Dictionary<string, Func<AudioEnhancementProfile, double, AudioEnhancementProfile>> s_profileSetters =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["highPassBaseHz"] = (p, v) => p with { HighPassBaseHz = v },
            ["highPassStrengthSlopeHz"] = (p, v) => p with { HighPassStrengthSlopeHz = v },
            ["presenceCenterHz"] = (p, v) => p with { PresenceCenterHz = v },
            ["presenceQ"] = (p, v) => p with { PresenceQ = v },
            ["presenceGainDbAt100"] = (p, v) => p with { PresenceGainDbAt100 = v },
            ["targetRmsBase"] = (p, v) => p with { TargetRmsBase = v },
            ["targetRmsAt100"] = (p, v) => p with { TargetRmsAt100 = v },
            ["makeupGainAt100"] = (p, v) => p with { MakeupGainAt100 = v },
            ["plosiveStrengthThreshold"] = (p, v) => p with { PlosiveStrengthThreshold = v },
            ["plosiveInputRmsThreshold"] = (p, v) => p with { PlosiveInputRmsThreshold = v },
            ["plosiveFilteredRatioThreshold"] = (p, v) => p with { PlosiveFilteredRatioThreshold = v },
            ["plosiveOutputRmsCeiling"] = (p, v) => p with { PlosiveOutputRmsCeiling = v },
            ["dynamicLowMidSuppressionStrengthThreshold"] = (p, v) => p with { DynamicLowMidSuppressionStrengthThreshold = v },
            ["dynamicLowMidSuppressionLowHz"] = (p, v) => p with { DynamicLowMidSuppressionLowHz = v },
            ["dynamicLowMidSuppressionHighHz"] = (p, v) => p with { DynamicLowMidSuppressionHighHz = v },
            ["dynamicLowMidSuppressionRatioThreshold"] = (p, v) => p with { DynamicLowMidSuppressionRatioThreshold = v },
            ["dynamicLowMidSuppressionMaxReductionDbAt100"] = (p, v) => p with { DynamicLowMidSuppressionMaxReductionDbAt100 = v },
            ["dynamicLowMidSuppressionCompensationDbAt100"] = (p, v) => p with { DynamicLowMidSuppressionCompensationDbAt100 = v },
            ["dynamicLowMidSuppressionAttackSeconds"] = (p, v) => p with { DynamicLowMidSuppressionAttackSeconds = v },
            ["dynamicLowMidSuppressionReleaseSeconds"] = (p, v) => p with { DynamicLowMidSuppressionReleaseSeconds = v },
            ["dynamicLowMidSuppressionMinInputRms"] = (p, v) => p with { DynamicLowMidSuppressionMinInputRms = v },
            ["limiterThresholdDb"] = (p, v) => p with { LimiterThresholdDb = v },
            ["limiterRatio"] = (p, v) => p with { LimiterRatio = v },
            ["limiterAttackSeconds"] = (p, v) => p with { LimiterAttackSeconds = v },
            ["limiterReleaseSeconds"] = (p, v) => p with { LimiterReleaseSeconds = v },
            ["outputCeiling"] = (p, v) => p with { OutputCeiling = v }
        };

    public static AudioLabPresetSet ParsePresetSet(JsonElement root, string sourcePath)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException(sourcePath + " 根节点必须是 JSON object。");
        }

        var recording = root.TryGetProperty("recording", out var recordingElement)
            ? ParseRecording(recordingElement)
            : new AudioLabRecordingSettings();
        var presets = new List<AudioLabPreset>();

        if (!root.TryGetProperty("presets", out var presetsElement) || presetsElement.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException(sourcePath + " 缺少 presets 数组。");
        }

        foreach (var item in presetsElement.EnumerateArray())
        {
            presets.Add(ParsePreset(item));
        }

        if (presets.Count == 0)
        {
            throw new FormatException(sourcePath + " 至少需要一个 preset。");
        }

        return new AudioLabPresetSet { Recording = recording, Presets = presets };
    }

    public static AudioEnhancementProfile ParseProfile(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString();
            if (string.Equals(text, "default", StringComparison.OrdinalIgnoreCase))
            {
                return AudioEnhancementProfile.Default;
            }

            throw new FormatException("profile 字符串只允许 default。实际值: " + text);
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("profile 必须是 \"default\" 或 object。");
        }

        var profile = AudioEnhancementProfile.Default;
        foreach (var property in element.EnumerateObject())
        {
            if (!s_profileSetters.TryGetValue(property.Name, out var setter))
            {
                throw new FormatException("未知 profile 字段: " + property.Name);
            }

            if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetDouble(out var value))
            {
                throw new FormatException("profile 字段必须是数字: " + property.Name);
            }

            profile = setter(profile, value);
        }

        return profile;
    }

    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        }

        var result = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(result) ? "preset" : result;
    }

    private static AudioLabPreset ParsePreset(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("preset 必须是 object。");
        }

        var name = ReadString(element, "name");
        var strength = Clamp(ReadInt(element, "strength"), 0, 100);
        var maxGain = Clamp(
            ReadInt(element, "maxGainMultiplier"),
            AudioEnhancementSettings.MinMaxGainMultiplier,
            AudioEnhancementSettings.MaxMaxGainMultiplier);

        var profile = AudioEnhancementProfile.Default;
        var summary = "default";
        var rawProfileJson = "\"default\"";
        if (element.TryGetProperty("profile", out var profileElement))
        {
            profile = ParseProfile(profileElement);
            summary = profileElement.ValueKind == JsonValueKind.String ? "default" : SummarizeProfile(profileElement);
            rawProfileJson = profileElement.GetRawText();
        }

        return new AudioLabPreset(name, strength, maxGain, profile, summary, rawProfileJson);
    }

    private static AudioLabRecordingSettings ParseRecording(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("recording 必须是 object。");
        }

        var recording = new AudioLabRecordingSettings();
        if (element.TryGetProperty("sampleRate", out var sampleRate)) recording.SampleRate = sampleRate.GetInt32();
        if (element.TryGetProperty("bitsPerSample", out var bits)) recording.BitsPerSample = bits.GetInt32();
        if (element.TryGetProperty("channels", out var channels)) recording.Channels = channels.GetInt32();
        if (element.TryGetProperty("frameMilliseconds", out var frameMs)) recording.FrameMilliseconds = frameMs.GetInt32();
        if (element.TryGetProperty("inputDeviceId", out var inputDevice)) recording.InputDeviceId = inputDevice.GetInt32();

        if (recording.BitsPerSample != 16 || recording.Channels != 1)
        {
            throw new FormatException("AudioLab 第一版只支持 PCM16 mono。");
        }

        return recording;
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new FormatException("preset 缺少字符串字段: " + propertyName);
        }

        return value.GetString() ?? string.Empty;
    }

    private static int ReadInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            throw new FormatException("preset 缺少数字字段: " + propertyName);
        }

        return value.GetInt32();
    }

    private static int Clamp(int value, int min, int max) => Math.Min(max, Math.Max(min, value));

    private static string SummarizeProfile(JsonElement profileElement)
    {
        return string.Join(", ", profileElement.EnumerateObject().Select(p =>
            p.Name + "=" + p.Value.GetDouble().ToString("0.###", CultureInfo.InvariantCulture)));
    }
}
