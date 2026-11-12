using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioConsole.Core;

public sealed record LastSetup(int Version, double MasterVolumePercent, AudioSourceSettings[] Sources,
    AudioRedirectSettings? Redirect = null, EqualizerSettings? Equalizer = null, EchoSettings? Echo = null);
public sealed record SetupRestoreResult(int RestoredCount, string[] Warnings);

/// <summary>Versioned device/gain settings only: no recordings, playback or Godot objects.</summary>
public sealed class LastSetupStore
{
    public const int CurrentVersion = 1;
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GodotAudioConsole", "last-setup.json");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        IgnoreReadOnlyProperties = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public string FilePath { get; }
    public LastSetupStore(string? path = null) => FilePath = Path.GetFullPath(path ?? DefaultPath);

    public static LastSetup Capture(ConsoleSnapshot state) => new(CurrentVersion, state.VolumePercent,
        state.Sources.Select(source => new AudioSourceSettings(source.Device, source.VolumePercent)).ToArray(),
        new(state.Redirect.Mode, state.Redirect.Destination), state.Equalizer, state.Echo);

    public LastSetup? Load()
    {
        if (!File.Exists(FilePath)) return null;
        using var stream = File.OpenRead(FilePath);
        var setup = JsonSerializer.Deserialize<LastSetup>(stream, JsonOptions)
            ?? throw new InvalidDataException("Il file last setup è vuoto.");
        Validate(setup);
        return setup;
    }

    public void Save(LastSetup setup)
    {
        Validate(setup);
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".last-setup.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                JsonSerializer.Serialize(stream, setup, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static void Validate(LastSetup setup)
    {
        if (setup.Version != CurrentVersion) throw new InvalidDataException("Versione del last setup non supportata.");
        StereoMixer.ValidateGain(setup.MasterVolumePercent);
        setup.Equalizer?.Validate();
        setup.Echo?.Validate();
        if (setup.Sources == null) throw new InvalidDataException("Lista delle sorgenti mancante.");
        if (setup.Redirect != null && (!Enum.IsDefined(setup.Redirect.Mode) ||
            setup.Redirect.Destination != null && string.IsNullOrWhiteSpace(setup.Redirect.Destination.Id)))
            throw new InvalidDataException("Configurazione invio audio non valida.");
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in setup.Sources)
        {
            if (source?.Device == null || string.IsNullOrWhiteSpace(source.Device.Id) ||
                string.IsNullOrWhiteSpace(source.Device.Name) || !Enum.IsDefined(source.Device.Source) ||
                (source.Device.CanonicalId != null && string.IsNullOrWhiteSpace(source.Device.CanonicalId)))
                throw new InvalidDataException("Sorgente audio non valida nel last setup.");
            StereoMixer.ValidateGain(source.VolumePercent);
            if (!identities.Add(source.Device.Identity)) throw new InvalidDataException("Sorgente duplicata nel last setup.");
        }
    }

    public static SetupRestoreResult Restore(AudioConsoleService service, LastSetup setup)
    {
        Validate(setup);
        var warnings = new List<string>();
        service.SetEqualizer(setup.Equalizer ?? new());
        service.SetEcho(setup.Echo ?? new());
        if (setup.Redirect != null)
        {
            try
            {
                var savedDestination = setup.Redirect.Destination;
                var destination = savedDestination == null ? null : service.GetRedirectDevices().FirstOrDefault(d => d.Id == savedDestination.Id);
                if (savedDestination != null && destination == null) warnings.Add(savedDestination.Name + ": uscita di invio non disponibile.");
                service.ConfigureRedirect(destination, setup.Redirect.Mode);
            }
            catch (Exception error) { warnings.Add("Invio audio: " + error.Message); }
        }
        var resolved = new List<AudioSourceSettings>();
        foreach (var group in setup.Sources.GroupBy(source => source.Device.Source))
        {
            IReadOnlyList<AudioInputDevice> available;
            try { available = service.GetDevices(group.Key); }
            catch (Exception error) { warnings.Add($"{group.Key}: {error.Message}"); continue; }
            foreach (var saved in group)
            {
                // Match the actual endpoint; never silently replace it with another default device.
                var device = available.FirstOrDefault(device => device.Identity == saved.Device.Identity);
                if (device == null) warnings.Add(saved.Device.Name + ": dispositivo non disponibile.");
                else resolved.Add(new(device, saved.VolumePercent));
            }
        }
        var ordering = setup.Sources.Select((source, index) => (source.Device.Identity, index))
            .ToDictionary(item => item.Identity, item => item.index);
        resolved.Sort((left, right) => ordering[left.Device.Identity].CompareTo(ordering[right.Device.Identity]));
        service.SetVolume(setup.MasterVolumePercent);
        try
        {
            service.ConfigureSources(resolved);
            return new(resolved.Count, warnings.ToArray());
        }
        catch
        {
            // An enumerated device may still fail to open. Keep the other working sources.
            service.ConfigureSources([]);
            var working = new List<AudioSourceSettings>();
            foreach (var candidate in resolved)
            {
                try { service.ConfigureSources(working.Append(candidate).ToArray()); working.Add(candidate); }
                catch (Exception error)
                {
                    warnings.Add(candidate.Device.Name + ": " + error.Message);
                    service.ConfigureSources(working);
                }
            }
            return new(working.Count, warnings.ToArray());
        }
    }
}
