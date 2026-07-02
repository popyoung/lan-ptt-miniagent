namespace LanPttIntercom.Audio;

/// <summary>
/// Tunable constants for the microphone enhancement chain.
/// </summary>
public sealed record AudioEnhancementProfile
{
    public static AudioEnhancementProfile Default { get; } = new();

    public double HighPassBaseHz { get; init; } = 80.0;
    public double HighPassStrengthSlopeHz { get; init; } = 0.8;
    public double PresenceCenterHz { get; init; } = 2200.0;
    public double PresenceQ { get; init; } = 0.9;
    public double PresenceGainDbAt100 { get; init; } = 3.0;
    public double TargetRmsBase { get; init; } = 0.055;
    public double TargetRmsAt100 { get; init; } = 0.220;
    public double MakeupGainAt100 { get; init; } = 2.2;
    public double PlosiveStrengthThreshold { get; init; } = 75.0;
    public double PlosiveInputRmsThreshold { get; init; } = 0.08;
    public double PlosiveFilteredRatioThreshold { get; init; } = 0.65;
    public double PlosiveOutputRmsCeiling { get; init; } = 0.65;
    public double LimiterThresholdDb { get; init; } = -2.0;
    public double LimiterRatio { get; init; } = 20.0;
    public double LimiterAttackSeconds { get; init; } = 0.002;
    public double LimiterReleaseSeconds { get; init; } = 0.050;
    public double OutputCeiling { get; init; } = 30000.0 / 32768.0;
}
