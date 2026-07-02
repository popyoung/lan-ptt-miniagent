using System.Net;
using System.Text.Json;
using System.Drawing;
using LanPttIntercom;
using LanPttIntercom.Audio;
using LanPttIntercom.Models;
using LanPttIntercom.Network;
using LanPttIntercom.Storage;
using LanPttAudioLab;
using LanPttAudioLab.Metrics;

var tests = new (string Name, Action Run)[]
{
    ("SettingsStore default path is executable directory settings.json", SettingsStoreDefaultPathUsesBaseDirectory),
    ("SettingsStore saves and backs up only inside configured directory", SettingsStoreUsesConfiguredDirectoryOnly),
    ("SettingsStore clamps enhancement max gain multiplier", SettingsStoreClampsEnhancementMaxGainMultiplier),
    ("AudioEnhancementProfile default matches current production constants", AudioEnhancementProfileDefaultMatchesProductionConstants),
    ("AudioLab profile overlay only changes provided fields", AudioLabProfileOverlayOnlyChangesProvidedFields),
    ("AudioLab profile overlay rejects unknown fields", AudioLabProfileOverlayRejectsUnknownFields),
    ("AudioLab preset save preserves profile overlay JSON", AudioLabPresetSavePreservesProfileOverlayJson),
    ("AudioLab runner writes raw and output references to run metadata", AudioLabRunnerWritesRawAndOutputReferencesToRunMetadata),
    ("WavFile round-trips PCM16 mono samples", WavFileRoundTripsPcm16MonoSamples),
    ("AudioMetrics calculates basic PCM16 values", AudioMetricsCalculatesBasicPcm16Values),
    ("PitchSweepAnalyzer segments voiced regions and estimates pitch", PitchSweepAnalyzerSegmentsVoicedRegionsAndEstimatesPitch),
    ("PitchSweepAnalyzer reports dropped short voiced segments", PitchSweepAnalyzerReportsDroppedShortVoicedSegments),
    ("VoiceEnhancer bypasses disabled audio enhancement", VoiceEnhancerBypassesDisabledEnhancement),
    ("VoiceEnhancer boosts quiet PCM16 mono voice without clipping", VoiceEnhancerBoostsQuietVoice),
    ("VoiceEnhancer strength zero keeps RMS conservative", VoiceEnhancerStrengthZeroKeepsRmsConservative),
    ("VoiceEnhancer strength one hundred is louder for medium voice", VoiceEnhancerStrengthOneHundredIsLouderForMediumVoice),
    ("VoiceEnhancer strength one hundred default 8x strongly boosts quiet voice", VoiceEnhancerStrengthOneHundredDefaultEightBoostsQuietVoice),
    ("VoiceEnhancer configured 100x is stronger than 8x for very quiet voice", VoiceEnhancerConfiguredHundredIsStrongerThanEightForVeryQuietVoice),
    ("VoiceEnhancer configured 100x strongly boosts low-rich normal voice", VoiceEnhancerConfiguredHundredStronglyBoostsLowRichNormalVoice),
    ("VoiceEnhancer keeps consecutive low-rich speech frames audible and stable", VoiceEnhancerKeepsConsecutiveLowRichSpeechFramesAudibleAndStable),
    ("VoiceEnhancer strength one hundred attenuates low frequency more than mid frequency", VoiceEnhancerStrengthOneHundredAttenuatesLowFrequencyMoreThanMidFrequency),
    ("VoiceEnhancer strength one hundred improves clarity band over muddy low-mid", VoiceEnhancerStrengthOneHundredImprovesClarityBandOverMuddyLowMid),
    ("VoiceEnhancer strength one hundred lifts presence band over lower speech band", VoiceEnhancerStrengthOneHundredLiftsPresenceBandOverLowerSpeechBand),
    ("VoiceEnhancer strength one hundred reduces plosive burst", VoiceEnhancerStrengthOneHundredReducesPlosiveBurst),
    ("VoiceEnhancer strength one hundred keeps plosive protection moderate", VoiceEnhancerStrengthOneHundredKeepsPlosiveProtectionModerate),
    ("VoiceEnhancer low-frequency dominant gain cap is gentle", VoiceEnhancerLowFrequencyDominantGainCapIsGentle),
    ("VoiceEnhancer cascade boundary avoids abrupt low-frequency drop", VoiceEnhancerCascadeBoundaryAvoidsAbruptLowFrequencyDrop),
    ("VoiceEnhancer strength zero preserves low male fundamental", VoiceEnhancerStrengthZeroPreservesLowMaleFundamental),
    ("VoiceEnhancer strength one hundred preserves mid-frequency speech energy", VoiceEnhancerStrengthOneHundredPreservesMidFrequencySpeechEnergy),
    ("VoiceEnhancer rejects non PCM16 mono settings clearly", VoiceEnhancerRejectsNonPcm16MonoClearly),
    ("VoiceEnhancer matches sample rate strength and profile changes", VoiceEnhancerMatchesSampleRateStrengthAndProfileChanges),
    ("Pcm16Frame applies output volume percentage", Pcm16FrameAppliesOutputVolume),
    ("Pcm16Frame applies output volume in place", Pcm16FrameAppliesOutputVolumeInPlace),
    ("MmsAudioPlayback keeps WAVEHDR prepared flag for submit", MmsAudioPlaybackKeepsPreparedFlagForSubmit),
    ("MmsAudioCapture keeps WAVEHDR prepared flag for reuse", MmsAudioCaptureKeepsPreparedFlagForReuse),
    ("VoiceUdpClient allows loopback but filters local LAN packets", VoiceUdpClientAllowsLoopbackButFiltersLocalLanPackets),
    ("TransmitStateGate transitions atomically under contention", TransmitStateGateTransitionsAtomically),
    ("WindowPlacement moves offscreen bounds into preferred visible work area", WindowPlacementMovesOffscreenBoundsIntoPreferredVisibleWorkArea),
    ("WindowPlacement falls back when preferred work area is unavailable", WindowPlacementFallsBackWhenPreferredWorkAreaIsUnavailable),
    ("WindowPlacement moves disconnected negative-coordinate bounds into current screen", WindowPlacementMovesDisconnectedNegativeCoordinateBoundsIntoCurrentScreen),
    ("WindowPlacement supports negative-coordinate monitors", WindowPlacementSupportsNegativeCoordinateMonitors),
    ("WindowPlacement preserves partially visible bounds", WindowPlacementPreservesPartiallyVisibleBounds),
    ("WindowPlacement preserves oversized bounds when visible", WindowPlacementPreservesOversizedBoundsWhenVisible),
    ("WindowPlacement preserves oversized cross-screen partially visible bounds", WindowPlacementPreservesOversizedCrossScreenPartiallyVisibleBounds),
    ("WindowPlacement recenters empty bounds into preferred work area", WindowPlacementRecentersEmptyBoundsIntoPreferredWorkArea),
    ("WindowPlacement expands sub-minimum bounds into current work area", WindowPlacementExpandsSubMinimumBoundsIntoCurrentWorkArea)
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine("PASS " + test.Name);
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine("FAIL " + test.Name + ": " + ex.Message);
    }
}

