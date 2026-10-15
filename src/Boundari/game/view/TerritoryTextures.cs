using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Boundari.Map;
using Godot;

namespace Boundari.View;

/// <summary>
/// GPU copy of who owns each tile, plus what's needed to animate captures smoothly:
/// the previous owner and the exact moment (in ticks, with a fraction) each tile
/// changed hands. The map is split into 256x256-tile chunks (texture-array layers)
/// so only chunks that actually changed are re-uploaded.
/// </summary>
public sealed class TerritoryTextures
{
    public const int ChunkSize = 128; // small, so a change re-uploads little
    public const ushort Water = ushort.MaxValue;
    public const ushort Fallout = ushort.MaxValue - 1; // unowned land poisoned by a bomb

    private const float LongAgo = -1e6f;

    private readonly GameMap _map;
    private readonly int _chunksX;
    private readonly int _layers;

    // Per chunk: RGBA8 (RG = owner, BA = previous owner) and R32F capture time.
    private readonly byte[][] _ownerBytes;
    private readonly byte[][] _timeBytes;
    private readonly Image[] _ownerImages;
    private readonly Image[] _timeImages;
    private readonly bool[] _dirty;
    private readonly ushort[] _shown; // owner currently drawn for each tile

    public readonly Texture2DArray OwnerArray = new();
    public readonly Texture2DArray TimeArray = new();
    public int ChunksX => _chunksX;

    public TerritoryTextures(GameMap map)
    {
        _map = map;
        _chunksX = (map.Width + ChunkSize - 1) / ChunkSize;
        int chunksY = (map.Height + ChunkSize - 1) / ChunkSize;
        _layers = _chunksX * chunksY;

        _ownerBytes = new byte[_layers][];
        _timeBytes = new byte[_layers][];
        _ownerImages = new Image[_layers];
        _timeImages = new Image[_layers];
        _dirty = new bool[_layers];
        _shown = new ushort[map.TileCount];

        var owners = new Godot.Collections.Array<Image>();
        var times = new Godot.Collections.Array<Image>();
        for (int l = 0; l < _layers; l++)
        {
            _ownerBytes[l] = new byte[ChunkSize * ChunkSize * 4];
            _timeBytes[l] = new byte[ChunkSize * ChunkSize * 4];
            MemoryMarshal.Cast<byte, float>(_timeBytes[l]).Fill(LongAgo);
            _ownerImages[l] = Image.CreateFromData(ChunkSize, ChunkSize, false, Image.Format.Rgba8, _ownerBytes[l]);
            _timeImages[l] = Image.CreateFromData(ChunkSize, ChunkSize, false, Image.Format.Rf, _timeBytes[l]);
            owners.Add(_ownerImages[l]);
            times.Add(_timeImages[l]);
        }
        OwnerArray.CreateFromImages(owners);
        TimeArray.CreateFromImages(times);
    }

    /// <summary>Shows exactly these owners, with no animation (start of a match).</summary>
    public void SetAll(ushort[]? owners, bool[]? fallout = null)
    {
        for (int i = 0; i < _map.TileCount; i++)
        {
            ushort id = Shown(i, owners, fallout);
            _shown[i] = id;
            Write(i, id, id, LongAgo);
        }
        Array.Fill(_dirty, true);
        Flush(all: true);
    }

    /// <summary>
    /// Records the tiles that changed during one tick. They're spread across that tick
    /// in the order they were taken, so the shader can sweep fronts forward smoothly.
    /// </summary>
    public void RecordTick(ushort[] owners, bool[]? fallout, List<int> changed, int tick)
    {
        int n = changed.Count;
        for (int k = 0; k < n; k++)
        {
            int i = changed[k];
            ushort now = Shown(i, owners, fallout);
            ushort before = _shown[i];
            if (now == before)
                continue;
            _shown[i] = now;
            Write(i, now, before, tick + (k + 1f) / n);
        }
    }

    // Chunks uploaded per frame at most; the rest go on the next frames (territory is drawn a
    // tick behind, which is several frames, so this never shows), keeping big fights from
    // turning into one slow frame.
    private const int MaxUploadsPerFlush = 24;
    private int _nextFlush;

    /// <summary>Uploads chunks that changed since they were last uploaded (a limited number per call).</summary>
    public void Flush(bool all = false)
    {
        int uploads = 0, start = _nextFlush;
        for (int k = 0; k < _layers && (all || uploads < MaxUploadsPerFlush); k++)
        {
            int l = (start + k) % _layers;
            if (!_dirty[l])
                continue;
            _dirty[l] = false;
            uploads++;
            _nextFlush = (l + 1) % _layers;
            _ownerImages[l].SetData(ChunkSize, ChunkSize, false, Image.Format.Rgba8, _ownerBytes[l]);
            _timeImages[l].SetData(ChunkSize, ChunkSize, false, Image.Format.Rf, _timeBytes[l]);
            OwnerArray.UpdateLayer(_ownerImages[l], l);
            TimeArray.UpdateLayer(_timeImages[l], l);
        }
    }

    private ushort Shown(int i, ushort[]? owners, bool[]? fallout)
    {
        if (_map.IsWater(i))
            return Water;
        ushort owner = owners?[i] ?? 0;
        return owner == 0 && fallout is not null && fallout[i] ? Fallout : owner;
    }

    private void Write(int tile, ushort owner, ushort previous, float time)
    {
        int x = tile % _map.Width, y = tile / _map.Width;
        int layer = (y / ChunkSize) * _chunksX + x / ChunkSize;
        int local = (y % ChunkSize) * ChunkSize + x % ChunkSize;
        var bytes = _ownerBytes[layer];
        bytes[local * 4] = (byte)(owner & 0xFF);
        bytes[local * 4 + 1] = (byte)(owner >> 8);
        bytes[local * 4 + 2] = (byte)(previous & 0xFF);
        bytes[local * 4 + 3] = (byte)(previous >> 8);
        MemoryMarshal.Cast<byte, float>(_timeBytes[layer])[local] = time;
        _dirty[layer] = true;
    }
}
