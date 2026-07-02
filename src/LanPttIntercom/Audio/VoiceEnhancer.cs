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
    private readonly int _maxGainMultiplier;
    private readonly NWaves.Filters.BiQuad.HighPassFilter _highPass;
    private readonly PeakingEq? _presenceEq;
    private readonly DynamicsProcessor _limiter;
    private byte[] _output = Array.Empty<byte>();
    private float[] _work = Array.Empty<float>();

    public VoiceEnhancer(AudioSettings settings)
    {
        if (settings.Channels != 1 || settings.BitsPerSample != 16)
        {
            throw new NotSupportedException("语音增强只支持 PCM16 单声道音频。");
        }

        _sampleRate = settings.SampleRate;
        _strength = Clamp(settings.Enhancement.Strength, 0, 100);
        _maxGainMultiplier = Clamp(
            settings.Enhancement.MaxGainMultiplier,
            AudioEnhancementSettings.MinMaxGainMultiplier,
            AudioEnhancementSettings.MaxMaxGainMultiplier);

        var cutoffHz = 80.0 + _strength * 0.8;
        var normalizedCutoff = Math.Clamp(cutoffHz / Math.Max(1, _sampleRate), 0.001, 0.45);
        _highPass = new NWaves.Filters.BiQuad.HighPassFilter(normalizedCutoff, 0.707);
        _presenceEq = _strength > 0
            ? new PeakingEq(_sampleRate, centerHz: 2200.0, q: 0.9, gainDb: 3.0 * (_strength / 100.0))
            : null;

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
               Clamp(settings.Enhancement.MaxGainMultiplier, AudioEnhancementSettings.MinMaxGainMultiplier, AudioEnhancementSettings.MaxMaxGainMultiplier) == _maxGainMultiplier &&
               settings.Channels == 1 &&
               settings.BitsPerSample == 16;
    }

    public byte[] ProcessPcm16Mono(byte[] pcm)
    {
        if (pcm == null) throw new ArgumentNullException(nameof(pcm));
        if (pcm.Length == 0) return Array.Empty<byte>();

        var output = new byte[pcm.Length];
        ProcessPcm16MonoInto(pcm, output);
        return output;
    }

    internal byte[] ProcessPcm16MonoReusable(byte[] pcm)
    {
        if (pcm == null) throw new ArgumentNullException(nameof(pcm));
        if (pcm.Length == 0) return Array.Empty<byte>();

        var output = EnsureOutputBuffer(pcm.Length);
        ProcessPcm16MonoInto(pcm, output);
        return output;
    }

    public void ProcessPcm16MonoInto(byte[] pcm, byte[] output)
    {
        if (pcm == null) throw new ArgumentNullException(nameof(pcm));
        if (output == null) throw new ArgumentNullException(nameof(output));
        if ((pcm.Length & 1) != 0) throw new ArgumentException("PCM16 frame length must be even.", nameof(pcm));
        if (output.Length < pcm.Length) throw new ArgumentException("Output buffer is smaller than the PCM frame.", nameof(output));

        var samples = pcm.Length / 2;
        var work = EnsureWorkBuffer(samples);

        double inputRmsSum = 0;
        double rmsSum = 0;
        for (int i = 0; i < samples; i++)
        {
            var sample = BitConverter.ToInt16(pcm, i * 2) / Int16Scale;
            inputRmsSum += sample * sample;
            sample = _highPass.Process(sample);
            if (_presenceEq != null)
            {
                sample = _presenceEq.Process(sample);
            }
            work[i] = sample;
            rmsSum += sample * sample;
        }

        var inputRms = Math.Sqrt(inputRmsSum / Math.Max(1, samples));
        var rms = Math.Sqrt(rmsSum / Math.Max(1, samples));
        var strength = _strength / 100.0;
        var targetRms = 0.055 + strength * 0.165;
        var autoGain = rms > 0.00001 ? targetRms / rms : 1.0;
        var maxGain = 1.6 + strength * (_maxGainMultiplier - 1.6);
        autoGain = Math.Clamp(autoGain, 1.0, maxGain);

        // Make high strength audibly different even when the mic is already
        // near/above target RMS, while clamping total gain to the user-configured
        // ceiling so amplification cannot grow without bound.
        var makeupGain = 1.0 + strength * 1.2;
        var gain = Math.Clamp(autoGain * makeupGain, 1.0, maxGain);
        if (_strength >= 75 && inputRms > 0.08 && rms > 0.00001 && rms < inputRms * 0.65)
        {
            // Limit only high-energy low-frequency bursts. Normal low-rich speech
            // must stay audible, especially when the user deliberately chooses 100x.
            var plosiveGainCeiling = (inputRms * 0.65) / rms;
            gain = Math.Min(gain, plosiveGainCeiling);
        }

        for (int i = 0; i < samples; i++)
        {
            var sample = (float)(work[i] * gain);
            sample = _limiter.Process(sample);
            sample = Math.Clamp(sample, -OutputCeiling, OutputCeiling);
            var intSample = (short)Math.Clamp((int)Math.Round(sample * 32767f), short.MinValue, short.MaxValue);
            output[i * 2] = (byte)(intSample & 0xFF);
            output[i * 2 + 1] = (byte)((intSample >> 8) & 0xFF);
        }
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

    private byte[] EnsureOutputBuffer(int bytes)
    {
        if (_output.Length != bytes)
        {
            _output = new byte[bytes];
        }
        return _output;
    }

    private float[] EnsureWorkBuffer(int samples)
    {
        if (_work.Length < samples)
        {
            _work = new float[samples];
        }
        return _work;
    }

    private sealed class PeakingEq
    {
        private readonly double _b0;
        private readonly double _b1;
        private readonly double _b2;
        private readonly double _a1;
        private readonly double _a2;
        private double _x1;
        private double _x2;
        private double _y1;
        private double _y2;

        public PeakingEq(int sampleRate, double centerHz, double q, double gainDb)
        {
            var safeSampleRate = Math.Max(1, sampleRate);
            var safeCenter = Math.Clamp(centerHz, 1.0, safeSampleRate * 0.45);
            var safeQ = Math.Max(0.1, q);
            var a = Math.Pow(10.0, gainDb / 40.0);
            var omega = 2.0 * Math.PI * safeCenter / safeSampleRate;
            var sin = Math.Sin(omega);
            var cos = Math.Cos(omega);
            var alpha = sin / (2.0 * safeQ);

            var b0 = 1.0 + alpha * a;
            var b1 = -2.0 * cos;
            var b2 = 1.0 - alpha * a;
            var a0 = 1.0 + alpha / a;
            var a1 = -2.0 * cos;
            var a2 = 1.0 - alpha / a;

            _b0 = b0 / a0;
            _b1 = b1 / a0;
            _b2 = b2 / a0;
            _a1 = a1 / a0;
            _a2 = a2 / a0;
        }

        public float Process(float input)
        {
            var output = _b0 * input + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1;
            _x1 = input;
            _y2 = _y1;
            _y1 = output;
            return (float)output;
        }
    }
}
