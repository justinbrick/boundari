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
        Assert.All(middle, t => Assert.True(t / w >= 15, $"boats should keep about 6 tiles off the shore, got y={t / w}"));
    }
}
