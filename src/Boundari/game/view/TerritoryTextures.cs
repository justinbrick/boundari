using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Boundari.Map;
using Godot;

namespace Boundari.View;

/// <summary>
/// GPU copy of who owns each tile, plus what's needed to animate captures smoothly:
/// the previous owner, the exact moment (in ticks, with a fraction) each tile changed
/// hands, how long it takes to fade over, which tiles are hatched (freshly conquered) or near
/// a defense post, and (only while thick borders are shown) each tile's distance to its border.
/// The map is split into 256x256-tile chunks (texture-array layers)
/// so only chunks that actually changed are re-uploaded.
/// </summary>
public sealed class TerritoryTextures
{
    public const int ChunkSize = 128; // small, so a change re-uploads little
    // Owner ids are packed in 12 bits (two per tile plus the fade length fit in RGBA8).
    public const ushort Water = 4095;
    public const ushort Fallout = 4094; // unowned land poisoned by a bomb
    public const ushort Outside = 4093; // land outside the play area (like water, it never borders a territory)
    public const int MaxPlayerIds = Outside;

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
    private readonly byte[][] _hatchBytes; // R8 bits: HatchedBit, WasHatchedBit, NearPostBit
    private const byte HatchedBit = 1;
    private const byte WasHatchedBit = 2; // hatched for its previous owner (while it fades away from them)
    private const byte NearPostBit = 4; // within reach (plus a little) of a finished defense post
    private readonly Image[] _hatchImages;
    // Per tile: how many defense posts' reach covers it (NearPostBit = more than none).
    private byte[]? _postReach;

    // Border distance (for borders thicker than the shader's smoothing can measure): per tile
    // (RGBA8), how far its centre is from the nearest tile of another owner (not water),
    // through its own land, minus half a tile (about the distance to the border line), in
    // 1/DistUnits tiles (255 = DistCap or more). R = before the latest change near it, G =
    // after, and B, A = the offset (+128) to the tile just taken whose front carries it from
    // R to G, so a thick border's inner edge sweeps along exactly with the territory (offset
    // 0: R is the distance its previous owner had there; see map.gdshader's
    // tile_border_distance). Only made and kept up to date while BorderDistances is on.
    public const float DistUnits = 24;
    private const int DistCap = 11; // tiles (255 / DistUnits, rounded up)
    private const int DistBlock = 32;
    private byte[][]? _distBytes;
    private Image[]? _distImages;
    private readonly int _distBlocksX;
    private readonly bool[] _distDirtyBlock;
    private readonly bool[] _distBlockChanged; // (set by the background work: any distance in the block changed)
    private readonly List<int> _distDirtyList = new();
    private bool _borderDistances;
    // Each tick's distances are worked out in the background (they can take ~10 ms when
    // everyone is expanding); the layers they touched upload once it's done.
    private System.Threading.Tasks.Task? _distTask;
    private int[] _distTaskBlocks = Array.Empty<int>();
    private readonly bool[] _distLayerDirty;
    private readonly bool[] _dirty;
    private readonly ushort[] _shown; // owner currently drawn for each tile

    public readonly Texture2DArray OwnerArray = new();
    public readonly Texture2DArray TimeArray = new();
    public readonly Texture2DArray HatchArray = new();
    public Texture2DArray? DistArray { get; private set; }
    public int ChunksX => _chunksX;

    /// <summary>The moment being drawn (MapView's visual time), for the border distances' blends.</summary>
    public float VisualTime { get; set; }

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
        _hatchBytes = new byte[_layers][];
        _hatchImages = new Image[_layers];
        _dirty = new bool[_layers];
        _distLayerDirty = new bool[_layers];
        _shown = new ushort[map.TileCount];
        _distBlocksX = (map.Width + DistBlock - 1) / DistBlock;
        _distDirtyBlock = new bool[_distBlocksX * ((map.Height + DistBlock - 1) / DistBlock)];
        _distBlockChanged = new bool[_distDirtyBlock.Length];

