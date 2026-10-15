using System;
using System.Threading.Tasks;
using Boundari.Map;
using Boundari.Sim;
using Godot;

namespace Boundari.View;

/// <summary>
/// How far each spot is inside its owner's territory: tiles to the nearest land of anyone
/// else (coasts don't count), on a grid of 2x2-tile cells. Worked out on a background thread
/// a few times a second, only while thick borders use it, and uploaded as a texture the map
/// shader reads with smooth filtering.
/// </summary>
public sealed class TerritoryDepth
{
    public const int CellTiles = 2;
    private const double RefreshSeconds = 0.25;

    private readonly GameMap _map;
    private readonly int _w, _h;
    private readonly int[] _owner;
    private readonly int[] _dist;     // chamfer distance: 3 per cell step straight, 4 diagonal
    private readonly byte[] _depthBytes;
    private Task? _task;
    private double _since = RefreshSeconds;

    public readonly ImageTexture DepthTexture;

    /// <summary>The field's size in tiles (a little over the map's, to whole cells).</summary>
    public Vector2 SizeInTiles => new(_w * CellTiles, _h * CellTiles);

    public TerritoryDepth(GameMap map)
    {
        _map = map;
        _w = (map.Width + CellTiles - 1) / CellTiles;
        _h = (map.Height + CellTiles - 1) / CellTiles;
        _owner = new int[_w * _h];
        _dist = new int[_w * _h];
        _depthBytes = new byte[_w * _h * 2];
        for (int i = 0; i < _w * _h; i++) // "very deep" until the first real calculation lands
            System.Buffers.Binary.BinaryPrimitives.WriteHalfLittleEndian(_depthBytes.AsSpan(i * 2, 2), (Half)10_000f);
        DepthTexture = ImageTexture.CreateFromImage(Image.CreateFromData(_w, _h, false, Image.Format.Rh, _depthBytes));
    }

    /// <summary>Call every frame while needed: starts a refresh when due and uploads finished ones.</summary>
    public void Update(Game game, double delta)
    {
        if (_task is { IsCompleted: true })
        {
            _task = null;
            DepthTexture.Update(Image.CreateFromData(_w, _h, false, Image.Format.Rh, _depthBytes));
        }
        if ((_since += delta) < RefreshSeconds || _task is not null)
            return;
        _since = 0;
        // Reads the owner grid while the game may be stepping; a stale tile here and there
        // only nudges a glow for a quarter of a second.
        _task = Task.Run(() => Compute(game.Owner));
    }

    private void Compute(ushort[] owners)
    {
        int w = _w, h = _h, mw = _map.Width;
        for (int cy = 0; cy < h; cy++)
            for (int cx = 0; cx < w; cx++)
            {
                int tx = Math.Min(cx * CellTiles + CellTiles / 2, _map.Width - 1);
                int ty = Math.Min(cy * CellTiles + CellTiles / 2, _map.Height - 1);
                int tile = ty * mw + tx;
                _owner[cy * w + cx] = _map.IsLand(tile) ? owners[tile] : -1;
            }

        // Cells next to someone else's land start at one step; the rest are found by two
        // chamfer passes that only travel through the same owner's cells.
        const int Inf = int.MaxValue / 2;
        for (int i = 0; i < _dist.Length; i++)
        {
            int o = _owner[i];
            if (o <= 0)
            {
                _dist[i] = 0;
                continue;
            }
            int x = i % w, y = i / w;
            bool edge = Foreign(x - 1, y, o) || Foreign(x + 1, y, o) || Foreign(x, y - 1, o) || Foreign(x, y + 1, o);
            _dist[i] = edge ? 3 : Inf;
        }
        for (int y = 1; y < h - 1; y++)
            for (int x = 1; x < w - 1; x++)
                Relax(y * w + x, -1, -w, -w - 1, -w + 1);
        for (int y = h - 2; y > 0; y--)
            for (int x = w - 2; x > 0; x--)
                Relax(y * w + x, 1, w, w + 1, w - 1);

        var span = _depthBytes.AsSpan();
        for (int i = 0; i < _dist.Length; i++)
        {
            int o = _owner[i];
            // Unreached cells (an island all of one owner) count as very deep.
            float tiles = o <= 0 ? 0 : Math.Min(_dist[i], 3 * 2000) / 3f * CellTiles - CellTiles / 2f;
            tiles = Math.Max(tiles, 0);
            System.Buffers.Binary.BinaryPrimitives.WriteHalfLittleEndian(span.Slice(i * 2, 2), (Half)tiles);
        }

        bool Foreign(int x, int y, int o)
        {
            if (x < 0 || y < 0 || x >= w || y >= h)
                return false;
            int n = _owner[y * w + x];
            return n >= 0 && n != o; // someone else's land, or unowned land (not water)
        }

        void Relax(int i, int a, int b, int c, int d)
        {
            int o = _owner[i];
            if (o <= 0)
                return;
            int v = _dist[i];
            if (_owner[i + a] == o) v = Math.Min(v, _dist[i + a] + 3);
            if (_owner[i + b] == o) v = Math.Min(v, _dist[i + b] + 3);
            if (_owner[i + c] == o) v = Math.Min(v, _dist[i + c] + 4);
            if (_owner[i + d] == o) v = Math.Min(v, _dist[i + d] + 4);
            _dist[i] = v;
        }
    }
}
