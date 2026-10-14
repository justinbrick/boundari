using System;
using Boundari.Core;

namespace Boundari.Sim;

/// <summary>
/// The most basic bot: every couple of seconds it looks at its options and picks one.
///   1. Wait until it has a decent share of its troop capacity.
///   2. If empty land touches its border, expand into it.
///   3. Otherwise attack the weakest neighbour, if clearly stronger than them.
/// Bots only submit intents, like a human would, and use their own seeded RNG,
/// so they behave identically on every machine.
/// </summary>
public sealed class SimpleBot
{
    private const int MinThinkTicks = 15;
    private const int MaxThinkTicks = 35;

    // Act once troops reach this share of capacity (percent).
    private const int ReadyPercent = 30;
    private const int ExpandRatioPercent = 40;
    private const int AttackRatioPercent = 50;

    // Attack a neighbour only if our troops per tile beat theirs by this much (percent).
    private const int AttackAdvantagePercent = 130;

    private readonly Game _game;
    private readonly Player _player;
    private readonly Rng _rng;
    private int _nextThink;

    public SimpleBot(Game game, Player player, Rng rng)
    {
        _game = game;
        _player = player;
        _rng = rng;
        _nextThink = _rng.RangeInt(0, MaxThinkTicks); // stagger bots so they don't all act at once
    }

    public void Think()
    {
        if (_game.Tick < _nextThink || !_player.HasSpawned || !_player.IsAlive)
            return;
        _nextThink = _game.Tick + _rng.RangeInt(MinThinkTicks, MaxThinkTicks);

        if (_player.Troops * 100 < _player.MaxTroops * ReadyPercent)
            return;

        // Look along the border: is there empty land, and who are the neighbours?
        bool emptyLandNearby = false;
        ushort weakest = Game.Nobody;
        long weakestDensity = long.MaxValue;
        Span<int> nb = stackalloc int[4];
        foreach (int tile in _player.Border)
        {
            int count = _game.LandNeighbours(tile, nb);
            for (int k = 0; k < count; k++)
            {
                ushort owner = _game.Owner[nb[k]];
                if (owner == _player.Id)
                    continue;
                if (owner == Game.Nobody)
                {
                    emptyLandNearby = true;
                    continue;
                }
                var other = _game.GetPlayer(owner)!;
                long density = other.Troops / Math.Max(1, other.TileCount);
                if (density < weakestDensity)
                {
                    weakestDensity = density;
                    weakest = owner;
                }
            }
        }

        if (emptyLandNearby)
        {
            _game.Submit(new AttackIntent(_player.Id, Game.Nobody, ExpandRatioPercent));
            return;
        }

        long myDensity = _player.Troops / Math.Max(1, _player.TileCount);
        if (weakest != Game.Nobody && myDensity * 100 >= weakestDensity * AttackAdvantagePercent)
            _game.Submit(new AttackIntent(_player.Id, weakest, AttackRatioPercent));
    }
}
