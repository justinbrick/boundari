using System;
using Boundari.Sim;
using Godot;

namespace Boundari.View;

/// <summary>
/// Dots, drawn plainly for now (the real look comes with the controls, in step 6 of the
/// dots plan): a circle in the owner's colour, darker the stronger it is; a white rim while
/// it is attacking; a ring on the dot a click would send (white) or stop (red); and, for
/// your own dots, how much stamina is left as an arc round the dot once it is tiring.
/// </summary>
public partial class MapOverlay
{
    private const float DotTiles = 1.5f;        // radius on the map...
    private const float DotMinPixels = 4;       // ...but never smaller than this on screen
    private static readonly Color DotRim = new(0.05f, 0.05f, 0.06f, 0.9f);
    private static readonly Color PushRim = new(1f, 1f, 1f, 0.95f);
    private static readonly Color SendRing = new(1f, 1f, 1f, 0.95f);
    private static readonly Color StopRing = new(1f, 0.25f, 0.2f, 0.95f);
    private static readonly Color StaminaArc = new(1f, 0.85f, 0.25f, 0.95f);

    private int _dotHover = -1;
    private bool _dotHoverStops;

    /// <summary>The dot a click would send into the land under the mouse (-1 = none), and whether the click would stop it instead.</summary>
    public void SetDotHover(int dotId, bool stops)
    {
        _dotHover = dotId;
        _dotHoverStops = stops;
    }

    private void DrawDots(float zoom, int w)
    {
        if (_game is null || _game.Dots.Count == 0)
            return;
        float r = Math.Max(DotTiles, DotMinPixels / zoom), px = 1 / zoom;
        foreach (var dot in _game.Dots)
        {
            var at = TileCentre(dot.Tile, w);
            if (dot.IsWalking)
                at = at.Lerp(TileCentre(dot.NextTile, w), (float)dot.StepFraction(w));
            if (!OnScreen(at, r * 3))
                continue;
            var fill = ColorOf(dot.Owner.Id).Darkened(0.15f * (dot.Strength - 1));
            bool pushing = !dot.IsWalking && _game.PushOf(dot) is not null;
            DrawCircle(at, r + 1.5f * px, pushing ? PushRim : DotRim);
            DrawCircle(at, r, fill);
            if (dot.Owner == _me && dot.Stamina < DotRules.MaxStamina)
            {
                float left = (float)dot.Stamina / DotRules.MaxStamina;
                DrawArc(at, r + 3.5f * px, -Mathf.Pi / 2, -Mathf.Pi / 2 + Mathf.Tau * left, 24, StaminaArc, 2 * px, true);
            }
            if (dot.Id == _dotHover)
                DrawArc(at, r * 1.6f + 4 * px, 0, Mathf.Tau, 32, _dotHoverStops ? StopRing : SendRing, 2.5f * px, true);
        }
    }
}