Console.Out.Flush();
Console.Error.Flush();
return failed == 0 ? 0 : 1;

static void SettingsStoreDefaultPathUsesBaseDirectory()
{
    var store = new SettingsStore();
    var expected = Path.Combine(AppContext.BaseDirectory, "settings.json");
    AssertEqual(Path.GetFullPath(expected), Path.GetFullPath(store.FilePath), "default settings path");
}

static void SettingsStoreUsesConfiguredDirectoryOnly()
{
    var dir = Path.Combine(Path.GetTempPath(), "LanPttIntercom.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        var store = new SettingsStore(dir);
        var settings = new AppSettings
        {
            ListenPort = 42000,
            Audio = new AudioSettings
            {
                Enhancement = new AudioEnhancementSettings
                {
                    Enabled = true,
                    Strength = 65,
                    MaxGainMultiplier = 80
                }
            }
        };

        store.Save(settings);
        AssertEqual(Path.Combine(dir, "settings.json"), store.FilePath, "configured settings path");
        Assert(File.Exists(store.FilePath), "settings.json was not created in configured directory");

        var loaded = store.Load();
        AssertEqual(42000, loaded.ListenPort, "persisted listen port");
        Assert(loaded.Audio.Enhancement.Enabled, "persisted enhancement enabled");
        AssertEqual(65, loaded.Audio.Enhancement.Strength, "persisted enhancement strength");
        AssertEqual(80, loaded.Audio.Enhancement.MaxGainMultiplier, "persisted enhancement max gain multiplier");

        File.WriteAllText(store.FilePath, "{ broken json", System.Text.Encoding.UTF8);
        _ = store.Load();
        var backups = Directory.GetFiles(dir, "settings.failed-*.json");
        AssertEqual(1, backups.Length, "backup file count");
        Assert(Path.GetDirectoryName(backups[0]) == dir, "backup escaped configured directory");
    }
    finally
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }
}

static void SettingsStoreClampsEnhancementMaxGainMultiplier()
{
    var dir = Path.Combine(Path.GetTempPath(), "LanPttIntercom.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        var store = new SettingsStore(dir);

        File.WriteAllText(store.FilePath,
            "{\"audio\":{\"enhancement\":{\"enabled\":true,\"strength\":50,\"maxGainMultiplier\":999}},\"ui\":{}}",
            System.Text.Encoding.UTF8);
        var high = store.Load();
        AssertEqual(100, high.Audio.Enhancement.MaxGainMultiplier, "high max gain multiplier clamp");

        File.WriteAllText(store.FilePath,
            "{\"audio\":{\"enhancement\":{\"enabled\":true,\"strength\":50,\"maxGainMultiplier\":1}},\"ui\":{}}",
            System.Text.Encoding.UTF8);
        var low = store.Load();
        AssertEqual(4, low.Audio.Enhancement.MaxGainMultiplier, "low max gain multiplier clamp");

        File.WriteAllText(store.FilePath,
            "{\"audio\":{\"enhancement\":{\"enabled\":true,\"strength\":50}},\"ui\":{}}",
            System.Text.Encoding.UTF8);
        var missing = store.Load();
        AssertEqual(8, missing.Audio.Enhancement.MaxGainMultiplier, "missing max gain multiplier default");
    }
    finally
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }
}

static void AudioEnhancementProfileDefaultMatchesProductionConstants()
{
    var profile = AudioEnhancementProfile.Default;

    AssertEqual(80.0, profile.HighPassBaseHz, "default high-pass base");
    AssertEqual(0.8, profile.HighPassStrengthSlopeHz, "default high-pass strength slope");
    AssertEqual(2200.0, profile.PresenceCenterHz, "default presence center");
    AssertEqual(0.9, profile.PresenceQ, "default presence Q");
    AssertEqual(3.0, profile.PresenceGainDbAt100, "default presence gain at 100");
    AssertEqual(0.055, profile.TargetRmsBase, "default target RMS base");
    AssertEqual(0.220, profile.TargetRmsAt100, "default target RMS at 100");
    AssertEqual(2.2, profile.MakeupGainAt100, "default makeup gain at 100");
    AssertEqual(75.0, profile.PlosiveStrengthThreshold, "default plosive strength threshold");
    AssertEqual(0.08, profile.PlosiveInputRmsThreshold, "default plosive input RMS threshold");
    AssertEqual(0.65, profile.PlosiveFilteredRatioThreshold, "default plosive filtered ratio threshold");
    AssertEqual(0.65, profile.PlosiveOutputRmsCeiling, "default plosive output RMS ceiling");
    AssertEqual(-2.0, profile.LimiterThresholdDb, "default limiter threshold");
    AssertEqual(20.0, profile.LimiterRatio, "default limiter ratio");
    AssertEqual(0.002, profile.LimiterAttackSeconds, "default limiter attack");
    AssertEqual(0.050, profile.LimiterReleaseSeconds, "default limiter release");
    AssertEqual(30000.0 / 32768.0, profile.OutputCeiling, "default output ceiling");
}

static void AudioLabProfileOverlayOnlyChangesProvidedFields()
{
    using var document = JsonDocument.Parse("{\"highPassBaseHz\":65,\"plosiveOutputRmsCeiling\":0.75}");
    var profile = AudioLabPresetParser.ParseProfile(document.RootElement);

    AssertEqual(65.0, profile.HighPassBaseHz, "overlay high-pass base");
    AssertEqual(0.75, profile.PlosiveOutputRmsCeiling, "overlay plosive output RMS ceiling");
    AssertEqual(AudioEnhancementProfile.Default.PresenceCenterHz, profile.PresenceCenterHz, "overlay should preserve unspecified presence center");
    AssertEqual(AudioEnhancementProfile.Default.OutputCeiling, profile.OutputCeiling, "overlay should preserve unspecified output ceiling");
}

static void AudioLabProfileOverlayRejectsUnknownFields()
{
    using var document = JsonDocument.Parse("{\"highPassBaseHz\":65,\"unknownProfileField\":1}");
    var error = AssertThrows<FormatException>(() => AudioLabPresetParser.ParseProfile(document.RootElement), "unknown profile field should be rejected");

    Assert(error.Message.Contains("unknownProfileField", StringComparison.Ordinal), "unknown field name should be included in error: " + error.Message);
}

