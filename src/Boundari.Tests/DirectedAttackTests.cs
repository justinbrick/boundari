using System;
using System.Linq;
using System.Reflection;
using Boundari.Map;
using Boundari.Sim;
using Xunit;
using Xunit.Abstractions;

/// <summary>Directed attacks: an arrow gives an attack a direction and a shape, never an area.</summary>
public class DirectedAttackTests
{
    private readonly ITestOutputHelper _out;
    public DirectedAttackTests(ITestOutputHelper output) => _out = output;

    private static readonly MethodInfo SetOwnerMethod =
        typeof(Game).GetMethod("SetOwner", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static void SetOwner(Game game, int tile, ushort owner) => SetOwnerMethod.Invoke(game, new object[] { tile, owner });
    private static void SetTroops(Player p, long troops) => typeof(Player).GetProperty(nameof(Player.Troops))!.SetValue(p, troops);

    /// <summary>A owns the west of the biggest landmass and B the east (border at x = Mid), all plains; the match is running.</summary>
    private static (Game Game, Player A, Player B, int Mid, int MidRow) Setup()
    {
        var map = MapGenerator.Generate(21, new MapGenSettings { Width = 400, Height = 250, MinIslandTiles = 8, MinLakeTiles = 30 });
        for (int t = 0; t < map.TileCount; t++)
            if (map.IsLand(t))
                map.Terrain[t] = TerrainType.Plains;
        var game = new Game(map, 1, spawnPhaseTicks: 2) { HatchedLand = false };
        var a = game.AddPlayer("A");
        var b = game.AddPlayer("B");
        int biggest = Enumerable.Range(0, map.LandmassSizes.Length).OrderByDescending(i => map.LandmassSizes[i]).First();
        int[] land = Enumerable.Range(0, map.TileCount).Where(t => map.LandmassId[t] == biggest).ToArray();
        game.Submit(new SpawnIntent(a.Id, land[0]));
        game.Submit(new SpawnIntent(b.Id, land[^1]));
        for (int t = 0; t < 4; t++)
            game.Step();
        int w = map.Width, mid = (int)land.Select(t => t % w).Average();
        foreach (int t in land)
            SetOwner(game, t, t % w >= mid ? b.Id : a.Id);
        // The row with the most of B's land just past the border: the arrows aim along it.
        int midRow = Enumerable.Range(0, map.Height)
            .OrderByDescending(y => Enumerable.Range(mid, 60).Count(x => x < w && game.Owner[map.Index(x, y)] == b.Id)).First();
        SetTroops(a, 400_000);
        SetTroops(b, 100_000);
        return (game, a, b, mid, midRow);
    }

    /// <summary>How deep past the border A got (furthest column), and how many rows A holds land in past the border.</summary>
    private static (int Depth, int Rows, int Taken) Reach(Game game, Player a, int mid)
    {
        var map = game.Map;
        int depth = 0, taken = 0;
        var rows = new System.Collections.Generic.HashSet<int>();
        for (int y = 0; y < map.Height; y++)
            for (int x = mid; x < map.Width; x++)
                if (game.Owner[map.Index(x, y)] == a.Id)
                {
                    depth = Math.Max(depth, x - mid + 1);
                    rows.Add(y);
                    taken++;
                }
        return (depth, rows.Count, taken);
    }

    private sealed record Result(int Depth, int Rows, int Taken, long TroopsUsed)
    {
        public double AverageDepth => (double)Taken / Math.Max(1, Rows);
        public double TroopsPerTile => (double)TroopsUsed / Math.Max(1, Taken);
    }

    /// <summary>Half of A's troops at B, as a normal attack (no arrow) or along the middle row, for 2.5 s.</summary>
    private Result Run(int? thinPermille, int ticks = 25)
    {
        var (game, a, b, mid, row) = Setup();
        var map = game.Map;
        if (thinPermille is { } thin)
            game.Submit(new DirectedAttackIntent(a.Id, b.Id, 50, map.Index(mid - 20, row), map.Index(mid + 20, row), thin));
        else
            game.Submit(new AttackIntent(a.Id, b.Id, 50));
        game.Step();
        long sent = game.Attacks.Where(x => x.Attacker == a).Sum(x => x.Troops);
        for (int t = 1; t < ticks; t++)
            game.Step();
        var (depth, rows, taken) = Reach(game, a, mid);
        var result = new Result(depth, rows, taken, sent - game.Attacks.Where(x => x.Attacker == a).Sum(x => x.Troops));
        _out.WriteLine($"{(thinPermille is { } p ? $"arrow thin={p}" : "normal")}: depth {depth} (average {result.AverageDepth:F1}), rows {rows}, tiles {taken}, {result.TroopsPerTile:F1} troops a tile");
        return result;
    }

    [Fact]
    public void ThinArrow_PushesDeeperAndNarrower_ButPaysMore()
    {
        var normal = Run(null);
        var thin = Run(1000);
        Assert.True(thin.Depth > normal.Depth * 2, $"thin depth {thin.Depth} vs normal {normal.Depth}");
        Assert.True(thin.AverageDepth > normal.AverageDepth * 1.2, $"thin average depth {thin.AverageDepth} vs normal {normal.AverageDepth}");
        Assert.True(thin.Rows < normal.Rows / 2, $"thin rows {thin.Rows} vs normal {normal.Rows}");
        Assert.True(thin.Taken < normal.Taken, $"thin tiles {thin.Taken} vs normal {normal.Taken}");
        Assert.True(thin.TroopsPerTile > normal.TroopsPerTile * 1.15, $"thin {thin.TroopsPerTile} troops a tile vs normal {normal.TroopsPerTile}");
    }

    [Fact]
    public void FlatArrow_StaysWide_SlowerButCheaper()
    {
        var normal = Run(null);
        var flat = Run(0);
        Assert.True(flat.Rows >= normal.Rows * 0.9, $"flat rows {flat.Rows} vs normal {normal.Rows}");
        Assert.True(flat.Taken < normal.Taken, $"flat tiles {flat.Taken} vs normal {normal.Taken}");
        Assert.True(flat.TroopsPerTile < normal.TroopsPerTile * 0.9, $"flat {flat.TroopsPerTile} troops a tile vs normal {normal.TroopsPerTile}");
    }

    [Fact]
    public void LongerArrows_PushDeeperAndNarrower()
    {
        Result? last = null;
        foreach (int thin in new[] { 0, 250, 500, 750, 1000 })
        {
            var r = Run(thin);
            if (last is not null)
            {
                Assert.True(r.AverageDepth > last.AverageDepth, $"thin={thin}: average depth {r.AverageDepth} vs {last.AverageDepth}");
                Assert.True(r.Rows <= last.Rows, $"thin={thin}: rows {r.Rows} vs {last.Rows}");
            }
            last = r;
        }
    }

    [Fact]
    public void ThinArrow_StillTakesTheSides_Later()
    {
        var (game, a, b, mid, row) = Setup();
        SetTroops(a, 2_000_000);
        var map = game.Map;
        game.Submit(new DirectedAttackIntent(a.Id, b.Id, 100, map.Index(mid - 20, row), map.Index(mid + 20, row), 1000));
        bool farSide = false;
        for (int t = 0; t < 1200 && !farSide; t++)
        {
            game.Step();
            for (int y = 0; y < map.Height && !farSide; y++)
                if (Math.Abs(y - row) > 60 && game.Owner[map.Index(mid + 1, y)] == a.Id)
                    farSide = true;
        }
        Assert.True(farSide, "a thin arrow never reached land far off its line");
    }

    [Fact]
    public void DirectedAttacks_DontMerge()
    {
        var (game, a, b, mid, row) = Setup();
        var map = game.Map;
        game.Submit(new AttackIntent(a.Id, b.Id, 20));
        game.Submit(new DirectedAttackIntent(a.Id, b.Id, 20, map.Index(mid - 20, row), map.Index(mid + 20, row), 500));
        game.Submit(new DirectedAttackIntent(a.Id, b.Id, 20, map.Index(mid - 20, row), map.Index(mid + 20, row - 10), 500));
        game.Submit(new AttackIntent(a.Id, b.Id, 20));
        game.Step();
        var mine = game.Attacks.Where(x => x.Attacker == a).ToList();
        Assert.Equal(3, mine.Count);
        Assert.Equal(2, mine.Count(x => x.Arrow is not null));
    }

    [Fact]
    public void Classic_TurnsArrowsIntoNormalAttacks()
    {
        var (game, a, b, mid, row) = Setup();
        game.DirectedAttacks = false;
        var map = game.Map;
        game.Submit(new DirectedAttackIntent(a.Id, b.Id, 20, map.Index(mid - 20, row), map.Index(mid + 20, row), 1000));
        game.Step();
        Assert.Single(game.Attacks, x => x.Attacker == a && x.Arrow is null);
    }
}
