using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Boundari.Map;
using Boundari.Sim;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// The front: dots hold the line, an attack makes a whole stretch of dots push, the stronger
/// side moves the line, gaps cave in, dots move with the line. Measured on flat farmland.
/// </summary>
public class FrontTests
{
    private readonly ITestOutputHelper _out;
    public FrontTests(ITestOutputHelper output) => _out = output;

    private static readonly MethodInfo SetOwnerMethod =
        typeof(Game).GetMethod("SetOwner", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo AddDotMethod =
        typeof(Game).GetMethod("AddDot", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly PropertyInfo StrengthProperty = typeof(Dot).GetProperty(nameof(Dot.Strength))!;

    private static void SetOwner(Game game, int tile, ushort owner) => SetOwnerMethod.Invoke(game, new object[] { tile, owner });

    private const int W = 200, H = 100;

    /// <summary>
    /// Flat farmland, A owning the columns west of <paramref name="frontX"/>; east of it is B's
    /// (or a Local's, or wilderness when <paramref name="eastKind"/> is null). No dots yet.
    /// </summary>
    private static (Game Game, Player A, Player? B) Field(int frontX, PlayerKind? eastKind = PlayerKind.Human)
    {
        var map = new GameMap(W, H, 1);
        for (int t = 0; t < map.TileCount; t++)
        {
            map.Terrain[t] = TerrainType.Plains;
            map.CoastField[t] = 1;
        }
        MapProcessing.LabelLandmasses(map);
        var game = new Game(map, 1, spawnPhaseTicks: 2);
        var a = game.AddPlayer("A");
        var b = eastKind is { } kind ? game.AddPlayer("B", kind) : null;
        game.Submit(new SpawnIntent(a.Id, map.Index(10, H / 2)));
        if (b is not null)
            game.Submit(new SpawnIntent(b.Id, map.Index(W - 10, H / 2)));
        for (int t = 0; t < 4; t++)
            game.Step();
        for (int t = 0; t < map.TileCount; t++)
            SetOwner(game, t, t % W < frontX ? a.Id : b?.Id ?? Game.Nobody);
        foreach (var d in game.Dots.ToList())
            game.Submit(new SellDotIntent(d.Owner.Id, d.Id));
        game.Step();
        Assert.Equal(GamePhase.Running, game.Phase);
        Assert.Empty(game.Dots);
        return (game, a, b);
    }

    private static Dot Place(Game game, Player p, int x, int y, int strength = 1)
    {
        var dot = (Dot)AddDotMethod.Invoke(game, new object[] { p, game.Map.Index(x, y) })!;
        StrengthProperty.SetValue(dot, strength);
        return dot;
    }

    /// <summary>A column of dots at x, every <paramref name="spacing"/> rows from row 2, within rows [fromY, toY) but not [skipFrom, skipTo).</summary>
    private static List<Dot> Line(Game game, Player p, int x, int strength = 1, int spacing = 6, int fromY = 0, int toY = H, int skipFrom = 0, int skipTo = 0)
    {
        var dots = new List<Dot>();
        for (int y = 2; y < H; y += spacing)
            if (y >= fromY && y < toY && (y < skipFrom || y >= skipTo))
                dots.Add(Place(game, p, x, y, strength));
        return dots;
    }

    /// <summary>How far east A's land reaches on this row.</summary>
    private static int Reach(Game game, Player a, int y)
    {
        int x = 0;
        while (x < W && game.Owner[game.Map.Index(x, y)] == a.Id)
            x++;
        return x;
    }

    private static void Run(Game game, int ticks)
    {
        for (int t = 0; t < ticks; t++)
            game.Step();
    }

    /// <summary>The field as text, every second row and column: A '.', B ' ', wilderness '~', A's dots 'a', B's 'b'.</summary>
    private string Picture(Game game, Player a, int fromX, int toX)
    {
        var dotAt = new Dictionary<int, Dot>();
        foreach (var d in game.Dots)
            dotAt[d.Tile] = d;
        var sb = new StringBuilder();
        for (int y = 0; y < H; y += 2)
        {
            for (int x = fromX; x < toX; x++)
            {
                int t = game.Map.Index(x, y);
                bool dot = dotAt.TryGetValue(t, out var d) || dotAt.TryGetValue(t + W, out d);
                sb.Append(dot ? (d!.Owner == a ? 'a' : 'b') : game.Owner[t] == a.Id ? '.' : game.Owner[t] == Game.Nobody ? '~' : ' ');
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    [Fact]
    public void WithoutAnAttack_TheLineHolds()
    {
        var (game, a, b) = Field(100);
        Line(game, a, 99);
        Line(game, b!, 100);
        int before = a.TileCount;
        Run(game, 300);
        Assert.Equal(before, a.TileCount);
        Assert.All(game.Dots, d => Assert.Contains(d.Tile, d.Owner.Border));
    }

    [Fact]
    public void OneDot_PushesIntoWilderness_AboutThreeTilesASecond_AndWalksWithTheLine()
    {
        var (game, a, _) = Field(20, eastKind: null);
        var dot = Place(game, a, 19, 50);
        game.Submit(new PushIntent(a.Id, dot.Id, game.Map.Index(20, 50)));
        Run(game, 100); // 10 s
        int depth = Reach(game, a, 50) - 20;
        int wide = Enumerable.Range(0, H).Count(y => Reach(game, a, y) - 20 >= depth / 2);
        _out.WriteLine($"10 s: {depth} tiles in, {wide} rows at least half as deep; dot at x {dot.Tile % W}, y {dot.Tile / W}");
        _out.WriteLine(Picture(game, a, 10, 70));
        Assert.InRange(depth, 20, 35);
        Assert.InRange(wide, 6, 24);
        Assert.True(dot.Tile % W >= 20 + depth - 3, "the dot keeps to the head of its attack");
        Assert.InRange(dot.Tile / W, 46, 54);

        // Stopped, the line stays where it is.
        game.Submit(new StopPushIntent(a.Id, dot.Id));
        Run(game, 2);
        int held = a.TileCount;
        Run(game, 50);
        Assert.Empty(game.Pushes);
        Assert.Equal(held, a.TileCount);
    }

    [Fact]
    public void AnAttack_TakesEveryDotOnThatStretch_ButNotDotsFarAlong()
    {
        var (game, a, b) = Field(100);
        var near = Line(game, a, 99, toY: 50); // rows 2..44, 6 apart
        var far = Place(game, a, 99, 80);           // 36 rows on: another stretch
        var inland = Place(game, a, 60, 20);        // not on the line at all
        game.Submit(new PushIntent(a.Id, near[0].Id, game.Map.Index(100, 2)));
        game.Step();
        var push = Assert.Single(game.Pushes);
        Assert.Equal(near.Select(d => d.Id), push.Dots.Select(d => d.Id));
        Assert.DoesNotContain(far, push.Dots);
        Assert.DoesNotContain(inland, push.Dots);

        // A second attack from the same stretch is the same attack.
        game.Submit(new PushIntent(a.Id, near[^1].Id, game.Map.Index(100, 44)));
        game.Step();
        Assert.Single(game.Pushes);

        // A dot that walks onto the stretch joins in.
        int stretch = game.Map.Index(99, 50);
        game.Submit(new MoveDotsIntent(a.Id, new[] { far.Id }, stretch));
        Run(game, 60);
        Assert.False(far.IsWalking);
        Assert.Contains(far, game.Pushes.Single().Dots);
    }

    [Fact]
    public void AnAttack_NeedsAStandingDotOnThatLine_AndNotAnAlly()
    {
        var (game, a, b) = Field(100);
        var inland = Place(game, a, 60, 20);
        var onLine = Place(game, a, 99, 50);
        game.Submit(new PushIntent(a.Id, inland.Id, game.Map.Index(100, 20)));  // too far from B
        game.Submit(new PushIntent(a.Id, onLine.Id, game.Map.Index(50, 50)));   // own land
        game.Submit(new PushIntent(b!.Id, onLine.Id, game.Map.Index(99, 50)));  // not B's dot
        game.Step();
        Assert.Empty(game.Pushes);

        game.Submit(new AllianceIntent(a.Id, b.Id));
        game.Submit(new AllianceIntent(b.Id, a.Id));
        Run(game, 2);
        Assert.True(a.IsAlliedWith(b));
        game.Submit(new PushIntent(a.Id, onLine.Id, game.Map.Index(100, 50)));
        game.Step();
        Assert.Empty(game.Pushes);
    }

    [Fact]
    public void EqualLines_Stalemate()
    {
        var (game, a, b) = Field(100);
        var mine = Line(game, a, 99);
        Line(game, b!, 100);
        game.Submit(new PushIntent(a.Id, mine[0].Id, game.Map.Index(100, 2)));
        int before = a.TileCount;
        Run(game, 300);
        _out.WriteLine($"A gained {a.TileCount - before} tiles in 30 s");
        Assert.Single(game.Pushes);
        Assert.Equal(before, a.TileCount);
    }

    [Fact]
    public void AStrongerLine_PushesThrough_AndTheDefendersFallBackAlive()
    {
        var (game, a, b) = Field(100);
        var mine = Line(game, a, 99, strength: 2);
        var theirs = Line(game, b!, 100);
        game.Submit(new PushIntent(a.Id, mine[0].Id, game.Map.Index(100, 2)));
        // (Only the first 8 s: as the attackers tire the line goes ragged, and from about
        // 10 s on defenders start being caught in surrounded pockets.)
        var depths = new List<double>();
        for (int s = 0; s < 4; s++)
        {
            Run(game, 20);
            depths.Add(Enumerable.Range(0, H).Average(y => Reach(game, a, y)) - 100);
        }
        _out.WriteLine("mean depth every 2 s: " + string.Join(", ", depths.Select(d => d.ToString("0.0"))));
        _out.WriteLine(Picture(game, a, 80, 200));
        Assert.True(depths[^1] >= 15, "level 2 against level 1 gets well through in 8 s");
        Assert.True(depths[^1] < 80, "but not across the whole field");
        Assert.Equal(theirs.Count, game.DotsOf(b!).Count);
        Assert.All(theirs, d => Assert.Equal(b!.Id, game.Owner[d.Tile]));
        Assert.All(theirs, d => Assert.Contains(d.Tile, b!.Border));
        Assert.All(mine, d => Assert.True(d.Tile % W >= 100, "the attackers walked forward with the line"));
    }

    [Fact]
    public void AGapInTheDefence_CavesIn()
    {
        var (game, a, b) = Field(100);
        var mine = Line(game, a, 99);
        Line(game, b!, 100, skipFrom: 38, skipTo: 62); // rows 38..61 unguarded
        game.Submit(new PushIntent(a.Id, mine[0].Id, game.Map.Index(100, 2)));
        Run(game, 100);
        int gap = Reach(game, a, 50) - 100, guarded = Reach(game, a, 14) - 100;
        _out.WriteLine($"10 s: {gap} tiles in at the gap, {guarded} where it is guarded");
        _out.WriteLine(Picture(game, a, 80, 160));
        Assert.True(gap >= 10, "the gap caves in");
        Assert.Equal(0, guarded);
    }

    [Fact]
    public void ADotPressedFromSeveralSides_HoldsLess()
    {
        var (game, a, b) = Field(100);
        // B holds a block sticking into A's land: x 100..199 plus a tongue x 90..99, rows 40..59,
        // and a one-tile spike at row 30 out to x 92.
        for (int y = 40; y < 60; y++)
            for (int x = 90; x < 100; x++)
                SetOwner(game, game.Map.Index(x, y), b!.Id);
        for (int x = 92; x < 100; x++)
            SetOwner(game, game.Map.Index(x, 30), b!.Id);
        var straight = Place(game, b!, 100, 10);
        var corner = Place(game, b!, 90, 40);
        var spike = Place(game, b!, 92, 30);
        var attacker = Place(game, a, 99, 10);
        game.Submit(new PushIntent(a.Id, attacker.Id, game.Map.Index(100, 10)));
        game.Step();
        Assert.Single(game.Pushes);
        Assert.Equal(1, game.SidesPressed(straight));
        Assert.Equal(2, game.SidesPressed(corner));
        Assert.Equal(3, game.SidesPressed(spike));
    }

    [Fact]
    public void Locals_HoldLikeALevel2Dot()
    {
        var (game, a, local) = Field(100, PlayerKind.Bot);
        var weak = Line(game, a, 99, toY: 38);
        var strong = Line(game, a, 99, strength: 2, fromY: 56);
        game.Submit(new PushIntent(a.Id, weak[0].Id, game.Map.Index(100, 2)));
        game.Submit(new PushIntent(a.Id, strong[0].Id, game.Map.Index(100, 98)));
        Run(game, 100);
        int weakDepth = Reach(game, a, 20) - 100, strongDepth = Reach(game, a, 80) - 100;
        _out.WriteLine($"10 s against Locals: level 1 line {weakDepth} tiles in, level 2 line {strongDepth}");
        Assert.Equal(0, weakDepth);
        Assert.True(strongDepth >= 10);
    }

    [Fact]
    public void ADotWithNoLandOfItsOwnNearby_IsCutOff_AndLost()
    {
        var (game, a, b) = Field(100);
        var pocket = Place(game, a, 50, 50);
        var pushedBack = Place(game, a, 99, 20);
        for (int y = 44; y <= 56; y++)
            for (int x = 44; x <= 56; x++)
                SetOwner(game, game.Map.Index(x, y), b!.Id);
        SetOwner(game, game.Map.Index(99, 20), b!.Id);
        game.Step();
        Assert.DoesNotContain(pocket, game.Dots);
        Assert.Contains(pushedBack, game.Dots);
        Assert.Equal(a.Id, game.Owner[pushedBack.Tile]);
        Assert.Equal(1, a.DotCount);
    }

    [Fact]
    public void PushingIntoSomeonesLand_Tires_WildernessDoesNot_AndRestingBringsItBack()
    {
        var (game, a, b) = Field(100);
        var attacker = Place(game, a, 99, 50);
        var defender = Place(game, b!, 100, 20);
        game.Submit(new PushIntent(a.Id, attacker.Id, game.Map.Index(100, 50)));
        Run(game, 150); // 15 s: half worn out
        _out.WriteLine($"15 s pushing: stamina {attacker.Stamina}, defender {defender.Stamina}");
        Assert.InRange(attacker.Stamina, DotRules.MaxStamina * 5 / 8 - 10, DotRules.MaxStamina * 5 / 8 + 10);
        Assert.Equal(DotRules.MaxStamina, defender.Stamina);
        Run(game, 200); // 35 s: worn out, but never below a quarter
        Assert.Equal(DotRules.MinStamina, attacker.Stamina);
        Assert.Equal(DotForce(1, DotRules.MinStamina), Game.DotForce(attacker));

        game.Submit(new StopPushIntent(a.Id, attacker.Id));
        Run(game, 300); // 30 s resting: half way back
        Assert.InRange(attacker.Stamina, DotRules.MaxStamina * 5 / 8 - 10, DotRules.MaxStamina * 5 / 8 + 10);
        Run(game, 300);
        Assert.Equal(DotRules.MaxStamina, attacker.Stamina);

        var (wild, c, _) = Field(20, eastKind: null);
        var explorer = Place(wild, c, 19, 50);
        wild.Submit(new PushIntent(c.Id, explorer.Id, wild.Map.Index(20, 50)));
        Run(wild, 100);
        Assert.Single(wild.Pushes);
        Assert.Equal(DotRules.MaxStamina, explorer.Stamina);
    }

    private static long DotForce(int strength, int stamina) => (long)strength * stamina * DotRules.DotForce / DotRules.MaxStamina;

    [Fact]
    public void StoppingAnAttack_HardensTheLineItHolds_For30Seconds()
    {
        var (game, a, b) = Field(100);
        var dot = Place(game, a, 99, 50);
        game.Submit(new PushIntent(a.Id, dot.Id, game.Map.Index(100, 50)));
        Run(game, 20);
        Assert.Empty(game.HardenedTiles);
        game.Submit(new StopPushIntent(a.Id, dot.Id));
        game.Step();
        Assert.Empty(game.Pushes);
        var hardened = game.HardenedTiles.ToList();
        _out.WriteLine($"{hardened.Count} tiles hardened");
        Assert.True(hardened.Count >= 10);
        Assert.All(hardened, t => Assert.Contains(t, a.Border));
        Assert.True(game.IsHardened(dot.Tile));
        Assert.False(game.IsHardened(game.Map.Index(99, 10)), "only the stretch the dots stood on");
        Run(game, 290);
        Assert.True(game.IsHardened(dot.Tile));
        Run(game, 20);
        Assert.Empty(game.HardenedTiles);
    }

    [Fact]
    public void AttacksOnHardenedLand_TakeThreeTimesAsLong()
    {
        // B digs in on a stretch (pushes, stops, then the dot is sold: the land stays
        // hardened); a lone dot of A's attacks there. Ticks until the tile in front falls:
        int TicksToTake(bool dugIn)
        {
            var (game, a, b) = Field(100);
            var digger = Place(game, b!, 100, 50);
            if (dugIn)
            {
                game.Submit(new PushIntent(b!.Id, digger.Id, game.Map.Index(99, 50)));
                game.Step();
                game.Submit(new StopPushIntent(b.Id, digger.Id));
                game.Step();
            }
            game.Submit(new SellDotIntent(b!.Id, digger.Id));
            game.Step();
            int front = game.Map.Index(100, 50);
            Assert.Equal(dugIn, game.IsHardened(front));
            var dot = Place(game, a, 99, 50);
            game.Submit(new PushIntent(a.Id, dot.Id, front));
            int ticks = 0;
            while (game.Owner[front] != a.Id && ticks < 200)
            {
                game.Step();
                ticks++;
            }
            return ticks;
        }
        int open = TicksToTake(false), hardened = TicksToTake(true);
        _out.WriteLine($"ticks to take the first tile: {open} open, {hardened} hardened");
        Assert.InRange(open, 3, 10);
        Assert.InRange(hardened, open * 5 / 2, open * 7 / 2);
    }

    [Fact]
    public void AClickSendsTheNearestDotFacingThatLand()
    {
        var (game, a, b) = Field(100);
        var north = Place(game, a, 99, 10);
        var south = Place(game, a, 99, 80);
        var inland = Place(game, a, 50, 45);
        Assert.Same(north, game.DotToPush(a, game.Map.Index(130, 30)));
        Assert.Same(south, game.DotToPush(a, game.Map.Index(101, 70)));
        Assert.Null(game.DotToPush(a, game.Map.Index(40, 45))); // own land
        game.Submit(new PushIntent(a.Id, south.Id, game.Map.Index(101, 70)));
        game.Step();
        Assert.True(game.IsPushingInto(south, b!.Id));
        Assert.False(game.IsPushingInto(north, b.Id));
        Assert.DoesNotContain(inland, game.Pushes.Single().Dots);
    }

    [Fact]
    public void SameAttacks_SameFront()
    {
        static (int, string) Play()
        {
            var (game, a, b) = Field(100);
            var mine = Line(game, a, 99, strength: 2);
            Line(game, b!, 100, skipFrom: 30, skipTo: 50);
            game.Submit(new PushIntent(a.Id, mine[3].Id, game.Map.Index(100, 20)));
            Run(game, 150);
            return (a.TileCount, string.Join(",", game.Dots.Select(d => d.Tile)));
        }
        Assert.Equal(Play(), Play());
    }

    [Fact]
    public void OnTheArdennes_AttacksStayCheap()
    {
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "Boundari.sln")) || !Directory.Exists(Path.Combine(dir, "src")))
            dir = Path.GetDirectoryName(dir)!;
        GameMap map;
        using (var file = File.OpenRead(Path.Combine(dir, "src", "Boundari", "maps", "ardennes.bmap")))
            map = MapFile.Read(file);
        var game = new Game(map, 1, spawnPhaseTicks: 2);
        int w = map.Width, h = map.Height;
        // Six commanders in a 3 x 2 grid of blocks with wilderness between them, each with
        // 60 level-1 dots: half pushing into wilderness, half into a neighbour.
        var players = new List<Player>();
        for (int i = 0; i < 6; i++)
        {
            var p = game.AddPlayer("P" + i);
            players.Add(p);
            game.Submit(new SpawnIntent(p.Id, map.Index(w / 6 + i % 3 * w / 3, h / 4 + i / 3 * h / 2)));
        }
        for (int t = 0; t < 4; t++)
            game.Step();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int t = map.Index(x, y), col = x * 3 / w, row = y * 2 / h;
                bool margin = x % (w / 3) > w / 3 - 60 && col < 2 || y % (h / 2) > h / 2 - 60 && row < 1;
                if (map.IsLand(t) && !margin)
                    SetOwner(game, t, players[row * 3 + col].Id);
            }
        foreach (var d in game.Dots.ToList())
            game.Submit(new SellDotIntent(d.Owner.Id, d.Id));
        game.Step();
        foreach (var p in players)
        {
            var border = p.Border.OrderBy(t => t).ToList();
            for (int i = 0; i < 60; i++)
                Place(game, p, border[i * border.Count / 60] % w, border[i * border.Count / 60] / w);
        }
        foreach (var d in game.Dots.ToList())
        {
            int x0 = d.Tile % w, y0 = d.Tile / w;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                    if (map.InBounds(x0 + dx, y0 + dy) && map.IsLand(map.Index(x0 + dx, y0 + dy)) && game.Owner[map.Index(x0 + dx, y0 + dy)] != d.Owner.Id)
                        game.Submit(new PushIntent(d.Owner.Id, d.Id, map.Index(x0 + dx, y0 + dy)));
        }
        game.Step();
        int pushing = game.Pushes.Sum(p => p.Dots.Count);
        var clock = Stopwatch.StartNew();
        long worst = 0;
        int before = players.Sum(p => p.TileCount);
        for (int t = 0; t < 300; t++)
        {
            long start = clock.ElapsedTicks;
            game.Step();
            worst = Math.Max(worst, clock.ElapsedTicks - start);
        }
        double avgMs = clock.Elapsed.TotalMilliseconds / 300, worstMs = worst * 1000.0 / Stopwatch.Frequency;
        _out.WriteLine($"{game.Pushes.Count} attacks, {pushing} dots pushing; 30 s: {players.Sum(p => p.TileCount) - before} tiles changed hands net; tick avg {avgMs:0.00} ms, worst {worstMs:0.00} ms");
        Assert.True(pushing > 60);
        // Alone: about 5 ms a tick, worst under 20 ms. Loose limits, as other tests share the CPU.
        Assert.True(avgMs < 50 && worstMs < 400, $"tick avg {avgMs:0.0} ms, worst {worstMs:0.0} ms");
    }
}
