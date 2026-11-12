using AudioConsole.Core;
using Godot;

/// <summary>UI integration scenario; run tests/console_ui_smoke.tscn, never opens hardware.</summary>
public partial class ConsoleUiSmoke : Node
{
    private readonly SyntheticInput _input = new();
    private readonly SyntheticPlayback _playback = new();
    private readonly SyntheticRedirect _redirect = new();

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
            view.AudioRedirectOverride = _redirect;
            var setupPath = ProjectSettings.GlobalizePath($"res://.godot/test-last-setup-{Guid.NewGuid():N}.json");
            view.SetupFileOverride = setupPath;
            AddChild(view);
            view.SetDeferred(Control.PropertyName.Size, new Vector2(1280, 900)); // Headless windows expose a 64px viewport.
            await ToSignal(GetTree().CreateTimer(0.15), SceneTreeTimer.SignalName.Timeout);
            Button Button(string name) => view.GetNode<Button>("%" + name);
            void Press(string name) => Button(name).EmitSignal(BaseButton.SignalName.Pressed);
            var sidebar = view.GetNode<Control>("%EffectsSidebar");
            var backdrop = view.GetNode<Control>("%EffectsBackdrop");
            var mainMargin = view.GetNode<Control>("Margin");
            Check(sidebar.Visible && !backdrop.Visible &&
                sidebar.IsAncestorOf(view.GetNode<Control>("%EqualizerPanel")) && sidebar.IsAncestorOf(view.GetNode<Control>("%EchoPanel")),
                $"Both effects are authored inside the docked right sidebar (size={view.Size}, visible={sidebar.Visible}, backdrop={backdrop.Visible}).");
            var dockedWidth = mainMargin.Size.X;
            Press("CloseEffects");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(!sidebar.Visible && !Button("EffectsToggle").ButtonPressed && mainMargin.Size.X > dockedWidth + 300,
                "Closing the sidebar returns its space to the console.");
            Button("EffectsToggle").ButtonPressed = true;
            var wideSize = view.Size;
            view.Size = new Vector2(640, 480);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(sidebar.Visible && backdrop.Visible && mainMargin.OffsetRight == 0 &&
                sidebar.GetGlobalRect().End.X <= view.GetGlobalRect().End.X,
                "Narrow windows use a right drawer without compressing the console.");
            backdrop.EmitSignal(Control.SignalName.GuiInput, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true });
            Check(!sidebar.Visible && !backdrop.Visible, "Clicking outside the narrow drawer closes it.");
            Button("EffectsToggle").ButtonPressed = true;
            view._UnhandledKeyInput(new InputEventKey { Pressed = true, Keycode = Key.Escape });
            Check(!sidebar.Visible, "Escape closes the effects drawer.");
            view.Size = wideSize;
            Button("EffectsToggle").ButtonPressed = true;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
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
            var redirectDevice = view.GetNode<OptionButton>("%RedirectDevice");
            var redirectMode = view.GetNode<OptionButton>("%RedirectMode");
            Check(redirectDevice.Selected == 0 && Button("RedirectStart").Disabled, "Sending requires an explicit destination.");
            redirectDevice.Select(1);
            redirectDevice.EmitSignal(OptionButton.SignalName.ItemSelected, 1L);
            Press("RedirectStart");
            await ToSignal(GetTree().CreateTimer(0.15), SceneTreeTimer.SignalName.Timeout);
            Check(_redirect.IsActive && _redirect.SamplesReceived && _input.IsRunning && !Button("RedirectStop").Disabled,
                "Live send keeps capture running and forwards mixed samples.");
            var eqPanel = view.GetNode<Control>("%EqualizerPanel");
            var eqEnabled = eqPanel.GetNode<CheckBox>("Header/Enabled");
            Check(!eqEnabled.ButtonPressed && eqPanel.GetNode<HSlider>("Bands/Bass").Value == 0,
                "Static EQ panel starts flat and bypassed.");
            eqEnabled.ButtonPressed = true;
            eqPanel.GetNode<HSlider>("Bands/Bass").Value = 4;
            eqPanel.GetNode<HSlider>("Bands/Mid").Value = -3;
            eqPanel.GetNode<HSlider>("Bands/Treble").Value = 2;
            eqPanel.GetNode<HSlider>("Bands/Output").Value = -6;
            await ToSignal(GetTree().CreateTimer(0.15), SceneTreeTimer.SignalName.Timeout);
            Check(_redirect.IsActive && _input.IsRunning && Button("StopButton").Disabled &&
                eqPanel.GetNode<Label>("Bands/BassValue").Text.Contains("4.0"),
                "EQ controls update during live sending without starting a recording.");
            if (DisplayServer.GetName() != "headless")
            {
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                GetViewport().GetTexture().GetImage().SavePng("res://.godot/equalizer-preview.png");
            }
            eqPanel.GetNode<Button>("Header/Reset").EmitSignal(BaseButton.SignalName.Pressed);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(eqPanel.GetNode<HSlider>("Bands/Bass").Value == 0 && eqEnabled.ButtonPressed,
                "EQ reset restores flat gain while preserving enabled state.");
            eqEnabled.ButtonPressed = false;
            var echoPanel = view.GetNode<Control>("%EchoPanel");
            var echoEnabled = echoPanel.GetNode<CheckBox>("Header/Enabled");
            Check(!echoEnabled.ButtonPressed && echoPanel.GetNode<HSlider>("Parameters/Delay").Value == 250,
                "Echo controls exist in the scene and start bypassed with default delay.");
            echoEnabled.ButtonPressed = true;
            echoPanel.GetNode<HSlider>("Parameters/Delay").Value = 300;
            echoPanel.GetNode<HSlider>("Parameters/Feedback").Value = 45;
            echoPanel.GetNode<HSlider>("Parameters/Amount").Value = 25;
            await ToSignal(GetTree().CreateTimer(0.15), SceneTreeTimer.SignalName.Timeout);
            Check(_redirect.IsActive && _input.IsRunning && Button("StopButton").Disabled &&
                echoPanel.GetNode<Label>("Parameters/DelayValue").Text == "300 ms",
                "Echo can be adjusted while sending live without REC.");
            Press("CloseEffects");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(!sidebar.Visible && _redirect.IsActive && _input.IsRunning && echoEnabled.ButtonPressed &&
                Button("EffectsToggle").Text.Contains("1"),
                "Hiding effects preserves enabled echo and live routing and shows the active count.");
            Button("EffectsToggle").ButtonPressed = true;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(sidebar.Visible && echoPanel.GetNode<HSlider>("Parameters/Delay").Value == 300,
                "Reopening the sidebar preserves effect controls.");
            if (DisplayServer.GetName() != "headless")
            {
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                GetViewport().GetTexture().GetImage().SavePng("res://.godot/echo-preview.png");
            }
            echoPanel.GetNode<Button>("Header/Reset").EmitSignal(BaseButton.SignalName.Pressed);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(echoEnabled.ButtonPressed && echoPanel.GetNode<HSlider>("Parameters/Delay").Value == 250 &&
                echoPanel.GetNode<HSlider>("Parameters/Feedback").Value == 35 && echoPanel.GetNode<HSlider>("Parameters/Amount").Value == 30,
                "Echo reset restores default parameters while preserving enabled state.");
            echoEnabled.ButtonPressed = false;
            view.Size = new Vector2(1660, 900);
            Button("LoopsToggle").ButtonPressed = true;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var loopSidebar = view.GetNode<Control>("%LoopsSidebar");
            var loopPanel = view.GetNode<Control>("%LoopPanel");
            void LoopPress(string path) => loopPanel.GetNode<Button>(path).EmitSignal(BaseButton.SignalName.Pressed);
            var loopList = loopPanel.GetNode<ItemList>("Clips");
            Check(loopSidebar.Visible && sidebar.Visible && loopSidebar.GetGlobalRect().Position.X > sidebar.GetGlobalRect().End.X,
                "Wide layouts show a separate Loop column alongside Effects.");
            var originalHistoryCount = list.ItemCount;
            LoopPress("RecordRow/Record");
            await ToSignal(GetTree().CreateTimer(0.25), SceneTreeTimer.SignalName.Timeout);
            Check(sourceMode.Disabled && !loopPanel.GetNode<Button>("RecordRow/StopRecord").Disabled && _input.IsRunning,
                "Dedicated loop recording keeps input active and locks source configuration.");
            LoopPress("RecordRow/StopRecord");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(loopList.ItemCount == 1 && list.ItemCount == originalHistoryCount,
                "Loop capture adds one independent clip without adding a main recording.");
            loopPanel.GetNode<HSlider>("VolumeRow/Volume").Value = 75;
            loopPanel.GetNode<SpinBox>("PauseRow/Pause").Value = 0.2;
            LoopPress("PlaybackRow/Play");
            await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);
            Check(_input.IsRunning && _redirect.IsActive && !loopPanel.GetNode<Button>("PlaybackRow/Stop").Disabled,
                "A loop runs in the same outgoing live mix as the microphone.");
            if (DisplayServer.GetName() != "headless")
            {
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                GetViewport().GetTexture().GetImage().SavePng("res://.godot/loop-bank-preview.png");
            }
            Press("CloseLoops");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(!loopSidebar.Visible && Button("LoopsToggle").Text.Contains("LIVE") && _redirect.IsActive,
                "Closing the Loop column preserves its running loop and sending.");
            Button("LoopsToggle").ButtonPressed = true;
            var loopSaveDialog = view.GetNode<FileDialog>("%LoopSaveDialog");
            loopSaveDialog.UseNativeDialog = false;
            LoopPress("PlaybackRow/Save");
            Check(loopSaveDialog.Visible, "The selected loop can be exported independently.");
            loopSaveDialog.Hide();
            var loopPath = ProjectSettings.GlobalizePath("res://.godot/loop-ui-test.wav");
            loopSaveDialog.EmitSignal(FileDialog.SignalName.FileSelected, loopPath);
            Check(System.IO.File.Exists(loopPath), "Loop export writes a WAV through the core library.");
            LoopPress("PlaybackRow/Stop");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(_input.IsRunning && _redirect.IsActive && loopPanel.GetNode<Button>("PlaybackRow/Stop").Disabled,
                "Stopping the loop preserves live microphone sending.");
            view.Size = wideSize;
            Press("RedirectStop");
            redirectMode.Select(1);
            redirectMode.EmitSignal(OptionButton.SignalName.ItemSelected, 1L);
            Press("RedirectStart");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(_redirect.IsActive && !_input.IsRunning && !_playback.IsPlaying && redirectDevice.Disabled,
                "File send suspends capture and locks destination changes without speaker playback.");
            Press("RedirectStop");
            Check(_input.IsRunning, "Stopping file send restores capture.");
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
            await ToSignal(GetTree().CreateTimer(0.45), SceneTreeTimer.SignalName.Timeout);
            Check(new LastSetupStore(setupPath).Load() is { Sources.Length: 2, MasterVolumePercent: 50 },
                "Setup changes are saved automatically after the debounce interval.");
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
            view.GetNode<HSlider>("%VolumeSlider").Value = 67;
            sourceRows.GetChildren().OfType<AudioSourceStripView>().First().GetNode<HSlider>("Gain").Value = 35;
            eqEnabled.ButtonPressed = true;
            eqPanel.GetNode<HSlider>("Bands/Bass").Value = -4;
            eqPanel.GetNode<HSlider>("Bands/Mid").Value = 2;
            eqPanel.GetNode<HSlider>("Bands/Treble").Value = 3;
            eqPanel.GetNode<HSlider>("Bands/Output").Value = -3;
            echoEnabled.ButtonPressed = true;
            echoPanel.GetNode<HSlider>("Parameters/Delay").Value = 400;
            echoPanel.GetNode<HSlider>("Parameters/Feedback").Value = 55;
            echoPanel.GetNode<HSlider>("Parameters/Amount").Value = 40;
            view.Free(); // Exit must flush changes even when the debounce timer has not elapsed.
            var saved = new LastSetupStore(setupPath).Load()!;
            Check(saved.Sources.Length == 2 && saved.MasterVolumePercent == 67 && saved.Sources[0].VolumePercent == 35,
                "Closing the UI persists the complete last setup.");
            Check(saved.Equalizer == new EqualizerSettings(true, -4, 2, 3, -3), "Closing persists every EQ control.");
            Check(saved.Echo == new EchoSettings(true, 400, 55, 40), "Closing persists every echo control.");
            Check(saved.Loop == new LoopSettings(75, 0.2), "Closing persists default loop volume and pause.");
            AudioConsoleView Reopen()
            {
                var reopened = GD.Load<PackedScene>("res://scenes/audio_console.tscn").Instantiate<AudioConsoleView>();
                reopened.AudioInputOverride = new MultiSourceAudioInput(_input.CreateInput);
                reopened.AudioPlaybackOverride = new SyntheticPlayback();
                reopened.AudioRedirectOverride = new SyntheticRedirect();
                reopened.SetupFileOverride = setupPath;
                AddChild(reopened);
                reopened.SetDeferred(Control.PropertyName.Size, new Vector2(1280, 900));
                return reopened;
            }
            var restored = Reopen();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var restoredRows = restored.GetNode<VBoxContainer>("%SourceRows");
            var restoredEq = restored.GetNode<Control>("%EqualizerPanel");
            var restoredEcho = restored.GetNode<Control>("%EchoPanel");
            var restoredLoop = restored.GetNode<Control>("%LoopPanel");
            Check(restoredLoop.GetNode<HSlider>("VolumeRow/Volume").Value == 75 &&
                restoredLoop.GetNode<SpinBox>("PauseRow/Pause").Value == 0.2 && restoredLoop.GetNode<ItemList>("Clips").ItemCount == 0,
                "Reopening restores loop defaults without session audio or automatic playback.");
            Check(restoredEcho.GetNode<CheckBox>("Header/Enabled").ButtonPressed &&
                restoredEcho.GetNode<HSlider>("Parameters/Delay").Value == 400 &&
                restoredEcho.GetNode<HSlider>("Parameters/Feedback").Value == 55 &&
                restoredEcho.GetNode<HSlider>("Parameters/Amount").Value == 40,
                "Every echo control is restored on reopening the UI.");
            Check(restoredEq.GetNode<CheckBox>("Header/Enabled").ButtonPressed &&
                restoredEq.GetNode<HSlider>("Bands/Bass").Value == -4 && restoredEq.GetNode<HSlider>("Bands/Mid").Value == 2 &&
                restoredEq.GetNode<HSlider>("Bands/Treble").Value == 3 && restoredEq.GetNode<HSlider>("Bands/Output").Value == -3,
                "All EQ controls restore on reopening the UI.");
            Check(restored.GetNode<OptionButton>("%RedirectDevice").Selected == 1 &&
                restored.GetNode<OptionButton>("%RedirectMode").Selected == 1 && restored.GetNode<Button>("%RedirectStop").Disabled,
                "Routing choices restore without restarting transmission.");
            Check(restoredRows.GetChildCount() == 2 && restored.GetNode<HSlider>("%VolumeSlider").Value == 67 &&
                restoredRows.GetChildren().OfType<AudioSourceStripView>().First().GetNode<HSlider>("Gain").Value == 35,
                "Reopening the UI restores sources and all gain controls.");
            foreach (var strip in restoredRows.GetChildren().OfType<AudioSourceStripView>().ToArray())
                strip.GetNode<Button>("Remove").EmitSignal(BaseButton.SignalName.Pressed);
            restored.Free();
            var emptyRestored = Reopen();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(emptyRestored.GetNode<VBoxContainer>("%SourceRows").GetChildCount() == 0 && emptyRestored.GetNode<Button>("%RecordButton").Disabled,
                "An empty saved setup must not silently add a microphone.");
            emptyRestored.Free();
            System.IO.File.WriteAllText(setupPath, "{ broken setup");
            var corruptRestored = Reopen();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(corruptRestored.GetNode<Label>("%StatusLabel").Text.StartsWith("Last setup non caricato"),
                "A damaged setup must be reported without preventing UI startup.");
            corruptRestored.Free();
            Check(System.IO.File.ReadAllText(setupPath) == "{ broken setup", "Fallback startup must not overwrite a damaged setup.");
            GD.Print("C# Godot UI integration: PASS");
            GetTree().Quit();
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

    private sealed class SyntheticRedirect : IAudioRedirect
    {
        public bool IsActive { get; private set; }
        public bool SamplesReceived { get; private set; }
        public string? Error => null;
        public TimeSpan Position => TimeSpan.FromSeconds(0.1);
        public IReadOnlyList<AudioOutputDevice> GetDevices() => [new("virtual", "CABLE Input • test", ["loopback:virtual"])];
        public void StartLive(string id, AudioFormat format) => IsActive = true;
        public void WriteSamples(ReadOnlyMemory<float> samples) => SamplesReceived |= samples.Length > 0;
        public void PlayFile(string id, string path) { Check(System.IO.File.Exists(path), "Route uses existing WAV"); IsActive = true; }
        public void Stop() => IsActive = false;
        public void Dispose() => Stop();
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
