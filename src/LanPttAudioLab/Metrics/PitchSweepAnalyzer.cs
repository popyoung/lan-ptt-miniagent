using System.Globalization;
using System.Text;

namespace LanPttAudioLab.Metrics;

public sealed record PitchSegment(
    int Index,
    double StartSeconds,
    double EndSeconds,
    double PitchHz,
    double PitchConfidence,
    double InputRms);

public sealed record PitchSegmentDetectionResult(
    IReadOnlyList<PitchSegment> Segments,
    int DroppedShortSegmentCount);

public sealed record PitchMetricRow(
    string Name,
    int SegmentIndex,
    double StartSeconds,
    double EndSeconds,
    double PitchHz,
    double PitchConfidence,
    double InputRms,
    double OutputRms,
    double GainDb,
    int NearCeilingCount,
    double LowFrequencyRatio,
    double ThdLikeRatio);

public sealed record PitchMetricsResult(
    IReadOnlyList<PitchMetricRow> Rows,
    int DroppedShortSegmentCount);

public static class PitchSweepAnalyzer
{
    public static IReadOnlyList<PitchSegment> DetectVoicedSegments(short[] samples, int sampleRate)
    {
        return DetectVoicedSegmentsWithDroppedCount(samples, sampleRate).Segments;
    }

    public static PitchSegmentDetectionResult DetectVoicedSegmentsWithDroppedCount(short[] samples, int sampleRate)
    {
        var frameSamples = Math.Max(1, sampleRate / 50);
        var frameRms = new List<double>();
        for (int offset = 0; offset < samples.Length; offset += frameSamples)
        {
            var count = Math.Min(frameSamples, samples.Length - offset);
            double sum = 0;
            for (int i = 0; i < count; i++)
            {
                sum += (double)samples[offset + i] * samples[offset + i];
            }

            frameRms.Add(Math.Sqrt(sum / Math.Max(1, count)));
        }

        var maxRms = frameRms.Count == 0 ? 0 : frameRms.Max();
        var threshold = Math.Max(500.0, maxRms * 0.15);
        var segments = new List<(int StartFrame, int EndFrame)>();
        int? start = null;
        var silenceRun = 0;
        for (int i = 0; i < frameRms.Count; i++)
        {
            if (frameRms[i] >= threshold)
            {
                start ??= i;
                silenceRun = 0;
            }
            else if (start != null)
            {
                silenceRun++;
                if (silenceRun >= 5)
                {
                    segments.Add((start.Value, i - silenceRun + 1));
                    start = null;
                    silenceRun = 0;
                }
            }
        }

        if (start != null)
        {
            segments.Add((start.Value, frameRms.Count));
        }

        var result = new List<PitchSegment>();
        var droppedShortSegmentCount = 0;
        foreach (var segment in segments)
        {
            var startSample = segment.StartFrame * frameSamples;
            var endSample = Math.Min(samples.Length, segment.EndFrame * frameSamples);
            if (endSample <= startSample) continue;

            var duration = (endSample - startSample) / (double)sampleRate;
            if (duration < 0.6)
            {
                droppedShortSegmentCount++;
                continue;
            }

            var slice = new short[endSample - startSample];
            Array.Copy(samples, startSample, slice, 0, slice.Length);
            var estimate = EstimatePitch(slice, sampleRate);
            result.Add(new PitchSegment(
                result.Count + 1,
                startSample / (double)sampleRate,
                endSample / (double)sampleRate,
                estimate.PitchHz,
                estimate.Confidence,
                Rms(slice)));
        }

        return new PitchSegmentDetectionResult(result, droppedShortSegmentCount);
    }

    public static (double PitchHz, double Confidence) EstimatePitch(IReadOnlyList<short> samples, int sampleRate)
    {
        if (samples.Count < sampleRate / 20) return (0, 0);

        var minLag = Math.Max(1, sampleRate / 800);
        var maxLag = Math.Min(samples.Count / 2, sampleRate / 70);
        double zeroLag = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            zeroLag += (double)samples[i] * samples[i];
        }

        if (zeroLag <= 0.0001) return (0, 0);

        var bestLag = 0;
        var best = double.MinValue;
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            double corr = 0;
            for (int i = 0; i < samples.Count - lag; i++)
            {
                corr += (double)samples[i] * samples[i + lag];
            }

