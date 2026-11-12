using AudioConsole.Core;
using Godot;

public partial class WaveformView : Control
{
    private WaveformPeak[] _history = [];
    private static readonly Color Ink = new("111b10");

    public void ShowHistory(WaveformPeak[] history)
    {
        _history = history;
        QueueRedraw();
    }

    public override void _Draw()
    {
        var center = Size.Y / 2;
        var amplitude = Size.Y * 0.43f;
        for (var i = 0; i < _history.Length; i++)
        {
            var peak = _history[i];
            var x = Size.X * i / Math.Max(1, _history.Length - 1);
            DrawLine(new(x, center - peak.High * amplitude),
                new(x, center - peak.Low * amplitude + 1), Ink, 1.5f);
        }
    }
}
