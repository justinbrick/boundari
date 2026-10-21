using System;
using System.Collections.Generic;
using Boundari.Sim;
using Godot;

namespace Boundari.View;

/// <summary>
/// Troop counts riding on the front lines of the attacks you're in, like OpenFront's
/// AttackingTroopsController: your attacks on other players in blue, other players' attacks
/// on you in red (tribes' attacks aren't labelled). One number sits on each of an attack's
/// (up to two) biggest front pieces; the spots are worked out 5 times a second and the
/// numbers glide there over 0.25 s, jumping instead when the front moves very far.
/// </summary>
public partial class MapOverlay
{
    private const double FrontPollSeconds = 0.2, FrontGlideSeconds = 0.25;
    private const int FrontSnapTiles = 200;
    private const int FrontLabelPixels = 24; // OpenFront's 17 px, x1.5; the same size at any zoom
    private static readonly Color OutgoingColour = new("#3fa9f5"), IncomingColour = new("#f87171");

    private sealed class FrontSlot
    {
        public Vector2 From, To;
        public double Start;
        public Vector2 At(double now) => From.Lerp(To, (float)Math.Min(1, (now - Start) / FrontGlideSeconds));
    }

    private sealed class FrontLabel
    {
        public string Text = "";
        public bool Incoming;
        public readonly List<FrontSlot> Slots = new();
    }

    private readonly Dictionary<int, FrontLabel> _fronts = new();
    private double _nextFrontPoll;

    private void DrawAttackLabels(float zoom, int w)
    {
        if (_game is null || _me is null)
            return;
        double now = Now;
        if (now >= _nextFrontPoll)
        {
            _nextFrontPoll = now + FrontPollSeconds;
            PollFronts(now, w);
        }
        if (_fronts.Count == 0)
            return;
        foreach (var front in _fronts.Values)
            foreach (var slot in front.Slots)
            {
                var at = slot.At(now);
                if (!OnScreen(at, 100 / zoom))
                    continue;
                float width = _font.GetStringSize(front.Text, HorizontalAlignment.Left, -1, FrontLabelPixels).X;
                DrawSetTransform(at, 0, Vector2.One / zoom);
                var origin = new Vector2(-width / 2, FrontLabelPixels * 0.35f);
                DrawStringOutline(_font, origin, front.Text, HorizontalAlignment.Left, -1, FrontLabelPixels, 4, new Color(0, 0, 0, 0.8f));
                DrawString(_font, origin, front.Text, HorizontalAlignment.Left, -1, FrontLabelPixels, front.Incoming ? IncomingColour : OutgoingColour);
            }
        DrawSetTransform(Vector2.Zero, 0, Vector2.One);
    }

    /// <summary>Which attacks get a number, and where each number is heading.</summary>
    private void PollFronts(double now, int w)
    {
        var active = new HashSet<int>();
        foreach (var attack in _game!.Attacks)
        {
            if (attack.Retreating)
                continue;
            bool outgoing = attack.Attacker == _me && attack.TargetId != 0;
            bool incoming = attack.TargetId == _me!.Id && !attack.Attacker.IsBot;
            if (!outgoing && !incoming)
                continue;
            active.Add(attack.Id);
            if (!_fronts.TryGetValue(attack.Id, out var front))
                _fronts[attack.Id] = front = new FrontLabel();
            front.Text = GameHud.FormatCount(attack.Troops);
            front.Incoming = incoming;

            var tiles = _game.AttackFrontTiles(attack);
            var targets = tiles.ConvertAll(t => TileCentre(t, w));
            if (targets.Count == 2 && front.Slots.Count == 2)
            {
                // Keep each number on its own front piece when the two swap places in the list.
                float straight = front.Slots[0].To.DistanceTo(targets[0]) + front.Slots[1].To.DistanceTo(targets[1]);
                float swapped = front.Slots[0].To.DistanceTo(targets[1]) + front.Slots[1].To.DistanceTo(targets[0]);
                if (swapped < straight)
                    (targets[0], targets[1]) = (targets[1], targets[0]);
            }
            if (front.Slots.Count > targets.Count)
                front.Slots.RemoveRange(targets.Count, front.Slots.Count - targets.Count);
            float snap = _game.Rules.D(FrontSnapTiles);
            for (int i = 0; i < targets.Count; i++)
            {
                if (i >= front.Slots.Count)
                {
                    front.Slots.Add(new FrontSlot { From = targets[i], To = targets[i], Start = now });
                    continue;
                }
                var slot = front.Slots[i];
                var from = slot.At(now);
                slot.From = from.DistanceTo(targets[i]) > snap ? targets[i] : from;
                slot.To = targets[i];
                slot.Start = now;
            }
        }
        foreach (int id in new List<int>(_fronts.Keys))
            if (!active.Contains(id))
                _fronts.Remove(id);
    }
}
