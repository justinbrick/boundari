using System;
using System.Collections.Generic;
using Boundari.Sim;
using Godot;

namespace Boundari.View;

/// <summary>
/// What the dot controls want shown on the map this frame (World fills it in): the selected
/// dots, the box being dragged out, where dots would go (bought or moved), the handles of
/// the group under the mouse, a dot being sold, and a few words by the mouse.
/// Positions are in map units.
/// </summary>
public sealed class DotOrdersView
{
    public readonly HashSet<int> Selected = new();
    public Rect2? Box;
    public readonly List<Vector2> Ghosts = new();
    public bool GhostsOk = true;
    public readonly List<(Vector2 From, Vector2 To)> Lines = new();

    /// <summary>The hovered group's handles: its two ends (spread / cramp) and its middle (slide); null = none.</summary>
    public Vector2[]? Handles;
    public int HotHandle = -1;

    public int SellingDot = -1;
    public float SellDone;

    /// <summary>A few words by the mouse (buy mode), and the tip over the hovered dot of yours.</summary>
    public string? Label;
    public Vector2 LabelAt;
    public string? Tip;
    public int TipDot = -1;
}

/// <summary>
/// Dots: a circle in the owner's colour with a little depth (lit from the top left, a darker
/// edge bottom right) and a lowercase "t", darker the stronger it is; a white rim while it
/// is attacking; a ring on the dot a click would send (white) or stop (red); a yellow ring
/// on selected dots; and, for your own dots, how much stamina is left as an arc once tiring.
/// Their size follows the zoom (as wide as the spacing between dots), never under 36 px across.
/// Dots glide: each tick, from where it was drawn to where it is now, over the tick.
/// </summary>
public partial class MapOverlay
{
    private const string DotFontFile = "res://fonts/NotoSerifGurmukhi-VariableFont_wght.ttf";
    private const float DotSpacingShare = 0.5f;  // radius, as a share of the spacing between dots (neighbours stand shoulder to shoulder; extras form a second row)
    private const float DotMinPixels = 18;       // radius never under this on screen (36 px across)
    private const float DotLetterPixels = 6;     // no "t" on dots smaller than this (radius)
    private static readonly Color PushRim = new(1f, 1f, 1f, 0.95f);
    private static readonly Color SendRing = new(1f, 1f, 1f, 0.95f);
    private static readonly Color StopRing = new(1f, 0.25f, 0.2f, 0.95f);
    private static readonly Color StaminaArc = new(1f, 0.85f, 0.25f, 0.95f);
    private static readonly Color SelectedRing = new(1f, 0.88f, 0.3f, 1f);
    private static readonly Color SellRing = new(1f, 0.3f, 0.25f, 1f);
    private static readonly Color BadGhost = new(1f, 0.3f, 0.25f, 0.9f);

    private int _dotHover = -1;
    private bool _dotHoverStops;
    private Font? _dotFont;

    // Gliding: per dot, where it is drawn from and to during the current tick.
    private readonly Dictionary<int, (Vector2 From, Vector2 To)> _glide = new();
    private readonly Dictionary<int, Vector2> _drawnAt = new();
    private long _glideTick = -1;
    private Game? _glideGame;

    /// <summary>What the dot controls want shown (World fills it in each frame).</summary>
    public DotOrdersView DotOrders { get; } = new();

    /// <summary>The dot a click would send into the land under the mouse (-1 = none), and whether the click would stop it instead.</summary>
    public void SetDotHover(int dotId, bool stops)
    {
        _dotHover = dotId;
        _dotHoverStops = stops;
    }

    /// <summary>A dot's radius in map units at this zoom.</summary>
    public float DotRadius(float zoom) =>
        Math.Max(DotSpacingShare * (_game?.Rules.DotSpacing ?? DotRules.Spacing), DotMinPixels / zoom);

    /// <summary>Where this dot was last drawn (map units), or the middle of its tile.</summary>
    public Vector2 DotDrawnAt(Dot dot) =>
        _drawnAt.TryGetValue(dot.Id, out var at) ? at : _game is null ? Vector2.Zero : TileCentre(dot.Tile, _game.Map.Width);

