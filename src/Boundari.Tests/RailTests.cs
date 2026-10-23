using System;
using System.Linq;
using System.Reflection;
using Boundari.Map;
using Boundari.Sim;
using Xunit;

public class RailTests
{
    private static readonly MethodInfo SetOwnerMethod =
        typeof(Game).GetMethod("SetOwner", BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>Flat land with a river (riverWidth tiles wide) running down the middle; one player owns all the land.</summary>
    private static (Game Game, Player P) RiverMap(int riverWidth)
    {
        var map = new GameMap(120, 60, 1);
        for (int t = 0; t < map.TileCount; t++)
        {
            int x = t % map.Width;
            bool river = x >= 60 && x < 60 + riverWidth;
            map.Terrain[t] = river ? TerrainType.Lake : TerrainType.Plains;
            map.CoastField[t] = river ? -1 : 1;
        }
        MapProcessing.LabelLandmasses(map);
        var game = new Game(map, 1, spawnPhaseTicks: 2);
        var p = game.AddPlayer("P");
        game.Submit(new SpawnIntent(p.Id, map.Index(10, 30)));
        for (int t = 0; t < 4; t++)
            game.Step();
        for (int t = 0; t < map.TileCount; t++)
            if (map.IsLand(t))
                SetOwnerMethod.Invoke(game, new object[] { t, p.Id });
        game.SetCheats(p, infiniteGold: true, instantBuild: true);
        return (game, p);
    }

    private static void BuildFactoryAcross(Game game, Player p)
    {
        var map = game.Map;
        game.Submit(new BuildIntent(p.Id, BuildingType.City, map.Index(30, 20)));
        game.Step();
        game.Submit(new BuildIntent(p.Id, BuildingType.Factory, map.Index(95, 42)));
        for (int t = 0; t < 5; t++)
            game.Step();
    }

    [Fact]
    public void Rails_BridgeRivers_InStraightRunsWithRightAngleTurns()
    {
        var (game, p) = RiverMap(riverWidth: 6);
        BuildFactoryAcross(game, p);

        var rail = Assert.Single(game.Railroads);
        int w = game.Map.Width;
        Assert.Contains(rail.Tiles, t => game.Map.IsWater(t)); // it bridges the river
        int turns = 0;
        for (int i = 1; i < rail.Tiles.Length; i++)
        {
            int step = rail.Tiles[i] - rail.Tiles[i - 1];
            Assert.True(step is 1 or -1 || Math.Abs(step) == w, "rails move one tile up, down, left or right");
            if (i >= 2 && step != rail.Tiles[i - 1] - rail.Tiles[i - 2])
                turns++;
        }
        Assert.InRange(turns, 1, 2); // an L or a Z, never a staircase
    }

    [Fact]
    public void Rails_DontBridgeWideWater()
    {
        var (game, p) = RiverMap(riverWidth: RailRules.MaxBridgeTiles + 3);
        BuildFactoryAcross(game, p);
        Assert.Empty(game.Railroads);
    }

    [Fact]
    public void Boats_KeepOffTheCoast_WhenThereIsRoom()
    {
        // A wide bay (y 10..50) in solid land; sail along its north shore.
        var map = new GameMap(120, 60, 1);
        for (int t = 0; t < map.TileCount; t++)
        {
            int x = t % map.Width, y = t / map.Width;
            bool water = x >= 5 && x < 115 && y >= 10 && y < 50;
            map.Terrain[t] = water ? TerrainType.Lake : TerrainType.Plains;
            map.CoastField[t] = water ? -1 : 1;
        }
        MapProcessing.LabelLandmasses(map);
        var ways = new Waterways(map);
        var path = ways.FindPath(map.Index(6, 10), map.Index(113, 10))!;
        Assert.NotNull(path);
        int w = map.Width;
        var middle = path.Where(t => t % w > 25 && t % w < 95).ToList();
        // OpenFront's band: 3 to 10 tiles off the coast (the shore row is y 10).
        Assert.All(middle, t => Assert.True(t / w >= 12 && t / w <= 20, $"boats should sail 3-10 tiles off the shore, got y={t / w}"));
    }

    [Fact]
    public void Boats_TakeTheShortWay_BetweenNearbyIslands()
    {
        // Open sea with three islands in a row, 12 tiles apart (the user's screenshot: a short
        // hop past the middle one shouldn't swing far out to sea to keep clear of the coasts).
        var map = new GameMap(200, 160, 1);
        for (int t = 0; t < map.TileCount; t++)
        {
            int x = t % map.Width, y = t / map.Width;
            bool land = (x >= 85 && x < 115 && y >= 30 && y < 60)    // the island we sail from
                || (x >= 90 && x < 110 && y >= 72 && y < 92)          // a small one in between
                || (x >= 85 && x < 115 && y >= 104 && y < 134);        // the island we sail to
            map.Terrain[t] = land ? TerrainType.Plains : TerrainType.Ocean;
            map.CoastField[t] = land ? 1 : -1;
        }
        MapProcessing.LabelLandmasses(map);
        var ways = new Waterways(map, 1.33);
        var path = ways.FindPath(map.Index(100, 60), map.Index(100, 103))!;
        Assert.NotNull(path);
        Assert.True(path.Length <= 50, $"a 43-tile hop took {path.Length} tiles");
    }

    [Fact]
    public void PreviewRails_MatchesTheRoadsActuallyLaid()
    {
        // Flat land, a first depot with cities round it, then cities further east: the new
        // depot links to existing stations and turns the eastern cities into stations too.
        var map = new GameMap(320, 200, 1);
        for (int t = 0; t < map.TileCount; t++)
        {
            map.Terrain[t] = TerrainType.Plains;
            map.CoastField[t] = 1;
        }
        MapProcessing.LabelLandmasses(map);
        var game = new Game(map, 1, spawnPhaseTicks: 2);
        var p = game.AddPlayer("P");
        game.Submit(new SpawnIntent(p.Id, map.Index(10, 100)));
        for (int t = 0; t < 4; t++)
            game.Step();
        for (int t = 0; t < map.TileCount; t++)
            SetOwnerMethod.Invoke(game, new object[] { t, p.Id });
        game.SetCheats(p, infiniteGold: true, instantBuild: true);

        void Build(BuildingType type, int x, int y)
        {
            game.Submit(new BuildIntent(p.Id, type, map.Index(x, y)));
            game.Step();
        }
        foreach (var (x, y) in new[] { (40, 60), (40, 140), (90, 100) })
            Build(BuildingType.City, x, y);
        Build(BuildingType.Factory, 60, 100);
        foreach (var (x, y) in new[] { (170, 60), (170, 140), (210, 100), (130, 30) })
            Build(BuildingType.City, x, y);
        for (int t = 0; t < 60; t++)
            game.Step(); // let the first depot's roads all be laid

        int site = map.Index(160, 100);
        var preview = game.PreviewRails(BuildingType.Factory, site);
        var before = game.Railroads.ToHashSet();
        Build(BuildingType.Factory, 160, 100);
        for (int t = 0; t < 60; t++)
            game.Step();
        var laid = game.Railroads.Where(r => !before.Contains(r)).Select(r => r.Tiles).ToList();

        Assert.True(preview.Count >= 3, $"expected several roads, previewed {preview.Count}");
        Assert.Equal(laid.Count, preview.Count);
        for (int i = 0; i < laid.Count; i++)
            Assert.Equal(laid[i], preview[i]);
    }
}
