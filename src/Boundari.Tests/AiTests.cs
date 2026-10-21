using System.Linq;
using System.Reflection;
using Boundari.Map;
using Boundari.Sim;
using Xunit;

/// <summary>The OpenFront nation and tribe behaviours (NationAi and friends) and what they rely on.</summary>
public class AiTests
{
    private static GameMap SmallMap(ulong seed = 21) =>
        MapGenerator.Generate(seed, new MapGenSettings { Width = 400, Height = 250, MinIslandTiles = 8, MinLakeTiles = 30 });

    private static Game AiGame(ulong seed, Difficulty difficulty, int nations, int bots, int ticks)
    {
        var game = new Game(SmallMap(), seed, spawnPhaseTicks: 5, difficulty: difficulty);
        game.AddNations(nations);
        game.AddBots(bots);
        for (int t = 0; t < ticks; t++)
            game.Step();
        return game;
    }

    [Theory]
    [InlineData(Difficulty.Easy)]
    [InlineData(Difficulty.Hard)]
    [InlineData(Difficulty.Impossible)]
    public void SameSeed_PlaysOutIdentically_AtEveryDifficulty(Difficulty difficulty)
    {
        var a = AiGame(5, difficulty, nations: 10, bots: 25, ticks: 2500);
        var b = AiGame(5, difficulty, nations: 10, bots: 25, ticks: 2500);

        Assert.Equal(a.Owner, b.Owner);
        Assert.Equal(a.Players.Select(p => p.Troops), b.Players.Select(p => p.Troops));
        Assert.Equal(a.Players.Select(p => p.Gold), b.Players.Select(p => p.Gold));
        Assert.Equal(a.Alliances.Count, b.Alliances.Count);
    }

    [Fact]
    public void Nations_ClearOutTheTribes()
    {
        var game = AiGame(9, Difficulty.Hard, nations: 6, bots: 30, ticks: 3000);
        int tribesLeft = game.Players.Count(p => p.IsBot && p.IsAlive);
        Assert.True(tribesLeft < 15, $"Hard nations should have taken most of the 30 tribes, {tribesLeft} left");
    }

    [Fact]
    public void AiBoat_BoardsWhileItsRouteIsPlanned_ThenSails()
    {
        var map = SmallMap();
        var game = new Game(map, 1, spawnPhaseTicks: 2);
        var nation = game.AddPlayer("N", PlayerKind.Nation); // no AI attached: we give the orders
        int biggest = Enumerable.Range(0, map.LandmassSizes.Length).OrderByDescending(i => map.LandmassSizes[i]).First();
        int coast = Enumerable.Range(0, map.TileCount).First(t => map.LandmassId[t] == biggest && game.IsCoastal(t));
        game.Submit(new SpawnIntent(nation.Id, coast));
        for (int t = 0; t < 5; t++)
            game.Step();
        int target = Enumerable.Range(0, map.TileCount)
            .First(t => map.IsLand(t) && map.LandmassId[t] != biggest && map.LandmassSizes[map.LandmassId[t]] > 300);

        long before = nation.Troops;
        game.Submit(new BoatIntent(nation.Id, target, 0, 1000));
        game.Step();
        Assert.True(nation.Troops < before, "the troops are aboard");
        Assert.Equal(1, game.BoatsAtSea(nation)); // boarding counts toward the three boats

        for (int t = 0; t < 50 && game.Boats.Count == 0; t++)
            game.Step();
        var boat = Assert.Single(game.Boats);
        Assert.Equal(1000, boat.Troops);
        Assert.Equal(1, game.BoatsAtSea(nation));
    }

    [Fact]
    public void ANation_IsPuzzledByAClown_AndLikesTheSenderLess()
    {
        var map = SmallMap();
        var game = new Game(map, 1, spawnPhaseTicks: 2);
        var a = game.AddPlayer("A");
        var b = game.AddPlayer("B", PlayerKind.Nation);
        int biggest = Enumerable.Range(0, map.LandmassSizes.Length).OrderByDescending(i => map.LandmassSizes[i]).First();
        int[] land = Enumerable.Range(0, map.TileCount).Where(t => map.LandmassId[t] == biggest).ToArray();
        game.Submit(new SpawnIntent(a.Id, land[0]));
        game.Submit(new SpawnIntent(b.Id, land[^1]));
        for (int t = 0; t < 5; t++)
            game.Step();

        game.Submit(new EmojiIntent(a.Id, b.Id, Emoji.Clown));
        game.Step();
        Assert.Equal(Relation.Distrustful, game.RelationOf(b, a)); // at once
        for (int t = 0; t < ChatRules.ReplyMaxTicks; t++) // the answer within a few seconds
            game.Step();
        Assert.Contains(game.ShownEmoji(b, a), new Emoji?[] { Emoji.Question, Emoji.Clown });
    }

    [Fact]
    public void MoveWarship_SendsItToPatrolElsewhere_OnlyForItsOwner()
    {
        var map = SmallMap();
        var game = new Game(map, 1, spawnPhaseTicks: 2);
        var me = game.AddPlayer("Me");
        var other = game.AddPlayer("Other");
        int water = Enumerable.Range(0, map.TileCount).First(t => map.IsWater(t) && game.Waterways.BodySize[game.Waterways.Body[t]] > 5000);
        int elsewhere = Enumerable.Range(0, map.TileCount).Last(t => game.Waterways.Body[t] == game.Waterways.Body[water]);
        var ctor = typeof(Warship).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
        var ship = (Warship)ctor.Invoke(new object[] { 1, me, water, water, 7UL });
        var ships = (System.Collections.Generic.List<Warship>)typeof(Game).GetField("_warships", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(game)!;
        ships.Add(ship);
        game.Submit(new SpawnIntent(me.Id, Enumerable.Range(0, map.TileCount).First(t => map.IsLand(t))));
        for (int t = 0; t < 3; t++)
            game.Step();

        game.Submit(new MoveWarshipIntent(other.Id, ship.Id, elsewhere));
        game.Step();
        Assert.Equal(water, ship.PatrolTile); // not theirs to move

        game.Submit(new MoveWarshipIntent(me.Id, ship.Id, elsewhere));
        game.Step();
        Assert.Equal(elsewhere, ship.PatrolTile);
    }
}
