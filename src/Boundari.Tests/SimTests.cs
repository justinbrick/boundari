using System;
using System.Linq;
using Boundari.Map;
using Boundari.Sim;
using Xunit;

public class SimTests
{
    private static GameMap SmallMap(ulong seed = 21) =>
        MapGenerator.Generate(seed, new MapGenSettings { Width = 400, Height = 250, MinIslandTiles = 8, MinLakeTiles = 30 });

    [Fact]
    public void SpawnPhase_WaitsForThePlayer_ThenStartsAtOnce()
    {
        var map = SmallMap();
        var game = new Game(map, 1);
        var me = game.AddPlayer("Me");
        game.StartsWhenSpawned = me.Id;
        for (int t = 0; t < Balance.SpawnPhaseTicks * 3; t++)
            game.Step();
        Assert.Equal(GamePhase.Spawning, game.Phase); // no countdown: still waiting

        int land = Enumerable.Range(0, map.TileCount).First(map.IsLand);
        game.Submit(new SpawnIntent(me.Id, land));
        game.Step();
        Assert.True(me.HasSpawned);
        Assert.Equal(GamePhase.Running, game.Phase);
        Assert.Equal(game.Tick, game.SpawnPhaseTicks);
    }

    private static Game BotGame(ulong seed, int bots, int ticks, int wimpyBots = 0)
    {
        var game = new Game(SmallMap(), seed, spawnPhaseTicks: 5);
        game.AddNations(bots);
        game.AddBots(wimpyBots);
        for (int t = 0; t < ticks; t++)
            game.Step();
        return game;
    }

    [Fact]
    public void SameSeed_PlaysOutIdentically()
    {
        var a = BotGame(7, bots: 12, ticks: 1500, wimpyBots: 20);
        var b = BotGame(7, bots: 12, ticks: 1500, wimpyBots: 20);

        Assert.Equal(a.Owner, b.Owner);
        Assert.Equal(a.Players.Select(p => p.Troops), b.Players.Select(p => p.Troops));
        Assert.Equal(a.Players.Select(p => p.Gold), b.Players.Select(p => p.Gold));
        Assert.Equal(a.Buildings.Select(x => (x.Tile, x.Type, x.Level)).OrderBy(x => x.Tile), b.Buildings.Select(x => (x.Tile, x.Type, x.Level)).OrderBy(x => x.Tile));
    }

    [Fact]
    public void Bots_AreWeakFiller_NationsOutgrowThem()
    {
        var game = BotGame(11, bots: 6, ticks: 3000, wimpyBots: 30);

        var nations = game.Players.Where(p => p.IsNation).ToList();
        var bots = game.Players.Where(p => p.IsBot).ToList();
        Assert.All(bots, b => Assert.True(b.Flag == "" && b.Name.Contains(' '), $"bot {b.Name} should have a made-up name and no flag"));
        double nationAvg = nations.Average(p => (double)p.TileCount), botAvg = bots.Average(p => (double)p.TileCount);
        Assert.True(nationAvg > botAvg * 3, $"nations should do far better than bots: {nationAvg:F0} vs {botAvg:F0} tiles on average");
        Assert.True(bots.Count(b => !b.IsAlive) > 0, "nations should have eaten some bots");
    }

    [Fact]
    public void Nations_BuildCitiesAndMore()
    {
        var game = BotGame(13, bots: 6, ticks: 6000, wimpyBots: 10);

        var nationBuildings = game.Buildings.Where(b => game.GetPlayer(b.Owner)!.IsNation).ToList();
        var types = nationBuildings.Select(b => b.Type).Distinct().ToList();
        Assert.Contains(BuildingType.City, types);
        Assert.True(types.Count >= 2, "nations should build more than one kind of building: " + string.Join(", ", types));
    }

    [Fact]
    public void TileCountsAndBorders_StayConsistent()
    {
        var game = BotGame(3, bots: 12, ticks: 1200);

        foreach (var p in game.Players)
        {
            Assert.Equal(game.Owner.Count(o => o == p.Id), p.TileCount);
            var expectedBorder = Enumerable.Range(0, game.Map.TileCount)
                .Where(t => game.Owner[t] == p.Id && IsBorder(game, t))
                .ToHashSet();
            Assert.True(expectedBorder.SetEquals(p.Border), $"{p} border is out of date");
        }
        Assert.All(game.Owner.Select((o, t) => (o, t)).Where(x => x.o != Game.Nobody),
                   x => Assert.True(game.Map.IsLand(x.t)));
    }

    [Fact]
    public void Bots_ExpandAndFight()
    {
        var game = BotGame(5, bots: 12, ticks: 2400);

        int spawnSize = (int)(Math.PI * Balance.SpawnRadius * Balance.SpawnRadius);
        // Some bots get wiped out on this crowded little map; the survivors should be big.
        Assert.All(game.Players, p => Assert.True(p.HasSpawned, $"{p} never spawned"));
        Assert.True(game.Players.Count(p => p.TileCount > spawnSize * 5) >= 4,
            "several bots should have grown: " + string.Join(", ", game.Players.Select(p => p.TileCount)));
        int owned = game.Owner.Count(o => o != Game.Nobody);
        Assert.True(owned > game.Map.LandTileCount / 2, $"bots should claim most land, got {owned}/{game.Map.LandTileCount}");
    }

    [Fact]
    public void Player_CanSpawnMoveSpawnAndExpand()
    {
        var map = SmallMap();
        var game = new Game(map, 1, spawnPhaseTicks: 5);
        var me = game.AddPlayer("Me");
        int[] land = Enumerable.Range(0, map.TileCount).Where(t => map.IsLand(t) && map.LandmassSizes[map.LandmassId[t]] > 2000).ToArray();

        game.Submit(new SpawnIntent(me.Id, land[0]));
        game.Step();
        int firstSize = me.TileCount;
        Assert.True(firstSize > 10);

        game.Submit(new SpawnIntent(me.Id, land[^1])); // move it
        game.Step();
        Assert.Equal(Game.Nobody, game.Owner[land[0]]);
        Assert.Equal(me.Id, game.Owner[land[^1]]);

        for (int t = 0; t < 10; t++)
            game.Step();
        Assert.Equal(GamePhase.Running, game.Phase);

        int before = me.TileCount;
        game.Submit(new AttackIntent(me.Id, Game.Nobody, 50));
        for (int t = 0; t < 50; t++)
            game.Step();
        Assert.True(me.TileCount > before * 3, $"expansion should grow territory: {before} -> {me.TileCount}");
    }

    private static bool IsBorder(Game game, int tile)
    {
        Span<int> nb = stackalloc int[4];
        int count = game.LandNeighbours(tile, nb);
        for (int k = 0; k < count; k++)
            if (game.Owner[nb[k]] != game.Owner[tile])
                return true;
        return false;
    }
}
