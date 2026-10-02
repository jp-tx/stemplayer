using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using StemPlayer.Models;
using StemPlayer.ViewModels;

namespace StemPlayer.Services;

public enum ReuseDecision { Reuse, Resplit, Cancel }

/// <summary>A cached split exists on disk but isn't in the current library, so the caller must decide what to do.</summary>
public record CacheConflict(string Title, string Dir, string Model);

/// <summary>Runs the import pipeline (copy or download, then separate) one song at a time.</summary>
public class ImportService
{
    readonly Library _library;
    readonly AppSettings _settings;
    readonly PythonEnv _env;
    readonly SeparatorService _separator;
    readonly YouTubeService _youtube;
    readonly BeatDetectionService _beats;
    readonly SplitCache _cache;
    readonly SemaphoreSlim _gate;

    public event Action<Track>? TrackImported;
    public event Action<string>? LogLine;

    /// <summary>Asked when a cached split exists on disk but isn't in the current library. Must marshal to the
    /// UI thread itself (this runs on a background thread).</summary>
    public Func<CacheConflict, Task<ReuseDecision>>? ResolveConflict;

    public ImportService(Library library, AppSettings settings, PythonEnv env)
    {
        _library = library; _settings = settings; _env = env;
        _separator = new SeparatorService(env, settings);
        _youtube = new YouTubeService(env);
        _beats = new BeatDetectionService(env);
        _cache = new SplitCache();
        _gate = new SemaphoreSlim(Math.Max(1, settings.MaxConcurrentImports));
    }

    static void UI(Action a) => Dispatcher.UIThread.Post(a);

    public Task ImportFileAsync(string path, ImportItemViewModel item) =>
        Run(item, ct => HashFileKeyAsync(path, ct), async (track, dir, ct) =>
        {
            Set(item, "Copying", 0.02);
            var ext = Path.GetExtension(path);
            var src = Path.Combine(dir, "source" + ext);
            File.Copy(path, src, true);
            ReadTags(track, src, Path.GetFileNameWithoutExtension(path));
            UI(() => item.Title = string.IsNullOrEmpty(track.Artist) ? track.Title : $"{track.Artist} - {track.Title}");
            await Split(track, src, dir, item, 0.02, ct);
        });

    public Task ImportYouTubeAsync(string url, ImportItemViewModel item) =>
        Run(item, _ => Task.FromResult("yt:" + url.Trim()), async (track, dir, ct) =>
        {
            track.SourceUrl = url;
            Set(item, "Downloading from YouTube", 0);
            var r = await _youtube.DownloadAsync(url, dir,
                p => Set(item, "Downloading from YouTube", p * 0.15),
                l => LogLine?.Invoke(l), ct);
            ReadTags(track, r.Mp3Path, r.Title);
            if (string.IsNullOrWhiteSpace(track.Title) || track.Title == "source") track.Title = r.Title;
            if (string.IsNullOrWhiteSpace(track.Artist)) track.Artist = r.Artist;
            if (track.DurationSeconds <= 0) track.DurationSeconds = r.Duration;
            UI(() => item.Title = string.IsNullOrEmpty(track.Artist) ? track.Title : $"{track.Artist} - {track.Title}");
            await Split(track, r.Mp3Path, dir, item, 0.15, ct);
        });

    static async Task<string> HashFileKeyAsync(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        return "file:" + Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
    }

    async Task Split(Track track, string source, string dir, ImportItemViewModel item, double baseline, CancellationToken ct)
    {
        track.Model = _settings.Model;
        string stage = "Loading model";
        Set(item, stage, baseline, indeterminate: true);
        track.Stems = await _separator.SeparateAsync(source, dir,
            p => Set(item, stage, baseline + p * (1 - baseline)),
            s => { stage = s; Set(item, s, null, indeterminate: s != "Separating"); },
            l => LogLine?.Invoke(l), ct);

        Set(item, "Detecting beats", 0.98, indeterminate: true);
        ApplyBeatInfo(track, await _beats.DetectAsync(source, AccentFile(track, dir), l => LogLine?.Invoke(l), ct));
    }

    static string? AccentFile(Track track, string dir) =>
        track.Stems.TryGetValue("drums", out var rel) ? Path.Combine(dir, rel) : null;

    static void ApplyBeatInfo(Track track, BeatInfo info)
    {
        track.BeatsMs = info.BeatsMs;
        track.Tempo = info.Tempo;
        if (info.BeatsPerBar is { } bpb) { track.BeatsPerBar = bpb; track.MeterConfidence = info.MeterConfidence; }
    }

