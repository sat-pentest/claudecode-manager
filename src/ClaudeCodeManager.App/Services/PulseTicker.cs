using System;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClaudeCodeManager.App.Services;

/// <summary>
/// App-wide pulse clock — a singleton whose <see cref="Opacity"/> oscillates on a fixed cadence.
/// Every "live/active" indicator in the app binds its Opacity to this so all pulses stay in
/// lockstep (any DispatcherTimer starting at a per-element trigger time would drift out of phase
/// across modules). 2.2s cycle, sine-eased, 0.35..1.0 range.
/// </summary>
public partial class PulseTicker : ObservableObject
{
    public static PulseTicker Current { get; } = new();

    [ObservableProperty] private double _opacity = 1.0;

    private readonly DateTime _start;
    private readonly DispatcherTimer _timer;

    private PulseTicker()
    {
        _start = DateTime.Now;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
        _timer.Tick += (_, _) =>
        {
            var t = (DateTime.Now - _start).TotalSeconds;
            var phase = (t % 2.2) / 2.2;
            var eased = 0.5 - 0.5 * Math.Cos(phase * Math.PI * 2);   // 0..1 sine
            Opacity = 0.35 + 0.65 * eased;
        };
        _timer.Start();
    }
}
