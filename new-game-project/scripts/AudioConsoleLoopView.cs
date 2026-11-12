using AudioConsole.Core;
using Godot;

public partial class AudioConsoleView
{
    private Button _loopRecord = null!, _loopStopRecord = null!, _loopPlay = null!, _loopStop = null!, _loopSave = null!;
    private ItemList _loopList = null!;
    private Label _loopElapsed = null!, _loopCount = null!, _loopVolumeValue = null!, _loopStatus = null!;
    private HSlider _loopVolume = null!;
    private SpinBox _loopPause = null!;
    private FileDialog _loopSaveDialog = null!;
    private Guid? _loopSaveTarget;
    private RecordingInfo[] _displayedLoops = [];

    private void BindLoopControls()
    {
        var panel = GetNode<Control>("%LoopPanel");
        _loopRecord = panel.GetNode<Button>("RecordRow/Record");
        _loopStopRecord = panel.GetNode<Button>("RecordRow/StopRecord");
        _loopPlay = panel.GetNode<Button>("PlaybackRow/Play");
        _loopStop = panel.GetNode<Button>("PlaybackRow/Stop");
        _loopSave = panel.GetNode<Button>("PlaybackRow/Save");
        _loopElapsed = panel.GetNode<Label>("RecordRow/Elapsed");
        _loopCount = panel.GetNode<Label>("ListHeader/Count");
        _loopList = panel.GetNode<ItemList>("Clips");
        _loopVolume = panel.GetNode<HSlider>("VolumeRow/Volume");
        _loopVolumeValue = panel.GetNode<Label>("VolumeRow/Value");
        _loopPause = panel.GetNode<SpinBox>("PauseRow/Pause");
        _loopStatus = panel.GetNode<Label>("Status");
        _loopSaveDialog = GetNode<FileDialog>("%LoopSaveDialog");
        _loopRecord.Pressed += () => LoopCommand(() => _service!.StartLoopRecording());
        _loopStopRecord.Pressed += () => LoopCommand(() => { _service!.StopLoopRecording(); });
        _loopPlay.Pressed += () => LoopCommand(() => _service!.StartLoop());
        _loopStop.Pressed += () => LoopCommand(() => _service!.StopLoop());
        _loopList.ItemSelected += index =>
        {
            if (index >= 0 && index < _displayedLoops.Length) LoopCommand(() => _service!.SelectLoop(_displayedLoops[index].Id));
        };
        _loopVolume.ValueChanged += _ => ApplyLoopSettings();
        _loopPause.ValueChanged += _ => ApplyLoopSettings();
        _loopSave.Pressed += () =>
        {
            _loopSaveTarget = _service?.GetSnapshot().Loops.SelectedId;
            _loopSaveDialog.CurrentFile = $"loop_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.wav";
            _loopSaveDialog.PopupCenteredRatio(0.8f);
        };
        _loopSaveDialog.FileSelected += path => LoopCommand(() =>
        {
            if (_loopSaveTarget.HasValue && _service!.GetSnapshot().Loops.SelectedId != _loopSaveTarget) _service.SelectLoop(_loopSaveTarget.Value);
            _status.Text = "Loop salvato: " + _service!.SaveLoopWav(path);
            _loopSaveTarget = null;
        });
        _loopSaveDialog.Canceled += () => _loopSaveTarget = null;
    }

    private void LoopCommand(Action action)
    {
        try { action(); }
        catch (Exception error) { _status.Text = "Loop: " + error.Message; }
    }

    private void ApplyLoopSettings()
    {
        if (_service == null) return;
        LoopCommand(() =>
        {
            _service.SetLoopSettings(new(_loopVolume.Value, _loopPause.Value));
            ScheduleSetupSave();
        });
    }

    private void RenderLoops(ConsoleSnapshot state)
    {
        var loops = state.Loops;
        if (!_displayedLoops.SequenceEqual(loops.Clips))
        {
            _displayedLoops = loops.Clips;
            _loopList.Clear();
            foreach (var clip in loops.Clips)
            {
                var index = _loopList.AddItem($"{clip.Name} • {FormatDuration(clip.Duration)}");
                _loopList.SetItemTooltip(index, clip.ExportPath ?? "Clip temporanea • salva WAV per conservarla");
            }
        }
        for (var i = 0; i < _displayedLoops.Length; i++)
        {
            _loopList.SetItemDisabled(i, loops.IsRecording);
            if (_displayedLoops[i].Id == loops.SelectedId) _loopList.Select(i);
        }
        _loopRecord.Disabled = !state.InputAvailable || state.IsInputSuspended || loops.IsRecording;
        _loopStopRecord.Disabled = !loops.IsRecording;
        _loopPlay.Disabled = loops.SelectedId == null || loops.IsRecording || state.IsInputSuspended || !state.InputAvailable || loops.IsPlaying;
        _loopStop.Disabled = !loops.IsPlaying;
        _loopSave.Disabled = loops.SelectedId == null || loops.IsRecording;
        _loopElapsed.Text = FormatDuration(loops.RecordingElapsed);
        _loopCount.Text = $"{loops.Clips.Length} / 10";
        _loopVolume.SetValueNoSignal(loops.Settings.VolumePercent);
        _loopPause.SetValueNoSignal(loops.Settings.PauseSeconds);
        _loopVolumeValue.Text = $"{loops.Settings.VolumePercent:0}%";
        _loopsToggle.Text = loops.IsRecording ? "Loop • REC" : loops.IsPlaying ? "Loop • LIVE" : "Loop";
        _loopStatus.Text = loops.Error ?? (loops.IsRecording ? "REC • sorgenti live, senza il loop precedente."
            : loops.IsWaiting ? $"In pausa • nuovo giro fra {loops.UntilNext.TotalSeconds:0.0} s"
            : loops.IsPlaying ? $"LOOP LIVE • {FormatDuration(loops.Position)}"
            : loops.SelectedId.HasValue ? "Clip pronta. ▶ la ripete nel mix dal vivo." : "Registra il primo loop con il pulsante rosso.");
    }
}
