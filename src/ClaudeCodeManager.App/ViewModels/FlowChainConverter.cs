using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace ClaudeCodeManager.App.ViewModels;

/// <summary>
/// What a bound element wants to know about the running chain walk.
/// </summary>
public enum FlowChainMode
{
    /// <summary>Canvas.Left for the travelling packet.</summary>
    PacketLeft,
    /// <summary>Packet shown only while the request is inside this hop.</summary>
    PacketVisibility,
    /// <summary>Packet head opacity — fades in/out at the hop ends.</summary>
    PacketOpacity,
    /// <summary>Length of the drawn (live) portion of this hop's rail.</summary>
    RailLength,
    /// <summary>Brush for the drawn portion — ember while live, danger for a broken hop.</summary>
    RailBrush,
    /// <summary>Arrowhead lights only once the hop has actually been traversed.</summary>
    ArrowBrush,
    /// <summary>Node body opacity — unreached nodes recede, reached ones come up full.</summary>
    NodeOpacity,
    /// <summary>Node outline colour by state (lit / waiting / unreachable).</summary>
    NodeStroke,
    /// <summary>Dashed outline for a node the request can never reach.</summary>
    NodeDash,
    /// <summary>Glow that switches on as the request lands on the node.</summary>
    NodeGlow,
    /// <summary>✕ marker on a broken hop — only after the packet has run into it.</summary>
    BreakVisibility,
    /// <summary>Fan-out worker row: dims until its own copy has been dispatched.</summary>
    WorkerOpacity,
}

/// <summary>
/// Single converter behind every animated part of a HARNESS flow diagram. One class with a Mode
/// keeps the whole choreography — hop timing, easing, break handling — in one place; splitting it
/// across a dozen converters made the timing rules drift apart.
///
/// Expected MultiBinding values:
///   [0] ChainProgress (double 0..1, whole chain)
///   [1] ChainAlpha    (double 0..1, fade-out envelope)
///   [2] Index         (int, this step's position in the flow)
///   [3] HopCount      (int, hops in the flow)
///   [4] IsReachable   (bool, this step and everything upstream is active)
///   [5] WorkerIndex   (int, optional — only for WorkerOpacity)
///
/// A step's *incoming* hop is hop number Index (entry→step0 is hop 0), and its *outgoing* rail is
/// hop Index+1. Connectors bind as the outgoing rail; nodes bind as the incoming arrival.
/// </summary>
public sealed class FlowChainConverter : IMultiValueConverter
{
    public FlowChainMode Mode { get; set; }

    public static readonly FlowChainConverter PacketLeft      = new() { Mode = FlowChainMode.PacketLeft };
    public static readonly FlowChainConverter PacketVisibility= new() { Mode = FlowChainMode.PacketVisibility };
    public static readonly FlowChainConverter PacketOpacity   = new() { Mode = FlowChainMode.PacketOpacity };
    public static readonly FlowChainConverter RailLength      = new() { Mode = FlowChainMode.RailLength };
    public static readonly FlowChainConverter RailBrush       = new() { Mode = FlowChainMode.RailBrush };
    public static readonly FlowChainConverter ArrowBrush      = new() { Mode = FlowChainMode.ArrowBrush };
    public static readonly FlowChainConverter NodeOpacity     = new() { Mode = FlowChainMode.NodeOpacity };
    public static readonly FlowChainConverter NodeStroke      = new() { Mode = FlowChainMode.NodeStroke };
    public static readonly FlowChainConverter NodeDash        = new() { Mode = FlowChainMode.NodeDash };
    public static readonly FlowChainConverter NodeGlow        = new() { Mode = FlowChainMode.NodeGlow };
    public static readonly FlowChainConverter BreakVisibility = new() { Mode = FlowChainMode.BreakVisibility };
    public static readonly FlowChainConverter WorkerOpacity   = new() { Mode = FlowChainMode.WorkerOpacity };

    /// <summary>Rail length in device pixels. The connector is 92 wide and the rail runs the whole
    /// gap edge to edge, leaving the last 6px to the arrowhead so the path visibly joins both boxes.</summary>
    private const double RailPixels = 86;
    /// <summary>How far the 16px-wide head travels before its leading edge meets the arrowhead.</summary>
    private const double PacketTravel = 70;
    /// <summary>A broken hop only draws to its ✕ at the midpoint.</summary>
    private const double BreakFraction = 0.5;

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var progress = Get(values, 0);
        var alpha    = values is { Length: > 1 } && values[1] is double a ? a : 1.0;
        var index    = GetInt(values, 2);
        var hops     = Math.Max(1, GetInt(values, 3));
        var reach    = values is { Length: > 4 } && values[4] is bool b && b;

        // Position of the request along the chain, in hop units.
        var chainPos = progress * hops;

