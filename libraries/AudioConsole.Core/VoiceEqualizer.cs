namespace AudioConsole.Core;

public sealed record EqualizerSettings(bool Enabled = false, double BassDb = 0, double MidDb = 0,
    double TrebleDb = 0, double OutputDb = 0)
{
    public void Validate()
    {
        foreach (var value in new[] { BassDb, MidDb, TrebleDb, OutputDb })
            if (!double.IsFinite(value) || value is < -12 or > 12)
                throw new ArgumentOutOfRangeException(nameof(value), "L'equalizzatore accetta valori tra -12 e +12 dB.");
    }
}

/// <summary>Three RBJ biquads: 160 Hz low shelf, 1 kHz peak (Q 0.7), 4 kHz high shelf.
/// No engine dependencies or per-sample allocations. The owner serializes settings and processing.
/// Formula reference: https://www.w3.org/TR/audio-eq-cookbook/ .</summary>
public sealed class VoiceEqualizer
{
    private readonly Biquad[] _filters = Enumerable.Range(0, 6).Select(_ => new Biquad()).ToArray();
    public EqualizerSettings Settings { get; private set; } = new();
    private int _sampleRate, _channels, _frames;
    private double _bass, _mid, _treble, _output, _wet, _parameterSmoothing, _frameSmoothing;
    private double _targetOutput = 1;

    public void Configure(EqualizerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        Settings = settings;
        _targetOutput = Math.Pow(10, settings.OutputDb / 20);
    }

    public void Reset(AudioFormat format)
    {
        format.Validate();
        _sampleRate = format.SampleRate;
        _channels = format.Channels;
        _frames = 0;
        _bass = Settings.BassDb; _mid = Settings.MidDb; _treble = Settings.TrebleDb;
        _output = _targetOutput;
        _wet = Settings.Enabled ? 1 : 0;
        _parameterSmoothing = 1 - Math.Exp(-32d / (0.02 * _sampleRate));
        _frameSmoothing = 1 - Math.Exp(-1d / (0.01 * _sampleRate));
        foreach (var filter in _filters) filter.Reset();
        UpdateCoefficients();
    }

    public float ProcessSample(float sample, int channel)
    {
        if (_sampleRate == 0 || channel < 0 || channel >= _channels) throw new InvalidOperationException("Formato EQ non inizializzato.");
        if (channel == 0)
        {
            _wet += _frameSmoothing * ((Settings.Enabled ? 1 : 0) - _wet);
            if (!Settings.Enabled && _wet < 1e-6) _wet = 0;
            _output += _frameSmoothing * (_targetOutput - _output);
            if (++_frames == 32)
            {
                _frames = 0;
                _bass += _parameterSmoothing * (Settings.BassDb - _bass);
                _mid += _parameterSmoothing * (Settings.MidDb - _mid);
                _treble += _parameterSmoothing * (Settings.TrebleDb - _treble);
                UpdateCoefficients();
            }
        }
        var dry = float.IsFinite(sample) ? sample : 0;
        var value = (double)dry;
        for (var band = 0; band < 3; band++) value = _filters[channel * 3 + band].Process(value);
        return _wet == 0 ? dry : (float)Math.Clamp(dry + _wet * (value * _output - dry), -float.MaxValue, float.MaxValue);
    }

    private void UpdateCoefficients()
    {
        for (var channel = 0; channel < _channels; channel++)
        {
            _filters[channel * 3].Configure(_sampleRate, 160, _bass, -1);
            _filters[channel * 3 + 1].Configure(_sampleRate, 1000, _mid, 0);
            _filters[channel * 3 + 2].Configure(_sampleRate, 4000, _treble, 1);
        }
    }

    private sealed class Biquad
    {
        private double _b0, _b1, _b2, _a1, _a2, _x1, _x2, _y1, _y2;
        public void Reset() => _x1 = _x2 = _y1 = _y2 = 0;
        public double Process(double value)
        {
            var result = _b0 * value + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
            _x2 = _x1; _x1 = value; _y2 = _y1; _y1 = Math.Abs(result) < 1e-25 ? 0 : result;
            return result;
        }

        public void Configure(int rate, double frequency, double db, int shelf)
        {
            var w = 2 * Math.PI * Math.Min(frequency, rate * 0.4) / rate;
            var c = Math.Cos(w); var s = Math.Sin(w); var a = Math.Pow(10, db / 40);
            double b0, b1, b2, a0, a1, a2;
            if (shelf == 0)
            {
                var alpha = s / (2 * 0.7);
                b0 = 1 + alpha * a; b1 = -2 * c; b2 = 1 - alpha * a;
                a0 = 1 + alpha / a; a1 = -2 * c; a2 = 1 - alpha / a;
            }
            else
            {
                var beta = s * Math.Sqrt(2 * a); // slope S = 1
                if (shelf < 0)
                {
                    b0 = a * (a + 1 - (a - 1) * c + beta);
                    b1 = 2 * a * (a - 1 - (a + 1) * c);
                    b2 = a * (a + 1 - (a - 1) * c - beta);
                    a0 = a + 1 + (a - 1) * c + beta;
                    a1 = -2 * (a - 1 + (a + 1) * c);
                    a2 = a + 1 + (a - 1) * c - beta;
                }
                else
                {
                    b0 = a * (a + 1 + (a - 1) * c + beta);
                    b1 = -2 * a * (a - 1 + (a + 1) * c);
                    b2 = a * (a + 1 + (a - 1) * c - beta);
                    a0 = a + 1 - (a - 1) * c + beta;
                    a1 = 2 * (a - 1 - (a + 1) * c);
                    a2 = a + 1 - (a - 1) * c - beta;
                }
            }
            _b0 = b0 / a0; _b1 = b1 / a0; _b2 = b2 / a0; _a1 = a1 / a0; _a2 = a2 / a0;
        }
    }
}
