using System.Buffers.Binary;
using AudioConsole.Core;

internal static class LoopTests
{
    public static void Run(string output, Action<bool, string> check)
    {
        var input = new FakeInput();
        var route = new LoopRedirect();
        var sessionRoot = Path.Combine(output, "loops-" + Guid.NewGuid().ToString("N"));
        var exported = Path.Combine(output, "loop-clip.wav");
        using (var service = new AudioConsoleService(input, new FakePlayback(), sessionRoot, route))
        {
            void Reject(Action action, string message)
            {
                try { action(); check(false, message); }
                catch (InvalidOperationException) { }
            }
            service.SelectInput("fake");
            service.ConfigureRedirect(route.GetDevices()[0], AudioRedirectMode.LiveMix);
            Reject(() => service.StartLoop(), "Playing without a clip is rejected.");
            service.StartLoopRecording();
            check(service.StopLoopRecording() == null && service.GetSnapshot().Loops.Clips.Length == 0,
                "Stopping empty loop capture creates no entry.");
            var clipData = Stereo(4800, 0.25f, 0.125f);
            service.StartLoopRecording();
            input.Push(clipData);
            check(Math.Abs(service.GetSnapshot().Loops.RecordingElapsed.TotalSeconds - 0.1) < 1e-6,
                "Loop capture has its own sample-based duration.");
            Reject(() => service.SelectInput("other"), "Input cannot change during loop capture.");
            var first = service.StopLoopRecording()!;
            check(first.Duration.TotalSeconds == 0.1 && service.GetSnapshot().Recordings.Length == 0,
                "Loop clips and normal recording history remain independent.");
            var file = File.ReadAllBytes(service.SaveLoopWav(exported));
            check(BinaryPrimitives.ReadInt16LittleEndian(file.AsSpan(44)) == 8192 &&
                BinaryPrimitives.ReadInt16LittleEndian(file.AsSpan(46)) == 4096, "Loop WAV preserves separate stereo channels.");
            service.SetLoopSettings(new(50, 0.05));
            service.StartRedirect();
            service.StartLoop();
            service.StartRecording();
            var voice = Stereo(14400, 0.125f, 0.0625f);
            input.Push(voice);
            var mixed = route.Samples;
            check(Math.Abs(mixed[2500 * 2] - 0.25) < 0.0001 && Math.Abs(mixed[2500 * 2 + 1] - 0.125) < 0.0001,
                "Loop and live voice are summed with independent loop gain and stereo channels.");
            check(mixed[5000 * 2] == 0.125f && mixed[5000 * 2 + 1] == 0.0625f,
                "During the configured gap, live voice continues without loop audio.");
            check(Math.Abs(mixed[9700 * 2] - 0.25) < 0.0001,
                "A second loop starts at clip duration plus the configured pause.");
            service.StopRecording();
            var recorded = File.ReadAllBytes(service.SaveWav(Path.Combine(output, "voice-and-loop.wav")));
            check(recorded.Length == mixed.Length * 2 + 44 &&
                BinaryPrimitives.ReadInt16LittleEndian(recorded.AsSpan(44 + 2500 * 4)) == 8192,
                "A normal recording contains the same combined loop/live mix that is sent to the destination.");
            service.StopLoop();
            input.Push(Stereo(200, 0.125f, 0.0625f));
            check(route.Samples.All(value => value is 0.125f or 0.0625f) && route.IsActive && input.IsRunning,
                "Stopping the loop leaves live input and redirection running.");
            service.SetLoopSettings(new(100, 0));
            service.StartLoop();
            input.Push(Stereo(5000, 0, 0));
            check(!service.GetSnapshot().Loops.IsWaiting && service.GetSnapshot().Loops.Position.TotalSeconds is > 0 and < 0.01,
                "Zero pause wraps continuously without inserting a silent wait.");
            service.StartLoopRecording();
            check(!service.GetSnapshot().Loops.IsPlaying, "Starting a new clip stops the previous loop to avoid overdubbing it.");
            Reject(() => service.PlaySelected(), "Local preview cannot interrupt loop capture.");
            input.Push(Stereo(4800, 0.5f, 0.25f));
            var second = service.StopLoopRecording()!;
            var secondFile = File.ReadAllBytes(service.SaveLoopWav(Path.Combine(output, "loop-second.wav")));
            check(BinaryPrimitives.ReadInt16LittleEndian(secondFile.AsSpan(44)) == 16384,
                "New loop capture contains live sources only, not the old loop.");
            service.SetLoopSettings(new(25, 0.3));
            service.SelectLoop(first.Id);
            check(service.GetSnapshot().Loops.Settings == new LoopSettings(100, 0), "Each loop remembers its own settings.");
            service.SelectLoop(second.Id);
            check(service.GetSnapshot().Loops.Settings == new LoopSettings(25, 0.3), "Switching clips restores their volume and pause.");
            service.StartLoop();
            service.PlaySelected();
            check(!service.GetSnapshot().Loops.IsPlaying && !input.IsRunning, "Local playback stops loops and suspends the mixer.");
            service.StopPlayback();
            check(input.IsRunning && !service.GetSnapshot().Loops.IsPlaying, "Ending preview never restarts a loop automatically.");
            for (var i = 0; i < 10; i++)
            {
                service.StartLoopRecording(); input.Push(clipData); service.StopLoopRecording();
            }
            check(service.GetSnapshot().Loops.Clips.Length == 10 && service.GetSnapshot().Recordings.Length == 1 &&
                service.GetSnapshot().Loops.Clips.All(clip => clip.Id != first.Id) && File.Exists(exported),
                "Loop bank caps at ten, evicts the oldest, preserves main history and never deletes exported WAVs.");
            service.StartLoopRecording(); input.Push(clipData); input.Fail("Mic disconnected");
            check(!service.GetSnapshot().Loops.IsRecording && service.GetSnapshot().Loops.Error == "Mic disconnected" &&
                !service.GetSnapshot().InputAvailable, "Device failure archives the partial loop and stops capture with a visible error.");
            service.Update();
            check(!route.IsActive, "Device failure also stops live sending.");
            var settings = new LoopSettings(72, 2.5);
            service.SetLoopSettings(settings);
            var setupStore = new LastSetupStore(Path.Combine(output, "loop-setup.json"));
            setupStore.Save(LastSetupStore.Capture(service.GetSnapshot()));
            using var restored = new AudioConsoleService(new MultiSourceAudioInput(() => new FakeInput()), temporaryRoot: output);
            LastSetupStore.Restore(restored, setupStore.Load()!);
            check(restored.GetSnapshot().Loops.Settings == settings && restored.GetSnapshot().Loops.Clips.Length == 0 &&
                !restored.GetSnapshot().Loops.IsPlaying, "Loop defaults persist without session audio or automatic playback.");
            try { service.SetLoopSettings(new(100, double.NaN)); check(false, "Nonfinite loop pauses are rejected."); }
            catch (ArgumentOutOfRangeException) { }
        }
        check(Directory.GetFiles(sessionRoot, "*.wav", SearchOption.AllDirectories).Length == 0 && File.Exists(exported),
            "Disposal removes session loop files and preserves exported clips.");

        var variable = new VariableInput { Format = new(16000, 1) };
        var resampledRoute = new LoopRedirect();
        using var resampled = new AudioConsoleService(variable, temporaryRoot: output, redirect: resampledRoute);
        resampled.SelectInput("variable");
        resampled.StartLoopRecording();
        variable.Push(Enumerable.Repeat(0.25f, 1600).ToArray());
        resampled.StopLoopRecording();
        variable.Format = new(48000, 2);
        resampled.SelectInput("variable");
        resampled.ConfigureRedirect(resampledRoute.GetDevices()[0], AudioRedirectMode.LiveMix);
        resampled.StartRedirect();
        resampled.StartLoop();
        variable.Push(new float[4800 * 2]);
        check(resampled.GetSnapshot().Loops.Position == TimeSpan.Zero &&
            Math.Abs(resampledRoute.Samples[2000] - 0.25) < 0.005 && resampledRoute.Samples[2000] == resampledRoute.Samples[2001],
            "Mono clips resample to stereo without clock drift across loop boundaries.");
        variable.Push(new float[4800 * 100 * 2]);
        check(resampled.GetSnapshot().Loops.Position == TimeSpan.Zero, "Resampled loops retain exact timing after a hundred cycles.");
        resampled.SetLoopSettings(new(100, 0.2));
        resampled.StopLoop(); resampled.StartLoop();
        variable.Push(new float[4800 * 2]);
        check(resampled.GetSnapshot().Loops.IsWaiting && resampled.GetSnapshot().Loops.UntilNext == TimeSpan.FromSeconds(0.2),
            "Pause countdown starts exactly at the end of a resampled clip.");
        variable.Push(new float[9600 * 2]);
        check(!resampled.GetSnapshot().Loops.IsWaiting && resampled.GetSnapshot().Loops.Position == TimeSpan.Zero,
            "Pause consumes its exact frame count before restarting.");
    }

