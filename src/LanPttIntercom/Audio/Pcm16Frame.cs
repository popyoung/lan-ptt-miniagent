using System;

namespace LanPttIntercom.Audio;

public static class Pcm16Frame
{
    public static byte[] ApplyVolume(byte[] pcm, int volumePercent)
    {
        if (pcm == null) throw new ArgumentNullException(nameof(pcm));
        if (pcm.Length == 0) return Array.Empty<byte>();
        if ((pcm.Length & 1) != 0) throw new ArgumentException("PCM16 frame length must be even.", nameof(pcm));

        var volume = Math.Clamp(volumePercent, 0, 100);
        if (volume == 100)
        {
            return (byte[])pcm.Clone();
        }

        var output = new byte[pcm.Length];
        for (int i = 0; i < pcm.Length / 2; i++)
        {
            var sample = BitConverter.ToInt16(pcm, i * 2);
            var scaled = (short)Math.Clamp((int)Math.Round(sample * (volume / 100.0)), short.MinValue, short.MaxValue);
            output[i * 2] = (byte)(scaled & 0xFF);
            output[i * 2 + 1] = (byte)((scaled >> 8) & 0xFF);
        }

        return output;
    }
}
