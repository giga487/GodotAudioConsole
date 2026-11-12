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
if (args.Contains("--loopback-hardware"))
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("WASAPI requires Windows.");
    using var input = new WasapiAudioInput();
    using var service = new AudioConsoleService(input, new WasapiAudioPlayback(), output);
    var devices = service.GetDevices(AudioSourceKind.ComputerOutput);
    Check(devices.Count > 0 && devices.All(device => device.Source == AudioSourceKind.ComputerOutput), "Loopback enumerates output devices.");
    if (devices.Count > 0)
    {
        Console.WriteLine($"WASAPI output sources: {devices.Count}");
        service.SelectInput(devices[0].Id);
        await Task.Delay(200);
        service.StartRecording();
        await Task.Delay(400); // No audio renderer other than the backend's silent keepalive.
        service.StopRecording();
        var quiet = service.GetSnapshot();
        Check(quiet.InputError == null && quiet.HasRecording && quiet.Elapsed.TotalSeconds > 0.2,
            "Loopback must retain time even while the output is silent: " + quiet.InputError);
        if (quiet.HasRecording)
        {
            var wav = File.ReadAllBytes(service.SaveWav(Path.Combine(output, "loopback-silence.wav")));
            Check(BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22)) == 2, "Surround outputs must be captured as stereo WAVs.");
        }
        using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        using var endpoint = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
        using var tone = new NAudio.Wave.WasapiOut(endpoint, NAudio.CoreAudioApi.AudioClientShareMode.Shared, true, 100);
        var signal = new NAudio.Wave.SampleProviders.SignalGenerator(48000, 2) { Gain = 0.02, Frequency = 440 };
        tone.Init(new NAudio.Wave.SampleProviders.SampleToWaveProvider(signal));
        service.StartRecording();
        tone.Play();
        await Task.Delay(500);
        tone.Stop();
        await Task.Delay(150);
        service.StopRecording();
        var recorded = service.GetSnapshot();
        Check(recorded.HasRecording && recorded.InputError == null, "Loopback must capture rendered audio.");
        if (recorded.HasRecording)
        {
            var wav = File.ReadAllBytes(service.SaveWav(Path.Combine(output, "loopback-tone.wav")));
            Check(Peak(wav.AsSpan(44)) > 10, "Loopback PCM must contain the quiet test tone.");
            service.PlaySelected();
            Check(service.GetSnapshot().IsInputSuspended, "Replay suspends loopback to avoid self-capture.");
            service.StopPlayback();
            Check(!service.GetSnapshot().IsInputSuspended && service.GetSnapshot().InputAvailable,
                "Stopping replay restores the same loopback source.");
        }
    }
}
else if (args.Contains("--playback-hardware"))
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("WASAPI requires Windows.");
    using var playback = new WasapiAudioPlayback();
    playback.Play(Path.Combine(output, "silent.wav"));
    Check(playback.IsPlaying, "WASAPI playback must start.");
    await Task.Delay(200);
    Check(playback.Position.TotalSeconds > 0 && playback.Position.TotalSeconds < 1, "Hardware playback position advances with the output clock.");
    playback.Pause();
    Check(playback.IsPaused && !playback.IsPlaying, "WASAPI must enter paused state.");
    var pausedPosition = playback.Position;
    await Task.Delay(200);
    Check(playback.Position == pausedPosition, "Playback position remains fixed while paused.");
    playback.Resume();
    Check(playback.IsPlaying && !playback.IsPaused, "WASAPI must resume the same stream.");
    Check(Math.Abs((playback.Position - pausedPosition).TotalSeconds) < 0.1, "Resume must not count the paused device-clock interval.");
    await Task.Delay(1800);
    Check(playback.Error == null, "WASAPI playback must not fail: " + playback.Error);
    Check(!playback.IsPlaying, "Playback must finish automatically at end of file.");
    playback.Stop();
}
else if (args.Contains("--hardware"))
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("WASAPI requires Windows.");
    using var service = new AudioConsoleService(new WasapiAudioInput(), new WasapiAudioPlayback());
    var devices = service.GetDevices();
    Check(devices.Count > 0, "At least one microphone must be available for hardware smoke test.");
    if (devices.Count > 0)
    {
        Console.WriteLine($"WASAPI inputs: {devices.Count}");
        service.SelectInput(devices[0].Id);
        service.SetVolume(0); // The hardware round-trip test replays silence, not room audio.
        Check(service.StartRecording() == RecordingStartResult.Started, "Hardware recording must start.");
        await Task.Delay(500);
        service.StopRecording();
        var state = service.GetSnapshot();
        Check(state.InputError == null, "WASAPI capture must not fail: " + state.InputError);
        Check(state.HasRecording && state.Elapsed.TotalSeconds > 0.1, "Hardware must deliver audio frames.");
        if (state.HasRecording) Console.WriteLine("Hardware WAV: " + service.SaveWav(Path.Combine(output, "hardware.wav")));
        if (state.HasRecording)
        {
            service.PlaySelected();
            Check(service.GetSnapshot().IsInputSuspended, "Real capture must be stopped during WASAPI playback.");
            await Task.Delay(900);
            service.Update();
            var restored = service.GetSnapshot();
            Check(!restored.IsInputSuspended && restored.InputAvailable && restored.InputError == null,
                "Real capture must reopen after natural playback completion.");
        }
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
        Check(!historyInput.IsRunning && history.GetSnapshot().IsInputSuspended, "Playback must close microphone capture.");
        historyInput.Push(samples); // Simulates a late callback already queued before Stop.
        Check(history.GetSnapshot().Waveform.All(peak => peak == default), "Suspended microphone callbacks must not update the live waveform.");
        historyPlayback.Position = TimeSpan.FromSeconds(0.25);
        var editorState = history.GetSnapshot().Editor!;
        Check(editorState.Position == TimeSpan.FromSeconds(0.25) && editorState.Levels.Left > 0.2f,
            "Playback clock and meters must come from the selected recording.");
        history.PausePlayback();
        Check(history.GetSnapshot().IsPaused && !history.GetSnapshot().IsPlaying, "Pause preserves the active playback session.");
        Check(!historyInput.IsRunning, "Microphone must remain closed during playback pause.");
        history.PlaySelected();
        Check(history.GetSnapshot().IsPlaying && historyPlayback.PlayCalls == 1, "Play after pause resumes without opening the WAV again.");
        history.StopPlayback();
        Check(!history.GetSnapshot().IsPlaying, "Stop playback must update state.");
        Check(historyInput.IsRunning && !history.GetSnapshot().IsInputSuspended, "Stop must restore microphone capture.");
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
        history.Update();
        Check(!history.GetSnapshot().IsPlaying, "Natural playback completion is visible in snapshots.");
        Check(historyInput.IsRunning && history.GetSnapshot().Editor!.Position == older.Duration,
            "Natural completion must restore capture and leave the playhead at the end.");
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

    var editInput = new FakeInput();
    using (var editor = new AudioConsoleService(editInput, new FakePlayback(), output))
    {
        editor.SelectInput("fake");
        editor.StartRecording();
        editInput.Push(samples);
        editor.StopRecording();
        var originalId = editor.GetSnapshot().SelectedRecordingId!.Value;
        var originalPath = editor.SaveWav(Path.Combine(output, "edit-original.wav"));
        var original = File.ReadAllBytes(originalPath);
        Check(editor.GetSnapshot().Editor!.Waveform.Any(peak => peak.Low < -0.2f && peak.High > 0.2f), "Editor waveform represents original PCM.");
        Throws<InvalidOperationException>(() => editor.GenerateSelectedPart(), "Empty selection must not create a take.");
        Throws<ArgumentOutOfRangeException>(() => editor.SetSelection(TimeSpan.Zero, TimeSpan.FromSeconds(2)), "Selection cannot exceed duration.");
        editor.SetSelection(TimeSpan.FromSeconds(0.6), TimeSpan.FromSeconds(0.2)); // Dragging right to left is supported.
        var part = editor.GenerateSelectedPart();
        Check(part.Duration == TimeSpan.FromSeconds(0.4) && editor.GetSnapshot().Recordings.Length == 2, "Selection creates a separate take with the exact duration.");
        var cropped = File.ReadAllBytes(editor.SaveWav(Path.Combine(output, "crop.wav")));
        Check(cropped.AsSpan(44).SequenceEqual(original.AsSpan(44 + 9600 * 4, 19200 * 4)), "Crop must preserve exact stereo PCM frames without re-encoding changes.");
        Check(editor.GetSnapshot().Recordings.Any(item => item.Id == originalId), "Generating a crop retains the original.");
        for (var i = 0; i < 8; i++)
        {
            editor.StartRecording();
            editInput.Push(samples.AsMemory(0, 960));
            editor.StopRecording();
        }
        editor.SelectRecording(originalId); // Original is now the oldest of ten entries.
        editor.SetSelection(TimeSpan.Zero, TimeSpan.FromSeconds(0.1));
        editor.GenerateSelectedPart();
        Check(editor.GetSnapshot().Recordings.Length == 10 && editor.GetSnapshot().Recordings.Any(item => item.Id == originalId),
            "At capacity, generating a crop must protect its source and retain ten items.");
        editor.SelectRecording(originalId);
        editor.SaveWav(originalPath);
        Check(File.ReadAllBytes(originalPath).SequenceEqual(original), "Original WAV must remain byte-for-byte unchanged.");
        var retainedCount = editor.GetSnapshot().Recordings.Length;
        editor.DisconnectInput();
        Check(!editor.GetSnapshot().InputAvailable && !editInput.IsRunning && editor.GetSnapshot().Recordings.Length == retainedCount,
            "Changing source closes capture without losing history.");
        var outputSource = editor.GetDevices(AudioSourceKind.ComputerOutput).Single();
        editor.SelectInput(outputSource.Id);
        editor.PlaySelected();
        editor.StopPlayback();
        Check(editInput.LastDeviceId == outputSource.Id, "Resume must reopen the selected output source, not the microphone.");
        editor.StartRecording();
        Throws<InvalidOperationException>(() => editor.DisconnectInput(), "Source changes are blocked while recording.");
        editor.StopRecording();
    }
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
    public bool IsRunning { get; private set; }
    public string? LastDeviceId { get; private set; }
    public event Action<ReadOnlyMemory<float>>? SamplesAvailable;
    public event Action<string>? Failed;
    public IReadOnlyList<AudioInputDevice> GetDevices(AudioSourceKind source = AudioSourceKind.Microphone)
        => [new(source == AudioSourceKind.ComputerOutput ? "fake-output" : "fake", "Synthetic input", source)];
    public AudioFormat Start(string deviceId) { IsRunning = true; LastDeviceId = deviceId; return new(48000, 2); }
    public void Stop() => IsRunning = false;
    public void Push(ReadOnlyMemory<float> samples) => SamplesAvailable?.Invoke(samples);
    public void Fail(string message) => Failed?.Invoke(message);
    public void Dispose() { }
}

internal sealed class FakePlayback : IAudioPlayback
{
    public bool IsPlaying { get; private set; }
    public bool IsPaused { get; private set; }
    public int PlayCalls { get; private set; }
    public TimeSpan Position { get; set; }
    public string? Error => null;
    public string? Path { get; private set; }
    public void Play(string wavPath)
    {
        if (!File.Exists(wavPath)) throw new FileNotFoundException("Missing temporary recording.", wavPath);
        Path = wavPath;
        PlayCalls++;
        Position = TimeSpan.Zero;
        IsPaused = false;
        IsPlaying = true;
    }
    public void Pause() { IsPaused = IsPlaying; IsPlaying = false; }
    public void Resume() { IsPlaying = IsPaused; IsPaused = false; }
    public void Stop() { IsPlaying = false; IsPaused = false; }
    public void Complete() => IsPlaying = false;
    public void Dispose() => Stop();
}
