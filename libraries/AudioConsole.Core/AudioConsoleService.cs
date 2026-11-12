using System.Buffers.Binary;
using System.Diagnostics;

namespace AudioConsole.Core;

/// <summary>
/// Owns application state, gain, waveform history, PCM and export. Capture callbacks
/// never touch the UI; commands/snapshots and callbacks share a short-lived lock.
/// Device lifecycle commands are serialized by the caller (normally the UI thread).
/// </summary>
public sealed class AudioConsoleService : IDisposable
{
    private readonly object _gate = new();
    private readonly IAudioInput _input;
    private readonly WaveformPeak[] _waveform = new WaveformPeak[480];
    private readonly MemoryStream _pcm = new();
    private AudioFormat _format;
    private AudioFormat _recordedFormat;
    private bool _inputAvailable, _isRecording, _unsaved, _disposed;
    private double _volume = 100;
    private string? _inputError;
    private long _recordingVersion;
    private int _waveIndex, _peakFrames, _channelIndex;
    private float _low, _high;
    private long _lastSignal, _lastClip;
    private long _recordedFrames;

    public AudioConsoleService(IAudioInput input)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _input.SamplesAvailable += ReceiveSamples;
        _input.Failed += InputFailed;
    }

    public IReadOnlyList<AudioInputDevice> GetDevices()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _input.GetDevices();
    }

    public void SelectInput(string deviceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_isRecording) throw new InvalidOperationException("Stop recording before changing input.");
            _inputAvailable = false;
        }
        // Never stop/join a capture thread while holding the callback's lock.
        _input.Stop();
        try
        {
            lock (_gate)
            {
                // Start callbacks wait for this lock until the format is initialized.
                _format = _input.Start(deviceId);
                _format.Validate();
                _inputAvailable = true;
                _inputError = null;
                _waveIndex = _peakFrames = _channelIndex = 0;
                _low = _high = 0;
                _lastSignal = _lastClip = 0;
                Array.Clear(_waveform);
            }
        }
        catch
        {
            _input.Stop();
            throw;
        }
    }

    public void SetVolume(double percent)
    {
        if (!double.IsFinite(percent) || percent is < 0 or > 200)
            throw new ArgumentOutOfRangeException(nameof(percent));
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); _volume = percent; }
    }

    public RecordingStartResult StartRecording(bool discardUnsaved = false)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_isRecording) return RecordingStartResult.AlreadyRecording;
            if (!_inputAvailable) return RecordingStartResult.NoInput;
            if (_unsaved && !discardUnsaved) return RecordingStartResult.UnsavedRecording;
            _pcm.SetLength(0);
            _recordedFrames = 0;
            _recordedFormat = _format;
            _recordingVersion++;
            _isRecording = true;
            _unsaved = false;
            return RecordingStartResult.Started;
        }
    }

    public void StopRecording()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_isRecording) return;
            _isRecording = false;
            _unsaved = _pcm.Length > 0;
        }
    }

    /// <summary>Writes a sibling temporary file, then replaces the destination on success.</summary>
    public string SaveWav(string path)
    {
        byte[] data;
        AudioFormat format;
        long version;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_isRecording || _pcm.Length == 0)
                throw new InvalidOperationException("Stop a nonempty recording before saving.");
            data = _pcm.ToArray();
            format = _recordedFormat;
            version = _recordingVersion;
        }
        if (!string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase)) path += ".wav";
        path = Path.GetFullPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
                WavWriter.Write(stream, format, data);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        lock (_gate)
        {
            if (_recordingVersion == version) _unsaved = false;
        }
        return path;
    }

    public ConsoleSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var history = new WaveformPeak[_waveform.Length];
            for (var i = 0; i < history.Length; i++) history[i] = _waveform[(_waveIndex + i) % history.Length];
            var now = Stopwatch.GetTimestamp();
            var signal = _lastClip != 0 && Stopwatch.GetElapsedTime(_lastClip, now).TotalSeconds < 0.35
                ? InputSignal.Clipping
                : _lastSignal != 0 && Stopwatch.GetElapsedTime(_lastSignal, now).TotalSeconds < 3
                    ? InputSignal.Live : InputSignal.Quiet;
            var elapsed = _recordedFormat.SampleRate > 0
                ? TimeSpan.FromSeconds((double)_recordedFrames / _recordedFormat.SampleRate) : TimeSpan.Zero;
            return new(_inputAvailable, _isRecording, _pcm.Length > 0, _unsaved,
                _volume, elapsed, signal, _inputError, history);
        }
    }

    private void ReceiveSamples(ReadOnlyMemory<float> samples)
    {
        lock (_gate)
        {
            if (_disposed || !_inputAvailable) return;
            var span = samples.Span;
            var gain = (float)(_volume / 100);
            var framesPerPeak = Math.Max(1, _format.SampleRate / 120);
            Span<byte> encoded = stackalloc byte[2];
            foreach (var source in span)
            {
                var value = float.IsFinite(source) ? source * gain : 0;
                if (Math.Abs(value) > 0.0001f) _lastSignal = Stopwatch.GetTimestamp();
                if (Math.Abs(value) >= 0.99f) _lastClip = Stopwatch.GetTimestamp();
                value = Math.Clamp(value, -1, 1);
                _low = Math.Min(_low, value);
                _high = Math.Max(_high, value);
                if (_isRecording)
                {
                    var pcm = (short)Math.Clamp((int)Math.Round(value * 32768), short.MinValue, short.MaxValue);
                    BinaryPrimitives.WriteInt16LittleEndian(encoded, pcm);
                    _pcm.Write(encoded);
                }
                if (++_channelIndex < _format.Channels) continue;
                _channelIndex = 0;
                if (_isRecording) _recordedFrames++;
                if (++_peakFrames < framesPerPeak) continue;
                _waveform[_waveIndex] = new(_low, _high);
                _waveIndex = (_waveIndex + 1) % _waveform.Length;
                _peakFrames = 0;
                _low = _high = 0;
            }
            if (_isRecording && span.Length > 0) _unsaved = true;
        }
    }

    private void InputFailed(string message)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _inputAvailable = _isRecording = false;
            _unsaved = _pcm.Length > 0;
            _inputError = message;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _inputAvailable = _isRecording = false;
        }
        _input.SamplesAvailable -= ReceiveSamples;
        _input.Failed -= InputFailed;
        _input.Dispose();
        _pcm.Dispose();
    }
}
