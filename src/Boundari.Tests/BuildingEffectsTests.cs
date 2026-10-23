using System;
using System.Linq;
using System.Reflection;
using Boundari.Map;
using Boundari.Sim;
using Xunit;

public class BuildingEffectsTests
{
    private static readonly MethodInfo SetOwnerMethod =
        typeof(Game).GetMethod("SetOwner", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static void SetOwner(Game game, int tile, ushort owner) => SetOwnerMethod.Invoke(game, new object[] { tile, owner });

    private static void GiveGold(Player p, long gold) => typeof(Player).GetProperty(nameof(Player.Gold))!.SetValue(p, gold);

    /// <summary>A small map; player A owns the whole biggest landmass (B optionally its east half).</summary>
    private static (Game Game, Player A, Player B, int[] Land) Setup(bool split)
    {
        var map = MapGenerator.Generate(21, new MapGenSettings { Width = 400, Height = 250, MinIslandTiles = 8, MinLakeTiles = 30 });
        var game = new Game(map, 1, spawnPhaseTicks: 2);
        var a = game.AddPlayer("A");
        var b = game.AddPlayer("B");
        int biggest = Enumerable.Range(0, map.LandmassSizes.Length).OrderByDescending(i => map.LandmassSizes[i]).First();
        int[] land = Enumerable.Range(0, map.TileCount).Where(t => map.LandmassId[t] == biggest).ToArray();
        game.Submit(new SpawnIntent(a.Id, land[0]));
        game.Submit(new SpawnIntent(b.Id, land[^1]));
        for (int t = 0; t < 4; t++)
            game.Step();
        int midX = (int)land.Select(t => t % map.Width).Average();
        foreach (int t in land)
            SetOwner(game, t, split && t % map.Width >= midX ? b.Id : a.Id);
        GiveGold(a, 100_000_000);
        GiveGold(b, 100_000_000);
        return (game, a, b, land);
    }

    private static void Wait(Game game, int ticks)
    {
        for (int t = 0; t < ticks; t++)
            game.Step();
    }

    [Fact]
    public void TroopCap_MatchesOpenFront()
    {
        Assert.Equal(100_000, Balance.MaxTroops(0, 0));
        Near(2 * ((long)(Math.Pow(10_000, 0.6) * 1000) + 50_000), Balance.MaxTroops(10_000, 0), 2);
        Near(Balance.MaxTroops(10_000, 0) / 3, Balance.MaxTroops(10_000, 0, PlayerKind.Bot), 2);
        Near(Balance.MaxTroops(10_000, 0) * 3 / 4, Balance.MaxTroops(10_000, 0, PlayerKind.Nation, Difficulty.Medium), 2);
        Assert.Equal(Balance.MaxTroops(10_000, 0), Balance.MaxTroops(10_000, 0, PlayerKind.Nation, Difficulty.Hard));
        Assert.Equal(Balance.MaxTroops(500, 0) + 500_000, Balance.MaxTroops(500, 2));
        // Growth slows to nothing at the cap and never goes over it.
        Assert.Equal(0, Balance.TroopGrowth(100_000, 100_000));
        Assert.True(Balance.TroopGrowth(50_000, 100_000) > Balance.TroopGrowth(50_000, 100_000, PlayerKind.Nation));
        Assert.True(Balance.TroopGrowth(50_000, 100_000, PlayerKind.Nation) > Balance.TroopGrowth(50_000, 100_000, PlayerKind.Bot));
        // Starting troops.
        Assert.Equal(25_000, Balance.StartTroopsFor(PlayerKind.Human, Difficulty.Medium));
        Assert.Equal(18_750, Balance.StartTroopsFor(PlayerKind.Nation, Difficulty.Medium));
        Assert.Equal(10_000, Balance.StartTroopsFor(PlayerKind.Bot, Difficulty.Impossible));
    }

    [Fact]
    public void City_RaisesTroopCap_AndStacks()
    {
        var (game, a, _, land) = Setup(split: false);
        int site = land[land.Length / 2];
        long before = a.MaxTroops;

        game.Submit(new BuildIntent(a.Id, BuildingType.City, site));
        game.Step();
        Near(before, a.MaxTroops, 1000); // still being built
        Wait(game, Balance.ConstructionTicks(BuildingType.City) + 1);
        Near(before + Balance.CityTroopIncrease, a.MaxTroops, 1000);

        long secondPrice = game.BuildingCost(a, BuildingType.City);
        Assert.Equal(250_000, secondPrice);
        game.Submit(new BuildIntent(a.Id, BuildingType.City, site)); // stack on top
        game.Step();
        var city = game.Buildings.Single(b => b.Type == BuildingType.City);
        Assert.Equal(2, city.Level);
        Near(before + 2 * Balance.CityTroopIncrease, a.MaxTroops, 1000);
        Assert.Equal(500_000, game.BuildingCost(a, BuildingType.City)); // two levels owned now
    }

    [Fact]
    public void DefensePost_ShieldsNearbyLand_AndIsDestroyedWhenTaken()
    {
        var (game, a, b, land) = Setup(split: true);
        var map = game.Map;
        // A border tile of B's, and a post right next to it.
        int front = land.First(t => game.Owner[t] == b.Id && game.Touches(t, a.Id));
        int post = land.Where(t => game.Owner[t] == b.Id).OrderBy(t => Dist(map, t, front)).First(t => Dist(map, t, front) >= 3);
        Assert.False(game.DefendedByPost(front, b.Id));
        game.Submit(new BuildIntent(b.Id, BuildingType.Defense, post));
        Wait(game, Balance.ConstructionTicks(BuildingType.Defense) + 2);
        Assert.True(game.DefendedByPost(front, b.Id));
        int far = land.First(t => game.Owner[t] == b.Id && Dist(map, t, post) > 3 * Balance.DefensePostRange);
        Assert.False(game.DefendedByPost(far, b.Id));

        SetOwner(game, post, a.Id); // captured: posts are destroyed, not taken over
        Assert.DoesNotContain(game.Buildings, x => x.Type == BuildingType.Defense);
        Assert.False(game.DefendedByPost(front, b.Id));
        Assert.False(game.DefendedByPost(front, a.Id));
    }

    [Fact]
    public void DefensePost_CrumblesAMinuteAfterItsFinished()
    {
        var (game, a, b, land) = Setup(split: true);
        int front = land.First(t => game.Owner[t] == b.Id && game.Touches(t, a.Id));
        int post = land.Where(t => game.Owner[t] == b.Id).OrderBy(t => Dist(game.Map, t, front)).First(t => Dist(game.Map, t, front) >= 3);
        game.Submit(new BuildIntent(b.Id, BuildingType.Defense, post));
        Wait(game, Balance.ConstructionTicks(BuildingType.Defense) + 2);
        Assert.True(game.DefendedByPost(front, b.Id));
        Wait(game, Balance.DefensePostLifetimeTicks - 10);
        Assert.True(game.DefendedByPost(front, b.Id));
        Wait(game, 10);
        Assert.False(game.DefendedByPost(front, b.Id));
        Assert.DoesNotContain(game.Buildings, x => x.Type == BuildingType.Defense);
    }

    [Fact]
    public void Factory_LinksNearbyCities_AndTrainsEarnGold()
    {
        var (game, a, _, land) = Setup(split: false);
        var map = game.Map;
        // A factory and two cities 30-60 tiles from it, all on A's land.
        int factory = land[land.Length / 2];
        int[] cities = land.Where(t => Dist(map, t, factory) is > 35 and < 60).Take(1)
            .Concat(land.Where(t => Dist(map, t, factory) is > 35 and < 60).Skip(500).Take(1)).ToArray();
        foreach (int c in cities)
            game.Submit(new BuildIntent(a.Id, BuildingType.City, c));
        Wait(game, Balance.ConstructionTicks(BuildingType.City) + 2);
        game.Submit(new BuildIntent(a.Id, BuildingType.Factory, factory));
        Wait(game, Balance.ConstructionTicks(BuildingType.Factory) + 2);

        Assert.True(game.Railroads.Count >= 2, $"expected rails to both cities, got {game.Railroads.Count}");
        Assert.All(game.Railroads, r => Assert.All(r.Tiles, t => Assert.True(map.IsLand(t))));

        long goldBefore = a.Gold;
        Wait(game, 3000);
        Assert.True(a.TrainGold > 0, "trains should have earned gold at the cities");
        Assert.True(a.Gold > goldBefore);
    }

    [Fact]
    public void Conquest_FinishesOffScraps_AndTakesGold()
    {
        var (game, a, b, land) = Setup(split: true);
        int w = game.Map.Width;
        int midX = land.Where(t => game.Owner[t] == b.Id).Min(t => t % w);
        foreach (int t in land) // B keeps just a thin strip along the old border
            if (game.Owner[t] == b.Id && t % w >= midX + 2)
                SetOwner(game, t, a.Id);
        Assert.InRange(b.TileCount, Balance.ConquerBelowTiles + 1, 2000);

        game.Submit(new AttackIntent(b.Id, a.Id, 1)); // B has fought, so it gives up gold
        Wait(game, 1);
        game.Submit(new AttackIntent(a.Id, b.Id, 100));
        Wait(game, 300);

        Assert.Equal(0, b.TileCount);
        Assert.Equal(0, b.Gold);
        Assert.True(a.Gold >= 150_000_000, $"A should take half of B's 100M: {a.Gold:N0}");
    }

    [Fact]
    public void Demolition_TakesThirtySeconds_WithACooldown()
    {
        var (game, a, _, land) = Setup(split: false);
        int first = land[land.Length / 3], second = land[2 * land.Length / 3];
        game.Submit(new BuildIntent(a.Id, BuildingType.City, first));
        game.Submit(new BuildIntent(a.Id, BuildingType.City, second));
        Wait(game, Balance.ConstructionTicks(BuildingType.City) + 1);

        game.Submit(new DemolishIntent(a.Id, first));
        game.Submit(new DemolishIntent(a.Id, second)); // too soon after the first: ignored
        Wait(game, Balance.DemolishTicks - 5);
        Assert.NotNull(game.BuildingAt(first));
        Wait(game, 10);
        Assert.Null(game.BuildingAt(first));
        Assert.NotNull(game.BuildingAt(second));
    }

    [Fact]
    public void NukePrices_MatchOpenFront()
    {
        var (game, _, _, _) = Setup(split: false);
        Assert.Equal(750_000, game.NukeCost(NukeType.Moab));
        Assert.Equal(5_000_000, game.NukeCost(NukeType.FatMan));
        Assert.Equal(25_000_000, game.NukeCost(NukeType.TsarBomba));
        Assert.Equal(70, Balance.SamRange(1));
    }

    private static void Near(long expected, long actual, long tolerance) =>
        Assert.InRange(actual, expected - tolerance, expected + tolerance);

    private static int Dist(GameMap map, int a, int b) =>
        Math.Abs(a % map.Width - b % map.Width) + Math.Abs(a / map.Width - b / map.Width);
}
