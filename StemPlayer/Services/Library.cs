using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using StemPlayer.Models;

namespace StemPlayer.Services;

/// <summary>Persists the track list as JSON; each track's stems live in &lt;libraryDir&gt;/&lt;trackId&gt;/.</summary>
public class Library
{
    readonly object _lock = new();
    readonly string _libraryDir;
    public List<Track> Tracks { get; private set; } = new();

    public Library(string libraryDir)
    {
        _libraryDir = libraryDir;
        try
        {
            if (File.Exists(Paths.LibraryJson))
                Tracks = JsonSerializer.Deserialize<List<Track>>(File.ReadAllText(Paths.LibraryJson)) ?? new();
        }
        catch { Tracks = new(); }
        // Drop entries whose stems have vanished from disk.
        Tracks = Tracks.Where(t => Directory.Exists(TrackDir(t))).ToList();
    }

    public string TrackDir(Track t) => Path.Combine(_libraryDir, t.Id);

    public void Add(Track t) { lock (_lock) { Tracks.Add(t); Save(); } }

    public void Remove(Track t)
    {
        lock (_lock) { Tracks.Remove(t); Save(); }
        try { Directory.Delete(TrackDir(t), true); } catch { }
    }

    public void Save()
    {
        lock (_lock)
            File.WriteAllText(Paths.LibraryJson, JsonSerializer.Serialize(Tracks, new JsonSerializerOptions { WriteIndented = true }));
    }
}
