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
        if (Vm?.NowPlaying?.Track == null || Vm.LoopStartSeconds is not { } s || Vm.LoopEndSeconds is not { } en) return;
        double x = e.GetPosition(LoopCanvas).X;
        _dragging = Math.Abs(x - XForSeconds(s)) <= Math.Abs(x - XForSeconds(en)) ? LoopHandle.Start : LoopHandle.End;
        e.Pointer.Capture(LoopCanvas);
    }

    void OnLoopPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragging == LoopHandle.None || Vm?.NowPlaying?.Track == null) return;
        var seconds = SecondsAtX(e.GetPosition(LoopCanvas).X);
        if (seconds == null) return;
        if (_dragging == LoopHandle.Start) Vm.LoopStartSeconds = seconds;
        else Vm.LoopEndSeconds = seconds;
        RedrawLoop();
    }

    void OnLoopPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging == LoopHandle.None) return;
        _dragging = LoopHandle.None;
        e.Pointer.Capture(null);
        // Dragged one handle past the other - swap so Start is always the earlier of the two.
        if (Vm != null && Vm.LoopStartSeconds > Vm.LoopEndSeconds)
            (Vm.LoopStartSeconds, Vm.LoopEndSeconds) = (Vm.LoopEndSeconds, Vm.LoopStartSeconds);
        Vm?.ApplyLoopEditNow();
        RedrawLoop();
    }

    double XForSeconds(double seconds)
    {
        var duration = Math.Max(Vm?.Duration ?? 0, 0.001);
        return Math.Clamp(seconds / duration, 0, 1) * LoopCanvas.Bounds.Width;
    }

    /// <summary>The time a pixel X corresponds to, snapped to the nearest beat or bar unless LoopByTime
    /// is on (free, unsnapped dragging).</summary>
    double? SecondsAtX(double x)
    {
        if (Vm?.NowPlaying?.Track is not { } t || Vm.Duration <= 0 || LoopCanvas.Bounds.Width <= 0) return null;
        double frac = Math.Clamp(x / LoopCanvas.Bounds.Width, 0, 1);
        double seconds = frac * Vm.Duration;
        if (Vm.LoopByTime) return seconds;

        var grid = GridSeconds(t, Vm.Duration, byBar: !Vm.SplitBar, Vm.TempoFactor);
        return grid.Count == 0 ? seconds : grid.OrderBy(g => Math.Abs(g - seconds)).First();
    }

    /// <summary>Every bar (or beat) boundary across the WHOLE track, not just inside the detected
    /// segment: extrapolated forward/backward from the segment's own downbeat phase using the
    /// measured tempo. This is what makes a negative bar (before a silent intro with no detected
    /// beats) or "last bar + N" (past a fade-out) a real, draggable position - the detector's
    /// confidence region isn't a hard boundary on where a loop can start or end. The grid itself is
    /// computed in the track's ORIGINAL time and divided by tempoFactor at the end to land in
    /// whichever timeline is actually playing (1.0 = unstretched).</summary>
    static List<double> GridSeconds(Track t, double duration, bool byBar, double tempoFactor)
    {
        var result = new List<double>();
        if (t.Segments.Count == 0 || t.Tempo <= 0 || t.BeatsMs.Count == 0) return result;
        var seg = t.Segments[0];
        double refTime = t.BeatsMs[seg.StartBeatIndex + seg.DownbeatOffset] / 1000.0;
        double step = 60.0 / t.Tempo * (byBar ? t.BeatsPerBar : 1);
        if (step <= 0) return result;

        double originalDuration = duration * tempoFactor;
        for (double time = refTime; time >= 0; time -= step) result.Add(time / tempoFactor);
        for (double time = refTime + step; time <= originalDuration; time += step) result.Add(time / tempoFactor);
        result.Sort();
        return result;
    }

    void RedrawLoop()
    {
        LoopCanvas.Children.Clear();
        var t = Vm?.NowPlaying?.Track;
        double duration = Vm?.Duration ?? 0;
        double width = LoopCanvas.Bounds.Width, height = LoopCanvas.Bounds.Height;
        if (t == null || duration <= 0 || width <= 0) return;

        double XFor(double seconds) => Math.Clamp(seconds / duration, 0, 1) * width;

        foreach (var time in GridSeconds(t, duration, byBar: true, Vm!.TempoFactor))
            LoopCanvas.Children.Add(new Line
            {
                StartPoint = new Point(XFor(time), 0),
                EndPoint = new Point(XFor(time), height),
                Stroke = Brushes.Gray,
                StrokeThickness = 1,
                Opacity = 0.5,
            });

        if (Vm!.LoopStartSeconds is { } s && Vm.LoopEndSeconds is { } en)
        {
            double xs = XForSeconds(s), xe = XForSeconds(en);
            var (lo, hi) = (Math.Min(xs, xe), Math.Max(xs, xe));
            // Dimmer when the loop isn't actually enabled, so it reads as "selected" vs "active".
            double fillOpacity = Vm.LoopEnabled ? 0.35 : 0.15;

            var fill = new Rectangle { Width = Math.Max(2, hi - lo), Height = height, Fill = new SolidColorBrush(Colors.CornflowerBlue, fillOpacity) };
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
