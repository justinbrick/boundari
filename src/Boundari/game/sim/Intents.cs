namespace Boundari.Sim;

/// <summary>
/// Something a player wants to do. Players (human or bot) never change the game
/// directly: they submit intents, which the game applies at the start of the next
/// tick. In multiplayer, intents are what gets sent between machines.
/// </summary>
public abstract record Intent(ushort PlayerId);

/// <summary>Start (or, during the spawn phase, move) a player's starting territory.</summary>
public sealed record SpawnIntent(ushort PlayerId, int Tile) : Intent(PlayerId);

/// <summary>
/// Send a share of the player's troops against a target: another player's id, or 0
/// to expand into empty land. <paramref name="RatioPercent"/> is 1..100; AI players may
/// instead name an exact number of <paramref name="Troops"/> (capped at what they have).
/// </summary>
public sealed record AttackIntent(ushort PlayerId, ushort TargetId, int RatioPercent, long Troops = 0) : Intent(PlayerId);

/// <summary>Tear down one of your own buildings (it goes after a delay, as in OpenFront).</summary>
public sealed record DemolishIntent(ushort PlayerId, int Tile) : Intent(PlayerId);
