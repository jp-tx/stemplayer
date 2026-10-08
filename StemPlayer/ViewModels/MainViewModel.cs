using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StemPlayer.Models;
using StemPlayer.Services;
using StemPlayer.Views;

namespace StemPlayer.ViewModels;

public partial class MainViewModel : ViewModelBase, IDisposable
{
    public AppSettings Settings { get; private set; }
    Library _library;
    PythonEnv _env;
    ImportService _importer;
    OpenAlOutput? _output;
    StemMixer? _mixer;
    readonly DispatcherTimer _timer;
    bool _seeking;

    /// <summary>What the library list shows: list headers and tracks, already filtered/collapsed.</summary>
    public ObservableCollection<LibraryRow> Rows { get; } = new();
    readonly System.Collections.Generic.List<TrackItemViewModel> _items = new();
    public ObservableCollection<ImportItemViewModel> Imports { get; } = new();
    public ObservableCollection<string> Log { get; } = new();

    public StemFaderViewModel[] Faders { get; } =
    {
        new(StemGroup.Vocals, "Vocals", "🎤"),
        new(StemGroup.Drums, "Drums", "🥁"),
        new(StemGroup.Bass, "Bass", "🎻"),
        new(StemGroup.Guitar, "Guitar", "🎸"),
        new(StemGroup.Keys, "Keys", "🎹"),
        new(StemGroup.Other, "Everything else", "🎶"),
    };

    [ObservableProperty] public partial LibraryRow? SelectedRow { get; set; }
    public TrackItemViewModel? SelectedTrack => SelectedRow as TrackItemViewModel;
    public bool IsTrackSelected => SelectedRow is TrackItemViewModel;
    public bool IsListSelected => SelectedRow is ListHeaderViewModel;
    public bool SelectedTrackInList => SelectedTrack is { } t && _library.ListOf(t.Track.Id) != null;

    partial void OnSelectedRowChanged(LibraryRow? value)
    {
        OnPropertyChanged(nameof(SelectedTrack));
        OnPropertyChanged(nameof(IsTrackSelected));
        OnPropertyChanged(nameof(IsListSelected));
        OnPropertyChanged(nameof(SelectedTrackInList));
    }
    [ObservableProperty] public partial TrackItemViewModel? NowPlaying { get; set; }
    [ObservableProperty] public partial bool IsPlaying { get; set; }
    [ObservableProperty] public partial double Position { get; set; }
    [ObservableProperty] public partial double Duration { get; set; } = 1;
    [ObservableProperty] public partial double MasterVolume { get; set; } = 1.0;
    [ObservableProperty] public partial string YouTubeUrl { get; set; } = "";
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial string Search { get; set; } = "";

    public string PositionText => TimeSpan.FromSeconds(Position).ToString(@"m\:ss");
    public string DurationText => TimeSpan.FromSeconds(Duration).ToString(@"m\:ss");
    public string NowPlayingText => NowPlaying == null ? "Nothing playing" : $"{NowPlaying.Title}  —  {NowPlaying.Artist}";
    public string TempoText => HasMeter ? $"{TempoBpm:0} BPM" : "";
    public string MeterText => NowPlaying?.Track is { Tempo: > 0 } t ? $"{t.BeatsPerBar}/4 · {BarCount} bars" : "";
    public bool HasMeter => NowPlaying?.Track is { Tempo: > 0 };
    public string PlayGlyph => IsPlaying ? "⏸" : "▶";

    // ---------- bars / loop editor ----------

    [ObservableProperty] public partial bool LoopEnabled { get; set; }
    [ObservableProperty] public partial bool LoopByTime { get; set; }
    [ObservableProperty] public partial bool SplitBar { get; set; }
    /// <summary>Canonical loop region, in seconds, regardless of LoopByTime. When LoopByTime is off,
    /// the view snaps these to beat/bar times as the user drags; when on, they're free.</summary>
    [ObservableProperty] public partial double? LoopStartSeconds { get; set; }
    [ObservableProperty] public partial double? LoopEndSeconds { get; set; }

    public int BarCount => NowPlaying?.Track is { } t
        ? t.Segments.Sum(s => (s.EndBeatIndex - s.StartBeatIndex) / t.BeatsPerBar) : 0;