static void AudioLabPresetSavePreservesProfileOverlayJson()
{
    var dir = Path.Combine(Path.GetTempPath(), "LanPttAudioLab.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        var path = Path.Combine(dir, "lab-presets.json");
        File.WriteAllText(path,
            "{\n" +
            "  \"recording\": {\n" +
            "    \"seconds\": 8,\n" +
            "    \"sampleRate\": 16000,\n" +
            "    \"bitsPerSample\": 16,\n" +
            "    \"channels\": 1,\n" +
            "    \"frameMilliseconds\": 20,\n" +
            "    \"inputDeviceId\": -1\n" +
            "  },\n" +
            "  \"presets\": [\n" +
            "    {\n" +
            "      \"name\": \"less-filtering\",\n" +
            "      \"strength\": 100,\n" +
            "      \"maxGainMultiplier\": 100,\n" +
            "      \"profile\": {\n" +
            "        \"highPassBaseHz\": 65,\n" +
            "        \"plosiveOutputRmsCeiling\": 0.75\n" +
            "      }\n" +
            "    }\n" +
            "  ]\n" +
            "}\n",
            System.Text.Encoding.UTF8);

        var set = AudioLabPresetSet.LoadOrCreate(path);
        set.Recording.FrameMilliseconds = 30;
        set.Save(path);

        using var saved = JsonDocument.Parse(File.ReadAllText(path, System.Text.Encoding.UTF8));
        var profile = saved.RootElement.GetProperty("presets")[0].GetProperty("profile");
        Assert(profile.TryGetProperty("highPassBaseHz", out var highPass), "saved overlay should keep highPassBaseHz");
        AssertEqual(65.0, highPass.GetDouble(), "saved overlay highPassBaseHz");
        Assert(profile.TryGetProperty("plosiveOutputRmsCeiling", out var plosive), "saved overlay should keep plosiveOutputRmsCeiling");
        AssertEqual(0.75, plosive.GetDouble(), "saved overlay plosiveOutputRmsCeiling");
        Assert(!profile.TryGetProperty("presenceCenterHz", out _), "saved overlay should not expand unspecified profile fields");
    }
    finally
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }
}

static void AudioLabRunnerWritesRawAndOutputReferencesToRunMetadata()
{
    var dir = Path.Combine(Path.GetTempPath(), "LanPttAudioLab.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        WavFile.WritePcm16Mono(Path.Combine(dir, "raw.wav"), 16000, MakeSineSamples(16000, 0.7, 220, 2000));
        File.WriteAllText(Path.Combine(dir, "lab-presets.json"),
            "{\"recording\":{\"seconds\":1,\"sampleRate\":16000,\"bitsPerSample\":16,\"channels\":1,\"frameMilliseconds\":20,\"inputDeviceId\":-1},\"presets\":[{\"name\":\"default-50-8\",\"strength\":50,\"maxGainMultiplier\":8,\"profile\":\"default\"}]}",
            System.Text.Encoding.UTF8);

        _ = AudioLabRunner.RunAllPresets(dir, AudioLabRunner.PitchSweep);

        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "run-type.json"), System.Text.Encoding.UTF8));
        var root = metadata.RootElement;
        AssertEqual("raw.wav", root.GetProperty("rawFileName").GetString(), "metadata raw file name");
        Assert(root.GetProperty("rawBytes").GetInt64() > 44, "metadata raw bytes should include WAV payload");
        AssertEqual(1, root.GetProperty("presetCount").GetInt32(), "metadata preset count");
        AssertEqual("default-50-8", root.GetProperty("presetNames")[0].GetString(), "metadata preset name");
        AssertEqual("outputs/default-50-8.wav", root.GetProperty("outputsFileNames")[0].GetString()?.Replace('\\', '/'), "metadata output file name");
    }
    finally
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }
}

static void WavFileRoundTripsPcm16MonoSamples()
{
    var dir = Path.Combine(Path.GetTempPath(), "LanPttAudioLab.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        var path = Path.Combine(dir, "roundtrip.wav");
        var samples = new short[] { 0, 1200, -1200, short.MaxValue, short.MinValue + 1 };

        WavFile.WritePcm16Mono(path, 16000, samples);
        var loaded = WavFile.ReadPcm16Mono(path);

        AssertEqual(16000, loaded.SampleRate, "round-trip sample rate");
        AssertEqual(samples.Length, loaded.Samples.Length, "round-trip sample count");
        for (int i = 0; i < samples.Length; i++)
        {
            AssertEqual(samples[i], loaded.Samples[i], "round-trip sample " + i);
        }
    }
    finally
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }
}

static void AudioMetricsCalculatesBasicPcm16Values()
{
    var samples = new short[] { 0, 30000, -30000, 1000, 100, -100, 0, 0 };
    var metrics = AudioMetrics.Calculate(samples, sampleRate: 1000, frameMilliseconds: 4);

    Assert(metrics.Rms > 15000 && metrics.Rms < 16000, "RMS should reflect sample energy: " + metrics.Rms);
    AssertEqual(30000, metrics.Peak, "peak");
    AssertEqual(2, metrics.NearCeilingCount, "near-ceiling count");
    AssertEqual(2, metrics.FrameRms.Count, "frame RMS count");
    Assert(metrics.LowEnergyFrameRatio > 0.4 && metrics.LowEnergyFrameRatio < 0.6, "one of two frames should be low energy: " + metrics.LowEnergyFrameRatio);
}

static void PitchSweepAnalyzerSegmentsVoicedRegionsAndEstimatesPitch()
{
    var sampleRate = 16000;
    var samples = new List<short>();
    samples.AddRange(new short[sampleRate / 5]);
    samples.AddRange(MakeSineSamples(sampleRate, seconds: 1.0, frequencyHz: 220, amplitude: 8000));
    samples.AddRange(new short[sampleRate / 5]);
    samples.AddRange(MakeSineSamples(sampleRate, seconds: 1.0, frequencyHz: 440, amplitude: 8000));

    var segments = PitchSweepAnalyzer.DetectVoicedSegments(samples.ToArray(), sampleRate);

    AssertEqual(2, segments.Count, "voiced segment count");
    Assert(Math.Abs(segments[0].PitchHz - 220) < 15, "first pitch should be close to 220Hz: " + segments[0].PitchHz);
    Assert(Math.Abs(segments[1].PitchHz - 440) < 25, "second pitch should be close to 440Hz: " + segments[1].PitchHz);
    Assert(segments[0].PitchConfidence > 0.5, "first pitch confidence should be useful: " + segments[0].PitchConfidence);
    Assert(segments[1].PitchConfidence > 0.5, "second pitch confidence should be useful: " + segments[1].PitchConfidence);
}

static void PitchSweepAnalyzerReportsDroppedShortVoicedSegments()
{
    var sampleRate = 16000;
    var samples = new List<short>();
    samples.AddRange(MakeSineSamples(sampleRate, seconds: 0.3, frequencyHz: 220, amplitude: 8000));
    samples.AddRange(new short[sampleRate / 5]);
    samples.AddRange(MakeSineSamples(sampleRate, seconds: 0.8, frequencyHz: 440, amplitude: 8000));

    var result = PitchSweepAnalyzer.DetectVoicedSegmentsWithDroppedCount(samples.ToArray(), sampleRate);

    AssertEqual(1, result.DroppedShortSegmentCount, "dropped short segment count");
    AssertEqual(1, result.Segments.Count, "kept voiced segment count");
}

static void VoiceEnhancerBypassesDisabledEnhancement()
{
    var audio = new AudioSettings
    {
        SampleRate = 16000,
        Channels = 1,
        BitsPerSample = 16,
        FrameMilliseconds = 20,
        Enhancement = new AudioEnhancementSettings { Enabled = false, Strength = 80 }
    };
    var input = MakeSineFrame(audio.FrameSamples, amplitude: 900);
    var output = VoiceEnhancer.ProcessPcm16Mono(input, audio);

    AssertEqual(input.Length, output.Length, "disabled output length");
    for (int i = 0; i < input.Length; i++)
    {
        if (input[i] != output[i])
        {
            throw new InvalidOperationException("disabled enhancer changed byte " + i);
        }
    }
}

