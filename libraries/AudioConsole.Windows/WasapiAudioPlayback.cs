using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using AudioConsole.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioConsole.Windows;

[SupportedOSPlatform("windows")]
public sealed class WasapiAudioPlayback : IAudioPlayback
{
    private WasapiOut? _output;
    private WaveFileReader? _reader;
    private volatile string? _error;
    private TimeSpan _duration, _pausedPosition, _clockOffset;
    private readonly string? _outputDeviceId;
    private MMDevice? _device;
    public WasapiAudioPlayback(string? outputDeviceId = null) => _outputDeviceId = outputDeviceId;
    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing && _error == null;
    public bool IsPaused => _output?.PlaybackState == PlaybackState.Paused && _error == null;
    public string? Error => _error;
    public TimeSpan Position => _output == null ? TimeSpan.Zero : IsPaused ? _pausedPosition
        : IsPlaying ? TimeSpan.FromSeconds(Math.Clamp((RawPosition() - _clockOffset).TotalSeconds, 0, _duration.TotalSeconds))
        : _error == null ? _duration : TimeSpan.Zero;

    private TimeSpan RawPosition()
    {
        try
        {
            return _output == null ? TimeSpan.Zero
                : TimeSpan.FromSeconds((double)_output.GetPosition() / _output.OutputWaveFormat.AverageBytesPerSecond);
        }
        catch (COMException error)
        {
            _error = error.Message;
            return TimeSpan.Zero;
        }
    }

    public void Play(string wavPath)
    {
        Stop();
        _error = null;
        try
        {
            _reader = new WaveFileReader(wavPath);
            _duration = _reader.TotalTime;
            _pausedPosition = _clockOffset = TimeSpan.Zero;
            if (_outputDeviceId == null)
                _output = new WasapiOut(AudioClientShareMode.Shared, useEventSync: true, latency: 100);
            else
            {
                using var enumerator = new MMDeviceEnumerator();
                _device = enumerator.GetDevice(_outputDeviceId);
                _output = new WasapiOut(_device, AudioClientShareMode.Shared, useEventSync: true, latency: 100);
            }
            _output.PlaybackStopped += OnPlaybackStopped;
            _output.Init(_reader);
            _output.Play();
        }
        catch
        {
            Stop();
            throw;
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs args)
    {
        if (sender == _output) _error = args.Exception?.Message;
    }

    public void Pause()
    {
        if (!IsPlaying) return;
        _pausedPosition = Position;
        _output!.Pause();
    }

    public void Resume()
    {
        if (!IsPaused) return;
        _output!.Play();
        // The device clock keeps advancing while its stream is paused.
        _clockOffset = RawPosition() - _pausedPosition;
    }

    public void Stop()
    {
        if (_output != null)
        {
            _output.PlaybackStopped -= OnPlaybackStopped;
            _output.Dispose();
            _output = null;
        }
        _reader?.Dispose();
        _reader = null;
        _device?.Dispose();
        _device = null;
    }

    public void Dispose() => Stop();
}