        var owners = new Godot.Collections.Array<Image>();
        var times = new Godot.Collections.Array<Image>();
        var hatches = new Godot.Collections.Array<Image>();
        for (int l = 0; l < _layers; l++)
        {
            _ownerBytes[l] = new byte[ChunkSize * ChunkSize * 4];
            _timeBytes[l] = new byte[ChunkSize * ChunkSize * 4];
            MemoryMarshal.Cast<byte, float>(_timeBytes[l]).Fill(LongAgo);
            _ownerImages[l] = Image.CreateFromData(ChunkSize, ChunkSize, false, Image.Format.Rgba8, _ownerBytes[l]);
            _timeImages[l] = Image.CreateFromData(ChunkSize, ChunkSize, false, Image.Format.Rf, _timeBytes[l]);
            _hatchBytes[l] = new byte[ChunkSize * ChunkSize];
            _hatchImages[l] = Image.CreateFromData(ChunkSize, ChunkSize, false, Image.Format.R8, _hatchBytes[l]);
            owners.Add(_ownerImages[l]);
            times.Add(_timeImages[l]);
            hatches.Add(_hatchImages[l]);
        }
        OwnerArray.CreateFromImages(owners);
        TimeArray.CreateFromImages(times);
        HatchArray.CreateFromImages(hatches);
    }

    /// <summary>Makes the border distance storage and texture (40 MB on Europe, so only once thick borders are used).</summary>
    private void EnsureDistances()
    {
        if (_distBytes is not null)
            return;
        _distBytes = new byte[_layers][];
        _distImages = new Image[_layers];
        var dists = new Godot.Collections.Array<Image>();
        for (int l = 0; l < _layers; l++)
        {
            var bytes = _distBytes[l] = new byte[ChunkSize * ChunkSize * 4];
            for (int i = 0; i < bytes.Length; i += 4)
            {
                bytes[i] = bytes[i + 1] = 255;
                bytes[i + 2] = bytes[i + 3] = 128;
            }
            _distImages[l] = Image.CreateFromData(ChunkSize, ChunkSize, false, Image.Format.Rgba8, bytes);
            dists.Add(_distImages[l]);
        }
        DistArray = new Texture2DArray();
        DistArray.CreateFromImages(dists);
    }

    /// <summary>
    /// Whether each tile's distance to its border is kept (for thick borders). Turning it on
    /// works it out for the whole map once (a fraction of a second on Europe).
    /// </summary>
    public bool BorderDistances
    {
        get => _borderDistances;
        set
        {
            if (_borderDistances == value)
                return;
            FinishDistances();
            _borderDistances = value;
            if (value)
            {
                EnsureDistances();
                RecomputeAllDistances();
            }
        }
    }

    /// <summary>Shows exactly these owners (and hatched tiles: settle tick, 0 = not hatched), with no animation (start of a match).</summary>
    public void SetAll(ushort[]? owners, bool[]? fallout = null, int[]? hatched = null)
    {
        FinishDistances();
        for (int i = 0; i < _map.TileCount; i++)
        {
            ushort id = Shown(i, owners, fallout);
            _shown[i] = id;
            // A hatched tile's time is when it was taken (the shader fades its stripes out by it).
            bool isHatched = hatched is not null && hatched[i] != 0;
            Write(i, id, id, isHatched ? hatched![i] - Boundari.Sim.HatchRules.SettleTicks : LongAgo, DefaultFade);
            WriteHatch(i, hatched is not null && hatched[i] != 0);
        }
        _frontLastTick.Clear();
        if (_borderDistances)
            RecomputeAllDistances();
        Array.Fill(_dirty, true);
        Flush(all: true);
    }

    /// <summary>
    /// Records the tiles that changed during one tick. Each front's tiles are spread across
    /// the tick in the order they were taken (on their own, so a slow front isn't early one
    /// tick and late the next because of other fronts), and fade over that front's pace (see
    /// <see cref="DefaultFade"/>), so the shader sweeps fronts forward smoothly.
    /// </summary>
    public void RecordTick(ushort[] owners, bool[]? fallout, int[]? hatched, List<int> changed, int tick)
    {
        FinishDistances(); // (last tick's, which read the owners about to change)
        // Hatching can change without the owner changing (land settling).
        foreach (int i in changed)
        {
            if (Shown(i, owners, fallout) != _shown[i])
                SetHatchBit(i, WasHatchedBit, HatchBits(i, HatchedBit));
            WriteHatch(i, hatched is not null && hatched[i] != 0);
        }

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
            if (NextTo(i, now))
                f.FirstRow++;
        }
        // A front that took several rows of land this tick (tiles taken / tiles that were next
        // to the taker's land) fades each tile in a fraction of the tick, so a row is done
        // before the next is: fading them all over the tick left the whole band half one
        // owner's and half the other's, and the borders (where neither clearly wins) filled it.
        foreach (int key in _thisTick.Keys)
        {
            ref var f = ref CollectionsMarshal.GetValueRefOrNullRef(_thisTick, key);
            float rows = (float)f.Count / Math.Max(f.FirstRow, 1);
            if (rows > FastFrontRows)
                f.Fade = Math.Max(FastFrontRows / rows, ShortestFade);
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
            if (_borderDistances)
                MarkDistancesDirty(i);
        }
        _thisTick.Clear();
        if (_borderDistances)
            RecomputeDirtyDistances(tick);
    }

    private const float FastFrontRows = 1.5f;
    private const float ShortestFade = 0.1f;

    /// <summary>Whether a tile touches (4 ways) land shown as <paramref name="owner"/>'s.</summary>
    private bool NextTo(int tile, ushort owner)
    {
        int x = tile % _map.Width, w = _map.Width;
        return (x > 0 && _shown[tile - 1] == owner) || (x < w - 1 && _shown[tile + 1] == owner)
            || (tile >= w && _shown[tile - w] == owner) || (tile + w < _shown.Length && _shown[tile + w] == owner);
    }

    private struct FrontThisTick
    {
        public int Count, Taken, FirstRow;
        public float Fade;
    }

    private readonly Dictionary<int, FrontThisTick> _thisTick = new();

    // Chunks are uploaded for up to UploadBudgetMs a frame (at least MinUploadsPerFlush); the
    // rest go on the next frames, keeping big fights from turning into one slow frame. A
    // chunk takes ~0.02 ms, so even a whole map at war is sent within a frame or two: borders
    // are drawn from the distances, and distances a few ticks older than the territory paint
    // the border where it used to be (blobs behind fast fronts, stray lines left behind).
    private const int MinUploadsPerFlush = 24;
    private const double UploadBudgetMs = 3;
    private int _nextFlush;

    /// <summary>Uploads chunks that changed since they were last uploaded (as many as fit in this frame's budget).</summary>
    public void Flush(bool all = false)
    {
        if (_distTask is { IsCompleted: true })
            FinishDistances();
        // Border distances upload even while the background work is writing them: it writes
        // each tile's four bytes at once, so a chunk sent meanwhile has every tile either from
        // before or after, and the chunk is sent again once that work is done.
        bool hasDist = _distBytes is not null;
        int uploads = 0, start = _nextFlush;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        long budget = (long)(UploadBudgetMs * System.Diagnostics.Stopwatch.Frequency / 1000);
        for (int k = 0; k < _layers; k++)
        {
            if (!all && uploads >= MinUploadsPerFlush && System.Diagnostics.Stopwatch.GetTimestamp() - started > budget)
                break;
            int l = (start + k) % _layers;
            if (!_dirty[l] && !(hasDist && _distLayerDirty[l]))
                continue;
            uploads++;
            _nextFlush = (l + 1) % _layers;
            if (_dirty[l])
            {
                _dirty[l] = false;
                _ownerImages[l].SetData(ChunkSize, ChunkSize, false, Image.Format.Rgba8, _ownerBytes[l]);
                _timeImages[l].SetData(ChunkSize, ChunkSize, false, Image.Format.Rf, _timeBytes[l]);
                _hatchImages[l].SetData(ChunkSize, ChunkSize, false, Image.Format.R8, _hatchBytes[l]);
                OwnerArray.UpdateLayer(_ownerImages[l], l);
                TimeArray.UpdateLayer(_timeImages[l], l);
                HatchArray.UpdateLayer(_hatchImages[l], l);
            }
            if (hasDist && _distLayerDirty[l])
            {
                _distLayerDirty[l] = false;
                _distImages![l].SetData(ChunkSize, ChunkSize, false, Image.Format.Rgba8, _distBytes![l]);
                DistArray!.UpdateLayer(_distImages[l], l);
            }
        }
    }

    /// <summary>Ids past the 12-bit range (never expected) are drawn as unclaimed.</summary>
    private static ushort Packable(ushort id) => id < MaxPlayerIds || id == Water || id == Fallout || id == Outside ? id : (ushort)0;

    private ushort Shown(int i, ushort[]? owners, bool[]? fallout)
    {
        if (_map.IsWater(i))
            return Water;
        if (_map.IsOutside(i))
            return Outside;
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

    private void WriteHatch(int tile, bool hatched) => SetHatchBit(tile, HatchedBit, hatched);

    private bool HatchBits(int tile, byte bit)
    {
        int x = tile % _map.Width, y = tile / _map.Width;
        return (_hatchBytes[(y / ChunkSize) * _chunksX + x / ChunkSize][(y % ChunkSize) * ChunkSize + x % ChunkSize] & bit) != 0;
    }

    private void SetHatchBit(int tile, byte bit, bool on)
    {
        int x = tile % _map.Width, y = tile / _map.Width;
        int layer = (y / ChunkSize) * _chunksX + x / ChunkSize;
        int local = (y % ChunkSize) * ChunkSize + x % ChunkSize;
        byte old = _hatchBytes[layer][local];
        byte value = on ? (byte)(old | bit) : (byte)(old & ~bit);
        if (old == value)
            return;
        _hatchBytes[layer][local] = value;
        _dirty[layer] = true;
    }

    // ------------------------------------------------------------------
    //  Defense post reach
    // ------------------------------------------------------------------

    /// <summary>
    /// A finished defense post appeared (+1) or went (-1): marks the tiles within
    /// <paramref name="radius"/> of it, so the shader only looks for posts near those.
    /// </summary>
    public void AddPostReach(int tile, int radius, int delta)
    {
        _postReach ??= new byte[_map.TileCount];
        int cx = tile % _map.Width, cy = tile / _map.Width;
        for (int y = Math.Max(cy - radius, 0); y <= Math.Min(cy + radius, _map.Height - 1); y++)
            for (int x = Math.Max(cx - radius, 0); x <= Math.Min(cx + radius, _map.Width - 1); x++)
            {
                if ((x - cx) * (x - cx) + (y - cy) * (y - cy) > radius * radius)
                    continue;
                int i = y * _map.Width + x;
                _postReach[i] = (byte)Math.Clamp(_postReach[i] + delta, 0, 255);
                SetHatchBit(i, NearPostBit, _postReach[i] > 0);
            }
    }

    // ------------------------------------------------------------------
    //  Border distance
    // ------------------------------------------------------------------

    private void MarkDistancesDirty(int tile)
    {
        int x = tile % _map.Width, y = tile / _map.Width;
        int bx0 = Math.Max(x - DistCap, 0) / DistBlock, bx1 = Math.Min(x + DistCap, _map.Width - 1) / DistBlock;
        int by0 = Math.Max(y - DistCap, 0) / DistBlock, by1 = Math.Min(y + DistCap, _map.Height - 1) / DistBlock;
        for (int by = by0; by <= by1; by++)
            for (int bx = bx0; bx <= bx1; bx++)
            {
                int b = by * _distBlocksX + bx;
                if (_distDirtyBlock[b])
                    continue;
                _distDirtyBlock[b] = true;
                _distDirtyList.Add(b);
            }
    }

    /// <summary>Starts working out the blocks this tick changed, in the background (blending from what's shown now).</summary>
    private void RecomputeDirtyDistances(int tick)
    {
        if (_distDirtyList.Count == 0)
            return;
        var blocks = _distDirtyList.ToArray();
        _distDirtyList.Clear();
        _distTaskBlocks = blocks;
        float now = VisualTime;
        _distTask = System.Threading.Tasks.Task.Run(() =>
            System.Threading.Tasks.Parallel.ForEach(blocks, b => RecomputeDistances(b % _distBlocksX * DistBlock, b / _distBlocksX * DistBlock, now, tick)));
    }

    /// <summary>Waits for the background distances (if any) and queues their layers for upload.</summary>
    private void FinishDistances()
    {
        if (_distTask is null)
            return;
        _distTask.Wait();
        _distTask = null;
        foreach (int b in _distTaskBlocks)
        {
            _distDirtyBlock[b] = false;
            if (_distBlockChanged[b])
                MarkLayersDirty(b);
            _distBlockChanged[b] = false;
        }
        _distTaskBlocks = Array.Empty<int>();
    }

    private void RecomputeAllDistances()
    {
        int blocks = _distDirtyBlock.Length;
        System.Threading.Tasks.Parallel.For(0, blocks, b => RecomputeDistances(b % _distBlocksX * DistBlock, b / _distBlocksX * DistBlock, float.NaN, 0));
        for (int b = 0; b < blocks; b++)
            MarkLayersDirty(b);
        _distDirtyList.Clear();
        Array.Fill(_distDirtyBlock, false);
        Array.Fill(_distBlockChanged, false);
    }

    private void MarkLayersDirty(int block)
    {
        int x = block % _distBlocksX * DistBlock, y = block / _distBlocksX * DistBlock;
        for (int ly = y / ChunkSize; ly <= Math.Min(y + DistBlock - 1, _map.Height - 1) / ChunkSize; ly++)
            for (int lx = x / ChunkSize; lx <= Math.Min(x + DistBlock - 1, _map.Width - 1) / ChunkSize; lx++)
                _distLayerDirty[ly * _chunksX + lx] = true;
    }

    // Steps looked back along by the two passes (a neighbour's nearest foreign tile, moved by the step).
    private static readonly (int X, int Y)[] Forward = { (-1, 0), (-1, -1), (0, -1), (1, -1) };
    private static readonly (int X, int Y)[] Backward = { (1, 0), (1, 1), (0, 1), (-1, 1) };
    private static readonly (int X, int Y)[] Right = { (1, 0) };
    private static readonly (int X, int Y)[] Left = { (-1, 0) };

    private float CaptureTime(int tile)
    {
        int x = tile % _map.Width, y = tile / _map.Width;
        return MemoryMarshal.Cast<byte, float>(_timeBytes[(y / ChunkSize) * _chunksX + x / ChunkSize])[(y % ChunkSize) * ChunkSize + x % ChunkSize];
    }

    private void Capture(int tile, out float time, out float fade)
    {
        int x = tile % _map.Width, y = tile / _map.Width;
        int layer = (y / ChunkSize) * _chunksX + x / ChunkSize, local = (y % ChunkSize) * ChunkSize + x % ChunkSize;
        fade = _ownerBytes[layer][local * 4 + 3] / FadeUnits;
        time = MemoryMarshal.Cast<byte, float>(_timeBytes[layer])[local];
    }

    // A front's tiles taken in one tick are spread over it (tick + k/n) and each fades over
    // the front's pace, so the line between the two owners (where a tile is half faded)
    // reaches a tile taken at c half a fade later, having set off from the front's old line
    // half a fade after the tick began. These two match map.gdshader's functions of the same names.

    /// <summary>How far the front taking this tile has come from where it stood at its tick's start (0) to the tile (1).</summary>
    private float FrontProgress(int tile, float now)
    {
        Capture(tile, out float c, out float fade);
        float tick = MathF.Ceiling(c) - 1;
        return Math.Clamp((now - tick - 0.5f * fade) / Math.Max(c - tick, 1e-3f), 0, 1);
    }

    /// <summary>How far that front has gone on from this tile (0) to where it ends its tick (1).</summary>
    private float FrontPassed(int tile, float now)
    {
        Capture(tile, out float c, out float fade);
        float tick = MathF.Ceiling(c) - 1;
        return Math.Clamp((now - c - 0.5f * fade) / Math.Max(tick + 1 - c, 1e-3f), 0, 1);
    }

    /// <summary>
    /// Works out the border distance of every tile in one DistBlock-sized block: a two-pass
    /// vector distance transform (8SSEDT) over the block plus DistCap tiles around it, where
    /// each tile carries the offset to its nearest foreign tile and passes it on only to
    /// tiles of the same owner (so distances go through a territory's own land). With a
    /// moment <paramref name="now"/> (not NaN), a tile whose distance changed blends to it from
    /// what's shown now, as the front of the tile just taken (in tick <paramref name="tick"/>
    /// or later) by its nearest foreign tile moves; a tile taken in this tick keeps its previous
    /// owner's distance (R, offset 0), which the shader shrinks as the front comes for it.
    /// </summary>
    private void RecomputeDistances(int bx, int by, float now, int tick)
    {
        int w = _map.Width, h = _map.Height;
        int x0 = Math.Max(bx - DistCap, 0), y0 = Math.Max(by - DistCap, 0);
        int x1 = Math.Min(bx + DistBlock + DistCap, w), y1 = Math.Min(by + DistBlock + DistCap, h);
        int rw = x1 - x0, rh = y1 - y0;
        const short None = short.MaxValue;
        var vx = new short[rw * rh];
        var vy = new short[rw * rh];
        var id = new ushort[rw * rh];
        for (int y = 0; y < rh; y++)
            for (int x = 0; x < rw; x++)
                id[y * rw + x] = _shown[(y0 + y) * w + x0 + x];

        static bool Measured(ushort owner) => owner != 0 && owner != Water && owner != Fallout && owner != Outside;
        // Start: tiles right next to a foreign tile (another owner's, unclaimed or fallout; not water or outside) point at it.
        for (int y = 0; y < rh; y++)
            for (int x = 0; x < rw; x++)
            {
                int i = y * rw + x;
                vx[i] = None;
                ushort mine = id[i];
                if (!Measured(mine))
                    continue;
                int best = int.MaxValue;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if ((dx == 0 && dy == 0) || nx < 0 || ny < 0 || nx >= rw || ny >= rh)
                            continue;
                        ushort other = id[ny * rw + nx];
                        int d2 = dx * dx + dy * dy;
                        if (d2 < best && other != mine && other != Water && other != Outside)
                        {
                            best = d2;
                            vx[i] = (short)dx;
                            vy[i] = (short)dy;
                        }
                    }
            }

        void Pull(int x, int y, (int X, int Y)[] steps)
        {
            int i = y * rw + x;
            ushort mine = id[i];
            if (!Measured(mine))
                return;
            int best = vx[i] == None ? int.MaxValue : vx[i] * vx[i] + vy[i] * vy[i];
            foreach (var (sx, sy) in steps)
            {
                int nx = x + sx, ny = y + sy;
                if (nx < 0 || ny < 0 || nx >= rw || ny >= rh)
                    continue;
                int n = ny * rw + nx;
                if (id[n] != mine || vx[n] == None)
                    continue;
                int cx = vx[n] + sx, cy = vy[n] + sy, d2 = cx * cx + cy * cy;
                if (d2 < best)
                {
                    best = d2;
                    vx[i] = (short)cx;
                    vy[i] = (short)cy;
                }
            }
        }
        for (int y = 0; y < rh; y++)
        {
            for (int x = 0; x < rw; x++)
                Pull(x, y, Forward);
            for (int x = rw - 1; x >= 0; x--)
                Pull(x, y, Right);
        }
        for (int y = rh - 1; y >= 0; y--)
        {
            for (int x = rw - 1; x >= 0; x--)
                Pull(x, y, Backward);
            for (int x = 0; x < rw; x++)
                Pull(x, y, Left);
        }

        // Write back the block itself, each tile's four bytes at once (its chunk may be
        // uploading meanwhile, see Flush), noting whether anything changed.
        bool animate = !float.IsNaN(now), changed = false;
        for (int y = by; y < Math.Min(by + DistBlock, h); y++)
            for (int x = bx; x < Math.Min(bx + DistBlock, w); x++)
            {
                int i = (y - y0) * rw + x - x0, tile = y * w + x;
                var words = MemoryMarshal.Cast<byte, uint>(_distBytes![(y / ChunkSize) * _chunksX + x / ChunkSize]);
                int local = (y % ChunkSize) * ChunkSize + x % ChunkSize;
                uint old = words[local];
                // (R, G, B, A as in the texture: little-endian, R lowest)
                byte oldFrom = (byte)old, oldTo = (byte)(old >> 8), oldOx = (byte)(old >> 16), oldOy = (byte)(old >> 24);
                byte to = 255;
                if (vx[i] != None)
                {
                    float d = MathF.Sqrt(vx[i] * vx[i] + vy[i] * vy[i]) - 0.5f;
                    to = (byte)Math.Min(255, (int)MathF.Round(d * DistUnits));
                }
                byte from = to;
                int ox = 0, oy = 0;
                // What the old values show (for the owner they were worked out for): a blend
                // along another tile's front, or (offset 0) the distance from when the tile was
                // taken, which starts at the front's line and grows as the front moves on.
                bool oldOwn = oldOx == 128 && oldOy == 128;
                int oldCause = Math.Clamp(y + oldOy - 128, 0, h - 1) * w + Math.Clamp(x + oldOx - 128, 0, w - 1);
                byte Blended(float time) => (byte)MathF.Round(oldFrom + (oldTo - oldFrom) * FrontProgress(oldCause, time));
                if (animate && CaptureTime(tile) >= tick)
                {
                    // Taken in this tick: R keeps what its previous owner shows here as this
                    // tick's fronts set off (the shader brings it to the edge as the front
                    // nears), so land swept up deep behind the line isn't all drawn as border.
                    Capture(tile, out _, out float fade);
                    from = oldOwn ? oldTo : Blended(Math.Max(tick + 0.5f * fade, now));
                }
                else if (animate && Measured(id[i]) && vx[i] != None)
                {
                    byte Shown(float time) => oldOwn
                        ? (byte)MathF.Round(0.5f * DistUnits + (oldTo - 0.5f * DistUnits) * FrontPassed(tile, time))
                        : Blended(time);
                    // What moves it: the tile taken last at (or next to) the nearest foreign tile.
                    int fx = x + vx[i], fy = y + vy[i], cause = -1;
                    float latest = float.MinValue;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = fx + dx, ny = fy + dy;
                            if (nx < 0 || ny < 0 || nx >= w || ny >= h || ny * w + nx == tile)
                                continue; // (never the tile itself: offset 0 means the above)
                            float taken = CaptureTime(ny * w + nx);
                            if (taken > latest)
                            {
                                latest = taken;
                                cause = ny * w + nx;
                            }
                        }
                    if (cause >= 0 && FrontProgress(cause, now) < 1)
                    {
                        // The new blend starts from where the one shown now will be when its
                        // front sets off, not from where it is now: territory is drawn a tick
                        // behind, so "now" is still the start of the last blend, and a front
                        // taking land every tick would keep restarting it there, leaving its old
                        // border behind as a ghost.
                        Capture(cause, out float c, out float fade);
                        byte start = Shown(Math.Max(MathF.Ceiling(c) - 1 + 0.5f * fade, now));
                        if (start != to)
                        {
                            from = start;
                            ox = cause % w - x;
                            oy = cause / w - y;
                        }
                    }
                }
                uint word = from | (uint)to << 8 | (uint)(byte)(ox + 128) << 16 | (uint)(byte)(oy + 128) << 24;
                if (word != old)
                {
                    words[local] = word;
                    changed = true;
                }
            }
        if (changed)
            _distBlockChanged[by / DistBlock * _distBlocksX + bx / DistBlock] = true;
    }
}
