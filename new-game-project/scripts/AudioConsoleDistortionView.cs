using AudioConsole.Core;
using Godot;

public partial class AudioConsoleView
{
    private CheckBox _distEnabled = null!;
    private OptionButton _distMode = null!;
    private HSlider[] _distSliders = [];
    private Label[] _distValues = [];

    private void BindDistortionControls()
    {
        var panel = GetNode<Control>("%DistortionPanel");
        _distEnabled = panel.GetNode<CheckBox>("Header/Enabled");
        _distMode = panel.GetNode<OptionButton>("Mode");
        string[] parameters = ["Drive", "Mix", "Output"];
        _distSliders = parameters.Select(name => panel.GetNode<HSlider>("Parameters/" + name)).ToArray();
        _distValues = parameters.Select(name => panel.GetNode<Label>("Parameters/" + name + "Value")).ToArray();
        _distEnabled.Toggled += _ => ApplyDistortion();
        _distMode.ItemSelected += _ => ApplyDistortion();
        foreach (var slider in _distSliders) slider.ValueChanged += _ => ApplyDistortion();
        panel.GetNode<Button>("Header/Reset").Pressed += () =>
        {
            _distMode.Select(0);
            _distSliders[0].SetValueNoSignal(12);
            _distSliders[1].SetValueNoSignal(50);
            _distSliders[2].SetValueNoSignal(-6);
            ApplyDistortion();
        };
    }

    private void ApplyDistortion()
    {
        if (_service == null) return;
        try
        {
            _service.SetDistortion(new(_distEnabled.ButtonPressed, (DistortionMode)_distMode.Selected,
                _distSliders[0].Value, _distSliders[1].Value, _distSliders[2].Value));
            ScheduleSetupSave();
        }
        catch (Exception error) { _status.Text = "Distorsione: " + error.Message; }
    }

    private void RenderDistortion(DistortionSettings settings)
    {
        _distEnabled.SetPressedNoSignal(settings.Enabled);
        _distMode.Select((int)settings.Mode);
        double[] values = [settings.DriveDb, settings.MixPercent, settings.OutputDb];
        for (var i = 0; i < values.Length; i++)
        {
            _distSliders[i].SetValueNoSignal(values[i]);
            _distValues[i].Text = values[i].ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + (i == 1 ? "%" : " dB");
        }
    }
}
