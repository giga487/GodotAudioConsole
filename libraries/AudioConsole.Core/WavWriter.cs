using System.Text;

namespace AudioConsole.Core;

/// <summary>Standard little-endian PCM16 RIFF/WAVE. No engine or audio framework required.</summary>
public static class WavWriter
{
    public static void Write(Stream destination, AudioFormat format, ReadOnlySpan<byte> pcm)
    {
        format.Validate();
        var blockAlign = checked((ushort)(format.Channels * sizeof(short)));
        if (pcm.Length % blockAlign != 0)
            throw new ArgumentException("PCM data must contain complete frames.", nameof(pcm));
        using var writer = new BinaryWriter(destination, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(checked((uint)pcm.Length + 36));
        writer.Write("WAVEfmt "u8);
        writer.Write(16u);
        writer.Write((ushort)1);
        writer.Write((ushort)format.Channels);
        writer.Write(format.SampleRate);
        writer.Write(checked(format.SampleRate * blockAlign));
        writer.Write(blockAlign);
        writer.Write((ushort)16);
        writer.Write("data"u8);
        writer.Write((uint)pcm.Length);
        writer.Write(pcm);
    }
}
