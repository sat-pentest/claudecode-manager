using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClaudeCodeManager.Core.Models;
using ClaudeCodeManager.Core.Services;

namespace ClaudeCodeManager.App.Views;

/// <summary>
/// The memory link graph, drawn directly rather than as bound shapes.
///
/// ~100 nodes and ~270 edges as WPF Shape objects would mean ~370 visuals each carrying a
/// template, hit-test geometry and layout slot, all re-laid-out every simulation tick. OnRender
/// draws the same picture into one visual with no element tree, which is what makes a live force
/// simulation affordable here.
/// </summary>
public sealed class MemoryGraphCanvas : FrameworkElement
{
    // ── public surface ────────────────────────────────────────────────────

    public static readonly DependencyProperty GraphProperty = DependencyProperty.Register(
        nameof(Graph), typeof(MemoryGraph), typeof(MemoryGraphCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnGraphChanged));

    public MemoryGraph? Graph
    {
        get => (MemoryGraph?)GetValue(GraphProperty);
        set => SetValue(GraphProperty, value);
    }

    /// <summary>Slug of the node the user picked. Two-way so the list and the graph agree on
    /// selection — clicking a node in either place moves both.</summary>
    public static readonly DependencyProperty SelectedSlugProperty = DependencyProperty.Register(
        nameof(SelectedSlug), typeof(string), typeof(MemoryGraphCanvas),
        new FrameworkPropertyMetadata(null,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender,
            OnSelectedSlugChanged));

    public string? SelectedSlug
    {
        get => (string?)GetValue(SelectedSlugProperty);
        set => SetValue(SelectedSlugProperty, value);
    }

