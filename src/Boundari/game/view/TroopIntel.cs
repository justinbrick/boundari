using Boundari.Sim;

namespace Boundari.View;

/// <summary>
/// What you get to see of other players' troops and gold under the light fog of war (see
/// <see cref="Game.FogOfWar"/>): every place that shows another player's troops or gold (name
/// labels, hover panel, leaderboard) asks here, so they all show the same thing. Only
/// yourself and your allies are shown; everyone else's numbers are hidden.
/// </summary>
public sealed class TroopIntel
{
    private Game? _game;
    private Player? _me;

    public void SetGame(Game game, Player me)
    {
        _game = game;
        _me = me;
    }

    /// <summary>Whether this player's troops and gold are shown: ourselves and allies (and everyone, without the fog or once we're out).</summary>
    public bool Sees(Player p) =>
        _game is null || _me is null || !_game.FogOfWar || p == _me || !_me.IsAlive || !_me.HasSpawned || _me.Allies.Contains(p.Id);

    /// <summary>The troop count as text, or "?" for a player we can't see.</summary>
    public string TroopText(Player p) => Sees(p) ? GameHud.FormatCount(p.Troops) : "?";

    /// <summary>Gold is only shown for ourselves and allies.</summary>
    public bool ShowsGold(Player p) => Sees(p);
}
