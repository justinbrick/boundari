using System;
using System.Collections.Generic;
using Boundari.Sim;
using Godot;

namespace Boundari.View;

/// <summary>
/// Draws each player's flag, name and troop count on the map, at the point deepest
/// inside their territory, sized to fit. Positions are recomputed a couple of times a
/// second and eased towards, so labels glide as territories change.
/// </summary>
public partial class TerritoryLabels : Node2D
{
    // Label placement runs on a coarse grid (cells of this many tiles) to stay cheap.
    private const int CellTiles = 4;
    private const double RefreshSeconds = 0.5;
    private const float MinScreenFontPixels = 7;   // smaller than this isn't drawn
    private const float GlideRate = 4f;             // how quickly labels move to new spots

    private static readonly Color TextColor = new(0.16f, 0.16f, 0.16f, 0.92f);
    private static readonly Color OutlineColor = new(1, 1, 1, 0.28f);

    private sealed class Label
    {
        public Vector2 Target, Position;
        public float TargetRadius, Radius;
        public bool Placed;
    }

    private readonly Dictionary<ushort, Label> _labels = new();
    private readonly Dictionary<string, Texture2D?> _flags = new();
    private Game? _game;
    private double _sinceRefresh = RefreshSeconds;
    private Font _font = null!;

    private int[] _cellOwner = Array.Empty<int>();
    private int[] _dist = Array.Empty<int>();

    public override void _Ready()
    {
        // A distance-field copy of the font stays sharp at any zoom level.
        var file = (FontFile)GD.Load<FontFile>("res://fonts/Exo2-VariableFont_wght.ttf").Duplicate();
        file.MultichannelSignedDistanceField = true;
        _font = new FontVariation
        {
            BaseFont = file,
            VariationOpentype = new Godot.Collections.Dictionary { { "wght", 700 } },
        };
    }

    public void SetGame(Game game)
    {
        _game = game;
        _labels.Clear();
        int w = (game.Map.Width + CellTiles - 1) / CellTiles, h = (game.Map.Height + CellTiles - 1) / CellTiles;
        _cellOwner = new int[w * h];
        _dist = new int[w * h];
        _sinceRefresh = RefreshSeconds;
    }

    public override void _Process(double delta)
    {
        if (_game is null)
            return;
        if ((_sinceRefresh += delta) >= RefreshSeconds)
        {
            _sinceRefresh = 0;
            Place();
        }

        float t = 1 - MathF.Exp(-GlideRate * (float)delta);
        foreach (var label in _labels.Values)
        {
            label.Position = label.Position.Lerp(label.Target, t);
            label.Radius += (label.TargetRadius - label.Radius) * t;
        }
        QueueRedraw();
    }

    /// <summary>
    /// Finds each territory's "pole of inaccessibility": the cell furthest from its own
    /// border, measured with a two-pass chamfer distance that only travels through the
    /// same owner's land. That's where the label fits best and reads most naturally.
    /// </summary>
    private void Place()
    {
        var game = _game!;
        var map = game.Map;
        int w = (map.Width + CellTiles - 1) / CellTiles, h = (map.Height + CellTiles - 1) / CellTiles;
        for (int cy = 0; cy < h; cy++)
            for (int cx = 0; cx < w; cx++)
            {
                int tx = Math.Min(cx * CellTiles + CellTiles / 2, map.Width - 1);
                int ty = Math.Min(cy * CellTiles + CellTiles / 2, map.Height - 1);
                int tile = map.Index(tx, ty);
                _cellOwner[cy * w + cx] = map.IsLand(tile) ? game.Owner[tile] : -1;
            }

        const int Inf = int.MaxValue / 2;
        for (int i = 0; i < _dist.Length; i++)
        {
            int o = _cellOwner[i];
            if (o <= 0) { _dist[i] = 0; continue; }
            int x = i % w, y = i / w;
            bool edge = x == 0 || y == 0 || x == w - 1 || y == h - 1
                || _cellOwner[i - 1] != o || _cellOwner[i + 1] != o || _cellOwner[i - w] != o || _cellOwner[i + w] != o;
            _dist[i] = edge ? 3 : Inf;
        }
        // Forward and backward chamfer passes (3 straight, 4 diagonal ~ 3x Euclidean).
        for (int y = 1; y < h - 1; y++)
            for (int x = 1; x < w - 1; x++)
                Relax(y * w + x, -1, -w, -w - 1, -w + 1);
        for (int y = h - 2; y > 0; y--)
            for (int x = w - 2; x > 0; x--)
                Relax(y * w + x, 1, w, w + 1, w - 1);

        var best = new Dictionary<ushort, (int Cell, int Dist)>();
        for (int i = 0; i < _dist.Length; i++)
        {
            int o = _cellOwner[i];
            if (o <= 0)
                continue;
            if (!best.TryGetValue((ushort)o, out var b) || _dist[i] > b.Dist)
                best[(ushort)o] = (i, _dist[i]);
        }

        foreach (var key in new List<ushort>(_labels.Keys))
            if (!best.ContainsKey(key))
                _labels.Remove(key);
        foreach (var (id, (cell, dist)) in best)
        {
            var target = new Vector2((cell % w + 0.5f) * CellTiles, (cell / w + 0.5f) * CellTiles);
            float radius = dist / 3f * CellTiles;
            if (!_labels.TryGetValue(id, out var label))
                _labels[id] = label = new Label();
            label.Target = target;
            label.TargetRadius = radius;
            if (!label.Placed)
            {
                label.Position = target;
                label.Radius = radius;
                label.Placed = true;
            }
        }

        void Relax(int i, int a, int b, int c, int d)
        {
            int o = _cellOwner[i];
            if (o <= 0)
                return;
            int v = _dist[i];
            if (_cellOwner[i + a] == o) v = Math.Min(v, _dist[i + a] + 3);
            if (_cellOwner[i + b] == o) v = Math.Min(v, _dist[i + b] + 3);
            if (_cellOwner[i + c] == o) v = Math.Min(v, _dist[i + c] + 4);
            if (_cellOwner[i + d] == o) v = Math.Min(v, _dist[i + d] + 4);
            _dist[i] = v;
        }
    }

