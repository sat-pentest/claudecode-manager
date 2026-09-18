using System;
using System.Windows.Threading;

namespace ClaudeCodeManager.App.Services;

/// <summary>
/// A <see cref="DispatcherTimer"/> that stops costing anything when nobody is looking.
///
/// Two behaviours on top of a plain timer:
///
/// <list type="bullet">
/// <item>Visibility — while <see cref="AppVisibility"/> reports the shell hidden or minimised the
/// timer is stopped outright. On the way back it fires once immediately rather than waiting out
/// the remaining interval, so a restored window is never showing stale data.</item>
/// <item>Backoff — the tick callback returns whether it actually saw new data. Repeated "nothing
/// changed" answers widen the interval up to <c>base * maxBackoffMultiplier</c>; the first change
/// snaps it straight back to the base interval. Pass a multiplier of 1 to opt out, which is what
/// a pure UI clock wants — it has no notion of new data, it just needs to stop while hidden.</item>
/// </list>
///
/// The callback runs on the dispatcher thread, exactly like the timer it replaces.
/// </summary>
public sealed class SmartPollTimer
{
    private readonly DispatcherTimer _timer;
    private readonly Func<bool> _onTick;
    private readonly TimeSpan _baseInterval;
    private readonly int _maxBackoffMultiplier;

    /// <summary>Set by Start/Stop — the caller's intent, independent of whether the timer is
    /// currently suppressed for visibility.</summary>
    private bool _wanted;
    private int _multiplier = 1;

    /// <param name="interval">Base cadence while data keeps changing.</param>
    /// <param name="onTick">Poll body. Return true when it observed a change, false when the poll
    /// was a no-op; the return value only drives backoff.</param>
    /// <param name="maxBackoffMultiplier">Upper bound on interval widening. 1 disables backoff.</param>
    /// <param name="priority">Dispatcher priority, Background by default so polling never competes
    /// with rendering or input.</param>
    public SmartPollTimer(
        TimeSpan interval,
        Func<bool> onTick,
        int maxBackoffMultiplier = 3,
        DispatcherPriority priority = DispatcherPriority.Background)
    {
        _baseInterval = interval;
        _onTick = onTick;
        _maxBackoffMultiplier = Math.Max(1, maxBackoffMultiplier);
        _timer = new DispatcherTimer(priority) { Interval = interval };
        _timer.Tick += (_, _) => Fire();

        AppVisibility.Current.Shown += (_, _) => OnShown();
        AppVisibility.Current.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppVisibility.IsVisible) && !AppVisibility.Current.IsVisible)
                _timer.Stop();
        };
    }

    /// <summary>Cadence currently in effect, for status text.</summary>
    public TimeSpan CurrentInterval => _timer.Interval;

    /// <summary>True while backoff has widened the interval past its base.</summary>
    public bool IsBackedOff => _multiplier > 1;

    public void Start()
    {
        _wanted = true;
        ResetInterval();
        if (AppVisibility.Current.IsVisible) _timer.Start();
    }

    public void Stop()
    {
        _wanted = false;
        _timer.Stop();
    }

    /// <summary>Poll now and reset the backoff, whatever the timer was about to do.</summary>
    public void PollNow()
    {
        ResetInterval();
        Fire();
    }

    private void OnShown()
    {
        if (!_wanted) return;
        // Whatever accumulated while hidden is unseen by definition, so come back at full cadence
        // and refresh before the first interval elapses.
        ResetInterval();
        _timer.Start();
        Fire();
    }

    private void Fire()
    {
        bool changed;
        try
        {
            changed = _onTick();
        }
        catch
        {
            // A poll that throws is treated as "no news": it must not widen into a silent stall,
            // and it must not take the timer down with it.
            changed = false;
        }

        if (changed) ResetInterval();
        else Widen();
    }

    private void ResetInterval()
    {
        _multiplier = 1;
        if (_timer.Interval != _baseInterval) _timer.Interval = _baseInterval;
    }

    private void Widen()
    {
        if (_multiplier >= _maxBackoffMultiplier) return;
        _multiplier++;
        // Assigning Interval restarts the countdown, so the widened gap begins from now.
        _timer.Interval = _baseInterval * _multiplier;
    }
}
