using System.Buffers.Binary;
using AudioConsole.Core;

internal static class DistortionTests
{
    public static void Run(string output, Action<bool, string> check)
    {
        var dsp = new VoiceDistortion();
        var settings = new DistortionSettings(true, DistortionMode.Soft, 12, 100, 0);
        dsp.Configure(settings);
        dsp.Reset(new(48000, 2));
        var expected = Math.Tanh(0.5 * Math.Pow(10, 12d / 20));
        check(Math.Abs(dsp.ProcessSample(0.5f, 0) - expected) < 1e-6 &&
            Math.Abs(dsp.ProcessSample(-0.5f, 1) + expected) < 1e-6, "Soft distortion uses symmetric saturation on both channels.");
        dsp.Configure(settings with { Mode = DistortionMode.Hard }); dsp.Reset(new(48000, 2));
        check(dsp.ProcessSample(0.5f, 0) == 1 && dsp.ProcessSample(-0.5f, 1) == -1,
            "Hard distortion clips driven peaks at +/-1 before output trim.");
        dsp.Configure(new(true, DistortionMode.Hard, 20, 100, 0)); dsp.Reset(new(48000, 1));
        check(Math.Abs(dsp.ProcessSample(0.01f, 0) - 0.1) < 1e-6, "Drive applies gain before clipping.");
        dsp.Configure(new(true, DistortionMode.Hard, 20, 50, -6)); dsp.Reset(new(48000, 1));
        var wetLevel = Math.Pow(10, -6d / 20);
        check(Math.Abs(dsp.ProcessSample(0.5f, 0) - (0.5 + 0.5 * (wetLevel - 0.5))) < 1e-6,
            "Mix blends the trimmed distorted signal with the untouched dry signal.");
        dsp.Configure(new(true, DistortionMode.Hard, 36, 0, -24)); dsp.Reset(new(48000, 1));
        check(dsp.ProcessSample(0.123f, 0) == 0.123f, "Zero mix returns the exact original despite Drive and Output settings.");
        dsp.Configure(new(false, DistortionMode.Hard, 36, 100, 6)); dsp.Reset(new(48000, 1));
        check(dsp.ProcessSample(0.123f, 0) == 0.123f && dsp.ProcessSample(float.NaN, 0) == 0,
            "Bypass preserves original samples and rejects nonfinite input.");
        dsp.Configure(new(true, DistortionMode.Hard, 36, 100, 0));
        var largestStep = 0f;
        var previous = 0.1f;
        for (var frame = 0; frame < 24000; frame++)
        {
            var value = dsp.ProcessSample(0.1f, 0);
            largestStep = Math.Max(largestStep, Math.Abs(value - previous)); previous = value;
        }
        check(largestStep < 0.005 && previous > 0.99, "Enabling distortion and moving Drive and Mix transitions smoothly.");
        dsp.Configure(new(false));
        for (var frame = 0; frame < 24000; frame++) previous = dsp.ProcessSample(0.1f, 0);
        check(previous == 0.1f, "Settled bypass returns bit-exact dry samples.");
        foreach (var invalid in new[] { new DistortionSettings(Mode: (DistortionMode)99), new DistortionSettings(DriveDb: double.NaN),
            new DistortionSettings(DriveDb: 37), new DistortionSettings(MixPercent: -1), new DistortionSettings(OutputDb: 7) })
        {
            try { dsp.Configure(invalid); check(false, "Invalid distortion settings are rejected."); }
            catch (ArgumentOutOfRangeException) { }
        }
        foreach (var rate in new[] { 16000, 44100, 96000 })
        {
            dsp.Configure(settings); dsp.Reset(new(rate, 1));
            check(float.IsFinite(dsp.ProcessSample(float.MaxValue, 0)) && dsp.ProcessSample(0, 0) == 0,
                "Distortion remains finite and silent on zero input across sample rates.");
        }

        var input = new FakeInput();
        var route = new DistortionRedirect();
        using var service = new AudioConsoleService(input, new FakePlayback(), output, route);
        settings = new(true, DistortionMode.Hard, 20, 100, -6);
        service.SetDistortion(settings);
        service.SelectInput("fake");
        service.ConfigureRedirect(route.GetDevices()[0], AudioRedirectMode.LiveMix);
        service.StartRedirect();
        var samples = Enumerable.Range(0, 4800 * 2).Select(i => i % 2 == 0 ? 0.2f : -0.2f).ToArray();
        input.Push(samples);
        check(Math.Abs(route.Samples[0] - wetLevel) < 1e-6 && service.GetSnapshot().Recordings.Length == 0,
            "Distortion reaches live output without recording.");
        service.StartRecording(); service.StartLoopRecording(); input.Push(samples);
        service.StopRecording(); service.StopLoopRecording();
        var wav = File.ReadAllBytes(service.SaveWav(Path.Combine(output, "distorted.wav")));
        var loopWav = File.ReadAllBytes(service.SaveLoopWav(Path.Combine(output, "distorted-loop.wav")));
        var pcmLevel = (short)Math.Round(wetLevel * 32768);
        check(BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44)) == pcmLevel &&
            BinaryPrimitives.ReadInt16LittleEndian(loopWav.AsSpan(44)) == pcmLevel,
            "Main recording and new loop clips both capture the distorted live signal.");
        service.SetDistortion(new());
        check(File.ReadAllBytes(service.SaveWav(Path.Combine(output, "distorted-unchanged.wav"))).SequenceEqual(wav),
            "Changing distortion leaves existing recordings untouched.");
        service.SetDistortion(new(true, DistortionMode.Hard, 36, 100, -24));
        service.StartLoop(); input.Push(new float[4800 * 2]);
        check(Math.Abs(route.Samples[2500 * 2] - pcmLevel / 32768f) < 0.0001,
            "Existing loop clips play after the effect chain without repeated distortion.");
        service.StopLoop();
        service.SetDistortion(settings);
        service.SetEcho(new(true, 40, 0, 50));
        service.SelectInput("fake"); input.Push(samples);
        check(Math.Abs(route.Samples[3000 * 2] - wetLevel * 1.5) < 1e-5,
            "Echo receives distorted voice, preserving the intended EQ -> Distortion -> Echo order.");
        var store = new LastSetupStore(Path.Combine(output, "distortion-setup.json"));
        store.Save(LastSetupStore.Capture(service.GetSnapshot()));
        using var restored = new AudioConsoleService(new MultiSourceAudioInput(() => new FakeInput()), temporaryRoot: output, redirect: new DistortionRedirect());
        LastSetupStore.Restore(restored, store.Load()!);
        check(restored.GetSnapshot().Distortion == settings && !restored.GetSnapshot().Redirect.IsActive,
            "Setup restores all distortion parameters without automatically sending audio.");
        LastSetupStore.Restore(restored, new(1, 100, []));
        check(restored.GetSnapshot().Distortion == new DistortionSettings(), "Old setups default to distortion bypass.");
    }

    private sealed class DistortionRedirect : IAudioRedirect
    {
        public bool IsActive { get; private set; }
        public string? Error => null;
        public TimeSpan Position => TimeSpan.Zero;
        public float[] Samples { get; private set; } = [];
        public IReadOnlyList<AudioOutputDevice> GetDevices() => [new("dist-test", "Distortion test", [])];
        public void StartLive(string id, AudioFormat format) => IsActive = true;
        public void WriteSamples(ReadOnlyMemory<float> samples) => Samples = samples.ToArray();
        public void PlayFile(string id, string path) => IsActive = true;
        public void Stop() => IsActive = false;
        public void Dispose() => Stop();
    }
}