    private Font DotFont()
    {
        if (_dotFont is not null)
            return _dotFont;
        if (ResourceLoader.Exists(DotFontFile) && GD.Load<FontFile>(DotFontFile) is { } file)
        {
            var semiBold = new FontVariation { BaseFont = file };
            semiBold.VariationOpentype = new Godot.Collections.Dictionary { { TextServerManager.GetPrimaryInterface().NameToTag("wght"), 600 } };
            _dotFont = semiBold;
        }
        return _dotFont ??= _font;
    }

    private void DrawDots(float zoom, int w)
    {
        if (_game is null)
            return;
        float r = DotRadius(zoom), px = 1 / zoom;
        if (_game.Tick != _glideTick || _game != _glideGame)
        {
            // A new tick: glide from where each dot was last drawn to where it is now.
            bool sameGame = _game == _glideGame;
            _glideTick = _game.Tick;
            _glideGame = _game;
            _glide.Clear();
            foreach (var dot in _game.Dots)
            {
                var now = TileCentre(dot.Tile, w);
                if (dot.IsWalking)
                    now = now.Lerp(TileCentre(dot.NextTile, w), (float)dot.StepFraction(w));
                _glide[dot.Id] = (sameGame && _drawnAt.TryGetValue(dot.Id, out var was) ? was : now, now);
            }
            _drawnAt.Clear();
        }
        var orders = DotOrders;
        foreach (var (from, to) in orders.Lines)
            DrawDashedLine(from, to, orders.GhostsOk ? new Color(1, 1, 1, 0.7f) : BadGhost, 2 * px, 6 * px, true);
        foreach (var dot in _game.Dots)
        {
            if (!_glide.TryGetValue(dot.Id, out var g))
                continue; // (bought this tick: shown from the next)
            var at = g.From.Lerp(g.To, Math.Clamp(_tickFraction, 0, 1));
            _drawnAt[dot.Id] = at;
            if (!OnScreen(at, r * 3))
                continue;
            bool pushing = !dot.IsWalking && _game.PushOf(dot) is not null;
            DrawDot(at, r, zoom, ColorOf(dot.Owner.Id), dot.Strength, pushing);
            if (orders.Selected.Contains(dot.Id))
                DrawArc(at, r + 3.5f * px, 0, Mathf.Tau, 32, SelectedRing, 2.5f * px, true);
            if (dot.Owner == _me && dot.Stamina < DotRules.MaxStamina)
            {
                float left = (float)dot.Stamina / DotRules.MaxStamina;
                DrawArc(at, r + 6.5f * px, -Mathf.Pi / 2, -Mathf.Pi / 2 + Mathf.Tau * left, 24, StaminaArc, 2 * px, true);
            }
            if (dot.Id == orders.SellingDot)
                DrawArc(at, r + 4 * px, -Mathf.Pi / 2, -Mathf.Pi / 2 + Mathf.Tau * orders.SellDone, 32, SellRing, 4 * px, true);
            if (dot.Id == _dotHover)
                DrawArc(at, r * 1.45f + 4 * px, 0, Mathf.Tau, 32, _dotHoverStops ? StopRing : SendRing, 2.5f * px, true);
        }
        var mine = _me is null ? Colors.Gray : ColorOf(_me.Id);
        foreach (var at in orders.Ghosts)
        {
            if (!OnScreen(at, r * 2))
                continue;
            DrawCircle(at, r, new Color(mine, 0.45f));
            DrawArc(at, r, 0, Mathf.Tau, 32, orders.GhostsOk ? new Color(1, 1, 1, 0.85f) : BadGhost, 2 * px, true);
        }
        if (orders.Box is { } box)
        {
            DrawRect(box, new Color(1f, 0.88f, 0.3f, 0.12f));
            DrawRect(box, SelectedRing, false, 1.5f * px);
        }
        if (orders.Handles is { } handles)
            for (int i = 0; i < handles.Length; i++)
                DrawHandle(handles[i], r, px, middle: i == 2, hot: i == orders.HotHandle);
        if (orders.Tip is { } tip && orders.TipDot >= 0 && _drawnAt.TryGetValue(orders.TipDot, out var over))
            DrawMapText(over + new Vector2(r + 10 * px, -4 * px), tip, zoom, centred: false); // beside the dot, clear of its neighbours on the line
        if (orders.Label is { } label)
            DrawMapText(orders.LabelAt + new Vector2(16, 26) * px, label, zoom, centred: false);
    }

