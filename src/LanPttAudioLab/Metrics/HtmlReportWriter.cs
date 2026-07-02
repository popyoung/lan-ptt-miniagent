using System.Net;
using System.Text;

namespace LanPttAudioLab.Metrics;

public static class HtmlReportWriter
{
    public static void Write(
        string path,
        string runType,
        string rawPath,
        IReadOnlyDictionary<string, string> outputPaths,
        IReadOnlyList<AudioLabPreset> presets,
        IReadOnlyList<AudioMetricSummary> commonMetrics,
        IReadOnlyList<PlosiveMetric> plosiveMetrics,
        IReadOnlyList<PitchMetricRow> pitchMetrics,
        int droppedShortPitchSegmentCount)
    {
        var reportDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        var html = new StringBuilder();
        html.AppendLine("<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
        html.AppendLine("<title>LanPttAudioLab Report</title>");
        html.AppendLine("<style>body{font-family:Segoe UI,Microsoft YaHei,sans-serif;margin:24px;line-height:1.45}table{border-collapse:collapse;margin:12px 0;width:100%}td,th{border:1px solid #ccc;padding:4px 6px;text-align:left}code{background:#eee;padding:1px 3px}section{margin-bottom:28px}audio{width:100%;max-width:720px}</style>");
        html.AppendLine("</head><body>");
        html.AppendLine("<h1>LanPttAudioLab 报告</h1>");
        html.AppendLine("<p>Run type: <code>" + H(runType) + "</code></p>");

        html.AppendLine("<section><h2>音频播放器</h2>");
        html.AppendLine("<h3>raw.wav</h3>");
        AudioTag(html, Relative(reportDir, rawPath));
        foreach (var output in outputPaths)
        {
            html.AppendLine("<h3>" + H(output.Key) + "</h3>");
            AudioTag(html, Relative(reportDir, output.Value));
        }
        html.AppendLine("</section>");

        html.AppendLine("<section><h2>预设摘要</h2><table><thead><tr><th>name</th><th>strength</th><th>maxGainMultiplier</th><th>profile</th></tr></thead><tbody>");
        foreach (var preset in presets)
        {
            html.AppendLine("<tr><td>" + H(preset.Name) + "</td><td>" + preset.Strength + "</td><td>" + preset.MaxGainMultiplier + "</td><td>" + H(preset.ProfileSummary) + "</td></tr>");
        }
        html.AppendLine("</tbody></table></section>");

        html.AppendLine("<section><h2>通用增强指标</h2><table><thead><tr><th>name</th><th>RMS</th><th>Peak</th><th>near-ceiling</th><th>frame RMS mean</th><th>envelope ratio</th><th>low-energy ratio</th></tr></thead><tbody>");
        foreach (var metrics in commonMetrics)
        {
            html.AppendLine("<tr><td>" + H(metrics.Name) + "</td><td>" + N(metrics.Rms) + "</td><td>" + metrics.Peak + "</td><td>" + metrics.NearCeilingCount + "</td><td>" + N(metrics.FrameRmsMean) + "</td><td>" + N(metrics.EnvelopeRatio) + "</td><td>" + N(metrics.LowEnergyFrameRatio) + "</td></tr>");
        }
        html.AppendLine("</tbody></table>");
        html.AppendLine("<p><strong>Low-energy ratio 说明：</strong>按帧 RMS 估算低能量帧比例，是筛查断续、门限过高或增强后能量异常下降的粗略指标，不等同于自动判定故障。</p>");
        html.AppendLine("</section>");

        if (runType == AudioLabRunner.SpeechQuality)
        {
            html.AppendLine("<section><h2>speech-quality 专属面板</h2>");
            html.AppendLine("<p>包含爆破音分数、候选突发数量、爆破后约 50-200ms ducking 近似比例、前 2 秒底噪 RMS。speech-quality 的统计包含脚本开头前 2 秒安静段，用于观察底噪是否被放大；ducking 和爆破音分数都是筛查指标，必须结合 raw/output 播放听感复核。</p>");
            html.AppendLine("<table><thead><tr><th>name</th><th>plosive score</th><th>bursts</th><th>post-burst ducking</th><th>noise RMS</th></tr></thead><tbody>");
            foreach (var metric in plosiveMetrics)
            {
                html.AppendLine("<tr><td>" + H(metric.Name) + "</td><td>" + N(metric.PlosiveScore) + "</td><td>" + metric.CandidateBurstCount + "</td><td>" + N(metric.PostBurstDuckingRatio) + "</td><td>" + N(metric.NoiseFirstTwoSecondsRms) + "</td></tr>");
            }
            html.AppendLine("</tbody></table></section>");
        }
        else if (runType == AudioLabRunner.PitchSweep)
        {
            html.AppendLine("<section><h2>pitch-sweep 专属面板</h2>");
            html.AppendLine("<p>包含 voiced segment、估算音高、置信度、每段输入/输出 RMS、增益、near-ceiling、低频比例和 THD-like 筛查指标。</p>");
            html.AppendLine("<p>低于 0.6 秒片段被丢弃数量: <strong>" + droppedShortPitchSegmentCount + "</strong>。短段不会进入音高估算表，避免不稳定估算误导；如这个数量偏高，应重新录制更长的保持音段。</p>");
            html.AppendLine("<table><thead><tr><th>preset</th><th>segment</th><th>start</th><th>end</th><th>pitch Hz</th><th>confidence</th><th>input RMS</th><th>output RMS</th><th>gain dB</th><th>near-ceiling</th><th>low freq</th><th>THD-like</th></tr></thead><tbody>");
            foreach (var row in pitchMetrics)
            {
                html.AppendLine("<tr><td>" + H(row.Name) + "</td><td>" + row.SegmentIndex + "</td><td>" + N(row.StartSeconds) + "</td><td>" + N(row.EndSeconds) + "</td><td>" + N(row.PitchHz) + "</td><td>" + N(row.PitchConfidence) + "</td><td>" + N(row.InputRms) + "</td><td>" + N(row.OutputRms) + "</td><td>" + N(row.GainDb) + "</td><td>" + row.NearCeilingCount + "</td><td>" + N(row.LowFrequencyRatio) + "</td><td>" + N(row.ThdLikeRatio) + "</td></tr>");
            }
            html.AppendLine("</tbody></table></section>");
        }

        html.AppendLine("</body></html>");
        File.WriteAllText(path, html.ToString(), Encoding.UTF8);
    }

    private static void AudioTag(StringBuilder html, string relativePath)
    {
        html.AppendLine("<audio controls preload=\"metadata\" src=\"" + H(relativePath.Replace('\\', '/')) + "\"></audio>");
    }

    private static string Relative(string baseDir, string path)
    {
        return Path.GetRelativePath(baseDir, Path.GetFullPath(path));
    }

    private static string H(string value) => WebUtility.HtmlEncode(value);
    private static string N(double value) => AudioMetrics.Num(value);
}
