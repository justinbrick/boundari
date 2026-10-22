using Godot;

namespace Boundari.View;

/// <summary>
/// Light fog of war: while you hover a player further away (not a neighbour or ally),
/// a thick white ring beside the mouse fills up clockwise until their troop count
/// refreshes (see <see cref="Sim.FogRules"/>).
/// </summary>
public partial class MapOverlay
{
    private const float RingRadiusPixels = 11, RingWidthPixels = 5;
    private static readonly Vector2 RingOffsetPixels = new(26, 30); // below and right of the pointer

    private Vector2? _ringAt;   // the mouse (map units), or null for no ring
    private float _ringProgress;

    public void SetRefreshRing(Vector2 mouse, float progress)
    {
        _ringAt = mouse;
        _ringProgress = progress;
    }

    public void ClearRefreshRing() => _ringAt = null;

    private void DrawRefreshRing(float zoom)
    {
        if (_ringAt is not { } mouse)
            return;
        DrawSetTransform(mouse, 0, Vector2.One / zoom);
        var centre = RingOffsetPixels;
        DrawArc(centre, RingRadiusPixels, 0, Mathf.Tau, 48, new Color(0, 0, 0, 0.45f), RingWidthPixels + 3, true); // dark edge, readable on light land
        DrawArc(centre, RingRadiusPixels, 0, Mathf.Tau, 48, new Color(1, 1, 1, 0.22f), RingWidthPixels, true);     // the empty track
        float start = -Mathf.Pi / 2; // from the top, clockwise
        if (_ringProgress > 0.002f)
            DrawArc(centre, RingRadiusPixels, start, start + Mathf.Tau * Mathf.Clamp(_ringProgress, 0, 1),
                48, Colors.White, RingWidthPixels, true);
        DrawSetTransform(Vector2.Zero, 0, Vector2.One);
    }
}
