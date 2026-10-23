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

/// <summary>
/// A directed attack (Boundari's own): like <see cref="AttackIntent"/>, but leaning the way
/// an arrow drawn from <paramref name="FromTile"/> to <paramref name="ToTile"/> points.
/// <paramref name="ThinPermille"/> is the arrow's shape: 0 = short and flat (wide, slow,
/// strong) .. 1000 = long and thin (narrow, fast, weak). See <see cref="DirectedRules"/>.
/// </summary>
public sealed record DirectedAttackIntent(ushort PlayerId, ushort TargetId, int RatioPercent, int FromTile, int ToTile, int ThinPermille) : Intent(PlayerId);

/// <summary>Call off one of your attacks (OpenFront's retreat: the troops come home after a short delay, a quarter lost if the target was a player).</summary>
public sealed record RetreatIntent(ushort PlayerId, int AttackId) : Intent(PlayerId);

/// <summary>
/// Ask another player for an alliance, or accept theirs if they already asked you (as in
/// OpenFront, asking someone who asked you is how you say yes). Sent to an ally near the
/// end of the alliance, it asks to renew it (renewed once both have asked).
/// </summary>
public sealed record AllianceIntent(ushort PlayerId, ushort TargetId) : Intent(PlayerId);

/// <summary>Break an alliance (the breaker is branded a traitor for a while).</summary>
public sealed record BreakAllianceIntent(ushort PlayerId, ushort TargetId) : Intent(PlayerId);

/// <summary>Turn down someone's alliance request.</summary>
public sealed record RejectAllianceIntent(ushort PlayerId, ushort RequesterId) : Intent(PlayerId);

/// <summary>
/// Buy up to <paramref name="Count"/> dots (cap and Funds allowing): they appear in the
/// middle of your land and walk to <paramref name="BorderTile"/>, spreading out along it.
/// </summary>
public sealed record BuyDotsIntent(ushort PlayerId, int Count, int BorderTile) : Intent(PlayerId);

/// <summary>Sell one of your dots (half of what it cost comes back).</summary>
public sealed record SellDotIntent(ushort PlayerId, int DotId) : Intent(PlayerId);

/// <summary>Raise one of your dots a strength level (up to 4).</summary>
public sealed record UpgradeDotIntent(ushort PlayerId, int DotId) : Intent(PlayerId);

/// <summary>Send some of your dots to a border tile: they walk there through your land and spread along it.</summary>
public sealed record MoveDotsIntent(ushort PlayerId, int[] DotIds, int BorderTile) : Intent(PlayerId);

/// <summary>
/// Spread some of your dots evenly along your border from one point to another (each
/// snapped to your nearest border tile), keeping their order: dragging a group's end
/// handle spreads or cramps it, dragging its middle handle slides it along the border.
/// </summary>
public sealed record SpreadDotsIntent(ushort PlayerId, int[] DotIds, int FromTile, int ToTile) : Intent(PlayerId);

/// <summary>
/// Attack with the stretch of border <paramref name="DotId"/> stands on: every dot on it
/// pushes into the land of whoever owns <paramref name="TargetTile"/> until stopped (not the wilderness: see <see cref="ExpandIntent"/>).
/// </summary>
public sealed record PushIntent(ushort PlayerId, int DotId, int TargetTile) : Intent(PlayerId);

/// <summary>Stop the attack this dot is part of (its dots stay where the line is).</summary>
public sealed record StopPushIntent(ushort PlayerId, int DotId) : Intent(PlayerId);

/// <summary>Start (or stop) expanding into the wilderness all along your border.</summary>
public sealed record ExpandIntent(ushort PlayerId, bool Start = true) : Intent(PlayerId);

/// <summary>Tear down one of your own buildings (it goes after a delay, as in OpenFront).</summary>
public sealed record DemolishIntent(ushort PlayerId, int Tile) : Intent(PlayerId);
