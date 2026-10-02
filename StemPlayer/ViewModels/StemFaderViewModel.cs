using System;
using CommunityToolkit.Mvvm.ComponentModel;
using StemPlayer.Models;

namespace StemPlayer.ViewModels;

public partial class StemFaderViewModel : ViewModelBase
{
    public StemGroup Group { get; }
    public string Name { get; }
    public string Icon { get; }
    public event Action<StemFaderViewModel>? Changed;

    public StemFaderViewModel(StemGroup group, string name, string icon)
    {
        Group = group; Name = name; Icon = icon;
    }

    /// <summary>1.0 = unity gain.</summary>
    [ObservableProperty] public partial double Volume { get; set; } = 1.0;
    [ObservableProperty] public partial bool Mute { get; set; }
    [ObservableProperty] public partial bool Solo { get; set; }
    /// <summary>False when the loaded track's model produced no stem for this fader.</summary>
    [ObservableProperty] public partial bool Available { get; set; } = true;

    public string VolumeText => $"{Volume * 100:0}%";

    partial void OnVolumeChanged(double value) { OnPropertyChanged(nameof(VolumeText)); Changed?.Invoke(this); }
    partial void OnMuteChanged(bool value) => Changed?.Invoke(this);
    partial void OnSoloChanged(bool value) => Changed?.Invoke(this);
}
