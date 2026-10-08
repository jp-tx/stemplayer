using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace StemPlayer.Views;

/// <summary>Asks for a list name, optionally offering the existing lists to pick from. Closes with the name, or null.</summary>
public partial class ListNameWindow : Window
{
    public ListNameWindow() : this("List", "", System.Array.Empty<string>()) { }

    public ListNameWindow(string prompt, string initial, string[] existing)
    {
        InitializeComponent();
        PromptText.Text = prompt;
        NameBox.Text = initial;
        ExistingList.ItemsSource = existing;
        ExistingList.IsVisible = ExistingLabel.IsVisible = existing.Length > 0;
        Opened += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    void OnExistingChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ExistingList.SelectedItem is string s) NameBox.Text = s;
    }

    void OnExistingDoubleTapped(object? sender, TappedEventArgs e) => OnOk(sender, e);
    void OnNameKeyDown(object? sender, KeyEventArgs e) { if (e.Key == Key.Enter) OnOk(sender, e); }
    void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    void OnOk(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim();
        Close(string.IsNullOrEmpty(name) ? null : name);
    }
}
