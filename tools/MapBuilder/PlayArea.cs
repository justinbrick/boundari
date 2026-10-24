using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Boundari.Map;

namespace Boundari.MapBuilder;

/// <summary>A stretch of a play area's edge, as (latitude, longitude) pairs.</summary>
public abstract record EdgeLeg;

/// <summary>
/// Hand-placed points: the edge runs through them on a smooth curve (so it looks natural,
/// not like straight cuts between them).
/// </summary>
public sealed record EdgePoints(params (double Lat, double Lon)[] Points) : EdgeLeg;

/// <summary>The edge follows a real river (Natural Earth's line by that name) between the points nearest these two.</summary>
public sealed record EdgeRiver(string Name, (double Lat, double Lon) From, (double Lat, double Lon) To) : EdgeLeg;

/// <summary>
/// Where a regional map can be played: land outside it becomes <see cref="TerrainType.Outside"/>
/// (dark grey in the game, like the world around the map, and never claimable). Water is all
/// playable.
/// <para>
/// On land the edge follows real borders (Natural Earth's admin-1 provinces/regions, so country
/// borders too). With <paramref name="Countries"/>, those countries are in whole and others
/// out, except <paramref name="SplitCountries"/> (too big for the map), whose regions are in
/// when most of their land lies inside the rough <paramref name="Edge"/> outline. Without
/// <paramref name="Countries"/>, every region goes by that rule. Regions named in
/// <paramref name="AlwaysIn"/> / <paramref name="AlwaysOut"/> are in / out whatever, and those in
/// <paramref name="CutByOutline"/> are cut along the outline. Separate islands of a region count
/// on their own.
/// </para>
/// The result is also kept as a smooth field (<see cref="GameMap.PlayField"/>) so the game draws
/// the edge as a clean line rather than tile by tile.
/// </summary>
public sealed record PlayArea(
    EdgeLeg[] Edge,
    string[]? AlwaysIn = null,
    string[]? AlwaysOut = null,
    string[]? Countries = null,
    string[]? SplitCountries = null,
    string[]? CutByOutline = null)
{
    private const double Range = 8;          // the field's reach, in tiles (it saturates past this)

    private const int PocketTiles = 20000;   // smaller playable patches cut off from the rest are dropped

    private const double LakeReach = 3;      // greyed-out lakes this near playable land stay water

    private enum Region { In, Out, Cut }

    private sealed record Unit(string Name, string Country, string Code, List<TileRing> Rings, double MinX, double MinY, double MaxX, double MaxY);

    /// <summary>Works out the play area, stores its field and marks every tile outside it as Outside.</summary>
    public void Apply(GameMap map, MapSpec spec, string dataDir)
    {
        var geo = map.Geo!;
        int w = map.Width, h = map.Height, n = map.TileCount;
        bool Land(int i) => map.CoastField[i] > 0;

        // The rough outline: a signed distance to it (+ inside), exact near it.
        var outline = Outline(Path.Combine(dataDir, "ne_10m_rivers_lake_centerlines.geojson"));
        var ring = new TileRing(outline.Select(p => geo.TileX(p.Lon, w)).ToArray(), outline.Select(p => geo.TileY(p.Lat, h)).ToArray());
        bool[] inGuide = Rasterizer.Fill(new[] { ring }, w, h);
        float[] guideDist = Rasterizer.EdgeDistance(new[] { ring }, w, h, Range);
        double Guide(int i) => (inGuide[i] ? 1 : -1) * Math.Min(guideDist[i], Range);

        // Provinces and regions, each island on its own, filled into the tile grid.
        var units = LoadUnits(Path.Combine(dataDir, "ne_10m_admin_1_states_provinces.geojson"), geo, w, h);
        var unitOf = new int[n];
        Array.Fill(unitOf, -1);
        for (int u = 0; u < units.Count; u++)
            FillUnit(units[u], u, unitOf, w, h);

        // Land the region outlines just miss (their coasts differ a little from ours) joins the nearest region.
        var landUnit = new int[n];
        var queue = new Queue<int>();
        for (int i = 0; i < n; i++)
        {
            landUnit[i] = Land(i) ? unitOf[i] : -1;
            if (landUnit[i] >= 0)
                queue.Enqueue(i);
        }
        while (queue.Count > 0)
        {
            int t = queue.Dequeue();
            foreach (int nb in Neighbours4(t, w, h))
                if (Land(nb) && landUnit[nb] < 0)
                {
                    landUnit[nb] = landUnit[t];
                    queue.Enqueue(nb);
                }
        }

        // Which regions are in: most of their land inside the outline, unless named otherwise.
        var landTiles = new int[units.Count];
        var insideTiles = new int[units.Count];
        for (int i = 0; i < n; i++)
            if (landUnit[i] >= 0)
            {
                landTiles[landUnit[i]]++;
                if (inGuide[i])
                    insideTiles[landUnit[i]]++;
            }
        bool Named(Unit u, string[]? names) => names is not null && names.Any(x => x == u.Name || x == u.Code || x == u.Country);
        // A region running off the map can't be wholly in (it would end in a straight cut along
        // the map's edge): if at least a tenth of it is inside the outline, the outline cuts it.
        bool OffMap(Unit u) => u.MinX < 0 || u.MinY < 0 || u.MaxX > w || u.MaxY > h;
        var state = new Region[units.Count];
        for (int u = 0; u < units.Count; u++)
        {
            var unit = units[u];
            bool whole = Countries is not null && Countries.Contains(unit.Country);
            bool split = Countries is null || (SplitCountries?.Contains(unit.Country) ?? false);
            state[u] = Named(unit, AlwaysOut) ? Region.Out
                : Named(unit, AlwaysIn) ? Region.In
                : Named(unit, CutByOutline) ? Region.Cut
                : whole ? (OffMap(unit) ? Region.Out : Region.In)
                : !split ? Region.Out
                : OffMap(unit) ? (insideTiles[u] * 10 >= landTiles[u] ? Region.Cut : Region.Out)
                : insideTiles[u] * 2 > landTiles[u] ? Region.In : Region.Out;
        }

        // Playable land tiles; then small playable pockets cut off from the main playable part of
        // their landmass are dropped (a lone province surrounded by greyed-out land).
        var playable = new bool[n];
        var cutOff = new bool[n];
        void Mark()
        {
            for (int i = 0; i < n; i++)
                playable[i] = !cutOff[i] && (landUnit[i] >= 0 ? state[landUnit[i]] == Region.In || (state[landUnit[i]] == Region.Cut && inGuide[i]) : Land(i) && inGuide[i]);
        }
        Mark();
        int pockets = DropPockets(map, playable, landUnit, landTiles, state, cutOff, w, h);
        Mark();
        Report(units, landTiles, insideTiles, state, OffMap);
        bool[] filled = GreyOutInlandWater(map, spec, playable);

        // The play area's edge on land: region borders and stretches of the outline (inside cut
        // regions) with playable land on one side and unplayable land on the other; each tile's
        // distance to them.
        var dist = new float[n];
        Array.Fill(dist, (float)Range);
        int borderEdges = 0;
        void Consider(double ax, double ay, double bx, double by)
        {
            if (IsEdge(ax, ay, bx, by, playable, landUnit, w, h))
            {
                borderEdges++;
                DistanceToSegment(dist, ax, ay, bx, by, w, h);
            }
        }
        foreach (var unit in units)
            foreach (var r in unit.Rings)
                for (int k = 0; k < r.X.Length; k++)
                    Consider(r.X[k], r.Y[k], r.X[(k + 1) % r.X.Length], r.Y[(k + 1) % r.Y.Length]);
        for (int k = 0; k < ring.X.Length; k++)
            Consider(ring.X[k], ring.Y[k], ring.X[(k + 1) % ring.X.Length], ring.Y[(k + 1) % ring.Y.Length]);

        // Land: + inside the play area, - outside, by its distance to that edge.
        var landField = new double[n];
        for (int i = 0; i < n; i++)
            if (filled[i])
                landField[i] = -Range;
            else if (Land(i))
                landField[i] = landUnit[i] >= 0 ? (playable[i] ? dist[i] : -dist[i]) : Guide(i);

        // The field carries on a few tiles out over the water, so the drawn edge doesn't jump
        // at the coast (the shader blends land and water there).
        var field = Spread(landField, i => Land(i), w, h, n, Guide);

        // Outside tiles, and the stored field.
        int outsideTiles = 0;
        var play = new byte[n];
        for (int i = 0; i < n; i++)
        {
            if (Land(i) && field[i] < 0)
            {
                map.Terrain[i] = TerrainType.Outside;
                outsideTiles++;
            }
            play[i] = Quantize(field[i]);
        }

        // Slivers of land the edge cut off from the rest (smaller than the smallest island kept) go too.
        MapProcessing.LabelLandmasses(map);
        int slivers = 0;
        for (int i = 0; i < n; i++)
        {
            int id = map.LandmassId[i];
            if (id != GameMap.NoLandmass && map.LandmassSizes[id] < spec.MinIslandTiles)
            {
                map.Terrain[i] = TerrainType.Outside;
                play[i] = Quantize(-1);
                slivers++;
            }
        }
        MapProcessing.LabelLandmasses(map);
        map.PlayField = play;
        Console.WriteLine($"  play area: {units.Count} regions ({state.Count(s => s == Region.In)} in, {state.Count(s => s == Region.Cut)} cut by the outline), " +
                          $"{pockets} pocket regions dropped, {borderEdges} edge segments, " +
                          $"{100.0 * outsideTiles / n:F1}% of the map outside, {slivers} sliver tiles dropped");
    }

    /// <summary>
    /// Rivers and lakes out in the greyed-out land are filled in as land (grey like the rest: it
    /// looks cleaner; the sea stays water). Water by playable land stays: lakes with a shore
    /// within <see cref="LakeReach"/> tiles of it (the whole lake), and river water nearer to it
    /// than to unplayable land, or lying between it and the far bank (a river along the edge
    /// stays water to its far shore). A river running out of the play area ends at the edge.
    /// Returns the tiles filled in.
    /// </summary>
    private static bool[] GreyOutInlandWater(GameMap map, MapSpec spec, bool[] playable)
    {
        int w = map.Width, h = map.Height, n = map.TileCount;
        var filled = new bool[n];
        if (!Program.Sources.TryGetValue(map, out var src))
            return filled;
        bool Inland(int i) => map.CoastField[i] <= 0 && src.InLand[i];
        var otherLand = new bool[n];
        for (int i = 0; i < n; i++)
            otherLand[i] = map.CoastField[i] > 0 && !playable[i];
        float[] toPlayable = Rasterizer.DistanceTransform(playable, w, h);
        float[] toOther = Rasterizer.DistanceTransform(otherLand, w, h);

        // Lakes (and the land cover's ponds), each as a whole.
        var seen = new bool[n];
        var queue = new Queue<int>();
        var lake = new List<int>();
        int lakes = 0;
        for (int i = 0; i < n; i++)
        {
            if (seen[i] || !Inland(i) || src.InRiver[i])
                continue;
            lake.Clear();
            seen[i] = true;
            queue.Enqueue(i);
            bool bySea = false;
            while (queue.Count > 0)
            {
                int t = queue.Dequeue();
                lake.Add(t);
                foreach (int nb in Neighbours4(t, w, h))
                {
                    bySea |= map.CoastField[nb] <= 0 && !src.InLand[nb];
                    if (!seen[nb] && Inland(nb) && !src.InRiver[nb])
                    {
                        seen[nb] = true;
                        queue.Enqueue(nb);
                    }
                }
            }
            // (open to the sea: not a lake but an island too small to keep, or a lagoon)
            if (bySea || lake.Any(t => toPlayable[t] <= LakeReach))
                continue;
            foreach (int t in lake)
                filled[t] = true;
            lakes++;
        }

        // Rivers, tile by tile.
        double reach = spec.RiverWidth * 1.6; // across the widest river, at a slant
        for (int i = 0; i < n; i++)
        {
            if (!Inland(i) || !src.InRiver[i] || toPlayable[i] <= 1.5 || toPlayable[i] <= toOther[i])
                continue;
            if (toPlayable[i] > reach || !Between(i))
                filled[i] = true;
        }

        int count = 0;
        for (int i = 0; i < n; i++)
            if (filled[i])
            {
                map.CoastField[i] = 0.05f * Program.FieldPerTile;
                count++;
            }
        if (count > 0)
        {
            Program.Classify(map, spec, minLakeTiles: 1); // (measures the land's coast field again without them)
            for (int i = 0; i < n; i++)
                filled[i] &= map.CoastField[i] > 0;
            // Water that ends at them (a river cut off at the edge) is no further from the shore than from them.
            float[] toFilled = Rasterizer.DistanceTransform(filled, w, h);
            for (int i = 0; i < n; i++)
                if (map.CoastField[i] <= 0)
                    map.CoastField[i] = Math.Max(map.CoastField[i], -(toFilled[i] - 0.5f) * Program.FieldPerTile);
        }
        Console.WriteLine($"  greyed-out water filled in: {lakes} lakes, {count} tiles in all");
        return filled;

        // True if some line through this river tile reaches playable land one way and land (or
        // the sea) the other, within one river's width.
        bool Between(int i)
        {
            double cx = i % w + 0.5, cy = i / w + 0.5;
            for (int k = 0; k < 16; k++)
            {
                double a = k * Math.PI / 16, dx = Math.Cos(a), dy = Math.Sin(a);
                var (d1, p1) = March(cx, cy, dx, dy);
                var (d2, p2) = March(cx, cy, -dx, -dy);
                if ((p1 || p2) && d1 + d2 <= reach)
                    return true;
            }
            return false;
        }

        // How far along this direction the river ends, and whether playable land is there.
        (double Dist, bool Playable) March(double x, double y, double dx, double dy)
        {
            for (double s = 0.5; s <= reach; s += 0.5)
            {
                int tx = (int)Math.Floor(x + dx * s), ty = (int)Math.Floor(y + dy * s);
                if (tx < 0 || ty < 0 || tx >= w || ty >= h)
                    break;
                int t = ty * w + tx;
                if (!Inland(t))
                    return (s, playable[t]);
            }
            return (double.PositiveInfinity, false);
        }
    }

    private static byte Quantize(double v) => (byte)Math.Clamp((int)Math.Round(128 + v * GameMap.PlayFieldSteps), 0, 255);

    /// <summary>Lists the regions the outline cuts through (so they can be moved in or out by name).</summary>
    private static void Report(List<Unit> units, int[] landTiles, int[] insideTiles, Region[] state, Func<Unit, bool> offMap)
    {
        var cut = Enumerable.Range(0, units.Count)
            .Where(u => landTiles[u] > 2000 && insideTiles[u] > 0 && insideTiles[u] < landTiles[u])
            .OrderBy(u => units[u].Country).ThenBy(u => units[u].Name);
        foreach (int u in cut)
            Console.WriteLine($"    {state[u].ToString().ToLowerInvariant(),-3} {units[u].Country} / {units[u].Name} ({units[u].Code}): {100.0 * insideTiles[u] / landTiles[u]:F0}% inside the outline{(offMap(units[u]) ? ", runs off the map" : "")}");
    }

    /// <summary>
    /// A field defined on one kind of tile (<paramref name="own"/>), carried 3 tiles onto the
    /// other kind (each tile taking the best of its neighbours); further on, <paramref name="fallback"/>.
    /// </summary>
    private static double[] Spread(double[] field, Func<int, bool> own, int w, int h, int n, Func<int, double> fallback)
    {
        var result = new double[n];
        var have = new bool[n];
        for (int i = 0; i < n; i++)
            if (own(i))
            {
                result[i] = field[i];
                have[i] = true;
            }
        for (int pass = 0; pass < 3; pass++)
        {
            var next = (bool[])have.Clone();
            for (int i = 0; i < n; i++)
            {
                if (have[i])
                    continue;
                double best = double.NegativeInfinity;
                foreach (int nb in Neighbours4(i, w, h))
                    if (have[nb])
                        best = Math.Max(best, result[nb]);
                if (best > double.NegativeInfinity)
                {
                    result[i] = best;
                    next[i] = true;
                }
            }
            have = next;
        }
        for (int i = 0; i < n; i++)
            if (!have[i])
                result[i] = fallback(i);
        return result;
    }

    /// <summary>True if this line (or a piece of it) has playable land on one side and unplayable land on the other.</summary>
    private static bool IsEdge(double ax, double ay, double bx, double by, bool[] playable, int[] landUnit, int w, int h)
    {
        double dx = bx - ax, dy = by - ay, len = Math.Sqrt(dx * dx + dy * dy);
        if (len == 0)
            return false;
        double nx = -dy / len * 0.75, ny = dx / len * 0.75;
        int pieces = Math.Max(1, (int)Math.Ceiling(len / 2));
        for (int p = 0; p < pieces; p++)
        {
            double t = (p + 0.5) / pieces, mx = ax + dx * t, my = ay + dy * t;
            int a = TileAt(mx + nx, my + ny), b = TileAt(mx - nx, my - ny);
            if (a >= 0 && b >= 0 && landUnit[a] >= 0 && landUnit[b] >= 0 && playable[a] != playable[b])
                return true;
        }
        return false;

        int TileAt(double x, double y)
        {
            int i = (int)Math.Floor(x), j = (int)Math.Floor(y);
            return i < 0 || j < 0 || i >= w || j >= h ? -1 : j * w + i;
        }
    }

    /// <summary>
    /// Playable patches of land cut off from the rest of the play area (not even joined by
    /// water: rivers and seas count as joining) and smaller than <see cref="PocketTiles"/> (a
    /// province left on its own amid greyed-out land): regions mostly in them are taken out,
    /// and stray bits of the rest are marked <paramref name="cutOff"/>. Returns how many regions.
    /// </summary>
    private static int DropPockets(GameMap map, bool[] playable, int[] landUnit, int[] landTiles, Region[] state, bool[] cutOff, int w, int h)
    {
        int n = map.TileCount;
        var patch = new int[n];
        Array.Fill(patch, -1);
        var sizes = new List<int>();
        var queue = new Queue<int>();
        bool Joins(int t) => playable[t] || map.CoastField[t] <= 0;
        for (int i = 0; i < n; i++)
        {
            if (!Joins(i) || patch[i] >= 0)
                continue;
            int id = sizes.Count, size = 0;
            patch[i] = id;
            queue.Enqueue(i);
            while (queue.Count > 0)
            {
                int t = queue.Dequeue();
                if (map.CoastField[t] > 0)
                    size++; // (land tiles)
                foreach (int nb in Neighbours4(t, w, h))
                    if (Joins(nb) && patch[nb] < 0)
                    {
                        patch[nb] = id;
                        queue.Enqueue(nb);
                    }
            }
            sizes.Add(size);
        }
        int biggest = 0;
        for (int p = 1; p < sizes.Count; p++)
            if (sizes[p] > sizes[biggest])
                biggest = p;
        var stranded = new int[state.Length];
        for (int i = 0; i < n; i++)
        {
            int p = patch[i];
            if (p >= 0 && p != biggest && sizes[p] < PocketTiles && map.CoastField[i] > 0)
            {
                cutOff[i] = true;
                if (landUnit[i] >= 0)
                    stranded[landUnit[i]]++;
            }
        }
        int dropped = 0;
        for (int u = 0; u < state.Length; u++)
            if (state[u] == Region.In && stranded[u] * 2 > landTiles[u])
            {
                state[u] = Region.Out;
                dropped++;
            }
        return dropped;
    }

    /// <summary>Lowers each tile's distance to the nearest border to its distance from this segment, within <see cref="Range"/>.</summary>
    private static void DistanceToSegment(float[] dist, double ax, double ay, double bx, double by, int w, int h)
    {
        int i0 = Math.Max(0, (int)Math.Floor(Math.Min(ax, bx) - Range));
        int i1 = Math.Min(w - 1, (int)Math.Ceiling(Math.Max(ax, bx) + Range));
        int j0 = Math.Max(0, (int)Math.Floor(Math.Min(ay, by) - Range));
        int j1 = Math.Min(h - 1, (int)Math.Ceiling(Math.Max(ay, by) + Range));
        double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
        for (int j = j0; j <= j1; j++)
            for (int i = i0; i <= i1; i++)
            {
                double px = i + 0.5 - ax, py = j + 0.5 - ay;
                double t = len2 > 0 ? Math.Clamp((px * dx + py * dy) / len2, 0, 1) : 0;
                double ex = px - t * dx, ey = py - t * dy;
                float d = (float)Math.Sqrt(ex * ex + ey * ey);
                int idx = j * w + i;
                if (d < dist[idx])
                    dist[idx] = d;
            }
    }

    private static IEnumerable<int> Neighbours4(int t, int w, int h)
    {
        int x = t % w, y = t / w;
        if (x > 0) yield return t - 1;
        if (x < w - 1) yield return t + 1;
        if (y > 0) yield return t - w;
        if (y < h - 1) yield return t + w;
    }

    /// <summary>Fills a region's tile centres (even-odd, so lakes cut out of it stay out) with its number.</summary>
    private static void FillUnit(Unit unit, int id, int[] unitOf, int w, int h)
    {
        int j0 = Math.Max(0, (int)Math.Floor(unit.MinY)), j1 = Math.Min(h - 1, (int)Math.Ceiling(unit.MaxY));
        if (j0 > j1 || unit.MaxX < 0 || unit.MinX > w)
            return;
        var crossings = new List<double>[j1 - j0 + 1];
        for (int j = 0; j < crossings.Length; j++)
            crossings[j] = new List<double>();
        foreach (var r in unit.Rings)
            for (int k = 0; k < r.X.Length; k++)
            {
                double x0 = r.X[k], y0 = r.Y[k], x1 = r.X[(k + 1) % r.X.Length], y1 = r.Y[(k + 1) % r.Y.Length];
                if (y0 == y1)
                    continue;
                int js = Math.Max(j0, (int)Math.Ceiling(Math.Min(y0, y1) - 0.5));
                int je = Math.Min(j1, (int)Math.Ceiling(Math.Max(y0, y1) - 0.5) - 1);
                for (int j = js; j <= je; j++)
                    crossings[j - j0].Add(x0 + (j + 0.5 - y0) / (y1 - y0) * (x1 - x0));
            }
        for (int j = j0; j <= j1; j++)
        {
            var xs = crossings[j - j0];
            xs.Sort();
            for (int p = 0; p + 1 < xs.Count; p += 2)
            {
                int iStart = Math.Max(0, (int)Math.Ceiling(xs[p] - 0.5));
                int iEnd = Math.Min(w - 1, (int)Math.Ceiling(xs[p + 1] - 0.5) - 1);
                for (int i = iStart; i <= iEnd; i++)
                    unitOf[j * w + i] = id;
            }
        }
    }

    /// <summary>Every polygon (island) of every province/region that reaches onto the map, in tile coordinates.</summary>
    private static List<Unit> LoadUnits(string path, MapGeo geo, int w, int h)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Province borders missing: run tools/download-data.ps1.", path);
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var units = new List<Unit>();
        foreach (var feature in doc.RootElement.GetProperty("features").EnumerateArray())
        {
            var geometry = feature.GetProperty("geometry");
            if (geometry.ValueKind != JsonValueKind.Object)
                continue;
            var props = feature.GetProperty("properties");
            string Prop(string key) => props.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            string name = Prop("name"), country = Prop("admin"), code = Prop("iso_3166_2");
            var coords = geometry.GetProperty("coordinates");
            var polygons = geometry.GetProperty("type").GetString() == "Polygon" ? new[] { coords } : coords.EnumerateArray().ToArray();
            foreach (var polygon in polygons)
            {
                var rings = new List<TileRing>();
                double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                foreach (var r in polygon.EnumerateArray())
                {
                    var xs = new List<double>();
                    var ys = new List<double>();
                    foreach (var point in r.EnumerateArray())
                    {
                        double x = geo.TileX(point[0].GetDouble(), w), y = geo.TileY(point[1].GetDouble(), h);
                        xs.Add(x);
                        ys.Add(y);
                        minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                        minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                    }
                    rings.Add(new TileRing(xs.ToArray(), ys.ToArray()));
                }
                if (maxX < 0 || maxY < 0 || minX > w || minY > h)
                    continue; // off the map
                units.Add(new Unit(name, country, code, rings, minX, minY, maxX, maxY));
            }
        }
        return units;
    }

    /// <summary>The whole closed outline: point legs as smooth curves, river legs as the river runs.</summary>
    private List<(double Lat, double Lon)> Outline(string riversPath)
    {
        // Each leg's points, rivers resolved to their real course.
        var legs = Edge.Select(leg => leg switch
        {
            EdgePoints p => (Points: p.Points.ToList(), Smooth: true),
            EdgeRiver r => (Points: RiverCourse(riversPath, r), Smooth: false),
            _ => throw new InvalidOperationException(),
        }).ToList();

        var outline = new List<(double Lat, double Lon)>();
        for (int l = 0; l < legs.Count; l++)
        {
            var (points, smooth) = legs[l];
            if (!smooth)
            {
                outline.AddRange(points);
                continue;
            }
            // A Catmull-Rom curve through the points, steered at each end by the neighbouring legs.
            // It runs on to the next leg's first point; the stretch from the previous leg's last
            // point is drawn here only when that leg is a river (a curve leg drew it already).
            var (prev, prevSmooth) = legs[(l - 1 + legs.Count) % legs.Count];
            var next = legs[(l + 1) % legs.Count].Points;
            var all = new List<(double Lat, double Lon)> { prev[^1] };
            all.AddRange(points);
            all.Add(next[0]);
            var before = prev.Count > 1 ? prev[^2] : prev[^1];
            var after = next.Count > 1 ? next[1] : next[0];
            for (int i = prevSmooth ? 1 : 0; i < all.Count - 1; i++)
            {
                var p0 = i == 0 ? before : all[i - 1];
                var p1 = all[i];
                var p2 = all[i + 1];
                var p3 = i + 2 < all.Count ? all[i + 2] : after;
                double length = Math.Sqrt((p2.Lat - p1.Lat) * (p2.Lat - p1.Lat) + (p2.Lon - p1.Lon) * (p2.Lon - p1.Lon));
                int steps = Math.Max(2, (int)(length / 0.05)); // a point every ~5 km
                for (int s = i == 0 ? 1 : 0; s < steps; s++)
                {
                    double t = (double)s / steps;
                    outline.Add((CatmullRom(p0.Lat, p1.Lat, p2.Lat, p3.Lat, t), CatmullRom(p0.Lon, p1.Lon, p2.Lon, p3.Lon, t)));
                }
            }
        }
        return outline;
    }

    private static double CatmullRom(double p0, double p1, double p2, double p3, double t) =>
        0.5 * (2 * p1 + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t * t + (-p0 + 3 * p1 - 3 * p2 + p3) * t * t * t);

    /// <summary>The named river's course between the points nearest <see cref="EdgeRiver.From"/> and <see cref="EdgeRiver.To"/>.</summary>
    private static List<(double Lat, double Lon)> RiverCourse(string path, EdgeRiver river)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var lines = new List<List<(double Lat, double Lon)>>();
        foreach (var feature in doc.RootElement.GetProperty("features").EnumerateArray())
        {
            var props = feature.GetProperty("properties");
            if (!props.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || name.GetString() != river.Name)
                continue;
            var geometry = feature.GetProperty("geometry");
            if (geometry.ValueKind != JsonValueKind.Object)
                continue;
            var coords = geometry.GetProperty("coordinates");
            if (geometry.GetProperty("type").GetString() == "LineString")
                lines.Add(ReadLine(coords));
            else
                foreach (var line in coords.EnumerateArray())
                    lines.Add(ReadLine(line));
        }
        if (lines.Count == 0)
            throw new InvalidDataException($"No river called {river.Name} in {path}.");

        // The longest line is the main course; Natural Earth may split it, so join pieces end to end first.
        var course = Join(lines).OrderByDescending(l => l.Count).First();
        int a = Nearest(course, river.From), b = Nearest(course, river.To);
        var part = course.GetRange(Math.Min(a, b), Math.Abs(b - a) + 1);
        if (a > b)
            part.Reverse();
        Console.WriteLine($"  edge follows the {river.Name}: {part.Count} points, from ({part[0].Lat:F2}, {part[0].Lon:F2}) to ({part[^1].Lat:F2}, {part[^1].Lon:F2})");
        return part;
    }

    /// <summary>Joins lines that end where another starts (within ~1 km) into longer ones.</summary>
    private static List<List<(double Lat, double Lon)>> Join(List<List<(double Lat, double Lon)>> lines)
    {
        static bool Same((double Lat, double Lon) p, (double Lat, double Lon) q) => Math.Abs(p.Lat - q.Lat) < 0.01 && Math.Abs(p.Lon - q.Lon) < 0.01;
        var pool = lines.Select(l => l.ToList()).ToList();
        bool joined = true;
        while (joined)
        {
            joined = false;
            for (int i = 0; i < pool.Count && !joined; i++)
                for (int j = 0; j < pool.Count && !joined; j++)
                {
                    if (i == j)
                        continue;
                    if (Same(pool[i][^1], pool[j][0]))
                    {
                        pool[i].AddRange(pool[j].Skip(1));
                        pool.RemoveAt(j);
                        joined = true;
                    }
                }
        }
        return pool;
    }

    private static int Nearest(List<(double Lat, double Lon)> line, (double Lat, double Lon) p)
    {
        int best = 0;
        double bestD = double.MaxValue;
        for (int i = 0; i < line.Count; i++)
        {
            double d = (line[i].Lat - p.Lat) * (line[i].Lat - p.Lat) + (line[i].Lon - p.Lon) * (line[i].Lon - p.Lon);
            if (d < bestD)
            {
                bestD = d;
                best = i;
            }
        }
        return best;
    }

    private static List<(double Lat, double Lon)> ReadLine(JsonElement points)
    {
        var line = new List<(double Lat, double Lon)>();
        foreach (var point in points.EnumerateArray())
            line.Add((point[1].GetDouble(), point[0].GetDouble()));
        return line;
    }
}
