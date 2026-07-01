using System;
using LanPttIntercom.Models;
using NWaves.Operations;

namespace LanPttIntercom.Audio;

/// <summary>
/// Low-latency microphone enhancement for PCM16 mono frames.
/// </summary>
public sealed class VoiceEnhancer
{
    private const float Int16Scale = 32768f;
    private const float OutputCeiling = 30000f / Int16Scale;

    private readonly int _sampleRate;
    private readonly int _strength;
    private readonly NWaves.Filters.BiQuad.HighPassFilter _highPass;
    private readonly DynamicsProcessor _limiter;

    public VoiceEnhancer(AudioSettings settings)
    {
        if (settings.Channels != 1 || settings.BitsPerSample != 16)
        {
            throw new NotSupportedException("语音增强只支持 PCM16 单声道音频。");
        }

        _sampleRate = settings.SampleRate;
        _strength = Clamp(settings.Enhancement.Strength, 0, 100);

        var cutoffHz = 70.0 + _strength * 0.6;
        var normalizedCutoff = Math.Clamp(cutoffHz / Math.Max(1, _sampleRate), 0.001, 0.45);
        _highPass = new NWaves.Filters.BiQuad.HighPassFilter(normalizedCutoff, 0.707);

        // NWaves limiter is online and keeps envelope state between frames.
        _limiter = new DynamicsProcessor(
            DynamicsMode.Limiter,
            _sampleRate,
            threshold: -2.0f,
            ratio: 20.0f,
            makeupGain: 0.0f,
            attack: 0.002f,
            release: 0.050f,
            minAmplitudeDb: -90.0f);
    }

    public bool Matches(AudioSettings settings)
    {
        return settings.SampleRate == _sampleRate &&
               Clamp(settings.Enhancement.Strength, 0, 100) == _strength &&
               settings.Channels == 1 &&
               settings.BitsPerSample == 16;
    }

    public byte[] ProcessPcm16Mono(byte[] pcm)
    {
        if (pcm == null) throw new ArgumentNullException(nameof(pcm));
        if (pcm.Length == 0) return Array.Empty<byte>();
        if ((pcm.Length & 1) != 0) throw new ArgumentException("PCM16 frame length must be even.", nameof(pcm));

        var output = new byte[pcm.Length];
        var samples = pcm.Length / 2;
        var work = new float[samples];

        double rmsSum = 0;
        for (int i = 0; i < samples; i++)
        {
            var sample = BitConverter.ToInt16(pcm, i * 2) / Int16Scale;
            sample = _highPass.Process(sample);
            work[i] = sample;
            rmsSum += sample * sample;
        }

        var rms = Math.Sqrt(rmsSum / Math.Max(1, samples));
        var targetRms = 0.055 + (_strength / 100.0) * 0.070;
        var gain = rms > 0.00001 ? targetRms / rms : 1.0;
        var maxGain = 1.4 + (_strength / 100.0) * 3.4;
        gain = Math.Clamp(gain, 1.0, maxGain);

        for (int i = 0; i < samples; i++)
        {
            var sample = (float)(work[i] * gain);
            sample = _limiter.Process(sample);
            sample = Math.Clamp(sample, -OutputCeiling, OutputCeiling);
            var intSample = (short)Math.Clamp((int)Math.Round(sample * 32767f), short.MinValue, short.MaxValue);
            output[i * 2] = (byte)(intSample & 0xFF);
            output[i * 2 + 1] = (byte)((intSample >> 8) & 0xFF);
        }

        return output;
    }

    public static byte[] ProcessPcm16Mono(byte[] pcm, AudioSettings settings)
    {
        if (!settings.Enhancement.Enabled)
        {
            return (byte[])pcm.Clone();
        }

        return new VoiceEnhancer(settings).ProcessPcm16Mono(pcm);
    }

    private static int Clamp(int value, int min, int max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }
}
