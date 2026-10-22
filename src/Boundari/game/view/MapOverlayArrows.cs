using System;
using System.Collections.Generic;
using Boundari.Sim;
using Godot;

namespace Boundari.View;

/// <summary>
/// Directed-attack arrows (Boundari's own): the arrow being dragged out, and your running
/// directed attacks' arrows. When you let go, the arrow shrinks over half a second into a
/// small arrow beside the attack's troop number on its front, and rides along with it.
/// Other players' arrows are never shown.
/// </summary>
public partial class MapOverlay
{
    private const double ArrowShrinkSeconds = 0.5;
    private const double ArrowWaitSeconds = 1.0;   // a launched arrow whose attack never shows up fades after this
    private const float SmallArrowPixels = 26;     // the arrow beside a troop number, tip to tail
    private const float SmallArrowGapPixels = 8;   // between the number and the arrow
    private static readonly Color ArrowNoTargetColour = new(0.8f, 0.8f, 0.8f);

    // The arrow being dragged: from/to in map units, 0 (flat) .. 1 (thin), and whether it points at anything.
    private (Vector2 From, Vector2 To, float Thin, bool Valid)? _dragArrow;

    private sealed class LaunchedArrow
    {
        public Vector2 From, To;
        public float Thin;
        public int FromTile, ToTile;
        public double Launched;
        public int AttackId = -1;       // the attack it turned into, once it shows up
        public Vector2 Settled, Dir;    // where it shrank to (map units) and which way it points
        public bool HasSettled;
    }

    private readonly List<LaunchedArrow> _launched = new();

    /// <summary>The arrow being dragged out (map units); null hides it.</summary>
    public void SetDragArrow(Vector2 from, Vector2 to, float thin, bool valid) => _dragArrow = (from, to, thin, valid);

    public void ClearDragArrow() => _dragArrow = null;

    /// <summary>An arrow was let go: it shrinks onto its attack's front once the attack shows up.</summary>
    public void LaunchArrow(Vector2 from, Vector2 to, float thin, int fromTile, int toTile) =>
        _launched.Add(new LaunchedArrow { From = from, To = to, Thin = thin, FromTile = fromTile, ToTile = toTile, Launched = Now });

    private void DrawArrows(float zoom, int w)
    {
        if (_dragArrow is { } drag)
            DrawArrowShape(drag.From, drag.To, drag.Thin, zoom, drag.Valid ? OutgoingColour : ArrowNoTargetColour, 1);
        if (_launched.Count == 0 || _game is null)
            return;

        double now = Now;
        for (int i = _launched.Count - 1; i >= 0; i--)
        {
            var arrow = _launched[i];
            var attack = FindArrowAttack(arrow);
            if (attack is null)
            {
                if (arrow.AttackId >= 0 || now - arrow.Launched > ArrowWaitSeconds + ArrowShrinkSeconds)
                {
                    _launched.RemoveAt(i); // the attack ended (or never started)
                    continue;
                }
                // Waiting for the attack to start: fade where it was drawn.
                float fade = 1 - (float)Math.Clamp((now - arrow.Launched - ArrowWaitSeconds) / ArrowShrinkSeconds, 0, 1);
                DrawArrowShape(arrow.From, arrow.To, arrow.Thin, zoom, OutgoingColour, fade);
                continue;
            }
            if (arrow.AttackId < 0)
            {
                arrow.AttackId = attack.Id;
                arrow.Launched = now; // the shrink starts when the attack shows up
            }

            // The small arrow beside the attack's (first) troop number.
            var dir = new Vector2((float)attack.Arrow!.DirX, (float)attack.Arrow.DirY);
            if (!SmallArrowSpot(attack, now, zoom, out var centre))
                centre = arrow.HasSettled ? arrow.Settled : arrow.To;
            arrow.Settled = centre;
            arrow.Dir = dir;
            arrow.HasSettled = true;
            float half = SmallArrowPixels / 2 / zoom;
            var smallFrom = centre - dir * half;
            var smallTo = centre + dir * half;

            float t = (float)Math.Clamp((now - arrow.Launched) / ArrowShrinkSeconds, 0, 1);
            t = t * t * (3 - 2 * t); // ease in and out
            var from = arrow.From.Lerp(smallFrom, t);
            var to = arrow.To.Lerp(smallTo, t);
            float thin = Mathf.Lerp(arrow.Thin, 0.5f, t);
            DrawArrowShape(from, to, thin, zoom, OutgoingColour, 1, Mathf.Lerp(1, SmallArrowScale, t));
        }
    }

    // The small arrow is drawn a little slimmer than a dragged one of the same shape.
    private const float SmallArrowScale = 0.45f;

    /// <summary>The running attack a launched arrow turned into (found by where the arrow was drawn).</summary>
    private Attack? FindArrowAttack(LaunchedArrow arrow)
    {
        foreach (var attack in _game!.Attacks)
        {
            if (attack.Attacker != _me || attack.Arrow is null || attack.Retreating)
                continue;
            if (arrow.AttackId >= 0 ? attack.Id == arrow.AttackId
                : attack.Arrow.FromTile == arrow.FromTile && attack.Arrow.ToTile == arrow.ToTile && !_launched.Exists(l => l.AttackId == attack.Id))
                return attack;
        }
        return null;
    }

    /// <summary>Where the small arrow sits: just right of the attack's first troop number (map units).</summary>
    private bool SmallArrowSpot(Attack attack, double now, float zoom, out Vector2 centre)
    {
        centre = default;
        if (!_fronts.TryGetValue(attack.Id, out var front) || front.Slots.Count == 0)
            return false;
        var at = front.Slots[0].At(now);
        float textWidth = front.Text.Length == 0 ? 0 : _font.GetStringSize(front.Text, HorizontalAlignment.Left, -1, FrontLabelPixels).X;
        float offset = front.Text.Length == 0 ? 0 : textWidth / 2 + SmallArrowGapPixels + SmallArrowPixels / 2;
        centre = at + new Vector2(offset / zoom, 0);
        return true;
    }

    /// <summary>
    /// An arrow from <paramref name="from"/> to <paramref name="to"/> (map units): short and
    /// flat = a fat shaft, long and thin = a slim one. Widths are screen pixels, so it looks
    /// the same at any zoom.
    /// </summary>
    private void DrawArrowShape(Vector2 from, Vector2 to, float thin, float zoom, Color colour, float alpha, float widthScale = 1)
    {
        var along = to - from;
        float length = along.Length();
        if (length * zoom < 2 || alpha <= 0)
            return;
        var dir = along / length;
        var side = new Vector2(-dir.Y, dir.X);
        float px = 1 / zoom * widthScale;
        float shaft = Mathf.Lerp(20, 5, thin) * px;          // half-widths
        float head = Mathf.Lerp(40, 14, thin) * px;
        float headLength = Math.Min(Mathf.Lerp(34, 26, thin) * px, length * 0.6f);
        var neck = to - dir * headLength;
        var points = new[]
        {
            from + side * shaft * 0.7f, neck + side * shaft, neck + side * head, to,
            neck - side * head, neck - side * shaft, from - side * shaft * 0.7f,
        };
        DrawColoredPolygon(points, new Color(colour, 0.55f * alpha));
        var outline = new Vector2[points.Length + 1];
        points.CopyTo(outline, 0);
        outline[^1] = points[0];
        DrawPolyline(outline, new Color(colour.Darkened(0.45f), 0.9f * alpha), 1.5f / zoom, true);
    }
}
