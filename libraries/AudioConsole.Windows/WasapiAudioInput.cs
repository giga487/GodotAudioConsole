using System.Buffers.Binary;
using System.Runtime.Versioning;
using AudioConsole.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioConsole.Windows;

/// <summary>Windows device adapter. No Godot references, playback, effects or UI callbacks.</summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiAudioInput : IAudioInput
{
    public const string DefaultDeviceId = "default";
    private const string LoopbackPrefix = "loopback:";
    private WasapiCapture? _capture;
    private MMDevice? _device;
    private WasapiOut? _silenceRenderer;
    private MMDevice? _silenceDevice;
    private bool _disposed;
    private readonly AudioFormat? _targetFormat;

    public WasapiAudioInput(AudioFormat? targetFormat = null)
    {
        targetFormat?.Validate();
        _targetFormat = targetFormat;
    }

    public static IMultiSourceAudioInput CreateMixer()
        => new MultiSourceAudioInput(() => new WasapiAudioInput(StereoMixer.Format));

    public event Action<ReadOnlyMemory<float>>? SamplesAvailable;
    public event Action<string>? Failed;

    public IReadOnlyList<AudioInputDevice> GetDevices(AudioSourceKind source = AudioSourceKind.Microphone)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Enum.IsDefined(source)) throw new ArgumentOutOfRangeException(nameof(source));
        var loopback = source == AudioSourceKind.ComputerOutput;
        using var enumerator = new MMDeviceEnumerator();
        var result = new List<AudioInputDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(loopback ? DataFlow.Render : DataFlow.Capture, DeviceState.Active))
        {
            using (device) result.Add(new((loopback ? LoopbackPrefix : "") + device.ID, device.FriendlyName, source));
        }
        if (result.Count > 0)
        {
            using var defaultEndpoint = enumerator.GetDefaultAudioEndpoint(loopback ? DataFlow.Render : DataFlow.Capture,
                loopback ? Role.Multimedia : Role.Console);
            result.Insert(0, new((loopback ? LoopbackPrefix : "") + DefaultDeviceId,
                loopback ? "Uscita predefinita di sistema" : "Predefinito di sistema", source,
                (loopback ? LoopbackPrefix : "") + defaultEndpoint.ID));
        }
        return result;
    }

    public AudioFormat Start(string deviceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();
        try
        {
            var loopback = deviceId.StartsWith(LoopbackPrefix, StringComparison.Ordinal);
            var endpointId = loopback ? deviceId[LoopbackPrefix.Length..] : deviceId;
            using var enumerator = new MMDeviceEnumerator();
            _device = endpointId == DefaultDeviceId
                ? enumerator.GetDefaultAudioEndpoint(loopback ? DataFlow.Render : DataFlow.Capture, loopback ? Role.Multimedia : Role.Console)
                : enumerator.GetDevice(endpointId);
            // Native mix format avoids imposing an unsupported sample rate on hardware.
            _capture = loopback ? new WasapiLoopbackCapture(_device)
                : new WasapiCapture(_device, useEventSync: true, audioBufferMillisecondsLength: 30);
            if (_targetFormat is { } target)
                _capture.WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(target.SampleRate, target.Channels);
            else if (loopback)
                // Shared-mode WASAPI performs stereo downmix, including 5.1/7.1 outputs.
                _capture.WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(_capture.WaveFormat.SampleRate, 2);
            var format = _capture.WaveFormat;
            ValidateFormat(format);
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _capture.StartRecording();
            if (loopback)
            {
                // Keep the output engine ticking through silence so gaps retain their duration.
                // A distinct endpoint wrapper gives this renderer its own AudioClient.
                _silenceDevice = enumerator.GetDevice(_device.ID);
                _silenceRenderer = new WasapiOut(_silenceDevice, AudioClientShareMode.Shared, useEventSync: true, latency: 100);
                _silenceRenderer.PlaybackStopped += OnSilenceStopped;
                _silenceRenderer.Init(new SilenceProvider(format));
                _silenceRenderer.Play();
            }
            return new(format.SampleRate, format.Channels);
        }
        catch
        {
            Stop();
            throw;
        }
    }

    private static void ValidateFormat(WaveFormat format)
    {
        new AudioFormat(format.SampleRate, format.Channels).Validate();
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32) return;
        if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample is 16 or 24 or 32) return;
        throw new NotSupportedException($"Formato della sorgente audio non supportato: {format}.");
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        if (sender is not WasapiCapture capture) return;
        var format = capture.WaveFormat;
        var bytesPerSample = format.BitsPerSample / 8;
        var bytes = args.Buffer.AsSpan(0, args.BytesRecorded);
        var samples = new float[bytes.Length / bytesPerSample];
        for (var i = 0; i < samples.Length; i++)
        {
            var value = bytes.Slice(i * bytesPerSample, bytesPerSample);
            samples[i] = format.Encoding == WaveFormatEncoding.IeeeFloat
                ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(value))
                : bytesPerSample switch
                {
                    2 => BinaryPrimitives.ReadInt16LittleEndian(value) / 32768f,
                    3 => ((value[0] | value[1] << 8 | value[2] << 16) << 8 >> 8) / 8388608f,
                    4 => BinaryPrimitives.ReadInt32LittleEndian(value) / 2147483648f,
                    _ => 0
                };
        }
        SamplesAvailable?.Invoke(samples);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        Failed?.Invoke(args.Exception?.Message ?? "Acquisizione audio terminata. Premi Aggiorna per riconnettere la sorgente.");
    }

    private void OnSilenceStopped(object? sender, StoppedEventArgs args)
    {
        if (sender == _silenceRenderer && args.Exception != null) Failed?.Invoke(args.Exception.Message);
    }

    public void Stop()
    {
        var capture = _capture;
        _capture = null;
        if (capture != null)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            // NAudio waits for the capture worker before releasing its audio client.
            capture.Dispose();
        }
        var silence = _silenceRenderer;
        _silenceRenderer = null;
        if (silence != null)
        {
            silence.PlaybackStopped -= OnSilenceStopped;
            silence.Dispose();
        }
        _silenceDevice?.Dispose();
        _silenceDevice = null;
        _device?.Dispose();
        _device = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
    }
}
