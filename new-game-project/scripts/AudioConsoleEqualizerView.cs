using AudioConsole.Core;
using Godot;

public partial class AudioConsoleView
{
    private CheckBox _eqEnabled = null!;
    private HSlider[] _eqSliders = [];
    private Label[] _eqValues = [];

    private void BindEqualizerControls()
    {
        var panel = GetNode<Control>("%EqualizerPanel");
        _eqEnabled = panel.GetNode<CheckBox>("Header/Enabled");
        string[] bands = ["Bass", "Mid", "Treble", "Output"];
        _eqSliders = bands.Select(name => panel.GetNode<HSlider>("Bands/" + name)).ToArray();
        _eqValues = bands.Select(name => panel.GetNode<Label>("Bands/" + name + "Value")).ToArray();
        _eqEnabled.Toggled += _ => ApplyEqualizer();
        foreach (var slider in _eqSliders) slider.ValueChanged += _ => ApplyEqualizer();
        panel.GetNode<Button>("Header/Reset").Pressed += () =>
        {
            foreach (var slider in _eqSliders) slider.SetValueNoSignal(0);
            ApplyEqualizer();
        };
    }

    private void ApplyEqualizer()
    {
        if (_service == null) return;
        try
        {
            _service.SetEqualizer(new(_eqEnabled.ButtonPressed, _eqSliders[0].Value, _eqSliders[1].Value,
                _eqSliders[2].Value, _eqSliders[3].Value));
            ScheduleSetupSave();
        }
        catch (Exception error) { _status.Text = "Equalizzatore: " + error.Message; }
    }

    private void RenderEqualizer(EqualizerSettings settings)
    {
        _eqEnabled.SetPressedNoSignal(settings.Enabled);
        double[] values = [settings.BassDb, settings.MidDb, settings.TrebleDb, settings.OutputDb];
        for (var i = 0; i < values.Length; i++)
        {
            _eqSliders[i].SetValueNoSignal(values[i]);
            _eqValues[i].Text = values[i].ToString("+0.0;-0.0;0.0", System.Globalization.CultureInfo.InvariantCulture) + " dB";
        }
    }
}
