using System;

namespace Boundari.Sim;

/// <summary>
/// Directed attacks: Boundari's own rule, not OpenFront's. The player drags out an arrow;
/// it only gives the attack a general direction and a shape, never an area. Every tile of
/// the target can still be taken, as in a normal attack; the arrow just changes which ones
/// go first. A short, flat arrow leans gently that way and pushes wide, slow but strong; a
/// long, thin arrow leans hard into a narrow, fast finger that pays more troops per tile.
/// </summary>
public static class DirectedRules
{
    // Shortest/flattest arrow (thin 0) to longest/thinnest (thin 1), in between linearly.
    public const double FlatSpeed = 0.7, ThinSpeed = 2.0;   // tiles per tick, times a normal attack's
    public const double FlatLoss = 0.8, ThinLoss = 1.3;     // attacker's troops lost per tile, times
    public const double FlatLean = 0.1, ThinLean = 0.3;   // how much sooner a step straight ahead comes than one sideways (and later one back)

    /// <summary>
    /// How far off the arrow's line (reference tiles) the thinnest arrow's push stays
    /// quick; flatter arrows reach wider (the flattest has no limit). Further out,
    /// tiles are only taken later, never left out.
    /// </summary>
    public const int ThinWidthTiles = 10;

    /// <summary>Extra wait, in ticks, for a tile one width off the line; it grows with the square of the distance...</summary>
    public const double SideTicks = 10;
    /// <summary>...up to this.</summary>
    public const double MaxSideTicks = 300;
}

/// <summary>The arrow a directed attack was given (see <see cref="DirectedRules"/>).</summary>
public sealed class AttackArrow
{
    /// <summary>Where the arrow was drawn from and to (tiles).</summary>
    public readonly int FromTile, ToTile;
    /// <summary>0 = shortest, flattest arrow .. 1 = longest, thinnest.</summary>
    public readonly double Thin;
    /// <summary>The arrow's direction, one tile long.</summary>
    public readonly double DirX, DirY;

    internal readonly double Speed, Loss, LeanStrength, InvWidth;

    /// <summary>The widest front (tiles) that sets this attack's pace: twice its width off the line (no limit for the flattest).</summary>
    internal readonly int FrontCap;
    private readonly int _fromX, _fromY;

    internal AttackArrow(int fromTile, int toTile, int thinPermille, int mapWidth, ScaledRules rules)
    {
        FromTile = fromTile;
        ToTile = toTile;
        Thin = Math.Clamp(thinPermille, 0, 1000) / 1000.0;
        _fromX = fromTile % mapWidth;
        _fromY = fromTile / mapWidth;
        double dx = toTile % mapWidth - _fromX, dy = toTile / mapWidth - _fromY, length = Math.Sqrt(dx * dx + dy * dy);
        DirX = dx / length;
        DirY = dy / length;
        Speed = DirectedRules.FlatSpeed + (DirectedRules.ThinSpeed - DirectedRules.FlatSpeed) * Thin;
        Loss = DirectedRules.FlatLoss + (DirectedRules.ThinLoss - DirectedRules.FlatLoss) * Thin;
        LeanStrength = DirectedRules.FlatLean + (DirectedRules.ThinLean - DirectedRules.FlatLean) * Thin;
        InvWidth = Thin / rules.D(DirectedRules.ThinWidthTiles);
        FrontCap = InvWidth > 0 ? (int)Math.Min(int.MaxValue, Math.Ceiling(2 / InvWidth)) : int.MaxValue;
    }

    /// <summary>
    /// A tile's wait in the attack's queue (40ths of a tick), leaned towards the arrow: a
    /// step straight ahead waits less, a step back more, and the further the tile is off
    /// the arrow's line, the longer it waits (thin arrows only).
    /// </summary>
    internal long Lean(long delay, int fromTile, int toTile, int mapWidth)
    {
        int tx = toTile % mapWidth, ty = toTile / mapWidth;
        double ahead = (tx - fromTile % mapWidth) * DirX + (ty - fromTile / mapWidth) * DirY; // 1 straight ahead .. -1 straight back
        double wait = delay * (1 - LeanStrength * ahead);
        if (InvWidth > 0)
        {
            double off = Math.Abs((tx - _fromX) * DirY - (ty - _fromY) * DirX) * InvWidth;
            wait += 40 * Math.Min(DirectedRules.MaxSideTicks, DirectedRules.SideTicks * off * off);
        }
        return (long)wait;
    }
}

public sealed partial class Game
{
    /// <summary>Match option: players may drag out directed attacks (on by default; off = classic OpenFront).</summary>
    public bool DirectedAttacks { get; set; } = true;

    /// <summary>A directed attack (see <see cref="DirectedRules"/>); without the option, or with no real arrow, a normal one.</summary>
    private void StartDirectedAttack(Player attacker, DirectedAttackIntent intent)
    {
        long troops = attacker.Troops * Math.Clamp(intent.RatioPercent, 1, 100) / 100;
        bool valid = intent.FromTile >= 0 && intent.FromTile < Map.TileCount && intent.ToTile >= 0 && intent.ToTile < Map.TileCount
            && intent.FromTile != intent.ToTile;
        var arrow = DirectedAttacks && valid ? new AttackArrow(intent.FromTile, intent.ToTile, intent.ThinPermille, Map.Width, Rules) : null;
        StartAttack(attacker, intent.TargetId, troops, arrow);
    }
}
