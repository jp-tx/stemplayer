using System;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StemPlayer.Models;

namespace StemPlayer.ViewModels;

public partial class ImportItemViewModel : ViewModelBase
{
    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string Stage { get; set; } = "Queued";
    /// <summary>0..1 overall progress for this song.</summary>
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial bool IsIndeterminate { get; set; }
    [ObservableProperty] public partial bool IsFailed { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }

    public System.Threading.CancellationTokenSource Cts { get; } = new();
    public string ProgressText => $"{Progress * 100:0}%";
    partial void OnProgressChanged(double value) => OnPropertyChanged(nameof(ProgressText));
}

/// <summary>A row in the library list: either a list header or a track.</summary>
public abstract class LibraryRow : ViewModelBase { }

public class ListHeaderViewModel : LibraryRow
{
    public TrackList List { get; }
    readonly int _total, _shown;
    public ICommand ToggleCommand { get; }

    public ListHeaderViewModel(TrackList list, int total, int shown, Action<ListHeaderViewModel> toggle)
    {
        List = list; _total = total; _shown = shown;
        ToggleCommand = new RelayCommand(() => toggle(this));
    }

    public string Name => List.Name;
    public string Arrow => List.Collapsed ? "▸" : "▾";
    public string CountText => _shown == _total ? $"{_total} {(_total == 1 ? "song" : "songs")}" : $"{_shown} of {_total} match";
    public void Refresh() => OnPropertyChanged(nameof(Arrow));
}

public class TrackItemViewModel : LibraryRow
{
    public Track Track { get; }
    public TrackItemViewModel(Track t) => Track = t;
    /// <summary>Left indent: tracks that belong to a list are nested under its header.</summary>
    public Avalonia.Thickness Indent { get; set; }
    public string Title => Track.Title;
    public string Artist => Track.Artist;
    public string Album => Track.Album;
    public string Duration => Track.DurationSeconds > 0 ? System.TimeSpan.FromSeconds(Track.DurationSeconds).ToString(@"m\:ss") : "";
    public string Stems => Track.Stems.Count + " stems";
}
