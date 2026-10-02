using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using StemPlayer.Models;
using StemPlayer.ViewModels;

namespace StemPlayer.Views;

public partial class MainWindow : Window
{
    MainViewModel? Vm => DataContext as MainViewModel;

    enum LoopHandle { None, Start, End }
    LoopHandle _dragging = LoopHandle.None;
    bool _loopDirty;

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

        // The loop timeline is drawn in code (no off-the-shelf dual-handle range control in Avalonia);
        // redraw whenever the view model says the grid/selection changed, or on the next layout pass
        // (covers a window resize) if a redraw was requested since the last one.
        DataContextChanged += (_, _) => { if (Vm != null) Vm.LoopGridChanged += () => _loopDirty = true; };
        LayoutUpdated += (_, _) => { if (_loopDirty) { _loopDirty = false; RedrawLoop(); } };
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

    void OnTrackListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || Vm?.SelectedTrack == null) return;
        e.Handled = true;
        Vm.DeleteTrackCommand.Execute(Vm.SelectedTrack);
    }

    void OnUrlKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Vm?.ImportYouTubeCommand.Execute(null);
    }

    // ---------- loop timeline ----------

    void OnLoopPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var t = Vm?.NowPlaying?.Track;
        if (t == null || Vm!.LoopStartBeat is not { } s || Vm.LoopEndBeat is not { } en) return;
        double x = e.GetPosition(LoopCanvas).X;
        _dragging = Math.Abs(x - XForBeat(t, s)) <= Math.Abs(x - XForBeat(t, en)) ? LoopHandle.Start : LoopHandle.End;
        e.Pointer.Capture(LoopCanvas);
    }

    void OnLoopPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragging == LoopHandle.None || Vm?.NowPlaying?.Track == null) return;
        var idx = BeatIndexAtX(e.GetPosition(LoopCanvas).X);
        if (idx == null) return;
        if (_dragging == LoopHandle.Start) Vm.LoopStartBeat = idx;
        else Vm.LoopEndBeat = idx;
        RedrawLoop();
    }

    void OnLoopPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging == LoopHandle.None) return;
        _dragging = LoopHandle.None;
        e.Pointer.Capture(null);
        // Dragged one handle past the other - swap so Start is always the earlier of the two.
        if (Vm != null && Vm.LoopStartBeat > Vm.LoopEndBeat)
            (Vm.LoopStartBeat, Vm.LoopEndBeat) = (Vm.LoopEndBeat, Vm.LoopStartBeat);
        RedrawLoop();
    }

    double XForBeat(Track t, int beatIndex)
    {
        var duration = Math.Max(Vm?.Duration ?? 0, 0.001);
        return Math.Clamp(t.BeatsMs[beatIndex] / 1000.0 / duration, 0, 1) * LoopCanvas.Bounds.Width;
    }

    int? BeatIndexAtX(double x)
    {
        var t = Vm?.NowPlaying?.Track;
        if (t == null || t.BeatsMs.Count == 0 || Vm!.Duration <= 0 || LoopCanvas.Bounds.Width <= 0) return null;
        double frac = Math.Clamp(x / LoopCanvas.Bounds.Width, 0, 1);
        double targetMs = frac * Vm.Duration * 1000.0;

        int nearest = 0; double best = double.MaxValue;
        for (int i = 0; i < t.BeatsMs.Count; i++)
        {
            double d = Math.Abs(t.BeatsMs[i] - targetMs);
            if (d < best) { best = d; nearest = i; }
        }
        if (Vm.SplitBar) return nearest;

        var bars = BarBoundaries(t);
        return bars.Count == 0 ? nearest : bars.OrderBy(b => Math.Abs(b - nearest)).First();
    }

    static List<int> BarBoundaries(Track t)
    {
        var list = new List<int>();
        foreach (var seg in t.Segments)
            for (int i = seg.StartBeatIndex + seg.DownbeatOffset; i < seg.EndBeatIndex; i += t.BeatsPerBar)
                list.Add(i);
        return list;
    }

    void RedrawLoop()
    {
        LoopCanvas.Children.Clear();
        var t = Vm?.NowPlaying?.Track;
        double duration = Vm?.Duration ?? 0;
        double width = LoopCanvas.Bounds.Width, height = LoopCanvas.Bounds.Height;
        if (t == null || duration <= 0 || width <= 0 || t.BeatsMs.Count == 0) return;

        double XFor(long ms) => Math.Clamp(ms / 1000.0 / duration, 0, 1) * width;

        foreach (var seg in t.Segments)
            for (int i = seg.StartBeatIndex + seg.DownbeatOffset; i < seg.EndBeatIndex; i += t.BeatsPerBar)
                LoopCanvas.Children.Add(new Line
                {
                    StartPoint = new Point(XFor(t.BeatsMs[i]), 0),
                    EndPoint = new Point(XFor(t.BeatsMs[i]), height),
                    Stroke = Brushes.Gray,
                    StrokeThickness = 1,
                    Opacity = 0.5,
                });

        if (Vm!.LoopStartBeat is { } s && Vm.LoopEndBeat is { } en && s < t.BeatsMs.Count && en < t.BeatsMs.Count)
        {
            double xs = XFor(t.BeatsMs[s]), xe = XFor(t.BeatsMs[en]);
            var (lo, hi) = (Math.Min(xs, xe), Math.Max(xs, xe));

            var fill = new Rectangle { Width = Math.Max(2, hi - lo), Height = height, Fill = new SolidColorBrush(Colors.CornflowerBlue, 0.35) };
            Canvas.SetLeft(fill, lo); Canvas.SetTop(fill, 0);
            LoopCanvas.Children.Add(fill);

            foreach (var x in new[] { xs, xe })
            {
                var handle = new Rectangle { Width = 4, Height = height, Fill = Brushes.CornflowerBlue, Cursor = new Cursor(StandardCursorType.SizeWestEast) };
                Canvas.SetLeft(handle, x - 2); Canvas.SetTop(handle, 0);
                LoopCanvas.Children.Add(handle);
            }
        }
    }
}
