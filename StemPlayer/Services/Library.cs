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
    public List<TrackList> Lists { get; private set; } = new();

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

        try
        {
            if (File.Exists(Paths.ListsJson))
                Lists = JsonSerializer.Deserialize<List<TrackList>>(File.ReadAllText(Paths.ListsJson)) ?? new();
        }
        catch { Lists = new(); }
        var ids = Tracks.Select(t => t.Id).ToHashSet();
        var claimed = new HashSet<string>();
        foreach (var l in Lists) l.TrackIds = l.TrackIds.Where(id => ids.Contains(id) && claimed.Add(id)).ToList();
    }

    public string TrackDir(Track t) => TrackDir(t.Id);
    public string TrackDir(string trackId) => Path.Combine(_libraryDir, trackId);

    public void Add(Track t) { lock (_lock) { Tracks.Add(t); Save(); } }

    /// <summary>Removes the library entry only; the stem files stay on disk (orphaned until reused or resplit).</summary>
    public void RemoveEntryOnly(Track t)
    {
        lock (_lock)
        {
            Tracks.Remove(t);
            foreach (var l in Lists) l.TrackIds.Remove(t.Id);
            Save(); SaveLists();
        }
    }

    public void Remove(Track t)
    {
        RemoveEntryOnly(t);
        try { Directory.Delete(TrackDir(t), true); } catch { }
    }

    public void Save()
    {
        lock (_lock)
            File.WriteAllText(Paths.LibraryJson, JsonSerializer.Serialize(Tracks, new JsonSerializerOptions { WriteIndented = true }));
    }

    // ---------- lists ----------

    public TrackList? ListOf(string trackId) => Lists.FirstOrDefault(l => l.TrackIds.Contains(trackId));

    public TrackList? FindList(string name) =>
        Lists.FirstOrDefault(l => string.Equals(l.Name, name.Trim(), System.StringComparison.OrdinalIgnoreCase));

    public TrackList CreateList(string name)
    {
        lock (_lock)
        {
            var l = new TrackList { Name = name.Trim() };
            Lists.Add(l); SaveLists();
            return l;
        }
    }

    /// <summary>Moves the track into <paramref name="list"/> (appended), removing it from any list it was in.</summary>
    public void AddToList(TrackList list, Track t)
    {
        lock (_lock)
        {
            foreach (var l in Lists) l.TrackIds.Remove(t.Id);
            list.TrackIds.Add(t.Id); SaveLists();
        }
    }

    public void RemoveFromList(Track t)
    {
        lock (_lock) { foreach (var l in Lists) l.TrackIds.Remove(t.Id); SaveLists(); }
    }

    public void DeleteList(TrackList list) { lock (_lock) { Lists.Remove(list); SaveLists(); } }

    public void SaveLists()
    {
        lock (_lock)
            File.WriteAllText(Paths.ListsJson, JsonSerializer.Serialize(Lists, new JsonSerializerOptions { WriteIndented = true }));
    }
}
