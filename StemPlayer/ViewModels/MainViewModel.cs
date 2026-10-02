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
    public string NowPlayingMeterText => NowPlaying?.Track is { Tempo: > 0 } t ? $"{t.Tempo:0} BPM · {t.BeatsPerBar}/4" : "";
    public string PlayGlyph => IsPlaying ? "⏸" : "▶";

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
        try
        {
            _output ??= new OpenAlOutput();
            _output.TrackEnded -= OnEnded;
            _output.TrackEnded += OnEnded;
            _mixer = new StemMixer(_library.TrackDir(t.Track), t.Track);
            _output.Load(_mixer);
            foreach (var f in Faders) f.Available = _mixer.HasGroup(f.Group);
            foreach (var f in Faders) ApplyFader(f);
            _output.SetVolume((float)MasterVolume);
            NowPlaying = t;
            Duration = Math.Max(1, _mixer.TotalFrames / (double)StemMixer.SampleRate);
            Position = 0;
            _output.Play();
            IsPlaying = true;
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
        else { _output.Play(); IsPlaying = true; }
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
    }

    partial void OnPositionChanged(double value) => OnPropertyChanged(nameof(PositionText));
    partial void OnDurationChanged(double value) => OnPropertyChanged(nameof(DurationText));
    partial void OnNowPlayingChanged(TrackItemViewModel? value)
    {
        OnPropertyChanged(nameof(NowPlayingText));
        OnPropertyChanged(nameof(NowPlayingMeterText));
    }
    partial void OnIsPlayingChanged(bool value) => OnPropertyChanged(nameof(PlayGlyph));
    partial void OnMasterVolumeChanged(double value) => _output?.SetVolume((float)value);

    public void Dispose() { _timer.Stop(); _output?.Dispose(); }
}
