using System.Text;
using System.Text.Json;
using LanPttAudioLab.Metrics;
using LanPttIntercom.Audio;

namespace LanPttAudioLab;

public sealed record AudioLabRunResult(
    string ExperimentDirectory,
    string RunType,
    string RawPath,
    IReadOnlyDictionary<string, string> OutputPaths,
    IReadOnlyList<AudioMetricSummary> CommonMetrics,
    IReadOnlyList<PlosiveMetric> PlosiveMetrics,
    IReadOnlyList<PitchMetricRow> PitchMetrics,
    int DroppedShortPitchSegmentCount,
    string ReportPath);

public static class AudioLabRunner
{
    public const string SpeechQuality = "speech-quality";
    public const string PitchSweep = "pitch-sweep";

    public static AudioLabRunResult RunAllPresets(string experimentDirectory, string runType)
    {
        if (runType != SpeechQuality && runType != PitchSweep)
        {
            throw new ArgumentException("run type 只支持 speech-quality 或 pitch-sweep。", nameof(runType));
        }

        var fullDir = Path.GetFullPath(experimentDirectory);
        Directory.CreateDirectory(fullDir);
        var rawPath = Path.Combine(fullDir, "raw.wav");
        if (!File.Exists(rawPath))
        {
            throw new FileNotFoundException("实验目录缺少 raw.wav，无法运行。", rawPath);
        }

        var presetsPath = Path.Combine(fullDir, "lab-presets.json");
        var presetSet = AudioLabPresetSet.LoadOrCreate(presetsPath);
        var raw = WavFile.ReadPcm16Mono(rawPath);
        var outputDir = Path.Combine(fullDir, "outputs");
        Directory.CreateDirectory(outputDir);

        var allSamples = new Dictionary<string, short[]> { ["raw"] = raw.Samples };
        var outputSamples = new Dictionary<string, short[]>();
        var outputPaths = new Dictionary<string, string>();
        foreach (var preset in presetSet.Presets)
        {
            var output = ProcessPreset(raw.Samples, raw.SampleRate, presetSet.Recording.FrameMilliseconds, preset);
            var outputPath = Path.Combine(outputDir, preset.SafeFileName + ".wav");
            WavFile.WritePcm16Mono(outputPath, raw.SampleRate, output);
            allSamples[preset.Name] = output;
            outputSamples[preset.Name] = output;
            outputPaths[preset.Name] = outputPath;
        }

        var commonMetrics = SpeechQualityAnalyzer.WriteMetricsCsv(
            Path.Combine(fullDir, "metrics.csv"),
            allSamples,
            raw.SampleRate,
            presetSet.Recording.FrameMilliseconds);
        IReadOnlyList<PlosiveMetric> plosiveMetrics = Array.Empty<PlosiveMetric>();
        IReadOnlyList<PitchMetricRow> pitchMetrics = Array.Empty<PitchMetricRow>();
        var droppedShortPitchSegmentCount = 0;

        if (runType == SpeechQuality)
        {
            plosiveMetrics = SpeechQualityAnalyzer.WritePlosiveMetricsCsv(
                Path.Combine(fullDir, "plosive-metrics.csv"),
                allSamples,
                raw.SampleRate);
        }
        else
        {
            var pitchResult = PitchSweepAnalyzer.WritePitchMetricsCsv(
                Path.Combine(fullDir, "pitch-metrics.csv"),
                raw.Samples,
                outputSamples,
                raw.SampleRate);
            pitchMetrics = pitchResult.Rows;
            droppedShortPitchSegmentCount = pitchResult.DroppedShortSegmentCount;
        }

        WriteRunTypeJson(fullDir, runType, presetSet.Recording, rawPath, raw.SampleRate, outputPaths, presetSet.Presets);
        var reportPath = Path.Combine(fullDir, "report.html");
        HtmlReportWriter.Write(
            reportPath,
            runType,
            rawPath,
            outputPaths,
            presetSet.Presets,
            commonMetrics,
            plosiveMetrics,
            pitchMetrics,
            droppedShortPitchSegmentCount);

        return new AudioLabRunResult(fullDir, runType, rawPath, outputPaths, commonMetrics, plosiveMetrics, pitchMetrics, droppedShortPitchSegmentCount, reportPath);
    }

    private static short[] ProcessPreset(short[] samples, int sampleRate, int frameMilliseconds, AudioLabPreset preset)
    {
        var recording = new AudioLabRecordingSettings
        {
            SampleRate = sampleRate,
            BitsPerSample = 16,
            Channels = 1,
            FrameMilliseconds = frameMilliseconds
        };
        var settings = recording.ToAudioSettings(preset.Strength, preset.MaxGainMultiplier);
        var enhancer = new VoiceEnhancer(settings, preset.Profile);
        var frameSamples = Math.Max(1, sampleRate * frameMilliseconds / 1000);
        var output = new short[samples.Length];
        var frameBytes = new byte[frameSamples * 2];
        var outputBytes = new byte[frameSamples * 2];

        for (int offset = 0; offset < samples.Length; offset += frameSamples)
        {
            var count = Math.Min(frameSamples, samples.Length - offset);
            var inputBytes = frameBytes;
            if (count != frameSamples)
            {
                inputBytes = new byte[count * 2];
            }

            for (int i = 0; i < count; i++)
            {
                var sample = samples[offset + i];
                inputBytes[i * 2] = (byte)(sample & 0xFF);
                inputBytes[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
            }

            enhancer.ProcessPcm16MonoInto(inputBytes, outputBytes);
            for (int i = 0; i < count; i++)
            {
                output[offset + i] = BitConverter.ToInt16(outputBytes, i * 2);
            }
        }

        return output;
    }

    private static void WriteRunTypeJson(
        string experimentDirectory,
        string runType,
        AudioLabRecordingSettings recording,
        string rawPath,
        int sampleRate,
        IReadOnlyDictionary<string, string> outputPaths,
        IReadOnlyList<AudioLabPreset> presets)
    {
        var metadata = new
        {
            runType,
            createdAtLocal = DateTime.Now.ToString("O"),
            sampleRate,
            recording.FrameMilliseconds,
            recording.InputDeviceId,
            rawFileName = Path.GetFileName(rawPath),
            rawBytes = new FileInfo(rawPath).Length,
            outputsFileNames = outputPaths.Values
                .Select(path => Path.GetRelativePath(experimentDirectory, path))
                .ToArray(),
            presetCount = presets.Count,
            presetNames = presets.Select(p => p.Name).ToArray(),
            presets = presets.Select(p => new
            {
                p.Name,
                p.Strength,
                p.MaxGainMultiplier,
                p.ProfileSummary,
                p.RawProfileJson,
                outputFileName = outputPaths.TryGetValue(p.Name, out var outputPath)
                    ? Path.GetRelativePath(experimentDirectory, outputPath)
                    : string.Empty
            }).ToArray(),
            toolVersion = typeof(AudioLabRunner).Assembly.GetName().Version?.ToString() ?? "unknown"
        };
        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        File.WriteAllText(Path.Combine(experimentDirectory, "run-type.json"), JsonSerializer.Serialize(metadata, options), Encoding.UTF8);
    }
}