    /// <summary>One dot: dark outline, a darker edge bottom right, the lit body, a highlight, and its "t".</summary>
    private void DrawDot(Vector2 at, float r, float zoom, Color owner, int strength, bool pushing)
    {
        float px = 1 / zoom;
        var fill = owner.Darkened(0.17f * (strength - 1));
        DrawCircle(at, r + 1.5f * px, pushing ? PushRim : fill.Darkened(0.65f));
        DrawCircle(at, r, fill.Darkened(0.32f));
        DrawCircle(at - new Vector2(0.07f, 0.07f) * r, r * 0.87f, fill);
        DrawArc(at - new Vector2(0.07f, 0.07f) * r, r * 0.8f, Mathf.Pi * 1.05f, Mathf.Pi * 1.6f, 12, fill.Lightened(0.35f), r * 0.1f, true);
        DrawCircle(at - new Vector2(0.34f, 0.36f) * r, r * 0.17f, new Color(1, 1, 1, 0.3f));
        float pixels = r * zoom;
        if (pixels < DotLetterPixels)
            return;
        var font = DotFont();
        int size = Math.Max(8, (int)MathF.Round(pixels * 1.5f));
        float width = font.GetStringSize("t", HorizontalAlignment.Left, -1, size).X;
        var letter = fill.Luminance > 0.62f ? new Color(0.08f, 0.08f, 0.1f, 0.9f) : new Color(1f, 1f, 0.97f, 0.95f);
        DrawSetTransform(at, 0, new Vector2(px, px));
        DrawString(font, new Vector2(-width / 2, size * 0.3f), "t", HorizontalAlignment.Left, -1, size, letter);
        DrawSetTransform(Vector2.Zero, 0, Vector2.One);
    }

    /// <summary>A group handle: a white square at each end (drag to spread or cramp), a white bar in the middle (drag to slide); yellow under the mouse.</summary>
    private void DrawHandle(Vector2 at, float r, float px, bool middle, bool hot)
    {
        var fill = hot ? SelectedRing : new Color(1, 1, 1, 0.95f);
        var edge = new Color(0.05f, 0.05f, 0.06f, 0.9f);
        var half = middle ? new Vector2(r * 1.05f, r * 0.48f) : new Vector2(r * 0.55f, r * 0.55f);
        half = half.Max(new Vector2(middle ? 7 : 4, 4) * px);
        var rect = new Rect2(at - half, half * 2);
        DrawRect(rect.Grow(1.5f * px), edge);
        DrawRect(rect, fill);
        if (!middle)
            return;
        // Two small arrows pointing apart: it slides.
        float s = half.Y * 0.7f;
        DrawColoredPolygon(new[] { at + new Vector2(-half.X * 0.85f, 0), at + new Vector2(-half.X * 0.85f + s, -s), at + new Vector2(-half.X * 0.85f + s, s) }, edge);
        DrawColoredPolygon(new[] { at + new Vector2(half.X * 0.85f, 0), at + new Vector2(half.X * 0.85f - s, -s), at + new Vector2(half.X * 0.85f - s, s) }, edge);
    }

    /// <summary>A few words on the map at screen size, outlined (centred over the point, or starting at it).</summary>
    private void DrawMapText(Vector2 at, string text, float zoom, bool centred)
    {
        const int Size = 15;
        string[] lines = text.Split('\n');
        float px = 1 / zoom, y = centred ? -(lines.Length - 1) * (Size + 3) : 0; // centred text sits above the point
        DrawSetTransform(at, 0, new Vector2(px, px));
        foreach (string line in lines)
        {
            float width = _font.GetStringSize(line, HorizontalAlignment.Left, -1, Size).X;
            var origin = new Vector2(centred ? -MathF.Round(width / 2) : 0, y);
            DrawStringOutline(_font, origin, line, HorizontalAlignment.Left, -1, Size, 4, new Color(0, 0, 0, 0.75f));
            DrawString(_font, origin, line, HorizontalAlignment.Left, -1, Size, Colors.White);
            y += Size + 3;
        }
        DrawSetTransform(Vector2.Zero, 0, Vector2.One);
    }
}
