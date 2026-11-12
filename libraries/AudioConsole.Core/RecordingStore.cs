namespace AudioConsole.Core;

/// <summary>Owns only its private session files; exported user files are never deleted.</summary>
internal sealed class RecordingStore : IDisposable
{
    public const int Capacity = 10;
    private sealed record Entry(RecordingInfo Info, string Path);
    private readonly List<Entry> _entries = [];
    private readonly string _directory;
    private int _sequence;

    public RecordingStore(string? temporaryRoot)
    {
        _directory = Path.Combine(temporaryRoot ?? Path.GetTempPath(), "GodotAudioConsole", Guid.NewGuid().ToString("N"));
    }

    public RecordingInfo[] GetRecordings() => _entries.Select(entry => entry.Info).ToArray();
    public RecordingInfo? Find(Guid? id) => _entries.Find(entry => entry.Info.Id == id)?.Info;
    public string GetPath(Guid id) => _entries.Find(entry => entry.Info.Id == id)?.Path
        ?? throw new ArgumentException("Registrazione non disponibile.", nameof(id));

    public RecordingInfo Add(AudioFormat format, ReadOnlySpan<byte> pcm, TimeSpan duration, Guid? preserveId = null, string? label = null)
    {
        Directory.CreateDirectory(_directory);
        var id = Guid.NewGuid();
        var path = Path.Combine(_directory, id.ToString("N") + ".wav");
        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                WavWriter.Write(stream, format, pcm);
            if (_entries.Count == Capacity)
            {
                var oldest = _entries.FindLastIndex(entry => entry.Info.Id != preserveId);
                File.Delete(_entries[oldest].Path);
                _entries.RemoveAt(oldest);
            }
        }
        catch
        {
            if (File.Exists(path)) File.Delete(path);
            throw;
        }
        var info = new RecordingInfo(id, $"{label ?? "Registrazione"} {++_sequence:000}", DateTimeOffset.Now, duration, null);
        _entries.Insert(0, new(info, path));
        return info;
    }

    public string Export(Guid id, string path)
    {
        var index = _entries.FindIndex(entry => entry.Info.Id == id);
        if (index < 0) throw new ArgumentException("Registrazione non disponibile.", nameof(id));
        var entry = _entries[index];
        if (!string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase)) path += ".wav";
        path = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(_directory, path);
        if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("Scegli una cartella esterna ai file temporanei della sessione.");
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            // Copy bytes only: source temp folders may have filesystem encryption attributes.
            using (var source = File.OpenRead(entry.Path))
            using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
                source.CopyTo(destination);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        _entries[index] = entry with { Info = entry.Info with { ExportPath = path } };
        return path;
    }

    public void Dispose()
    {
        foreach (var entry in _entries)
        {
            try { File.Delete(entry.Path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        _entries.Clear();
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
