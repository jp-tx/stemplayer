using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using StemPlayer.ViewModels;

namespace StemPlayer.Views;

public partial class MainWindow : Window
{
    MainViewModel? Vm => DataContext as MainViewModel;

    public MainWindow()
    {
        InitializeComponent();
        // Slider consumes pointer events; listen even when handled so we know when the user is scrubbing.
        SeekSlider.AddHandler(PointerPressedEvent, (_, _) => Vm?.BeginSeek(), RoutingStrategies.Tunnel, handledEventsToo: true);
        SeekSlider.AddHandler(PointerReleasedEvent, (_, _) => Vm?.EndSeek(), RoutingStrategies.Tunnel, handledEventsToo: true);
        Closing += (_, _) => Vm?.Dispose();
        // Track Shift for the fader "move all the others" gesture. Pointer events also carry modifiers, which
        // keeps this correct if Shift was pressed while another control had focus.
        AddHandler(KeyDownEvent, (_, e) => SetShift(e.KeyModifiers), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyUpEvent, (_, e) => SetShift(e.KeyModifiers), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, (_, e) => SetShift(e.KeyModifiers), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, (_, e) => SetShift(e.KeyModifiers), RoutingStrategies.Tunnel, handledEventsToo: true);
        Deactivated += (_, _) => SetShift(KeyModifiers.None);
    }

    void SetShift(KeyModifiers m) { if (Vm != null) Vm.ShiftHeld = m.HasFlag(KeyModifiers.Shift); }

    async void OnImportFiles(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import songs",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Audio") { Patterns = new[] { "*.mp3", "*.wav", "*.flac", "*.m4a", "*.ogg", "*.opus", "*.aac", "*.wma" } },
                FilePickerFileTypes.All,
            },
        });
        Vm?.ImportFiles(files.Select(f => f.TryGetLocalPath()).Where(p => p != null)!);
    }

    async void OnOptions(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var win = new SettingsWindow { DataContext = new SettingsViewModel(Vm.Settings) };
        if (await win.ShowDialog<bool>(this)) Vm.SettingsChanged();
    }

    void OnTrackDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm?.SelectedTrack != null) Vm.PlayTrackCommand.Execute(Vm.SelectedTrack);
    }

    void OnUrlKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Vm?.ImportYouTubeCommand.Execute(null);
    }
}
