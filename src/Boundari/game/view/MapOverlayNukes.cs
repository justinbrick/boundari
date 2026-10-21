using System;
using System.Collections.Generic;
using Boundari.Sim;
using Godot;

namespace Boundari.View;

/// <summary>Bombs in flight, SAM interceptors, explosions, silo/SAM reloads, and the bomb aiming preview.</summary>
public partial class MapOverlay
{
    private NukeType? _aimType;
    private int _aimTile = -1;
    private bool _aimValid;
    private bool _aimUp = true;

    // The aiming preview (path, Flak reach), worked out again when the aim changes and a
    // few times a second (Flak gets built, reloads, alliances change).
    private NukePreview? _preview;
    private (NukeType, int, bool) _previewFor = (NukeType.Warhead, -1, true);
    private double _previewAge;

    // OpenFront's trajectory preview look (its pixel sizes x1.5 for our screen).
    private const float TrajectoryLine = 3.75f, TrajectoryOutline = 2.25f;
    private const float DashOn = 12, DashOff = 6;          // where Flak can reach the bomb
    private const float HighDashOn = 3, HighDashOff = 9;   // up high, out of Flak's reach
    private const float MarkerRadius = 9;
    private static readonly Color TrajectoryColour = new(1, 1, 1);
    private static readonly Color TrajectoryOutlineColour = new(0.549f, 0.549f, 0.549f);
    private static readonly Color InterceptColour = new(1, 0.314f, 0.314f);
    private static readonly Color InterceptOutlineColour = new(0.588f, 0.353f, 0.353f);

    // OpenFront's bomb "telegraph": the target ring of every bomb in flight, red if it's
    // someone else's, green if yours, yellow if an ally's; a slowly turning dashed ring.
    private static readonly Color TelegraphEnemy = new(1, 0, 0);
    private static readonly Color TelegraphMine = new(0, 1, 0);
    private static readonly Color TelegraphAlly = new(1, 1, 0);

    private static readonly Color BlastCore = new(1f, 0.97f, 0.85f);
    private static readonly Color BlastFire = new(1f, 0.55f, 0.15f);
    private static readonly Color DangerRed = new(0.95f, 0.2f, 0.15f);
    private static readonly Color AimColour = Colors.White;

    public void SetNukeGhost(NukeType type, int tile, bool valid, bool up = true)
    {
        _aimType = type;
        _aimTile = tile;
        _aimValid = valid;
        _aimUp = up;
    }

    public void ClearNukeGhost()
    {
        _aimType = null;
        _aimTile = -1;
    }

    private void DrawNukeLayer(float zoom, int w)
    {
        DrawReloads(zoom, w);
        DrawExplosions(zoom, w);
        foreach (var m in _game!.SamMissiles)
            DrawSamMissile(m, zoom, w);
        foreach (var n in _game.Nukes)
            DrawNuke(n, zoom, w);
        DrawAim(zoom, w);
    }

    /// <summary>Silos and SAMs with every missile out: a grey ring filling up as the first one reloads.</summary>
    private void DrawReloads(float zoom, int w)
    {
        float size = BuildingIconPixels(zoom) / zoom;
        foreach (var b in _game!.Buildings)
        {
            if (b.Type is not (BuildingType.Silo or BuildingType.Sam) || b.Reloading == 0 || !OnScreen(TileCentre(b.Tile, w), size))
                continue;
            int cooldown = b.Type == BuildingType.Sam ? Balance.SamCooldownTicks : Balance.SiloCooldownTicks;
            float done = Math.Clamp((_game.Tick - 1 + _tickFraction - b.ReloadStartTick) / cooldown, 0, 1);
            var at = TileCentre(b.Tile, w);
            bool empty = b.Reloading >= b.Level;
            DrawArc(at, size * 0.7f, 0, Mathf.Tau, 32, new Color(0, 0, 0, empty ? 0.45f : 0.2f), size * 0.1f, true);
            DrawArc(at, size * 0.7f, -Mathf.Pi / 2, -Mathf.Pi / 2 + Mathf.Tau * done, 32,
                new Color(0.85f, 0.85f, 0.85f, empty ? 0.95f : 0.5f), size * 0.1f, true);
        }
    }

