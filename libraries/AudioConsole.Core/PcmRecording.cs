using System.Buffers.Binary;

namespace AudioConsole.Core;

/// <summary>Analysis of session WAVs written by WavWriter; editing always aligns to audio frames.</summary>
internal sealed class PcmRecording
{
    public AudioFormat Format { get; }
    public byte[] Data { get; }
    public int FrameCount => Data.Length / (Format.Channels * 2);
    public TimeSpan Duration => TimeSpan.FromSeconds((double)FrameCount / Format.SampleRate);
    public WaveformPeak[] Overview { get; }

    private PcmRecording(AudioFormat format, byte[] data)
    {
        Format = format;
        Data = data;
        var count = Math.Min(720, FrameCount);
        Overview = new WaveformPeak[count];
        for (var bin = 0; bin < count; bin++)
        {
            var first = (int)((long)bin * FrameCount / count) * format.Channels;
            var last = (int)((long)(bin + 1) * FrameCount / count) * format.Channels;
            var low = 0f;
            var high = 0f;
            for (var sample = first; sample < last; sample++)
            {
                var value = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(sample * 2)) / 32768f;
                low = Math.Min(low, value);
                high = Math.Max(high, value);
            }
            Overview[bin] = new(low, high);
        }
    }

    public static PcmRecording Read(string path)
    {
        var wav = File.ReadAllBytes(path);
        if (wav.Length < 44 || !wav.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
            !wav.AsSpan(8, 8).SequenceEqual("WAVEfmt "u8) ||
            BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(16)) != 16 ||
            BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(20)) != 1 ||
            BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34)) != 16 ||
            !wav.AsSpan(36, 4).SequenceEqual("data"u8))
            throw new InvalidDataException("La copia temporanea non è un WAV PCM16 valido.");
        var format = new AudioFormat(BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)),
            BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22)));
        format.Validate();
        var length = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40));
        if (length < 0 || length != wav.Length - 44 || length % (format.Channels * 2) != 0)
            throw new InvalidDataException("Dati PCM incompleti.");
        return new(format, wav.AsSpan(44).ToArray());
    }

    public int ToFrame(TimeSpan time) => (int)Math.Clamp(Math.Round(time.TotalSeconds * Format.SampleRate), 0, FrameCount);

    public AudioLevels LevelsAt(TimeSpan time)
    {
        var first = ToFrame(time);
        var last = Math.Min(FrameCount, first + Format.SampleRate / 30);
        var left = 0f;
        var right = 0f;
        for (var frame = first; frame < last; frame++)
        {
            var offset = frame * Format.Channels * 2;
            left = Math.Max(left, Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(Data.AsSpan(offset)) / 32768f));
            right = Math.Max(right, Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(Data.AsSpan(offset + (Format.Channels == 2 ? 2 : 0))) / 32768f));
        }
        return new(left, right);
    }
}
