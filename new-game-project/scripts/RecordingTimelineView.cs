using AudioConsole.Core;
using Godot;
using System.Globalization;

/// <summary>Only interaction and drawing; audio, selection validation and slicing live in Core.</summary>
public partial class RecordingTimelineView : Control
{
    [Signal] public delegate void SelectionRequestedEventHandler(double startSeconds, double endSeconds);
    private RecordingEditorSnapshot? _state;
    private bool _dragging;
    private double _anchor;
    public bool InteractionEnabled { get; set; } = true;

    public void ShowRecording(RecordingEditorSnapshot? state)
    {
        if (_state?.RecordingId != state?.RecordingId) _dragging = false;
        _state = state;
        QueueRedraw();
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (!InteractionEnabled || _state == null || _state.Duration <= TimeSpan.Zero) return;
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left } button)
        {
            if (button.Pressed) { _dragging = true; _anchor = TimeAt(button.Position.X); }
            if (_dragging) RequestSelection(TimeAt(button.Position.X));
            if (!button.Pressed) _dragging = false;
            AcceptEvent();
        }
        else if (@event is InputEventMouseMotion motion && _dragging)
        {
            RequestSelection(TimeAt(motion.Position.X));
            AcceptEvent();
        }
    }

    private double TimeAt(float x) => Math.Clamp(x / Math.Max(1, Size.X), 0, 1) * (_state?.Duration.TotalSeconds ?? 0);
    private void RequestSelection(double end) => EmitSignal(SignalName.SelectionRequested, Math.Min(_anchor, end), Math.Max(_anchor, end));

    public override void _Draw()
    {
        var height = Math.Max(1, Size.Y - 24);
        var center = height / 2;
        var duration = _state?.Duration.TotalSeconds ?? 0;
        for (var tick = 0; tick <= 4; tick++)
        {
            var x = Size.X * tick / 4;
            DrawLine(new(x, 0), new(x, height), new Color(1, 1, 1, 0.1f));
            var seconds = duration * tick / 4;
            var secondsFormat = duration < 10 ? "00.000" : "00.0";
            var label = $"{(int)seconds / 60:00}:" + (seconds % 60).ToString(secondsFormat, CultureInfo.InvariantCulture);
            var textWidth = GetThemeDefaultFont().GetStringSize(label, fontSize: 12).X;
            DrawString(GetThemeDefaultFont(), new(Math.Clamp(x - textWidth / 2, 0, Math.Max(0, Size.X - textWidth)), Size.Y - 4),
                label, fontSize: 12, modulate: new Color("bcbcbc"));
        }
        DrawLine(new(0, center), new(Size.X, center), new Color(1, 1, 1, 0.2f));
        if (_state == null || duration <= 0) return;
        var start = (float)(_state.SelectionStart.TotalSeconds / duration) * Size.X;
        var end = (float)(_state.SelectionEnd.TotalSeconds / duration) * Size.X;
        if (end > start)
        {
            DrawRect(new Rect2(start, 0, end - start, height), new Color(0.45f, 0.7f, 0.95f, 0.22f));
            DrawLine(new(start, 0), new(start, height), new Color("92bfe8"), 2);
            DrawLine(new(end, 0), new(end, height), new Color("92bfe8"), 2);
        }
        var peaks = _state.Waveform;
        for (var i = 0; i < peaks.Length; i++)
        {
            var x = (i + 0.5f) * Size.X / peaks.Length;
            DrawLine(new(x, center - peaks[i].High * height * 0.43f),
                new(x, center - peaks[i].Low * height * 0.43f + 1), new Color("9ebb88"), Math.Max(1, Size.X / peaks.Length * 0.65f));
        }
        var playhead = (float)Math.Clamp(_state.Position.TotalSeconds / duration, 0, 1) * Size.X;
        DrawLine(new(playhead, 0), new(playhead, height), new Color("f0cc79"), 2);
    }
}
