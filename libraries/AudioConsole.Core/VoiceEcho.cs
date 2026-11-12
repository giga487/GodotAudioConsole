namespace AudioConsole.Core;

public sealed record EchoSettings(bool Enabled = false, double DelayMs = 250, double FeedbackPercent = 35,
    double AmountPercent = 30)
{
    public void Validate()
    {
        if (!double.IsFinite(DelayMs) || DelayMs is < 40 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(DelayMs), "Il ritardo deve essere tra 40 e 1000 ms.");
        if (!double.IsFinite(FeedbackPercent) || FeedbackPercent is < 0 or > 85)
            throw new ArgumentOutOfRangeException(nameof(FeedbackPercent), "Il feedback deve essere tra 0 e 85%.");
        if (!double.IsFinite(AmountPercent) || AmountPercent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(AmountPercent), "La quantità echo deve essere tra 0 e 100%.");
    }
}

/// <summary>Bounded mono/stereo feedback delay, owned and serialized by the application service.
/// Adds delayed audio to the original. Parameter smoothing and tap crossfades run without allocations.</summary>
public sealed class VoiceEcho
{
    public EchoSettings Settings { get; private set; } = new();
    private double[] _buffer = [];
    private int _sampleRate, _channels, _capacity, _writeFrame, _delayFrames, _nextDelayFrames;
    private double _wet, _feedback, _smoothing, _tapFade = 1, _fadeStep;
    private bool _bypassed = true;

    public void Configure(EchoSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        Settings = settings;
    }

    public void Reset(AudioFormat format)
    {
        format.Validate();
        _sampleRate = format.SampleRate;
        _channels = format.Channels;
        _capacity = checked(_sampleRate + 1); // Maximum delay is one second, plus the current frame.
        var size = checked(_capacity * _channels);
        if (_buffer.Length != size) _buffer = new double[size];
        else Array.Clear(_buffer);
        _writeFrame = 0;
        _delayFrames = _nextDelayFrames = DesiredDelay();
        _tapFade = 1;
        _fadeStep = 1 / Math.Max(1, 0.02 * _sampleRate);
        _smoothing = 1 - Math.Exp(-1 / (0.01 * _sampleRate));
        _wet = Settings.Enabled ? Settings.AmountPercent / 100 : 0;
        _feedback = Settings.FeedbackPercent / 100;
        _bypassed = !Settings.Enabled;
    }

    private int DesiredDelay() => Math.Clamp((int)Math.Round(Settings.DelayMs * _sampleRate / 1000), 1, _capacity - 1);

    public float ProcessSample(float sample, int channel)
    {
        if (_sampleRate == 0 || channel < 0 || channel >= _channels) throw new InvalidOperationException("Formato echo non inizializzato.");
        var dry = float.IsFinite(sample) ? sample : 0;
        if (channel == 0)
        {
            var target = Settings.Enabled ? Settings.AmountPercent / 100 : 0;
            _wet += _smoothing * (target - _wet);
            _feedback += _smoothing * (Settings.FeedbackPercent / 100 - _feedback);
            if (!Settings.Enabled && _wet < 1e-6)
            {
                _wet = 0;
                if (!_bypassed) Array.Clear(_buffer);
                _bypassed = true;
            }
            if (Settings.Enabled) _bypassed = false;
            if (_tapFade >= 1)
            {
                var desired = DesiredDelay();
                if (desired != _delayFrames) { _nextDelayFrames = desired; _tapFade = 0; }
            }
        }
        if (_bypassed) return dry;
        var delayed = Read(_delayFrames, channel);
        if (_tapFade < 1) delayed += _tapFade * (Read(_nextDelayFrames, channel) - delayed);
        var stored = (Settings.Enabled ? (double)dry : 0) + _feedback * delayed;
        _buffer[_writeFrame * _channels + channel] = Math.Abs(stored) < 1e-25 ? 0 : stored;
        var result = (float)Math.Clamp(dry + _wet * delayed, -float.MaxValue, float.MaxValue);
        if (channel == _channels - 1)
        {
            _writeFrame = (_writeFrame + 1) % _capacity;
            if (_tapFade < 1)
            {
                _tapFade = Math.Min(1, _tapFade + _fadeStep);
                if (_tapFade >= 1) _delayFrames = _nextDelayFrames;
            }
        }
        return result;
    }

    private double Read(int delay, int channel) => _buffer[((_writeFrame - delay + _capacity) % _capacity) * _channels + channel];
}
