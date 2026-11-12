namespace AudioConsole.Core;

/// <summary>Bounded stereo buffers on one 48 kHz timeline. Inputs are summed, never concatenated.</summary>
public sealed class StereoMixer
{
    public static readonly AudioFormat Format = new(48000, 2);
    private sealed class Channel
    {
        public readonly float[] Buffer = new float[48000 * 2];
        public int ReadIndex, Count;
        public double Gain;
        public AudioLevels Levels;
        public Channel(double percent) => Gain = percent / 100;
    }
    private readonly object _gate = new();
    private readonly Dictionary<string, Channel> _channels;

    public StereoMixer(IReadOnlyList<AudioSourceSettings> sources)
    {
        if (sources.Select(source => source.Device.Identity).Distinct().Count() != sources.Count)
            throw new ArgumentException("La stessa sorgente è già presente nel mix.", nameof(sources));
        foreach (var source in sources) ValidateGain(source.VolumePercent);
        _channels = sources.ToDictionary(source => source.Device.Identity, source => new Channel(source.VolumePercent));
    }

    public static void ValidateGain(double percent)
    {
        if (!double.IsFinite(percent) || percent is < 0 or > 200) throw new ArgumentOutOfRangeException(nameof(percent));
    }

    public void SetVolume(string identity, double percent)
    {
        ValidateGain(percent);
        lock (_gate) _channels[identity].Gain = percent / 100;
    }

    public void Push(string identity, ReadOnlySpan<float> samples)
    {
        if (samples.Length % 2 != 0) throw new ArgumentException("Occorrono frame stereo completi.", nameof(samples));
        lock (_gate)
        {
            var channel = _channels[identity];
            foreach (var value in samples)
            {
                if (channel.Count == channel.Buffer.Length)
                {
                    channel.ReadIndex = (channel.ReadIndex + 2) % channel.Buffer.Length;
                    channel.Count -= 2;
                }
                channel.Buffer[(channel.ReadIndex + channel.Count) % channel.Buffer.Length] = float.IsFinite(value) ? value : 0;
                channel.Count++;
            }
        }
    }

    public float[] ReadFrames(int frames)
    {
        if (frames <= 0) throw new ArgumentOutOfRangeException(nameof(frames));
        var result = new float[checked(frames * 2)];
        lock (_gate)
        {
            foreach (var channel in _channels.Values)
            {
                var left = 0f;
                var right = 0f;
                var count = Math.Min(result.Length, channel.Count);
                for (var sample = 0; sample < count; sample++)
                {
                    var value = channel.Buffer[channel.ReadIndex] * (float)channel.Gain;
                    channel.ReadIndex = (channel.ReadIndex + 1) % channel.Buffer.Length;
                    channel.Count--;
                    result[sample] += value;
                    if (sample % 2 == 0) left = Math.Max(left, Math.Abs(value));
                    else right = Math.Max(right, Math.Abs(value));
                }
                channel.Levels = new(Math.Min(left, 1), Math.Min(right, 1));
            }
        }
        return result;
    }

    public AudioLevels GetLevels(string identity) { lock (_gate) return _channels[identity].Levels; }
}
