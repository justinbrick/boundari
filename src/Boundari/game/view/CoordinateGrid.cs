using System;
using Godot;

namespace Boundari.View;

/// <summary>
/// The coordinate grid, the way OpenFront draws it: the map cut into about ten cells along
/// its short side (at most 50 columns), thin white lines along the cells' top and left
/// edges, and a label in each cell's top-left corner, row letter then column number ("B7").
/// Lines and text keep the same size on screen at any zoom; labels hide once the cells get
/// too small to hold them.
/// </summary>
public partial class CoordinateGrid : Node2D
{
    private const int BaseCellCount = 10;
    private const int MaxColumns = 50;
    private const int MinRows = 2;
    private const float Opacity = 0.5f;
    private const float LineScreenPixels = 1.25f;
    private const float FontScreenPixels = 24;     // grows a little with zoom, see LabelPixels
    private const float LabelPadPixels = 8;
    private const float MinCellScreenPixels = 60;  // no labels on cells smaller than this

    private int _mapW, _mapH;
    private float _cell;
    private Font _font = null!;
    private Vector2 _drawnZoom, _drawnOrigin;

    public override void _Ready()
    {
        _font = new SystemFont
        {
            FontNames = new[] { "Consolas", "Courier New", "DejaVu Sans Mono", "monospace" },
            FontWeight = 700,
        };
    }

    /// <summary>Sizes the grid to a newly loaded map.</summary>
    public void SetMap(int width, int height)
    {
        _mapW = width;
        _mapH = height;
        _cell = CellSize(width, height);
        QueueRedraw();
    }

    /// <summary>Same cell size as OpenFront: ~10 cells on the short side, at most 50 columns.</summary>
    private static float CellSize(int mapW, int mapH)
    {
        float raw = Math.Min(mapW, mapH) / (float)BaseCellCount;
        int rows = Math.Max(1, (int)MathF.Round(mapH / raw));
        int cols = Math.Max(1, (int)MathF.Round(mapW / raw));
        if (cols > MaxColumns)
        {
            int maxRows = MaxColumns * mapH / mapW;
            rows = Math.Max(MinRows, Math.Min(rows, maxRows));
            cols = MaxColumns;
        }
        return Math.Min(mapW / (float)cols, mapH / (float)rows);
    }

    public override void _Process(double delta)
    {
        // Line widths and text sizes are in screen pixels, so redraw when the camera moves.
        var transform = GetCanvasTransform();
        if (transform.X != _drawnZoom || transform.Origin != _drawnOrigin)
        {
            _drawnZoom = transform.X;
            _drawnOrigin = transform.Origin;
            QueueRedraw();
        }
    }

    public override void _Draw()
    {
        if (_cell <= 0)
            return;
        float zoom = Math.Max(GetCanvasTransform().X.X, 1e-4f); // screen pixels per tile
        float px = 1 / zoom;
        var view = GetCanvasTransform().AffineInverse() * GetViewportRect();
        var lineColor = new Color(1, 1, 1, Opacity);

        int col0 = Math.Max(0, (int)(view.Position.X / _cell)), col1 = (int)Math.Min(_mapW / _cell, view.End.X / _cell);
        int row0 = Math.Max(0, (int)(view.Position.Y / _cell)), row1 = (int)Math.Min(_mapH / _cell, view.End.Y / _cell);
        float top = Math.Max(0, view.Position.Y), bottom = Math.Min(_mapH, view.End.Y);
        float left = Math.Max(0, view.Position.X), right = Math.Min(_mapW, view.End.X);
        float width = LineScreenPixels * px;

        // Each cell's top and left edges (OpenFront leaves the map's right and bottom edges bare).
        for (int c = col0; c <= col1; c++)
        {
            float x = c * _cell;
            if (x < _mapW)
                DrawRect(new Rect2(x, top, width, bottom - top), lineColor);
        }
        for (int r = row0; r <= row1; r++)
        {
            float y = r * _cell;
            if (y < _mapH)
                DrawRect(new Rect2(left, y, right - left, width), lineColor);
        }

        if (_cell * zoom < MinCellScreenPixels)
            return;
        float fontPixels = Math.Clamp(FontScreenPixels + (zoom - 1) * 1.2f, FontScreenPixels * 0.9f, FontScreenPixels * 1.6f);
        // OpenFront's glyphs fill about 28/36 of each label row, with a 4/36 black outline.
        int textPixels = Math.Max(6, (int)MathF.Round(fontPixels * 28 / 36));
        int outline = Math.Max(1, (int)MathF.Round(fontPixels * 4 / 36));
        float ascent = _font.GetAscent(textPixels);
        float rowGap = (fontPixels - _font.GetHeight(textPixels)) / 2; // centre the text in its row
        var textColor = new Color(1, 1, 1, Opacity);
        var outlineColor = new Color(0, 0, 0, Opacity);
        for (int r = row0; r <= row1; r++)
            for (int c = col0; c <= col1; c++)
            {
                if (c * _cell >= _mapW || r * _cell >= _mapH)
                    continue;
                var corner = new Vector2(c * _cell, r * _cell);
                DrawSetTransform(corner, 0, new Vector2(px, px)); // draw in screen pixels from the corner
                var at = new Vector2(LabelPadPixels, LabelPadPixels + rowGap + ascent);
                string label = RowName(r) + (c + 1);
                DrawStringOutline(_font, at, label, HorizontalAlignment.Left, -1, textPixels, outline, outlineColor);
                DrawString(_font, at, label, HorizontalAlignment.Left, -1, textPixels, textColor);
            }
        DrawSetTransform(Vector2.Zero, 0, Vector2.One);
    }

    /// <summary>A, B, ... Z, then AA, AB, ...</summary>
    private static string RowName(int row) =>
        row < 26 ? ((char)('A' + row)).ToString() : $"{(char)('A' + row / 26 - 1)}{(char)('A' + row % 26)}";
}
