using Avalonia.Controls;
using Avalonia.Interactivity;
using StemPlayer.Models;

namespace StemPlayer.Views;

public partial class DeleteTrackWindow : Window
{
    public DeleteTrackWindow() : this("this track") { }

    public DeleteTrackWindow(string title)
    {
        InitializeComponent();
        MessageText.Text = $"Delete \"{title}\"?";
    }

    void OnCancel(object? sender, RoutedEventArgs e) => Close(DeleteChoice.Cancel);
    void OnEntryOnly(object? sender, RoutedEventArgs e) => Close(DeleteChoice.EntryOnly);
    void OnEverything(object? sender, RoutedEventArgs e) => Close(DeleteChoice.Everything);
}
