using CommunityToolkit.Mvvm.ComponentModel;
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

public class TrackItemViewModel : ViewModelBase
{
    public Track Track { get; }
    public TrackItemViewModel(Track t) => Track = t;
    public string Title => Track.Title;
    public string Artist => Track.Artist;
    public string Album => Track.Album;
    public string Duration => Track.DurationSeconds > 0 ? System.TimeSpan.FromSeconds(Track.DurationSeconds).ToString(@"m\:ss") : "";
    public string Stems => Track.Stems.Count + " stems";
}
