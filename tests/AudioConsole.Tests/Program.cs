using System.Buffers.Binary;
using AudioConsole.Core;
using AudioConsole.Windows;

var failures = 0;
void Check(bool condition, string message)
{
    if (condition) return;
    failures++;
    Console.Error.WriteLine("FAIL: " + message);
}
void Throws<T>(Action action, string message) where T : Exception
{
    try { action(); Check(false, message); }
    catch (T) { }
}

var output = Path.GetFullPath(Path.Combine(".artifacts", "tests"));
Directory.CreateDirectory(output);
if (args.Contains("--playback-hardware"))
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("WASAPI requires Windows.");
    using var playback = new WasapiAudioPlayback();
    playback.Play(Path.Combine(output, "silent.wav"));
    Check(playback.IsPlaying, "WASAPI playback must start.");
    await Task.Delay(1800);
    Check(playback.Error == null, "WASAPI playback must not fail: " + playback.Error);
    Check(!playback.IsPlaying, "Playback must finish automatically at end of file.");
    playback.Stop();
}
else if (args.Contains("--hardware"))
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("WASAPI requires Windows.");
    using var service = new AudioConsoleService(new WasapiAudioInput());
    var devices = service.GetDevices();
    Check(devices.Count > 0, "At least one microphone must be available for hardware smoke test.");
    if (devices.Count > 0)
    {
        Console.WriteLine($"WASAPI inputs: {devices.Count}");
        service.SelectInput(devices[0].Id);
        Check(service.StartRecording() == RecordingStartResult.Started, "Hardware recording must start.");
        await Task.Delay(500);
        service.StopRecording();
        var state = service.GetSnapshot();
        Check(state.InputError == null, "WASAPI capture must not fail: " + state.InputError);
        Check(state.HasRecording && state.Elapsed.TotalSeconds > 0.1, "Hardware must deliver audio frames.");
        if (state.HasRecording) Console.WriteLine("Hardware WAV: " + service.SaveWav(Path.Combine(output, "hardware.wav")));
    }
}
else
{
    var source = new FakeInput();
    using var service = new AudioConsoleService(source);
    Check(service.StartRecording() == RecordingStartResult.NoInput, "Cannot record without an input.");
    service.SelectInput("fake");
    Check(service.GetSnapshot().InputAvailable, "Input must be available after selection.");
    var samples = new float[48000 * 2];
    for (var frame = 0; frame < 48000; frame++)
    {
        var value = (float)(Math.Sin(2 * Math.PI * 440 * frame / 48000) * 0.25);
        samples[frame * 2] = value;
        samples[frame * 2 + 1] = -value; // Opposite phases must remain visible in waveform.
    }
    Check(service.StartRecording() == RecordingStartResult.Started, "First take starts.");
    Throws<InvalidOperationException>(() => service.SelectInput("other"), "Input selection is locked while recording.");
    Check(service.StartRecording() == RecordingStartResult.AlreadyRecording, "Starting twice does not erase data.");
    source.Push(samples);
    var running = service.GetSnapshot();
    Check(running.IsRecording && running.Elapsed == TimeSpan.FromSeconds(1), "Timer reflects captured frames.");
    Check(running.Waveform.Any(peak => peak.Low < -0.2 && peak.High > 0.2), "Waveform retains both stereo phases.");
    running.Waveform[0] = new(42, 42);
    Check(service.GetSnapshot().Waveform[0].High != 42, "Snapshots must not expose mutable service state.");
    Throws<InvalidOperationException>(() => service.SaveWav(Path.Combine(output, "running.wav")), "Cannot save while recording.");
    service.StopRecording();
    Check(service.GetSnapshot().Recordings.Length == 1 && !service.GetSnapshot().Recordings[0].IsExported,
        "Stopped take must be kept in temporary history without exporting.");
    var fullPath = service.SaveWav(Path.Combine(output, "full.WAV"));
    Check(fullPath.EndsWith("full.WAV", StringComparison.Ordinal), "Uppercase extension must not be duplicated.");
    var full = File.ReadAllBytes(fullPath);
    Check(full.AsSpan(0, 4).SequenceEqual("RIFF"u8) && full.AsSpan(8, 4).SequenceEqual("WAVE"u8), "Valid RIFF/WAVE signatures.");
    Check(BinaryPrimitives.ReadInt32LittleEndian(full.AsSpan(4)) == full.Length - 8, "RIFF length matches file.");
    Check(BinaryPrimitives.ReadInt16LittleEndian(full.AsSpan(22)) == 2, "Stereo format is preserved.");
    Check(BinaryPrimitives.ReadInt32LittleEndian(full.AsSpan(24)) == 48000, "Sample rate is preserved.");
    Check(BinaryPrimitives.ReadInt16LittleEndian(full.AsSpan(34)) == 16, "PCM16 format.");
    Check(full.Length == 44 + 48000 * 2 * 2, "Exactly one second of stereo data.");
    service.StopRecording();
    Check(!service.GetSnapshot().HasUnsavedRecording, "An extra Stop must not mark saved data as dirty.");

    service.SetVolume(50);
    service.StartRecording();
    source.Push(samples);
    service.StopRecording();
    Throws<DirectoryNotFoundException>(() => service.SaveWav(Path.Combine(output, Guid.NewGuid().ToString(), "bad.wav")), "Failed save must report an error.");
    Check(service.GetSnapshot().HasUnsavedRecording, "Failed save retains unsaved audio.");
    var half = File.ReadAllBytes(service.SaveWav(Path.Combine(output, "half")));
    var fullPeak = Peak(full.AsSpan(44));
    var halfPeak = Peak(half.AsSpan(44));
    Check(Math.Abs((double)halfPeak / fullPeak - 0.5) < 0.001, "50% volume halves exported samples.");
    Console.WriteLine($"PCM peaks: 100%={fullPeak}, 50%={halfPeak}");

    service.SetVolume(0);
    service.StartRecording();
    source.Push(samples);
    service.StopRecording();
    Check(Peak(File.ReadAllBytes(service.SaveWav(Path.Combine(output, "silent.wav"))).AsSpan(44)) == 0, "0% records silence.");
    service.SetVolume(200);
    service.StartRecording();
    source.Push(new float[] { 0.75f, -0.75f, float.NaN, float.PositiveInfinity });
    service.StopRecording();
    var clipped = File.ReadAllBytes(service.SaveWav(Path.Combine(output, "clipped.wav")));
    Check(BinaryPrimitives.ReadInt16LittleEndian(clipped.AsSpan(44)) == short.MaxValue, "Positive clipping saturates.");
    Check(BinaryPrimitives.ReadInt16LittleEndian(clipped.AsSpan(46)) == short.MinValue, "Negative clipping saturates.");
    Check(BinaryPrimitives.ReadInt32LittleEndian(clipped.AsSpan(48)) == 0, "Nonfinite input becomes silence.");
    Throws<ArgumentOutOfRangeException>(() => service.SetVolume(double.NaN), "Reject invalid gain.");

    service.StartRecording();
    source.Push(samples);
    source.Fail("device removed");
    var failed = service.GetSnapshot();
    Check(!failed.IsRecording && !failed.InputAvailable && failed.HasUnsavedRecording && failed.InputError == "device removed",
        "Device failure stops recording and preserves the take.");
    var length = failed.Elapsed;
    source.Push(samples);
    Check(service.GetSnapshot().Elapsed == length, "Late callbacks from failed input must be ignored.");
    service.SelectInput("fake");
    var retained = service.GetSnapshot().Recordings.Length;
    Check(service.StartRecording() == RecordingStartResult.Started, "A new take preserves previous unsaved recordings.");
    service.StopRecording();
    Check(service.GetSnapshot().Recordings.Length == retained, "An empty take must not add or erase a history entry.");
    service.StartRecording();
    Parallel.For(0, 20, index => { source.Push(samples.AsMemory(0, 960)); service.GetSnapshot(); });
    service.StopRecording();
    Check(service.GetSnapshot().Elapsed.TotalSeconds == 0.2, "Concurrent callbacks and snapshots are serialized safely.");
    Check(!typeof(AudioConsoleService).Assembly.GetReferencedAssemblies().Any(reference => reference.Name!.Contains("Godot")),
        "Core DLL must not reference Godot.");

    var historyRoot = Path.Combine(output, "history-" + Guid.NewGuid().ToString("N"));
    var historyInput = new FakeInput();
    var historyPlayback = new FakePlayback();
    string? firstTemporary = null;
    var preservedExport = Path.Combine(output, "preserved-export.wav");
    using (var history = new AudioConsoleService(historyInput, historyPlayback, historyRoot))
    {
        history.SelectInput("fake");
        history.StartRecording();
        historyInput.Push(samples);
        history.StopRecording();
        history.PlaySelected();
        firstTemporary = historyPlayback.Path;
        Check(File.Exists(firstTemporary) && history.GetSnapshot().IsPlaying, "Unsaved take must play from a real temporary WAV.");
        history.StopPlayback();
        Check(!history.GetSnapshot().IsPlaying, "Stop playback must update state.");
        history.SaveWav(preservedExport);
        var firstId = history.GetSnapshot().SelectedRecordingId;
        for (var i = 0; i < 10; i++)
        {
            history.StartRecording();
            historyInput.Push(samples.AsMemory(0, 960));
            history.StopRecording();
        }
        var state = history.GetSnapshot();
        Check(state.Recordings.Length == 10 && state.Recordings.All(entry => entry.Id != firstId), "History retains exactly the newest ten takes.");
        Check(!File.Exists(firstTemporary) && File.Exists(preservedExport), "Eviction removes only the temporary copy, never an exported WAV.");
        Check(Directory.GetFiles(historyRoot, "*.wav", SearchOption.AllDirectories).Length == 10, "At most ten temporary WAV files remain.");
        var older = state.Recordings[5];
        history.SelectRecording(older.Id);
        history.PlaySelected();
        Check(history.GetSnapshot().SelectedRecordingId == older.Id && historyPlayback.IsPlaying, "Any older take can be selected and played.");
        historyPlayback.Complete();
        Check(!history.GetSnapshot().IsPlaying, "Natural playback completion is visible in snapshots.");
        var olderPath = history.SaveWav(Path.Combine(output, "older.wav"));
        Check(File.Exists(olderPath) && history.GetSnapshot().Recordings.Single(entry => entry.Id == older.Id).IsExported,
            "Export updates only the selected history entry.");
        history.PlaySelected();
        history.StartRecording();
        Check(!historyPlayback.IsPlaying, "Starting a recording stops playback before capture.");
        Throws<InvalidOperationException>(() => history.PlaySelected(), "Cannot play while recording.");
        Throws<InvalidOperationException>(() => history.SelectRecording(older.Id), "Cannot change selection while recording.");
        history.StopRecording();
    }
    Check(Directory.GetFiles(historyRoot, "*.wav", SearchOption.AllDirectories).Length == 0 && File.Exists(preservedExport),
        "Closing the session cleans temporary history and preserves exports.");
}

