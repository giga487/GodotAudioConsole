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
    private readonly IAudioPlayback? _playback;
    private readonly RecordingStore _recordings;
    private Guid? _selectedRecordingId;
    private string? _selectedInputId;
    private bool _inputSuspended;
    private PcmRecording? _selectedAudio;
    private int _selectionStartFrame, _selectionEndFrame;
    private TimeSpan _playhead;
    private readonly WaveformPeak[] _waveform = new WaveformPeak[480];
    private readonly MemoryStream _pcm = new();
    private AudioFormat _format;
    private AudioFormat _recordedFormat;
    private bool _inputAvailable, _isRecording, _disposed;
    private double _volume = 100;
    private string? _inputError;
    private int _waveIndex, _peakFrames, _channelIndex;
    private float _low, _high;
    private long _lastSignal, _lastClip;
    private long _recordedFrames;

    public AudioConsoleService(IAudioInput input, IAudioPlayback? playback = null, string? temporaryRoot = null)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _playback = playback;
        _recordings = new RecordingStore(temporaryRoot);
        _input.SamplesAvailable += ReceiveSamples;
        _input.Failed += InputFailed;
    }

    public IReadOnlyList<AudioInputDevice> GetDevices(AudioSourceKind source = AudioSourceKind.Microphone)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _input.GetDevices(source);
    }

    public void DisconnectInput()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_isRecording || _inputSuspended) throw new InvalidOperationException("Ferma registrazione e riascolto prima di cambiare sorgente.");
            _inputAvailable = false;
            _selectedInputId = null;
            _inputError = null;
            _lastSignal = _lastClip = 0;
            _waveIndex = _peakFrames = _channelIndex = 0;
            _low = _high = 0;
            Array.Clear(_waveform);
        }
        _input.Stop();
    }

    public void ConfigureSources(IReadOnlyList<AudioSourceSettings> sources)
    {
        if (_input is not IMultiSourceAudioInput multi) throw new NotSupportedException("Mixer non disponibile.");
        _ = new StereoMixer(sources); // Validate before stopping the existing configuration.
        DisconnectInput();
        multi.ConfigureSources(sources);
        if (sources.Count > 0) SelectInput(MultiSourceAudioInput.MixDeviceId);
    }

    public void SetSourceVolume(string identity, double percent)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_input is not IMultiSourceAudioInput multi) throw new NotSupportedException("Mixer non disponibile.");
            multi.SetSourceVolume(identity, percent);
        }
    }

    public void SelectInput(string deviceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_isRecording) throw new InvalidOperationException("Stop recording before changing input.");
            if (_inputSuspended) throw new InvalidOperationException("Ferma il riascolto prima di cambiare sorgente.");
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
                _selectedInputId = deviceId;
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

    public RecordingStartResult StartRecording()
    {
        StopPlayback();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_isRecording) return RecordingStartResult.AlreadyRecording;
            if (!_inputAvailable) return RecordingStartResult.NoInput;
            ArchivePendingLocked();
            _pcm.SetLength(0);
            _recordedFrames = 0;
            _recordedFormat = _format;
            _isRecording = true;
            return RecordingStartResult.Started;
        }
    }

    public void StopRecording()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _isRecording = false;
            ArchivePendingLocked();
        }
    }

    private void ArchivePendingLocked()
    {
        if (_pcm.Length == 0) return;
        var duration = TimeSpan.FromSeconds((double)_recordedFrames / _recordedFormat.SampleRate);
        var entry = _recordings.Add(_recordedFormat, _pcm.GetBuffer().AsSpan(0, checked((int)_pcm.Length)), duration);
        SetSelectedLocked(entry.Id);
        _pcm.SetLength(0);
    }

    /// <summary>Exports the selected take without removing it from temporary history.</summary>
    public string SaveWav(string path)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_isRecording) throw new InvalidOperationException("Ferma la registrazione prima di salvare.");
            ArchivePendingLocked();
            return _recordings.Export(_selectedRecordingId ?? throw new InvalidOperationException("Seleziona una registrazione."), path);
        }
    }

    public void SelectRecording(Guid id)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_isRecording) throw new InvalidOperationException("Ferma la registrazione prima di cambiare selezione.");
            if (_recordings.Find(id) == null) throw new ArgumentException("Registrazione non disponibile.", nameof(id));
        }
        StopPlayback();
        lock (_gate)
        {
            if (_selectedRecordingId != id) SetSelectedLocked(id);
        }
    }

    private void SetSelectedLocked(Guid id)
    {
        var audio = PcmRecording.Read(_recordings.GetPath(id));
        _selectedRecordingId = id;
        _selectedAudio = audio;
        _selectionStartFrame = _selectionEndFrame = 0;
        _playhead = TimeSpan.Zero;
    }

    public void SetSelection(TimeSpan start, TimeSpan end)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_isRecording || _selectedAudio == null) throw new InvalidOperationException("Seleziona una registrazione ferma.");
            if (start < TimeSpan.Zero || end < TimeSpan.Zero || start > _selectedAudio.Duration || end > _selectedAudio.Duration)
                throw new ArgumentOutOfRangeException(nameof(start));
            _selectionStartFrame = _selectedAudio.ToFrame(start < end ? start : end);
            _selectionEndFrame = _selectedAudio.ToFrame(start < end ? end : start);
        }
    }

    public RecordingInfo GenerateSelectedPart()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_isRecording || _selectedAudio == null || _selectionEndFrame <= _selectionStartFrame)
                throw new InvalidOperationException("Seleziona un intervallo non vuoto della registrazione.");
        }
        StopPlayback();
        lock (_gate)
        {
            var audio = _selectedAudio!;
            var firstByte = _selectionStartFrame * audio.Format.Channels * 2;
            var length = (_selectionEndFrame - _selectionStartFrame) * audio.Format.Channels * 2;
            var duration = TimeSpan.FromSeconds((double)(_selectionEndFrame - _selectionStartFrame) / audio.Format.SampleRate);
            var entry = _recordings.Add(audio.Format, audio.Data.AsSpan(firstByte, length), duration,
                preserveId: _selectedRecordingId, label: "Ritaglio");
            SetSelectedLocked(entry.Id);
            return entry;
        }
    }

    public void PlaySelected()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_isRecording) throw new InvalidOperationException("Ferma la registrazione prima dell'ascolto.");
            if (_playback == null) throw new NotSupportedException("Riproduzione audio non disponibile.");
            if (_selectedRecordingId == null) throw new InvalidOperationException("Seleziona una registrazione.");
            _inputSuspended = true;
            _lastSignal = _lastClip = 0;
            Array.Clear(_waveform);
        }
        // Stop can join the capture callback thread; it must run outside _gate.
        _input.Stop();
        try
        {
            lock (_gate)
            {
                if (_playback!.IsPaused) _playback.Resume();
                else { _playhead = TimeSpan.Zero; _playback.Play(_recordings.GetPath(_selectedRecordingId!.Value)); }
            }
        }
        catch { ResumeInput(); throw; }
    }

    public void PausePlayback()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_playback?.IsPlaying == true) _playback.Pause();
        }
    }

    public void StopPlayback()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _playback?.Stop();
            _playhead = TimeSpan.Zero;
        }
        ResumeInput();
    }

    /// <summary>Call on the application's command thread to restore capture after natural playback completion.</summary>
    public void Update()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_inputSuspended || _playback?.IsPlaying == true || _playback?.IsPaused == true) return;
            _playhead = _playback?.Error == null ? _selectedAudio?.Duration ?? TimeSpan.Zero : _playhead;
            _playback?.Stop();
        }
        ResumeInput();
    }

    private void ResumeInput()
    {
        lock (_gate)
        {
            if (_disposed || !_inputSuspended) return;
            _inputSuspended = false;
            if (!_inputAvailable || _selectedInputId == null) return;
            try
            {
                _format = _input.Start(_selectedInputId);
                _format.Validate();
                _inputError = null;
                _channelIndex = _peakFrames = 0;
                _low = _high = 0;
            }
            catch (Exception error)
            {
                _inputAvailable = false;
                _inputError = "Impossibile riattivare la sorgente audio: " + error.Message;
            }
        }
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
            var recordings = _recordings.GetRecordings();
            var elapsed = (_isRecording || _pcm.Length > 0) && _recordedFormat.SampleRate > 0
                ? TimeSpan.FromSeconds((double)_recordedFrames / _recordedFormat.SampleRate) : TimeSpan.Zero;
            var selected = _recordings.Find(_selectedRecordingId);
            if (!_isRecording && _pcm.Length == 0) elapsed = selected?.Duration ?? TimeSpan.Zero;
            var playing = _playback?.IsPlaying == true;
            if (playing || _playback?.IsPaused == true) _playhead = _playback!.Position;
            RecordingEditorSnapshot? editor = null;
            if (_selectedAudio != null && _selectedRecordingId.HasValue)
                editor = new(_selectedRecordingId.Value, _selectedAudio.Duration, _playhead,
                    TimeSpan.FromSeconds((double)_selectionStartFrame / _selectedAudio.Format.SampleRate),
                    TimeSpan.FromSeconds((double)_selectionEndFrame / _selectedAudio.Format.SampleRate),
                    (WaveformPeak[])_selectedAudio.Overview.Clone(), playing ? _selectedAudio.LevelsAt(_playhead) : default);
            return new(_inputAvailable, _isRecording, selected != null || _pcm.Length > 0,
                _pcm.Length > 0 || recordings.Any(recording => !recording.IsExported),
                _volume, elapsed, signal, _inputError, history, recordings, _selectedRecordingId,
                playing, _playback?.IsPaused == true, _playback?.Error, _inputSuspended, editor,
                (_input as IMultiSourceAudioInput)?.GetSources() ?? []);
        }
    }

    private void ReceiveSamples(ReadOnlyMemory<float> samples)
    {
        lock (_gate)
        {
            if (_disposed || !_inputAvailable || _inputSuspended) return;
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
        }
    }

    private void InputFailed(string message)
    {
        lock (_gate)
        {
            if (_disposed || _inputSuspended) return;
            _inputAvailable = _isRecording = false;
            _inputError = message;
            try { ArchivePendingLocked(); }
            catch (IOException error) { _inputError += " • Copia temporanea non riuscita: " + error.Message; }
            catch (UnauthorizedAccessException error) { _inputError += " • Copia temporanea non riuscita: " + error.Message; }
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
        _playback?.Dispose();
        _recordings.Dispose();
        _pcm.Dispose();
    }
}
