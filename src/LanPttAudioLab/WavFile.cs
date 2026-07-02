using System.Text;

namespace LanPttAudioLab;

public sealed record WavPcm16Mono(int SampleRate, short[] Samples)
{
    public byte[] ToBytes()
    {
        var bytes = new byte[Samples.Length * 2];
        for (int i = 0; i < Samples.Length; i++)
        {
            bytes[i * 2] = (byte)(Samples[i] & 0xFF);
            bytes[i * 2 + 1] = (byte)((Samples[i] >> 8) & 0xFF);
        }

        return bytes;
    }
}

public static class WavFile
{
    public static WavPcm16Mono ReadPcm16Mono(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.ASCII);

        RequireFourCc(reader, "RIFF", "WAV 必须以 RIFF 开头。");
        _ = reader.ReadInt32();
        RequireFourCc(reader, "WAVE", "WAV 必须是 WAVE 格式。");

        int? sampleRate = null;
        short? channels = null;
        short? bitsPerSample = null;
        short? formatTag = null;
        byte[]? data = null;

        while (stream.Position + 8 <= stream.Length)
        {
            var chunkId = ReadFourCc(reader);
            var chunkSize = reader.ReadInt32();
            var chunkStart = stream.Position;

            if (chunkId == "fmt ")
            {
                formatTag = reader.ReadInt16();
                channels = reader.ReadInt16();
                sampleRate = reader.ReadInt32();
                _ = reader.ReadInt32();
                _ = reader.ReadInt16();
                bitsPerSample = reader.ReadInt16();
            }
            else if (chunkId == "data")
            {
                data = reader.ReadBytes(chunkSize);
            }

            stream.Position = chunkStart + chunkSize + (chunkSize % 2);
        }

        if (formatTag != 1) throw new FormatException("只支持 PCM WAV。");
        if (channels != 1) throw new FormatException("只支持 mono WAV。");
        if (bitsPerSample != 16) throw new FormatException("只支持 PCM16 WAV。");
        if (sampleRate == null) throw new FormatException("WAV 缺少 sample rate。");
        if (data == null) throw new FormatException("WAV 缺少 data chunk。");
        if ((data.Length & 1) != 0) throw new FormatException("PCM16 data chunk 长度必须是偶数。");

        var samples = new short[data.Length / 2];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = BitConverter.ToInt16(data, i * 2);
        }

        return new WavPcm16Mono(sampleRate.Value, samples);
    }

    public static void WritePcm16Mono(string path, int sampleRate, IReadOnlyList<short> samples)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);

        var dataBytes = checked(samples.Count * 2);
        WriteFourCc(writer, "RIFF");
        writer.Write(36 + dataBytes);
        WriteFourCc(writer, "WAVE");

        WriteFourCc(writer, "fmt ");
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);

        WriteFourCc(writer, "data");
        writer.Write(dataBytes);
        foreach (var sample in samples)
        {
            writer.Write(sample);
        }
    }

    public static short[] BytesToSamples(byte[] bytes)
    {
        if ((bytes.Length & 1) != 0) throw new ArgumentException("PCM16 byte length must be even.", nameof(bytes));
        var samples = new short[bytes.Length / 2];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = BitConverter.ToInt16(bytes, i * 2);
        }

        return samples;
    }

    private static void RequireFourCc(BinaryReader reader, string expected, string message)
    {
        var actual = ReadFourCc(reader);
        if (actual != expected) throw new FormatException(message + " 实际值: " + actual);
    }

    private static string ReadFourCc(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(4);
        if (bytes.Length != 4) throw new EndOfStreamException("WAV chunk header 不完整。");
        return Encoding.ASCII.GetString(bytes);
    }

    private static void WriteFourCc(BinaryWriter writer, string text)
    {
        writer.Write(Encoding.ASCII.GetBytes(text));
    }
}