        // ── Node modes: driven by the *incoming* hop (arrives at chainPos >= index + 1) ──
        if (Mode is FlowChainMode.NodeOpacity or FlowChainMode.NodeStroke
                 or FlowChainMode.NodeDash or FlowChainMode.NodeGlow)
        {
            var arrived = reach && chainPos >= index + 1;
            return Mode switch
            {
                FlowChainMode.NodeOpacity => (object)(reach ? (arrived ? 0.45 + 0.55 * alpha : 0.45) : 0.4),
                FlowChainMode.NodeStroke  => (object)(!reach ? Danger
                                             : arrived ? Lerp(BorderSoft, Ember, alpha)
                                             : BorderSoft),
                FlowChainMode.NodeDash    => reach ? (object?)null! : Dashed,
                FlowChainMode.NodeGlow    => arrived && alpha > 0.5 ? (object)EmberGlow : null!,
                _ => DependencyProperty.UnsetValue
            };
        }

        // ── Worker rows: dispatched one after another once the fan-out node is live ──
        if (Mode == FlowChainMode.WorkerOpacity)
        {
            var worker = GetInt(values, 5);
            var arrived = reach && chainPos >= index + 1;
            if (!arrived) return 0.3;
            // Stagger the copies across the hop that follows; during the hold everything is out.
            var dispatched = progress >= 0.99 || chainPos >= index + 1 + worker * 0.18;
            return (object)(dispatched ? 0.35 + 0.65 * alpha : 0.3);
        }

        // ── Connector modes: this step's *outgoing* rail is hop index + 1 ──
        var local = chainPos - (index + 1);            // 0 = leaving this node, 1 = at the next
        var eased = Ease(Clamp01(local));
        var entered = local > 0;
        var traversed = local >= 1;

        // A hop into an unreachable step never completes — the packet runs to the ✕ and stops.
        var limit = reach ? 1.0 : BreakFraction;
        var drawn = Math.Min(eased, limit);

        return Mode switch
        {
            FlowChainMode.RailLength => (object)(drawn * RailPixels),
            FlowChainMode.RailBrush => (object)(reach ? Ember : Danger),
            FlowChainMode.ArrowBrush => (object)(traversed && reach ? Lerp(RustDim, Ember, alpha) : RustDim),
            FlowChainMode.PacketLeft => (object)(Math.Min(eased, limit) * PacketTravel),
            FlowChainMode.PacketVisibility => (object)(entered && (reach ? !traversed : eased < limit + 0.15)
                                              ? Visibility.Visible : Visibility.Collapsed),
            // Fade the head in as it leaves and out as it lands, so it doesn't pop at either end.
            FlowChainMode.PacketOpacity => (object)(entered ? alpha * Envelope(Clamp01(local)) : 0.0),
            FlowChainMode.BreakVisibility => (object)(!reach && eased >= limit ? Visibility.Visible : Visibility.Collapsed),
            _ => DependencyProperty.UnsetValue
        };
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => Array.Empty<object>();

    // ── helpers ───────────────────────────────────────────────────────────

    private static double Get(object[] v, int i) => v is not null && v.Length > i && v[i] is double d ? d : 0.0;

    private static int GetInt(object[] v, int i) => v is not null && v.Length > i && v[i] is int n ? n : 0;

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    /// <summary>Ease-in-out per hop: leaves a node quickly, settles into the next.</summary>
    private static double Ease(double t) => 0.5 - 0.5 * Math.Cos(t * Math.PI);

    /// <summary>Head opacity envelope — up over the first 20% of a hop, down over the last 20%.</summary>
    private static double Envelope(double t) => t < 0.2 ? t / 0.2 : t > 0.8 ? (1 - t) / 0.2 : 1.0;

    private static Brush Lerp(Brush from, Brush to, double t)
        => t >= 0.999 ? to : t <= 0.001 ? from : Freeze(new SolidColorBrush(
            LerpColor(((SolidColorBrush)from).Color, ((SolidColorBrush)to).Color, t)));

    private static Color LerpColor(Color a, Color b, double t) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    // Palette is pulled from the live theme so the diagram can never drift from RustTheme.xaml;
    // the literals are only a fallback for design-time, where no Application exists.
    private static Brush Res(string key, string fallback)
    {
        if (Application.Current?.TryFindResource(key) is Brush b) return b;
        return Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback)));
    }

    private static Brush? _ember, _danger, _borderSoft, _rustDim;
    private static Brush Ember      => _ember      ??= Res("B.Ember",      "#FFF26A2E");
    private static Brush Danger     => _danger     ??= Res("B.Danger",     "#FFB22020");
    private static Brush BorderSoft => _borderSoft ??= Res("B.BorderSoft", "#FF2A1408");
    private static Brush RustDim    => _rustDim    ??= Res("B.RustDim",    "#FF7A2812");

    private static readonly DoubleCollection Dashed = Frozen(new DoubleCollection { 3, 3 });
    private static DoubleCollection Frozen(DoubleCollection c) { c.Freeze(); return c; }

    private static readonly DropShadowEffect EmberGlow = Frozen(new DropShadowEffect
    {
        Color = Color.FromRgb(0xF2, 0x6A, 0x2E),
        BlurRadius = 12,
        ShadowDepth = 0,
        Opacity = 0.65
    });
    private static DropShadowEffect Frozen(DropShadowEffect e) { e.Freeze(); return e; }
}
