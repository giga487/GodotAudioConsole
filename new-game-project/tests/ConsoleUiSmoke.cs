using AudioConsole.Core;
using Godot;

/// <summary>UI integration scenario; run tests/console_ui_smoke.tscn, never opens hardware.</summary>
public partial class ConsoleUiSmoke : Node
{
    private readonly SyntheticInput _input = new();

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
            view.AudioInputOverride = _input;
            view.AudioPlaybackOverride = new SyntheticPlayback();
            AddChild(view);
            await ToSignal(GetTree().CreateTimer(0.15), SceneTreeTimer.SignalName.Timeout);
            Button Button(string name) => view.GetNode<Button>("%" + name);
            void Press(string name) => Button(name).EmitSignal(BaseButton.SignalName.Pressed);
            Check(!Button("RecordButton").Disabled && Button("StopButton").Disabled && Button("SaveButton").Disabled,
                "Initial button states.");
            Press("RecordButton");
            await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);
            Check(Button("RecordButton").Disabled && !Button("StopButton").Disabled, "Recording button states.");
            view.FindChildren("*", "HSlider", true, false).OfType<HSlider>().Single().Value = 50;
            await ToSignal(GetTree().CreateTimer(0.15), SceneTreeTimer.SignalName.Timeout);
            Press("StopButton");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(!Button("SaveButton").Disabled, "Stopped recording can be saved.");
            var confirmation = view.GetChildren().OfType<ConfirmationDialog>().Single(dialog => dialog is not FileDialog);
            var list = view.GetNode<ItemList>("%RecordingList");
            Check(list.ItemCount == 1 && list.GetItemText(0).Contains("Temporanea"), "Stopped take must appear in history before export.");
            Press("PlayButton");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(!Button("StopPlaybackButton").Disabled && Button("PlayButton").Icon.ResourcePath.EndsWith("pause.svg"), "Playing switches the button to PAUSE.");
            Press("PlayButton");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(Button("PlayButton").Icon.ResourcePath.EndsWith("play.svg") && !Button("StopPlaybackButton").Disabled,
                "Paused playback offers PLAY and STOP.");
            Press("PlayButton");
            Press("StopPlaybackButton");
            var dialog = view.GetChildren().OfType<FileDialog>().Single();
            var path = ProjectSettings.GlobalizePath("res://.godot/csharp-ui-test.wav");
            dialog.EmitSignal(FileDialog.SignalName.FileSelected, path);
            Check(System.IO.File.Exists(path), "UI command must save through the external DLL.");
            Press("RecordButton");
            await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
            Press("StopButton");
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Check(list.ItemCount == 2, "A second take must preserve the previous one.");
            list.EmitSignal(ItemList.SignalName.ItemSelected, 1L);
            Press("PlayButton");
            Press("StopPlaybackButton");
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
        private long _sampleIndex;
        public event Action<ReadOnlyMemory<float>>? SamplesAvailable;
        public event Action<string>? Failed { add { } remove { } }
        public IReadOnlyList<AudioInputDevice> GetDevices() => [new("test", "Microfono sintetico • test C#")];
        public AudioFormat Start(string deviceId) { _running = true; return new(48000, 1); }
        public void Stop() => _running = false;
        public void Dispose() => Stop();
        public void PushTone(double delta)
        {
            if (!_running) return;
            var samples = new float[(int)(48000 * delta)];
            for (var i = 0; i < samples.Length; i++)
                samples[i] = (float)(Math.Sin(2 * Math.PI * 220 * _sampleIndex++ / 48000) * 0.25);
            SamplesAvailable?.Invoke(samples);
        }
    }

    private sealed class SyntheticPlayback : IAudioPlayback
    {
        public bool IsPlaying { get; private set; }
        public bool IsPaused { get; private set; }
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
