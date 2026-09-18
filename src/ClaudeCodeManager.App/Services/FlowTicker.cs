using System;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClaudeCodeManager.App.Services;

/// <summary>
/// App-wide clock for the HARNESS flow diagrams. Shares the singleton rationale of
/// <see cref="PulseTicker"/> — one clock keeps every diagram on screen in step instead of each
/// element's storyboard drifting apart — but drives a *chain walk* rather than a pulse.
///
/// One 3s cycle runs a request down a flow: the path draws itself hop by hop, each node lights
/// as the packet reaches it, everything holds briefly, then fades out and the cycle restarts.
/// <see cref="ChainProgress"/> is 0..1 across the whole chain (consumers scale it by their hop
/// count to find their own segment); <see cref="ChainAlpha"/> is the fade-out envelope so the
/// reset reads as a decay instead of a cut.
/// </summary>
public partial class FlowTicker : ObservableObject
{
    public static FlowTicker Current { get; } = new();

    /// <summary>0..1 position of the request along the whole chain.</summary>
    [ObservableProperty] private double _chainProgress;

    /// <summary>1.0 while the chain runs and holds, ramping to 0 just before the cycle restarts.</summary>
    [ObservableProperty] private double _chainAlpha;

    private const double CycleSeconds = 3.0;
    private const double WalkEnd = 0.78;   // path is fully drawn at 78% of the cycle
    private const double HoldEnd = 0.92;   // fully-lit hold until 92%, then fade

    private DateTime _start;
    private readonly DispatcherTimer _timer;

    /// <summary>Restart the cycle from the top — used when the user picks a different flow, so the
    /// click is answered by a run from the entry node rather than by joining a cycle mid-way.</summary>
    public void Restart() => _start = DateTime.Now;

    private FlowTicker()
    {
        _start = DateTime.Now;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) =>
        {
            var p = ((DateTime.Now - _start).TotalSeconds % CycleSeconds) / CycleSeconds;
            if (p <= WalkEnd)
            {
                // Linear here on purpose — the per-hop ease (accelerate off a node, settle into
                // the next) is applied downstream in FlowChainConverter, where the hop boundaries
                // are known. Easing the whole walk instead would make middle hops race.
                ChainProgress = p / WalkEnd;
                ChainAlpha = 1.0;
            }
            else if (p <= HoldEnd)
            {
                ChainProgress = 1.0;
                ChainAlpha = 1.0;
            }
            else
            {
                ChainProgress = 1.0;
                ChainAlpha = 1.0 - (p - HoldEnd) / (1.0 - HoldEnd);
            }
        };
        _timer.Start();
    }
}
