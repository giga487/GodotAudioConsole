namespace AudioConsole.Core;

public enum DistortionMode { Soft, Hard }

public sealed record DistortionSettings(bool Enabled = false, DistortionMode Mode = DistortionMode.Soft,
    double DriveDb = 12, double MixPercent = 50, double OutputDb = -6)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Mode)) throw new ArgumentOutOfRangeException(nameof(Mode));
        if (!double.IsFinite(DriveDb) || DriveDb is < 0 or > 36)
            throw new ArgumentOutOfRangeException(nameof(DriveDb), "Drive: da 0 a 36 dB.");
        if (!double.IsFinite(MixPercent) || MixPercent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(MixPercent), "Mix: da 0 a 100%.");
        if (!double.IsFinite(OutputDb) || OutputDb is < -24 or > 6)
            throw new ArgumentOutOfRangeException(nameof(OutputDb), "Uscita distorsione: da -24 a +6 dB.");
    }
}

/// <summary>Symmetric waveshaping: tanh for soft saturation, a limiter for hard clipping.
/// The wet signal has its own output gain; dry stays intact at Mix=0 or bypass.
/// All parameters, mode changes and bypass crossfade over a 10ms time constant.</summary>
public sealed class VoiceDistortion
{
    public DistortionSettings Settings { get; private set; } = new();
    private int _channels;
    private double _drive, _output, _blend, _hard, _smoothing;
    private double _targetDrive = Math.Pow(10, 12d / 20), _targetOutput = Math.Pow(10, -6d / 20);

    public void Configure(DistortionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        Settings = settings;
        _targetDrive = Math.Pow(10, settings.DriveDb / 20);
        _targetOutput = Math.Pow(10, settings.OutputDb / 20);
    }

    public void Reset(AudioFormat format)
    {
        format.Validate();
        _channels = format.Channels;
        _smoothing = 1 - Math.Exp(-1d / (0.01 * format.SampleRate));
        _drive = _targetDrive;
        _output = _targetOutput;
        _blend = Settings.Enabled ? Settings.MixPercent / 100 : 0;
        _hard = Settings.Mode == DistortionMode.Hard ? 1 : 0;
    }

    public float ProcessSample(float sample, int channel)
    {
        if (_channels == 0 || channel < 0 || channel >= _channels) throw new InvalidOperationException("Formato distorsione non inizializzato.");
        if (channel == 0)
        {
            _drive += _smoothing * (_targetDrive - _drive);
            _output += _smoothing * (_targetOutput - _output);
            var targetBlend = Settings.Enabled ? Settings.MixPercent / 100 : 0;
            _blend += _smoothing * (targetBlend - _blend);
            if (targetBlend == 0 && _blend < 1e-6) _blend = 0;
            _hard += _smoothing * ((Settings.Mode == DistortionMode.Hard ? 1 : 0) - _hard);
        }
        var dry = float.IsFinite(sample) ? sample : 0;
        if (_blend == 0) return dry;
        var driven = dry * _drive;
        var soft = Math.Tanh(driven);
        var shaped = soft + _hard * (Math.Clamp(driven, -1, 1) - soft);
        return (float)Math.Clamp(dry + _blend * (shaped * _output - dry), -float.MaxValue, float.MaxValue);
    }
}
