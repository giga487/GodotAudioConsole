using System.Buffers.Binary;
using AudioConsole.Core;

internal static class EchoTests
{
    public static void Run(string output, Action<bool, string> check)
    {
        var echo = new VoiceEcho();
        echo.Configure(new(true, 100, 50, 40));
        echo.Reset(new(48000, 2));
        var left = new float[48000];
        var right = new float[48000];
        for (var frame = 0; frame < left.Length; frame++)
        {
            left[frame] = echo.ProcessSample(frame == 0 ? 1 : 0, 0);
            right[frame] = echo.ProcessSample(frame == 7 ? 0.25f : 0, 1);
        }
        check(left[0] == 1 && left.Skip(1).Take(4799).All(value => value == 0), "Echo preserves dry input and waits for the configured delay.");
        for (var repeat = 1; repeat < 10; repeat++)
            check(Math.Abs(left[repeat * 4800] - 0.4 * Math.Pow(0.5, repeat - 1)) < 1e-6,
                "Echo repeats at exact frame intervals with exponential feedback decay.");
        check(right[0] == 0 && left[4807] == 0 && Math.Abs(right[4807] - 0.1) < 1e-6,
            "Echo channels have independent delay histories.");
        echo.Configure(new(true, 40, 0, 100));
        echo.Reset(new(1000, 1));
        var single = Enumerable.Range(0, 200).Select(frame => echo.ProcessSample(frame == 0 ? 1 : 0, 0)).ToArray();
        check(single[40] == 1 && single[80] == 0 && single.Count(value => value != 0) == 2, "Zero feedback produces exactly one repeat.");
        echo.Configure(new(true, 1000, 0, 100));
        echo.Reset(new(1000, 1));
        var maximumDelay = Enumerable.Range(0, 2002).Select(frame => echo.ProcessSample(frame == 0 ? 1 : 0, 0)).ToArray();
        check(maximumDelay[1000] == 1 && maximumDelay.Count(value => value != 0) == 2, "Maximum delay survives ring-buffer wrap without timing errors.");
        echo.Configure(new(true, 40, 85, 0));
        echo.Reset(new(1000, 1));
        check(Enumerable.Range(0, 500).All(_ => echo.ProcessSample(0.125f, 0) == 0.125f), "Zero amount preserves dry amplitude even with feedback enabled.");
        echo.Configure(new(false, 100, 85, 100));
        echo.Reset(new(1000, 1));
        check(echo.ProcessSample(0.123f, 0) == 0.123f && echo.ProcessSample(float.NaN, 0) == 0,
            "Bypass returns exact finite dry samples.");
        echo.Configure(new(true, 40, 85, 100));
        echo.Reset(new(1000, 1));
        var bounded = true;
        for (var frame = 0; frame < 3000; frame++)
        {
            var sample = echo.ProcessSample(1, 0);
            bounded &= float.IsFinite(sample) && sample <= 7.667;
        }
        check(bounded, "Maximum permitted feedback stays bounded under continuous input.");
        echo.Configure(new(false, 40, 85, 100));
        for (var frame = 0; frame < 500; frame++) echo.ProcessSample(0, 0);
        echo.Configure(new(true, 40, 85, 100));
        var cleared = true;
        for (var frame = 0; frame < 500; frame++) cleared &= echo.ProcessSample(0, 0) == 0;
        check(cleared, "Full bypass clears the tail before re-enabling.");
        echo.ProcessSample(1, 0);
        echo.Reset(new(1000, 1));
        check(Enumerable.Range(0, 500).All(_ => echo.ProcessSample(0, 0) == 0), "Reset clears the complete echo buffer.");
        var stable = true;
        var maximumStep = 0f;
        var previous = 0f;
        for (var frame = 0; frame < 5000; frame++)
        {
            if (frame % 10 == 0) echo.Configure(new(frame % 30 != 0, frame % 20 == 0 ? 40 : 1000, frame % 20 == 0 ? 85 : 0, 100));
            var sample = echo.ProcessSample(0.1f, 0);
            stable &= float.IsFinite(sample) && Math.Abs(sample) < 1;
            if (frame > 0) maximumStep = Math.Max(maximumStep, Math.Abs(sample - previous));
            previous = sample;
        }
        check(stable && maximumStep < 0.1, "Rapid bypass, delay and feedback changes remain bounded with smooth transitions.");
        foreach (var invalid in new[] { new EchoSettings(DelayMs: 39), new EchoSettings(DelayMs: 1001),
            new EchoSettings(FeedbackPercent: 86), new EchoSettings(FeedbackPercent: double.NaN), new EchoSettings(AmountPercent: -1) })
        {
            try { echo.Configure(invalid); check(false, "Invalid echo settings must be rejected."); }
            catch (ArgumentOutOfRangeException) { }
        }
        foreach (var rate in new[] { 16000, 44100, 96000 })
        {
            echo.Configure(new(true, 100, 0, 50));
            echo.Reset(new(rate, 1));
            var hit = 0f;
            for (var frame = 0; frame <= rate / 10; frame++) hit = echo.ProcessSample(frame == 0 ? 1 : 0, 0);
            check(hit == 0.5f, "Echo timing respects the actual sample rate.");
        }

        var input = new FakeInput();
        var route = new EchoRedirect();
        using var service = new AudioConsoleService(input, new FakePlayback(), output, route);
        var settings = new EchoSettings(true, 100, 50, 40);
        service.SetEcho(settings);
        service.SelectInput("fake");
        service.ConfigureRedirect(route.GetDevices()[0], AudioRedirectMode.LiveMix);
        service.StartRedirect();
        var impulse = new float[48000 * 2];
        impulse[0] = 0.5f;
        input.Push(impulse);
        check(Math.Abs(route.Samples[9600] - 0.2) < 1e-6 && service.GetSnapshot().Recordings.Length == 0,
            "Echo reaches live redirect without recording or creating temporary takes.");
        service.StartRecording();
        input.Push(impulse);
        service.StopRecording();
        var wav = File.ReadAllBytes(service.SaveWav(Path.Combine(output, "echo.wav")));
        var same = true;
        for (var i = 0; i < route.Samples.Length; i++)
            same &= BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + i * 2)) ==
                (short)Math.Clamp((int)Math.Round(route.Samples[i] * 32768), short.MinValue, short.MaxValue);
        check(same, "Recorded PCM and live routing receive the identical echo signal.");
        service.PlaySelected();
        service.StopPlayback();
        service.StartRedirect();
        input.Push(new float[48000 * 2]);
        check(route.Samples.All(value => value == 0), "Reopening capture after playback clears the previous live tail.");
        service.SetEcho(new());
        check(File.ReadAllBytes(service.SaveWav(Path.Combine(output, "echo-original.wav"))).SequenceEqual(wav),
            "Existing recordings remain unchanged after changing echo settings.");
        service.SetEcho(settings);
        var store = new LastSetupStore(Path.Combine(output, "echo-setup.json"));
        store.Save(LastSetupStore.Capture(service.GetSnapshot()));
        using var restored = new AudioConsoleService(new MultiSourceAudioInput(() => new FakeInput()), temporaryRoot: output, redirect: new EchoRedirect());
        LastSetupStore.Restore(restored, store.Load()!);
        check(restored.GetSnapshot().Echo == settings && !restored.GetSnapshot().Redirect.IsActive,
            "Setup restores echo settings without automatically sending audio.");
        LastSetupStore.Restore(restored, new(1, 100, []));
        check(restored.GetSnapshot().Echo == new EchoSettings(), "Old setup files default to echo bypass.");
        service.StopRedirect();
        service.SetEqualizer(new(true, OutputDb: -6));
        service.SelectInput("fake");
        service.StartRedirect();
        input.Push(impulse);
        var attenuation = Math.Pow(10, -6d / 20);
        check(Math.Abs(route.Samples[0] - 0.5 * attenuation) < 1e-6 && Math.Abs(route.Samples[9600] - 0.2 * attenuation) < 1e-6,
            "Echo and equalizer run together, with EQ applied to both voice and repetitions.");
    }

    private sealed class EchoRedirect : IAudioRedirect
    {
        public bool IsActive { get; private set; }
        public string? Error => null;
        public TimeSpan Position => TimeSpan.Zero;
        public float[] Samples { get; private set; } = [];
        public IReadOnlyList<AudioOutputDevice> GetDevices() => [new("echo-output", "Echo test", [])];
        public void StartLive(string id, AudioFormat format) => IsActive = true;
        public void WriteSamples(ReadOnlyMemory<float> samples) => Samples = samples.ToArray();
        public void PlayFile(string id, string path) => IsActive = true;
        public void Stop() => IsActive = false;
        public void Dispose() => Stop();
    }
}
