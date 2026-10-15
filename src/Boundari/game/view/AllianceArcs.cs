using System;
using System.Collections.Generic;
using Godot;

namespace Boundari.View;

/// <summary>
/// Alliance requests, shown on the map instead of in a chat log: for each player asking you
/// to ally, an arched dotted line from their territory to yours (the dots flow towards you),
/// with a handshake badge at the top of the arch. A ring around the badge runs down as the
/// request's 20 seconds pass. Accept by right-clicking their territory and choosing the
/// alliance wedge.
/// </summary>
public partial class AllianceArcs : Node2D
{
    /// <summary>One request to show: who asked, and how much of its time is left (1 = all, 0 = none).</summary>
    public readonly record struct Request(ushort From, float TimeLeft);

    // Sizes in screen pixels (the same at any zoom).
    private const float DotSpacing = 11f;
    private const float DotRadius = 2.2f;
    private const float DotFlowSpeed = 26f;       // pixels a second, towards you
    private const float BadgeRadius = 19f;
    private const float IconWidth = 25f;
    private const float RingWidth = 2.6f;
    private const float ArchShare = 0.28f;        // how high the arch rises, as a share of its length
    private const float MinArchPixels = 40f;

    private static readonly Color DotColor = new(0.86f, 1f, 0.8f, 0.95f);
    private static readonly Color DotShadow = new(0.05f, 0.12f, 0.05f, 0.55f);
    private static readonly Color BadgeColor = new(0.07f, 0.11f, 0.07f, 0.86f);
    private static readonly Color RingColor = new(0.45f, 0.86f, 0.36f, 1f);
    private static readonly Color RingTrack = new(1f, 1f, 1f, 0.12f);

    private readonly List<Request> _requests = new();
    private Texture2D? _icon;
    private float _time;

    /// <summary>Where territories are on the map (their label spots).</summary>
    public TerritoryLabels? Labels { get; set; }

    /// <summary>The local player.</summary>
    public ushort Me { get; set; }

    public override void _Ready()
    {
        _icon = ResourceLoader.Exists("res://ui/radial/handshake.png") ? GD.Load<Texture2D>("res://ui/radial/handshake.png") : null;
    }

    /// <summary>The requests waiting for the local player (call while the game isn't stepping).</summary>
    public void SetRequests(IEnumerable<Request> requests)
    {
        bool had = _requests.Count > 0;
        _requests.Clear();
        _requests.AddRange(requests);
        if (had || _requests.Count > 0)
            QueueRedraw();
    }

    public override void _Process(double delta)
    {
        _time += (float)delta;
        if (_requests.Count > 0)
            QueueRedraw(); // the dots and the ring move
    }

    public override void _Draw()
    {
        if (Labels is null || _requests.Count == 0 || !Labels.TryGetAnchor(Me, out var mine))
            return;
        float px = 1 / Math.Max(GetCanvasTransform().X.X, 1e-4f); // one screen pixel, in tiles
        foreach (var request in _requests)
            if (Labels.TryGetAnchor(request.From, out var theirs))
                DrawRequest(theirs, mine, request.TimeLeft, px);
    }

    private void DrawRequest(Vector2 from, Vector2 to, float timeLeft, float px)
    {
        var d = to - from;
        float length = d.Length();
        if (length < 1e-3f)
            return;

        // A quadratic curve whose middle rises off the straight line, on the upper side.
        var normal = new Vector2(-d.Y, d.X) / length;
        if (normal.Y > 0 || (normal.Y == 0 && normal.X > 0))
            normal = -normal;
        float rise = Math.Max(length * ArchShare, MinArchPixels * px);
        var mid = (from + to) / 2;
        var apex = mid + normal * rise;
        var control = 2 * apex - mid; // a quadratic curve passes its control point's halfway mark at t = 0.5
        Vector2 At(float t) => (1 - t) * (1 - t) * from + 2 * (1 - t) * t * control + t * t * to;

        // Measure the curve, then lay dots along it at even screen spacing, drifting towards you.
        const int Segments = 96;
        Span<float> along = stackalloc float[Segments + 1];
        var prev = from;
        for (int i = 1; i <= Segments; i++)
        {
            var p = At(i / (float)Segments);
            along[i] = along[i - 1] + p.DistanceTo(prev);
            prev = p;
        }
        float total = along[Segments];
        float spacing = DotSpacing * px;
        float offset = (_time * DotFlowSpeed * px) % spacing;
        float badgeGap = (BadgeRadius + 4) * px;
        float apexAt = along[Segments / 2];
        int seg = 0;
        for (float s = offset; s < total; s += spacing)
        {
            if (Math.Abs(s - apexAt) < badgeGap)
                continue; // leave room for the badge
            while (seg < Segments - 1 && along[seg + 1] < s)
                seg++;
            float f = (s - along[seg]) / Math.Max(along[seg + 1] - along[seg], 1e-6f);
            var p = At((seg + f) / Segments);
            // Dots fade in and out at the two ends.
            float edge = Math.Min(s, total - s) / Math.Max(spacing * 3, 1e-6f);
            float alpha = Math.Clamp(edge, 0, 1);
            DrawCircle(p, (DotRadius + 1) * px, DotShadow with { A = DotShadow.A * alpha });
            DrawCircle(p, DotRadius * px, DotColor with { A = DotColor.A * alpha });
        }

        // The badge: a dark disc with the handshake, and a ring for the time left.
        float pulse = 1 + 0.05f * MathF.Sin(_time * 4);
        float r = BadgeRadius * px * pulse;
        DrawCircle(apex, r, BadgeColor);
        DrawArc(apex, r - RingWidth * px / 2, 0, MathF.Tau, 48, RingTrack, RingWidth * px, true);
        if (timeLeft > 0)
            DrawArc(apex, r - RingWidth * px / 2, -MathF.PI / 2, -MathF.PI / 2 + MathF.Tau * timeLeft, 48, RingColor, RingWidth * px, true);
        if (_icon is not null)
        {
            float w = IconWidth * px * pulse, h = w * _icon.GetHeight() / _icon.GetWidth();
            DrawTextureRect(_icon, new Rect2(apex - new Vector2(w, h) / 2, w, h), false);
        }
    }
}
