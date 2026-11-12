using AudioConsole.Core;
using Godot;

public partial class WaveformView : Control
{
    private WaveformPeak[] _history = [];
    private static readonly Color Background = new("8fae79");
    private static readonly Color Ink = new("111b10");

    public override void _Ready()
    {
        CustomMinimumSize = new(0, 190);
        SizeFlagsVertical = SizeFlags.ExpandFill;
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public void ShowHistory(WaveformPeak[] history)
    {
        _history = history;
        QueueRedraw();
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), Background);
        var center = Size.Y / 2;
        var amplitude = Size.Y * 0.43f;
        for (var i = 1; i < 8; i++)
        {
            var x = Size.X * i / 8;
            DrawLine(new(x, 0), new(x, Size.Y), new Color(0, 0, 0, 0.07f));
        }
        DrawLine(new(0, center), new(Size.X, center), new Color(0, 0, 0, 0.25f));
        for (var i = 0; i < _history.Length; i++)
        {
            var peak = _history[i];
            var x = Size.X * i / Math.Max(1, _history.Length - 1);
            DrawLine(new(x, center - peak.High * amplitude),
                new(x, center - peak.Low * amplitude + 1), Ink, 1.5f);
        }
        DrawRect(new Rect2(Vector2.Zero, Size), Ink, filled: false, width: 2);
    }
}