    /// <summary>Where a bomb is this frame, between its last two points, and which way it's heading.</summary>
    private (Vector2 Pos, Vector2 Dir) NukePosition(Nuke n, int w)
    {
        var start = TileCentre(n.From, w);
        Vector2 Point(int i) => i < 0 ? start : new Vector2(n.PathX[Math.Min(i, n.PathX.Length - 1)], n.PathY[Math.Min(i, n.PathX.Length - 1)]);
        // Drawn one tick behind, like everything else, so it glides.
        int i0 = n.WaitTicks > 0 ? n.Index : n.Index - 1;
        float f = n.WaitTicks > 0 ? 0 : _tickFraction;
        var a = Point(i0);
        var b = Point(i0 + 1);
        var dir = (b - a).LengthSquared() > 1e-4f ? (b - a).Normalized() : new Vector2(0, -1);
        return (a.Lerp(b, f), dir);
    }

    private void DrawNuke(Nuke n, float zoom, int w)
    {
        var (pos, dir) = NukePosition(n, w);
        var colour = ColorOf(n.Owner.Id);
        bool atMe = _me is not null && (_game!.Owner[n.To] == _me.Id || (n.Type == NukeType.TsarBomba && _game.Owner[n.To] != Game.Nobody && n.Owner != _me && TargetsMe(n)));
        bool mine = n.Owner == _me;

        // Where it will land (people see bombs coming): OpenFront's telegraph ring.
        if (n.Type != NukeType.TsarBomba && (n.Type != NukeType.Warhead || atMe || mine))
        {
            var ring = mine ? TelegraphMine : _me is not null && n.Owner.IsAlliedWith(_me) ? TelegraphAlly : TelegraphEnemy;
            DrawTelegraph(TileCentre(n.To, w), _game!.Rules.Blast(n.Type).Outer, ring, zoom);
        }

        // The arc it flies: solid behind it, dotted ahead (just for bombs you sent or that are coming at you).
        if (mine || atMe)
        {
            var ahead = new List<Vector2> { pos };
            for (int i = Math.Max(0, n.Index + 1); i < n.PathX.Length; i += 2)
                ahead.Add(new Vector2(n.PathX[i], n.PathY[i]));
            ahead.Add(new Vector2(n.PathX[^1], n.PathY[^1]));
            DrawDashes(ahead, new Color(atMe ? DangerRed : Colors.White, 0.55f), 1.2f / zoom, 6 / zoom);
        }
        var behind = new List<Vector2>();
        for (int i = Math.Max(0, n.Index - 12); i <= n.Index && i < n.PathX.Length; i++)
            behind.Add(new Vector2(n.PathX[i], n.PathY[i]));
        behind.Add(pos);
        if (behind.Count >= 2)
            DrawPolyline(behind.ToArray(), new Color(1, 1, 1, 0.5f), (n.Type == NukeType.Warhead ? 1.2f : 2.5f) / zoom, true);

        // The missile itself: a slim body in the owner's colour with a white nose.
        float length = n.Type switch
        {
            NukeType.TsarBomba => 26,
            NukeType.FatMan => 20,
            NukeType.Moab => 16,
            _ => 8,
        } / zoom;
        var side = new Vector2(-dir.Y, dir.X) * length * 0.22f;
        var nose = pos + dir * length * 0.5f;
        var tail = pos - dir * length * 0.5f;
        FillShape(new[] { nose, pos + side, tail + side, tail - side, pos - side }, colour.Darkened(0.2f));
        FillShape(new[] { nose, pos + dir * length * 0.15f + side * 0.9f, pos + dir * length * 0.15f - side * 0.9f }, Colors.White);
        DrawPolyline(new[] { nose, pos + side, tail + side, tail - side, pos - side, nose }, new Color(0.1f, 0.1f, 0.1f, 0.8f), 1f / zoom, true);
        DrawCircle(tail - dir * length * 0.15f, length * 0.16f, new Color(1f, 0.7f, 0.3f, 0.85f)); // exhaust
    }

