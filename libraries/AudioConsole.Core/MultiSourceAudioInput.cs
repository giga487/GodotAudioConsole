using System.Diagnostics;

namespace AudioConsole.Core;

/// <summary>Composes independent normalized capture devices. Commands run on one application thread.</summary>
public sealed class MultiSourceAudioInput : IMultiSourceAudioInput
{
    public const string MixDeviceId = "audio-console:mix";
    private sealed record Capture(IAudioInput Input, Action<ReadOnlyMemory<float>> Samples, Action<string> Failure);
    private readonly Func<IAudioInput> _factory;
    private readonly object _gate = new();
    private readonly List<Capture> _captures = [];
    private AudioSourceSettings[] _settings = [];
    private readonly Dictionary<string, string> _errors = [];
    private StereoMixer? _mixer;
    private Thread? _worker;
    private ManualResetEventSlim? _stop;
    private bool _running, _disposed;

    public MultiSourceAudioInput(Func<IAudioInput> factory) => _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    public event Action<ReadOnlyMemory<float>>? SamplesAvailable;
    public event Action<string>? Failed;

    public IReadOnlyList<AudioInputDevice> GetDevices(AudioSourceKind source = AudioSourceKind.Microphone)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var input = _factory();
        return input.GetDevices(source).ToArray();
    }

    public void ConfigureSources(IReadOnlyList<AudioSourceSettings> sources)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var validated = new StereoMixer(sources);
        lock (_gate)
        {
            if (_running) throw new InvalidOperationException("Ferma la cattura prima di configurare le sorgenti.");
            _settings = sources.ToArray();
            _mixer = validated;
            _errors.Clear();
        }
    }

    public AudioFormat Start(string deviceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();
        if (deviceId != MixDeviceId) throw new ArgumentException("Usa ConfigureSources prima di avviare il mix.", nameof(deviceId));
        if (_settings.Length == 0) throw new InvalidOperationException("Aggiungi almeno una sorgente audio.");
        var stop = new ManualResetEventSlim();
        _stop = stop;
        lock (_gate) { _mixer = new StereoMixer(_settings); _errors.Clear(); }
        try
        {
            foreach (var setting in _settings)
            {
                var input = _factory();
                Action<ReadOnlyMemory<float>> samples = data =>
                {
                    lock (_gate) { if (_running) _mixer!.Push(setting.Device.Identity, data.Span); }
                };
                Action<string> failure = error => ReportFailure(setting.Device.Identity, setting.Device.Name + ": " + error);
                input.SamplesAvailable += samples;
                input.Failed += failure;
                _captures.Add(new(input, samples, failure));
                try
                {
                    if (input.Start(setting.Device.Identity) != StereoMixer.Format)
                        throw new NotSupportedException("Il backend deve fornire ogni sorgente in stereo a 48 kHz.");
                }
                catch (Exception error)
                {
                    lock (_gate) _errors[setting.Device.Identity] = error.Message;
                    throw;
                }
            }
            lock (_gate)
            {
                if (_errors.Count > 0) throw new InvalidOperationException(_errors.Values.First());
                _running = true;
            }
            _worker = new Thread(() => MixLoop(stop)) { IsBackground = true, Name = "Audio console mix" };
            _worker.Start();
            return StereoMixer.Format;
        }
        catch { Stop(); throw; }
    }

    private void MixLoop(ManualResetEventSlim stop)
    {
        const int batchFrames = 480;
        var clock = Stopwatch.StartNew();
        long produced = 0;
        try
        {
            while (!stop.Wait(5))
            {
                // One output clock, including startup silence while the jitter buffers fill.
                var due = (long)(clock.Elapsed.TotalSeconds * StereoMixer.Format.SampleRate);
                while (produced + batchFrames <= due && !stop.IsSet)
                {
                    float[] block;
                    lock (_gate) block = produced < 5760 ? new float[batchFrames * 2] : _mixer!.ReadFrames(batchFrames);
                    SamplesAvailable?.Invoke(block);
                    produced += batchFrames;
                }
            }
        }
        catch (Exception error) { ReportFailure("mix", error.Message); }
    }

    private void ReportFailure(string identity, string error)
    {
        lock (_gate)
        {
            _errors[identity] = error;
            _running = false;
            _stop?.Set();
        }
        Failed?.Invoke(error);
    }

    public void SetSourceVolume(string identity, double percent)
    {
        StereoMixer.ValidateGain(percent);
        lock (_gate)
        {
            var index = Array.FindIndex(_settings, setting => setting.Device.Identity == identity);
            if (index < 0) throw new ArgumentException("Sorgente non disponibile.", nameof(identity));
            _settings[index] = _settings[index] with { VolumePercent = percent };
            _mixer?.SetVolume(identity, percent);
        }
    }

    public AudioSourceState[] GetSources()
    {
        lock (_gate) return _settings.Select(setting => new AudioSourceState(setting.Device, setting.VolumePercent,
            _running ? _mixer?.GetLevels(setting.Device.Identity) ?? default : default,
            _errors.GetValueOrDefault(setting.Device.Identity))).ToArray();
    }

    public void Stop()
    {
        lock (_gate) { _running = false; _stop?.Set(); }
        if (_worker != null && _worker != Thread.CurrentThread) _worker.Join();
        _worker = null;
        foreach (var capture in _captures)
        {
            capture.Input.SamplesAvailable -= capture.Samples;
            capture.Input.Failed -= capture.Failure;
            capture.Input.Dispose();
        }
        _captures.Clear();
        _stop?.Dispose();
        _stop = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
    }
}
