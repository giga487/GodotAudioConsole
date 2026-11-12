using AudioConsole.Core;
using Godot;

public partial class AudioConsoleView
{
    private Control _effectsSidebar = null!, _effectsBackdrop = null!, _mainMargin = null!;
    private Button _effectsToggle = null!;

    private void BindEffectsSidebar()
    {
        _effectsSidebar = GetNode<Control>("%EffectsSidebar");
        _effectsBackdrop = GetNode<Control>("%EffectsBackdrop");
        _mainMargin = GetNode<Control>("Margin");
        _effectsToggle = GetNode<Button>("%EffectsToggle");
        _effectsToggle.Toggled += SetEffectsVisible;
        GetNode<Button>("%CloseEffects").Pressed += () => SetEffectsVisible(false);
        _effectsBackdrop.GuiInput += input =>
        {
            if (input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            {
                SetEffectsVisible(false);
                _effectsBackdrop.AcceptEvent();
            }
        };
        Resized += UpdateEffectsLayout;
        UpdateEffectsLayout();
    }

    private void SetEffectsVisible(bool visible)
    {
        _effectsSidebar.Visible = visible;
        _effectsToggle.SetPressedNoSignal(visible);
        UpdateEffectsLayout();
        if (!visible) _effectsToggle.GrabFocus();
    }

    private void UpdateEffectsLayout()
    {
        // In narrow windows the authored sidebar overlays the console instead of squeezing its controls.
        var docked = Size.X >= 1120;
        _mainMargin.OffsetRight = _effectsSidebar.Visible && docked ? -376 : 0;
        _effectsBackdrop.Visible = _effectsSidebar.Visible && !docked;
    }

    private void RenderEffectsSummary(ConsoleSnapshot state)
    {
        var active = (state.Equalizer.Enabled ? 1 : 0) + (state.Echo.Enabled ? 1 : 0);
        _effectsToggle.Text = active == 0 ? "Effetti" : $"Effetti • {active}";
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (_effectsSidebar.Visible && @event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
        {
            SetEffectsVisible(false);
            GetViewport().SetInputAsHandled();
        }
    }
}