    private bool TargetsMe(Nuke n)
    {
        foreach (var m in _game!.Nukes)
            if (m.Type == NukeType.Warhead && m.Owner == n.Owner && _game.Owner[m.To] == _me!.Id)
                return true;
        return false;
    }

    /// <summary>A Flak missile: a small white dart with a fading smoke trail.</summary>
    private void DrawSamMissile(SamMissile m, float zoom, int w)
    {
        var from = TileCentre(m.From, w);
        var to = TileCentre(m.To, w);
        float f = Math.Clamp((m.Age - 1 + _tickFraction) / m.FlightTicks, 0, 1);
        var pos = from.Lerp(to, f);
        var dir = (to - from).LengthSquared() > 1e-4f ? (to - from).Normalized() : Vector2.Up;
        const int Puffs = 6;
        for (int i = Puffs; i >= 1; i--) // smoke, oldest (faintest, widest) first
        {
            var a = from.Lerp(to, Math.Max(0, f - 0.06f * i));
            var b = from.Lerp(to, Math.Max(0, f - 0.06f * (i - 1)));
            DrawLine(a, b, new Color(0.85f, 0.85f, 0.85f, 0.5f * (1 - (float)i / (Puffs + 1))), (1.5f + 0.4f * i) / zoom, true);
        }
        float length = 9 / zoom;
        var side = new Vector2(-dir.Y, dir.X) * length * 0.18f;
        var nose = pos + dir * length * 0.5f;
        var tail = pos - dir * length * 0.5f;
        FillShape(new[] { nose, tail + side, tail - side }, Colors.White);
        DrawCircle(tail, length * 0.16f, new Color(1f, 0.75f, 0.35f, 0.9f));
    }

    // A Flak hit: OpenFront's small explosion (9 frames of 70 ms) and a shock ring that
    // spreads to 40 tiles over 0.8 s.
    private const float InterceptBurstSeconds = 0.63f;
    private const float InterceptRingSeconds = 0.8f;
    private const float InterceptRingTiles = 40;

    private void DrawIntercept(Vector2 at, float age, float zoom)
    {
        if (age < InterceptBurstSeconds)
        {
            float t = age / InterceptBurstSeconds;
            float r = (10 + 14 * MathF.Sqrt(t)) / zoom;
            DrawCircle(at, r, new Color(1f, 0.55f, 0.15f, 0.75f * (1 - t)));
            DrawCircle(at, r * 0.6f, new Color(1f, 0.95f, 0.75f, 0.9f * (1 - t)));
            for (int k = 0; k < 6; k++) // sparks
            {
                float angle = k * Mathf.Tau / 6 + 0.4f;
                var d = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                DrawLine(at + d * r * 0.8f, at + d * r * (1.1f + 0.5f * t), new Color(1f, 0.85f, 0.4f, 1 - t), 1.5f / zoom, true);
            }
        }
        if (age < InterceptRingSeconds)
        {
            float t = age / InterceptRingSeconds;
            float radius = InterceptRingTiles * (float)_game!.Rules.Linear * t;
            DrawArc(at, radius, 0, Mathf.Tau, 64, new Color(1, 0.92f, 0.75f, 0.7f * (1 - t)), Math.Max(1.5f / zoom, radius * 0.04f), true);
        }
    }

