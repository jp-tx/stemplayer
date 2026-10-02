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
    StretchService _stretch;
    readonly DispatcherTimer _timer;
    bool _seeking;

    public ObservableCollection<TrackItemViewModel> Tracks { get; } = new();
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

    [ObservableProperty] public partial TrackItemViewModel? SelectedTrack { get; set; }
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
    public string NowPlayingMeterText => NowPlaying?.Track is { Tempo: > 0 } t
        ? $"{TempoBpm:0} BPM · {t.BeatsPerBar}/4 · {BarCount} bars" : "";
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

    // ---------- tempo / time-stretch ----------

    [ObservableProperty] public partial double TempoBpm { get; set; }
    [ObservableProperty] public partial bool IsStretching { get; set; }

    /// <summary>Ratio of the currently playing audio's tempo to the track's detected tempo (1.0 =
    /// unstretched/original). All beat-grid math below is anchored in the ORIGINAL track's time
    /// (BeatsMs, Tempo); this is what converts to/from the timeline that's actually playing.</summary>
    public double TempoFactor { get; private set; } = 1.0;

    const double TempoStepBpm = 5.0;

    [RelayCommand] Task TempoUp() => ChangeTempoAsync(TempoBpm + TempoStepBpm);
    [RelayCommand] Task TempoDown() => ChangeTempoAsync(TempoBpm - TempoStepBpm);
    [RelayCommand] Task TempoReset() => NowPlaying?.Track is { Tempo: > 0 } t ? ChangeTempoAsync(t.Tempo) : Task.CompletedTask;

    /// <summary>Renders (or reuses a cached render of) every stem time-stretched to the new tempo via
    /// pedalboard's Rubber Band-based time_stretch - chosen specifically for minimal artifacts, since
    /// this is a transcription tool. Offline, not real-time: a render takes real wall-clock time even
    /// with stems processed in parallel, so IsStretching gates the UI while it runs.</summary>
    async Task ChangeTempoAsync(double newBpm)
    {
        if (IsStretching || NowPlaying?.Track is not { Tempo: > 0 } t) return;
        newBpm = Math.Round(Math.Clamp(newBpm, 20, 400));
        double oldFactor = TempoFactor;
        double factor = newBpm / t.Tempo;
        double targetSeconds = Position * oldFactor / factor; // keep the same musical position
        bool wasPlaying = IsPlaying;

        string dir;
        if (Math.Abs(factor - 1.0) < 0.001)
        {
            factor = 1.0;
            dir = _library.TrackDir(t);
        }
        else
        {
            IsStretching = true;
            Status = $"Time-stretching to {newBpm:0} BPM…";
            string? rendered;
            try
            {
                rendered = await _stretch.StretchAsync(_library.TrackDir(t), factor,
                    p => Status = $"Time-stretching to {newBpm:0} BPM… {p * 100:0}%",
                    l => Dispatcher.UIThread.Post(() => { Log.Add(l); while (Log.Count > 500) Log.RemoveAt(0); }));
            }
            finally { IsStretching = false; }
            if (rendered == null) { Status = "Time-stretch failed; staying at the current tempo."; return; }
            dir = rendered;
        }

        // Rescale the loop selection (if any) from the old timeline into the new one, so adjusting
        // tempo doesn't silently discard a loop the user already set up.
        if (LoopStartSeconds is { } ls) LoopStartSeconds = ls * oldFactor / factor;
        if (LoopEndSeconds is { } le) LoopEndSeconds = le * oldFactor / factor;

        TempoBpm = newBpm;
        TempoFactor = factor;
        ReloadMixer(dir, t, targetSeconds, wasPlaying);
        OnPropertyChanged(nameof(NowPlayingMeterText));
        LoopGridChanged?.Invoke();
    }

    /// <summary>The segment whose own bar grid is closest to a given (original-timeline) time - used
    /// to anchor labeling/shifting for a point that may fall outside every segment (near a stop, or
    /// beyond the first/last detected beat entirely).</summary>
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

    /// <summary>1-based bar and beat-within-bar for any time in the CURRENTLY PLAYING timeline
    /// (rescaled by TempoFactor back to the original track time before the arithmetic), extrapolated
    /// from the nearest segment's own downbeat phase using the measured tempo - this is what makes a
    /// negative bar (before the first detected beat, e.g. a silent intro) or "last bar + N" (past the
    /// last detected beat, e.g. a fade-out) a well-defined position instead of just undefined space.</summary>
    (int Bar, int Beat)? BarBeatForTime(Track t, double seconds)
    {
        double original = seconds * TempoFactor;
        if (NearestSegment(t, original) is not { } seg || t.Tempo <= 0) return null;
        double refTime = t.BeatsMs[seg.StartBeatIndex + seg.DownbeatOffset] / 1000.0;
        double beatSeconds = 60.0 / t.Tempo;
        int rel = (int)Math.Round((original - refTime) / beatSeconds);
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
        var seg = (LoopStartSeconds is { } s ? NearestSegment(t, s * TempoFactor) : null) ?? t.Segments.FirstOrDefault();
        if (seg == null) return;
        seg.DownbeatOffset = ((seg.DownbeatOffset + direction) % t.BeatsPerBar + t.BeatsPerBar) % t.BeatsPerBar;
        _library.Save();

        double beatSeconds = 60.0 / t.Tempo / TempoFactor; // one beat's length in the CURRENT (possibly stretched) timeline
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
            LoopStartSeconds = t.BeatsMs[startIdx] / 1000.0 / TempoFactor;
            LoopEndSeconds = t.BeatsMs[Math.Min(endIdx, t.BeatsMs.Count - 1)] / 1000.0 / TempoFactor;
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
        _stretch = new StretchService(_env);
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
        imp.TrackImported += t => Tracks.Add(new TrackItemViewModel(t));
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
        _stretch = new StretchService(_env);
        Reload();
        CheckSetup();
        StartBeatBackfill();
    }

    void Reload()
    {
        Tracks.Clear();
        foreach (var t in _library.Tracks.OrderBy(t => t.Artist).ThenBy(t => t.Title))
            Tracks.Add(new TrackItemViewModel(t));
    }

    async void CheckSetup()
    {
        if (!await _env.HasSeparatorAsync())
            Status = "audio-separator is not installed yet. Open Options > Set up Python environment.";
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
        Tracks.Remove(t);
    }

    // ---------- playback ----------

    [RelayCommand]
    void PlayTrack(TrackItemViewModel? t)
    {
        t ??= SelectedTrack;
        if (t == null) return;
        NowPlaying = t; // -> OnNowPlayingChanged: resets TempoBpm/TempoFactor and the loop selection
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
        if (Tracks.Count == 0) return;
        var i = NowPlaying == null ? -1 : Tracks.IndexOf(NowPlaying);
        if (i + 1 < Tracks.Count) PlayTrack(Tracks[i + 1]);
    }

    [RelayCommand]
    void PlayPrevious()
    {
        if (Tracks.Count == 0) return;
        var i = NowPlaying == null ? 1 : Tracks.IndexOf(NowPlaying);
        if (Position > 3 || i <= 0) _output?.Seek(0); else PlayTrack(Tracks[i - 1]);
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
        TempoFactor = 1.0;
        TempoBpm = value?.Track.Tempo ?? 0;
        OnPropertyChanged(nameof(NowPlayingMeterText));
        OnPropertyChanged(nameof(HasMeter));
        ResetLoopSelection();
    }
    partial void OnIsPlayingChanged(bool value) => OnPropertyChanged(nameof(PlayGlyph));
    partial void OnMasterVolumeChanged(double value) => _output?.SetVolume((float)value);

    public void Dispose() { _timer.Stop(); _output?.Dispose(); }
}
