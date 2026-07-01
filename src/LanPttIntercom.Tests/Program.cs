using System.Net;
using System.Text.Json;
using LanPttIntercom.Audio;
using LanPttIntercom.Models;
using LanPttIntercom.Network;
using LanPttIntercom.Storage;

var tests = new (string Name, Action Run)[]
{
    ("SettingsStore default path is executable directory settings.json", SettingsStoreDefaultPathUsesBaseDirectory),
    ("SettingsStore saves and backs up only inside configured directory", SettingsStoreUsesConfiguredDirectoryOnly),
    ("VoiceEnhancer bypasses disabled audio enhancement", VoiceEnhancerBypassesDisabledEnhancement),
    ("VoiceEnhancer boosts quiet PCM16 mono voice without clipping", VoiceEnhancerBoostsQuietVoice),
    ("VoiceEnhancer strength zero keeps RMS conservative", VoiceEnhancerStrengthZeroKeepsRmsConservative),
    ("VoiceEnhancer strength one hundred is louder for medium voice", VoiceEnhancerStrengthOneHundredIsLouderForMediumVoice),
    ("VoiceEnhancer rejects non PCM16 mono settings clearly", VoiceEnhancerRejectsNonPcm16MonoClearly),
    ("VoiceEnhancer matches sample rate and strength changes", VoiceEnhancerMatchesSampleRateAndStrengthChanges),
    ("Pcm16Frame applies output volume percentage", Pcm16FrameAppliesOutputVolume),
    ("Pcm16Frame applies output volume in place", Pcm16FrameAppliesOutputVolumeInPlace),
    ("MmsAudioPlayback keeps WAVEHDR prepared flag for submit", MmsAudioPlaybackKeepsPreparedFlagForSubmit),
    ("MmsAudioCapture keeps WAVEHDR prepared flag for reuse", MmsAudioCaptureKeepsPreparedFlagForReuse),
    ("VoiceUdpClient allows loopback but filters local LAN packets", VoiceUdpClientAllowsLoopbackButFiltersLocalLanPackets),
    ("TransmitStateGate transitions atomically under contention", TransmitStateGateTransitionsAtomically)
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
                    Strength = 65
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
        Enhancement = new AudioEnhancementSettings { Enabled = true, Strength = 80 }
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
        Enhancement = new AudioEnhancementSettings { Enabled = true, Strength = 0 }
    };
    var input = MakeSineFrame(audio.FrameSamples, amplitude: 1200);
    var output = VoiceEnhancer.ProcessPcm16Mono(input, audio);
    var before = RmsPcm16(input);
    var after = RmsPcm16(output);

    Assert(after <= before * 1.5, "strength 0 should stay conservative; before RMS " + before + ", after RMS " + after);
}

static void VoiceEnhancerStrengthOneHundredIsLouderForMediumVoice()
{
    var low = new AudioSettings
    {
        SampleRate = 16000,
        Channels = 1,
        BitsPerSample = 16,
        FrameMilliseconds = 20,
        Enhancement = new AudioEnhancementSettings { Enabled = true, Strength = 0 }
    };
    var high = new AudioSettings
    {
        SampleRate = 16000,
        Channels = 1,
        BitsPerSample = 16,
        FrameMilliseconds = 20,
        Enhancement = new AudioEnhancementSettings { Enabled = true, Strength = 100 }
    };
    var input = MakeSineFrame(low.FrameSamples, amplitude: 10000);
    var lowOutput = VoiceEnhancer.ProcessPcm16Mono(input, low);
    var highOutput = VoiceEnhancer.ProcessPcm16Mono(input, high);
    var lowRms = RmsPcm16(lowOutput);
    var highRms = RmsPcm16(highOutput);
    var peak = PeakPcm16(highOutput);

    Assert(highRms > lowRms * 1.25, "strength 100 should be audibly louder than strength 0 for medium voice; low RMS " + lowRms + ", high RMS " + highRms);
    Assert(peak <= 30000, "strength 100 medium frame exceeded output ceiling: " + peak);
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

static void VoiceEnhancerMatchesSampleRateAndStrengthChanges()
{
    var audio = new AudioSettings
    {
        SampleRate = 16000,
        Channels = 1,
        BitsPerSample = 16,
        FrameMilliseconds = 20,
        Enhancement = new AudioEnhancementSettings { Enabled = true, Strength = 50 }
    };
    var enhancer = new VoiceEnhancer(audio);

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

static byte[] MakeSineFrame(int samples, short amplitude)
{
    var data = new byte[samples * 2];
    for (int i = 0; i < samples; i++)
    {
        var sample = (short)(Math.Sin(i * 2.0 * Math.PI * 220.0 / 16000.0) * amplitude);
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
