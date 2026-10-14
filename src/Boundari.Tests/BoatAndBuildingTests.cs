using System.Linq;
using Boundari.Map;
using Boundari.Sim;
using Xunit;

public class BoatAndBuildingTests
{
    private static (Game Game, Player Me, GameMap Map) StartOnCoast(ulong seed = 21)
    {
        var map = MapGenerator.Generate(seed, new MapGenSettings { Width = 400, Height = 250, MinIslandTiles = 8, MinLakeTiles = 30 });
        var game = new Game(map, 1, spawnPhaseTicks: 2);
        var me = game.AddPlayer("Me");
        int biggest = Enumerable.Range(0, map.LandmassSizes.Length).OrderByDescending(i => map.LandmassSizes[i]).First();
        int coast = Enumerable.Range(0, map.TileCount).First(t => map.LandmassId[t] == biggest && game.IsCoastal(t));
        game.Submit(new SpawnIntent(me.Id, coast));
        for (int t = 0; t < 5; t++)
            game.Step();
        return (game, me, map);
    }

    [Fact]
    public void Boat_CrossesWater_AndLandsOnAnotherLandmass()
    {
        var (game, me, map) = StartOnCoast();
        int home = map.LandmassId[game.Owner.ToList().IndexOf(me.Id)];
        // Somewhere on a different, sizeable landmass in the same sea.
        int target = Enumerable.Range(0, map.TileCount)
            .First(t => map.IsLand(t) && map.LandmassId[t] != home && map.LandmassSizes[map.LandmassId[t]] > 300);

        Assert.False(game.CanReachByLand(me, target));
        long before = me.Troops;
        game.Submit(new BoatIntent(me.Id, target, 50));
        game.Step();
        Assert.Single(game.Boats);
        Assert.True(me.Troops < before);

        for (int t = 0; t < 2000 && game.Boats.Count > 0; t++)
            game.Step();
        for (int t = 0; t < 100; t++)
            game.Step();
        Assert.Empty(game.Boats);
        int landed = Enumerable.Range(0, map.TileCount).Count(t => game.Owner[t] == me.Id && map.LandmassId[t] == map.LandmassId[target]);
        Assert.True(landed > 1, "troops should have landed and expanded on the other landmass");
    }

    [Fact]
    public void Buildings_NeedMoneyOwnLandSpacingAndPortsNeedCoast()
    {
        var (game, me, map) = StartOnCoast();
        int[] mine = Enumerable.Range(0, map.TileCount).Where(t => game.Owner[t] == me.Id).ToArray();
        int coast = mine.First(game.IsCoastal);
        int inland = mine.FirstOrDefault(t => !game.IsCoastal(t), -1);

        Assert.False(game.CanBuild(me, BuildingType.Port, coast)); // can't afford it yet
        GiveGold(me, 5_000_000);
        Assert.True(game.CanBuild(me, BuildingType.Port, coast));
        if (inland >= 0)
            Assert.False(game.CanBuild(me, BuildingType.Port, inland));
        Assert.False(game.CanBuild(me, BuildingType.City, mine.Length > 0 ? FirstNotMine(game, me) : 0));

        game.Submit(new BuildIntent(me.Id, BuildingType.Port, coast));
        game.Step();
        Assert.Equal(1, game.CountBuildings(me, BuildingType.Port));
        Assert.InRange(me.Gold, 5_000_000 - Balance.BuildingCost(BuildingType.Port), 5_000_000 - Balance.BuildingCost(BuildingType.Port) + 1000); // plus a tick of income
        Assert.False(game.CanBuild(me, BuildingType.City, coast)); // too close to the port
    }

    private static int FirstNotMine(Game game, Player me) =>
        Enumerable.Range(0, game.Map.TileCount).First(t => game.Map.IsLand(t) && game.Owner[t] != me.Id);

    private static void GiveGold(Player p, long gold) =>
        typeof(Player).GetProperty(nameof(Player.Gold))!.SetValue(p, gold);
}
