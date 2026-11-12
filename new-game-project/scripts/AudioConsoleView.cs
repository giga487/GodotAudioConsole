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
        BuildInterface();
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

    private void BuildInterface()
    {
        var theme = new Theme { DefaultFontSize = 17 };
        theme.SetColor("font_color", "Label", new Color("e6e6e6"));
        foreach (var state in new[] { "normal", "hover", "pressed", "disabled", "focus" })
        {
            var style = new StyleBoxFlat
            {
                BgColor = new Color(state switch
                {
                    "hover" => "616161", "pressed" => "383838", "disabled" => "333333", _ => "505050"
                })
            };
            style.SetCornerRadiusAll(4);
            style.SetContentMarginAll(12);
            if (state == "focus")
            {
                style.BgColor = Colors.Transparent;
                style.BorderColor = new Color("a9c797");
                style.SetBorderWidthAll(2);
            }
            theme.SetStylebox(state, "Button", style);
            theme.SetStylebox(state, "OptionButton", style);
        }
        Theme = theme;
        var background = new ColorRect { Color = new Color("292929"), MouseFilter = MouseFilterEnum.Ignore };
        AddChild(background);
        background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var margin = new MarginContainer();
        AddChild(margin);
        margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "left", "top", "right", "bottom" })
            margin.AddThemeConstantOverride("margin_" + side, 24);
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 14);
        margin.AddChild(column);
        var header = new HBoxContainer();
        column.AddChild(header);
        var title = MakeLabel("AUDIO CONSOLE", 20);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(title);
        _signal = MakeLabel("INGRESSO LIVE", 14);
        header.AddChild(_signal);
        _waveform = new WaveformView();
        column.AddChild(_waveform);
        var deviceRow = MakeRow(column);
        deviceRow.AddChild(MakeLabel("Microfono"));
        _devices = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
        _devices.ItemSelected += SelectDevice;
        deviceRow.AddChild(_devices);
        _refresh = MakeButton("Aggiorna", RefreshDevices);
        deviceRow.AddChild(_refresh);
        var volumeRow = MakeRow(column);
        volumeRow.AddChild(MakeLabel("Volume registrazione"));
        var gain = new HSlider
        {
            MinValue = 0, MaxValue = 200, Step = 1, Value = 100,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new(140, 0),
            TooltipText = "100% = livello originale. Oltre 100% aumenta il guadagno."
        };
        gain.ValueChanged += value =>
        {
            _service?.SetVolume(value);
            _volumeLabel.Text = $"{value:0}%";
        };
        volumeRow.AddChild(gain);
        _volumeLabel = MakeLabel("100%");
        _volumeLabel.CustomMinimumSize = new(56, 0);
        _volumeLabel.HorizontalAlignment = HorizontalAlignment.Right;
        volumeRow.AddChild(_volumeLabel);
        var transport = MakeRow(column);
        _record = MakeButton("Registra", () => StartRecording());
        _record.Disabled = true;
        transport.AddChild(_record);
        _stop = MakeButton("Stop", StopRecording);
        _stop.Disabled = true;
        transport.AddChild(_stop);
        _save = MakeButton("Salva WAV", OpenSaveDialog);
        _save.Disabled = true;
        transport.AddChild(_save);
        _timer = MakeLabel("00:00.0", 26);
        _timer.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _timer.HorizontalAlignment = HorizontalAlignment.Right;
        transport.AddChild(_timer);
        _status = MakeLabel("Pronto. Seleziona il microfono e premi Registra.", 15);
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _status.CustomMinimumSize = new(0, 44);
        column.AddChild(_status);
        _saveDialog = new FileDialog
        {
            Title = "Salva registrazione WAV", FileMode = FileDialog.FileModeEnum.SaveFile,
            Access = FileDialog.AccessEnum.Filesystem, Filters = ["*.wav ; Audio WAV"], UseNativeDialog = true
        };
        _saveDialog.FileSelected += SaveWav;
        AddChild(_saveDialog);
        _discardDialog = new ConfirmationDialog
        {
            Title = "Registrazione non salvata", DialogText = "La registrazione non è salvata. Vuoi scartarla?",
            OkButtonText = "Scarta", CancelButtonText = "Annulla"
        };
        _discardDialog.Confirmed += () =>
        {
            if (_pendingQuit) GetTree().Quit();
            else StartRecording(discardUnsaved: true);
        };
        AddChild(_discardDialog);
    }

    private static HBoxContainer MakeRow(Node parent)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        parent.AddChild(row);
        return row;
    }

    private static Label MakeLabel(string text, int size = 17)
    {
        var label = new Label { Text = text };
        label.AddThemeFontSizeOverride("font_size", size);
        return label;
    }

    private static Button MakeButton(string text, Action action)
    {
        var button = new Button { Text = text };
        button.Pressed += action;
        return button;
    }
}
