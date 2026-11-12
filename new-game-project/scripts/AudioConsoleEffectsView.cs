using AudioConsole.Core;
using Godot;

public partial class AudioConsoleView
{
    private Control _effectsSidebar = null!, _effectsBackdrop = null!, _mainMargin = null!;
    private Button _effectsToggle = null!;
    private Control _loopsSidebar = null!;
    private Button _loopsToggle = null!;

    private void BindEffectsSidebar()
    {
        _effectsSidebar = GetNode<Control>("%EffectsSidebar");
        _effectsBackdrop = GetNode<Control>("%EffectsBackdrop");
        _mainMargin = GetNode<Control>("Margin");
        _effectsToggle = GetNode<Button>("%EffectsToggle");
        _loopsSidebar = GetNode<Control>("%LoopsSidebar");
        _loopsToggle = GetNode<Button>("%LoopsToggle");
        _loopsToggle.Toggled += SetLoopsVisible;
        GetNode<Button>("%CloseLoops").Pressed += () => SetLoopsVisible(false);
        _effectsToggle.Toggled += SetEffectsVisible;
        GetNode<Button>("%CloseEffects").Pressed += () => SetEffectsVisible(false);
        _effectsBackdrop.GuiInput += input =>
        {
            if (input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            {
                SetEffectsVisible(false);
                SetLoopsVisible(false);
                _effectsBackdrop.AcceptEvent();
            }
        };
        Resized += UpdateEffectsLayout;
        UpdateEffectsLayout();
    }

    private void SetEffectsVisible(bool visible)
    {
        if (visible && Size.X < 1464) { _loopsSidebar.Visible = false; _loopsToggle.SetPressedNoSignal(false); }
        _effectsSidebar.Visible = visible;
        _effectsToggle.SetPressedNoSignal(visible);
        UpdateEffectsLayout();
        if (!visible) _effectsToggle.GrabFocus();
    }

    private void SetLoopsVisible(bool visible)
    {
        if (visible && Size.X < 1464) { _effectsSidebar.Visible = false; _effectsToggle.SetPressedNoSignal(false); }
        _loopsSidebar.Visible = visible;
        _loopsToggle.SetPressedNoSignal(visible);
        UpdateEffectsLayout();
        if (!visible) _loopsToggle.GrabFocus();
    }

    private void UpdateEffectsLayout()
    {
        if (Size.X < 1464 && _effectsSidebar.Visible && _loopsSidebar.Visible)
        {
            _loopsSidebar.Visible = false;
            _loopsToggle.SetPressedNoSignal(false);
        }
        var loopWidth = _loopsSidebar.Visible ? 320 : 0;
        var effectsWidth = _effectsSidebar.Visible ? 360 : 0;
        var between = loopWidth > 0 && effectsWidth > 0 ? 16 : 0;
        _effectsSidebar.OffsetLeft = -384 - (_loopsSidebar.Visible ? 336 : 0);
        _effectsSidebar.OffsetRight = -24 - (_loopsSidebar.Visible ? 336 : 0);
        var docked = Size.X >= 1120;
        var anyPanel = loopWidth + effectsWidth > 0;
        _mainMargin.OffsetRight = anyPanel && docked ? -(loopWidth + effectsWidth + between + 48) : 0;
        _effectsBackdrop.Visible = anyPanel && !docked;
    }

    private void RenderEffectsSummary(ConsoleSnapshot state)
    {
        var active = (state.Equalizer.Enabled ? 1 : 0) + (state.Echo.Enabled ? 1 : 0);
        _effectsToggle.Text = active == 0 ? "Effetti" : $"Effetti • {active}";
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if ((_effectsSidebar.Visible || _loopsSidebar.Visible) && @event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
        {
            SetEffectsVisible(false);
            SetLoopsVisible(false);
            GetViewport().SetInputAsHandled();
        }
    }
}
