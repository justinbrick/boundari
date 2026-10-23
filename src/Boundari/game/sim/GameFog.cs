namespace Boundari.Sim;

/// <summary>
/// Light fog of war: Boundari's own rule, not OpenFront's. What you can see of other
/// players' troops and gold (display only: the simulation and the AI use the real numbers).
/// You and your allies are shown exactly and live; everyone else's troops (current and
/// maximum) and gold are hidden.
/// </summary>
public sealed partial class Game
{
    /// <summary>Match option: light fog of war (on by default; off = classic OpenFront, everything shown).</summary>
    public bool FogOfWar { get; set; } = true;
}