static void VoiceEnhancerBoostsQuietVoice()
{
    var audio = new AudioSettings
    {
        SampleRate = 16000,
        Channels = 1,
        BitsPerSample = 16,
        FrameMilliseconds = 20,
        Enhancement = new AudioEnhancementSettings { Enabled = true, Strength = 80, MaxGainMultiplier = 8 }
    };
    var input = MakeSineFrame(audio.FrameSamples, amplitude: 700);
    var output = VoiceEnhancer.ProcessPcm16Mono(input, audio);
    var before = RmsPcm16(input);
    var after = RmsPcm16(output);
    var peak = PeakPcm16(output);

    Assert(after > before * 1.5, "enhanced frame did not materially increase RMS");
    Assert(peak <= 30000, "enhanced frame clipped too aggressively: " + peak);
}

static void VoiceEnhancerStrengthZeroKeepsRmsConservative()
{
    var audio = new AudioSettings
    {
        SampleRate = 16000,
        Channels = 1,
        BitsPerSample = 16,
        FrameMilliseconds = 20,
        Enhancement = new AudioEnhancementSettings { Enabled = true, Strength = 0, MaxGainMultiplier = 100 }
    };
    var input = MakeSineFrame(audio.FrameSamples, amplitude: 1200);
    var output = VoiceEnhancer.ProcessPcm16Mono(input, audio);
    var before = RmsPcm16(input);
    var after = RmsPcm16(output);

    Assert(after <= before * 1.7, "strength 0 should stay conservative; before RMS " + before + ", after RMS " + after);
}

static void VoiceEnhancerStrengthOneHundredIsLouderForMediumVoice()
{
    var low = new AudioSettings
    {
        SampleRate = 16000,
        Channels = 1,
        BitsPerSample = 16,
        FrameMilliseconds = 20,
        Enhancement = new AudioEnhancementSettings { Enabled = true, Strength = 0, MaxGainMultiplier = 8 }
    };
    var high = new AudioSettings
    {
        SampleRate = 16000,
        Channels = 1,
        BitsPerSample = 16,
        FrameMilliseconds = 20,
        Enhancement = new AudioEnhancementSettings { Enabled = true, Strength = 100, MaxGainMultiplier = 8 }
    };
    var input = MakeSineFrameAtFrequency(low.FrameSamples, 1000, 10000);
    var lowOutput = VoiceEnhancer.ProcessPcm16Mono(input, low);
    var highOutput = VoiceEnhancer.ProcessPcm16Mono(input, high);
    var lowRms = RmsPcm16(lowOutput);
    var highRms = RmsPcm16(highOutput);
    var peak = PeakPcm16(highOutput);

    Assert(highRms > lowRms * 2.0, "strength 100 with 8x max should clearly boost medium voice; low RMS " + lowRms + ", high RMS " + highRms + ", ratio " + (highRms / Math.Max(0.0001, lowRms)));
    Assert(peak <= 30000, "strength 100 medium frame exceeded output ceiling: " + peak);
}

static void VoiceEnhancerStrengthOneHundredDefaultEightBoostsQuietVoice()
{
    var audio = new AudioSettings
    {
        SampleRate = 16000,
        Channels = 1,
        BitsPerSample = 16,
        FrameMilliseconds = 20,
        Enhancement = new AudioEnhancementSettings { Enabled = true, Strength = 100, MaxGainMultiplier = 8 }
    };
    var input = MakeSineFrameAtFrequency(audio.FrameSamples, 1000, 300);
    var output = VoiceEnhancer.ProcessPcm16Mono(input, audio);
    var before = RmsPcm16(input);
    var after = RmsPcm16(output);
    var peak = PeakPcm16(output);

    Assert(after > before * 6.0, "strength 100 with 8x max should boost quiet voice by more than 6x; before RMS " + before + ", after RMS " + after);
    Assert(peak <= 30000, "strength 100 8x quiet frame exceeded output ceiling: " + peak);
}

static void VoiceEnhancerConfiguredHundredIsStrongerThanEightForVeryQuietVoice()
{
    var eight = new AudioSettings
    {
        SampleRate = 16000,
        Channels = 1,
        BitsPerSample = 16,
        FrameMilliseconds = 20,
        Enhancement = new AudioEnhancementSettings { Enabled = true, Strength = 100, MaxGainMultiplier = 8 }
    };
    var hundred = new AudioSettings
    {
        SampleRate = 16000,
        Channels = 1,
        BitsPerSample = 16,
        FrameMilliseconds = 20,
        Enhancement = new AudioEnhancementSettings { Enabled = true, Strength = 100, MaxGainMultiplier = 100 }
    };
    var input = MakeSineFrame(eight.FrameSamples, amplitude: 80);
    var eightOutput = VoiceEnhancer.ProcessPcm16Mono(input, eight);
    var hundredOutput = VoiceEnhancer.ProcessPcm16Mono(input, hundred);
    var eightRms = RmsPcm16(eightOutput);
    var hundredRms = RmsPcm16(hundredOutput);
    var peak = PeakPcm16(hundredOutput);

    Assert(hundredRms > eightRms * 5.0, "100x max gain should be much stronger than 8x for very quiet voice; 8x RMS " + eightRms + ", 100x RMS " + hundredRms + ", ratio " + (hundredRms / Math.Max(0.0001, eightRms)));
    Assert(peak <= 30000, "strength 100 100x very quiet frame exceeded output ceiling: " + peak);
}

static void VoiceEnhancerConfiguredHundredStronglyBoostsLowRichNormalVoice()
{
    var audio = new AudioSettings
    {
        SampleRate = 16000,
        Channels = 1,
        BitsPerSample = 16,
        FrameMilliseconds = 20,
        Enhancement = new AudioEnhancementSettings { Enabled = true, Strength = 100, MaxGainMultiplier = 100 }
    };
    var input = MakeCompositeSineFrame(
        audio.FrameSamples,
        startSample: 0,
        (120, 650),
        (220, 650),
        (1000, 450),
        (2200, 250));
    var output = VoiceEnhancer.ProcessPcm16Mono(input, audio);
    var before = RmsPcm16(input);
    var after = RmsPcm16(output);
    var peak = PeakPcm16(output);

    Assert(after > before * 4.0, "100x max should strongly boost low-rich normal voice instead of treating it as plosive/noise; before RMS " + before + ", after RMS " + after + ", ratio " + (after / Math.Max(0.0001, before)));
    Assert(peak <= 30000, "100x low-rich normal voice exceeded output ceiling: " + peak);
}

