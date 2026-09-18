using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace ClaudeCodeManager.App.ViewModels;

/// <summary>
/// Which part of a pipeline phase card the sweep is driving.
/// </summary>
public enum PipelineSweepMode
{
    /// <summary>Card outline — soft border at rest, ember at the crest.</summary>
    CardBorder,
    /// <summary>Card fill — panel at rest, warms slightly under the crest.</summary>
    CardBackground,
    /// <summary>Ember bloom, only near the crest.</summary>
    CardGlow,
    /// <summary>Barely-there swell so the crest reads as physical, not just brighter.</summary>
    CardScale,
    /// <summary>Number badge — rust at rest, ember at the crest.</summary>
    BadgeBackground,
    /// <summary>Phase title — ember at rest, near-white at the crest.</summary>
    TitleBrush,
    /// <summary>Detail line — muted at rest, full text colour at the crest.</summary>
    DetailBrush,
    /// <summary>The ↓ between this card and the next; peaks as the crest crosses the gap.</summary>
    ArrowBrush,
}

/// <summary>
/// Drives the WORKFLOWS pipeline sweep. Deliberately *not* the HARNESS choreography: there the
/// diagram starts dark and a packet lights each node for the first time, because the point is a
/// request travelling a path that might break. A pipeline has no such story — every phase is real
/// and already on — so this is a crest of emphasis passing over cards that stay lit throughout.
/// At w = 0 every mode returns exactly the card's resting appearance, so the sweep only ever adds.
///
/// Expected MultiBinding values:
///   [0] Clock (double, seconds — <see cref="Services.PipelineTicker"/>)
///   [1] Index (int, 1-based position of this phase)
///   [2] Count (int, phases in the pipeline)
/// </summary>
public sealed class PipelineSweepConverter : IMultiValueConverter
{
    public PipelineSweepMode Mode { get; set; }

    public static readonly PipelineSweepConverter CardBorder      = new() { Mode = PipelineSweepMode.CardBorder };
    public static readonly PipelineSweepConverter CardBackground  = new() { Mode = PipelineSweepMode.CardBackground };
    public static readonly PipelineSweepConverter CardGlow        = new() { Mode = PipelineSweepMode.CardGlow };
    public static readonly PipelineSweepConverter CardScale       = new() { Mode = PipelineSweepMode.CardScale };
    public static readonly PipelineSweepConverter BadgeBackground = new() { Mode = PipelineSweepMode.BadgeBackground };
    public static readonly PipelineSweepConverter TitleBrush      = new() { Mode = PipelineSweepMode.TitleBrush };
    public static readonly PipelineSweepConverter DetailBrush     = new() { Mode = PipelineSweepMode.DetailBrush };
    public static readonly PipelineSweepConverter ArrowBrush      = new() { Mode = PipelineSweepMode.ArrowBrush };

    /// <summary>
    /// Seconds the crest spends advancing one card.
    ///
    /// Was 0.36, which put the crest across an eight-phase pipeline in under three seconds. The
    /// cards are tall, so that worked out around 380 px/s -- fast enough to read as flicker rather
    /// than as something travelling.
    /// </summary>
    private const double StepSeconds = 0.82;

    /// <summary>Dead time after the crest leaves the last card, so the loop reads as a repeat
    /// rather than a wrap-around.</summary>
    private const double TailSeconds = 2.0;

    /// <summary>Crest half-width in cards. Wide enough to touch two or three at once, which is what
    /// makes it read as a wave instead of a marching selection.</summary>
    private const double CardHalfWidth = 1.25;

    /// <summary>Narrower for arrows -- a connector shouldn't stay lit across three cards.</summary>
    private const double ArrowHalfWidth = 0.85;

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var clock = values is { Length: > 0 } && values[0] is double c ? c : 0.0;
        var index = values is { Length: > 1 } && values[1] is int i ? i : 1;
        var count = values is { Length: > 2 } && values[2] is int n && n > 0 ? n : 1;

        // Crest position in card units. It starts one crest-width ABOVE the first card and ends one
        // below the last, so the wave slides in and out of the pipeline instead of appearing inside
        // it. Starting at 0 -- the top edge of card 1 -- put the crest centre only half a card away
        // from card 1 at the instant the cycle restarted, so that card jumped straight to about
        // half intensity: a visible pop on every loop, and the main reason the sweep read as
        // unnatural rather than merely quick.
        var travel = count + 2 * CardHalfWidth;
        var cycle = travel * StepSeconds + TailSeconds;
        var pos = (clock % cycle) / StepSeconds - CardHalfWidth;

        // Cards are centred mid-slot; the arrow after card `index` sits on the slot boundary.
        var isArrow = Mode == PipelineSweepMode.ArrowBrush;
        var centre = isArrow ? index : index - 0.5;
        var w = Window(pos - centre, isArrow ? ArrowHalfWidth : CardHalfWidth);

