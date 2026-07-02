using System.Globalization;

namespace LanPttAudioLab.Metrics;

public sealed record AudioMetricSummary(
    string Name,
    double Rms,
    int Peak,
    int NearCeilingCount,
    IReadOnlyList<double> FrameRms,
    double LowEnergyFrameRatio,
    double FrameRmsMean,
    double FrameRmsMin,
    double FrameRmsMax)
{
    public double EnvelopeRatio => FrameRmsMin <= 0.0001 ? 0 : FrameRmsMax / FrameRmsMin;
}

public static class AudioMetrics
{
    public const int NearCeilingThreshold = 29500;

    public static AudioMetricSummary Calculate(
        IReadOnlyList<short> samples,
        int sampleRate,
        int frameMilliseconds = 20,
        string name = "audio")
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (frameMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(frameMilliseconds));

        double sumSquares = 0;
        var peak = 0;
        var nearCeiling = 0;
        foreach (var sample in samples)
        {
            var abs = Math.Abs((int)sample);
            peak = Math.Max(peak, abs);
            if (abs >= NearCeilingThreshold) nearCeiling++;
            sumSquares += (double)sample * sample;
        }

        var rms = Math.Sqrt(sumSquares / Math.Max(1, samples.Count));
        var frameSamples = Math.Max(1, sampleRate * frameMilliseconds / 1000);
        var frames = new List<double>();
        for (int offset = 0; offset < samples.Count; offset += frameSamples)
        {
            var count = Math.Min(frameSamples, samples.Count - offset);
            double frameSum = 0;
            for (int i = 0; i < count; i++)
            {
                var sample = samples[offset + i];
                frameSum += (double)sample * sample;
            }

            frames.Add(Math.Sqrt(frameSum / Math.Max(1, count)));
        }

        var frameMean = frames.Count == 0 ? 0 : frames.Average();
        var frameMin = frames.Count == 0 ? 0 : frames.Min();
        var frameMax = frames.Count == 0 ? 0 : frames.Max();
        var lowThreshold = Math.Max(300.0, frameMean * 0.25);
        var lowRatio = frames.Count == 0 ? 0 : frames.Count(v => v < lowThreshold) / (double)frames.Count;

        return new AudioMetricSummary(
            name,
            rms,
            peak,
            nearCeiling,
            frames,
            lowRatio,
            frameMean,
            frameMin,
            frameMax);
    }

    public static string CsvHeader =>
        "name,rms,peak,near_ceiling_count,frame_rms_mean,frame_rms_min,frame_rms_max,envelope_ratio,low_energy_frame_ratio";

    public static string ToCsvRow(AudioMetricSummary metrics)
    {
        return string.Join(",",
            Csv(metrics.Name),
            Num(metrics.Rms),
            metrics.Peak.ToString(CultureInfo.InvariantCulture),
            metrics.NearCeilingCount.ToString(CultureInfo.InvariantCulture),
            Num(metrics.FrameRmsMean),
            Num(metrics.FrameRmsMin),
            Num(metrics.FrameRmsMax),
            Num(metrics.EnvelopeRatio),
            Num(metrics.LowEnergyFrameRatio));
    }

    internal static string Num(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    internal static string Csv(string value)
    {
        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r'))
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