    async Task Run(ImportItemViewModel item, Func<CancellationToken, Task<string>> keyFor, Func<Track, string, CancellationToken, Task> body)
    {
        var ct = item.Cts.Token;
        string? dir = null;
        try
        {
            Set(item, "Checking", 0, indeterminate: true);
            var key = await keyFor(ct);
            if (await TryUseCache(key, item)) return;

            var track = new Track { Title = item.Title };
            dir = Path.Combine(SettingsStore.LibraryDir(_settings), track.Id);
            Directory.CreateDirectory(dir);

            await _gate.WaitAsync(ct);
            try { await body(track, dir, ct); }
            finally { _gate.Release(); }

            _library.Add(track);
            _cache.Set(key, track.Id, track.Model);
            UI(() => { item.Stage = "Done"; item.Progress = 1; item.IsIndeterminate = false; TrackImported?.Invoke(track); });
        }
        catch (OperationCanceledException)
        {
            UI(() => { item.IsFailed = true; item.Stage = "Cancelled"; item.IsIndeterminate = false; });
            if (dir != null) try { Directory.Delete(dir, true); } catch { }
        }
        catch (Exception e)
        {
            UI(() => { item.IsFailed = true; item.Stage = "Failed"; item.Error = e.Message; item.IsIndeterminate = false; });
            LogLine?.Invoke(e.Message);
            if (dir != null) try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <returns>True if the import is already handled (already in library, reused, or cancelled) and
    /// the caller should not run the normal import pipeline.</returns>
    async Task<bool> TryUseCache(string key, ImportItemViewModel item)
    {
        var cached = _cache.Find(key);
        if (cached == null) return false;

        if (_library.Tracks.Any(t => t.Id == cached.TrackId))
        {
            UI(() => { item.Stage = "Already in library"; item.Progress = 1; item.IsIndeterminate = false; });
            return true;
        }

        var oldDir = _library.TrackDir(cached.TrackId);
        if (!Directory.Exists(oldDir) || !Directory.EnumerateFiles(oldDir, "*.wav").Any())
        {
            _cache.Remove(key); // stale: the folder is gone
            return false;
        }

        var decision = ResolveConflict == null
            ? ReuseDecision.Resplit
            : await ResolveConflict(new CacheConflict(item.Title, oldDir, cached.Model));

        if (decision == ReuseDecision.Cancel)
        {
            UI(() => { item.IsFailed = true; item.Stage = "Cancelled"; item.IsIndeterminate = false; });
            return true;
        }
        if (decision == ReuseDecision.Reuse)
        {
            var track = BuildFromExisting(cached.TrackId, oldDir, cached.Model);
            _library.Add(track);
            UI(() =>
            {
                item.Title = string.IsNullOrEmpty(track.Artist) ? track.Title : $"{track.Artist} - {track.Title}";
                item.Stage = "Done"; item.Progress = 1; item.IsIndeterminate = false;
                TrackImported?.Invoke(track);
            });
            return true;
        }

        // Resplit: the orphaned folder is superseded by a fresh split under a new id.
        try { Directory.Delete(oldDir, true); } catch { }
        return false;
    }

    static Track BuildFromExisting(string trackId, string dir, string model)
    {
        var track = new Track { Id = trackId, Model = model };
        var source = Directory.EnumerateFiles(dir, "source.*").FirstOrDefault();
        if (source != null) ReadTags(track, source, "Imported track");
        else track.Title = "Imported track";
        foreach (var f in Directory.EnumerateFiles(dir, "*.wav"))
            track.Stems[Path.GetFileNameWithoutExtension(f)] = Path.GetFileName(f);
        return track;
    }

    /// <summary>Fills in beats/meter for any already-split tracks that don't have them yet (imported
    /// before this feature existed, reused from an orphaned folder, or only partially analyzed by an
    /// older version). Runs quietly in the background, one track at a time sharing the same import
    /// gate; safe to call once at startup. Triggers on MeterConfidence being null rather than BeatsMs
    /// being empty, since that's the one field that can't come from a JSON default.</summary>
    public async Task BackfillBeatsAsync(CancellationToken ct = default)
    {
        foreach (var track in _library.Tracks.ToList())
        {
            if (ct.IsCancellationRequested) return;
            if (track.MeterConfidence != null) continue;
            var dir = _library.TrackDir(track);
            var source = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "source.*").FirstOrDefault() : null;
            if (source == null) continue;

            await _gate.WaitAsync(ct);
            BeatInfo info;
            try { info = await _beats.DetectAsync(source, AccentFile(track, dir), l => LogLine?.Invoke(l), ct); }
            finally { _gate.Release(); }
            if (info.BeatsPerBar == null) continue; // detection failed; try again next startup
            ApplyBeatInfo(track, info);
            _library.Save();
        }
    }

    static void Set(ImportItemViewModel item, string stage, double? progress, bool indeterminate = false) =>
        UI(() => { item.Stage = stage; if (progress.HasValue) item.Progress = progress.Value; item.IsIndeterminate = indeterminate; });

    static void ReadTags(Track t, string path, string fallbackTitle)
    {
        try
        {
            using var f = TagLib.File.Create(path);
            t.Title = string.IsNullOrWhiteSpace(f.Tag.Title) ? fallbackTitle : f.Tag.Title;
            t.Artist = f.Tag.FirstPerformer ?? t.Artist;
            t.Album = f.Tag.Album ?? "";
            t.DurationSeconds = f.Properties.Duration.TotalSeconds;
        }
        catch { t.Title = string.IsNullOrWhiteSpace(t.Title) ? fallbackTitle : t.Title; }
    }
}
