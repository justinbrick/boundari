using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Boundari.Map;
using Boundari.Sim;
using Xunit;
using Xunit.Abstractions;

/// <summary>Dots: buying (they walk from home to a border and spread out), selling, upgrading, moving, the cap.</summary>
public class DotTests
{
    private readonly ITestOutputHelper _out;
    public DotTests(ITestOutputHelper output) => _out = output;

    private static readonly MethodInfo SetOwnerMethod =
        typeof(Game).GetMethod("SetOwner", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static void SetOwner(Game game, int tile, ushort owner) => SetOwnerMethod.Invoke(game, new object[] { tile, owner });
    private static void SetGold(Player p, long gold) => typeof(Player).GetProperty(nameof(Player.Gold))!.SetValue(p, gold);

    private const long Income = Balance.PlayerGoldPerTick; // Funds earned during each step

    private static GameMap PlainsMap()
    {
        var map = MapGenerator.Generate(21, new MapGenSettings { Width = 400, Height = 250, MinIslandTiles = 8, MinLakeTiles = 30 });
        for (int t = 0; t < map.TileCount; t++)
            if (map.IsLand(t))
                map.Terrain[t] = TerrainType.Plains;
        return map;
    }

    private static int[] BiggestLandmass(GameMap map)
    {
        int biggest = Enumerable.Range(0, map.LandmassSizes.Length).OrderByDescending(i => map.LandmassSizes[i]).First();
        return Enumerable.Range(0, map.TileCount).Where(t => map.LandmassId[t] == biggest).ToArray();
    }

    /// <summary>A owns the west of the biggest landmass and B the east, the match is running, and A has no dots yet.</summary>
    private static (Game Game, Player A, Player B) Setup()
    {
        var map = PlainsMap();
        var game = new Game(map, 1, spawnPhaseTicks: 2);
        var a = game.AddPlayer("A");
        var b = game.AddPlayer("B");
        int[] land = BiggestLandmass(map);
        game.Submit(new SpawnIntent(a.Id, land[0]));
        game.Submit(new SpawnIntent(b.Id, land[^1]));
        for (int t = 0; t < 4; t++)
            game.Step();
        int w = map.Width, mid = (int)land.Select(t => t % w).Average();
        foreach (int t in land)
            SetOwner(game, t, t % w >= mid ? b.Id : a.Id);
        foreach (var d in game.DotsOf(a))
            game.Submit(new SellDotIntent(a.Id, d.Id));
        game.Step();
        Assert.Equal(GamePhase.Running, game.Phase);
        Assert.Equal(0, a.DotCount);
        return (game, a, b);
    }

    private static int Chebyshev(GameMap map, int s, int t) =>
        Math.Max(Math.Abs(s % map.Width - t % map.Width), Math.Abs(s / map.Width - t / map.Width));

    /// <summary>A's border tile nearest the given row (on the A/B front).</summary>
    private static int FrontTile(Game game, Player a, int row) =>
        a.Border.OrderBy(t => Math.Abs(t / game.Map.Width - row)).ThenBy(t => t).First();

    /// <summary>Steps until none of the player's dots is walking, checking each tick that every dot is on its owner's land. Returns the ticks taken.</summary>
    private static int WalkAll(Game game, Player p, int maxTicks = 2000)
    {
        for (int t = 1; t <= maxTicks; t++)
        {
            game.Step();
            foreach (var d in game.DotsOf(p))
                Assert.Equal(p.Id, game.Owner[d.Tile]);
            if (game.DotsOf(p).All(d => !d.IsWalking))
                return t;
        }
        throw new Exception("dots still walking");
    }

    [Fact]
    public void Commanders_StartWithTenDotsJustInsideTheirBorder_LocalsWithNone()
    {
        var map = PlainsMap();
        var game = new Game(map, 1, spawnPhaseTicks: 50);
        var me = game.AddPlayer("Me");
        var local = game.AddPlayer("Local", PlayerKind.Bot);
        int[] land = BiggestLandmass(map);
        game.Submit(new SpawnIntent(me.Id, land[land.Length / 3]));
        game.Submit(new SpawnIntent(local.Id, land[^1]));
        game.Step();

        void Check()
        {
            var dots = game.DotsOf(me);
            Assert.Equal(DotRules.FreeAtStart, dots.Count);
            Assert.Equal(dots.Count, me.DotCount);
            Assert.All(dots, d =>
            {
                Assert.InRange(game.DistanceToLine(d), 1, DotRules.StandBack);
                Assert.False(d.IsWalking);
                Assert.Equal(1, d.Strength);
                Assert.Equal(DotRules.MaxStamina, d.Stamina);
                Assert.Equal(0, d.Paid);
            });
        }
        Check();
        Assert.Empty(game.DotsOf(local));

        // Moving the spawn moves the dots with it.
        int moved = land[land.Length * 2 / 3];
        game.Submit(new SpawnIntent(me.Id, moved));
        game.Step();
        Check();
        Assert.All(game.DotsOf(me), d => Assert.True(Chebyshev(map, d.Tile, moved) <= game.Rules.SpawnRadius + 1));
    }

    [Fact]
    public void BoughtDots_WalkFromHomeToTheBorder_AndSpreadAlongIt()
    {
        var (game, a, _) = Setup();
        var map = game.Map;
        SetGold(a, 4 * DotRules.Price + 10_000);
        int front = FrontTile(game, a, map.Height / 2);
        game.Submit(new BuyDotsIntent(a.Id, 6, front));
        game.Step();

        var dots = game.DotsOf(a);
        Assert.Equal(4, dots.Count); // Funds for four
        Assert.Equal(10_000 + Income, a.Gold);
        Assert.All(dots, d => Assert.True(d.IsWalking, "new dots walk from home"));
        Assert.All(dots, d => Assert.Equal(a.Id, game.Owner[d.Destination]));
        Assert.All(dots, d => Assert.True(a.Border.Min(t => Chebyshev(map, t, d.Destination)) == DotRules.StandBack - 1, "they stand just behind the border (3 tiles from the line, 2 from its last tile)"));
        Assert.Contains(dots, d => Chebyshev(map, d.Destination, front) <= DotRules.StandBack + 2);
        var ends = dots.Select(d => d.Destination).ToList();
        for (int i = 0; i < ends.Count; i++)
            for (int j = i + 1; j < ends.Count; j++)
                Assert.True(Chebyshev(map, ends[i], ends[j]) >= game.Rules.DotSpacing, "spread along the border");

        int farthest = dots.Max(d => Chebyshev(map, d.Tile, d.Destination));
        int ticks = WalkAll(game, a);
        Assert.True(ticks >= farthest - 1, $"walked {farthest} tiles in {ticks} ticks: faster than 10 tiles a second");
        Assert.All(game.DotsOf(a), d => Assert.Equal(d.Destination, d.Tile));
    }

    [Fact]
    public void Buying_StopsAtTheCap()
    {
        var (game, a, _) = Setup();
        SetGold(a, 100 * DotRules.Price);
        int front = FrontTile(game, a, game.Map.Height / 2);
        game.Submit(new BuyDotsIntent(a.Id, 50, front));
        game.Step();
        Assert.Equal(DotRules.StartCap, a.DotCount);
        Assert.Equal((100 - DotRules.StartCap) * DotRules.Price + Income, a.Gold);
        game.Submit(new BuyDotsIntent(a.Id, 1, front));
        game.Step();
        Assert.Equal(DotRules.StartCap, a.DotCount);
    }

    [Fact]
    public void Upgrading_CostsMoreEachLevel_UpToFour_AndSellingReturnsHalf()
    {
        var (game, a, b) = Setup();
        int front = FrontTile(game, a, game.Map.Height / 2);
        SetGold(a, DotRules.Price);
        game.Submit(new BuyDotsIntent(a.Id, 1, front));
        game.Step();
        var dot = Assert.Single(game.DotsOf(a));

        SetGold(a, 10_000); // not enough for an upgrade
        game.Submit(new UpgradeDotIntent(a.Id, dot.Id));
        game.Step();
        Assert.Equal(1, dot.Strength);

        SetGold(a, 25_000 + 50_000 + 100_000);
        for (int i = 0; i < 4; i++) // the fourth does nothing
            game.Submit(new UpgradeDotIntent(a.Id, dot.Id));
        game.Step();
        Assert.Equal(DotRules.MaxStrength, dot.Strength);
        Assert.Equal(Income, a.Gold);
        Assert.Equal(DotRules.Price + 175_000, dot.Paid);

        game.Submit(new SellDotIntent(b.Id, dot.Id)); // not theirs to sell
        game.Step();
        Assert.Equal(1, a.DotCount);

        game.Submit(new SellDotIntent(a.Id, dot.Id));
        game.Step();
        Assert.Equal(0, a.DotCount);
        Assert.Empty(game.DotsOf(a));
        Assert.Equal(3 * Income + (DotRules.Price + 175_000) / 2, a.Gold);
    }

    [Fact]
    public void MovedDots_WalkThroughOwnLand_ToTheNewStretchOfBorder()
    {
        var (game, a, _) = Setup();
        var map = game.Map;
        int rows = a.Border.Max(t => t / map.Width) - a.Border.Min(t => t / map.Width);
        int top = a.Border.Min(t => t / map.Width), here = FrontTile(game, a, top + rows / 4), there = FrontTile(game, a, top + rows * 3 / 4);
        SetGold(a, 3 * DotRules.Price);
        game.Submit(new BuyDotsIntent(a.Id, 3, here));
        game.Step();
        WalkAll(game, a);

        var ids = game.DotsOf(a).Select(d => d.Id).ToArray();
        game.Submit(new MoveDotsIntent(a.Id, ids, there));
        game.Step();
        var dots = game.DotsOf(a);
        Assert.All(dots, d => Assert.True(d.IsWalking));
        Assert.Contains(dots, d => Chebyshev(map, d.Destination, there) <= DotRules.StandBack + 2);
        Assert.All(dots, d => Assert.True(Chebyshev(map, d.Destination, there) <= 3 * game.Rules.DotSpacing));
        int farthest = dots.Max(d => Chebyshev(map, d.Tile, d.Destination));
        int ticks = WalkAll(game, a);
        Assert.True(ticks >= farthest - 1);
        Assert.All(game.DotsOf(a), d => Assert.Equal(d.Destination, d.Tile));
    }

    /// <summary>A's front tiles a quarter and three quarters down its border (rows apart), with four dots bought in between and walked there.</summary>
    private static (int From, int To, int[] Ids) FourDotsOnTheFront(Game game, Player a)
    {
        var map = game.Map;
        int rows = a.Border.Max(t => t / map.Width) - a.Border.Min(t => t / map.Width);
        int top = a.Border.Min(t => t / map.Width);
        int from = FrontTile(game, a, top + rows / 4), to = FrontTile(game, a, top + rows * 3 / 4);
        SetGold(a, 4 * DotRules.Price);
        game.Submit(new BuyDotsIntent(a.Id, 4, FrontTile(game, a, top + rows / 2)));
        game.Step();
        WalkAll(game, a);
        return (from, to, game.DotsOf(a).Select(d => d.Id).ToArray());
    }

    [Fact]
    public void SpreadDots_StandEvenlyFromOnePointToTheOther_InTheirOrder()
    {
        var (game, a, _) = Setup();
        var map = game.Map;
        var (from, to, ids) = FourDotsOnTheFront(game, a);
        var before = game.DotsOf(a).OrderBy(d => d.Tile / map.Width).Select(d => d.Id).ToList();

        game.Submit(new SpreadDotsIntent(a.Id, ids, from, to));
        game.Step();
        var dots = game.DotsOf(a).OrderBy(d => d.Destination / map.Width).ToList();
        Assert.Equal(before, dots.Select(d => d.Id).ToList()); // nobody crosses over
        Assert.True(Chebyshev(map, dots[0].Destination, from) <= DotRules.StandBack + 2, "the first stands at one end");
        Assert.True(Chebyshev(map, dots[^1].Destination, to) <= DotRules.StandBack + 2, "the last at the other");
        var gaps = Enumerable.Range(1, 3).Select(i => Chebyshev(map, dots[i - 1].Destination, dots[i].Destination)).ToList();
        _out.WriteLine($"gaps {string.Join(", ", gaps)}, stretch {Chebyshev(map, from, to)} tiles");
        Assert.True(gaps.Max() - gaps.Min() <= 4, $"evenly spread: gaps {string.Join(", ", gaps)}");
        WalkAll(game, a);
        Assert.All(game.DotsOf(a), d => Assert.Equal(d.Destination, d.Tile));
    }

    [Fact]
    public void SpreadDots_ShiftedEnds_SlideTheGroupAlongTheBorder()
    {
        var (game, a, _) = Setup();
        var map = game.Map;
        var (from, to, ids) = FourDotsOnTheFront(game, a);
        game.Submit(new SpreadDotsIntent(a.Id, ids, from, to));
        game.Step();
        WalkAll(game, a);
        var was = game.DotsOf(a).ToDictionary(d => d.Id, d => d.Tile / map.Width);

        const int Rows = 10;
        game.Submit(new SpreadDotsIntent(a.Id, ids, from + Rows * map.Width, to + Rows * map.Width)); // off the border: snapped to it
        game.Step();
        WalkAll(game, a);
        Assert.All(game.DotsOf(a), d => Assert.InRange(d.Tile / map.Width - was[d.Id], Rows - 3, Rows + 3));
    }

    [Fact]
    public void SpreadDots_OnlyMovesTheSendersOwnDots()
    {
        var (game, a, b) = Setup();
        var (from, to, ids) = FourDotsOnTheFront(game, a);
        var tiles = game.DotsOf(a).Select(d => d.Tile).ToList();
        game.Submit(new SpreadDotsIntent(b.Id, ids, from, to));
        game.Step();
        Assert.All(game.DotsOf(a), d => Assert.False(d.IsWalking));
        Assert.Equal(tiles, game.DotsOf(a).Select(d => d.Tile).ToList());
    }

    [Fact]
    public void ADotsGroup_IsTheStandingDotsChainedToIt_AndBuyPreviewsShowWhereDotsWillStand()
    {
        var (game, a, _) = Setup();
        var map = game.Map;
        int front = FrontTile(game, a, map.Height / 2);
        SetGold(a, 10 * DotRules.Price);
        var preview = game.BuySpotsPreview(a, front, 3);
        game.Submit(new BuyDotsIntent(a.Id, 3, front));
        game.Step();
        Assert.Equal(preview.OrderBy(t => t), game.DotsOf(a).Select(d => d.Destination).OrderBy(t => t));
        Assert.Single(game.DotGroup(game.DotsOf(a)[0])); // still walking: on its own
        WalkAll(game, a);
        var dots = game.DotsOf(a);
        Assert.Equal(dots.Select(d => d.Id), game.DotGroup(dots[1]).Select(d => d.Id)); // spaced along one stretch: one group

        int bottom = a.Border.Max(t => t / map.Width);
        game.Submit(new MoveDotsIntent(a.Id, new[] { dots[0].Id }, FrontTile(game, a, bottom - 2)));
        game.Step();
        WalkAll(game, a);
        Assert.Single(game.DotGroup(dots[0]));
        Assert.Equal(2, game.DotGroup(dots[1]).Count);
    }

    [Fact]
    public void Dots_OnlyGoToTheBuyersOwnBorder()
    {
        var (game, a, b) = Setup();
        SetGold(a, 10 * DotRules.Price);
        int inland = Enumerable.Range(0, game.Map.TileCount).First(t => game.Owner[t] == a.Id && !a.Border.Contains(t));
        int enemy = FrontTile(game, b, game.Map.Height / 2);
        game.Submit(new BuyDotsIntent(a.Id, 2, inland));
        game.Submit(new BuyDotsIntent(a.Id, 2, enemy));
        game.Step();
        Assert.Equal(0, a.DotCount);
        Assert.Equal(10 * DotRules.Price + Income, a.Gold);
    }

    [Fact]
    public void DotsOfAPlayerWithNoLand_AreGone()
    {
        var (game, a, b) = Setup();
        SetGold(a, 2 * DotRules.Price);
        game.Submit(new BuyDotsIntent(a.Id, 2, FrontTile(game, a, game.Map.Height / 2)));
        game.Step();
        Assert.Equal(2, a.DotCount);
        for (int t = 0; t < game.Map.TileCount; t++)
            if (game.Owner[t] == a.Id)
                SetOwner(game, t, b.Id);
        game.Step();
        Assert.Equal(0, a.DotCount);
        Assert.Empty(game.DotsOf(a));
    }

    [Fact]
    public void SameIntents_SameDots()
    {
        static List<(int, int, int)> Play()
        {
            var (game, a, _) = Setup();
            SetGold(a, 10 * DotRules.Price);
            game.Submit(new BuyDotsIntent(a.Id, 10, FrontTile(game, a, game.Map.Height / 2)));
            for (int t = 0; t < 30; t++)
                game.Step();
            return game.Dots.Select(d => (d.Id, d.Tile, d.Destination)).ToList();
        }
        Assert.Equal(Play(), Play());
    }

    [Fact]
    public void OnTheArdennes_ABigCountrysDots_FindTheirWayQuickly()
    {
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "Boundari.sln")) || !Directory.Exists(Path.Combine(dir, "src")))
            dir = Path.GetDirectoryName(dir)!;
        GameMap map;
        using (var file = File.OpenRead(Path.Combine(dir, "src", "Boundari", "maps", "ardennes.bmap")))
            map = MapFile.Read(file);
        var game = new Game(map, 1, spawnPhaseTicks: 2);
        var a = game.AddPlayer("A");
        int w = map.Width, h = map.Height;
        game.Submit(new SpawnIntent(a.Id, map.Index(w / 2, h / 2)));
        for (int t = 0; t < 4; t++)
            game.Step();
        // A holds a 700-tile square (about half the map), the rest is wilderness.
        for (int y = h / 2 - 350; y < h / 2 + 350; y++)
            for (int x = w / 2 - 350; x < w / 2 + 350; x++)
                if (map.IsLand(map.Index(x, y)))
                    SetOwner(game, map.Index(x, y), a.Id);
        foreach (var d in game.DotsOf(a))
            game.Submit(new SellDotIntent(a.Id, d.Id));
        game.Step();

        SetGold(a, 10 * DotRules.Price);
        int west = a.Border.OrderBy(t => t % w).ThenBy(t => Math.Abs(t / w - h / 2)).First();
        int east = a.Border.OrderByDescending(t => t % w).ThenBy(t => Math.Abs(t / w - h / 2)).First();
        var clock = Stopwatch.StartNew();
        game.Submit(new BuyDotsIntent(a.Id, 10, west));
        game.Step();
        long buyMs = clock.ElapsedMilliseconds;
        Assert.Equal(10, a.DotCount);
        int ticks = WalkAll(game, a, 5000);

        clock.Restart();
        game.Submit(new MoveDotsIntent(a.Id, game.DotsOf(a).Select(d => d.Id).ToArray(), east));
        game.Step();
        long moveMs = clock.ElapsedMilliseconds;
        int across = WalkAll(game, a, 5000);
        _out.WriteLine($"buy 10: {buyMs} ms, walk to the west border {ticks} ticks; move 10 across: {moveMs} ms, {across} ticks");
        Assert.True(across >= 650, "crossing 700 tiles takes over a minute of game time");
        Assert.True(buyMs < 2000 && moveMs < 2000, $"buy {buyMs} ms, move {moveMs} ms");
    }
}