static void VoiceEnhancerKeepsConsecutiveLowRichSpeechFramesAudibleAndStable()
{
    var audio = new AudioSettings
    {
        SampleRate = 16000,
        Channels = 1,
        BitsPerSample = 16,
        FrameMilliseconds = 20,
        Enhancement = new AudioEnhancementSettings { Enabled = true, Strength = 100, MaxGainMultiplier = 100 }
    };
    var enhancer = new VoiceEnhancer(audio);
    var outputRms = new List<double>();

    for (int frame = 0; frame < 12; frame++)
    {
        var input = MakeCompositeSineFrame(
            audio.FrameSamples,
            startSample: frame * audio.FrameSamples,
            (120, 650),
            (220, 650),
            (1000, 450),
            (2200, 250));
        var output = enhancer.ProcessPcm16Mono(input);
        var before = RmsPcm16(input);
        var after = RmsPcm16(output);
        outputRms.Add(after);

        Assert(after > before * 3.0, "100x low-rich speech frame " + frame + " should remain clearly audible; before RMS " + before + ", after RMS " + after + ", ratio " + (after / Math.Max(0.0001, before)));
    }

    var min = outputRms.Min();
    var max = outputRms.Max();
    Assert(max / Math.Max(0.0001, min) < 1.8, "consecutive enhanced speech frames should not pump/drop out; min RMS " + min + ", max RMS " + max + ", ratio " + (max / Math.Max(0.0001, min)));
}

static void VoiceEnhancerStrengthOneHundredAttenuatesLowFrequencyMoreThanMidFrequency()
{
    var audio = new AudioSettings
    {
        SampleRate = 16000,
        Channels = 1,
        BitsPerSample = 16,
        FrameMilliseconds = 20,
        Enhancement = new AudioEnhancementSettings { Enabled = true, Strength = 100, MaxGainMultiplier = 8 }
    };
    var low = MakeSineFrameAtFrequency(audio.FrameSamples, 90, 1200);
    var mid = MakeSineFrameAtFrequency(audio.FrameSamples, 1000, 1200);
    var lowOut = VoiceEnhancer.ProcessPcm16Mono(low, audio);
    var midOut = VoiceEnhancer.ProcessPcm16Mono(mid, audio);
    var lowRatio = RmsPcm16(lowOut) / Math.Max(0.0001, RmsPcm16(low));
    var midRatio = RmsPcm16(midOut) / Math.Max(0.0001, RmsPcm16(mid));

    Assert(lowRatio < midRatio * 0.45, "90Hz should be attenuated much more than 1000Hz; low ratio " + lowRatio + ", mid ratio " + midRatio);
}

static void VoiceEnhancerStrengthOneHundredImprovesClarityBandOverMuddyLowMid()
{
    var audio = new AudioSettings
    {
        SampleRate = 16000,
        Channels = 1,
        BitsPerSample = 16,
        FrameMilliseconds = 20,
        Enhancement = new AudioEnhancementSettings { Enabled = true, Strength = 100, MaxGainMultiplier = 8 }
    };
    var muddy = MakeSineFrameAtFrequency(audio.FrameSamples, 250, 1200);
    var clarity = MakeSineFrameAtFrequency(audio.FrameSamples, 2000, 1200);
    var muddyOut = VoiceEnhancer.ProcessPcm16Mono(muddy, audio);
    var clarityOut = VoiceEnhancer.ProcessPcm16Mono(clarity, audio);
    var muddyRatio = RmsPcm16(muddyOut) / Math.Max(0.0001, RmsPcm16(muddy));
    var clarityRatio = RmsPcm16(clarityOut) / Math.Max(0.0001, RmsPcm16(clarity));

    Assert(clarityRatio > muddyRatio * 1.5, "2kHz clarity band should be at least 1.5x stronger than 250Hz muddy low-mid; muddy ratio " + muddyRatio + ", clarity ratio " + clarityRatio);
}

static void VoiceEnhancerStrengthOneHundredLiftsPresenceBandOverLowerSpeechBand()
{
    var audio = new AudioSettings
    {
        SampleRate = 16000,
        Channels = 1,
        BitsPerSample = 16,
        FrameMilliseconds = 20,
        Enhancement = new AudioEnhancementSettings { Enabled = true, Strength = 100, MaxGainMultiplier = 8 }
    };
    var lowerSpeech = MakeSineFrameAtFrequency(audio.FrameSamples, 1000, 1200);
    var presence = MakeSineFrameAtFrequency(audio.FrameSamples, 2200, 1200);
    var lowerOut = VoiceEnhancer.ProcessPcm16Mono(lowerSpeech, audio);
    var presenceOut = VoiceEnhancer.ProcessPcm16Mono(presence, audio);
    var lowerRatio = RmsPcm16(lowerOut) / Math.Max(0.0001, RmsPcm16(lowerSpeech));
    var presenceRatio = RmsPcm16(presenceOut) / Math.Max(0.0001, RmsPcm16(presence));

    Assert(presenceRatio > lowerRatio * 1.2, "2.2kHz presence band should be measurably lifted over 1kHz lower speech band; 1kHz ratio " + lowerRatio + ", 2.2kHz ratio " + presenceRatio);
}

static void VoiceEnhancerStrengthOneHundredReducesPlosiveBurst()
{
    var audio = new AudioSettings
    {
        SampleRate = 16000,
        Channels = 1,
        BitsPerSample = 16,
        FrameMilliseconds = 20,
        Enhancement = new AudioEnhancementSettings { Enabled = true, Strength = 100, MaxGainMultiplier = 8 }
    };
    var burst = MakeBurstFrame(audio.FrameSamples, frequencyHz: 120, amplitude: 18000, activeSamples: audio.FrameSamples / 4);
    var output = VoiceEnhancer.ProcessPcm16Mono(burst, audio);
    var before = RmsPcm16(burst);
    var after = RmsPcm16(output);
    var peak = PeakPcm16(output);

    Assert(after < before * 0.75, "120Hz plosive-ish burst should still be reduced without muting normal speech; before RMS " + before + ", after RMS " + after + ", ratio " + (after / Math.Max(0.0001, before)));
    Assert(peak <= 30000, "plosive burst output exceeded peak ceiling: " + peak);
}

static void VoiceEnhancerStrengthOneHundredKeepsPlosiveProtectionModerate()
{
    var audio = new AudioSettings
    {
        SampleRate = 16000,
        Channels = 1,
        BitsPerSample = 16,
        FrameMilliseconds = 20,
        Enhancement = new AudioEnhancementSettings { Enabled = true, Strength = 100, MaxGainMultiplier = 8 }
    };
    var burst = MakeBurstFrame(audio.FrameSamples, frequencyHz: 120, amplitude: 18000, activeSamples: audio.FrameSamples / 4);
    var output = VoiceEnhancer.ProcessPcm16Mono(burst, audio);
    var before = RmsPcm16(burst);
    var after = RmsPcm16(output);
    var peak = PeakPcm16(output);

    var ratio = after / Math.Max(0.0001, before);

    Assert(ratio > 0.45, "plosive protection should not over-filter into dropouts; before RMS " + before + ", after RMS " + after + ", ratio " + ratio);
    Assert(ratio < 0.75, "plosive protection should still reduce low-frequency bursts; before RMS " + before + ", after RMS " + after + ", ratio " + ratio);
    Assert(peak <= 30000, "strong plosive burst output exceeded peak ceiling: " + peak);
}

