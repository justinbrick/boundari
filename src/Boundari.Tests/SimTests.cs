using System;
using System.Linq;
using Boundari.Map;
using Boundari.Sim;
using Xunit;

public class SimTests
{
    private static GameMap SmallMap(ulong seed = 21) =>
        MapGenerator.Generate(seed, new MapGenSettings { Width = 400, Height = 250, MinIslandTiles = 8, MinLakeTiles = 30 });

    private static Game BotGame(ulong seed, int bots, int ticks)
    {
        var game = new Game(SmallMap(), seed, spawnPhaseTicks: 5);
        game.AddBots(bots);
        for (int t = 0; t < ticks; t++)
            game.Step();
        return game;
    }

    [Fact]
    public void SameSeed_PlaysOutIdentically()
    {
        var a = BotGame(7, bots: 12, ticks: 900);
        var b = BotGame(7, bots: 12, ticks: 900);

        Assert.Equal(a.Owner, b.Owner);
        Assert.Equal(a.Players.Select(p => p.Troops), b.Players.Select(p => p.Troops));
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
        Assert.True(game.Players.Count(p => p.TileCount > spawnSize * 5) >= 8, "most bots should have grown");
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
