using AudioConsole.Core;
using AudioConsole.Windows;
using Godot;

/// <summary>Presentation only: renders snapshots and forwards user commands to the DLL.</summary>
public partial class AudioConsoleView : Control
{
    public IAudioInput? AudioInputOverride { private get; set; }
    private AudioConsoleService? _service;
    private IReadOnlyList<AudioInputDevice> _deviceList = [];
    private string? _selectedDeviceId;
    private WaveformView _waveform = null!;
    private OptionButton _devices = null!;
    private Button _refresh = null!, _record = null!, _stop = null!, _save = null!;
    private Label _volumeLabel = null!, _timer = null!, _status = null!, _signal = null!;
    private FileDialog _saveDialog = null!;
    private ConfirmationDialog _discardDialog = null!;
    private bool _pendingQuit;
    private string? _lastInputError;

    public override void _Ready()
    {
        BindControls();
        GetTree().AutoAcceptQuit = false;
        try
        {
            IAudioInput input;
            if (AudioInputOverride != null) input = AudioInputOverride;
            else if (OperatingSystem.IsWindows()) input = new WasapiAudioInput();
            else throw new PlatformNotSupportedException("Il backend audio attuale richiede Windows.");
            _service = new AudioConsoleService(input);
            RefreshDevices();
        }
        catch (Exception error) { ShowError(error); }
    }

    public override void _Process(double delta)
    {
        if (_service == null) return;
        var state = _service.GetSnapshot();
        _waveform.ShowHistory(state.Waveform);
        _record.Disabled = !state.InputAvailable || state.IsRecording;
        _stop.Disabled = !state.IsRecording;
        _save.Disabled = state.IsRecording || !state.HasRecording;
        _devices.Disabled = state.IsRecording || _deviceList.Count == 0;
        _refresh.Disabled = state.IsRecording;
        var tenths = (long)(state.Elapsed.TotalSeconds * 10);
        _timer.Text = $"{tenths / 600:00}:{tenths / 10 % 60:00}.{tenths % 10}";
        _signal.Text = state.Signal switch
        {
            InputSignal.Clipping => "PICCO • RIDUCI VOLUME",
            InputSignal.Live => state.IsRecording ? "REC • INGRESSO LIVE" : "INGRESSO LIVE",
            _ => state.IsRecording ? "REC • NESSUN SEGNALE" : "NESSUN SEGNALE"
        };
        _signal.TooltipText = state.Signal == InputSignal.Quiet
            ? "Parla nel microfono. Controlla ingresso, volume e permessi microfono del sistema." : "";
        if (state.InputError != null && state.InputError != _lastInputError)
            _status.Text = "Ingresso audio interrotto: " + state.InputError;
        _lastInputError = state.InputError;
    }

    private void RefreshDevices()
    {
        if (_service == null) return;
        try
        {
            _deviceList = _service.GetDevices();
            _devices.Clear();
            foreach (var device in _deviceList) _devices.AddItem(device.Name);
            if (_deviceList.Count == 0)
            {
                _status.Text = "Nessun microfono. Collega un ingresso e premi Aggiorna.";
                return;
            }
            var selected = _deviceList.ToList().FindIndex(device => device.Id == _selectedDeviceId);
            if (selected < 0 || !_service.GetSnapshot().InputAvailable) SelectDevice(Math.Max(0, selected));
            else _devices.Select(selected);
        }
        catch (Exception error) { ShowError(error); }
    }

    private void SelectDevice(long index)
    {
        if (_service == null || index < 0 || index >= _deviceList.Count) return;
        try
        {
            var device = _deviceList[(int)index];
            _service.SelectInput(device.Id);
            _selectedDeviceId = device.Id;
            _devices.Select((int)index);
            _status.Text = "Pronto. Controlla il segnale e premi Registra.";
        }
        catch (Exception error) { ShowError(error); }
    }

    private void StartRecording(bool discardUnsaved = false)
    {
        if (_service == null) return;
        switch (_service.StartRecording(discardUnsaved))
        {
            case RecordingStartResult.Started:
                _status.Text = "Registrazione in corso…";
                break;
            case RecordingStartResult.UnsavedRecording:
                _pendingQuit = false;
                _discardDialog.PopupCentered();
                break;
            case RecordingStartResult.NoInput:
                _status.Text = "Seleziona un microfono disponibile e riprova.";
                break;
        }
    }

    private void StopRecording()
    {
        _service?.StopRecording();
        _status.Text = _service?.GetSnapshot().HasRecording == true
            ? "Registrazione pronta. Premi Salva WAV per conservarla."
            : "Nessun audio ricevuto. Controlla il microfono e riprova.";
    }

    private void OpenSaveDialog()
    {
        var state = _service?.GetSnapshot();
        if (state == null || state.IsRecording || !state.HasRecording) return;
        _saveDialog.CurrentFile = $"registrazione_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.wav";
        _saveDialog.PopupCenteredRatio(0.8f);
    }

    private void SaveWav(string path)
    {
        try { _status.Text = "WAV salvato: " + _service!.SaveWav(path); }
        catch (Exception error) { _status.Text = "Salvataggio non riuscito: " + error.Message; }
    }

    private void CloseRequested()
    {
        if (_service?.GetSnapshot().IsRecording == true) _service.StopRecording();
        if (_service?.GetSnapshot().HasUnsavedRecording == true)
        {
            _pendingQuit = true;
            _discardDialog.PopupCentered();
        }
        else GetTree().Quit();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest) CloseRequested();
    }

    public override void _ExitTree() => _service?.Dispose();
    private void ShowError(Exception error) => _status.Text = "Audio non disponibile: " + error.Message;

    private void BindControls()
    {
        _waveform = GetNode<WaveformView>("%WaveformDisplay/Trace");
        _devices = GetNode<OptionButton>("%MicrophoneSelect");
        _refresh = GetNode<Button>("%RefreshButton");
        _record = GetNode<Button>("%RecordButton");
        _stop = GetNode<Button>("%StopButton");
        _save = GetNode<Button>("%SaveButton");
        _volumeLabel = GetNode<Label>("%VolumeValue");
        _timer = GetNode<Label>("%ElapsedTime");
        _status = GetNode<Label>("%StatusLabel");
        _signal = GetNode<Label>("%SignalLabel");
        _saveDialog = GetNode<FileDialog>("%SaveDialog");
        _discardDialog = GetNode<ConfirmationDialog>("%DiscardDialog");

        _devices.ItemSelected += SelectDevice;
        _refresh.Pressed += RefreshDevices;
        _record.Pressed += () => StartRecording();
        _stop.Pressed += StopRecording;
        _save.Pressed += OpenSaveDialog;
        GetNode<HSlider>("%VolumeSlider").ValueChanged += value =>
        {
            _service?.SetVolume(value);
            _volumeLabel.Text = $"{value:0}%";
        };
        _saveDialog.FileSelected += SaveWav;
        _discardDialog.Confirmed += () =>
        {
            if (_pendingQuit) GetTree().Quit();
            else StartRecording(discardUnsaved: true);
        };
    }
}
