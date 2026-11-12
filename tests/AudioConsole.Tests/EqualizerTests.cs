using System.Buffers.Binary;
using AudioConsole.Core;

internal static class EqualizerTests
{
    public static void Run(string output, Action<bool, string> check)
    {
        double Response(EqualizerSettings settings, double frequency, int rate = 48000)
        {
            var eq = new VoiceEqualizer();
            eq.Configure(settings);
            eq.Reset(new(rate, 2));
            double dry = 0, wet = 0;
            for (var frame = 0; frame < rate; frame++)
            {
                var sample = (float)(0.1 * Math.Sin(2 * Math.PI * frequency * frame / rate));
                var result = eq.ProcessSample(sample, 0);
                var right = eq.ProcessSample(0, 1);
                if (frame == rate - 1) check(right == 0, "EQ channel histories must stay independent.");
                if (frame < rate / 2) continue;
                dry += sample * sample;
                wet += result * result;
            }
            return 10 * Math.Log10(wet / dry);
        }

        check(Math.Abs(Response(new(true), 1000)) < 0.001, "Flat enabled EQ preserves amplitude.");
        check(Math.Abs(Response(new(false, 12, -12, 12, 12), 1000)) < 0.001, "Bypass excludes every band and EQ output gain.");
        check(Math.Abs(Response(new(true, BassDb: 6), 30) - 6) < 0.1, "Bass shelf boosts low frequencies by the requested dB.");
        check(Math.Abs(Response(new(true, BassDb: -6), 30) + 6) < 0.1, "Bass shelf cuts low frequencies.");
        check(Math.Abs(Response(new(true, BassDb: 12), 10000)) < 0.1, "Bass adjustment preserves the high band.");
        check(Math.Abs(Response(new(true, MidDb: 6), 1000) - 6) < 0.1, "Mid peak reaches the requested gain at 1 kHz.");
        check(Math.Abs(Response(new(true, MidDb: -6), 1000) + 6) < 0.1, "Mid peak supports cuts.");
        check(Math.Abs(Response(new(true, MidDb: 12), 30)) < 0.1, "Mid adjustment preserves deep bass.");
        check(Math.Abs(Response(new(true, TrebleDb: 6), 10000) - 6) < 0.2, "Treble shelf boosts high frequencies.");
        check(Math.Abs(Response(new(true, TrebleDb: -6), 10000) + 6) < 0.2, "Treble shelf supports cuts.");
        check(Math.Abs(Response(new(true, TrebleDb: 12), 30)) < 0.1, "Treble adjustment preserves bass.");
        check(Math.Abs(Response(new(true, OutputDb: -6), 1000) + 6) < 0.001, "EQ output trim applies dB gain.");
        foreach (var rate in new[] { 16000, 44100, 96000 })
            check(double.IsFinite(Response(new(true, 12, -12, 12), 1000, rate)), "EQ supports different capture sample rates.");

        var dynamicEq = new VoiceEqualizer();
        dynamicEq.Reset(new(48000, 1));
        dynamicEq.Configure(new(true, OutputDb: 12));
        var previous = 0.1f;
        var largestStep = 0f;
        for (var frame = 0; frame < 24000; frame++)
        {
            var value = dynamicEq.ProcessSample(0.1f, 0);
            largestStep = Math.Max(largestStep, Math.Abs(value - previous));
            previous = value;
        }
        check(largestStep < 0.002 && previous > 0.39, "Enable and output gain changes transition smoothly.");
        dynamicEq.Configure(new(false, 12, 12, 12, 12));
        for (var frame = 0; frame < 24000; frame++) previous = dynamicEq.ProcessSample(0.1f, 0);
        check(previous == 0.1f && dynamicEq.ProcessSample(float.NaN, 0) == 0, "Settled bypass restores exact finite input samples.");
        try { dynamicEq.Configure(new(true, BassDb: double.NaN)); check(false, "Nonfinite EQ values must be rejected."); }
        catch (ArgumentOutOfRangeException) { }
        try { dynamicEq.Configure(new(true, OutputDb: 13)); check(false, "Out-of-range EQ values must be rejected."); }
        catch (ArgumentOutOfRangeException) { }
        dynamicEq.Reset(new(48000, 1));
        var stable = true;
        for (var frame = 0; frame < 48000; frame++)
        {
            if (frame % 64 == 0) dynamicEq.Configure(frame % 128 == 0 ? new(true, 12, -12, 12, 12) : new(true, -12, 12, -12, -12));
            var value = dynamicEq.ProcessSample((float)(0.1 * Math.Sin(frame * 0.1)), 0);
            stable &= float.IsFinite(value) && Math.Abs(value) < 20;
        }
        check(stable, "Rapid changes to every EQ band must remain stable and finite.");

        var input = new FakeInput();
        var route = new TestRedirect();
        using var service = new AudioConsoleService(input, new FakePlayback(), output, route);
        var settings = new EqualizerSettings(true, 4, -3, 2, -6);
        service.SetEqualizer(settings);
        service.SelectInput("fake");
        service.ConfigureRedirect(route.GetDevices()[0], AudioRedirectMode.LiveMix);
        service.StartRedirect();
        var tone = new float[48000 * 2];
        for (var frame = 0; frame < 48000; frame++) tone[frame * 2] = tone[frame * 2 + 1] = (float)(0.1 * Math.Sin(2 * Math.PI * 1000 * frame / 48000));
        input.Push(tone);
        check(route.Samples.Length == tone.Length && service.GetSnapshot().Recordings.Length == 0 && !service.GetSnapshot().IsRecording,
            "Equalized audio routes live without recording or temporary takes.");
        service.StartRecording();
        input.Push(tone);
        service.StopRecording();
        var wav = File.ReadAllBytes(service.SaveWav(Path.Combine(output, "equalized.wav")));
        var matches = true;
        for (var i = 0; i < route.Samples.Length; i++)
            matches &= BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + i * 2)) ==
                (short)Math.Clamp((int)Math.Round(route.Samples[i] * 32768), short.MinValue, short.MaxValue);
        check(matches, "Recording and live redirect receive the same equalized signal.");
        service.SetEqualizer(new());
        check(File.ReadAllBytes(service.SaveWav(Path.Combine(output, "equalized-unchanged.wav"))).SequenceEqual(wav),
            "Changing EQ leaves existing recordings untouched.");
        service.SetEqualizer(settings);
        var store = new LastSetupStore(Path.Combine(output, "equalizer-setup.json"));
        store.Save(LastSetupStore.Capture(service.GetSnapshot()));
        using var restored = new AudioConsoleService(new MultiSourceAudioInput(() => new FakeInput()), temporaryRoot: output, redirect: new TestRedirect());
        LastSetupStore.Restore(restored, store.Load()!);
        check(restored.GetSnapshot().Equalizer == settings && !restored.GetSnapshot().Redirect.IsActive, "Setup restores all EQ values without starting audio send.");
        LastSetupStore.Restore(restored, new(1, 100, []));
        check(restored.GetSnapshot().Equalizer == new EqualizerSettings(), "Old setups without EQ restore bypass and flat controls.");
        service.StopRedirect();
        service.SetEqualizer(new(true, OutputDb: -12));
        service.SelectInput("fake"); // Reopening initializes the filter at the new settings.
        service.StartRedirect();
        input.Push(new float[] { 2, 2 });
        check(Math.Abs(route.Samples[0] - 2 * Math.Pow(10, -12d / 20)) < 0.0001,
            "EQ can attenuate mix headroom before final clipping.");
        service.StopRedirect();
        service.SetEqualizer(new(true, OutputDb: 12));
        service.SelectInput("fake");
        service.StartRedirect();
        input.Push(new float[] { 0.75f, -0.75f });
        check(route.Samples.SequenceEqual(new float[] { 1, -1 }) && service.GetSnapshot().Signal == InputSignal.Clipping,
            "Boosted EQ output is clipped safely and reported by the final peak indicator.");
        Console.WriteLine("Three-band EQ response, transitions and live routing: PASS");
    }

    private sealed class TestRedirect : IAudioRedirect
    {
        public bool IsActive { get; private set; }
        public string? Error => null;
        public TimeSpan Position => TimeSpan.Zero;
        public float[] Samples { get; private set; } = [];
        public IReadOnlyList<AudioOutputDevice> GetDevices() => [new("eq-output", "EQ test output", [])];
        public void StartLive(string id, AudioFormat format) => IsActive = true;
        public void WriteSamples(ReadOnlyMemory<float> samples) => Samples = samples.ToArray();
        public void PlayFile(string id, string path) => IsActive = true;
        public void Stop() => IsActive = false;
        public void Dispose() => Stop();
    }
}
