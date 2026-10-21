using System;
using System.Collections.Generic;
using Boundari.Sim;
using Godot;

namespace Boundari.View;

/// <summary>
/// OpenFront's alert frame: a thick border round the screen that blinks twice when an ally
/// betrays you (red) or someone sends a real attack at you (orange). Like OpenFront, tribes'
/// attacks, attacks smaller than a fifth of your troops and answers to your own attack in the
/// last 15 s don't count, and after a flash it stays quiet for 15 s. Drawn just under the HUD
/// (first child of Main_Layer), so the HUD stays on top as in OpenFront.
/// </summary>
public partial class AlertFrame : Control
{
    private const float BorderPx = 26;           // OpenFront's 17 CSS px
    private const double BlinkSeconds = 1.6;     // one blink: fade in, then out
    private const int Blinks = 2;
    private const int RetaliationTicks = 15 * Balance.TicksPerSecond;
    private const int CooldownTicks = 15 * Balance.TicksPerSecond;
    private static readonly Color BetrayalColour = Color.FromHtml("#ee0000");
    private static readonly Color AttackColour = Color.FromHtml("#ffa500");

    private Color _colour = AttackColour;
    private double _time = -1; // seconds into the animation, or -1 when hidden
    private int _lastAlertTick = -1;
    private readonly HashSet<int> _seenAttacks = new();
    private readonly Dictionary<ushort, int> _attackedAt = new(); // whom we attacked, and when
    private readonly List<int> _scratch = new();

    public AlertFrame()
    {
        Name = "AlertFrame";
        AnchorRight = 1;
        AnchorBottom = 1;
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;
    }

    /// <summary>Puts the frame under the scene's HUD (or on its own layer if there's no Main_Layer).</summary>
    public static AlertFrame Attach(Node root)
    {
        var frame = new AlertFrame();
        if (root.GetNodeOrNull<CanvasLayer>("Main_Layer") is { } hud)
        {
            hud.AddChild(frame);
            hud.MoveChild(frame, 0);
        }
        else
        {
            var layer = new CanvasLayer { Name = "AlertLayer", Layer = 1 };
            root.AddChild(layer);
            layer.AddChild(frame);
        }
        return frame;
    }

    /// <summary>Reads betrayals from the game's events (before the notification bar clears them).</summary>
    public void TakeEvents(Game game, Player me)
    {
        foreach (var e in game.Events)
            if (e.Kind == GameEventKind.AllianceBroken && e.B == me.Id)
                Flash(BetrayalColour, game.Tick);
    }

    /// <summary>Watches attacks each frame (while the game isn't being stepped).</summary>
    public void Update(Game game, Player me)
    {
        if (!me.HasSpawned || !me.IsAlive)
        {
            _seenAttacks.Clear();
            _attackedAt.Clear();
            _lastAlertTick = -1;
            return;
        }
        int tick = game.Tick;

        // Our own attacks on players, to tell retaliation apart.
        foreach (var a in game.Attacks)
            if (a.Attacker == me && a.TargetId != Game.Nobody && !a.Retreating
                && (!_attackedAt.TryGetValue(a.TargetId, out int at) || tick - at >= RetaliationTicks))
                _attackedAt[a.TargetId] = tick;
        _scratch.Clear();
        foreach (var (id, at) in _attackedAt)
            if (tick - at > RetaliationTicks)
                _scratch.Add(id);
        foreach (int id in _scratch)
            _attackedAt.Remove((ushort)id);

        bool cooling = _lastAlertTick >= 0 && tick - _lastAlertTick < CooldownTicks;
        long small = me.Troops / 5;
        _scratch.Clear();
        foreach (var a in game.Attacks)
        {
            if (a.TargetId != me.Id || a.Retreating)
                continue;
            _scratch.Add(a.Id);
            if (!_seenAttacks.Add(a.Id) || a.Attacker.Kind == PlayerKind.Bot)
                continue;
            bool retaliation = _attackedAt.TryGetValue(a.Attacker.Id, out int at) && tick - at < RetaliationTicks;
            if (!cooling && !retaliation && a.Troops >= small)
            {
                Flash(AttackColour, tick);
                cooling = true;
            }
        }
        _seenAttacks.IntersectWith(_scratch); // forget attacks that have ended
    }

    /// <summary>Testing (--alert): flashes as if betrayed or attacked.</summary>
    public void Test(bool betrayal) => Flash(betrayal ? BetrayalColour : AttackColour, 0);

    private void Flash(Color colour, int tick)
    {
        _colour = colour;
        _time = 0;
        _lastAlertTick = tick;
        Visible = true;
    }

    public override void _Process(double delta)
    {
        if (_time < 0)
            return;
        _time += delta;
        if (_time >= BlinkSeconds * Blinks)
        {
            _time = -1;
            Visible = false;
            return;
        }
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_time < 0)
            return;
        // Each blink: 0 -> 1 -> 0, eased in and out (CSS ease-in-out between keyframes).
        double phase = _time % BlinkSeconds / BlinkSeconds * 2;
        double x = phase < 1 ? phase : 2 - phase;
        float alpha = (float)(x * x * (3 - 2 * x));
        var colour = new Color(_colour, _colour.A * alpha);
        var size = Size;
        float b = Math.Min(BorderPx, Math.Min(size.X, size.Y) / 2);
        DrawRect(new Rect2(0, 0, size.X, b), colour);
        DrawRect(new Rect2(0, size.Y - b, size.X, b), colour);
        DrawRect(new Rect2(0, b, b, size.Y - 2 * b), colour);
        DrawRect(new Rect2(size.X - b, b, b, size.Y - 2 * b), colour);
    }
}