static void VoiceEnhancerLowFrequencyDominantGainCapIsGentle()
{
    var audio = new AudioSettings
    {
        SampleRate = 16000,
        Channels = 1,
        BitsPerSample = 16,
        FrameMilliseconds = 20,
        Enhancement = new AudioEnhancementSettings { Enabled = true, Strength = 100, MaxGainMultiplier = 8 }
    };
    var input = MakeSineFrameAtFrequency(audio.FrameSamples, 120, 18000);
    var output = VoiceEnhancer.ProcessPcm16Mono(input, audio);
    var before = RmsPcm16(input);
    var after = RmsPcm16(output);
    var ratio = after / Math.Max(0.0001, before);

    Assert(ratio <= 0.70, "low-frequency dominant gain cap should gently limit high-energy low-frequency bursts; before RMS " + before + ", after RMS " + after + ", ratio " + ratio);
    Assert(ratio >= 0.45, "low-frequency dominant gain cap should not mute high-energy low-frequency bursts into dropouts; before RMS " + before + ", after RMS " + after + ", ratio " + ratio);
}

static void VoiceEnhancerCascadeBoundaryAvoidsAbruptLowFrequencyDrop()
{
    var strength49 = new AudioSettings
    {
        SampleRate = 16000,
        Channels = 1,
        BitsPerSample = 16,
        FrameMilliseconds = 20,
        Enhancement = new AudioEnhancementSettings { Enabled = true, Strength = 49, MaxGainMultiplier = 8 }
    };
    var strength51 = new AudioSettings
    {
        SampleRate = 16000,
        Channels = 1,
        BitsPerSample = 16,
        FrameMilliseconds = 20,
        Enhancement = new AudioEnhancementSettings { Enabled = true, Strength = 51, MaxGainMultiplier = 8 }
    };
    var input = MakeSineFrameAtFrequency(strength49.FrameSamples, 90, 1200);
    var out49 = VoiceEnhancer.ProcessPcm16Mono(input, strength49);
    var out51 = VoiceEnhancer.ProcessPcm16Mono(input, strength51);
    var ratio49 = RmsPcm16(out49) / Math.Max(0.0001, RmsPcm16(input));
    var ratio51 = RmsPcm16(out51) / Math.Max(0.0001, RmsPcm16(input));

    Assert(ratio51 >= ratio49 * 0.75, "strength 51 should not abruptly drop low-frequency speech compared with strength 49; strength49 ratio " + ratio49 + ", strength51 ratio " + ratio51);
    Assert(ratio51 <= ratio49 * 1.5, "strength 51 should remain in the same low-frequency behavior band as strength 49; strength49 ratio " + ratio49 + ", strength51 ratio " + ratio51);
}

static void VoiceEnhancerStrengthZeroPreservesLowMaleFundamental()
{
    var audio = new AudioSettings
    {
        SampleRate = 16000,
        Channels = 1,
        BitsPerSample = 16,
        FrameMilliseconds = 20,
        Enhancement = new AudioEnhancementSettings { Enabled = true, Strength = 0, MaxGainMultiplier = 8 }
    };
    var input = MakeSineFrameAtFrequency(audio.FrameSamples, 90, 1200);
    var output = VoiceEnhancer.ProcessPcm16Mono(input, audio);
    var before = RmsPcm16(input);
    var after = RmsPcm16(output);
    var ratio = after / Math.Max(0.0001, before);

    Assert(ratio >= 0.5, "strength 0 should not excessively attenuate 90Hz low male fundamental; before RMS " + before + ", after RMS " + after + ", ratio " + ratio);
}

static void VoiceEnhancerStrengthOneHundredPreservesMidFrequencySpeechEnergy()
{
    var lowStrength = new AudioSettings
    {
        SampleRate = 16000,
        Channels = 1,
        BitsPerSample = 16,
        FrameMilliseconds = 20,
        Enhancement = new AudioEnhancementSettings { Enabled = true, Strength = 0, MaxGainMultiplier = 8 }
    };
    var highStrength = new AudioSettings
    {
        SampleRate = 16000,
        Channels = 1,
        BitsPerSample = 16,
        FrameMilliseconds = 20,
        Enhancement = new AudioEnhancementSettings { Enabled = true, Strength = 100, MaxGainMultiplier = 8 }
    };
    var mid = MakeSineFrameAtFrequency(lowStrength.FrameSamples, 1000, 1200);
    var lowOut = VoiceEnhancer.ProcessPcm16Mono(mid, lowStrength);
    var highOut = VoiceEnhancer.ProcessPcm16Mono(mid, highStrength);
    var lowRms = RmsPcm16(lowOut);
    var highRms = RmsPcm16(highOut);

    Assert(highRms >= lowRms * 0.707, "1000Hz speech energy should not be worse than strength 0 by more than 3dB; strength0 RMS " + lowRms + ", strength100 RMS " + highRms);
}

static void VoiceEnhancerRejectsNonPcm16MonoClearly()
{
    var stereo = new AudioSettings
    {
        SampleRate = 16000,
        Channels = 2,
        BitsPerSample = 16,
        FrameMilliseconds = 20,
        Enhancement = new AudioEnhancementSettings { Enabled = true, Strength = 50 }
    };
    var stereoError = AssertThrows<NotSupportedException>(() => new VoiceEnhancer(stereo), "stereo settings should be rejected");
    AssertEqual("语音增强只支持 PCM16 单声道音频。", stereoError.Message, "stereo rejection message");

    var eightBit = new AudioSettings
    {
        SampleRate = 16000,
        Channels = 1,
        BitsPerSample = 8,
        FrameMilliseconds = 20,
        Enhancement = new AudioEnhancementSettings { Enabled = true, Strength = 50 }
    };
    var bitsError = AssertThrows<NotSupportedException>(() => new VoiceEnhancer(eightBit), "8-bit settings should be rejected");
    AssertEqual("语音增强只支持 PCM16 单声道音频。", bitsError.Message, "bits rejection message");
}

static void VoiceEnhancerMatchesSampleRateStrengthAndProfileChanges()
{
    var audio = new AudioSettings
    {
        SampleRate = 16000,
        Channels = 1,
        BitsPerSample = 16,
        FrameMilliseconds = 20,
        Enhancement = new AudioEnhancementSettings { Enabled = true, Strength = 50 }
    };
    var enhancer = new VoiceEnhancer(audio, AudioEnhancementProfile.Default);

    Assert(enhancer.Matches(audio), "same settings should match");
    Assert(!enhancer.Matches(new AudioSettings
    {
        SampleRate = 8000,
        Channels = 1,
        BitsPerSample = 16,
        FrameMilliseconds = 20,
        Enhancement = new AudioEnhancementSettings { Enabled = true, Strength = 50 }
    }), "sample rate change should not match");
    Assert(!enhancer.Matches(new AudioSettings
    {
        SampleRate = 16000,
        Channels = 1,
        BitsPerSample = 16,
        FrameMilliseconds = 20,
        Enhancement = new AudioEnhancementSettings { Enabled = true, Strength = 60 }
    }), "strength change should not match");
    Assert(!enhancer.Matches(new AudioSettings
    {
        SampleRate = 16000,
        Channels = 1,
        BitsPerSample = 16,
        FrameMilliseconds = 20,
        Enhancement = new AudioEnhancementSettings { Enabled = true, Strength = 50, MaxGainMultiplier = 30 }
    }), "max gain multiplier change should not match");
    Assert(!enhancer.Matches(audio, AudioEnhancementProfile.Default with { HighPassBaseHz = 65 }), "profile change should not match");
}

