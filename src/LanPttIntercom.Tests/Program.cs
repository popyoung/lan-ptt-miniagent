using System.Text.Json;
using LanPttIntercom.Audio;
using LanPttIntercom.Models;
using LanPttIntercom.Storage;

var tests = new (string Name, Action Run)[]
{
    ("SettingsStore default path is executable directory settings.json", SettingsStoreDefaultPathUsesBaseDirectory),
    ("SettingsStore saves and backs up only inside configured directory", SettingsStoreUsesConfiguredDirectoryOnly),
    ("VoiceEnhancer bypasses disabled audio enhancement", VoiceEnhancerBypassesDisabledEnhancement),
    ("VoiceEnhancer boosts quiet PCM16 mono voice without clipping", VoiceEnhancerBoostsQuietVoice),
    ("Pcm16Frame applies output volume percentage", Pcm16FrameAppliesOutputVolume),
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