            var normalized = corr / zeroLag;
            if (normalized > best)
            {
                best = normalized;
                bestLag = lag;
            }
        }

        if (bestLag <= 0 || best < 0.2) return (0, Math.Max(0, best));
        return (sampleRate / (double)bestLag, Math.Clamp(best, 0, 1));
    }

    public static PitchMetricsResult WritePitchMetricsCsv(
        string path,
        short[] rawSamples,
        IReadOnlyDictionary<string, short[]> outputSamplesByName,
        int sampleRate)
    {
        var detection = DetectVoicedSegmentsWithDroppedCount(rawSamples, sampleRate);
        var rows = new List<PitchMetricRow>();
        foreach (var segment in detection.Segments)
        {
            var start = (int)Math.Round(segment.StartSeconds * sampleRate);
            var length = Math.Max(0, Math.Min(rawSamples.Length, (int)Math.Round(segment.EndSeconds * sampleRate)) - start);
            if (length <= 0) continue;

            foreach (var output in outputSamplesByName)
            {
                var outputSlice = Slice(output.Value, start, length);
                var outputRms = Rms(outputSlice);
                var gainDb = 20.0 * Math.Log10(Math.Max(1.0, outputRms) / Math.Max(1.0, segment.InputRms));
                rows.Add(new PitchMetricRow(
                    output.Key,
                    segment.Index,
                    segment.StartSeconds,
                    segment.EndSeconds,
                    segment.PitchHz,
                    segment.PitchConfidence,
                    segment.InputRms,
                    outputRms,
                    gainDb,
                    outputSlice.Count(s => Math.Abs((int)s) >= AudioMetrics.NearCeilingThreshold),
                    LowFrequencyRatio(outputSlice, sampleRate),
                    ThdLikeRatio(outputSlice, sampleRate, segment.PitchHz)));
            }
        }

        var lines = new List<string>
        {
            "# dropped_short_segment_count=" + detection.DroppedShortSegmentCount.ToString(CultureInfo.InvariantCulture),
            "name,segment_index,start_seconds,end_seconds,pitch_hz,pitch_confidence,input_rms,output_rms,gain_db,near_ceiling_count,low_frequency_ratio,thd_like_ratio"
        };
        lines.AddRange(rows.Select(r => string.Join(",",
            AudioMetrics.Csv(r.Name),
            r.SegmentIndex.ToString(CultureInfo.InvariantCulture),
            AudioMetrics.Num(r.StartSeconds),
            AudioMetrics.Num(r.EndSeconds),
            AudioMetrics.Num(r.PitchHz),
            AudioMetrics.Num(r.PitchConfidence),
            AudioMetrics.Num(r.InputRms),
            AudioMetrics.Num(r.OutputRms),
            AudioMetrics.Num(r.GainDb),
            r.NearCeilingCount.ToString(CultureInfo.InvariantCulture),
            AudioMetrics.Num(r.LowFrequencyRatio),
            AudioMetrics.Num(r.ThdLikeRatio))));
        File.WriteAllLines(path, lines, Encoding.UTF8);
        return new PitchMetricsResult(rows, detection.DroppedShortSegmentCount);
    }

    private static short[] Slice(short[] samples, int start, int length)
    {
        var safeStart = Math.Clamp(start, 0, samples.Length);
        var safeLength = Math.Clamp(length, 0, samples.Length - safeStart);
        var slice = new short[safeLength];
        Array.Copy(samples, safeStart, slice, 0, safeLength);
        return slice;
    }

    private static double Rms(IReadOnlyList<short> samples)
    {
        double sum = 0;
        foreach (var sample in samples)
        {
            sum += (double)sample * sample;
        }

        return Math.Sqrt(sum / Math.Max(1, samples.Count));
    }

    private static double LowFrequencyRatio(IReadOnlyList<short> samples, int sampleRate)
    {
        var low = GoertzelMagnitude(samples, sampleRate, 120) + GoertzelMagnitude(samples, sampleRate, 180);
        var mid = GoertzelMagnitude(samples, sampleRate, 700) + GoertzelMagnitude(samples, sampleRate, 1200);
        return low / Math.Max(0.0001, low + mid);
    }

    private static double ThdLikeRatio(IReadOnlyList<short> samples, int sampleRate, double pitchHz)
    {
        if (pitchHz <= 0) return 1;
        var fundamental = GoertzelMagnitude(samples, sampleRate, pitchHz);
        var harmonicSum = 0.0;
        for (int h = 2; h <= 5; h++)
        {
            var frequency = pitchHz * h;
            if (frequency >= sampleRate * 0.45) break;
            harmonicSum += GoertzelMagnitude(samples, sampleRate, frequency);
        }

        return harmonicSum / Math.Max(0.0001, fundamental + harmonicSum);
    }

    private static double GoertzelMagnitude(IReadOnlyList<short> samples, int sampleRate, double frequency)
    {
        if (samples.Count == 0 || frequency <= 0) return 0;
        var omega = 2.0 * Math.PI * frequency / sampleRate;
        var coeff = 2.0 * Math.Cos(omega);
        var q0 = 0.0;
        var q1 = 0.0;
        var q2 = 0.0;
        foreach (var sample in samples)
        {
            q0 = coeff * q1 - q2 + sample;
            q2 = q1;
            q1 = q0;
        }

        return Math.Sqrt(q1 * q1 + q2 * q2 - q1 * q2 * coeff);
    }
}
