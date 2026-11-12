using AudioConsole.Core;
using AudioConsole.Windows;
using Godot;

/// <summary>Presentation only: renders snapshots and forwards user commands to the DLL.</summary>
public partial class AudioConsoleView : Control
{
    [Export] public Texture2D PlayIcon { get; set; } = null!;
    [Export] public Texture2D PauseIcon { get; set; } = null!;
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
    private RecordingTimelineView _timeline = null!;
    private SpinBox _selectionStart = null!, _selectionEnd = null!;
    private Button _generatePart = null!;
    private Label _playbackTime = null!;
    private ProgressBar _leftMeter = null!, _rightMeter = null!;
    private bool _syncingEditor;
    private PopupMenu _historyContextMenu = null!;
    private Guid? _historyMenuTarget;
    private enum HistoryAction { PlayPause, Stop, Save, GeneratePart = 4 }

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
        _service.Update();
        var state = _service.GetSnapshot();
        _waveform.ShowHistory(state.Waveform);
        RenderEditor(state);
        _record.Disabled = !state.InputAvailable || state.IsRecording;
        _stop.Disabled = !state.IsRecording;
        _save.Disabled = state.IsRecording || !state.HasRecording;
        _play.Disabled = state.IsRecording || !state.HasRecording;
        _play.Icon = state.IsPlaying ? PauseIcon : PlayIcon;
        _play.TooltipText = state.IsPlaying ? "Pausa riproduzione" : state.IsPaused ? "Riprendi riproduzione" : "Riproduci registrazione selezionata";
        _stopPlayback.Disabled = !state.IsPlaying && !state.IsPaused;
        RenderHistory(state);
        if (_historyContextMenu.Visible) UpdateHistoryMenu(state);
        _devices.Disabled = state.IsRecording || state.IsInputSuspended || _deviceList.Count == 0;
        _refresh.Disabled = state.IsRecording || state.IsInputSuspended;
        var tenths = (long)(state.Elapsed.TotalSeconds * 10);
        _timer.Text = $"{tenths / 600:00}:{tenths / 10 % 60:00}.{tenths % 10}";
        _signal.Text = state.IsInputSuspended ? "MIC SOSPESO • RIASCOLTO" : state.Signal switch
        {
            InputSignal.Clipping => "PICCO • RIDUCI VOLUME",
            InputSignal.Live => state.IsRecording ? "REC • INGRESSO LIVE" : "INGRESSO LIVE",
            _ => state.IsRecording ? "REC • NESSUN SEGNALE" : "NESSUN SEGNALE"
        };
        _signal.TooltipText = state.IsInputSuspended ? "Microfono disattivato durante il riascolto e la pausa. Si riattiva a STOP o a fine file."
            : state.Signal == InputSignal.Quiet
            ? "Parla nel microfono. Controlla ingresso, volume e permessi microfono del sistema." : "";
        if (state.InputError != null && state.InputError != _lastInputError)
            _status.Text = "Ingresso audio interrotto: " + state.InputError;
        _lastInputError = state.InputError;
        if (state.PlaybackError != null && state.PlaybackError != _lastPlaybackError)
            _status.Text = "Ascolto interrotto: " + state.PlaybackError;
        _lastPlaybackError = state.PlaybackError;
    }

    private void RenderEditor(ConsoleSnapshot state)
    {
        var editor = state.Editor;
        _timeline.InteractionEnabled = !state.IsRecording;
        _timeline.ShowRecording(editor);
        _playbackTime.Text = $"{FormatDuration(editor?.Position ?? TimeSpan.Zero)} / {FormatDuration(editor?.Duration ?? TimeSpan.Zero)}";
        _leftMeter.Value = editor?.Levels.Left ?? 0;
        _rightMeter.Value = editor?.Levels.Right ?? 0;
        _selectionStart.Editable = _selectionEnd.Editable = editor != null && !state.IsRecording;
        _syncingEditor = true;
        try
        {
            _selectionStart.MaxValue = _selectionEnd.MaxValue = editor?.Duration.TotalSeconds ?? 0;
            _selectionStart.SetValueNoSignal(editor?.SelectionStart.TotalSeconds ?? 0);
            _selectionEnd.SetValueNoSignal(editor?.SelectionEnd.TotalSeconds ?? 0);
        }
        finally { _syncingEditor = false; }
        _generatePart.Disabled = state.IsRecording || editor == null || editor.SelectionEnd <= editor.SelectionStart;
    }

    private void SetSelection(double start, double end)
    {
        if (_syncingEditor) return;
        try { _service?.SetSelection(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end)); }
        catch (Exception error) { _status.Text = "Selezione non valida: " + error.Message; }
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
            _status.Text = _displayedHistory[index].Name + " selezionata. Premi ▶ o Salva WAV.";
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

    private void TogglePlayback()
    {
        if (_service?.GetSnapshot().IsPlaying == true)
        {
            _service.PausePlayback();
            _status.Text = "Ascolto in pausa.";
        }
        else PlaySelected();
    }

    private void StopPlayback()
    {
        _service?.StopPlayback();
        _status.Text = "Ascolto fermato.";
    }

    private void GenerateSelectedPart()
    {
        try
        {
            var part = _service!.GenerateSelectedPart();
            _status.Text = part.Name + " aggiunto alla lista. L'originale è intatto.";
        }
        catch (Exception error) { _status.Text = "Ritaglio non riuscito: " + error.Message; }
    }

    private void OnHistoryGuiInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right } mouse || _service == null) return;
        var index = _historyList.GetItemAtPosition(mouse.Position, exact: true);
        var state = _service.GetSnapshot();
        _historyMenuTarget = index >= 0 && index < _displayedHistory.Length ? _displayedHistory[index].Id : null;
        if (_historyMenuTarget.HasValue && !state.IsRecording)
        {
            // Right-clicking the current playing item must not stop it just to open its menu.
            if (state.SelectedRecordingId != _historyMenuTarget) SelectRecording(index);
            _historyList.Select(index);
        }
        UpdateHistoryMenu(_service.GetSnapshot());
        var transform = _historyContextMenu.IsEmbedded() ? _historyList.GetGlobalTransformWithCanvas() : _historyList.GetScreenTransform();
        _historyContextMenu.Position = (Vector2I)(transform * mouse.Position);
        _historyContextMenu.ResetSize();
        _historyContextMenu.Popup();
        _historyList.AcceptEvent();
    }

    private void UpdateHistoryMenu(ConsoleSnapshot state)
    {
        var available = !state.IsRecording && _historyMenuTarget.HasValue && state.SelectedRecordingId == _historyMenuTarget
            && state.Recordings.Any(item => item.Id == _historyMenuTarget);
        _historyContextMenu.SetItemText(0, available && state.IsPlaying ? "Pausa" : available && state.IsPaused ? "Riprendi" : "Riproduci");
        _historyContextMenu.SetItemIcon(0, available && state.IsPlaying ? PauseIcon : PlayIcon);
        _historyContextMenu.SetItemDisabled(0, !available);
        _historyContextMenu.SetItemDisabled(1, !available || (!state.IsPlaying && !state.IsPaused));
        _historyContextMenu.SetItemDisabled(2, !available);
        _historyContextMenu.SetItemDisabled(4, !available || state.Editor == null || state.Editor.SelectionEnd <= state.Editor.SelectionStart);
    }

    private void OnHistoryMenuAction(long id)
    {
        _historyContextMenu.Hide();
        if (_service == null) return;
        var state = _service.GetSnapshot();
        UpdateHistoryMenu(state);
        var index = _historyContextMenu.GetItemIndex((int)id);
        if (index < 0 || _historyContextMenu.IsItemDisabled(index)) return;
        switch ((HistoryAction)id)
        {
            case HistoryAction.PlayPause: TogglePlayback(); break;
            case HistoryAction.Stop: StopPlayback(); break;
            case HistoryAction.Save: OpenSaveDialog(); break;
            case HistoryAction.GeneratePart: GenerateSelectedPart(); break;
        }
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
        var previous = _service?.GetSnapshot().SelectedRecordingId;
        try { _service?.StopRecording(); }
        catch (Exception error) { _status.Text = "Copia temporanea non riuscita: " + error.Message; return; }
        _status.Text = _service?.GetSnapshot().SelectedRecordingId is { } current && current != previous
            ? "Registrazione aggiunta alla lista. Premi ▶ o Salva WAV."
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
        _historyContextMenu = GetNode<PopupMenu>("%HistoryContextMenu");
        _historyContextMenu.IdPressed += OnHistoryMenuAction;
        _historyList = GetNode<ItemList>("%RecordingList");
        _historyList.GuiInput += OnHistoryGuiInput;
        _historyCount = GetNode<Label>("%HistoryCount");
        _emptyHistory = GetNode<Label>("%EmptyHistory");
        _play = GetNode<Button>("%PlayButton");
        _stopPlayback = GetNode<Button>("%StopPlaybackButton");
        var editor = GetNode<Control>("%RecordingEditor");
        _timeline = editor.GetNode<RecordingTimelineView>("TimelineRow/TimelinePanel/Timeline");
        _playbackTime = editor.GetNode<Label>("Header/PlaybackTime");
        _leftMeter = editor.GetNode<ProgressBar>("TimelineRow/LeftMeterColumn/LeftMeter");
        _rightMeter = editor.GetNode<ProgressBar>("TimelineRow/RightMeterColumn/RightMeter");
        _selectionStart = editor.GetNode<SpinBox>("SelectionRow/StartTime");
        _selectionEnd = editor.GetNode<SpinBox>("SelectionRow/EndTime");
        _generatePart = editor.GetNode<Button>("SelectionRow/GenerateButton");
        _timeline.SelectionRequested += SetSelection;
        _selectionStart.ValueChanged += value => SetSelection(value, Math.Max(value, _selectionEnd.Value));
        _selectionEnd.ValueChanged += value => SetSelection(Math.Min(value, _selectionStart.Value), value);
        _generatePart.Pressed += GenerateSelectedPart;

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
        _play.Pressed += TogglePlayback;
        _stopPlayback.Pressed += StopPlayback;
    }
}