Console.WriteLine($"Audio console C# tests: {(failures == 0 ? "PASS" : "FAIL")} ({failures} failures)");
return failures == 0 ? 0 : 1;

static int Peak(ReadOnlySpan<byte> pcm)
{
    var peak = 0;
    for (var offset = 0; offset < pcm.Length; offset += 2)
        peak = Math.Max(peak, Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(pcm[offset..])));
    return peak;
}

internal sealed class FakeInput : IAudioInput
{
    public event Action<ReadOnlyMemory<float>>? SamplesAvailable;
    public event Action<string>? Failed;
    public IReadOnlyList<AudioInputDevice> GetDevices() => [new("fake", "Synthetic input")];
    public AudioFormat Start(string deviceId) => new(48000, 2);
    public void Stop() { }
    public void Push(ReadOnlyMemory<float> samples) => SamplesAvailable?.Invoke(samples);
    public void Fail(string message) => Failed?.Invoke(message);
    public void Dispose() { }
}

internal sealed class FakePlayback : IAudioPlayback
{
    public bool IsPlaying { get; private set; }
    public string? Error => null;
    public string? Path { get; private set; }
    public void Play(string wavPath)
    {
        if (!File.Exists(wavPath)) throw new FileNotFoundException("Missing temporary recording.", wavPath);
        Path = wavPath;
        IsPlaying = true;
    }
    public void Stop() => IsPlaying = false;
    public void Complete() => IsPlaying = false;
    public void Dispose() => Stop();
}
