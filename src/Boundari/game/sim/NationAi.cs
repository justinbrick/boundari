using System;
using Boundari.Core;

namespace Boundari.Sim;

/// <summary>
/// A nation: a proper AI opponent, much stronger than a bot. Every couple of seconds it:
///   1. Waits until it has a decent share of its troop capacity.
///   2. If empty land touches its border, expands into it.
///   3. Otherwise attacks the weakest neighbour, if clearly stronger than them.
/// Between attacks it builds (see <see cref="NationStructures"/>): three building decisions
/// per attack cycle, as OpenFront's nations do. Nations only submit intents, like a human
/// would, and use their own seeded RNG, so they behave identically on every machine.
/// </summary>
internal sealed class NationAi : IPlayerAi
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
    private readonly NationStructures _structures;
    private int _nextThink;
    private int _nextBuild;
    private readonly int _buildEvery;

    public NationAi(Game game, Player player, Rng rng)
    {
        _game = game;
        _player = player;
        _rng = rng;
        _structures = new NationStructures(game, player, new Rng(rng.Fork()));
        _nextThink = _rng.RangeInt(0, MaxThinkTicks); // stagger nations so they don't all act at once

        // OpenFront's nation rhythm (an attack cycle, by difficulty), with building decisions
        // a third of the way through it.
        int cycle = game.Difficulty switch
        {
            Difficulty.Easy => _rng.RangeInt(65, 99),
            Difficulty.Medium => _rng.RangeInt(55, 69),
            Difficulty.Hard => _rng.RangeInt(45, 59),
            _ => _rng.RangeInt(30, 49),
        };
        _buildEvery = Math.Max(1, cycle / 3);
        _nextBuild = _rng.RangeInt(0, _buildEvery);
    }

    public void Think()
    {
        if (!_player.HasSpawned || !_player.IsAlive || _game.Phase != GamePhase.Running)
            return;
        if (_game.Tick >= _nextBuild)
        {
            _nextBuild = _game.Tick + _buildEvery;
            _structures.Handle();
        }
        if (_game.Tick >= _nextThink)
        {
            _nextThink = _game.Tick + _rng.RangeInt(MinThinkTicks, MaxThinkTicks);
            Attack();
        }
    }

    private void Attack()
    {
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
