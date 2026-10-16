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

    private static readonly Color BlastCore = new(1f, 0.97f, 0.85f);
    private static readonly Color BlastFire = new(1f, 0.55f, 0.15f);
    private static readonly Color DangerRed = new(0.95f, 0.2f, 0.15f);

    public void SetNukeGhost(NukeType type, int tile, bool valid)
    {
        _aimType = type;
        _aimTile = tile;
        _aimValid = valid;
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

        // Where it will land (people see bombs coming): the blast circle, red if it's coming for you.
        if (n.Type != NukeType.TsarBomba)
        {
            var (inner, outer) = _game!.Rules.Blast(n.Type);
            var target = TileCentre(n.To, w);
            var ring = atMe ? DangerRed : mine ? new Color(1, 1, 1, 0.8f) : new Color(colour, 0.8f);
            if (n.Type != NukeType.Warhead || atMe || mine)
            {
                DrawArc(target, outer, 0, Mathf.Tau, 64, new Color(ring, 0.7f), 1.5f / zoom, true);
                DrawCircle(target, inner, new Color(ring, 0.12f));
            }
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

    private void DrawSamMissile(SamMissile m, float zoom, int w)
    {
        var from = TileCentre(m.From, w);
        var to = TileCentre(m.To, w);
        float f = Math.Clamp((m.Age - 1 + _tickFraction) / m.FlightTicks, 0, 1);
        var pos = from.Lerp(to, f);
        var back = from.Lerp(to, Math.Max(0, f - 0.25f));
        DrawLine(back, pos, new Color(1, 1, 1, 0.6f), 1.5f / zoom, true);
        DrawCircle(pos, 2.5f / zoom, Colors.White);
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
                if (age > 0.8f)
                    continue;
                float r = (6 + 10 * age) / zoom;
                DrawCircle(at, r, new Color(1, 1, 1, 0.5f * (1 - age / 0.8f)));
                DrawArc(at, r * 1.4f, 0, Mathf.Tau, 24, new Color(1, 0.8f, 0.4f, 0.8f * (1 - age / 0.8f)), 1.5f / zoom, true);
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

    /// <summary>Aiming a bomb: the blast circles at the mouse and the arc from your nearest loaded silo.</summary>
    private void DrawAim(float zoom, int w)
    {
        if (_aimType is not { } type || _aimTile < 0 || _me is null)
            return;
        var at = TileCentre(_aimTile, w);
        var tint = _aimValid ? new Color(1f, 0.85f, 0.3f) : DangerRed;
        if (type == NukeType.TsarBomba)
        {
            DrawArc(at, _game!.Rules.MirvRange, 0, Mathf.Tau, 128, new Color(tint, 0.5f), 1.5f / zoom, true);
            DrawCircle(at, 20, new Color(tint, 0.35f));
        }
        else
        {
            var (inner, outer) = _game!.Rules.Blast(type);
            DrawCircle(at, outer, new Color(tint, 0.12f));
            DrawCircle(at, inner, new Color(tint, 0.28f));
            DrawArc(at, outer, 0, Mathf.Tau, 64, new Color(tint, 0.8f), 1.5f / zoom, true);
        }
        if (_game!.ReadySilo(_me, _aimTile) is { } silo)
        {
            int sx = silo.Tile % w, sy = silo.Tile / w;
            int tx = type == NukeType.TsarBomba ? (_aimTile % w + sx) / 2 : _aimTile % w;
            int ty = type == NukeType.TsarBomba ? Math.Max(0, _aimTile / w - 500) + 50 : _aimTile / w;
            var (xs, ys) = NukeRules.Arc(sx, sy, tx, ty, _game.Map.Height, 3, _game.Rules.NukeArcMinHeight);
            var points = new List<Vector2>(xs.Length + 1) { TileCentre(silo.Tile, w) };
            for (int i = 0; i < xs.Length; i++)
                points.Add(new Vector2(xs[i], ys[i]));
            DrawDashes(points, new Color(tint, 0.8f), 1.5f / zoom, 8 / zoom);
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
