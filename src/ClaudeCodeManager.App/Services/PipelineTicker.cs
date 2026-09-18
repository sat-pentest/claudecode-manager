using System;
using System.Diagnostics;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClaudeCodeManager.App.Services;

/// <summary>
/// App-wide clock for the WORKFLOWS pipeline sweep. Same singleton rationale as
/// <see cref="PulseTicker"/> and <see cref="FlowTicker"/> — one clock keeps every card in step.
///
/// Unlike <see cref="FlowTicker"/>, this one exposes a plain monotonic <see cref="Clock"/> in
/// seconds rather than a normalised 0..1 progress. The pipeline sweep is not a chain walk that
/// starts dark and lights up: every phase card is already lit, and a highlight *passes over* them.
/// That means the cycle length has to scale with the phase count (a 12-phase pipeline should take
/// longer to sweep than a 5-phase one, not run each card 2.4× faster), which the converter can
/// only work out once it knows the count — so the phasing is computed downstream, not here.
///
/// Driven by <see cref="CompositionTarget.Rendering"/> rather than a timer. A 33 ms DispatcherTimer
/// produces ~30 updates a second against a 60 Hz or 144 Hz compositor, so frames land unevenly and
/// a slow, wide sweep shows it as judder. Rendering fires once per composed frame, in step with
/// whatever the display is actually doing.
/// </summary>
public partial class PipelineTicker : ObservableObject
{
    public static PipelineTicker Current { get; } = new();

    /// <summary>Seconds of *visible* animation elapsed. Consumers take their own modulo.</summary>
    [ObservableProperty] private double _clock;

    /// <summary>
    /// Counts only while the shell is on screen. Wall-clock would work too, but then a window
    /// restored after ten minutes in the tray resumes mid-cycle at an arbitrary phase — the sweep
    /// appears to teleport. Pausing the stopwatch makes it continue from where it was left.
    /// </summary>
    private readonly Stopwatch _elapsed = new();
    private bool _subscribed;

    private PipelineTicker()
    {
        _elapsed.Start();
        Subscribe();

        AppVisibility.Current.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(AppVisibility.IsVisible)) return;

            if (AppVisibility.Current.IsVisible)
            {
                _elapsed.Start();
                Subscribe();
            }
            else
            {
                // Nothing to animate for an off-screen window, and this fires every frame.
                _elapsed.Stop();
                Unsubscribe();
            }
        };
    }

    private void Subscribe()
    {
        if (_subscribed) return;
        CompositionTarget.Rendering += OnRendering;
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        CompositionTarget.Rendering -= OnRendering;
        _subscribed = false;
    }

    private void OnRendering(object? sender, EventArgs e) => Clock = _elapsed.Elapsed.TotalSeconds;
}
