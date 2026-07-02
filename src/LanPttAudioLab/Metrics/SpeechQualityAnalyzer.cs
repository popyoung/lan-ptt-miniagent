using System.Globalization;
using System.Text;

namespace LanPttAudioLab.Metrics;

public sealed record PlosiveMetric(
    string Name,
    double PlosiveScore,
    int CandidateBurstCount,
    double PostBurstDuckingRatio,
    double NoiseFirstTwoSecondsRms);

public static class SpeechQualityAnalyzer
{
    public static IReadOnlyList<AudioMetricSummary> WriteMetricsCsv(
        string path,
        IReadOnlyDictionary<string, short[]> samplesByName,
        int sampleRate,
        int frameMilliseconds)
    {
        var metrics = samplesByName
            .Select(pair => AudioMetrics.Calculate(pair.Value, sampleRate, frameMilliseconds, pair.Key))
            .ToList();

        var lines = new List<string> { AudioMetrics.CsvHeader };
        lines.AddRange(metrics.Select(AudioMetrics.ToCsvRow));
        File.WriteAllLines(path, lines, Encoding.UTF8);
        return metrics;
    }

    public static IReadOnlyList<PlosiveMetric> WritePlosiveMetricsCsv(
        string path,
        IReadOnlyDictionary<string, short[]> samplesByName,
        int sampleRate)
    {
        var metrics = samplesByName
            .Select(pair => AnalyzePlosives(pair.Key, pair.Value, sampleRate))
            .ToList();

        var lines = new List<string>
        {
            "name,plosive_score,candidate_burst_count,post_burst_ducking_ratio,noise_first_two_seconds_rms"
        };
        lines.AddRange(metrics.Select(m => string.Join(",",
            AudioMetrics.Csv(m.Name),
            AudioMetrics.Num(m.PlosiveScore),
            m.CandidateBurstCount.ToString(CultureInfo.InvariantCulture),
            AudioMetrics.Num(m.PostBurstDuckingRatio),
            AudioMetrics.Num(m.NoiseFirstTwoSecondsRms))));
        File.WriteAllLines(path, lines, Encoding.UTF8);
        return metrics;
    }

    public static PlosiveMetric AnalyzePlosives(string name, IReadOnlyList<short> samples, int sampleRate)
    {
        var window = Math.Max(1, sampleRate / 100);
        var rms = WindowRms(samples, window);
        if (rms.Count == 0)
        {
            return new PlosiveMetric(name, 0, 0, 0, 0);
        }

        var baseline = Percentile(rms, 0.50);
        var highThreshold = Math.Max(1000.0, baseline * 3.0);
        var bursts = new List<int>();
        for (int i = 1; i < rms.Count; i++)
        {
            if (rms[i] >= highThreshold && rms[i] > rms[i - 1] * 1.8)
            {
                bursts.Add(i);
            }
        }

        var maxRms = rms.Max();
        var score = baseline <= 0.0001 ? 0 : maxRms / baseline;
        var duckingRatios = new List<double>();
        foreach (var burst in bursts)
        {
            var postStart = burst + 5;
            var postEnd = Math.Min(rms.Count, burst + 20);
            if (postStart >= postEnd) continue;
            var post = rms.Skip(postStart).Take(postEnd - postStart).Average();
            duckingRatios.Add(post / Math.Max(1.0, baseline));
        }

        var noiseSamples = Math.Min(samples.Count, sampleRate * 2);
        double noiseSum = 0;
        for (int i = 0; i < noiseSamples; i++)
        {
            noiseSum += (double)samples[i] * samples[i];
        }

        var noiseRms = Math.Sqrt(noiseSum / Math.Max(1, noiseSamples));
        return new PlosiveMetric(
            name,
            score,
            bursts.Count,
            duckingRatios.Count == 0 ? 0 : duckingRatios.Average(),
            noiseRms);
    }

    private static List<double> WindowRms(IReadOnlyList<short> samples, int window)
    {
        var result = new List<double>();
        for (int offset = 0; offset < samples.Count; offset += window)
        {
            var count = Math.Min(window, samples.Count - offset);
            double sum = 0;
            for (int i = 0; i < count; i++)
            {
                sum += (double)samples[offset + i] * samples[offset + i];
            }

            result.Add(Math.Sqrt(sum / Math.Max(1, count)));
        }

        return result;
    }

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToArray();
        var index = (int)Math.Clamp(Math.Round((sorted.Length - 1) * percentile), 0, sorted.Length - 1);
        return sorted[index];
    }
}