static void Pcm16FrameAppliesOutputVolume()
{
    var input = new byte[4];
    WriteSample(input, 0, 12000);
    WriteSample(input, 1, -8000);

    var half = Pcm16Frame.ApplyVolume(input, 50);
    AssertEqual(6000, BitConverter.ToInt16(half, 0), "first half-volume sample");
    AssertEqual(-4000, BitConverter.ToInt16(half, 2), "second half-volume sample");

    var muted = Pcm16Frame.ApplyVolume(input, 0);
    AssertEqual(0, BitConverter.ToInt16(muted, 0), "muted first sample");
    AssertEqual(0, BitConverter.ToInt16(muted, 2), "muted second sample");
}

static void Pcm16FrameAppliesOutputVolumeInPlace()
{
    var input = new byte[4];
    WriteSample(input, 0, 12000);
    WriteSample(input, 1, -8000);

    Pcm16Frame.ApplyVolumeInPlace(input, 50);
    AssertEqual(6000, BitConverter.ToInt16(input, 0), "first in-place half-volume sample");
    AssertEqual(-4000, BitConverter.ToInt16(input, 2), "second in-place half-volume sample");

    Pcm16Frame.ApplyVolumeInPlace(input, 100);
    AssertEqual(6000, BitConverter.ToInt16(input, 0), "100 percent keeps first in-place sample");
    AssertEqual(-4000, BitConverter.ToInt16(input, 2), "100 percent keeps second in-place sample");
}

static void MmsAudioPlaybackKeepsPreparedFlagForSubmit()
{
    const uint whdrDone = 0x00000001;
    const uint whdrPrepared = 0x00000002;
    const uint whdrBeginLoop = 0x00000004;
    const uint whdrInQueue = 0x00000010;

    var flags = MmsAudioPlayback.PrepareHeaderFlagsForSubmit(whdrDone | whdrPrepared | whdrBeginLoop | whdrInQueue);

    Assert((flags & whdrPrepared) != 0, "submit flags lost WHDR_PREPARED");
    Assert((flags & whdrDone) == 0, "submit flags kept WHDR_DONE");
    Assert((flags & whdrInQueue) == 0, "submit flags kept WHDR_INQUEUE");
    Assert((flags & whdrBeginLoop) != 0, "submit flags should not clear unrelated flags");
}

static void MmsAudioCaptureKeepsPreparedFlagForReuse()
{
    const uint whdrDone = 0x00000001;
    const uint whdrPrepared = 0x00000002;
    const uint whdrBeginLoop = 0x00000004;
    const uint whdrInQueue = 0x00000010;

    var flags = MmsAudioCapture.PrepareHeaderFlagsForReuse(whdrDone | whdrPrepared | whdrBeginLoop | whdrInQueue);

    Assert((flags & whdrPrepared) != 0, "reuse flags lost WHDR_PREPARED");
    Assert((flags & whdrDone) == 0, "reuse flags kept WHDR_DONE");
    Assert((flags & whdrInQueue) == 0, "reuse flags kept WHDR_INQUEUE");
    Assert((flags & whdrBeginLoop) != 0, "reuse flags should not clear WHDR_BEGINLOOP");
}

static void VoiceUdpClientAllowsLoopbackButFiltersLocalLanPackets()
{
    var localLan = IPAddress.Parse("192.168.1.20");
    var otherLan = IPAddress.Parse("192.168.1.21");
    var localAddresses = new[] { localLan };

    Assert(!VoiceUdpClient.ShouldDropPacketFrom(IPAddress.Loopback, localAddresses), "IPv4 loopback should be accepted for self-test");
    Assert(!VoiceUdpClient.ShouldDropPacketFrom(IPAddress.IPv6Loopback, localAddresses), "IPv6 loopback should be accepted for self-test");
    Assert(VoiceUdpClient.ShouldDropPacketFrom(localLan, localAddresses), "non-loopback local LAN address should still be filtered");
    Assert(!VoiceUdpClient.ShouldDropPacketFrom(otherLan, localAddresses), "remote LAN peer should be accepted");
}

static void TransmitStateGateTransitionsAtomically()
{
    var gate = new LanPttIntercom.TransmitStateGate();
    var started = 0;
    Parallel.For(0, 64, _ =>
    {
        if (gate.TryStart())
        {
            Interlocked.Increment(ref started);
        }
    });

    AssertEqual(1, started, "only one concurrent start should win");
    Assert(gate.IsTransmitting, "gate should be transmitting after winning start");

    var stopped = 0;
    Parallel.For(0, 64, _ =>
    {
        if (gate.TryStop())
        {
            Interlocked.Increment(ref stopped);
        }
    });

    AssertEqual(1, stopped, "only one concurrent stop should win");
    Assert(!gate.IsTransmitting, "gate should not be transmitting after winning stop");
}

static void WindowPlacementMovesOffscreenBoundsIntoPreferredVisibleWorkArea()
{
    var preferred = new Rectangle(1920, 0, 1280, 984);
    var workingAreas = new[]
    {
        new Rectangle(0, 0, 1920, 1040),
        preferred
    };
    var offscreen = new Rectangle(-5000, -3000, 760, 780);

    var restored = WindowPlacement.NormalizeToVisibleWorkArea(offscreen, workingAreas, minimumSize: new Size(700, 760), preferredWorkingArea: preferred);

    Assert(preferred.Contains(restored), "restored bounds should be contained by preferred work area: " + restored);
}

static void WindowPlacementFallsBackWhenPreferredWorkAreaIsUnavailable()
{
    var fallback = new Rectangle(0, 0, 1920, 1040);
    var workingAreas = new[]
    {
        fallback,
        new Rectangle(3000, 0, 1280, 984)
    };
    var unavailablePreferred = new Rectangle(5000, 5000, 1024, 768);
    var offscreen = new Rectangle(-5000, -3000, 760, 780);

    var restored = WindowPlacement.NormalizeToVisibleWorkArea(offscreen, workingAreas, minimumSize: new Size(700, 760), preferredWorkingArea: unavailablePreferred);

    Assert(fallback.Contains(restored), "restored bounds should use deterministic fallback when preferred is unavailable: " + restored);
}

static void WindowPlacementMovesDisconnectedNegativeCoordinateBoundsIntoCurrentScreen()
{
    var current = new Rectangle(0, 0, 1920, 1040);
    var oldNegativeMonitorBounds = new Rectangle(-1500, 40, 760, 780);

    var restored = WindowPlacement.NormalizeToVisibleWorkArea(oldNegativeMonitorBounds, new[] { current }, minimumSize: new Size(700, 760), preferredWorkingArea: current);

    Assert(current.Contains(restored), "bounds from a disconnected negative-coordinate monitor should move into current screen: " + restored);
}

