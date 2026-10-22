using System.Collections.Generic;
using Boundari.Sim;

namespace Boundari.View;

/// <summary>
/// What you get to see of other players' troops and gold under the light fog of war (see
/// <see cref="FogRules"/>): every place that shows another player's troops or gold (name
/// labels, hover panel, leaderboard) asks here, so they all show the same thing. Updated
/// every frame on the main thread (after SyncSim).
/// </summary>
public sealed class TroopIntel
{
    public enum Sight { Exact, Neighbour, Far }

    private const double NeighbourPollSeconds = 0.2;

    private Game? _game;
    private Player? _me;
    private readonly HashSet<ushort> _neighbours = new();
    private double _sinceNeighbours = NeighbourPollSeconds;
    // Further players' shown troops, and the refresh they're from (FogRules.LastFarRefresh).
    private readonly Dictionary<ushort, (long Troops, int Refresh)> _far = new();

    public void SetGame(Game game, Player me)
    {
        _game = game;
        _me = me;
        _neighbours.Clear();
        _far.Clear();
        _sinceNeighbours = NeighbourPollSeconds;
    }

    /// <summary>Every frame: who our neighbours are (5 times a second), and further players' refreshes.</summary>
    public void Update(double delta)
    {
        if (_game is null || _me is null)
            return;
        if ((_sinceNeighbours += delta) >= NeighbourPollSeconds)
        {
            _sinceNeighbours = 0;
            _neighbours.Clear();
            if (_me.IsAlive)
                foreach (var p in _game.BorderingPlayers(_me))
                    _neighbours.Add(p.Id);
        }
        // Take each further player's number at the moment their refresh comes round.
        foreach (var p in _game.Players)
        {
            if (!p.IsAlive)
                continue;
            int refresh = FogRules.LastFarRefresh(p.Id, _game.Tick);
            if (!_far.TryGetValue(p.Id, out var seen) || seen.Refresh != refresh)
                _far[p.Id] = (p.Troops, refresh);
        }
    }

    /// <summary>How well we see this player: ourselves and allies exactly (and everyone, without the fog or once we're out).</summary>
    public Sight SightOf(Player p)
    {
        if (_game is null || _me is null || !_game.FogOfWar || p == _me || !_me.IsAlive || !_me.HasSpawned || _me.Allies.Contains(p.Id))
            return Sight.Exact;
        return _neighbours.Contains(p.Id) ? Sight.Neighbour : Sight.Far;
    }

    /// <summary>The troops we're shown for this player (exact, approximate, or approximate and up to 20 s old).</summary>
    public long ShownTroops(Player p) => SightOf(p) switch
    {
        Sight.Exact => p.Troops,
        Sight.Neighbour => FogRules.Approximate(p.Troops, near: true),
        _ => FogRules.Approximate(_far.TryGetValue(p.Id, out var seen) ? seen.Troops : p.Troops, near: false),
    };

    /// <summary>The troop count as text: approximate ones start with "~".</summary>
    public string TroopText(Player p) =>
        (SightOf(p) == Sight.Exact ? "" : "~") + GameHud.FormatCount(ShownTroops(p));

    /// <summary>Gold is only shown for ourselves and allies (and everyone, without the fog or once we're out).</summary>
    public bool ShowsGold(Player p) => SightOf(p) == Sight.Exact;

    /// <summary>For a further player: how far along to their next refresh (0..1); null for anyone we see live.</summary>
    public double? RefreshProgress(Player p, double tickFraction = 0) =>
        _game is not null && SightOf(p) == Sight.Far ? FogRules.FarCycle(p.Id, _game.Tick, tickFraction) : null;
}
