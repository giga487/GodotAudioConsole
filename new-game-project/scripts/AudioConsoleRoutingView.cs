using AudioConsole.Core;
using Godot;

public partial class AudioConsoleView
{
    private OptionButton _redirectDevices = null!, _redirectModeSelect = null!;
    private Button _redirectStart = null!, _redirectStop = null!, _redirectRefresh = null!;
    private Label _redirectStatus = null!;
    private IReadOnlyList<AudioOutputDevice> _outputs = [];

    private void BindRedirectControls()
    {
        _redirectDevices = GetNode<OptionButton>("%RedirectDevice");
        _redirectModeSelect = GetNode<OptionButton>("%RedirectMode");
        _redirectStart = GetNode<Button>("%RedirectStart");
        _redirectStop = GetNode<Button>("%RedirectStop");
        _redirectRefresh = GetNode<Button>("%RedirectRefresh");
        _redirectStatus = GetNode<Label>("%RedirectStatus");
        _redirectDevices.ItemSelected += _ => ConfigureRedirect();
        _redirectModeSelect.ItemSelected += _ => ConfigureRedirect();
        _redirectRefresh.Pressed += RefreshRedirectDevices;
        _redirectStart.Pressed += () =>
        {
            try { _service?.StartRedirect(); }
            catch (Exception error) { _status.Text = "Invio non avviato: " + error.Message; }
        };
        _redirectStop.Pressed += () => _service?.StopRedirect();
        GetNode<LinkButton>("%VirtualCableHelp").Pressed += () => OS.ShellOpen("https://vb-audio.com/Cable/");
    }

    private void RefreshRedirectDevices()
    {
        if (_service == null) return;
        try
        {
            _outputs = _service.GetRedirectDevices();
            _redirectDevices.Clear();
            _redirectDevices.AddItem("Seleziona uscita virtuale…");
            foreach (var output in _outputs) _redirectDevices.AddItem(output.Name);
            var state = _service.GetSnapshot().Redirect;
            var index = _outputs.ToList().FindIndex(d => d.Id == state.Destination?.Id);
            _redirectDevices.Select(index + 1);
            _redirectModeSelect.Select((int)state.Mode);
        }
        catch (Exception error) { _status.Text = "Uscite non disponibili: " + error.Message; }
    }

    private void ConfigureRedirect()
    {
        if (_service == null) return;
        try
        {
            var index = _redirectDevices.Selected - 1;
            _service.ConfigureRedirect(index >= 0 && index < _outputs.Count ? _outputs[index] : null,
                (AudioRedirectMode)_redirectModeSelect.Selected);
            ScheduleSetupSave();
        }
        catch (Exception error) { _status.Text = error.Message; RefreshRedirectDevices(); }
    }

    private void RenderRedirect(ConsoleSnapshot state)
    {
        var redirect = state.Redirect;
        _redirectDevices.Disabled = _redirectModeSelect.Disabled = _redirectRefresh.Disabled = redirect.IsActive;
        _redirectStart.Disabled = redirect.IsActive || redirect.Destination == null ||
            (redirect.Mode == AudioRedirectMode.LiveMix ? !state.InputAvailable : state.IsRecording || !state.HasRecording);
        _redirectStop.Disabled = !redirect.IsActive;
        _redirectStatus.Text = redirect.Error != null ? "Invio interrotto: " + redirect.Error
            : redirect.IsActive ? $"IN INVIO • {(redirect.Mode == AudioRedirectMode.LiveMix ? "MIX LIVE" : "REGISTRAZIONE")} → {redirect.Destination?.Name}"
            : redirect.Destination == null ? "Configura un cavo virtuale e scegli la sua uscita qui sopra."
            : $"Fermo • {redirect.Destination.Name}";
    }
}