static void WindowPlacementSupportsNegativeCoordinateMonitors()
{
    var negative = new Rectangle(-1600, 0, 1600, 900);
    var primary = new Rectangle(0, 0, 1920, 1040);
    var workingAreas = new[] { primary, negative };
    var bounds = new Rectangle(-1500, 40, 760, 780);

    var restored = WindowPlacement.NormalizeToVisibleWorkArea(bounds, workingAreas, minimumSize: new Size(700, 760), preferredWorkingArea: primary);

    AssertEqual(bounds, restored, "negative-coordinate visible bounds should be preserved");
}

static void WindowPlacementPreservesPartiallyVisibleBounds()
{
    var primary = new Rectangle(0, 0, 1920, 1040);
    var bounds = new Rectangle(1800, 120, 760, 780);

    var restored = WindowPlacement.NormalizeToVisibleWorkArea(bounds, new[] { primary }, minimumSize: new Size(700, 760), preferredWorkingArea: primary);

    AssertEqual(bounds, restored, "partially visible bounds should be preserved");
}

static void WindowPlacementPreservesOversizedBoundsWhenVisible()
{
    var primary = new Rectangle(0, 0, 1024, 700);
    var oversized = new Rectangle(-100, -50, 1400, 900);

    var restored = WindowPlacement.NormalizeToVisibleWorkArea(oversized, new[] { primary }, minimumSize: new Size(700, 760), preferredWorkingArea: primary);

    AssertEqual(oversized, restored, "oversized bounds should be preserved when they intersect a work area");
}

static void WindowPlacementPreservesOversizedCrossScreenPartiallyVisibleBounds()
{
    var primary = new Rectangle(0, 0, 1920, 1040);
    var secondary = new Rectangle(1920, 0, 1280, 984);
    var crossScreen = new Rectangle(1700, 20, 2000, 900);

    var restored = WindowPlacement.NormalizeToVisibleWorkArea(crossScreen, new[] { primary, secondary }, minimumSize: new Size(700, 760), preferredWorkingArea: primary);

    AssertEqual(crossScreen, restored, "oversized cross-screen bounds should be preserved when partially visible");
}

static void WindowPlacementRecentersEmptyBoundsIntoPreferredWorkArea()
{
    var primary = new Rectangle(0, 0, 1920, 1040);
    var preferred = new Rectangle(1920, 0, 1280, 984);
    var minimum = new Size(700, 760);

    var restored = WindowPlacement.NormalizeToVisibleWorkArea(Rectangle.Empty, new[] { primary, preferred }, minimum, preferred);

    Assert(preferred.Contains(restored), "empty bounds should be moved into preferred work area: " + restored);
    AssertEqual(minimum.Width, restored.Width, "empty bounds should expand to minimum width");
    AssertEqual(minimum.Height, restored.Height, "empty bounds should expand to minimum height");
}

static void WindowPlacementExpandsSubMinimumBoundsIntoCurrentWorkArea()
{
    var current = new Rectangle(0, 0, 500, 400);
    var minimum = new Size(700, 760);
    var tiny = new Rectangle(1, 1, 1, 1);

    var restored = WindowPlacement.NormalizeToVisibleWorkArea(tiny, new[] { current }, minimum, current);

    Assert(current.Contains(restored), "sub-minimum bounds should be moved into current work area: " + restored);
    AssertEqual(current.Width, restored.Width, "sub-minimum width should clamp to small work area width");
    AssertEqual(current.Height, restored.Height, "sub-minimum height should clamp to small work area height");
}

static byte[] MakeSineFrame(int samples, short amplitude)
{
    return MakeSineFrameAtFrequency(samples, 220, amplitude);
}

static byte[] MakeSineFrameAtFrequency(int samples, double frequencyHz, short amplitude)
{
    var data = new byte[samples * 2];
    for (int i = 0; i < samples; i++)
    {
        var sample = (short)(Math.Sin(i * 2.0 * Math.PI * frequencyHz / 16000.0) * amplitude);
        data[i * 2] = (byte)(sample & 0xFF);
        data[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
    }
    return data;
}

static short[] MakeSineSamples(int sampleRate, double seconds, double frequencyHz, short amplitude)
{
    var count = (int)Math.Round(sampleRate * seconds);
    var data = new short[count];
    for (int i = 0; i < count; i++)
    {
        data[i] = (short)(Math.Sin(i * 2.0 * Math.PI * frequencyHz / sampleRate) * amplitude);
    }

    return data;
}

static byte[] MakeCompositeSineFrame(int samples, int startSample, params (double FrequencyHz, short Amplitude)[] components)
{
    var data = new byte[samples * 2];
    for (int i = 0; i < samples; i++)
    {
        double value = 0;
        foreach (var component in components)
        {
            value += Math.Sin((startSample + i) * 2.0 * Math.PI * component.FrequencyHz / 16000.0) * component.Amplitude;
        }

        var sample = (short)Math.Clamp((int)Math.Round(value), short.MinValue, short.MaxValue);
        data[i * 2] = (byte)(sample & 0xFF);
        data[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
    }
    return data;
}

static byte[] MakeBurstFrame(int samples, double frequencyHz, short amplitude, int activeSamples)
{
    var data = new byte[samples * 2];
    var active = Math.Clamp(activeSamples, 0, samples);
    for (int i = 0; i < active; i++)
    {
        var envelope = 1.0 - (i / Math.Max(1.0, active));
        var sample = (short)(Math.Sin(i * 2.0 * Math.PI * frequencyHz / 16000.0) * amplitude * envelope);
        data[i * 2] = (byte)(sample & 0xFF);
        data[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
    }
    return data;
}

static double RmsPcm16(byte[] data)
{
    double sum = 0;
    var count = data.Length / 2;
    for (int i = 0; i < count; i++)
    {
        var sample = BitConverter.ToInt16(data, i * 2);
        sum += sample * sample;
    }
    return Math.Sqrt(sum / Math.Max(1, count));
}

static int PeakPcm16(byte[] data)
{
    var peak = 0;
    for (int i = 0; i < data.Length / 2; i++)
    {
        peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(data, i * 2)));
    }
    return peak;
}

static void WriteSample(byte[] data, int sampleIndex, short value)
{
    data[sampleIndex * 2] = (byte)(value & 0xFF);
    data[sampleIndex * 2 + 1] = (byte)((value >> 8) & 0xFF);
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException(message + ": expected " + expected + ", actual " + actual);
    }
}

static T AssertThrows<T>(Action action, string message)
    where T : Exception
{
    try
    {
        action();
    }
    catch (T ex)
    {
        return ex;
    }
    catch (Exception ex)
    {
        throw new InvalidOperationException(message + ": expected " + typeof(T).Name + ", actual " + ex.GetType().Name);
    }

    throw new InvalidOperationException(message + ": expected " + typeof(T).Name + " but no exception was thrown");
}
