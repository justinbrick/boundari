using System;
using System.Collections.Generic;
using Boundari.Sim;
using Godot;

namespace Boundari.View;

/// <summary>
/// Every Flak's range as a slowly turning dashed ring, like OpenFront's SAMRadiusPass:
/// shown while placing a Flak, Silo or Barracks, aiming a bomb, or hovering the Flak or
/// bomb buttons. Yours green, allies' yellow, enemies' red; overlapping rings of the same
/// side merge into one outline (only the parts outside the others are drawn).
/// </summary>
public partial class MapOverlay
{
    /// <summary>Set by the game each frame: whether the Flak rings are shown.</summary>
    public bool ShowFlakRanges { get; set; }

    // OpenFront's samRadius settings, in reference tiles (scaled by the tile scale).
    private const float FlakRingHalfWidth = 1.5f;
    private const float FlakDash = 12, FlakGap = 6;
    private const float FlakSpin = 14;               // tiles a second along the ring
    private const float FlakAlpha = 0.8f;
    private static readonly Color FlakMine = new(0, 1, 0), FlakAlly = new(1, 1, 0), FlakEnemy = new(1, 0, 0);

    private readonly List<(Vector2 At, float Radius, Color Colour, bool Friendly)> _flakRings = new();
    private readonly List<(float From, float To)> _covered = new();

    private void DrawFlakRanges(CanvasItem c, float zoom)
    {
        if (!ShowFlakRanges || _game is null)
            return;
        int w = _game.Map.Width;
        float linear = (float)_game.Rules.Linear;
        _flakRings.Clear();
        foreach (var b in _game.Buildings)
        {
            if (b.Type != BuildingType.Sam || !b.IsReady(_game.Tick))
                continue;
            bool mine = b.Owner == _me?.Id, ally = !mine && _me is not null && _game.GetPlayer(b.Owner) is { } owner && _me.IsAlliedWith(owner);
            _flakRings.Add((TileCentre(b.Tile, w), _game.Rules.SamRange(b.Level), mine ? FlakMine : ally ? FlakAlly : FlakEnemy, mine || ally));
        }
        float halfWidth = Math.Max(FlakRingHalfWidth * linear, 0.75f / zoom); // at least 1.5 screen pixels
        float dash = FlakDash * linear, period = (FlakDash + FlakGap) * linear;
        float shift = (float)(Time.GetTicksMsec() / 1000.0 * FlakSpin * linear % period);
        foreach (var ring in _flakRings)
        {
            if (!_visible.Grow(ring.Radius + halfWidth).HasPoint(ring.At))
                continue;
            foreach (var (from, to) in Uncovered(ring))
                DrawDashedArc(c, ring.At, ring.Radius, from, to, dash, period, shift, halfWidth * 2, new Color(ring.Colour, FlakAlpha), zoom);
        }
    }

    /// <summary>The angles (0..2 pi) of this ring outside every other ring on the same side.</summary>
    private List<(float, float)> Uncovered((Vector2 At, float Radius, Color Colour, bool Friendly) ring)
    {
        _covered.Clear();
        foreach (var other in _flakRings)
        {
            if (other.Friendly != ring.Friendly || other.At == ring.At && other.Radius == ring.Radius)
                continue;
            var d = other.At - ring.At;
            float dist = d.Length();
            if (dist < 1e-4f)
                continue;
            if (other.Radius - ring.Radius >= dist)
                return new List<(float, float)>(); // wholly inside the other ring
            if (dist >= ring.Radius + other.Radius)
                continue;
            float cos = (ring.Radius * ring.Radius + dist * dist - other.Radius * other.Radius) / (2 * ring.Radius * dist);
            float half = MathF.Acos(Math.Clamp(cos, -1, 1)), centre = MathF.Atan2(d.Y, d.X);
            AddCovered(centre - half, centre + half);
        }
        _covered.Sort((a, b) => a.From.CompareTo(b.From));
        var free = new List<(float, float)>();
        float cursor = 0;
        foreach (var (from, to) in _covered)
        {
            if (from > cursor)
                free.Add((cursor, from));
            cursor = Math.Max(cursor, to);
        }
        if (cursor < Mathf.Tau)
            free.Add((cursor, Mathf.Tau));
        return free;
    }

    // Adds an angle range, split in two if it wraps past 0.
    private void AddCovered(float from, float to)
    {
        from = Mathf.PosMod(from, Mathf.Tau);
        to = Mathf.PosMod(to, Mathf.Tau);
        if (to < from)
        {
            _covered.Add((from, Mathf.Tau));
            _covered.Add((0, to));
        }
        else
            _covered.Add((from, to));
    }

    /// <summary>The dashes of a ring between two angles; dashes are measured in tiles along the ring and slide by <paramref name="shift"/>.</summary>
    private static void DrawDashedArc(CanvasItem c, Vector2 at, float radius, float from, float to, float dash, float period, float shift, float width, Color colour, float zoom)
    {
        // Along the ring, in tiles: position s = angle x radius; a dash covers [k x period - shift, + dash].
        float s0 = from * radius, s1 = to * radius;
        for (float start = MathF.Floor((s0 + shift) / period) * period - shift; start < s1; start += period)
        {
            float a = Math.Max(start, s0), b = Math.Min(start + dash, s1);
            if (b <= a)
                continue;
            int points = Math.Clamp((int)((b - a) * zoom / 6) + 2, 2, 24);
            c.DrawArc(at, radius, a / radius, b / radius, points, colour, width, true);
        }
    }
}
