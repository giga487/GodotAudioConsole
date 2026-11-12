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
    private WasapiCapture? _capture;
    private MMDevice? _device;
    private bool _disposed;

    public event Action<ReadOnlyMemory<float>>? SamplesAvailable;
    public event Action<string>? Failed;

    public IReadOnlyList<AudioInputDevice> GetDevices()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var enumerator = new MMDeviceEnumerator();
        var result = new List<AudioInputDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (device) result.Add(new(device.ID, device.FriendlyName));
        }
        if (result.Count > 0) result.Insert(0, new(DefaultDeviceId, "Predefinito di sistema"));
        return result;
    }

    public AudioFormat Start(string deviceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            _device = deviceId == DefaultDeviceId
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console)
                : enumerator.GetDevice(deviceId);
            // Native mix format avoids imposing an unsupported sample rate on hardware.
            _capture = new WasapiCapture(_device, useEventSync: true, audioBufferMillisecondsLength: 30);
            var format = _capture.WaveFormat;
            ValidateFormat(format);
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _capture.StartRecording();
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
        throw new NotSupportedException($"Formato microfono non supportato: {format}.");
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
        Failed?.Invoke(args.Exception?.Message ?? "Acquisizione audio terminata. Premi Aggiorna per riconnettere il microfono.");
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
