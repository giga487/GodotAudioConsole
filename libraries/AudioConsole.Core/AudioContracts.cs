namespace AudioConsole.Core;

public sealed record AudioInputDevice(string Id, string Name);

/// <summary>Normalized, interleaved floating point audio. The backend owns its hardware.</summary>
public readonly record struct AudioFormat(int SampleRate, int Channels)
{
    public void Validate()
    {
        if (SampleRate <= 0 || Channels is < 1 or > 2)
            throw new ArgumentOutOfRangeException(nameof(AudioFormat), "Only mono/stereo audio is supported.");
    }
}

public interface IAudioInput : IDisposable
{
    event Action<ReadOnlyMemory<float>>? SamplesAvailable;
    event Action<string>? Failed;
    IReadOnlyList<AudioInputDevice> GetDevices();
    AudioFormat Start(string deviceId);
    void Stop();
}

public interface IAudioPlayback : IDisposable
{
    bool IsPlaying { get; }
    bool IsPaused { get; }
    TimeSpan Position { get; }
    string? Error { get; }
    void Play(string wavPath);
    void Pause();
    void Resume();
    void Stop();
}

public sealed record RecordingInfo(Guid Id, string Name, DateTimeOffset CreatedAt,
    TimeSpan Duration, string? ExportPath)
{
    public bool IsExported => ExportPath != null;
}

public readonly record struct WaveformPeak(float Low, float High);
public readonly record struct AudioLevels(float Left, float Right);
public sealed record RecordingEditorSnapshot(Guid RecordingId, TimeSpan Duration, TimeSpan Position,
    TimeSpan SelectionStart, TimeSpan SelectionEnd, WaveformPeak[] Waveform, AudioLevels Levels);
public enum InputSignal { Quiet, Live, Clipping }
public enum RecordingStartResult { Started, AlreadyRecording, NoInput }

/// <summary>An immutable copy, safe to consume from a UI thread.</summary>
public sealed record ConsoleSnapshot(
    bool InputAvailable,
    bool IsRecording,
    bool HasRecording,
    bool HasUnsavedRecording,
    double VolumePercent,
    TimeSpan Elapsed,
    InputSignal Signal,
    string? InputError,
    WaveformPeak[] Waveform,
    RecordingInfo[] Recordings,
    Guid? SelectedRecordingId,
    bool IsPlaying,
    bool IsPaused,
    string? PlaybackError,
    bool IsInputSuspended,
    RecordingEditorSnapshot? Editor);
