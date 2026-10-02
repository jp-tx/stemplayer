using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace StemPlayer.Services;

public class CacheEntry
{
    public string Key { get; set; } = "";
    public string TrackId { get; set; } = "";
    public string Model { get; set; } = "";
}

/// <summary>
/// Remembers which source (an imported file's hash, or a YouTube URL) produced which track, so a
/// reimport can offer to reuse stems that are still on disk instead of resplitting from scratch.
/// </summary>
public class SplitCache
{
    readonly object _lock = new();
    List<CacheEntry> _entries;

    public SplitCache()
    {
        try { _entries = File.Exists(Paths.CacheJson) ? JsonSerializer.Deserialize<List<CacheEntry>>(File.ReadAllText(Paths.CacheJson)) ?? new() : new(); }
        catch { _entries = new(); }
    }

    public CacheEntry? Find(string key) { lock (_lock) return _entries.FirstOrDefault(e => e.Key == key); }

    public void Set(string key, string trackId, string model)
    {
        lock (_lock)
        {
            _entries.RemoveAll(e => e.Key == key);
            _entries.Add(new CacheEntry { Key = key, TrackId = trackId, Model = model });
            Save();
        }
    }

    public void Remove(string key)
    {
        lock (_lock) { _entries.RemoveAll(e => e.Key == key); Save(); }
    }

    void Save() => File.WriteAllText(Paths.CacheJson, JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true }));
}