        var level = (int)Math.Round(w * Levels);

        return Mode switch
        {
            PipelineSweepMode.CardBorder      => Ramp(ref _borderRamp, BorderSoft, Ember, level),
            PipelineSweepMode.CardBackground  => Ramp(ref _bgRamp, BgPanel, BgHover, level),
            PipelineSweepMode.BadgeBackground => Ramp(ref _badgeRamp, Rust, Ember, level),
            PipelineSweepMode.TitleBrush      => Ramp(ref _titleRamp, Ember, Hot, level),
            PipelineSweepMode.DetailBrush     => Ramp(ref _detailRamp, TextMuted, Text, level),
            PipelineSweepMode.ArrowBrush      => Ramp(ref _arrowRamp, Ember, Hot, level),
            // Below a third of the crest the bloom is invisible but still costs a render layer.
            PipelineSweepMode.CardGlow        => level < Levels / 3 ? null! : Glow(level),
            PipelineSweepMode.CardScale       => (object)(1.0 + 0.014 * w),
            _ => DependencyProperty.UnsetValue
        };
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => Array.Empty<object>();

    // ── helpers ───────────────────────────────────────────────────────────

    /// <summary>Raised cosine — 1 at the crest, 0 at ±half, smooth at both ends so nothing snaps.</summary>
    private static double Window(double d, double half)
    {
        var t = Math.Abs(d) / half;
        return t >= 1 ? 0.0 : 0.5 * (1 + Math.Cos(t * Math.PI));
    }

    /// <summary>Intensity steps. The sweep repaints ~30×/s across every card on screen, so the
    /// brushes are quantised and cached rather than allocated per frame; 20 steps is past the
    /// point where the banding is visible on these colour pairs.</summary>
    private const int Levels = 20;

    private static object Ramp(ref Brush[]? cache, Brush from, Brush to, int level)
    {
        var ramp = cache ??= Build(from, to);
        return ramp[Math.Clamp(level, 0, Levels)];
    }

    private static Brush[] Build(Brush from, Brush to)
    {
        var a = ((SolidColorBrush)from).Color;
        var b = ((SolidColorBrush)to).Color;
        var ramp = new Brush[Levels + 1];
        for (var i = 0; i <= Levels; i++)
        {
            var t = (double)i / Levels;
            ramp[i] = i == 0 ? from : i == Levels ? to : Freeze(new SolidColorBrush(LerpColor(a, b, t)));
        }
        return ramp;
    }

    private static DropShadowEffect[]? _glowRamp;
    private static DropShadowEffect Glow(int level)
    {
        var ramp = _glowRamp ??= BuildGlow();
        return ramp[Math.Clamp(level, 0, Levels)];
    }

    private static DropShadowEffect[] BuildGlow()
    {
        var ramp = new DropShadowEffect[Levels + 1];
        for (var i = 0; i <= Levels; i++)
        {
            var t = (double)i / Levels;
            var e = new DropShadowEffect
            {
                Color = Color.FromRgb(0xF2, 0x6A, 0x2E),
                BlurRadius = 8 + 14 * t,
                ShadowDepth = 0,
                Opacity = 0.55 * t
            };
            e.Freeze();
            ramp[i] = e;
        }
        return ramp;
    }

    private static Color LerpColor(Color a, Color b, double t) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    // Palette from the live theme so the sweep can never drift from RustTheme.xaml; the literals
    // are only a design-time fallback, where no Application exists.
    private static Brush Res(string key, string fallback)
    {
        if (Application.Current?.TryFindResource(key) is Brush b) return b;
        return Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback)));
    }

    private static Brush? _ember, _rust, _borderSoft, _bgPanel, _bgHover, _text, _textMuted, _hot;
    private static Brush Ember      => _ember      ??= Res("B.Ember",      "#FFF26A2E");
    private static Brush Rust       => _rust       ??= Res("B.Rust",       "#FFD9441C");
    private static Brush BorderSoft => _borderSoft ??= Res("B.BorderSoft", "#FF2A1408");
    private static Brush BgPanel    => _bgPanel    ??= Res("B.BG.Panel",   "#FF14110F");
    private static Brush BgHover    => _bgHover    ??= Res("B.BG.Hover",   "#FF1F1A16");
    private static Brush Text       => _text       ??= Res("B.Text",       "#FFE8DDD0");
    private static Brush TextMuted  => _textMuted  ??= Res("B.TextMuted",  "#FF9A8A78");
    /// <summary>Crest colour — hotter than ember, kept out of the theme because nothing at rest
    /// should ever be this bright.</summary>
    private static Brush Hot        => _hot        ??= Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xC9, 0xA0)));

    private static Brush[]? _borderRamp, _bgRamp, _badgeRamp, _titleRamp, _detailRamp, _arrowRamp;
}
