using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Boundari.Map;
using Boundari.Sim;
using Xunit;

public class NukeTests
{
    private static readonly MethodInfo SetOwnerMethod =
        typeof(Game).GetMethod("SetOwner", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static void SetOwner(Game game, int tile, ushort owner) => SetOwnerMethod.Invoke(game, new object[] { tile, owner });

    /// <summary>A small map's biggest landmass, west half A's and east half B's; both cheating, past the no-bomb window.</summary>
    private static (Game Game, Player A, Player B, int[] Land) Setup()
    {
        var map = MapGenerator.Generate(21, new MapGenSettings { Width = 400, Height = 250, MinIslandTiles = 8, MinLakeTiles = 30 });
        var game = new Game(map, 1, spawnPhaseTicks: 2);
        var a = game.AddPlayer("A");
        var b = game.AddPlayer("B");
        int biggest = Enumerable.Range(0, map.LandmassSizes.Length).OrderByDescending(i => map.LandmassSizes[i]).First();
        int[] land = Enumerable.Range(0, map.TileCount).Where(t => map.LandmassId[t] == biggest).ToArray();
        game.Submit(new SpawnIntent(a.Id, land[0]));
        game.Submit(new SpawnIntent(b.Id, land[^1]));
        Wait(game, 4);
        int midX = (int)land.Select(t => t % map.Width).Average();
        foreach (int t in land)
            SetOwner(game, t, t % map.Width >= midX ? b.Id : a.Id);
        game.SetCheats(a, infiniteGold: true, instantBuild: true);
        game.SetCheats(b, infiniteGold: true, instantBuild: true);
        Wait(game, NukeRules.SpawnImmunityTicks);
        return (game, a, b, land);
    }

    [Fact]
    public void AimingPreview_ShowsWhereEnemyFlakWouldStopTheBomb()
    {
        var (game, a, b, _) = Setup();
        Build(game, a, BuildingType.Silo, t => game.Owner[t] == a.Id);
        int target = DeepInside(game, b);
        var clear = game.PreviewNuke(a, NukeType.Moab, target);
        Assert.NotNull(clear);
        Assert.Equal(-1, clear!.InterceptIndex);
        Assert.False(clear.BetraysAlly);

        // B puts Flak right where the bomb comes down: the preview now marks a point near the end.
        Build(game, b, BuildingType.Sam, t => game.Owner[t] == b.Id && Math.Abs(t % game.Map.Width - target % game.Map.Width) + Math.Abs(t / game.Map.Width - target / game.Map.Width) < 12);
        var blocked = game.PreviewNuke(a, NukeType.Moab, target)!;
        Assert.InRange(blocked.InterceptIndex, 0, blocked.X.Length - 1);
        Assert.True(blocked.Targetable[blocked.InterceptIndex]);
    }

    [Fact]
    public void FlippedArc_BowsTheOtherWay()
    {
        var (up, _) = NukeRules.Arc(10, 100, 200, 100, 250, 3);
        var (down, downY) = NukeRules.Arc(10, 100, 200, 100, 250, 3, up: false);
        Assert.Equal(up.Length, down.Length);
        Assert.True(downY[downY.Length / 2] > 100); // below the straight line (y grows down the map)
    }

    private static void Wait(Game game, int ticks)
    {
        for (int t = 0; t < ticks; t++)
            game.Step();
    }

    private static int Build(Game game, Player p, BuildingType type, Func<int, bool> where)
    {
        int tile = Enumerable.Range(0, game.Map.TileCount).First(t => where(t) && game.CanBuild(p, type, t));
        game.Submit(new BuildIntent(p.Id, type, tile));
        Wait(game, 2);
        Assert.True(game.BuildingAt(tile)?.IsReady(game.Tick), $"{type} should be finished instantly with the cheat");
        return tile;
    }

    /// <summary>The player's tile with the most of their own land around it (the most "inland" spot).</summary>
    private static int DeepInside(Game game, Player p)
    {
        int w = game.Map.Width;
        int Depth(int t)
        {
            int x = t % w, y = t / w, r = 1;
            for (; r < 60; r++)
                foreach (var (dx, dy) in new[] { (r, 0), (-r, 0), (0, r), (0, -r) })
                    if (!game.Map.InBounds(x + dx, y + dy) || game.Owner[game.Map.Index(x + dx, y + dy)] != p.Id)
                        return r;
            return r;
        }
        return Enumerable.Range(0, game.Map.TileCount).Where(t => game.Owner[t] == p.Id && t % 3 == 0)
            .OrderByDescending(Depth).ThenBy(t => t).First();
    }

    /// <summary>Steps until no bombs are in the air (or the limit), returning the troops B had just before the last step.</summary>
    private static long UntilLanded(Game game, Player watch, int limit = 400)
    {
        long before = watch.Troops;
        for (int t = 0; t < limit && game.Nukes.Count > 0; t++)
        {
            before = watch.Troops;
            game.Step();
        }
        return before;
    }

    private static int Manhattan(Game game, int a, int b)
    {
        int w = game.Map.Width;
        return Math.Abs(a % w - b % w) + Math.Abs(a / w - b / w);
    }

    [Fact]
    public void Moab_WipesLandAndBuildings_LeavingFallout()
    {
        var (game, a, b, _) = Setup();
        Build(game, a, BuildingType.Silo, t => game.Owner[t] == a.Id);
        int target = DeepInside(game, b);
        int city = Build(game, b, BuildingType.City, t => game.Owner[t] == b.Id && Manhattan(game, t, target) < 10);
        int tiles = b.TileCount;

        Assert.True(game.CanLaunch(a, NukeType.Moab, target));
        game.Submit(new NukeIntent(a.Id, NukeType.Moab, target));
        Wait(game, 1);
        Assert.Single(game.Nukes);
        long troopsBefore = UntilLanded(game, b);

        Assert.Empty(game.Nukes);
        Assert.True(b.Troops < troopsBefore * 95 / 100, $"B should lose troops with the land: {troopsBefore} -> {b.Troops}");
        Assert.Equal(Game.Nobody, game.Owner[target]);
        Assert.True(game.Fallout[target]);
        Assert.Null(game.BuildingAt(city));
        Assert.InRange(tiles - b.TileCount, (int)(Math.PI * 12 * 12 * 0.8), (int)(Math.PI * 30 * 30));
        Assert.Contains(game.Explosions, e => e.Tile == target && !e.Intercepted);
    }

    [Fact]
    public void Sam_ShootsDownBombs_ThenReloads()
    {
        var (game, a, b, _) = Setup();
        Build(game, a, BuildingType.Silo, t => game.Owner[t] == a.Id);
        int target = DeepInside(game, b);
        int sam = Build(game, b, BuildingType.Sam, t => game.Owner[t] == b.Id && Manhattan(game, t, target) < 20);
        int tiles = b.TileCount;

        game.Submit(new NukeIntent(a.Id, NukeType.Moab, target));
        Wait(game, 1);
        UntilLanded(game, b);

        Assert.Equal(tiles, b.TileCount);
        Assert.False(game.Fallout[target]);
        Assert.Contains(game.Explosions, e => e.Intercepted);
        // A level-1 SAM has one missile: it reloads 9 seconds after firing.
        Wait(game, Balance.SamCooldownTicks);
        Assert.True(game.IsLoaded(game.BuildingAt(sam)!));
    }

    [Fact]
    public void TsarBomba_SplitsIntoWarheadsOverTheTarget()
    {
        var (game, a, b, _) = Setup();
        Build(game, a, BuildingType.Silo, t => game.Owner[t] == a.Id);
        int target = DeepInside(game, b);
        int tiles = b.TileCount;
        long price = game.NukeCost(NukeType.TsarBomba);

        game.Submit(new NukeIntent(a.Id, NukeType.TsarBomba, target));
        int most = 0;
        for (int t = 0; t < 300; t++)
        {
            game.Step();
            most = Math.Max(most, game.Nukes.Count(n => n.Type == NukeType.Warhead));
        }

        Assert.True(most >= 3, $"should split into several warheads, saw {most}");
        Assert.Empty(game.Nukes);
        Assert.True(b.TileCount < tiles * 9 / 10, $"warheads should take a bite out of B: {tiles} -> {b.TileCount}");
        Assert.Equal(price + Balance.TsarBombaCostPerLaunch, game.NukeCost(NukeType.TsarBomba));
    }

    [Fact]
    public void Fallout_ClearsWhenRetaken()
    {
        var (game, a, b, _) = Setup();
        Build(game, a, BuildingType.Silo, t => game.Owner[t] == a.Id);
        int target = DeepInside(game, b);
        game.Submit(new NukeIntent(a.Id, NukeType.Moab, target));
        Wait(game, 200);
        int poisoned = game.FalloutTiles;
        Assert.True(poisoned > 300, $"a MOAB should leave fallout, got {poisoned} tiles");

        game.Submit(new AttackIntent(b.Id, Game.Nobody, 100));
        Wait(game, 600);
        Assert.True(game.FalloutTiles < poisoned, "retaking fallout should clean it up");
    }

    [Fact]
    public void ClickingEmptyLand_OnlyGoesIntoThatStretchOfIt()
    {
        var (game, a, b, _) = Setup();
        int[] Pocket(Player p)
        {
            int c = DeepInside(game, p), w = game.Map.Width;
            var tiles = Enumerable.Range(0, game.Map.TileCount)
                .Where(t => Math.Abs(t % w - c % w) <= 3 && Math.Abs(t / w - c / w) <= 3 && game.Owner[t] == p.Id).ToArray();
            foreach (int t in tiles)
                SetOwner(game, t, Game.Nobody);
            return tiles;
        }
        int[] mine = Pocket(b), theirs = Pocket(a); // irradiated holes in both players' land

        // B clicks the hole in A's land: it doesn't touch B, so nothing happens to B's own hole.
        Assert.False(game.CanReachByLand(b, theirs[0]));
        game.Submit(new AttackIntent(b.Id, Game.Nobody, 100, Tile: theirs[0]));
        Wait(game, 100);
        Assert.All(mine, t => Assert.Equal(Game.Nobody, game.Owner[t]));

        // Clicking its own hole retakes it.
        Assert.True(game.CanReachByLand(b, mine[0]));
        game.Submit(new AttackIntent(b.Id, Game.Nobody, 100, Tile: mine[0]));
        Wait(game, 100);
        Assert.All(mine, t => Assert.Equal(b.Id, game.Owner[t]));
        Assert.All(theirs, t => Assert.Equal(Game.Nobody, game.Owner[t]));
    }

    [Fact]
    public void NoBombs_DuringTheSpawnWindow_OrWithoutASilo()
    {
        var map = MapGenerator.Generate(21, new MapGenSettings { Width = 400, Height = 250, MinIslandTiles = 8, MinLakeTiles = 30 });
        var game = new Game(map, 1, spawnPhaseTicks: 2);
        var a = game.AddPlayer("A");
        game.SetCheats(a, true, true);
        int land = Enumerable.Range(0, map.TileCount).First(t => map.IsLand(t) && map.LandmassSizes[map.LandmassId[t]] > 2000);
        game.Submit(new SpawnIntent(a.Id, land));
        Wait(game, 4);
        Assert.True(game.NukesLocked);
        Wait(game, NukeRules.SpawnImmunityTicks);
        Assert.False(game.NukesLocked);
        Assert.False(game.CanLaunch(a, NukeType.Moab, land)); // no silo
    }

    [Fact]
    public void Cheats_BuildForFree_AndInstantly()
    {
        var (game, a, _, _) = Setup();
        long gold = a.Gold;
        for (int i = 0; i < 3; i++)
            Build(game, a, BuildingType.City, t => game.Owner[t] == a.Id);
        Assert.True(a.Gold >= gold, "infinite gold: building costs nothing");
    }

    [Fact]
    public void Europe_Has52NationsNearTheirHomes()
    {
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "Boundari.sln")) || !Directory.Exists(Path.Combine(dir, "src")))
            dir = Path.GetDirectoryName(dir)!;
        GameMap map;
        using (var file = File.OpenRead(Path.Combine(dir, "src", "Boundari", "maps", "europe.bmap")))
            map = MapFile.Read(file);
        var game = new Game(map, 5);
        game.AddNations(MapNations.Europe);

        Assert.Equal(52, game.Players.Count);
        var geo = map.Geo!;
        var firstTile = new int[game.Players.Count + 1];
        Array.Fill(firstTile, -1);
        for (int t = 0; t < map.TileCount; t++)
            if (game.Owner[t] != Game.Nobody && firstTile[game.Owner[t]] < 0)
                firstTile[game.Owner[t]] = t;
        foreach (var (spec, p) in MapNations.Europe.Zip(game.Players))
        {
            Assert.Equal(spec.Name, p.Name);
            Assert.True(p.HasSpawned && p.IsNation, $"{p.Name} should have spawned");
            int hx = Math.Clamp((int)geo.TileX(spec.Lon, map.Width), 0, map.Width - 1);
            int hy = Math.Clamp((int)geo.TileY(spec.Lat, map.Height), 0, map.Height - 1);
            int spawn = firstTile[p.Id];
            long dx = spawn % map.Width - hx, dy = spawn / map.Width - hy;
            Assert.True(dx * dx + dy * dy < 160 * 160, $"{p.Name} started {Math.Sqrt(dx * dx + dy * dy):F0} tiles from home");
        }
    }
}
