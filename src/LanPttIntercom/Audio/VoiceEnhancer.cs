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
    private const double LowMidSuppressionHysteresisWidth = 0.05;

    private readonly int _sampleRate;
    private readonly int _strength;
    private readonly int _maxGainMultiplier;
    private readonly AudioEnhancementProfile _profile;
    private readonly NWaves.Filters.BiQuad.HighPassFilter _highPass;
    private readonly PeakingEq? _presenceEq;
    private readonly BiquadFilter? _lowMidBandHighPass;
    private readonly BiquadFilter? _lowMidBandLowPass;
    private readonly DynamicPeakingEq? _lowMidCutEq;
    private readonly DynamicsProcessor _limiter;
    private byte[] _output = Array.Empty<byte>();
    private float[] _work = Array.Empty<float>();
    private float[] _lowMidBand = Array.Empty<float>();
    private double _lowMidReductionDb;
    private double _lowMidCompensationGain = 1.0;
    private bool _lowMidSuppressionGateActive;

    public VoiceEnhancer(AudioSettings settings)
        : this(settings, AudioEnhancementProfile.Default)
    {
    }

    public VoiceEnhancer(AudioSettings settings, AudioEnhancementProfile profile)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        if (profile == null) throw new ArgumentNullException(nameof(profile));
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
        _profile = profile;

        var cutoffHz = _profile.HighPassBaseHz + _strength * _profile.HighPassStrengthSlopeHz;
        var normalizedCutoff = Math.Clamp(cutoffHz / Math.Max(1, _sampleRate), 0.001, 0.45);
        _highPass = new NWaves.Filters.BiQuad.HighPassFilter(normalizedCutoff, 0.707);
        _presenceEq = _strength > 0
            ? new PeakingEq(
                _sampleRate,
                centerHz: _profile.PresenceCenterHz,
                q: _profile.PresenceQ,
                gainDb: _profile.PresenceGainDbAt100 * (_strength / 100.0))
            : null;
        if (_profile.DynamicLowMidSuppressionMaxReductionDbAt100 > 0.0)
        {
            var lowHz = Math.Clamp(_profile.DynamicLowMidSuppressionLowHz, 1.0, _sampleRate * 0.45);
            var highHz = Math.Clamp(_profile.DynamicLowMidSuppressionHighHz, lowHz + 1.0, _sampleRate * 0.45);
            _lowMidBandHighPass = BiquadFilter.CreateHighPass(_sampleRate, lowHz, 0.707);
            _lowMidBandLowPass = BiquadFilter.CreateLowPass(_sampleRate, highHz, 0.707);
            var centerHz = Math.Sqrt(lowHz * highHz);
            var q = Math.Clamp(centerHz / Math.Max(1.0, highHz - lowHz), 0.45, 1.2);
            _lowMidCutEq = new DynamicPeakingEq(_sampleRate, centerHz, q);
        }

        // NWaves limiter is online and keeps envelope state between frames.
        _limiter = new DynamicsProcessor(
            DynamicsMode.Limiter,
            _sampleRate,
            threshold: (float)_profile.LimiterThresholdDb,
            ratio: (float)_profile.LimiterRatio,
            makeupGain: 0.0f,
            attack: (float)_profile.LimiterAttackSeconds,
            release: (float)_profile.LimiterReleaseSeconds,
            minAmplitudeDb: -90.0f);
    }

    public bool Matches(AudioSettings settings)
    {
        return Matches(settings, AudioEnhancementProfile.Default);
    }

    public bool Matches(AudioSettings settings, AudioEnhancementProfile profile)
    {
        return settings.SampleRate == _sampleRate &&
               Clamp(settings.Enhancement.Strength, 0, 100) == _strength &&
               Clamp(settings.Enhancement.MaxGainMultiplier, AudioEnhancementSettings.MinMaxGainMultiplier, AudioEnhancementSettings.MaxMaxGainMultiplier) == _maxGainMultiplier &&
               settings.Channels == 1 &&
               settings.BitsPerSample == 16 &&
               EqualityComparer<AudioEnhancementProfile>.Default.Equals(profile, _profile);
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
        double preSuppressionRmsSum = 0;
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
            preSuppressionRmsSum += sample * sample;
        }

        var inputRms = Math.Sqrt(inputRmsSum / Math.Max(1, samples));
        var processedRmsBeforeDynamicLowMid = Math.Sqrt(preSuppressionRmsSum / Math.Max(1, samples));
        var lowMidSuppressionActive = PrepareDynamicLowMidSuppression(
            work,
            samples,
            inputRms,
            processedRmsBeforeDynamicLowMid,
            out var lowMidTargetReductionDb,
            out var lowMidTargetCompensationGain);
        var strength = _strength / 100.0;
        var targetRms = _profile.TargetRmsBase + strength * (_profile.TargetRmsAt100 - _profile.TargetRmsBase);
        var autoGain = processedRmsBeforeDynamicLowMid > 0.00001 ? targetRms / processedRmsBeforeDynamicLowMid : 1.0;
        var maxGain = 1.6 + strength * (_maxGainMultiplier - 1.6);
        autoGain = Math.Clamp(autoGain, 1.0, maxGain);

        // Make high strength audibly different even when the mic is already
        // near/above target RMS, while clamping total gain to the user-configured
        // ceiling so amplification cannot grow without bound.
        var makeupGain = 1.0 + strength * (_profile.MakeupGainAt100 - 1.0);
        var gain = Math.Clamp(autoGain * makeupGain, 1.0, maxGain);
        var plosiveProtectionActive = _strength >= _profile.PlosiveStrengthThreshold &&
            inputRms > _profile.PlosiveInputRmsThreshold &&
            processedRmsBeforeDynamicLowMid > 0.00001 &&
            processedRmsBeforeDynamicLowMid < inputRms * _profile.PlosiveFilteredRatioThreshold;
        if (plosiveProtectionActive)
        {
            // Limit only high-energy low-frequency bursts. Normal low-rich speech
            // must stay audible, especially when the user deliberately chooses 100x.
            var plosiveGainCeiling = (inputRms * _profile.PlosiveOutputRmsCeiling) / processedRmsBeforeDynamicLowMid;
            gain = Math.Min(gain, plosiveGainCeiling);
            lowMidSuppressionActive = false;
            _lowMidSuppressionGateActive = false;
        }

        var lowMidCutEq = _lowMidCutEq;
        var lowMidReductionDb = plosiveProtectionActive
            ? ResetLowMidReductionDb()
            : SmoothLowMidReductionDb(lowMidSuppressionActive ? lowMidTargetReductionDb : 0.0, samples);
        var lowMidCompensationGain = plosiveProtectionActive
            ? ResetLowMidCompensationGain()
            : SmoothLowMidCompensationGain(lowMidSuppressionActive ? lowMidTargetCompensationGain : 1.0, samples);
        lowMidCutEq?.SetGainDb(-lowMidReductionDb);

        double preLimiterPeak = 0;
        for (int i = 0; i < samples; i++)
        {
            var sample = (float)(work[i] * gain);
            if (lowMidCutEq != null && !plosiveProtectionActive)
            {
                sample = lowMidCutEq.Process(sample);
            }

            work[i] = sample;
            preLimiterPeak = Math.Max(preLimiterPeak, Math.Abs(sample));
        }

        lowMidCompensationGain = CapLowMidCompensationGain(lowMidCompensationGain, preLimiterPeak);

        for (int i = 0; i < samples; i++)
        {
            var sample = (float)(work[i] * lowMidCompensationGain);
            sample = _limiter.Process(sample);
            sample = Math.Clamp(sample, (float)-_profile.OutputCeiling, (float)_profile.OutputCeiling);
            var intSample = (short)Math.Clamp((int)Math.Round(sample * 32767f), short.MinValue, short.MaxValue);
            output[i * 2] = (byte)(intSample & 0xFF);
            output[i * 2 + 1] = (byte)((intSample >> 8) & 0xFF);
        }
    }

    public static byte[] ProcessPcm16Mono(byte[] pcm, AudioSettings settings)
    {
        return ProcessPcm16Mono(pcm, settings, AudioEnhancementProfile.Default);
    }

    public static byte[] ProcessPcm16Mono(byte[] pcm, AudioSettings settings, AudioEnhancementProfile profile)
    {
        if (!settings.Enhancement.Enabled)
        {
            return (byte[])pcm.Clone();
        }

        return new VoiceEnhancer(settings, profile).ProcessPcm16Mono(pcm);
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

    private float[] EnsureLowMidBandBuffer(int samples)
    {
        if (_lowMidBand.Length < samples)
        {
            _lowMidBand = new float[samples];
        }
        return _lowMidBand;
    }

    private bool PrepareDynamicLowMidSuppression(
        float[] samples,
        int count,
        double inputRms,
        double processedRms,
        out double targetReductionDb,
        out double targetCompensationGain)
    {
        targetReductionDb = 0.0;
        targetCompensationGain = 1.0;
        var bandHighPass = _lowMidBandHighPass;
        var bandLowPass = _lowMidBandLowPass;
        var activeStrength = GetDynamicLowMidSuppressionStrength();
        if (bandHighPass == null ||
            bandLowPass == null ||
            activeStrength <= 0.0 ||
            inputRms < Math.Max(0.0, _profile.DynamicLowMidSuppressionMinInputRms) ||
            processedRms <= 0.000001)
        {
            _lowMidSuppressionGateActive = false;
            return false;
        }

        var band = EnsureLowMidBandBuffer(count);
        double bandSum = 0;
        for (int i = 0; i < count; i++)
        {
            var sample = samples[i];
            var bandSample = bandLowPass.Process(bandHighPass.Process(sample));
            band[i] = bandSample;
            bandSum += bandSample * bandSample;
        }

        var bandRms = Math.Sqrt(bandSum / Math.Max(1, count));
        var ratioThreshold = Math.Clamp(_profile.DynamicLowMidSuppressionRatioThreshold, 0.01, 0.99);
        var bandRatio = bandRms / Math.Max(0.000001, processedRms);
        var releaseThreshold = Math.Clamp(ratioThreshold - LowMidSuppressionHysteresisWidth, 0.01, ratioThreshold);
        if (_lowMidSuppressionGateActive)
        {
            _lowMidSuppressionGateActive = bandRatio > releaseThreshold;
        }
        else
        {
            _lowMidSuppressionGateActive = bandRatio > ratioThreshold;
        }

        if (!_lowMidSuppressionGateActive)
        {
            return false;
        }

        var linearExcess = Math.Clamp((bandRatio - releaseThreshold) / (1.0 - releaseThreshold), 0.0, 1.0);
        var excess = Math.Sqrt(linearExcess);
        var reductionDb = Math.Max(0.0, _profile.DynamicLowMidSuppressionMaxReductionDbAt100) * activeStrength * excess;
        targetReductionDb = reductionDb;
        var compensationDb = Math.Max(0.0, _profile.DynamicLowMidSuppressionCompensationDbAt100) * activeStrength * excess;
        targetCompensationGain = Math.Pow(10.0, compensationDb / 20.0);
        return reductionDb > 0.0001 || compensationDb > 0.0001;
    }

    private double GetDynamicLowMidSuppressionStrength()
    {
        if (_strength <= 0)
        {
            return 0.0;
        }

        var threshold = Math.Clamp(_profile.DynamicLowMidSuppressionStrengthThreshold, 0.0, 99.0);
        if (_strength <= threshold)
        {
            return 0.0;
        }

        return Math.Clamp((_strength - threshold) / Math.Max(1.0, 100.0 - threshold), 0.0, 1.0);
    }

    private double SmoothLowMidReductionDb(double targetReductionDb, int samples)
    {
        var timeSeconds = targetReductionDb > _lowMidReductionDb
            ? _profile.DynamicLowMidSuppressionAttackSeconds
            : _profile.DynamicLowMidSuppressionReleaseSeconds;
        var coefficient = SmoothingCoefficient(timeSeconds, samples);
        _lowMidReductionDb = targetReductionDb + (_lowMidReductionDb - targetReductionDb) * coefficient;
        return _lowMidReductionDb;
    }

    private double ResetLowMidReductionDb()
    {
        _lowMidReductionDb = 0.0;
        return _lowMidReductionDb;
    }

    private double SmoothLowMidCompensationGain(double targetGain, int samples)
    {
        var timeSeconds = targetGain > _lowMidCompensationGain
            ? _profile.DynamicLowMidSuppressionAttackSeconds
            : _profile.DynamicLowMidSuppressionReleaseSeconds;
        var coefficient = SmoothingCoefficient(timeSeconds, samples);
        _lowMidCompensationGain = targetGain + (_lowMidCompensationGain - targetGain) * coefficient;
        return _lowMidCompensationGain;
    }

    private double ResetLowMidCompensationGain()
    {
        _lowMidCompensationGain = 1.0;
        return _lowMidCompensationGain;
    }

    private double CapLowMidCompensationGain(double compensationGain, double preLimiterPeak)
    {
        if (compensationGain <= 1.0 || preLimiterPeak <= 0.000001)
        {
            return compensationGain;
        }

        var limiterLinearThreshold = Math.Pow(10.0, _profile.LimiterThresholdDb / 20.0);
        var peakCeiling = Math.Min(Math.Max(0.1, limiterLinearThreshold), Math.Max(0.1, _profile.OutputCeiling)) * 0.98;
        var headroomGain = peakCeiling / preLimiterPeak;
        if (headroomGain <= 1.0)
        {
            return 1.0;
        }

        return Math.Min(compensationGain, headroomGain);
    }

    private double SmoothingCoefficient(double timeSeconds, int samples)
    {
        if (timeSeconds <= 0.0)
        {
            return 0.0;
        }

        return Math.Exp(-Math.Max(1, samples) / Math.Max(1.0, timeSeconds * _sampleRate));
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

    private sealed class BiquadFilter
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

        private BiquadFilter(double b0, double b1, double b2, double a0, double a1, double a2)
        {
            _b0 = b0 / a0;
            _b1 = b1 / a0;
            _b2 = b2 / a0;
            _a1 = a1 / a0;
            _a2 = a2 / a0;
        }

        public static BiquadFilter CreateHighPass(int sampleRate, double cutoffHz, double q)
        {
            var coefficients = CalculateBaseCoefficients(sampleRate, cutoffHz, q);
            var b0 = (1.0 + coefficients.Cos) / 2.0;
            var b1 = -(1.0 + coefficients.Cos);
            var b2 = (1.0 + coefficients.Cos) / 2.0;
            return new BiquadFilter(b0, b1, b2, coefficients.A0, coefficients.A1, coefficients.A2);
        }

        public static BiquadFilter CreateLowPass(int sampleRate, double cutoffHz, double q)
        {
            var coefficients = CalculateBaseCoefficients(sampleRate, cutoffHz, q);
            var b0 = (1.0 - coefficients.Cos) / 2.0;
            var b1 = 1.0 - coefficients.Cos;
            var b2 = (1.0 - coefficients.Cos) / 2.0;
            return new BiquadFilter(b0, b1, b2, coefficients.A0, coefficients.A1, coefficients.A2);
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

        private static (double Cos, double A0, double A1, double A2) CalculateBaseCoefficients(int sampleRate, double cutoffHz, double q)
        {
            var safeSampleRate = Math.Max(1, sampleRate);
            var safeCutoff = Math.Clamp(cutoffHz, 1.0, safeSampleRate * 0.45);
            var safeQ = Math.Max(0.1, q);
            var omega = 2.0 * Math.PI * safeCutoff / safeSampleRate;
            var sin = Math.Sin(omega);
            var cos = Math.Cos(omega);
            var alpha = sin / (2.0 * safeQ);

            return (cos, 1.0 + alpha, -2.0 * cos, 1.0 - alpha);
        }
    }

    private sealed class DynamicPeakingEq
    {
        private readonly int _sampleRate;
        private readonly double _centerHz;
        private readonly double _q;
        private double _b0 = 1.0;
        private double _b1;
        private double _b2;
        private double _a1;
        private double _a2;
        private double _x1;
        private double _x2;
        private double _y1;
        private double _y2;
        // NaN forces the first SetGainDb(0) call to initialize coefficients.
        private double _gainDb = double.NaN;

        public DynamicPeakingEq(int sampleRate, double centerHz, double q)
        {
            _sampleRate = Math.Max(1, sampleRate);
            _centerHz = Math.Clamp(centerHz, 1.0, _sampleRate * 0.45);
            _q = Math.Max(0.1, q);
            SetGainDb(0.0);
        }

        public void SetGainDb(double gainDb)
        {
            if (Math.Abs(gainDb - _gainDb) < 0.001)
            {
                return;
            }

            _gainDb = gainDb;
            var a = Math.Pow(10.0, gainDb / 40.0);
            var omega = 2.0 * Math.PI * _centerHz / _sampleRate;
            var sin = Math.Sin(omega);
            var cos = Math.Cos(omega);
            var alpha = sin / (2.0 * _q);

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