    private static float[] Stereo(int frames, float left, float right)
    {
        var data = new float[frames * 2];
        for (var i = 0; i < frames; i++) { data[i * 2] = left; data[i * 2 + 1] = right; }
        return data;
    }

    private sealed class LoopRedirect : IAudioRedirect
    {
        public bool IsActive { get; private set; }
        public string? Error => null;
        public TimeSpan Position => TimeSpan.Zero;
        public float[] Samples { get; private set; } = [];
        public IReadOnlyList<AudioOutputDevice> GetDevices() => [new("loop-test", "Loop test output", [])];
        public void StartLive(string id, AudioFormat format) => IsActive = true;
        public void WriteSamples(ReadOnlyMemory<float> samples) => Samples = samples.ToArray();
        public void PlayFile(string id, string path) => IsActive = true;
        public void Stop() => IsActive = false;
        public void Dispose() => Stop();
    }

    private sealed class VariableInput : IAudioInput
    {
        public AudioFormat Format { get; set; }
        public event Action<ReadOnlyMemory<float>>? SamplesAvailable;
        public event Action<string>? Failed { add { } remove { } }
        public IReadOnlyList<AudioInputDevice> GetDevices(AudioSourceKind source = AudioSourceKind.Microphone) => [new("variable", "Variable input")];
        public AudioFormat Start(string id) => Format;
        public void Push(float[] samples) => SamplesAvailable?.Invoke(samples);
        public void Stop() { }
        public void Dispose() { }
    }
}
