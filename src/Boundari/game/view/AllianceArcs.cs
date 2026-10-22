using System;
using System.Collections.Generic;
using Godot;

namespace Boundari.View;

/// <summary>
/// Alliance requests, shown on the map instead of in a chat log: for each player asking you
/// to ally, an arched dotted line from their territory to yours (the dots flow towards you).
/// Accept by right-clicking their territory and choosing the alliance wedge.
/// </summary>
public partial class AllianceArcs : Node2D
{
    /// <summary>One request to show: who asked, and how much of its time is left (1 = all, 0 = none).</summary>
    public readonly record struct Request(ushort From, float TimeLeft);

    // Sizes in screen pixels (the same at any zoom).
    private const float DotSpacing = 11f;
    private const float DotRadius = 2.2f;
    private const float DotFlowSpeed = 26f;       // pixels a second, towards you
    private const float ArchShare = 0.28f;        // how high the arch rises, as a share of its length
    private const float MinArchPixels = 40f;

    private static readonly Color DotColor = new(0.86f, 1f, 0.8f, 0.95f);
    private static readonly Color DotShadow = new(0.05f, 0.12f, 0.05f, 0.55f);

    private readonly List<Request> _requests = new();
    private float _time;

    /// <summary>Where territories are on the map (their label spots).</summary>
    public TerritoryLabels? Labels { get; set; }

    /// <summary>The local player.</summary>
    public ushort Me { get; set; }

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
            QueueRedraw(); // the dots move
    }

    public override void _Draw()
    {
        if (Labels is null || _requests.Count == 0 || !Labels.TryGetAnchor(Me, out var mine))
            return;
        float px = 1 / Math.Max(GetCanvasTransform().X.X, 1e-4f); // one screen pixel, in tiles
        _visible = (GetCanvasTransform().AffineInverse() * GetViewportRect()).Grow((DotRadius + 1) * px);
        long started = FrameProfiler.Start();
        foreach (var request in _requests)
            if (Labels.TryGetAnchor(request.From, out var theirs))
                DrawRequest(theirs, mine, px);
        FrameProfiler.Stop("alliance arcs", started);
    }

    private Rect2 _visible; // the map on screen

    // A dot is one small round picture: hundreds of DrawCircle shapes a frame were slow.
    private static ImageTexture? _dot;

    public override void _Ready() => TextureFilter = TextureFilterEnum.LinearWithMipmaps;

    private void DrawDot(Vector2 at, float radius, Color colour)
    {
        _dot ??= MakeDot();
        DrawTextureRect(_dot, new Rect2(at - Vector2.One * radius, Vector2.One * radius * 2), false, colour);
    }

    private static ImageTexture MakeDot()
    {
        const int Size = 64;
        var image = Image.CreateEmpty(Size, Size, false, Image.Format.Rgba8);
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                float d = new Vector2(x + 0.5f - Size / 2f, y + 0.5f - Size / 2f).Length();
                image.SetPixel(x, y, new Color(1, 1, 1, Math.Clamp(Size / 2f - d, 0, 1)));
            }
        image.GenerateMipmaps();
        return ImageTexture.CreateFromImage(image);
    }

    private void DrawRequest(Vector2 from, Vector2 to, float px)
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
        if (!_visible.Intersects(new Rect2(from, Vector2.Zero).Expand(to).Expand(control), true))
            return; // the curve stays inside the box of its ends and control point

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
        for (int seg = 0; seg < Segments; seg++)
        {
            // Zoomed in, most of the curve is off screen: skip those stretches whole.
            float stretch = along[seg + 1] - along[seg];
            var a = At(seg / (float)Segments);
            if (!_visible.Grow(stretch).Intersects(new Rect2(a, Vector2.Zero).Expand(At((seg + 1) / (float)Segments)), true))
                continue;
            for (float s = offset + MathF.Ceiling((along[seg] - offset) / spacing) * spacing; s < along[seg + 1] && s < total; s += spacing)
            {
                var p = At((seg + (s - along[seg]) / Math.Max(stretch, 1e-6f)) / Segments);
                if (!_visible.HasPoint(p))
                    continue;
                // Dots fade in and out at the two ends.
                float edge = Math.Min(s, total - s) / Math.Max(spacing * 3, 1e-6f);
                float alpha = Math.Clamp(edge, 0, 1);
                DrawDot(p, (DotRadius + 1) * px, DotShadow with { A = DotShadow.A * alpha });
                DrawDot(p, DotRadius * px, DotColor with { A = DotColor.A * alpha });
            }
        }
    }
}
