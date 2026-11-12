using AudioConsole.Core;
using Godot;

public partial class AudioConsoleView
{
    private CheckBox _echoEnabled = null!;
    private HSlider _echoDelay = null!, _echoFeedback = null!, _echoAmount = null!;
    private Label _echoDelayValue = null!, _echoFeedbackValue = null!, _echoAmountValue = null!;

    private void BindEchoControls()
    {
        var panel = GetNode<Control>("%EchoPanel");
        _echoEnabled = panel.GetNode<CheckBox>("Header/Enabled");
        _echoDelay = panel.GetNode<HSlider>("Parameters/Delay");
        _echoFeedback = panel.GetNode<HSlider>("Parameters/Feedback");
        _echoAmount = panel.GetNode<HSlider>("Parameters/Amount");
        _echoDelayValue = panel.GetNode<Label>("Parameters/DelayValue");
        _echoFeedbackValue = panel.GetNode<Label>("Parameters/FeedbackValue");
        _echoAmountValue = panel.GetNode<Label>("Parameters/AmountValue");
        _echoEnabled.Toggled += _ => ApplyEcho();
        _echoDelay.ValueChanged += _ => ApplyEcho();
        _echoFeedback.ValueChanged += _ => ApplyEcho();
        _echoAmount.ValueChanged += _ => ApplyEcho();
        panel.GetNode<Button>("Header/Reset").Pressed += () =>
        {
            _echoDelay.SetValueNoSignal(250);
            _echoFeedback.SetValueNoSignal(35);
            _echoAmount.SetValueNoSignal(30);
            ApplyEcho();
        };
    }

    private void ApplyEcho()
    {
        if (_service == null) return;
        try
        {
            _service.SetEcho(new(_echoEnabled.ButtonPressed, _echoDelay.Value, _echoFeedback.Value, _echoAmount.Value));
            ScheduleSetupSave();
        }
        catch (Exception error) { _status.Text = "Echo: " + error.Message; }
    }

    private void RenderEcho(EchoSettings settings)
    {
        _echoEnabled.SetPressedNoSignal(settings.Enabled);
        _echoDelay.SetValueNoSignal(settings.DelayMs);
        _echoFeedback.SetValueNoSignal(settings.FeedbackPercent);
        _echoAmount.SetValueNoSignal(settings.AmountPercent);
        _echoDelayValue.Text = $"{settings.DelayMs:0} ms";
        _echoFeedbackValue.Text = $"{settings.FeedbackPercent:0}%";
        _echoAmountValue.Text = $"{settings.AmountPercent:0}%";
    }
}
