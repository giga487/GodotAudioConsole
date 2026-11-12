using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AudioConsole.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioConsole.Windows;

/// <summary>Renders to an explicitly selected endpoint, including the input side of a virtual audio cable.</summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiAudioRedirect : IAudioRedirect
{
    private readonly object _gate = new();
    private WasapiOut? _output;
    private MMDevice? _device;
    private BufferedWaveProvider? _buffer;
    private WasapiAudioPlayback? _file;
    private volatile string? _error;
    public bool IsActive => _file?.IsPlaying == true || (_output?.PlaybackState == PlaybackState.Playing && _error == null);
    public string? Error => _file?.Error ?? _error;
    public TimeSpan Position => _file?.Position ?? TimeSpan.Zero;

    public IReadOnlyList<AudioOutputDevice> GetDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var captures = new List<(string Id, string Name)>();
        foreach (var endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            using (endpoint) captures.Add((endpoint.ID, endpoint.FriendlyName));
        var result = new List<AudioOutputDevice>();
        foreach (var endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (endpoint)
            {
                var blocked = new List<string> { "loopback:" + endpoint.ID };
                var name = endpoint.FriendlyName;
                if (name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) || name.Contains("VoiceMeeter", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Steam Streaming Microphone", StringComparison.OrdinalIgnoreCase))
                    blocked.AddRange(captures.Where(capture => CableKey(capture.Name) == CableKey(name)).Select(capture => capture.Id));
                result.Add(new(endpoint.ID, name, blocked.ToArray()));
            }
        }
        return result;
    }

    private static string CableKey(string name) => name.ToUpperInvariant().Replace("INPUT", "").Replace("OUTPUT", "")
        .Replace("SPEAKERS", "").Replace("MICROPHONE", "").Replace(" ", "");

    public void StartLive(string outputDeviceId, AudioFormat format)
    {
        Stop();
        _error = null;
        format.Validate();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            _device = enumerator.GetDevice(outputDeviceId);
            _buffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels))
                { BufferDuration = TimeSpan.FromMilliseconds(250), ReadFully = true, DiscardOnBufferOverflow = true };
            _output = new WasapiOut(_device, AudioClientShareMode.Shared, true, 60);
            _output.PlaybackStopped += OnStopped;
            _output.Init(_buffer);
            _output.Play();
        }
        catch { Stop(); throw; }
    }

    public void WriteSamples(ReadOnlyMemory<float> samples)
    {
        lock (_gate)
        {
            if (_buffer == null || _error != null) return;
            var bytes = MemoryMarshal.AsBytes(samples.Span).ToArray();
            if (_buffer.BufferedBytes + bytes.Length > _buffer.BufferLength) _buffer.ClearBuffer();
            _buffer.AddSamples(bytes, 0, bytes.Length);
        }
    }

    public void PlayFile(string outputDeviceId, string wavPath)
    {
        Stop();
        _error = null;
        _file = new WasapiAudioPlayback(outputDeviceId);
        try { _file.Play(wavPath); }
        catch { Stop(); throw; }
    }

    private void OnStopped(object? sender, StoppedEventArgs args)
    {
        if (sender == _output) _error = args.Exception?.Message ?? "Uscita di invio arrestata.";
    }

    public void Stop()
    {
        lock (_gate)
        {
            _file?.Dispose();
            _file = null;
            if (_output != null)
            {
                _output.PlaybackStopped -= OnStopped;
                _output.Dispose();
                _output = null;
            }
            _buffer = null;
            _device?.Dispose();
            _device = null;
        }
    }
    public void Dispose() => Stop();
}
