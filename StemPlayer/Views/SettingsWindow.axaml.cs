using Avalonia.Controls;
using Avalonia.Interactivity;
using StemPlayer.ViewModels;

namespace StemPlayer.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow() => InitializeComponent();

    void OnSave(object? sender, RoutedEventArgs e)
    {
        (DataContext as SettingsViewModel)?.Apply();
        Close(true);
    }

    void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
