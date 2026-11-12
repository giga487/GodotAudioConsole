using AudioConsole.Core;
using Godot;

public partial class AudioSourceStripView : HBoxContainer
{
    [Signal] public delegate void GainRequestedEventHandler(string identity, double percent);
    [Signal] public delegate void RemoveRequestedEventHandler(string identity);
    private string _identity = "";
    private Label _name = null!, _value = null!;
    private HSlider _gain = null!;
    private ProgressBar _level = null!;
    private Button _remove = null!;

    public override void _Ready()
    {
        _name = GetNode<Label>("Name");
        _value = GetNode<Label>("GainValue");
        _gain = GetNode<HSlider>("Gain");
        _level = GetNode<ProgressBar>("Level");
        _remove = GetNode<Button>("Remove");
        _gain.ValueChanged += value => EmitSignal(SignalName.GainRequested, _identity, value);
        _remove.Pressed += () => EmitSignal(SignalName.RemoveRequested, _identity);
    }

    public void ShowSource(AudioSourceState source, bool configurationLocked, bool suspended)
    {
        _identity = source.Device.Identity;
        _name.Text = (source.Device.Source == AudioSourceKind.ComputerOutput ? "PC • " : "MIC • ") + source.Device.Name;
        _name.TooltipText = source.Error ?? source.Device.Name;
        _name.Modulate = source.Error == null ? Colors.White : new Color("ec6262");
        _gain.SetValueNoSignal(source.VolumePercent);
        _gain.Editable = !suspended;
        _value.Text = $"{source.VolumePercent:0}%";
        _level.Value = Math.Max(source.Levels.Left, source.Levels.Right);
        _remove.Disabled = configurationLocked;
    }
}