    /// <summary>A flash and fireball that swell to the blast size and fade, then a fading shock ring; interceptions are a small puff.</summary>
    private void DrawExplosions(float zoom, int w)
    {
        foreach (var e in _game!.Explosions)
        {
            float age = (_game.Tick - 1 - e.Tick + _tickFraction) / Balance.TicksPerSecond; // seconds
            if (age < 0)
                continue;
            var at = TileCentre(e.Tile, w);
            if (e.Intercepted)
            {
                DrawIntercept(at, age, zoom);
                continue;
            }
            int outer = _game!.Rules.Blast(e.Type).Outer;
            float grow = Math.Min(1, age / 0.35f);
            float fade = Math.Clamp(1 - (age - 0.3f) / 1.7f, 0, 1);
            if (fade > 0)
            {
                DrawCircle(at, outer * (0.3f + 0.7f * grow), new Color(BlastFire, 0.55f * fade));
                DrawCircle(at, outer * (0.15f + 0.45f * grow), new Color(BlastCore, 0.85f * fade));
            }
            float ring = Math.Clamp(age / 2.5f, 0, 1);
            if (ring < 1)
                DrawArc(at, outer * (1 + 0.8f * ring), 0, Mathf.Tau, 64, new Color(1, 0.9f, 0.7f, 0.6f * (1 - ring)), 2.5f / zoom, true);
        }
    }

    /// <summary>
    /// Aiming a bomb: the blast circles at the mouse, and (like OpenFront's trajectory
    /// preview) the arc from the silo that would fire it: dashed where Flak can reach it,
    /// finely dotted up high where it can't, turning red with an X from the first point an
    /// enemy Flak covers, with small rings where it climbs out of and comes back into reach.
    /// The blast circles are white, red if Flak would shoot the bomb down (or the strike can't
    /// happen, or would break an alliance).
    /// </summary>
    private void DrawAim(float zoom, int w)
    {
        if (_aimType is not { } type || _aimTile < 0 || _me is null)
            return;
        var at = TileCentre(_aimTile, w);
        if (type == NukeType.TsarBomba)
        {
            var tint = _aimValid ? AimColour : DangerRed; // Flak can't stop this one
            DrawArc(at, _game!.Rules.MirvRange, 0, Mathf.Tau, 128, new Color(tint, 0.5f), 1.5f / zoom, true);
            DrawCircle(at, 20, new Color(tint, 0.35f));
            if (_game.ReadySilo(_me, _aimTile) is { } silo)
            {
                int sx = silo.Tile % w, sy = silo.Tile / w;
                var (xs, ys) = NukeRules.Arc(sx, sy, (_aimTile % w + sx) / 2, Math.Max(0, _aimTile / w - 500) + 50, _game.Map.Height, 3, _game.Rules.NukeArcMinHeight);
                var points = new List<Vector2>(xs.Length + 1) { TileCentre(silo.Tile, w) };
                for (int i = 0; i < xs.Length; i++)
                    points.Add(new Vector2(xs[i], ys[i]));
                DrawDashes(points, new Color(tint, 0.8f), 1.5f / zoom, 8 / zoom);
            }
            return;
        }

        if (_previewFor != (type, _aimTile, _aimUp) || (_previewAge += GetProcessDeltaTime()) > 0.25)
        {
            _previewFor = (type, _aimTile, _aimUp);
            _previewAge = 0;
            _preview = _game!.PreviewNuke(_me, type, _aimTile, _aimUp);
        }
        var (inner, outer) = _game!.Rules.Blast(type);
        bool shotDown = _preview is { InterceptIndex: >= 0 };
        var ring = !_aimValid || shotDown || _preview?.BetraysAlly == true ? DangerRed : AimColour;
        DrawCircle(at, outer, new Color(ring, 0.12f));
        DrawCircle(at, inner, new Color(ring, 0.28f));
        DrawArc(at, outer, 0, Mathf.Tau, 64, new Color(ring, 0.8f), 1.5f / zoom, true);
        if (_preview is { } p)
            DrawTrajectory(p, w, zoom);
    }

    private readonly List<(Vector2 A, Vector2 B, bool Red)> _dashes = new();

