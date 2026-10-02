using Avalonia.Controls;
using Avalonia.Interactivity;
using StemPlayer.Services;

namespace StemPlayer.Views;

public partial class ReuseStemsWindow : Window
{
    public ReuseStemsWindow() : this(new CacheConflict("this song", "", "")) { }

    public ReuseStemsWindow(CacheConflict conflict)
    {
        InitializeComponent();
        MessageText.Text = $"\"{conflict.Title}\" was already split before (model: {conflict.Model}), " +
                            "but it's no longer in your library. Reuse the existing stems, or split it again from scratch?";
    }

    void OnCancel(object? sender, RoutedEventArgs e) => Close(ReuseDecision.Cancel);
    void OnResplit(object? sender, RoutedEventArgs e) => Close(ReuseDecision.Resplit);
    void OnReuse(object? sender, RoutedEventArgs e) => Close(ReuseDecision.Reuse);
}
