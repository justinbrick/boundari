using Boundari.Core;

namespace Boundari.Sim;

/// <summary>
/// A nation: a proper AI opponent, much stronger than a tribe, run like OpenFront's
/// NationExecution. Right after the spawn phase it pours half its troops into empty land.
/// Then once per attack cycle (a rhythm rolled per nation, faster on harder difficulties)
/// it, in OpenFront's order: sends casual emojis, answers alliance requests and renewals,
/// considers an Atomic Bomb, builds, maybe launches a warship, sets embargoes, attacks
/// (see <see cref="NationAttacks"/>), counters warship swarms and considers bombing someone.
/// It also builds a third and two thirds of the way through the cycle, and every tick
/// follows up its boat landings and bomb strikes and hits back at whoever sinks its ships.
/// Nations only submit intents, like a human would, and use their own seeded RNG, so they
/// behave identically on every machine.
/// </summary>
internal sealed class NationAi : IPlayerAi
{
    private readonly Game _game;
    private readonly Player _player;
    private readonly NationStructures _structures;
    private readonly NationNukes _nukes;
    private readonly NationAlliances _alliances;
    private readonly NationEmojis _emojis;
    private readonly NationWarships _warships;
    private readonly NationAttacks _attacks;
    private readonly int _attackRate, _attackTick;
    private bool _started;

    public NationAi(Game game, Player player, Rng rng)
    {
        _game = game;
        _player = player;

        // Each nation's nerve, rolled once (OpenFront): attack players once troops reach the
        // trigger share of the cap, never send below the reserve, and keep only the expand
        // reserve home when grabbing empty land or tribes.
        double trigger = rng.RangeInt(50, 59) / 100.0;
        double reserve = rng.RangeInt(30, 39) / 100.0;
        double expand = rng.RangeInt(10, 19) / 100.0;

        // OpenFront's attack rhythm by difficulty, in ticks.
        _attackRate = game.Difficulty switch
        {
            Difficulty.Easy => rng.RangeInt(65, 99),
            Difficulty.Medium => rng.RangeInt(55, 69),
            Difficulty.Hard => rng.RangeInt(45, 59),
            _ => rng.RangeInt(30, 49),
        };
        _attackTick = rng.RangeInt(0, _attackRate - 1);

        _emojis = new NationEmojis(game, player, new Rng(rng.Fork()));
        _structures = new NationStructures(game, player, new Rng(rng.Fork()));
        _nukes = new NationNukes(game, player, new Rng(rng.Fork()), _emojis);
        _alliances = new NationAlliances(game, player, new Rng(rng.Fork()), _emojis);
        _warships = new NationWarships(game, player, new Rng(rng.Fork()), _emojis);
        _attacks = new NationAttacks(game, player, new Rng(rng.Fork()), trigger, reserve, expand, _alliances, _emojis, _warships);
    }

    public void Think()
    {
        if (!_player.HasSpawned || !_player.IsAlive || _game.Phase != GamePhase.Running)
            return;
        if (!_started)
        {
            _started = true;
            _attacks.FirstExpansion();
            return;
        }

        // Every tick.
        if (_game.Difficulty != Difficulty.Easy && _player.Constructed[(int)BuildingType.Port] > 0)
            _warships.TrackShipsAndRetaliate();
        _attacks.FollowUpLandings();
        _nukes.FollowUpStrikes(_attacks);

        int offset = _game.Tick % _attackRate;
        if (offset != _attackTick)
        {
            // Build twice more between attack cycles (a third and two thirds through), or
            // the nation earns more gold than it can spend.
            if (offset == (_attackTick + _attackRate / 3) % _attackRate
                || offset == (_attackTick + _attackRate * 2 / 3) % _attackRate)
                _structures.Handle();
            return;
        }

        _emojis.MaybeSendCasualEmoji();
        _alliances.HandleRequests();
        _alliances.HandleRenewals();
        _nukes.ConsiderTsarBomba();
        _structures.Handle();
        _warships.MaybeSpawnWarship();
        _alliances.HandleEmbargoes();
        _attacks.MaybeAttack();
        _warships.CounterWarshipInfestation();
        _nukes.MaybeSendNuke();
    }
}