    private void DrawTrajectory(NukePreview p, int w, float zoom)
    {
        // Walk the path in screen pixels, laying dashes in each stretch's pattern.
        _dashes.Clear();
        var markers = new List<Vector2>();
        Vector2 prev = TileCentre(p.SiloTile, w);
        bool prevHigh = false, on = true;
        float left = DashOn;
        for (int i = 0; i < p.X.Length; i++)
        {
            var next = new Vector2(p.X[i], p.Y[i]);
            bool high = !p.Targetable[i];
            bool red = p.InterceptIndex >= 0 && i > p.InterceptIndex;
            if (i > 0 && high != prevHigh)
            {
                markers.Add(prev);
                on = true;
                left = high ? HighDashOn : DashOn;
            }
            prevHigh = high;
            float len = prev.DistanceTo(next) * zoom; // pixels
            float pos = 0;
            while (len - pos > 1e-3f)
            {
                float step = Math.Min(left, len - pos);
                if (on)
                    _dashes.Add((prev.Lerp(next, pos / len), prev.Lerp(next, (pos + step) / len), red));
                pos += step;
                left -= step;
                if (left <= 1e-3f)
                {
                    on = !on;
                    left = on ? (high ? HighDashOn : DashOn) : (high ? HighDashOff : DashOff);
                }
            }
            prev = next;
        }
        float lineWidth = TrajectoryLine / zoom, outlineWidth = (TrajectoryLine + 2 * TrajectoryOutline) / zoom;
        foreach (var (a, b, red) in _dashes)
            DrawLine(a, b, red ? InterceptOutlineColour : TrajectoryOutlineColour, outlineWidth, true);
        foreach (var (a, b, red) in _dashes)
            DrawLine(a, b, red ? InterceptColour : TrajectoryColour, lineWidth, true);

        float r = MarkerRadius / zoom;
        foreach (var m in markers) // where it climbs out of, and drops back into, Flak's reach
        {
            DrawArc(m, r, 0, Mathf.Tau, 24, TrajectoryOutlineColour, outlineWidth, true);
            DrawArc(m, r, 0, Mathf.Tau, 24, TrajectoryColour, lineWidth, true);
        }
        if (p.InterceptIndex >= 0) // the first point an enemy Flak covers
        {
            var x = new Vector2(p.X[p.InterceptIndex], p.Y[p.InterceptIndex]);
            foreach (var (width, colour) in new[] { (outlineWidth, InterceptOutlineColour), (lineWidth, InterceptColour) })
            {
                DrawLine(x + new Vector2(-r, -r), x + new Vector2(r, r), colour, width, true);
                DrawLine(x + new Vector2(-r, r), x + new Vector2(r, -r), colour, width, true);
            }
        }
    }

    /// <summary>OpenFront's telegraph: a faint fill and a turning dashed ring, gently pulsing.</summary>
    private void DrawTelegraph(Vector2 at, float radius, Color colour, float zoom)
    {
        float time = Time.GetTicksMsec() / 1000f;
        float alpha = 0.85f + 0.1f * MathF.Sin(time * 3);
        DrawCircle(at, radius, new Color(colour, Math.Max(0, alpha - 0.6f) * 0.5f));
        // Dashes 18 px on, 9 off around the ring, creeping round at 30 px a second.
        float circumference = Mathf.Tau * radius * zoom; // pixels
        int dashes = Math.Max(4, (int)(circumference / 27));
        float dashAngle = Mathf.Tau / dashes, offset = time * 30 / Math.Max(1, radius * zoom);
        for (int k = 0; k < dashes; k++)
        {
            float a0 = offset + k * dashAngle;
            DrawArc(at, radius, a0, a0 + dashAngle * 2 / 3, 6, new Color(colour, alpha), 2.25f / zoom, true);
        }
    }

    /// <summary>A dashed line along a path.</summary>
    private void DrawDashes(List<Vector2> points, Color colour, float width, float dash)
    {
        bool on = true;
        float left = dash;
        for (int i = 1; i < points.Count; i++)
        {
            var a = points[i - 1];
            var b = points[i];
            float len = a.DistanceTo(b);
            float pos = 0;
            while (len - pos > 1e-4f)
            {
                float step = Math.Min(left, len - pos);
                var p0 = a.Lerp(b, pos / len);
                var p1 = a.Lerp(b, (pos + step) / len);
                if (on)
                    DrawLine(p0, p1, colour, width, true);
                pos += step;
                left -= step;
                if (left <= 1e-4f)
                {
                    on = !on;
                    left = dash;
                }
            }
        }
    }
}
