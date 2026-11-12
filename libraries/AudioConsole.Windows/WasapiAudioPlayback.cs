using System.Runtime.Versioning;
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
    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing && _error == null;
    public bool IsPaused => _output?.PlaybackState == PlaybackState.Paused && _error == null;
    public string? Error => _error;

    public void Play(string wavPath)
    {
        Stop();
        _error = null;
        try
        {
            _reader = new WaveFileReader(wavPath);
            _output = new WasapiOut(AudioClientShareMode.Shared, useEventSync: true, latency: 100);
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
        if (IsPlaying) _output!.Pause();
    }

    public void Resume()
    {
        if (IsPaused) _output!.Play();
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
    }

    public void Dispose() => Stop();
}