    public string LoopSelectionText
    {
        get
        {
            if (NowPlaying?.Track is not { } t || LoopStartSeconds is not { } s0 || LoopEndSeconds is not { } e0) return "";
            if (LoopByTime)
                return $"{TimeSpan.FromSeconds(s0):m\\:ss}  →  {TimeSpan.FromSeconds(e0):m\\:ss}";

            string Fmt(double seconds)
            {
                var bb = BarBeatForTime(t, seconds);
                if (bb == null) return "—";
                return SplitBar ? $"Bar {bb.Value.Bar}, Beat {bb.Value.Beat}" : $"Bar {bb.Value.Bar}";
            }
            return $"{Fmt(s0)}  →  {Fmt(e0)}";
        }
    }

    // ---------- tempo / real-time time-stretch ----------

    [ObservableProperty] public partial double TempoBpm { get; set; }

    const double TempoStepBpm = 5.0;

    [RelayCommand] void TempoUp() => ChangeTempo(TempoBpm + TempoStepBpm);
    [RelayCommand] void TempoDown() => ChangeTempo(TempoBpm - TempoStepBpm);
    [RelayCommand] void TempoReset() { if (NowPlaying?.Track is { Tempo: > 0 } t) ChangeTempo(t.Tempo); }

    /// <summary>Changes playback tempo instantly via SoundTouch inside OpenAlOutput - real-time, pitch
    /// preserved, no render step. Chosen over a higher-quality offline renderer (e.g. Rubber Band)
    /// specifically because responsiveness matters more here than pristine audio: the file itself
    /// never changes length, so Position/Duration/BeatsMs all stay in one timeline, nothing to rescale.</summary>
    void ChangeTempo(double newBpm)
    {
        if (NowPlaying?.Track is not { Tempo: > 0 } t) return;
        TempoBpm = Math.Round(Math.Clamp(newBpm, 20, 400));
        _output?.SetTempoRatio(TempoBpm / t.Tempo);
        OnPropertyChanged(nameof(TempoText));
    }

    /// <summary>The segment whose own bar grid is closest to a given time - used to anchor labeling/
    /// shifting for a point that may fall outside every segment (near a stop, or beyond the first/last
    /// detected beat entirely).</summary>
    static BeatSegment? NearestSegment(Track t, double seconds)
    {
        if (t.Segments.Count == 0) return null;
        BeatSegment best = t.Segments[0]; double bestD = double.MaxValue;
        foreach (var seg in t.Segments)
        {
            double segStart = t.BeatsMs[seg.StartBeatIndex] / 1000.0;
            double segEnd = t.BeatsMs[seg.EndBeatIndex - 1] / 1000.0;
            double d = seconds < segStart ? segStart - seconds : seconds > segEnd ? seconds - segEnd : 0;
            if (d < bestD) { bestD = d; best = seg; }
        }
        return best;
    }

    /// <summary>1-based bar and beat-within-bar for any time in the track, extrapolated from the
    /// nearest segment's own downbeat phase using the measured tempo - this is what makes a negative
    /// bar (before the first detected beat, e.g. a silent intro) or "last bar + N" (past the last
    /// detected beat, e.g. a fade-out) a well-defined position instead of just undefined space.</summary>
    static (int Bar, int Beat)? BarBeatForTime(Track t, double seconds)
    {
        if (NearestSegment(t, seconds) is not { } seg || t.Tempo <= 0) return null;
        double refTime = t.BeatsMs[seg.StartBeatIndex + seg.DownbeatOffset] / 1000.0;
        double beatSeconds = 60.0 / t.Tempo;
        int rel = (int)Math.Round((seconds - refTime) / beatSeconds);
        int bar = (int)Math.Floor(rel / (double)t.BeatsPerBar) + 1;
        int beat = ((rel % t.BeatsPerBar) + t.BeatsPerBar) % t.BeatsPerBar + 1;
        return (bar, beat);
    }

    /// <summary>Shifts which beat counts as "1" for whichever segment is nearest the current loop
    /// selection (or the whole track's first segment if no selection), correcting a detector phase
    /// error. The loop selection itself moves by exactly one tempo-beat in the same direction so
    /// "bar 1" (or whichever bar was selected) keeps tracking the same bar under the corrected grid -
    /// the underlying beat timestamps and audio are unaffected, only which beat each label refers to.</summary>
    [RelayCommand] void BeatShiftUp() => ShiftDownbeat(1);
    [RelayCommand] void BeatShiftDown() => ShiftDownbeat(-1);

