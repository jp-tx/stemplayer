using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StemPlayer.Models;
using StemPlayer.Services;

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
        new(StemGroup.Keys, "Keys", "🎹"),
        new(StemGroup.Guitar, "Guitar", "🎸"),
        new(StemGroup.Bass, "Bass", "🎻"),
        new(StemGroup.Other, "Everything else", "🥁"),
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
    public string PlayGlyph => IsPlaying ? "⏸" : "▶";

    public MainViewModel()
    {
        Settings = SettingsStore.Load();
        (_library, _env, _importer) = Build();
        foreach (var f in Faders) f.Changed += ApplyFader;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Reload();
        CheckSetup();
    }

    (Library, PythonEnv, ImportService) Build()
    {
        var lib = new Library(SettingsStore.LibraryDir(Settings));
        var env = new PythonEnv(Settings);
        var imp = new ImportService(lib, Settings, env);
        imp.TrackImported += t => Tracks.Add(new TrackItemViewModel(t));
        imp.LogLine += l => Dispatcher.UIThread.Post(() => { Log.Add(l); while (Log.Count > 500) Log.RemoveAt(0); });
        return (lib, env, imp);
    }

    /// <summary>Called after the settings dialog closes so new paths/models take effect.</summary>
    public void SettingsChanged()
    {
        SettingsStore.Save(Settings);
        (_library, _env, _importer) = Build();
        Reload();
        CheckSetup();
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

    // ---------- library ----------

    [RelayCommand]
    void DeleteTrack(TrackItemViewModel? t)
    {
        t ??= SelectedTrack;
        if (t == null) return;
        if (NowPlaying == t) StopPlayback();
        _library.Remove(t.Track);
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
        foreach (var f in Faders) { f.Volume = 1; f.Mute = false; f.Solo = false; }
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
    partial void OnNowPlayingChanged(TrackItemViewModel? value) => OnPropertyChanged(nameof(NowPlayingText));
    partial void OnIsPlayingChanged(bool value) => OnPropertyChanged(nameof(PlayGlyph));
    partial void OnMasterVolumeChanged(double value) => _output?.SetVolume((float)value);

    public void Dispose() { _timer.Stop(); _output?.Dispose(); }
}
