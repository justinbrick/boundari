using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Boundari.Map;
using Godot;

namespace Boundari.View;

/// <summary>
/// GPU copy of who owns each tile, plus what's needed to animate captures smoothly:
/// the previous owner, the exact moment (in ticks, with a fraction) each tile changed
/// hands, and how long it takes to fade over. The map is split into 256x256-tile chunks (texture-array layers)
/// so only chunks that actually changed are re-uploaded.
/// </summary>
public sealed class TerritoryTextures
{
    public const int ChunkSize = 128; // small, so a change re-uploads little
    // Owner ids are packed in 12 bits (two per tile plus the fade length fit in RGBA8).
    public const ushort Water = 4095;
    public const ushort Fallout = 4094; // unowned land poisoned by a bomb
    public const int MaxPlayerIds = Fallout;

    private const float LongAgo = -1e6f;

    // How long (ticks) a captured tile takes to fade to its new owner. A front that takes a
    // tile every tick, or one every few ticks, fades each tile over the whole gap since its
    // previous capture, so the next tile starts as the last one finishes and the front moves
    // steadily instead of popping forward then waiting. Fronts that haven't moved recently
    // (or ever) use the default.
    private const float DefaultFade = 0.6f;
    private const int LongestFadeTicks = 15;
    private const float FadeUnits = 16; // stored in 1/16 ticks (one byte: up to ~16 ticks)

    // Last tick each front (new owner, previous owner) took a tile.
    private readonly Dictionary<int, int> _frontLastTick = new();

    private readonly GameMap _map;
    private readonly int _chunksX;
    private readonly int _layers;

    // Per chunk: RGBA8 (owner and previous owner in 12 bits each, then the fade length)
    // and R32F capture time.
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
            Write(i, id, id, LongAgo, DefaultFade);
        }
        _frontLastTick.Clear();
        Array.Fill(_dirty, true);
        Flush(all: true);
    }

    /// <summary>
    /// Records the tiles that changed during one tick. Each front's tiles are spread across
    /// the tick in the order they were taken (on their own, so a slow front isn't early one
    /// tick and late the next because of other fronts), and fade over that front's pace (see
    /// <see cref="DefaultFade"/>), so the shader sweeps fronts forward smoothly.
    /// </summary>
    public void RecordTick(ushort[] owners, bool[]? fallout, List<int> changed, int tick)
    {
        // First pass: how many tiles each front took this tick, and its pace.
        foreach (int i in changed)
        {
            ushort now = Shown(i, owners, fallout);
            if (now == _shown[i])
                continue;
            ref var f = ref CollectionsMarshal.GetValueRefOrAddDefault(_thisTick, now << 16 | _shown[i], out bool known);
            if (!known)
            {
                f.Fade = DefaultFade;
                ref int last = ref CollectionsMarshal.GetValueRefOrAddDefault(_frontLastTick, now << 16 | _shown[i], out bool seen);
                if (seen && last < tick && tick - last <= LongestFadeTicks)
                    f.Fade = Math.Max(DefaultFade, tick - last);
                last = tick;
            }
            f.Count++;
        }
        // Second pass: write each tile with its place in its front's run.
        foreach (int i in changed)
        {
            ushort now = Shown(i, owners, fallout);
            ushort before = _shown[i];
            if (now == before)
                continue;
            _shown[i] = now;
            ref var f = ref CollectionsMarshal.GetValueRefOrNullRef(_thisTick, now << 16 | before);
            f.Taken++;
            Write(i, now, before, tick + (float)f.Taken / f.Count, f.Fade);
        }
        _thisTick.Clear();
    }

    private struct FrontThisTick
    {
        public int Count, Taken;
        public float Fade;
    }

    private readonly Dictionary<int, FrontThisTick> _thisTick = new();

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

    /// <summary>Ids past the 12-bit range (never expected) are drawn as unclaimed.</summary>
    private static ushort Packable(ushort id) => id < MaxPlayerIds || id == Water || id == Fallout ? id : (ushort)0;

    private ushort Shown(int i, ushort[]? owners, bool[]? fallout)
    {
        if (_map.IsWater(i))
            return Water;
        ushort owner = owners?[i] ?? 0;
        return owner == 0 && fallout is not null && fallout[i] ? Fallout : owner;
    }

    private void Write(int tile, ushort owner, ushort previous, float time, float fade)
    {
        owner = Packable(owner);
        previous = Packable(previous);
        int x = tile % _map.Width, y = tile / _map.Width;
        int layer = (y / ChunkSize) * _chunksX + x / ChunkSize;
        int local = (y % ChunkSize) * ChunkSize + x % ChunkSize;
        var bytes = _ownerBytes[layer];
        bytes[local * 4] = (byte)(owner & 0xFF);
        bytes[local * 4 + 1] = (byte)(owner >> 8 | (previous & 0xF) << 4);
        bytes[local * 4 + 2] = (byte)(previous >> 4);
        bytes[local * 4 + 3] = (byte)Math.Clamp((int)MathF.Round(fade * FadeUnits), 1, 255);
        MemoryMarshal.Cast<byte, float>(_timeBytes[layer])[local] = time;
        _dirty[layer] = true;
    }
}
