using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudeCodeManager.Core.Services;

/// <summary>A node's position and velocity during layout. Mutable on purpose — the simulation
/// runs many ticks over the same array and allocating per tick would dominate the cost.</summary>
public sealed class LayoutNode
{
    public string Id = "";
    public double X, Y, Vx, Vy;
    /// <summary>Repulsion and link strength both scale with this. Degree works better than a flat
    /// mass: hubs push their neighbours out far enough to be readable.</summary>
    public double Weight = 1;
    /// <summary>Pinned nodes are dragged by the user and must not be moved by the simulation.</summary>
    public bool Pinned;
}

public sealed class LayoutEdge
{
    public int A, B;
    public double Strength = 1;
}

/// <summary>
/// Force-directed layout — the same three forces d3-force uses (repulsion, spring, centring),
/// written out because the graph is ~100 nodes and pulling in a layout library for that would
/// cost more than it saves.
///
/// Repulsion is the O(n²) pair loop rather than Barnes-Hut. At this size that is ~5.7k pairs per
/// tick — measured 0.05 ms/tick, settling in 263 ticks — so a quadtree would only start paying
/// around a few thousand nodes.
/// </summary>
public sealed class ForceLayout
{
    public List<LayoutNode> Nodes { get; } = new();
    public List<LayoutEdge> Edges { get; } = new();

    /// <summary>Simulation temperature. Starts hot so the graph can untangle, then cools so it
    /// settles instead of jittering forever.</summary>
    public double Alpha { get; private set; } = 1.0;

    private double _alphaDecay = 0.020;
    private const double AlphaMin = 0.005;

    // Tuned against the live memory graph (107 nodes / 271 edges). The three are coupled: raising
    // repulsion without raising centring lets weakly-linked leaves drift off — at 9000/0.012 the
    // same graph settled across 8254 x 4719 px, which needs so much zoom-out that labels vanish.
    // These land it at ~713 x 761 px with 36 px minimum node spacing.
    public double Repulsion { get; set; } = 4000;
    public double LinkDistance { get; set; } = 80;
    public double LinkStrength { get; set; } = 0.06;
    public double Centring { get; set; } = 0.030;
    public double Damping { get; set; } = 0.82;

    /// <summary>
    /// Floor on the distance used for repulsion. Without it the force is R/d² with no bound, so two
    /// nodes that happen to seed close together receive an enormous impulse and are flung thousands
    /// of pixels out; the graph then spends the rest of its alpha budget crawling back and freezes
    /// mid-collapse. Measured before this existed: the same 104-node graph peaked at 46,000 px tall
    /// and settled at 868 x 5,600 — a frozen filament, not an equilibrium.
    /// </summary>
    public double MinRepulsionDistance { get; set; } = 14;

    /// <summary>Per-tick displacement cap. The second half of the same guarantee: bounded force
    /// still integrates badly if one tick can move a node further than the graph is wide.</summary>
    public double MaxVelocity { get; set; } = 30;

    // Centring is split per axis so the settled graph can be given the viewport's proportions.
    // A square result inside a 1.29:1 canvas wastes the sides; matching the aspect fills it.
    private double _centringX = 1, _centringY = 1;

    /// <summary>Shape the result to a viewport. Pass width/height.</summary>
    public void SetAspect(double widthOverHeight)
    {
        if (widthOverHeight <= 0 || double.IsNaN(widthOverHeight)) { _centringX = _centringY = 1; return; }
        var a = Math.Clamp(widthOverHeight, 0.4, 3.0);
        // Weaker centring on the long axis lets the graph spread that way.
        _centringX = 1 / Math.Sqrt(a);
        _centringY = Math.Sqrt(a);
    }

    public bool Settled => Alpha <= AlphaMin;

    /// <summary>Re-heat after a change (a drag, a filter) so the graph re-settles.</summary>
    public void Reheat(double alpha = 0.6) => Alpha = Math.Max(Alpha, alpha);

