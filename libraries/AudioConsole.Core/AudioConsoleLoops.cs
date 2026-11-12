namespace AudioConsole.Core;

public sealed partial class AudioConsoleService
{
    private readonly LoopBank _loops;

    public void SetLoopSettings(LoopSettings settings)
    {
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); _loops.Configure(settings); }
    }

    public void StartLoopRecording()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_inputAvailable || _inputSuspended) throw new InvalidOperationException("Attiva gli ingressi del mix prima di registrare un loop.");
            _loops.StartRecording();
        }
    }

    public RecordingInfo? StopLoopRecording()
    {
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); return _loops.StopRecording(); }
    }

    public void SelectLoop(Guid id)
    {
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); _loops.Select(id); }
    }

    public void StartLoop()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_inputAvailable || _inputSuspended) throw new InvalidOperationException("Attiva gli ingressi e usa Mix dal vivo per ascoltare il loop.");
            _loops.Start();
        }
    }

    public void StopLoop()
    {
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); _loops.Stop(); }
    }

    public string SaveLoopWav(string path)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_loops.IsRecording) throw new InvalidOperationException("Ferma la registrazione del loop prima di salvarlo.");
            return _loops.Export(path);
        }
    }
}
