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
RedirectTests.Run(output, Check);
EqualizerTests.Run(output, Check);
EchoTests.Run(output, Check);
LoopTests.Run(output, Check);
if (args.Contains("--redirect-hardware"))
{
    await RedirectHardwareTest.Run(output, Check);
}
else if (args.Contains("--mix-hardware"))
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("WASAPI requires Windows.");
    using var input = WasapiAudioInput.CreateMixer();
    using var service = new AudioConsoleService(input, new WasapiAudioPlayback(), output);
    var microphones = service.GetDevices();
    var outputs = service.GetDevices(AudioSourceKind.ComputerOutput);
    Check(microphones.Count > 0 && outputs.Count > 0, "Mix hardware test needs one microphone and one output.");
    if (microphones.Count > 0 && outputs.Count > 0)
    {
        service.ConfigureSources([new(microphones[0]), new(outputs[0])]);
        service.SetVolume(0); // Replay silence rather than captured room/PC audio.
        await Task.Delay(300);
        service.StartRecording();
        service.SetSourceVolume(microphones[0].Identity, 50);
        await Task.Delay(400);
        service.StopRecording();
        var state = service.GetSnapshot();
        Check(state.InputError == null && state.Sources.Length == 2 && state.HasRecording,
            "Microphone and PC must run concurrently: " + state.InputError);
        Check(state.Elapsed.TotalSeconds is > 0.25 and < 0.65, "Two sources must share one recording clock.");
        if (state.HasRecording)
        {
            var wav = File.ReadAllBytes(service.SaveWav(Path.Combine(output, "multi-source-hardware.wav")));
            Check(BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)) == 48000 && BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22)) == 2,
                "Mixed hardware WAV must be stereo 48 kHz.");
            service.PlaySelected();
            Check(service.GetSnapshot().IsInputSuspended, "Playback suspends the whole native mixer.");
            service.StopPlayback();
            Check(service.GetSnapshot().InputAvailable && service.GetSnapshot().Sources.Length == 2,
                "Stopping playback reopens all normalized native sources.");
        }
    }
}
else if (args.Contains("--loopback-hardware"))
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
    var mic = new AudioInputDevice("mic", "Microfono sintetico");
    var pc = new AudioInputDevice("pc", "Audio PC sintetico", AudioSourceKind.ComputerOutput);
    var engine = new StereoMixer([new(mic, 50), new(pc)]);
    engine.Push(mic.Identity, new float[] { 0.2f, -0.2f, 0.4f, -0.4f });
    engine.Push(pc.Identity, new float[] { 0.3f, 0.1f, 0.2f, 0.2f });
    var mixed = engine.ReadFrames(2);
    Check(mixed.Length == 4 && Math.Abs(mixed[0] - 0.4f) < 0.0001f && Math.Abs(mixed[1]) < 0.0001f,
        "Inputs must be summed frame-by-frame with independent gains, never appended.");
    Check(Math.Abs(mixed[2] - 0.4f) < 0.0001f && Math.Abs(mixed[3]) < 0.0001f, "Stereo channel ordering must be preserved.");
    Check(engine.GetLevels("mic").Left > 0.19f && engine.GetLevels("pc").Left > 0.29f, "Each source has its own post-gain meter.");
    Check(engine.ReadFrames(100).All(value => value == 0), "Missing source frames must become silence.");
    engine.SetVolume("pc", 0);
    engine.Push("pc", new float[] { 1, 1 });
    Check(engine.ReadFrames(1).All(value => value == 0), "A muted source contributes no audio.");
    Throws<ArgumentException>(() => new StereoMixer([new(mic), new(new("default", "Alias", CanonicalId: "mic"))]),
        "Default-device aliases cannot double-count the same physical endpoint.");
    var captures = new List<FakeInput>();
    using (var multi = new MultiSourceAudioInput(() => { var capture = new FakeInput(); captures.Add(capture); return capture; }))
    using (var mixSession = new AudioConsoleService(multi, new FakePlayback(), output))
    {
        mixSession.ConfigureSources([new(mic, 50), new(pc)]);
        foreach (var capture in captures.Where(capture => capture.IsRunning))
            capture.Push(Enumerable.Repeat(capture.LastDeviceId == "mic" ? 0.2f : 0.3f, 48000 * 2).ToArray());
        await Task.Delay(150); // Let the common jitter buffer fill before comparing exact PCM values.
        mixSession.StartRecording();
        await Task.Delay(300);
        mixSession.SetSourceVolume("pc", 0);
        await Task.Delay(100);
        mixSession.StopRecording();
        var state = mixSession.GetSnapshot();
        Check(state.Sources.Length == 2 && state.InputError == null && state.HasRecording, "Asynchronous sources produce one take.");
        Check(state.Elapsed.TotalSeconds is > 0.15 and < 0.5, "Source count must not multiply recorded duration.");
        var pcm = File.ReadAllBytes(mixSession.SaveWav(Path.Combine(output, "mixed.wav")));
        Check(Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(44)) - 13107) < 3,
            "Exported PCM must include both independently scaled sources.");
        Check(Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(pcm.Length - 2)) - 3277) < 3,
            "Source gain changes while recording must affect subsequent PCM.");
        var mixedRetained = state.Recordings.Length;
        mixSession.PlaySelected();
        Check(captures.All(capture => !capture.IsRunning), "Playback closes every source.");
        mixSession.StopPlayback();
        Check(captures.Count(capture => capture.IsRunning) == 2 && mixSession.GetSnapshot().Sources.Single(source => source.Device.Id == "pc").VolumePercent == 0,
            "Replay completion restores every source and its gain.");
        mixSession.StartRecording();
        Throws<InvalidOperationException>(() => mixSession.ConfigureSources([new(mic)]), "Recording locks the source configuration.");
        await Task.Delay(50);
        captures.First(capture => capture.IsRunning).Fail("device disconnected");
        Check(!mixSession.GetSnapshot().IsRecording && !mixSession.GetSnapshot().InputAvailable, "A source failure stops a take instead of silently dropping a channel.");
        mixSession.ConfigureSources([new(pc)]);
        Check(mixSession.GetSnapshot().InputAvailable && mixSession.GetSnapshot().Sources.Length == 1, "Removing a failed source can restore capture.");
        mixSession.ConfigureSources([]);
        Check(!mixSession.GetSnapshot().InputAvailable && captures.All(capture => !capture.IsRunning) && mixSession.GetSnapshot().Recordings.Length >= mixedRetained,
            "Removing all sources closes capture and preserves history.");
    }

    var setupDirectory = Path.Combine(output, "setup-" + Guid.NewGuid().ToString("N"));
    var setupStore = new LastSetupStore(Path.Combine(setupDirectory, "last-setup.json"));
    Check(setupStore.Load() == null, "A missing setup file represents first launch.");
    var savedSetup = new LastSetup(LastSetupStore.CurrentVersion, 62,
        [new(new("fake", "Microfono"), 33), new(new("fake-output", "Uscita PC", AudioSourceKind.ComputerOutput), 75)]);
    setupStore.Save(savedSetup);
    var loadedSetup = setupStore.Load()!;
    Check(loadedSetup.MasterVolumePercent == 62 && loadedSetup.Sources.Length == 2 && loadedSetup.Sources[0].VolumePercent == 33,
        "JSON round-trip retains master gain, source identities and individual gains.");
    var setupJson = File.ReadAllText(setupStore.FilePath);
    Check(setupJson.Contains("ComputerOutput") && !setupJson.Contains("levels") && !setupJson.Contains("recordings") && !setupJson.Contains("identity"),
        "Setup JSON contains only reusable settings, not audio or transient state.");
    Check(Directory.GetFiles(setupDirectory, "*.tmp").Length == 0, "Atomic setup saves leave no temporary files.");
    using (var restored = new AudioConsoleService(new MultiSourceAudioInput(() => new FakeInput()), new FakePlayback(), output))
    {
        var result = LastSetupStore.Restore(restored, loadedSetup);
        var state = restored.GetSnapshot();
        Check(result.RestoredCount == 2 && result.Warnings.Length == 0 && state.VolumePercent == 62 &&
            state.Sources.Single(source => source.Device.Id == "fake-output").VolumePercent == 75 && !state.IsRecording,
            "Restore opens both sources with saved gains without starting a recording.");
    }
    using (var missing = new AudioConsoleService(new MultiSourceAudioInput(() => new FakeInput { HideOutput = true }), temporaryRoot: output))
    {
        var result = LastSetupStore.Restore(missing, loadedSetup);
        Check(result.RestoredCount == 1 && result.Warnings.Length == 1 && missing.GetSnapshot().Sources[0].VolumePercent == 33,
            "Missing devices do not block restoration of available sources.");
        Check(File.ReadAllText(setupStore.FilePath) == setupJson, "Partial restoration never rewrites the saved configuration.");
    }
    using (var blocked = new AudioConsoleService(new MultiSourceAudioInput(() => new FakeInput { FailDeviceId = "fake-output" }), temporaryRoot: output))
    {
        var result = LastSetupStore.Restore(blocked, loadedSetup);
        Check(result.RestoredCount == 1 && result.Warnings.Length == 1 && blocked.GetSnapshot().InputAvailable,
            "An enumerated device that fails to open must not block other sources.");
        var empty = new LastSetup(LastSetupStore.CurrentVersion, 0, []);
        setupStore.Save(empty);
        LastSetupStore.Restore(blocked, setupStore.Load()!);
        Check(blocked.GetSnapshot().Sources.Length == 0 && !blocked.GetSnapshot().InputAvailable && blocked.GetSnapshot().VolumePercent == 0,
            "An explicitly empty setup and muted master must remain empty and muted.");
    }
    var validJson = File.ReadAllText(setupStore.FilePath);
    Throws<InvalidDataException>(() => setupStore.Save(savedSetup with { Version = 99 }), "Unsupported setup versions must be rejected.");
    Throws<ArgumentOutOfRangeException>(() => setupStore.Save(savedSetup with { MasterVolumePercent = 201 }), "Invalid setup gains must be rejected.");
    Throws<InvalidDataException>(() => setupStore.Save(savedSetup with { Sources = [savedSetup.Sources[0], savedSetup.Sources[0]] }), "Duplicate setup sources must be rejected.");
    Check(File.ReadAllText(setupStore.FilePath) == validJson, "Invalid save attempts must preserve the previous setup.");
    File.WriteAllText(setupStore.FilePath, "{ broken JSON");
    Throws<System.Text.Json.JsonException>(() => setupStore.Load(), "Corrupt JSON must produce a recoverable load error.");
    Check(File.ReadAllText(setupStore.FilePath) == "{ broken JSON", "Loading corrupt settings must not modify the original file.");
    var blockedParent = Path.Combine(setupDirectory, "not-a-directory");
    File.WriteAllText(blockedParent, "preserve");
    Throws<IOException>(() => new LastSetupStore(Path.Combine(blockedParent, "last-setup.json")).Save(savedSetup),
        "Unwritable setup paths must surface a save error.");
    Check(File.ReadAllText(blockedParent) == "preserve", "A failed setup save must not damage existing files.");
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
    public bool HideOutput { get; init; }
    public string? FailDeviceId { get; init; }
    public event Action<ReadOnlyMemory<float>>? SamplesAvailable;
    public event Action<string>? Failed;
    public IReadOnlyList<AudioInputDevice> GetDevices(AudioSourceKind source = AudioSourceKind.Microphone)
        => source == AudioSourceKind.ComputerOutput && HideOutput ? []
            : [new(source == AudioSourceKind.ComputerOutput ? "fake-output" : "fake", "Synthetic input", source)];
    public AudioFormat Start(string deviceId)
    {
        if (deviceId == FailDeviceId) throw new InvalidOperationException("Device cannot be opened.");
        IsRunning = true; LastDeviceId = deviceId; return new(48000, 2);
    }
    public void Stop() => IsRunning = false;
    public void Push(ReadOnlyMemory<float> samples) => SamplesAvailable?.Invoke(samples);
    public void Fail(string message) => Failed?.Invoke(message);
    public void Dispose() => Stop();
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