    /// <summary>
    /// Whether deactivated entries (.md.disabled) take part in the graph. A third of this directory
    /// is disabled; drawn hollow they are still the most numerous single thing on screen, and when
    /// the question is "what does Claude Code actually load", they are noise.
    ///
    /// Filtering happens here rather than before the graph is built, so the link classification
    /// stays correct: a link to a disabled entry points at a file that exists, and would otherwise
    /// be miscounted as a link to a memory nobody has written yet.
    /// </summary>
    public static readonly DependencyProperty ShowDisabledProperty = DependencyProperty.Register(
        nameof(ShowDisabled), typeof(bool), typeof(MemoryGraphCanvas),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender, OnShowDisabledChanged));

    public bool ShowDisabled
    {
        get => (bool)GetValue(ShowDisabledProperty);
        set => SetValue(ShowDisabledProperty, value);
    }

    private static void OnShowDisabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((MemoryGraphCanvas)d).Rebuild();

    private static void OnSelectedSlugChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // A selection starts the edge flow, so frames are needed again even though the layout has
        // long since settled.
        var c = (MemoryGraphCanvas)d;
        if (!string.IsNullOrEmpty(c.SelectedSlug)) c.Subscribe();
    }

    /// <summary>Raised when a node is activated (double-click) — the view opens that entry.</summary>
    public event EventHandler<string>? NodeActivated;

    // ── internals ─────────────────────────────────────────────────────────

    private readonly ForceLayout _layout = new();
    private readonly List<MemoryNode> _nodes = new();
    private readonly Dictionary<string, int> _index = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(int A, int B, MemoryLinkKind Kind)> _edges = new();
    private readonly HashSet<string> _neighbours = new(StringComparer.OrdinalIgnoreCase);

    private double _scale = 1.0;
    private Vector _pan;
    private bool _fitPending = true;

    /// <summary>Drives the dash flow on the focused node's edges. Its own clock rather than the
    /// layout's alpha, because the flow has to keep running long after the simulation has settled.</summary>
    private readonly System.Diagnostics.Stopwatch _flow = System.Diagnostics.Stopwatch.StartNew();

    private int _hover = -1;
    private int _drag = -1;
    private Point _lastMouse;
    private bool _panning;
    private bool _subscribed;

    public MemoryGraphCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        // Without a background the element is transparent to hit-testing and no mouse event lands.
        // A Freezable brush from the theme cannot be relied on at construction time, so paint it in
        // OnRender instead and keep this element hit-visible by drawing that rect every frame.
        Unloaded += (_, _) => Unsubscribe();
        Loaded += (_, _) => { if (_layout.Nodes.Count > 0 && !_layout.Settled) Subscribe(); };
    }

    private static void OnGraphChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((MemoryGraphCanvas)d).Rebuild();

    private void Rebuild()
    {
        Unsubscribe();
        _nodes.Clear(); _index.Clear(); _edges.Clear(); _neighbours.Clear();
        _layout.Nodes.Clear(); _layout.Edges.Clear();
        _hover = _drag = -1;
        _fitPending = true;

        var g = Graph;
        if (g is null || g.Nodes.Count == 0) { InvalidateVisual(); return; }

        foreach (var n in g.Nodes)
        {
            if (!ShowDisabled && n.Disabled) continue;
            _index[n.Slug] = _nodes.Count;
            _nodes.Add(n);
            _layout.Nodes.Add(new LayoutNode { Id = n.Slug, Weight = 1 + Math.Sqrt(n.Degree) * 0.45 });
        }

        // Two entries that link to each other, or link twice, are one relationship. Feeding the
        // duplicate to the simulation makes that spring twice as stiff and skews the layout toward
        // whichever pairs happen to be written both ways (measured: 27 of 271 links were duplicates).
        var seen = new Dictionary<(int, int), int>();
        foreach (var l in g.Links)
        {
            var to = l.ResolvedTo ?? l.To;
            if (!_index.TryGetValue(l.From, out var a) || !_index.TryGetValue(to, out var b) || a == b) continue;
            _edges.Add((a, b, l.Kind));   // drawing keeps every link so the kinds stay visible
            var key = a < b ? (a, b) : (b, a);
            if (seen.ContainsKey(key)) continue;
            seen[key] = _layout.Edges.Count;
            // An unwritten target is a weaker relationship than a real one — let it sit further out
            // so the hub it hangs off stays legible.
            _layout.Edges.Add(new LayoutEdge { A = a, B = b, Strength = l.Kind == MemoryLinkKind.Unwritten ? 0.55 : 1.0 });
        }

        ApplyAspect();
        _layout.Seed();
        // Settle off-screen first. Watching ~100 nodes fly apart from a ring is not informative,
        // and the whole simulation costs ~14 ms, so there is nothing to gain by showing it.
        _layout.RunToSettle();
        InvalidateVisual();
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

    /// <summary>Whether anything on screen still changes frame to frame.</summary>
    private bool NeedsFrames
        => !_layout.Settled || _drag >= 0 || _hover >= 0 || !string.IsNullOrEmpty(SelectedSlug);

    private void OnRendering(object? sender, EventArgs e)
    {
        _layout.Tick();
        InvalidateVisual();
        if (!NeedsFrames) Unsubscribe();
    }

    // ── transforms ────────────────────────────────────────────────────────

    private Point ToScreen(LayoutNode n)
        => new(n.X * _scale + _pan.X + ActualWidth / 2, n.Y * _scale + _pan.Y + ActualHeight / 2);

    private Point ToWorld(Point p)
        => new((p.X - _pan.X - ActualWidth / 2) / _scale, (p.Y - _pan.Y - ActualHeight / 2) / _scale);

    /// <summary>Scale and centre the laid-out graph into the viewport.</summary>
    public void FitToView()
    {
        if (_layout.Nodes.Count == 0 || ActualWidth < 10 || ActualHeight < 10) return;
        var (x0, y0, x1, y1) = _layout.Bounds();
        var w = Math.Max(1, x1 - x0);
        var h = Math.Max(1, y1 - y0);
        // Margin leaves room for the labels, which extend past the node circles.
        _scale = Math.Min((ActualWidth - 120) / w, (ActualHeight - 80) / h);
        _scale = Math.Clamp(_scale, 0.12, 2.5);
        var cx = (x0 + x1) / 2;
        var cy = (y0 + y1) / 2;
        _pan = new Vector(-cx * _scale, -cy * _scale);
        _fitPending = false;
        InvalidateVisual();
    }

    private void ApplyAspect()
    {
        if (ActualWidth > 10 && ActualHeight > 10) _layout.SetAspect(ActualWidth / ActualHeight);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        // The graph is laid out before the element has a size, so the first real size is the first
        // chance to give it the viewport's proportions. Re-settling here is cheap (~14 ms) and only
        // happens while the layout has not been hand-arranged.
        if (_fitPending && _layout.Nodes.Count > 0)
        {
            ApplyAspect();
            _layout.Seed();
            _layout.RunToSettle();
        }
        if (_fitPending) FitToView();
    }

    // ── interaction ───────────────────────────────────────────────────────

    private int HitTestNode(Point screen)
    {
        // Back to front: the last drawn node is the one on top.
        for (var i = _nodes.Count - 1; i >= 0; i--)
        {
            var p = ToScreen(_layout.Nodes[i]);
            var r = RadiusOf(i) * _scale + 4;
            var dx = screen.X - p.X;
            var dy = screen.Y - p.Y;
            if (dx * dx + dy * dy <= r * r) return i;
        }
        return -1;
    }

    private double RadiusOf(int i)
    {
        var n = _nodes[i];
        // Wider spread than before (was 5.0 + sqrt*2.2, i.e. 5..15 px). Hubs carry the structure of
        // this graph, so they should be findable at a glance rather than merely slightly larger.
        return n.IsGhost ? 3.5 : 4.0 + Math.Sqrt(n.Degree) * 3.0;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var pos = e.GetPosition(this);

        if (_drag >= 0)
        {
            var w = ToWorld(pos);
            var ln = _layout.Nodes[_drag];
            ln.X = w.X; ln.Y = w.Y;
            _layout.Reheat(0.35);
            Subscribe();
            InvalidateVisual();
            return;
        }

        if (_panning)
        {
            _pan += pos - _lastMouse;
            _lastMouse = pos;
            InvalidateVisual();
            return;
        }

        var hit = HitTestNode(pos);
        if (hit == _hover) return;
        _hover = hit;
        if (hit >= 0) Subscribe();
        RecomputeNeighbours(hit);
        Cursor = hit >= 0 ? Cursors.Hand : Cursors.Arrow;
        ToolTip = hit >= 0 ? BuildTip(_nodes[hit]) : null;
        InvalidateVisual();
    }

    private void RecomputeNeighbours(int i)
    {
        _neighbours.Clear();
        if (i < 0) return;
        foreach (var (a, b, _) in _edges)
        {
            if (a == i) _neighbours.Add(_nodes[b].Slug);
            else if (b == i) _neighbours.Add(_nodes[a].Slug);
        }
    }

    private static string BuildTip(MemoryNode n)
    {
        if (n.IsGhost)
            return $"{n.Slug}\n아직 작성되지 않은 메모 · 들어오는 링크 {n.InDegree}개";
        var type = n.Type == MemoryType.Unknown ? "type 없음" : n.Type.ToString().ToLowerInvariant();
        var dis = n.Disabled ? " · DISABLED" : "";
        var desc = n.Entry?.Frontmatter.Description;
        var tail = string.IsNullOrWhiteSpace(desc) ? "" : "\n" + (desc!.Length > 110 ? desc[..110] + "…" : desc);
        return $"{n.Slug}\n{type}{dis} · 나감 {n.OutDegree} · 들어옴 {n.InDegree}{tail}";
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        _neighbours.Clear();
        _panning = false;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var pos = e.GetPosition(this);
        var hit = HitTestNode(pos);

        if (e.ClickCount == 2)
        {
            if (hit >= 0 && !_nodes[hit].IsGhost) NodeActivated?.Invoke(this, _nodes[hit].Slug);
            else FitToView();
            return;
        }

        if (hit >= 0)
        {
            SelectedSlug = _nodes[hit].Slug;
            _drag = hit;
            _layout.Nodes[hit].Pinned = true;
        }
        else
        {
            _panning = true;
            _lastMouse = pos;
        }
        CaptureMouse();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_drag >= 0)
        {
            // Stay pinned: a node the user positioned should keep that position, otherwise the
            // simulation immediately undoes the arrangement they just made.
            _drag = -1;
        }
        _panning = false;
        ReleaseMouseCapture();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var pos = e.GetPosition(this);
        var before = ToWorld(pos);
        var factor = e.Delta > 0 ? 1.12 : 1 / 1.12;
        var next = Math.Clamp(_scale * factor, 0.12, 4.0);
        if (Math.Abs(next - _scale) < 1e-9) return;
        _scale = next;
        // Keep the point under the cursor fixed — zooming to the centre instead makes it
        // impossible to inspect a cluster near an edge.
        var after = ToWorld(pos);
        _pan += new Vector((after.X - before.X) * _scale, (after.Y - before.Y) * _scale);
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>Release every pinned node and re-run the simulation.</summary>
    public void Relayout()
    {
        foreach (var n in _layout.Nodes) n.Pinned = false;
        _layout.Seed();
        _layout.RunToSettle();
        FitToView();
    }

    // ── palette ───────────────────────────────────────────────────────────

    private static Brush Res(string key, string fallback)
    {
        if (Application.Current?.TryFindResource(key) is Brush b) return b;
        var br = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback));
        br.Freeze();
        return br;
    }

    private static Brush? _bg, _edge, _edgeHot, _danger, _muted, _text, _ember, _rust, _success, _warn, _dim;
    private static Brush Bg      => _bg      ??= Res("B.BG.Sunken", "#FF050403");
    private static Brush Edge    => _edge    ??= Res("B.BorderSoft", "#FF2A1408");
    private static Brush EdgeHot => _edgeHot ??= Res("B.Ember", "#FFF26A2E");
    private static Brush Danger  => _danger  ??= Res("B.Danger", "#FFB22020");
    private static Brush Muted   => _muted   ??= Res("B.TextMuted", "#FF9A8A78");
    private static Brush Text    => _text    ??= Res("B.Text", "#FFE8DDD0");
    private static Brush Ember   => _ember   ??= Res("B.Ember", "#FFF26A2E");
    private static Brush Rust    => _rust    ??= Res("B.Rust", "#FFD9441C");
    private static Brush Success => _success ??= Res("B.Success", "#FF6FA844");
    private static Brush Warn    => _warn    ??= Res("B.Warn", "#FFD9A02C");
    private static Brush Dim     => _dim     ??= Res("B.TextDim", "#FF5A4F44");

    // Edges need two weights below the theme's softest border: one for rest and one for the
    // background of a hover, which has to fall away without vanishing entirely.
    private static Brush? _edgeRest, _edgeFaint;
    private static Brush EdgeRest  => _edgeRest  ??= Frozen(Color.FromArgb(0x66, 0x6B, 0x38, 0x22));
    private static Brush EdgeFaint => _edgeFaint ??= Frozen(Color.FromArgb(0x2E, 0x4A, 0x27, 0x18));

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // Everything drawn per node or per edge is cached and frozen. Allocating inside the render loop
    // cost 27 ms/frame at ~100 nodes and ~270 edges — over the 16.7 ms a 60 Hz frame allows, which
    // showed up as stutter exactly while dragging, when the simulation is running.
    private static readonly Dictionary<Color, Brush> CoreCache = new();
    private static Brush Core(Color c)
    {
        if (CoreCache.TryGetValue(c, out var b)) return b;
        b = Frozen(Color.FromArgb(200,
            (byte)Math.Min(255, c.R + 55), (byte)Math.Min(255, c.G + 45), (byte)Math.Min(255, c.B + 40)));
        CoreCache[c] = b;
        return b;
    }

    private static readonly Dictionary<(string, double), Pen> PenCache = new();
    private static Pen CachedPen(Brush brush, double thickness, string key, DashStyle? dash = null)
    {
        if (PenCache.TryGetValue((key, thickness), out var p)) return p;
        p = new Pen(brush, thickness);
        if (dash is not null) p.DashStyle = dash;
        p.Freeze();
        PenCache[(key, thickness)] = p;
        return p;
    }

    private static DashStyle? _dashUnwritten, _dashGhost;
    private static DashStyle DashUnwritten => _dashUnwritten ??= FrozenDash(3, 3);
    private static DashStyle DashGhost => _dashGhost ??= FrozenDash(2, 2);
    private static DashStyle FrozenDash(double a, double b)
    {
        var d = new DashStyle(new[] { a, b }, 0);
        d.Freeze();
        return d;
    }

    private static Brush FillFor(MemoryNode n) => n.Type switch
    {
        MemoryType.Reference => Ember,
        MemoryType.Feedback => Warn,
        MemoryType.Project => Success,
        MemoryType.User => Rust,
        _ => Muted,
    };

    private static Color ColourFor(MemoryNode n) => ((SolidColorBrush)FillFor(n)).Color;

    /// <summary>
    /// Radial falloff behind each node, so a node reads as a light source rather than a flat disc.
    /// This is the single thing that separates a polished graph from a scatter of circles.
    ///
    /// Cached per colour and frozen: the alternative is building a gradient brush per node per
    /// frame, which at ~100 nodes and 60 fps is 6,000 brush allocations a second.
    /// </summary>
    /// <summary>
    /// Radial falloff behind each node, so a node reads as a light source rather than a flat disc.
    /// This is the single thing that separates a polished graph from a scatter of circles.
    ///
    /// Cached per colour and frozen; the alternative is a gradient brush per node per frame, which
    /// at ~100 nodes and 60 fps is 6,000 allocations a second.
    ///
    /// Baking these into bitmap sprites and blitting was tried and reverted — it moved neither
    /// drawing-instruction time (5.8 ms either way) nor rasterisation beyond run-to-run noise, so it
    /// was machinery with nothing to show for it. Instruction time is the number that matters here:
    /// it is what runs on the UI thread, while rasterisation happens on the render thread.
    /// </summary>
    private static readonly Dictionary<Color, RadialGradientBrush> GlowCache = new();

    private static RadialGradientBrush Glow(Color c)
    {
        if (GlowCache.TryGetValue(c, out var b)) return b;
        b = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(150, c.R, c.G, c.B), 0.0),
                new GradientStop(Color.FromArgb( 60, c.R, c.G, c.B), 0.40),
                new GradientStop(Color.FromArgb( 16, c.R, c.G, c.B), 0.72),
                new GradientStop(Color.FromArgb(  0, c.R, c.G, c.B), 1.0),
            },
        };
        b.Freeze();
        GlowCache[c] = b;
        return b;
    }

    /// <summary>Vignette. A flat fill reads as an empty panel; a centre that is a shade warmer
    /// gives the canvas depth and quietly pulls the eye to where the graph actually is.</summary>
    private static Brush? _ground;
    private static Brush Ground
    {
        get
        {
            if (_ground is not null) return _ground;
            var edge = ((SolidColorBrush)Bg).Color;
            var mid = Color.FromRgb(
                (byte)Math.Min(255, edge.R + 12), (byte)Math.Min(255, edge.G + 8), (byte)Math.Min(255, edge.B + 6));
            var g = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.5, 0.45),
                Center = new Point(0.5, 0.45),
                RadiusX = 0.75,
                RadiusY = 0.85,
                GradientStops = { new GradientStop(mid, 0.0), new GradientStop(edge, 1.0) },
            };
            g.Freeze();
            _ground = g;
            return g;
        }
    }

    /// <summary>The app's mono family, not a hardcoded face — the graph was the only surface in
    /// the app still rendering in Consolas.</summary>
    private static Typeface? _face;
    private static Typeface Face
    {
        get
        {
            if (_face is not null) return _face;
            var fam = Application.Current?.TryFindResource("Font.Mono") as FontFamily
                      ?? new FontFamily("Consolas");
            _face = new Typeface(fam, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            return _face;
        }
    }

    // ── render ────────────────────────────────────────────────────────────

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Ground, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_nodes.Count == 0)
        {
            DrawCentredText(dc, "표시할 메모리 그래프가 없습니다", Muted, 13);
            return;
        }
        if (_fitPending) FitToView();

        // Dimming is a hover gesture, not a selection state. Fading everything outside the
        // selected node's neighbourhood left 91% of the graph at 22% opacity as the *resting*
        // look — the canvas read as almost empty. Selection now only adds a ring and brighter
        // neighbours; the rest of the graph stays fully drawn until the mouse asks a question.
        var hovering = _hover >= 0;
        var focus = hovering ? _nodes[_hover].Slug : SelectedSlug;
        var focusing = !string.IsNullOrEmpty(focus);
        if (!hovering && focusing)
        {
            if (_index.TryGetValue(focus!, out var si)) RecomputeNeighbours(si);
        }

        // ── edges ──
        foreach (var (a, b, kind) in _edges)
        {
            var na = _nodes[a].Slug;
            var nb = _nodes[b].Slug;
            var lit = focusing && (na == focus || nb == focus);
            var dimmed = hovering && !lit;

            Brush stroke = kind switch
            {
                MemoryLinkKind.Misspelled => Danger,
                MemoryLinkKind.Unwritten => Dim,
                _ => lit ? EdgeHot : EdgeRest,
            };
            // Edges are context, not content. At 0.9 px of a fairly light colour they competed with
            // the nodes and the whole thing read as a wire ball; thinner and dimmer lets the nodes
            // carry the picture, which is what the eye should be reading first.
            var thickness = lit ? 1.5 : 0.7;
            if (dimmed) stroke = EdgeFaint;

            var pa = ToScreen(_layout.Nodes[a]);
            var pb = ToScreen(_layout.Nodes[b]);

            if (lit)
            {
                // Dashes travel outward from the focused node, so the picture answers "what does
                // this reach" with a direction rather than just a colour. Offset decreases over
                // time because a growing DashStyle.Offset walks the pattern back toward the start.
                var fromFocus = na == focus;
                var start = fromFocus ? pa : pb;
                var end = fromFocus ? pb : pa;

                // Offset is in multiples of pen thickness, so the pattern and the speed both have
                // to be expressed in those units to stay stable when the thickness changes.
                var dash = new DashStyle(new double[] { 2.2, 2.6 }, -_flow.Elapsed.TotalSeconds * 9.0 % 4.8);
                dash.Freeze();
                var flowPen = new Pen(stroke, thickness) { DashStyle = dash, DashCap = PenLineCap.Round };
                flowPen.Freeze();

                // A dim continuous line underneath keeps the connection readable between dashes.
                dc.DrawLine(CachedPen(EdgeRest, thickness, "lit-base"), start, end);
                dc.DrawLine(flowPen, start, end);
            }
            else
            {
                var key = $"{kind}|{(dimmed ? "dim" : "rest")}";
                var pen = CachedPen(stroke, thickness, key,
                    kind == MemoryLinkKind.Unwritten ? DashUnwritten : null);
                dc.DrawLine(pen, pa, pb);
            }
        }

        // ── nodes ──
        for (var i = 0; i < _nodes.Count; i++)
        {
            var n = _nodes[i];
            var p = ToScreen(_layout.Nodes[i]);
            var r = RadiusOf(i) * Math.Max(0.55, Math.Min(1.4, _scale));
            var isFocus = focusing && n.Slug == focus;
            var isNear = _neighbours.Contains(n.Slug);
            var faded = hovering && !isFocus && !isNear;

            Brush fill;
            Pen? outline = null;
            if (n.IsGhost)
            {
                fill = Bg;
                outline = CachedPen(Dim, 1, "ghost", DashGhost);
            }
            else
            {
                fill = FillFor(n);
                if (n.Disabled)
                {
                    // Disabled entries still exist and still link, but Claude Code does not load
                    // them — hollow, not coloured in.
                    outline = CachedPen(fill, 1.2, "off:" + n.Type);
                    fill = Bg;
                }
            }

            if (faded) dc.PushOpacity(0.22);

            // Bloom first, then the body on top of it.
            if (!faded)
            {
                var glowColour = n.IsGhost ? ((SolidColorBrush)Dim).Color : ColourFor(n);
                var gr = r * (isFocus ? 5.2 : isNear ? 3.8 : 3.0);
                dc.DrawEllipse(Glow(glowColour), null, p, gr, gr);
            }

            // Selection ring sits outside the bloom so it stays visible against it.
            if (isFocus) dc.DrawEllipse(null, CachedPen(Text, 1.4, "ring"), p, r + 5, r + 5);

            dc.DrawEllipse(fill, outline, p, r, r);

            // A small bright core reads as emission rather than paint. Only on solid nodes —
            // a hollow (disabled) or ghost node must stay visibly hollow.
            if (!n.IsGhost && !n.Disabled && r >= 5)
                dc.DrawEllipse(Core(ColourFor(n)), null,
                    new Point(p.X - r * 0.18, p.Y - r * 0.18), r * 0.42, r * 0.42);

            if (faded) dc.Pop();
        }

        DrawLabels(dc, focus, hovering);
        DrawInfoCard(dc, focus);
        DrawLegend(dc);
    }

    /// <summary>
    /// Labels, in a pass of their own so they sit above every node and so collisions can be
    /// resolved. Drawn most-important-first and skipped when the box would overlap one already
    /// placed: with 100 nodes the alternative is a pile of overlapping text in the dense middle
    /// (measured before this: 9 labels drawn, 12 overlapping pairs).
    /// </summary>
    private void DrawLabels(DrawingContext dc, string? focus, bool hovering)
    {
        var placed = new List<Rect>();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Priority: the focused node, then its neighbours, then hubs by degree.
        var order = Enumerable.Range(0, _nodes.Count)
            .Select(i => (i, n: _nodes[i]))
            .Select(t => (t.i, rank:
                t.n.Slug == focus ? 3
                : _neighbours.Contains(t.n.Slug) ? 2
                : 1))
            .Where(t => t.rank > 1 || ShowByDensity(_nodes[t.i]))
            .OrderByDescending(t => t.rank)
            .ThenByDescending(t => _nodes[t.i].Degree)
            .ToList();

        foreach (var (i, rank) in order)
        {
            var n = _nodes[i];
            var p = ToScreen(_layout.Nodes[i]);
            var isFocus = rank == 3;
            // Off-screen labels cost layout and can still steal a collision slot from a visible one.
            if (p.X < -50 || p.Y < -20 || p.X > ActualWidth + 50 || p.Y > ActualHeight + 20) continue;

            var ft = new FormattedText(n.Slug, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face,
                isFocus ? 11.5 : 10, isFocus ? Text : Muted, dpi)
            { MaxTextWidth = 190, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };

            var r = RadiusOf(i) * Math.Max(0.55, Math.Min(1.4, _scale));
            var box = new Rect(p.X + r + 4, p.Y - ft.Height / 2, ft.WidthIncludingTrailingWhitespace, ft.Height);
            if (placed.Any(b => b.IntersectsWith(box))) continue;
            placed.Add(box);

            if (hovering && rank == 1) dc.PushOpacity(0.35);
            dc.DrawText(ft, box.TopLeft);
            if (hovering && rank == 1) dc.Pop();
        }
    }

    /// <summary>Whether a non-focused node earns a label at the current zoom.</summary>
    private bool ShowByDensity(MemoryNode n)
        => _scale > 1.1 || (_scale > 0.5 && n.Degree >= 5) || (_scale > 0.3 && n.Degree >= 10);

    /// <summary>
    /// Detail for the node under the cursor, or the selected one. GRAPH mode replaces the editor
    /// pane, so without this the graph can tell you a node exists but nothing about what it says —
    /// and the description is usually the reason you were looking for it.
    /// </summary>
    private void DrawInfoCard(DrawingContext dc, string? focus)
    {
        if (string.IsNullOrEmpty(focus) || !_index.TryGetValue(focus!, out var i)) return;
        var n = _nodes[i];
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        const double w = 290, pad = 12;
        var lines = new List<FormattedText>();

        FormattedText T(string text, double size, Brush brush, double maxW, int maxLines = 1)
            => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, brush, dpi)
            { MaxTextWidth = maxW, MaxLineCount = maxLines, Trimming = TextTrimming.CharacterEllipsis };

        var typeLabel = n.IsGhost ? "미작성"
            : n.Type == MemoryType.Unknown ? "type 없음"
            : n.Type.ToString().ToLowerInvariant();
        var badge = T(typeLabel + (n.Disabled ? " · 비활성" : ""), 9.5,
                      n.IsGhost ? Dim : FillFor(n), w - pad * 2);
        lines.Add(badge);
        lines.Add(T(n.Slug, 12, Text, w - pad * 2, 2));

        var desc = n.Entry?.Frontmatter.Description;
        if (!string.IsNullOrWhiteSpace(desc)) lines.Add(T(desc!, 10.5, Muted, w - pad * 2, 3));
        else if (n.IsGhost) lines.Add(T("이 이름으로 링크만 있고 파일이 없습니다.", 10.5, Muted, w - pad * 2, 2));

        lines.Add(T($"나감 {n.OutDegree}   들어옴 {n.InDegree}", 10, Muted, w - pad * 2));

        var h = pad * 2 + lines.Sum(l => l.Height) + (lines.Count - 1) * 5;
        var card = new Rect(ActualWidth - w - 14, 14, w, h);

        var back = Frozen(Color.FromArgb(0xE8, 0x14, 0x11, 0x0F));
        var border = new Pen(Res("B.Border", "#FF4A1F0E"), 1);
        border.Freeze();
        dc.DrawRoundedRectangle(back, border, card, 3, 3);

        // The type colour as a left edge — the same encoding the nodes use, so the card is
        // obviously about the node you are pointing at.
        dc.DrawRectangle(n.IsGhost ? Dim : FillFor(n), null, new Rect(card.X, card.Y + 1, 2, card.Height - 2));

        var y = card.Y + pad;
        foreach (var l in lines)
        {
            dc.DrawText(l, new Point(card.X + pad, y));
            y += l.Height + 5;
        }
    }

    private void DrawCentredText(DrawingContext dc, string s, Brush brush, double size)
    {
        var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face,
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point((ActualWidth - ft.Width) / 2, (ActualHeight - ft.Height) / 2));
    }

    /// <summary>
    /// Legend built from what the graph actually contains. A fixed list showed "user" and
    /// "type 없음" permanently even though this directory has none of either — two entries of pure
    /// noise. Counts make it double as a census.
    /// </summary>
    private void DrawLegend(DrawingContext dc)
    {
        var g = Graph;
        if (g is null || _nodes.Count == 0) return;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Counted from what is on screen, not from the whole corpus — otherwise hiding the disabled
        // entries would leave a legend insisting they are still there. The header above the canvas
        // keeps the full-corpus figures.
        var shown = _nodes;

        var present = new (MemoryType Type, Brush Brush, string Label)[]
        {
            (MemoryType.Project,   Success, "project"),
            (MemoryType.Feedback,  Warn,    "feedback"),
            (MemoryType.Reference, Ember,   "reference"),
            (MemoryType.User,      Rust,    "user"),
            (MemoryType.Unknown,   Muted,   "type 없음"),
        };

        double x = 12, y = ActualHeight - 22;

        void Chip(Brush? fill, Pen? pen, string label)
        {
            dc.DrawEllipse(fill, pen, new Point(x + 4, y + 5), 4, 4);
            var t = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                Face, 10, Muted, dpi);
            dc.DrawText(t, new Point(x + 12, y));
            x += 12 + t.Width + 14;
        }

        foreach (var (type, brush, label) in present)
        {
            var count = shown.Count(n => !n.IsGhost && n.Type == type);
            if (count == 0) continue;
            Chip(brush, null, $"{label} {count}");
        }

        // Encodings that are not a colour — worth stating, because a hollow circle otherwise just
        // looks like a rendering glitch.
        var disabled = shown.Count(n => !n.IsGhost && n.Disabled);
        if (disabled > 0) Chip(Bg, CachedPen(Muted, 1.2, "legend-off"), $"비활성 {disabled}");
        else if (!ShowDisabled)
        {
            var hidden = g.Nodes.Count(n => !n.IsGhost && n.Disabled);
            if (hidden > 0)
            {
                var t = new FormattedText($"비활성 {hidden} 숨김", CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, Face, 10, Dim, dpi);
                dc.DrawText(t, new Point(x, y));
                x += t.Width + 14;
            }
        }

        var ghosts = shown.Count(n => n.IsGhost);
        if (ghosts > 0) Chip(Bg, CachedPen(Dim, 1, "legend-ghost", DashGhost), $"미작성 {ghosts}");

        var links = _edges.Count;
        var stats = $"노드 {shown.Count(n => !n.IsGhost)}  ·  링크 {links}" +
                    (g.MisspelledCount > 0 ? $"  ·  오타 {g.MisspelledCount}" : "");
        var sft = new FormattedText(stats, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face,
            10, Muted, dpi);
        dc.DrawText(sft, new Point(ActualWidth - sft.Width - 12, y));
    }

}
