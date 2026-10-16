using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace Boundari.View;

/// <summary>
/// "+gold" pop-ups on the map, like OpenFront's: income ("+ 45.0K") rises from the port or
/// station it was earned at and fades over 1.5 s; gold taken in a conquest sits under the
/// beaten player's name and fades over 2.5 s.
/// </summary>
public partial class MapOverlay
{
    private const double IncomeSeconds = 1.5, ConquestSeconds = 2.5;
    private const float IncomeRiseTiles = 3;     // per second
    private const float IncomeStartTiles = -3;   // above the tile
    private const float PopupTiles = 6;          // text size on the map (OpenFront's world scale)
    private const float PopupMinPixels = 12, PopupMaxPixels = 36;
    private const float PopupHiddenBelowZoom = 0.45f; // reference px per tile (OpenFront's cull zoom x1.5)

    private sealed record GoldPopup(Vector2 At, string Text, double Start, double Lifetime, bool Rises);

    private readonly List<GoldPopup> _goldPopups = new();

    /// <summary>Income at this map position (rises and fades).</summary>
    public void AddIncomePopup(Vector2 at, long gold) =>
        _goldPopups.Add(new GoldPopup(at + new Vector2(0, IncomeStartTiles * (float)(_game?.Rules.Linear ?? 1)), "+ " + PopupGold(gold), Now, IncomeSeconds, true));

    /// <summary>Conquest gold under a name (fades in place).</summary>
    public void AddConquestPopup(Vector2 at, long gold) =>
        _goldPopups.Add(new GoldPopup(at, "+ " + PopupGold(gold), Now, ConquestSeconds, false));

    private static double Now => Time.GetTicksMsec() / 1000.0;

    /// <summary>OpenFront's popup numbers: 950, 45.0K, 1.2M.</summary>
    private static string PopupGold(long gold)
    {
        var inv = CultureInfo.InvariantCulture;
        return gold >= 1_000_000 ? (gold / 1_000_000.0).ToString("0.0", inv) + "M"
            : gold >= 1_000 ? (gold / 1_000.0).ToString("0.0", inv) + "K"
            : gold.ToString(inv);
    }

    private void DrawGoldPopups(float zoom)
    {
        double now = Now;
        _goldPopups.RemoveAll(p => now - p.Start >= p.Lifetime);
        if (_goldPopups.Count == 0 || ReferenceZoom(zoom) < PopupHiddenBelowZoom)
            return;
        float linear = (float)(_game?.Rules.Linear ?? 1);
        int px = (int)Math.Clamp(PopupTiles * linear * zoom, PopupMinPixels, PopupMaxPixels);
        foreach (var p in _goldPopups)
        {
            float age = (float)(now - p.Start);
            float alpha = Math.Max(0, 1 - age / (float)p.Lifetime);
            var at = p.At - new Vector2(0, p.Rises ? IncomeRiseTiles * linear * age : 0);
            if (!OnScreen(at, 50 / zoom))
                continue;
            // Drawn in screen pixels at the spot, so the text stays sharp.
            float width = _font.GetStringSize(p.Text, HorizontalAlignment.Left, -1, px).X;
            DrawSetTransform(at, 0, Vector2.One / zoom);
            var origin = new Vector2(-width / 2, px * 0.35f);
            DrawStringOutline(_font, origin, p.Text, HorizontalAlignment.Left, -1, px, Math.Max(2, px / 6), new Color(0, 0, 0, 0.75f * alpha));
            DrawString(_font, origin, p.Text, HorizontalAlignment.Left, -1, px, new Color(1, 1, 1, alpha));
        }
        DrawSetTransform(Vector2.Zero, 0, Vector2.One);
    }
}
