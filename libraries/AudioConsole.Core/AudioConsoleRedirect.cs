namespace AudioConsole.Core;

public sealed partial class AudioConsoleService
{
    private readonly IAudioRedirect? _redirect;
    private AudioRedirectMode _redirectMode;
    private AudioOutputDevice? _redirectDestination;
    private bool _redirectActive;
    private string? _redirectError;

    public IReadOnlyList<AudioOutputDevice> GetRedirectDevices() => _redirect?.GetDevices() ?? [];

    public void ConfigureRedirect(AudioOutputDevice? destination, AudioRedirectMode mode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        // Resolve metadata from the backend, never trust saved feedback identifiers.
        var resolved = destination == null ? null : GetRedirectDevices().FirstOrDefault(d => d.Id == destination.Id)
            ?? throw new InvalidOperationException("Uscita di invio non disponibile.");
        StopRedirect();
        lock (_gate) { _redirectDestination = resolved; _redirectMode = mode; _redirectError = null; }
    }

    private void CheckFeedback(IEnumerable<string> identities)
    {
        if (_redirectDestination != null && identities.Any(id => _redirectDestination.FeedbackInputIds.Contains(id)))
            throw new InvalidOperationException("L'uscita di invio è anche una sorgente del mix: rimuovila per evitare un circuito audio.");
    }

    public void StartRedirect()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_redirect == null) throw new NotSupportedException("Invio audio non disponibile.");
        if (_redirectDestination == null) throw new InvalidOperationException("Seleziona un'uscita, per esempio CABLE Input.");
        lock (_gate)
        {
            if (_redirectMode == AudioRedirectMode.LiveMix)
            {
                if (!_inputAvailable) throw new InvalidOperationException("Aggiungi una sorgente al mix.");
                CheckFeedback((_input as IMultiSourceAudioInput)?.GetSources().Select(s => s.Device.Identity)
                    ?? new[] { _selectedInputId! });
            }
            else if (_isRecording || _selectedRecordingId == null)
                throw new InvalidOperationException("Ferma la registrazione e seleziona una voce nella lista.");
        }
        StopRedirect();
        StopPlayback();
        try
        {
            if (_redirectMode == AudioRedirectMode.SelectedRecording)
            {
                lock (_gate) { _inputSuspended = true; Array.Clear(_waveform); _lastSignal = _lastClip = 0; }
                _input.Stop();
            }
            lock (_gate)
            {
                _redirectError = null;
                if (_redirectMode == AudioRedirectMode.LiveMix) _redirect.StartLive(_redirectDestination.Id, _format);
                else _redirect.PlayFile(_redirectDestination.Id, _recordings.GetPath(_selectedRecordingId!.Value));
                _redirectActive = true;
                _playhead = TimeSpan.Zero;
            }
        }
        catch { _redirect.Stop(); ResumeInput(); throw; }
    }

    private void StopFileRedirect()
    {
        if (_redirectActive && _redirectMode == AudioRedirectMode.SelectedRecording) StopRedirect();
    }

    public void StopRedirect()
    {
        lock (_gate)
        {
            _redirectActive = false;
            _redirect?.Stop();
        }
        ResumeInput();
    }
}