    void ShiftDownbeat(int direction)
    {
        if (NowPlaying?.Track is not { } t || t.Tempo <= 0) return;
        var seg = (LoopStartSeconds is { } s ? NearestSegment(t, s) : null) ?? t.Segments.FirstOrDefault();
        if (seg == null) return;
        seg.DownbeatOffset = ((seg.DownbeatOffset + direction) % t.BeatsPerBar + t.BeatsPerBar) % t.BeatsPerBar;
        _library.Save();

        double beatSeconds = 60.0 / t.Tempo;
        if (LoopStartSeconds is { } s0) LoopStartSeconds = s0 + direction * beatSeconds;
        if (LoopEndSeconds is { } e0) LoopEndSeconds = e0 + direction * beatSeconds;

        OnPropertyChanged(nameof(LoopSelectionText));
        LoopGridChanged?.Invoke();
        ApplyLoopEditNow();
    }

    /// <summary>If the loop is enabled and actively playing, jump playback to the (possibly just-moved)
    /// loop start right away instead of waiting for the old boundary to naturally come around again -
    /// a loop edit should be felt immediately, not after the next wrap.</summary>
    public void ApplyLoopEditNow()
    {
        if (LoopEnabled && IsPlaying && _output != null && LoopStartSeconds is { } s)
        {
            _output.Seek((long)(s * StemMixer.SampleRate));
            Position = s;
        }
    }

    /// <summary>Raised whenever the view needs to redraw the loop timeline (grid shifted, selection
    /// moved, a checkbox toggled, a different track is now playing).</summary>
    public event Action? LoopGridChanged;

    void ResetLoopSelection()
    {
        if (NowPlaying?.Track is { Segments.Count: > 0 } t)
        {
            var seg = t.Segments[0];
            int startIdx = seg.StartBeatIndex + seg.DownbeatOffset;
            int endIdx = Math.Min(startIdx + t.BeatsPerBar, seg.EndBeatIndex);
            LoopStartSeconds = t.BeatsMs[startIdx] / 1000.0;
            LoopEndSeconds = t.BeatsMs[Math.Min(endIdx, t.BeatsMs.Count - 1)] / 1000.0;
        }
        else { LoopStartSeconds = null; LoopEndSeconds = null; }
        LoopGridChanged?.Invoke();
    }

    partial void OnSplitBarChanged(bool value) { OnPropertyChanged(nameof(LoopSelectionText)); LoopGridChanged?.Invoke(); }
    partial void OnLoopByTimeChanged(bool value) { OnPropertyChanged(nameof(LoopSelectionText)); LoopGridChanged?.Invoke(); }
    partial void OnLoopEnabledChanged(bool value) => LoopGridChanged?.Invoke();
    partial void OnLoopStartSecondsChanged(double? value) => OnPropertyChanged(nameof(LoopSelectionText));
    partial void OnLoopEndSecondsChanged(double? value) => OnPropertyChanged(nameof(LoopSelectionText));

    public MainViewModel()
    {
        Settings = SettingsStore.Load();
        (_library, _env, _importer) = Build();
        foreach (var f in Faders) { f.Changed += ApplyFader; f.VolumeMoved += OnFaderMoved; }
        UpdateService.CleanupOldVersion();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Reload();
        CheckSetup();
        StartBeatBackfill();
    }

    /// <summary>Fills in beat timestamps for already-split tracks that don't have them yet. Fire-and-forget;
    /// quiet, best-effort, and shares the import gate so it never competes with an active import for CPU.</summary>
    void StartBeatBackfill() => _ = _importer.BackfillBeatsAsync();

    (Library, PythonEnv, ImportService) Build()
    {
        var lib = new Library(SettingsStore.LibraryDir(Settings));
        var env = new PythonEnv(Settings);
        var imp = new ImportService(lib, Settings, env);
        imp.TrackImported += t => Dispatcher.UIThread.Post(() => { _items.Add(new TrackItemViewModel(t)); Rebuild(); });
        imp.LogLine += l => Dispatcher.UIThread.Post(() => { Log.Add(l); while (Log.Count > 500) Log.RemoveAt(0); });
        imp.ResolveConflict = ResolveConflictAsync;
        return (lib, env, imp);
    }

