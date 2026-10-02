using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using StemPlayer.Models;
using StemPlayer.ViewModels;

namespace StemPlayer.Services;

/// <summary>Runs the import pipeline (copy or download, then separate) one song at a time.</summary>
public class ImportService
{
    readonly Library _library;
    readonly AppSettings _settings;
    readonly PythonEnv _env;
    readonly SeparatorService _separator;
    readonly YouTubeService _youtube;
    readonly SemaphoreSlim _gate;

    public event Action<Track>? TrackImported;
    public event Action<string>? LogLine;

    public ImportService(Library library, AppSettings settings, PythonEnv env)
    {
        _library = library; _settings = settings; _env = env;
        _separator = new SeparatorService(env, settings);
        _youtube = new YouTubeService(env);
        _gate = new SemaphoreSlim(Math.Max(1, settings.MaxConcurrentImports));
    }

    static void UI(Action a) => Dispatcher.UIThread.Post(a);

    public Task ImportFileAsync(string path, ImportItemViewModel item) =>
        Run(item, async (track, dir, ct) =>
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
        Run(item, async (track, dir, ct) =>
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

    async Task Split(Track track, string source, string dir, ImportItemViewModel item, double baseline, CancellationToken ct)
    {
        track.Model = _settings.Model;
        string stage = "Loading model";
        Set(item, stage, baseline, indeterminate: true);
        track.Stems = await _separator.SeparateAsync(source, dir,
            p => Set(item, stage, baseline + p * (1 - baseline)),
            s => { stage = s; Set(item, s, null, indeterminate: s != "Separating"); },
            l => LogLine?.Invoke(l), ct);
    }

    async Task Run(ImportItemViewModel item, Func<Track, string, CancellationToken, Task> body)
    {
        var track = new Track { Title = item.Title };
        var dir = Path.Combine(SettingsStore.LibraryDir(_settings), track.Id);
        Directory.CreateDirectory(dir);
        var ct = item.Cts.Token;
        try
        {
            await _gate.WaitAsync(ct);
            try { await body(track, dir, ct); }
            finally { _gate.Release(); }

            _library.Add(track);
            UI(() => { item.Stage = "Done"; item.Progress = 1; item.IsIndeterminate = false; TrackImported?.Invoke(track); });
        }
        catch (OperationCanceledException)
        {
            UI(() => { item.IsFailed = true; item.Stage = "Cancelled"; item.IsIndeterminate = false; });
            try { Directory.Delete(dir, true); } catch { }
        }
        catch (Exception e)
        {
            UI(() => { item.IsFailed = true; item.Stage = "Failed"; item.Error = e.Message; item.IsIndeterminate = false; });
            LogLine?.Invoke(e.Message);
            try { Directory.Delete(dir, true); } catch { }
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
