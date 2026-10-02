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
    /// <summary>Raised with the signed change whenever the user (or code) moves this fader.</summary>
    public event Action<StemFaderViewModel, double>? VolumeMoved;

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

    partial void OnVolumeChanged(double oldValue, double newValue)
    {
        OnPropertyChanged(nameof(VolumeText));
        Changed?.Invoke(this);
        VolumeMoved?.Invoke(this, newValue - oldValue);
    }
    partial void OnMuteChanged(bool value) => Changed?.Invoke(this);
    partial void OnSoloChanged(bool value) => Changed?.Invoke(this);
}
