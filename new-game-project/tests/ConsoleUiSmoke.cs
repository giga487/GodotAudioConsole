using AudioConsole.Core;
using Godot;

/// <summary>UI integration scenario; run tests/console_ui_smoke.tscn, never opens hardware.</summary>
public partial class ConsoleUiSmoke : Node
{
    private readonly SyntheticInput _input = new();
    private readonly SyntheticPlayback _playback = new();

    public override void _Process(double delta) => _input.PushTone(delta);

    public override async void _Ready()
    {
        try
        {
            var view = GD.Load<PackedScene>("res://scenes/audio_console.tscn").Instantiate<AudioConsoleView>();
            Check(view.GetNode<Button>("%RecordButton").Icon != null && view.GetNode<Button>("%RecordButton").Text == "",
                "Controls must be authored in the scene before _Ready runs.");
            Check(view.GetNode<Panel>("%WaveformDisplay").GetNode<Control>("CenterLine") != null,
                "Display background and center line must exist without runtime drawing.");
            view.AudioInputOverride = new MultiSourceAudioInput(_input.CreateInput);
            view.AudioPlaybackOverride = _playback;
            AddChild(view);
            await ToSignal(GetTree().CreateTimer(0.15), SceneTreeTimer.SignalName.Timeout);
            Button Button(string name) => view.GetNode<Button>("%" + name);
            void Press(string name) => Button(name).EmitSignal(BaseButton.SignalName.Pressed);
            var sourceMode = view.GetNode<OptionButton>("%SourceMode");
            void SwitchSource(int index)
            {
                sourceMode.Select(index);
                sourceMode.EmitSignal(OptionButton.SignalName.ItemSelected, (long)index);
            }
            Check(sourceMode.ItemCount == 2, "Recording modes must be authored in the scene.");
            _input.NoOutputs = true;
            SwitchSource(1);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(_input.IsRunning && !Button("RecordButton").Disabled && Button("AddSourceButton").Disabled,
                "Browsing unavailable outputs preserves the already-active microphone.");
            _input.NoOutputs = false;
            Press("RefreshButton");
            Check(_input.LastDeviceId == "test" && view.GetNode<OptionButton>("%SourceDeviceSelect").ItemCount == 1,
                "Refreshing available devices does not replace the active mix.");
            SwitchSource(0);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var list = view.GetNode<ItemList>("%RecordingList");
            var menu = view.GetNode<PopupMenu>("%HistoryContextMenu");
            void RightClick(int? index)
            {
                var position = index.HasValue ? list.GetItemRect(index.Value).GetCenter() : new Vector2(8, list.Size.Y - 4);
                list.EmitSignal(Control.SignalName.GuiInput,
                    new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = true, Position = position });
            }
            void MenuAction(int id) => menu.EmitSignal(PopupMenu.SignalName.IdPressed, (long)id);
            RightClick(null);
            Check(menu.Visible && menu.IsItemDisabled(0) && menu.IsItemDisabled(2), "An empty history opens a menu with unavailable actions disabled.");
            menu.Hide();
            Check(!Button("RecordButton").Disabled && Button("StopButton").Disabled && Button("SaveButton").Disabled,
                "Initial button states.");
            Press("RecordButton");
            await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);
            Check(Button("RecordButton").Disabled && !Button("StopButton").Disabled, "Recording button states.");
            Check(sourceMode.Disabled, "Source switching is disabled while recording.");
            SwitchSource(1);
            Check(sourceMode.Selected == 0 && _input.LastDeviceId == "test", "A forced source-change signal cannot bypass the recording guard.");
            view.GetNode<HSlider>("%VolumeSlider").Value = 50;
            await ToSignal(GetTree().CreateTimer(0.15), SceneTreeTimer.SignalName.Timeout);
            Press("StopButton");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(!Button("SaveButton").Disabled, "Stopped recording can be saved.");
            var confirmation = view.GetChildren().OfType<ConfirmationDialog>().Single(dialog => dialog is not FileDialog);
            Check(list.ItemCount == 1 && list.GetItemText(0).Contains("Temporanea"), "Stopped take must appear in history before export.");
            Press("PlayButton");
            _playback.Position = TimeSpan.FromSeconds(0.1);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(!Button("StopPlaybackButton").Disabled && Button("PlayButton").Icon.ResourcePath.EndsWith("pause.svg"), "Playing switches the button to PAUSE.");
            Check(!_input.IsRunning && view.GetNode<Label>("%SignalLabel").Text.Contains("MIC SOSPESO"), "Playback must suspend physical input and label it clearly.");
            Check(sourceMode.Disabled, "Source switching is disabled during replay.");
            var editor = view.GetNode<Control>("%RecordingEditor");
            Check(editor.GetNode<Label>("Header/PlaybackTime").Text.StartsWith("00:00.1"), "Playback has its own advancing timer.");
            Check(editor.GetNode<ProgressBar>("TimelineRow/LeftMeterColumn/LeftMeter").Value > 0.1, "Playback meters must show recorded sample levels.");
            Press("PlayButton");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(Button("PlayButton").Icon.ResourcePath.EndsWith("play.svg") && !Button("StopPlaybackButton").Disabled,
                "Paused playback offers PLAY and STOP.");
            Check(!_input.IsRunning, "Pausing playback must not reactivate the microphone.");
            Press("PlayButton");
            Press("StopPlaybackButton");
            Check(_input.IsRunning, "STOP restores the microphone.");
            var dialog = view.GetChildren().OfType<FileDialog>().Single();
            dialog.UseNativeDialog = false; // Keep the save-dialog integration test inside Godot.
            RightClick(0);
            MenuAction(2);
            Check(dialog.Visible, "Save WAV from the context menu opens the save dialog.");
            dialog.Hide();
            var path = ProjectSettings.GlobalizePath("res://.godot/csharp-ui-test.wav");
            dialog.EmitSignal(FileDialog.SignalName.FileSelected, path);
            Check(System.IO.File.Exists(path), "UI command must save through the external DLL.");
            Press("RecordButton");
            RightClick(0);
            Check(menu.IsItemDisabled(0) && menu.IsItemDisabled(2) && menu.IsItemDisabled(4),
                "Context commands must be disabled during recording.");
            menu.Hide();
            await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
            Press("StopButton");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(list.ItemCount == 2, "A second take must preserve the previous one.");
            RightClick(1);
            Check(menu.Visible && list.IsSelected(1) && !menu.IsItemDisabled(0), "Right-click selects the targeted older take.");
            MenuAction(0);
            Check(_playback.IsPlaying, "Context PLAY uses the right-clicked take.");
            RightClick(1);
            Check(menu.GetItemText(0) == "Pausa" && _playback.IsPlaying, "Opening the menu on the playing take preserves playback.");
            MenuAction(0);
            Check(_playback.IsPaused, "Context PAUSE pauses playback.");
            RightClick(1);
            Check(menu.GetItemText(0) == "Riprendi", "A paused take offers Resume.");
            MenuAction(0);
            Check(_playback.IsPlaying, "Context RESUME resumes playback.");
            RightClick(1);
            MenuAction(1);
            Check(!_playback.IsPlaying && _input.IsRunning, "Context STOP restores microphone capture.");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Press("PlayButton");
            Press("StopPlaybackButton");
            var timeline = editor.GetNode<RecordingTimelineView>("TimelineRow/TimelinePanel/Timeline");
            timeline._GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = new(timeline.Size.X * 0.2f, 30) });
            timeline._GuiInput(new InputEventMouseMotion { Position = new(timeline.Size.X * 0.7f, 30) });
            timeline._GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = new(timeline.Size.X * 0.7f, 30) });
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var generate = editor.GetNode<Button>("SelectionRow/GenerateButton");
            Check(!generate.Disabled && editor.GetNode<SpinBox>("SelectionRow/EndTime").Value > 0,
                "Dragging selects an interval and enables crop generation.");
            var startControl = editor.GetNode<SpinBox>("SelectionRow/StartTime");
            Check(Math.Abs(startControl.Value - startControl.MaxValue * 0.2) < 0.001,
                "Dragging must use the selected recording's duration.");
            if (DisplayServer.GetName() != "headless")
            {
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                GetViewport().GetTexture().GetImage().SavePng("res://.godot/selection-preview.png");
            }
            RightClick(1);
            Check(!menu.IsItemDisabled(4), "Context crop generation is enabled for a valid selection.");
            if (DisplayServer.GetName() != "headless")
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                menu.GetTexture().GetImage().SavePng("res://.godot/context-menu-preview.png");
            }
            MenuAction(4);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(list.ItemCount == 3 && list.GetItemText(0).Contains("Ritaglio"), "Generating selection adds a crop without replacing either original take.");
            SwitchSource(1);
            Press("AddSourceButton");
            Check(_input.LastDeviceId == "test-output", "Adding a PC device opens it alongside the microphone.");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var sourceRows = view.GetNode<VBoxContainer>("%SourceRows");
            Check(sourceRows.GetChildCount() == 2, "Both sources have visible authored mixer strips.");
            Press("AddSourceButton");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(sourceRows.GetChildCount() == 2, "Adding a source twice does not duplicate it.");
            var sourceStrip = sourceRows.GetChildren().OfType<AudioSourceStripView>().Last();
            sourceStrip.GetNode<HSlider>("Gain").Value = 50;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(sourceStrip.GetNode<Label>("GainValue").Text == "50%", "Each source has its own gain control.");
            Check(list.ItemCount == 3, "Changing capture mode preserves recordings and clips.");
            Press("PlayButton");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(view.GetNode<Label>("%SignalLabel").Text.Contains("MIX SOSPESO") && !_input.IsRunning,
                "All source captures are suspended while replaying a take.");
            Press("StopPlaybackButton");
            Check(_input.LastDeviceId == "test-output" && _input.IsRunning, "Stopping replay restores all mixer sources.");
            sourceStrip.GetNode<Button>("Remove").EmitSignal(BaseButton.SignalName.Pressed);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(sourceRows.GetChildCount() == 1 && list.ItemCount == 3, "Removing a source preserves recordings and the other source.");
            sourceRows.GetChildren().OfType<AudioSourceStripView>().Single().GetNode<Button>("Remove").EmitSignal(BaseButton.SignalName.Pressed);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(sourceRows.GetChildCount() == 0 && Button("RecordButton").Disabled && !_input.IsRunning,
                "Removing the final source stops capture and disables recording.");
            Press("AddSourceButton"); // PC picker is still selected.
            SwitchSource(0);
            Press("AddSourceButton");
            await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
            Press("RecordButton");
            await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
            Press("StopButton");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(sourceRows.GetChildCount() == 2 && list.ItemCount == 4, "Two sources produce one new mixed recording in history.");
            view.Notification((int)NotificationWMCloseRequest);
            Check(confirmation.Visible, "Closing with unsaved audio requires confirmation.");
            confirmation.Hide();
            if (DisplayServer.GetName() != "headless")
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                GetViewport().GetTexture().GetImage().SavePng("res://.godot/csharp-console-preview.png");
            }
            GD.Print("C# Godot UI integration: PASS");
            view.Notification((int)NotificationWMCloseRequest);
            confirmation.EmitSignal(ConfirmationDialog.SignalName.Confirmed);
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            GetTree().Quit(1);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class SyntheticInput : IAudioInput
    {
        private bool _running;
        private readonly SyntheticInput _owner;
        private readonly List<SyntheticInput> _instances = [];
        public SyntheticInput() => _owner = this;
        private SyntheticInput(SyntheticInput owner) { _owner = owner; owner._instances.Add(this); }
        public IAudioInput CreateInput() => new SyntheticInput(this);
        public bool IsRunning => _instances.Any(instance => instance._running);
        private long _sampleIndex;
        public event Action<ReadOnlyMemory<float>>? SamplesAvailable;
        public event Action<string>? Failed { add { } remove { } }
        public bool NoOutputs { get; set; }
        public string? LastDeviceId { get; private set; }
        public IReadOnlyList<AudioInputDevice> GetDevices(AudioSourceKind source = AudioSourceKind.Microphone)
            => source == AudioSourceKind.ComputerOutput ? _owner.NoOutputs ? [] : [new("test-output", "Uscita PC sintetica • test C#", source)]
                : [new("test", "Microfono sintetico • test C#")];
        public AudioFormat Start(string deviceId) { _running = true; _owner.LastDeviceId = deviceId; return StereoMixer.Format; }
        public void Stop() => _running = false;
        public void Dispose() { Stop(); _owner._instances.Remove(this); }
        public void PushTone(double delta)
        {
            foreach (var instance in _instances.ToArray())
            {
                if (!instance._running) continue;
                var samples = new float[(int)(48000 * delta) * 2];
                for (var i = 0; i < samples.Length; i += 2)
                    samples[i] = samples[i + 1] = (float)(Math.Sin(2 * Math.PI * 220 * instance._sampleIndex++ / 48000) * 0.25);
                instance.SamplesAvailable?.Invoke(samples);
            }
        }
    }

    private sealed class SyntheticPlayback : IAudioPlayback
    {
        public bool IsPlaying { get; private set; }
        public bool IsPaused { get; private set; }
        public TimeSpan Position { get; set; }
        public string? Error => null;
        public void Play(string wavPath)
        {
            Check(System.IO.File.Exists(wavPath), "Playback must use an existing temporary file.");
            IsPlaying = true;
            IsPaused = false;
        }
        public void Pause() { IsPaused = IsPlaying; IsPlaying = false; }
        public void Resume() { IsPlaying = IsPaused; IsPaused = false; }
        public void Stop() { IsPlaying = false; IsPaused = false; }
        public void Dispose() => Stop();
    }
}