    /// <summary>
    /// Seed positions on a circle rather than at random. A random cloud can start with two clusters
    /// interleaved and the simulation then has to pull them through each other, which looks like
    /// chaos for the first second; a ring untangles outward cleanly.
    /// </summary>
    public void Seed(double radius = 260)
    {
        var n = Nodes.Count;
        if (n == 0) return;
        // Golden-angle placement so nodes adjacent in the list do not start adjacent on the ring —
        // neighbours in the list are often the same type and would otherwise all start bunched.
        const double golden = 2.399963229728653;
        for (var i = 0; i < n; i++)
        {
            var a = i * golden;
            var r = radius * Math.Sqrt((i + 0.5) / n);
            Nodes[i].X = Math.Cos(a) * r;
            Nodes[i].Y = Math.Sin(a) * r;
            Nodes[i].Vx = Nodes[i].Vy = 0;
        }
        Alpha = 1.0;
    }

    public void Tick()
    {
        if (Settled) return;
        var n = Nodes.Count;
        if (n == 0) return;

        // ── repulsion ──────────────────────────────────────────────────────
        for (var i = 0; i < n; i++)
        {
            var a = Nodes[i];
            for (var j = i + 1; j < n; j++)
            {
                var b = Nodes[j];
                var dx = b.X - a.X;
                var dy = b.Y - a.Y;
                var d2 = dx * dx + dy * dy;
                // Two nodes at the same spot give a zero vector and no way to separate; nudge them
                // apart deterministically using their index so the layout stays reproducible.
                if (d2 < 0.01)
                {
                    dx = ((i * 37 + j * 17) % 11 - 5) * 0.1;
                    dy = ((i * 23 + j * 41) % 11 - 5) * 0.1;
                    d2 = dx * dx + dy * dy + 0.01;
                }
                var d = Math.Sqrt(d2);
                // Clamp the denominator, not the distance — the direction still comes from the real
                // vector, only the magnitude stops running away.
                var dEff = Math.Max(d2, MinRepulsionDistance * MinRepulsionDistance);
                var f = Repulsion * a.Weight * b.Weight / dEff * Alpha;
                var fx = dx / d * f;
                var fy = dy / d * f;
                if (!a.Pinned) { a.Vx -= fx; a.Vy -= fy; }
                if (!b.Pinned) { b.Vx += fx; b.Vy += fy; }
            }
        }

        // ── springs ────────────────────────────────────────────────────────
        foreach (var e in Edges)
        {
            if (e.A < 0 || e.B < 0 || e.A >= n || e.B >= n || e.A == e.B) continue;
            var a = Nodes[e.A];
            var b = Nodes[e.B];
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var d = Math.Sqrt(dx * dx + dy * dy);
            if (d < 0.01) continue;
            var f = (d - LinkDistance) * LinkStrength * e.Strength * Alpha;
            var fx = dx / d * f;
            var fy = dy / d * f;
            if (!a.Pinned) { a.Vx += fx; a.Vy += fy; }
            if (!b.Pinned) { b.Vx -= fx; b.Vy -= fy; }
        }

        // ── centring + integrate ───────────────────────────────────────────
        foreach (var p in Nodes)
        {
            if (p.Pinned) { p.Vx = p.Vy = 0; continue; }
            p.Vx -= p.X * Centring * _centringX * Alpha;
            p.Vy -= p.Y * Centring * _centringY * Alpha;
            p.Vx *= Damping;
            p.Vy *= Damping;
            var sp = Math.Sqrt(p.Vx * p.Vx + p.Vy * p.Vy);
            if (sp > MaxVelocity)
            {
                var k = MaxVelocity / sp;
                p.Vx *= k;
                p.Vy *= k;
            }
            p.X += p.Vx;
            p.Y += p.Vy;
        }

        Alpha -= _alphaDecay * Alpha;
        if (Alpha < AlphaMin) Alpha = AlphaMin;
    }

    /// <summary>Run to rest without rendering — used by tests and by the initial layout so the
    /// graph is already readable on the first frame instead of exploding outward on screen.</summary>
    public void RunToSettle(int maxTicks = 400)
    {
        for (var i = 0; i < maxTicks && !Settled; i++) Tick();
    }

    /// <summary>Bounding box of the laid-out graph, for fitting it to the viewport.</summary>
    public (double MinX, double MinY, double MaxX, double MaxY) Bounds()
    {
        if (Nodes.Count == 0) return (0, 0, 0, 0);
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in Nodes)
        {
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }
        return (minX, minY, maxX, maxY);
    }
}
