using AudioConsole.Core;

internal static class RedirectTests
{
    public static void Run(string output, Action<bool, string> check)
    {
        var input = new FakeInput();
        var playback = new FakePlayback();
        var redirect = new FakeRedirect();
        using var service = new AudioConsoleService(input, playback, output, redirect);
        service.SelectInput("fake");
        var destination = redirect.GetDevices()[0];
        service.ConfigureRedirect(destination, AudioRedirectMode.LiveMix);
        check(!redirect.IsActive, "Choosing an output must not send audio.");
        service.SetVolume(50);
        service.StartRedirect();
        service.StartRecording();
        input.Push(new float[] { 0.6f, -0.4f, float.NaN, 4 });
        check(redirect.Samples.SequenceEqual(new float[] { 0.3f, -0.2f, 0, 1 }), "Redirected mix follows master gain, finite filtering and clipping.");
        service.StopRecording();
        check(service.GetSnapshot().Redirect.IsActive && service.GetSnapshot().HasRecording, "Live routing and recording can run together.");
        service.StopRedirect();
        check(input.IsRunning && !redirect.IsActive, "Stopping live send preserves input monitoring.");
        service.ConfigureRedirect(new("feedback", "Feedback", []), AudioRedirectMode.LiveMix);
        try { service.StartRedirect(); check(false, "Direct feedback must be rejected."); }
        catch (InvalidOperationException) { check(!redirect.IsActive && input.IsRunning, "Feedback rejection leaves capture intact."); }
        service.ConfigureRedirect(destination, AudioRedirectMode.SelectedRecording);
        service.StartRedirect();
        check(!input.IsRunning && !playback.IsPlaying && redirect.FilePath != null && service.GetSnapshot().IsInputSuspended,
            "A selected take routes to the chosen endpoint and suspends capture.");
        redirect.Position = TimeSpan.FromTicks(100);
        check(service.GetSnapshot().Editor!.Position == redirect.Position, "Timeline follows redirected file playback.");
        service.StopPlayback();
        check(!input.IsRunning, "Local STOP cannot reopen capture during file routing.");
        redirect.IsActive = false;
        service.Update();
        check(input.IsRunning && !service.GetSnapshot().Redirect.IsActive, "File completion restores capture.");
        service.StartRedirect();
        service.PlaySelected();
        check(!redirect.IsActive && playback.IsPlaying && !input.IsRunning, "Local preview stops file send and retains capture suspension.");
        service.StopPlayback();
        service.StartRedirect();
        redirect.Error = "Unplugged";
        service.Update();
        check(input.IsRunning && service.GetSnapshot().Redirect.Error == "Unplugged", "Output failure releases capture and remains visible.");
        service.ConfigureRedirect(destination, AudioRedirectMode.LiveMix);
        service.StartRedirect();
        redirect.ThrowWrite = true;
        service.StartRecording();
        input.Push(new float[] { 0.1f, 0.1f });
        service.Update();
        check(!redirect.IsActive && service.GetSnapshot().IsRecording, "An output write failure must not stop a recording.");
        service.StopRecording();
        var setup = LastSetupStore.Capture(service.GetSnapshot());
        var store = new LastSetupStore(Path.Combine(output, "redirect-setup.json"));
        store.Save(setup);
        var loaded = store.Load()!;
        check(loaded.Redirect?.Destination?.Id == destination.Id && !File.ReadAllText(store.FilePath).Contains("isActive"),
            "Routing settings are saved without active sending state.");
        using var restored = new AudioConsoleService(new MultiSourceAudioInput(() => new FakeInput()), temporaryRoot: output, redirect: new FakeRedirect());
        LastSetupStore.Restore(restored, loaded);
        check(restored.GetSnapshot().Redirect.Destination?.Id == destination.Id && !restored.GetSnapshot().Redirect.IsActive,
            "Restoring routing never automatically sends audio.");
        using var unavailable = new AudioConsoleService(new MultiSourceAudioInput(() => new FakeInput()), temporaryRoot: output);
        check(LastSetupStore.Restore(unavailable, loaded).Warnings.Length == 1 && unavailable.GetSnapshot().Redirect.Destination == null,
            "An unavailable route is reported without choosing another output.");

        using var mixer = new AudioConsoleService(new MultiSourceAudioInput(() => new FakeInput()), temporaryRoot: output, redirect: new FakeRedirect());
        mixer.ConfigureSources([new(new("fake", "Mic"))]);
        mixer.ConfigureRedirect(destination, AudioRedirectMode.LiveMix);
        mixer.StartRedirect();
        try
        {
            mixer.ConfigureSources([new(new("fake", "Mic")), new(new("loopback:out", "Output", AudioSourceKind.ComputerOutput))]);
            check(false, "Adding the destination loopback to an active send must fail.");
        }
        catch (InvalidOperationException) { check(mixer.GetSnapshot().Sources.Length == 1, "Feedback validation precedes source reconfiguration."); }
        mixer.ConfigureSources([]);
        check(!mixer.GetSnapshot().Redirect.IsActive, "Removing all sources stops sending.");
    }

    private sealed class FakeRedirect : IAudioRedirect
    {
        public bool IsActive { get; set; }
        public string? Error { get; set; }
        public TimeSpan Position { get; set; }
        public string? FilePath { get; private set; }
        public bool ThrowWrite { get; set; }
        public float[] Samples { get; private set; } = [];
        public IReadOnlyList<AudioOutputDevice> GetDevices() => [new("out", "Cable", ["loopback:out"]), new("feedback", "Feedback", ["fake"])];
        public void StartLive(string deviceId, AudioFormat format) { IsActive = true; Error = null; }
        public void WriteSamples(ReadOnlyMemory<float> samples)
        {
            if (ThrowWrite) throw new IOException("Write failed");
            Samples = samples.ToArray();
        }
        public void PlayFile(string deviceId, string path) { FilePath = path; IsActive = File.Exists(path); Error = null; }
        public void Stop() => IsActive = false;
        public void Dispose() => Stop();
    }
}
