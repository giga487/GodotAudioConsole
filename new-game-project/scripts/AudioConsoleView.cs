using AudioConsole.Core;
using AudioConsole.Windows;
using Godot;

/// <summary>Presentation only: renders snapshots and forwards user commands to the DLL.</summary>
public partial class AudioConsoleView : Control
{
    public IAudioInput? AudioInputOverride { private get; set; }
    public IAudioPlayback? AudioPlaybackOverride { private get; set; }
    private AudioConsoleService? _service;
    private IReadOnlyList<AudioInputDevice> _deviceList = [];
    private string? _selectedDeviceId;
    private WaveformView _waveform = null!;
    private OptionButton _devices = null!;
    private Button _refresh = null!, _record = null!, _stop = null!, _save = null!;
    private Button _play = null!, _stopPlayback = null!;
    private ItemList _historyList = null!;
    private Label _historyCount = null!, _emptyHistory = null!;
    private RecordingInfo[] _displayedHistory = [];
    private Label _volumeLabel = null!, _timer = null!, _status = null!, _signal = null!;
    private FileDialog _saveDialog = null!;
    private ConfirmationDialog _discardDialog = null!;
    private string? _lastInputError;
    private string? _lastPlaybackError;
    private Guid? _saveTarget;

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
            var playback = AudioPlaybackOverride;
            if (playback == null && OperatingSystem.IsWindows() && AudioInputOverride == null)
                playback = new WasapiAudioPlayback();
            _service = new AudioConsoleService(input, playback);
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
        _play.Disabled = state.IsRecording || !state.HasRecording || state.IsPlaying;
        _stopPlayback.Disabled = !state.IsPlaying;
        RenderHistory(state);
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
        if (state.PlaybackError != null && state.PlaybackError != _lastPlaybackError)
            _status.Text = "Ascolto interrotto: " + state.PlaybackError;
        _lastPlaybackError = state.PlaybackError;
    }

    private void RenderHistory(ConsoleSnapshot state)
    {
        if (!_displayedHistory.SequenceEqual(state.Recordings))
        {
            _displayedHistory = state.Recordings;
            _historyList.Clear();
            foreach (var recording in state.Recordings)
            {
                var status = recording.IsExported ? "Salvata" : "Temporanea";
                var index = _historyList.AddItem($"{recording.Name}   •   {FormatDuration(recording.Duration)}   •   {status}");
                _historyList.SetItemTooltip(index, $"{recording.CreatedAt:HH:mm:ss}\n{recording.ExportPath ?? "Da salvare come WAV"}");
            }
        }
        _historyCount.Text = $"{state.Recordings.Length} / 10";
        _emptyHistory.Visible = state.Recordings.Length == 0;
        for (var i = 0; i < _displayedHistory.Length; i++)
        {
            _historyList.SetItemDisabled(i, state.IsRecording);
            if (_displayedHistory[i].Id == state.SelectedRecordingId) _historyList.Select(i);
        }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        var tenths = (long)(duration.TotalSeconds * 10);
        return $"{tenths / 600:00}:{tenths / 10 % 60:00}.{tenths % 10}";
    }

    private void SelectRecording(long index)
    {
        if (_service == null || index < 0 || index >= _displayedHistory.Length) return;
        try
        {
            _service.SelectRecording(_displayedHistory[index].Id);
            _status.Text = _displayedHistory[index].Name + " selezionata. Premi Ascolta o Salva WAV.";
        }
        catch (Exception error) { _status.Text = error.Message; }
    }

    private void PlaySelected()
    {
        try
        {
            _service?.PlaySelected();
            _status.Text = "Ascolto della registrazione selezionata…";
        }
        catch (Exception error) { _status.Text = "Ascolto non riuscito: " + error.Message; }
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

    private void StartRecording()
    {
        if (_service == null) return;
        try
        {
            switch (_service.StartRecording())
            {
                case RecordingStartResult.Started:
                    _status.Text = "Registrazione in corso…";
                    break;
                case RecordingStartResult.NoInput:
                    _status.Text = "Seleziona un microfono disponibile e riprova.";
                    break;
            }
        }
        catch (Exception error) { _status.Text = "Registrazione non avviata: " + error.Message; }
    }

    private void StopRecording()
    {
        try { _service?.StopRecording(); }
        catch (Exception error) { _status.Text = "Copia temporanea non riuscita: " + error.Message; return; }
        _status.Text = _service?.GetSnapshot().HasRecording == true
            ? "Registrazione aggiunta alla lista. Premi Ascolta o Salva WAV."
            : "Nessun audio ricevuto. Controlla il microfono e riprova.";
    }

    private void OpenSaveDialog()
    {
        var state = _service?.GetSnapshot();
        if (state == null || state.IsRecording || !state.HasRecording) return;
        _saveTarget = state.SelectedRecordingId;
        _saveDialog.CurrentFile = $"registrazione_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.wav";
        _saveDialog.PopupCenteredRatio(0.8f);
    }

    private void SaveWav(string path)
    {
        try
        {
            if (_saveTarget.HasValue) _service!.SelectRecording(_saveTarget.Value);
            _status.Text = "WAV salvato: " + _service!.SaveWav(path);
            _saveTarget = null;
        }
        catch (Exception error) { _status.Text = "Salvataggio non riuscito: " + error.Message; }
    }

    private void CloseRequested()
    {
        if (_service?.GetSnapshot().IsRecording == true) StopRecording();
        if (_service?.GetSnapshot().HasUnsavedRecording == true)
        {
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
        _historyList = GetNode<ItemList>("%RecordingList");
        _historyCount = GetNode<Label>("%HistoryCount");
        _emptyHistory = GetNode<Label>("%EmptyHistory");
        _play = GetNode<Button>("%PlayButton");
        _stopPlayback = GetNode<Button>("%StopPlaybackButton");

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
        _saveDialog.Canceled += () => _saveTarget = null;
        _discardDialog.Confirmed += () => GetTree().Quit();
        _historyList.ItemSelected += SelectRecording;
        _historyList.ItemActivated += index =>
        {
            SelectRecording(index);
            PlaySelected();
        };
        _play.Pressed += PlaySelected;
        _stopPlayback.Pressed += () =>
        {
            _service?.StopPlayback();
            _status.Text = "Ascolto fermato.";
        };
    }
}
