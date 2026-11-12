using System.Buffers.Binary;

namespace AudioConsole.Core;

public sealed record LoopSettings(double VolumePercent = 100, double PauseSeconds = 0)
{
    public void Validate()
    {
        StereoMixer.ValidateGain(VolumePercent);
        if (!double.IsFinite(PauseSeconds) || PauseSeconds is < 0 or > 300)
            throw new ArgumentOutOfRangeException(nameof(PauseSeconds), "La pausa deve essere tra 0 e 300 secondi.");
    }
}

public sealed record LoopSnapshot(bool IsRecording, TimeSpan RecordingElapsed, RecordingInfo[] Clips,
    Guid? SelectedId, bool IsPlaying, TimeSpan Position, bool IsWaiting, TimeSpan UntilNext,
    LoopSettings Settings, string? Error);

/// <summary>Session clip bank with independent capture and a sample-clocked player.
/// The application serializes commands and sample callbacks. Live voice is captured before adding the loop.</summary>
internal sealed class LoopBank : IDisposable
{
    private readonly RecordingStore _store;
    private readonly MemoryStream _capture = new();
    private readonly Dictionary<Guid, LoopSettings> _settings = [];
    private AudioFormat _format;
    private PcmRecording? _audio;
    private Guid? _selected;
    private LoopSettings _defaults = new();
    private long _recordedFrames, _waitingFrames, _sourceTicks;
    private double _gain;
    private double Position => _format.SampleRate == 0 ? 0 : (double)_sourceTicks / _format.SampleRate;
    private double _gainSmoothing;
    public bool IsRecording { get; private set; }
    public bool IsPlaying { get; private set; }
    public string? Error { get; private set; }
    public bool HasUnsaved => _capture.Length > 0 || _store.GetRecordings().Any(clip => !clip.IsExported);
    public LoopSettings Settings => _selected.HasValue ? _settings[_selected.Value] : _defaults;

    public LoopBank(string? temporaryRoot) => _store = new(temporaryRoot);

    public void ResetFormat(AudioFormat format)
    {
        format.Validate();
        if (IsRecording) throw new InvalidOperationException("Ferma la registrazione del loop.");
        if (_recordedFrames > 0) StopRecording();
        Stop();
        _format = format;
        _gainSmoothing = 1 - Math.Exp(-1d / (0.005 * format.SampleRate));
        Error = null;
    }

    public void Configure(LoopSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        _defaults = settings;
        if (_selected.HasValue) _settings[_selected.Value] = settings;
    }

    public void StartRecording()
    {
        if (IsRecording) return;
        _format.Validate();
        if (_recordedFrames > 0) StopRecording(); // Retry a previously failed temporary write before replacing capture data.
        _defaults = Settings;
        Stop();
        Error = null;
        _capture.SetLength(0);
        _recordedFrames = 0;
        IsRecording = true;
    }

    public void Capture(float sample, int channel)
    {
        if (!IsRecording) return;
        Span<byte> encoded = stackalloc byte[2];
        var value = float.IsFinite(sample) ? Math.Clamp(sample, -1, 1) : 0;
        BinaryPrimitives.WriteInt16LittleEndian(encoded,
            (short)Math.Clamp((int)Math.Round(value * 32768), short.MinValue, short.MaxValue));
        _capture.Write(encoded);
        if (channel == _format.Channels - 1) _recordedFrames++;
    }

    public RecordingInfo? StopRecording()
    {
        IsRecording = false;
        if (_recordedFrames == 0) { _capture.SetLength(0); return null; }
        // Only complete frames are committed if a backend stopped during an interleaved frame.
        var length = checked((int)(_recordedFrames * _format.Channels * 2));
        var clip = _store.Add(_format, _capture.GetBuffer().AsSpan(0, length),
            TimeSpan.FromSeconds((double)_recordedFrames / _format.SampleRate), label: "Loop");
        _settings[clip.Id] = _defaults;
        var retained = _store.GetRecordings().Select(info => info.Id).ToHashSet();
        foreach (var id in _settings.Keys.Where(id => !retained.Contains(id)).ToArray()) _settings.Remove(id);
        _capture.SetLength(0);
        _recordedFrames = 0;
        Select(clip.Id);
        return clip;
    }

    public void Select(Guid id)
    {
        if (IsRecording) throw new InvalidOperationException("Ferma la registrazione del loop.");
        var audio = PcmRecording.Read(_store.GetPath(id));
        Stop();
        _audio = audio;
        _selected = id;
        Error = null;
    }

    public void Start()
    {
        if (IsRecording || _audio == null || _audio.FrameCount == 0)
            throw new InvalidOperationException("Registra e seleziona un loop prima di avviarlo.");
        _format.Validate();
        _sourceTicks = 0;
        _waitingFrames = 0;
        _gain = 0;
        IsPlaying = true;
    }

    public void Stop() { IsPlaying = false; _sourceTicks = 0; _waitingFrames = 0; }

    public float ReadSample(int channel)
    {
        if (!IsPlaying || _audio == null) return 0;
        if (channel == 0) _gain += _gainSmoothing * (Settings.VolumePercent / 100 - _gain);
        var value = 0f;
        if (_waitingFrames == 0)
        {
            var position = Position;
            var frame = (int)position;
            var next = Math.Min(_audio.FrameCount - 1, frame + 1);
            var first = ReadFrame(frame, channel);
            var second = ReadFrame(next, channel);
            var fadeFrames = Math.Min(_audio.Format.SampleRate * 0.005, _audio.FrameCount / 4d);
            var envelope = fadeFrames < 1 ? 1 : Math.Min(1, Math.Min(position, _audio.FrameCount - 1 - position) / fadeFrames);
            value = (float)((first + (position - frame) * (second - first)) * _gain * Math.Max(0, envelope));
        }
        if (channel == _format.Channels - 1)
        {
            if (_waitingFrames > 0) _waitingFrames--;
            else
            {
                _sourceTicks += _audio.Format.SampleRate;
                var cycleTicks = (long)_audio.FrameCount * _format.SampleRate;
                if (_sourceTicks >= cycleTicks)
                {
                    _waitingFrames = (long)Math.Round(Settings.PauseSeconds * _format.SampleRate);
                    _sourceTicks = _waitingFrames > 0 ? 0 : _sourceTicks % cycleTicks;
                }
            }
        }
        return value;
    }

    private float ReadFrame(int frame, int channel)
    {
        float ReadChannel(int sourceChannel) => BinaryPrimitives.ReadInt16LittleEndian(
            _audio!.Data.AsSpan((frame * _audio.Format.Channels + sourceChannel) * 2)) / 32768f;
        if (_audio!.Format.Channels == 1) return ReadChannel(0);
        return _format.Channels == 1 ? (ReadChannel(0) + ReadChannel(1)) / 2 : ReadChannel(channel);
    }

    public string Export(string path) => _store.Export(_selected ?? throw new InvalidOperationException("Seleziona un loop."), path);

    public void Fail(string message)
    {
        Stop();
        try { StopRecording(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { message += " • " + error.Message; }
        Error = message;
    }

    public LoopSnapshot Snapshot() => new(IsRecording,
        _format.SampleRate == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)_recordedFrames / _format.SampleRate),
        _store.GetRecordings(), _selected, IsPlaying,
        _audio == null ? TimeSpan.Zero : TimeSpan.FromSeconds(Position / _audio.Format.SampleRate),
        IsPlaying && _waitingFrames > 0, _format.SampleRate == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)_waitingFrames / _format.SampleRate), Settings, Error);

    public void Dispose() { _store.Dispose(); _capture.Dispose(); }
}