    public override void _Draw()
    {
        if (_game is null)
            return;
        float zoom = GetViewport().GetCamera2D()?.Zoom.X ?? 1;

        foreach (var (id, label) in _labels)
        {
            var player = _game.GetPlayer(id);
            if (player is null || label.Radius < 1)
                continue;

            // Size to the room inside the territory, then shrink if the name is too wide.
            int size = Math.Max(1, (int)(label.Radius * 0.55f));
            var flag = FlagTexture(player.Flag);
            float nameWidth = _font.GetStringSize(player.Name, HorizontalAlignment.Left, -1, size).X;
            float flagWidth = flag is null ? 0 : size * 0.95f;
            float gap = flag is null ? 0 : size * 0.25f;
            float total = flagWidth + gap + nameWidth;
            if (total > label.Radius * 1.8f)
            {
                size = Math.Max(1, (int)(size * label.Radius * 1.8f / total));
                nameWidth = _font.GetStringSize(player.Name, HorizontalAlignment.Left, -1, size).X;
                flagWidth = flag is null ? 0 : size * 0.95f;
                gap = flag is null ? 0 : size * 0.25f;
                total = flagWidth + gap + nameWidth;
            }
            if (size * zoom < MinScreenFontPixels)
                continue;

            float ascent = _font.GetAscent(size);
            var nameBaseline = new Vector2(label.Position.X - total / 2 + flagWidth + gap, label.Position.Y);
            if (flag is not null)
            {
                float flagHeight = flagWidth * 0.75f;
                var rect = new Rect2(label.Position.X - total / 2, nameBaseline.Y - ascent * 0.5f - flagHeight * 0.55f, flagWidth, flagHeight);
                DrawTextureRect(flag, rect, false);
            }
            int outline = Math.Max(1, size / 10);
            DrawStringOutline(_font, nameBaseline, player.Name, HorizontalAlignment.Left, -1, size, outline, OutlineColor);
            DrawString(_font, nameBaseline, player.Name, HorizontalAlignment.Left, -1, size, TextColor);

            int troopSize = Math.Max(1, (int)(size * 0.7f));
            string troops = GameHud.FormatCount(player.Troops);
            float troopWidth = _font.GetStringSize(troops, HorizontalAlignment.Left, -1, troopSize).X;
            var troopBaseline = new Vector2(label.Position.X - troopWidth / 2, label.Position.Y + troopSize * 1.05f);
            DrawStringOutline(_font, troopBaseline, troops, HorizontalAlignment.Left, -1, troopSize, outline, OutlineColor);
            DrawString(_font, troopBaseline, troops, HorizontalAlignment.Left, -1, troopSize, TextColor);
        }
    }

    private Texture2D? FlagTexture(string code)
    {
        if (string.IsNullOrEmpty(code))
            return null;
        if (!_flags.TryGetValue(code, out var texture))
        {
            string path = $"res://ui/flags - large/{code}.png";
            texture = ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
            _flags[code] = texture;
        }
        return texture;
    }
}