    static Avalonia.Controls.Window? MainWindow =>
        (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    static Task<ReuseDecision> ResolveConflictAsync(CacheConflict conflict) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var win = new ReuseStemsWindow(conflict);
            return MainWindow == null ? ReuseDecision.Resplit : await win.ShowDialog<ReuseDecision>(MainWindow);
        });

    /// <summary>Called after the settings dialog closes so new paths/models take effect.</summary>
    public void SettingsChanged()
    {
        SettingsStore.Save(Settings);
        (_library, _env, _importer) = Build();
        Reload();
        CheckSetup();
        StartBeatBackfill();
    }

    void Reload()
    {
        _items.Clear();
        _items.AddRange(_library.Tracks.Select(t => new TrackItemViewModel(t)));
        Rebuild();
    }

    // ---------- lists + search ----------

    partial void OnSearchChanged(string value) { OnPropertyChanged(nameof(HasSearch)); Rebuild(); }

    bool Searching => !string.IsNullOrWhiteSpace(Search);

    static readonly Avalonia.Thickness ListIndent = new(22, 0, 0, 0);

    /// <summary>Recomputes the visible rows: lists first (each followed by its tracks unless collapsed),
    /// then ungrouped tracks. While searching, only matching tracks show and collapsed lists are expanded.</summary>
    void Rebuild()
    {
        var selTrack = (SelectedRow as TrackItemViewModel)?.Track.Id;
        var selList = (SelectedRow as ListHeaderViewModel)?.List.Id;
        var byId = _items.ToDictionary(i => i.Track.Id);
        var terms = (Search ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool Match(TrackItemViewModel i, string listName)
        {
            if (terms.Length == 0) return true;
            var hay = $"{i.Title} {i.Artist} {i.Album} {listName}";
            return terms.All(t => hay.Contains(t, StringComparison.OrdinalIgnoreCase));
        }

        var rows = new System.Collections.Generic.List<LibraryRow>();
        foreach (var l in _library.Lists)
        {
            var members = l.TrackIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
            var shown = terms.Length == 0 ? members : members.Where(m => Match(m, l.Name)).ToList();
            if (terms.Length > 0 && shown.Count == 0) continue;
            rows.Add(new ListHeaderViewModel(l, members.Count, shown.Count, ToggleList));
            foreach (var m in shown) m.Indent = ListIndent;
            if (terms.Length > 0 || !l.Collapsed) rows.AddRange(shown);
        }
        foreach (var t in _items.Where(i => _library.ListOf(i.Track.Id) == null)
                                .OrderBy(i => i.Artist).ThenBy(i => i.Title))
        {
            t.Indent = default;
            if (Match(t, "")) rows.Add(t);
        }

        Rows.Clear();
        foreach (var r in rows) Rows.Add(r);
        SelectedRow = rows.FirstOrDefault(r =>
            (r is TrackItemViewModel t && t.Track.Id == selTrack) ||
            (r is ListHeaderViewModel h && h.List.Id == selList));
        OnPropertyChanged(nameof(SelectedTrackInList));
    }

    /// <summary>Collapses/expands one list in place (no full rebuild, so the scroll position stays put).</summary>
    void ToggleList(ListHeaderViewModel h)
    {
        if (Searching) return; // search forces lists open
        var l = h.List;
        l.Collapsed = !l.Collapsed;
        _library.SaveLists();
        h.Refresh();
        int idx = Rows.IndexOf(h);
        if (idx < 0) return;
        if (l.Collapsed)
            while (idx + 1 < Rows.Count && Rows[idx + 1] is TrackItemViewModel t && l.TrackIds.Contains(t.Track.Id))
                Rows.RemoveAt(idx + 1);
        else
            foreach (var id in l.TrackIds)
                if (_items.FirstOrDefault(i => i.Track.Id == id) is { } m) Rows.Insert(++idx, m);
    }

    public bool HasSearch => !string.IsNullOrEmpty(Search);

    [RelayCommand] void ClearSearch() => Search = "";

    [RelayCommand]
    async Task NewList()
    {
        var name = await PromptListNameAsync("New list", "", offerExisting: false);
        if (string.IsNullOrWhiteSpace(name) || _library.FindList(name) != null) return;
        _library.CreateList(name);
        Rebuild();
    }

    [RelayCommand]
    async Task AddToList(TrackItemViewModel? t)
    {
        t ??= SelectedTrack;
        if (t == null) return;
        var name = await PromptListNameAsync($"Add \"{t.Title}\" to a list", _library.ListOf(t.Track.Id)?.Name ?? "", offerExisting: true);
        if (string.IsNullOrWhiteSpace(name)) return;
        var list = _library.FindList(name) ?? _library.CreateList(name);
        list.Collapsed = false;
        _library.AddToList(list, t.Track);
        Rebuild();
    }

    [RelayCommand]
    void RemoveFromList(TrackItemViewModel? t)
    {
        t ??= SelectedTrack;
        if (t == null) return;
        _library.RemoveFromList(t.Track);
        Rebuild();
    }

    [RelayCommand]
    async Task RenameList(ListHeaderViewModel? h)
    {
        h ??= SelectedRow as ListHeaderViewModel;
        if (h == null) return;
        var name = await PromptListNameAsync("Rename list", h.List.Name, offerExisting: false);
        if (string.IsNullOrWhiteSpace(name)) return;
        if (_library.FindList(name) is { } other && other != h.List) return;
        h.List.Name = name.Trim();
        _library.SaveLists();
        Rebuild();
    }

    [RelayCommand]
    void DeleteList(ListHeaderViewModel? h)
    {
        h ??= SelectedRow as ListHeaderViewModel;
        if (h == null) return;
        _library.DeleteList(h.List); // the songs stay in the library
        Rebuild();
    }

    [RelayCommand]
    void PlayList(ListHeaderViewModel? h)
    {
        h ??= SelectedRow as ListHeaderViewModel;
        if (h == null) return;
        if (h.List.TrackIds.Select(id => _items.FirstOrDefault(i => i.Track.Id == id)).FirstOrDefault(i => i != null) is { } first)
            PlayTrack(first);
    }

    Task<string?> PromptListNameAsync(string title, string initial, bool offerExisting) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (MainWindow == null) return null;
            var win = new ListNameWindow(title, initial, offerExisting ? _library.Lists.Select(l => l.Name).ToArray() : Array.Empty<string>());
            return await win.ShowDialog<string?>(MainWindow);
        });

    /// <summary>The order playback follows from <paramref name="t"/>: its list (which cycles), or all ungrouped songs.</summary>
    System.Collections.Generic.List<TrackItemViewModel> PlayOrder(TrackItemViewModel t, out bool cycles)
    {
        var l = _library.ListOf(t.Track.Id);
        cycles = l != null;
        if (l != null)
            return l.TrackIds.Select(id => _items.FirstOrDefault(i => i.Track.Id == id)).OfType<TrackItemViewModel>().ToList();
        return _items.Where(i => _library.ListOf(i.Track.Id) == null).OrderBy(i => i.Artist).ThenBy(i => i.Title).ToList();
    }

    bool _autoSetupRunning;

    async void CheckSetup()
    {
        if (!await _env.HasSeparatorAsync())
        {
            if (_autoSetupRunning) return;
            _autoSetupRunning = true;
            try
            {
                Status = "Setting up Python environment (first run, this can take several minutes)...";
                bool ok = await _env.InstallAsync(PythonEnv.DefaultVariant(),
                    line => Avalonia.Threading.Dispatcher.UIThread.Post(() => Status = "Setting up Python: " + line));
                Status = ok ? (PythonEnv.HasFfmpeg() ? "" : "ffmpeg was not found on PATH; install it for importing.")
                            : "Automatic Python setup failed. Open Options > Set up Python environment to see the log.";
            }
            finally { _autoSetupRunning = false; }
        }
        else Status = PythonEnv.HasFfmpeg() ? "" : "ffmpeg was not found on PATH; install it for importing.";
    }

    // ---------- import ----------

    public void ImportFiles(System.Collections.Generic.IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            var item = new ImportItemViewModel { Title = Path.GetFileNameWithoutExtension(p) };
            Imports.Add(item);
            _ = _importer.ImportFileAsync(p, item);
        }
    }

    [RelayCommand]
    void ImportYouTube()
    {
        var url = YouTubeUrl.Trim();
        if (url.Length == 0) return;
        var item = new ImportItemViewModel { Title = url };
        Imports.Add(item);
        YouTubeUrl = "";
        _ = _importer.ImportYouTubeAsync(url, item);
    }

    [RelayCommand] void CancelImport(ImportItemViewModel item) => item.Cts.Cancel();
    [RelayCommand] void DismissImport(ImportItemViewModel item) { item.Cts.Cancel(); Imports.Remove(item); }
    [RelayCommand] void ClearFinished() { foreach (var i in Imports.Where(i => i.Stage is "Done" or "Failed" or "Cancelled").ToList()) Imports.Remove(i); }

    // ---------- updates ----------

    [ObservableProperty] public partial bool IsUpdating { get; set; }
    [ObservableProperty] public partial bool RestartNeeded { get; set; }

    [RelayCommand]
    async System.Threading.Tasks.Task Update()
    {
        if (IsUpdating) return;
        IsUpdating = true;
        try
        {
            var exe = UpdateService.InstalledExe;
            if (exe == null) { Status = "Updating only works from the installed StemPlayer executable (not 'dotnet run')."; return; }
            var svc = new UpdateService();
            Status = "Checking for updates…";
            var info = await svc.CheckAsync(UpdateService.CurrentVersion);
            if (info == null) { Status = $"You're up to date (v{UpdateService.CurrentVersion.ToString(3)})."; return; }
            var progress = new Progress<double>(p => Status = $"Downloading {info.Tag}… {p * 100:0}%");
            await svc.ApplyAsync(info, exe, progress);
            Status = $"Updated to {info.Tag}. Restart StemPlayer to use it.";
            RestartNeeded = true;
        }
        catch (Exception e) { Status = "Update failed: " + e.Message; }
        finally { IsUpdating = false; }
    }

    [RelayCommand]
    void Restart()
    {
        var exe = UpdateService.InstalledExe;
        if (exe == null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false });
            (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        }
        catch (Exception e) { Status = "Could not restart: " + e.Message; }
    }

    // ---------- library ----------

    [RelayCommand]
    async Task DeleteTrack(TrackItemViewModel? t)
    {
        t ??= SelectedTrack;
        if (t == null) return;

        var win = new DeleteTrackWindow(t.Title);
        var choice = MainWindow == null ? DeleteChoice.Cancel : await win.ShowDialog<DeleteChoice>(MainWindow);
        if (choice == DeleteChoice.Cancel) return;

        if (NowPlaying == t) StopPlayback();
        if (choice == DeleteChoice.Everything) _library.Remove(t.Track);
        else _library.RemoveEntryOnly(t.Track);
        _items.Remove(t);
        Rebuild();
    }

    // ---------- playback ----------

    [RelayCommand]
    void PlayTrack(TrackItemViewModel? t)
    {
        t ??= SelectedTrack;
        if (t == null) return;
        NowPlaying = t; // -> OnNowPlayingChanged: resets TempoBpm and the loop selection
        var start = LoopEnabled && LoopStartSeconds is { } s0 ? s0 : 0;
        ReloadMixer(_library.TrackDir(t.Track), t.Track, start, resumePlaying: true);
    }

    /// <summary>(Re)loads the mixer from <paramref name="dir"/> (the track's normal folder, or a
    /// cached time-stretched render of it) and seeks to <paramref name="targetSeconds"/> in that
    /// folder's own timeline. Shared by PlayTrack and tempo changes, which swap the playing audio out
    /// from under an already-loaded track.</summary>
    void ReloadMixer(string dir, Track track, double targetSeconds, bool resumePlaying)
    {
        try
        {
            _output ??= new OpenAlOutput();
            _output.TrackEnded -= OnEnded;
            _output.TrackEnded += OnEnded;
            _mixer = new StemMixer(dir, track);
            _output.Load(_mixer);
            foreach (var f in Faders) f.Available = _mixer.HasGroup(f.Group);
            foreach (var f in Faders) ApplyFader(f);
            _output.SetVolume((float)MasterVolume);
            Duration = Math.Max(1, _mixer.TotalFrames / (double)StemMixer.SampleRate);
            Position = Math.Clamp(targetSeconds, 0, Duration);
            if (Position > 0) _output.Seek((long)(Position * StemMixer.SampleRate));
            if (resumePlaying) { _output.Play(); IsPlaying = true; }
            Status = "";
        }
        catch (Exception e) { Status = "Playback error: " + e.Message; }
    }

    void OnEnded() => Dispatcher.UIThread.Post(() => { IsPlaying = false; PlayNext(); });

    [RelayCommand]
    void TogglePlay()
    {
        if (_output == null || NowPlaying == null) { PlayTrack(SelectedTrack); return; }
        if (IsPlaying) { _output.Pause(); IsPlaying = false; }
        else
        {
            if (LoopEnabled && LoopStartSeconds is { } s)
            {
                _output.Seek((long)(s * StemMixer.SampleRate));
                Position = s;
            }
            _output.Play();
            IsPlaying = true;
        }
    }

    [RelayCommand]
    void PlayNext()
    {
        if (NowPlaying == null)
        {
            if (Rows.OfType<TrackItemViewModel>().FirstOrDefault() is { } first) PlayTrack(first);
            return;
        }
        var order = PlayOrder(NowPlaying, out var cycles);
        var i = order.IndexOf(NowPlaying);
        if (i < 0 || order.Count == 0) return;
        if (i + 1 < order.Count) PlayTrack(order[i + 1]);
        else if (cycles) PlayTrack(order[0]);
    }

    [RelayCommand]
    void PlayPrevious()
    {
        if (NowPlaying == null) return;
        var order = PlayOrder(NowPlaying, out var cycles);
        var i = order.IndexOf(NowPlaying);
        if (Position > 3 || i < 0 || (i == 0 && !cycles)) _output?.Seek(0);
        else PlayTrack(order[(i - 1 + order.Count) % order.Count]);
    }

    void StopPlayback()
    {
        _output?.Stop();
        NowPlaying = null; IsPlaying = false; Position = 0;
    }

    [RelayCommand]
    void ResetFaders()
    {
        _propagating = true; // don't let a held Shift drag the other faders along
        try { foreach (var f in Faders) { f.Volume = 1; f.Mute = false; f.Solo = false; } }
        finally { _propagating = false; }
    }

    /// <summary>True while Shift is held (set by the window). Moving a fader then moves all the others by the same amount.</summary>
    public bool ShiftHeld { get; set; }
    bool _propagating;

    void OnFaderMoved(StemFaderViewModel moved, double delta)
    {
        if (!ShiftHeld || _propagating || delta == 0) return;
        _propagating = true;
        try
        {
            foreach (var f in Faders)
                if (f != moved && f.Available) f.Volume = Math.Clamp(f.Volume + delta, 0, 1.5);
        }
        finally { _propagating = false; }
    }

    void ApplyFader(StemFaderViewModel f)
    {
        if (_mixer == null) return;
        _mixer.SetGain(f.Group, (float)f.Volume);
        _mixer.SetMute(f.Group, f.Mute);
        _mixer.SetSolo(f.Group, f.Solo);
    }

    public void BeginSeek() => _seeking = true;
    public void EndSeek()
    {
        _seeking = false;
        _output?.Seek((long)(Position * StemMixer.SampleRate));
    }

    void Tick()
    {
        if (_output == null || _seeking || NowPlaying == null) return;
        Position = _output.PositionFrames / (double)StemMixer.SampleRate;

        if (LoopEnabled && LoopStartSeconds is { } s && LoopEndSeconds is { } e && e > s && Position >= e)
        {
            _output.Seek((long)(s * StemMixer.SampleRate));
            Position = s;
        }
    }

    partial void OnPositionChanged(double value) => OnPropertyChanged(nameof(PositionText));
    partial void OnDurationChanged(double value) => OnPropertyChanged(nameof(DurationText));
    partial void OnNowPlayingChanged(TrackItemViewModel? value)
    {
        OnPropertyChanged(nameof(NowPlayingText));
        OnPropertyChanged(nameof(BarCount));
        TempoBpm = value?.Track.Tempo ?? 0;
        _output?.SetTempoRatio(1.0);
        OnPropertyChanged(nameof(TempoText));
        OnPropertyChanged(nameof(MeterText));
        OnPropertyChanged(nameof(HasMeter));
        ResetLoopSelection();
    }
    partial void OnIsPlayingChanged(bool value) => OnPropertyChanged(nameof(PlayGlyph));
    partial void OnMasterVolumeChanged(double value) => _output?.SetVolume((float)value);

    public void Dispose() { _timer.Stop(); _output?.Dispose(); }
}
