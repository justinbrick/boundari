using System;
using System.Linq;
using System.Reflection;
using Boundari.Map;
using Boundari.Sim;
using Xunit;
using Xunit.Abstractions;

/// <summary>Land attacks behave like OpenFront's (AttackExecution + EngineConfig.attackLogic).</summary>
public class AttackTests
{
    private readonly ITestOutputHelper _out;
    public AttackTests(ITestOutputHelper output) => _out = output;

    private static readonly MethodInfo SetOwnerMethod =
        typeof(Game).GetMethod("SetOwner", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static void SetTroops(Player p, long troops) => typeof(Player).GetProperty(nameof(Player.Troops))!.SetValue(p, troops);

    /// <summary>A owns the west half of the biggest landmass, B the east half (all flat plains).</summary>
    private static (Game Game, Player A, Player B) Setup()
    {
        var map = MapGenerator.Generate(21, new MapGenSettings { Width = 400, Height = 250, MinIslandTiles = 8, MinLakeTiles = 30 });
        for (int t = 0; t < map.TileCount; t++)
            if (map.IsLand(t))
                map.Terrain[t] = TerrainType.Plains;
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
            SetOwnerMethod.Invoke(game, new object[] { t, t % map.Width >= midX ? b.Id : a.Id });
        return (game, a, b);
    }

    /// <summary>Tiles A takes from B in a few ticks with this many attacking troops, against B holding this many.</summary>
    private int TilesTaken(long attackTroops, long defenderTroops, int ticks = 10)
    {
        var (game, a, b) = Setup();
        SetTroops(a, attackTroops);
        SetTroops(b, defenderTroops);
        int before = b.TileCount;
        game.Submit(new AttackIntent(a.Id, b.Id, 100));
        for (int t = 0; t < ticks; t++)
        {
            SetTroops(b, defenderTroops); // hold the defender steady (no growth, no losses) to isolate the attack's pace
            game.Step();
        }
        return before - b.TileCount;
    }

    [Fact]
    public void BiggerArmies_TakeLandFaster()
    {
        int weak = TilesTaken(30_000, 200_000);
        int even = TilesTaken(200_000, 200_000);
        int strong = TilesTaken(1_000_000, 200_000);
        _out.WriteLine($"tiles in 10 ticks: weak {weak}, even {even}, strong {strong}");
        Assert.True(even > weak * 3, "an even fight should go several times faster than a hopeless one");
        Assert.True(strong > even, "an overwhelming army should go faster still");
    }

    [Fact]
    public void ThinlyHeldLand_IsOverrunQuickly()
    {
        // A big army against a big, nearly empty territory pours through it.
        var (game, a, b) = Setup();
        SetTroops(a, 500_000);
        SetTroops(b, 2_000);
        int start = b.TileCount;
        int front = Enumerable.Range(0, game.Map.TileCount).Count(t => game.Owner[t] == b.Id && game.Touches(t, a.Id));
        game.Submit(new AttackIntent(a.Id, b.Id, 100));
        game.Step();
        game.Step();
        int perTick = (start - b.TileCount) / 2;
        _out.WriteLine($"B had {start} tiles, front {front}; taken per tick: {perTick}");
        // OpenFront: at overwhelming odds a tile costs 0.82/8.55 * 16.5 = 1.58 of the budget,
        // spread over the front, so about 60% of the front's length falls every tick.
        Assert.True(perTick > front / 2, "with overwhelming odds most of the front falls every tick");
    }

    [Fact]
    public void AttackLogic_MatchesOpenFrontsFormula()
    {
        var (game, a, b) = Setup();
        SetTroops(b, 100_000);
        var attack = (Attack)typeof(Attack).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)[0]
            .Invoke(new object[] { a, b.Id, 50_000L, -1, new Boundari.Core.Rng(1) });
        var logic = typeof(Game).GetMethod("AttackLogic", BindingFlags.NonPublic | BindingFlags.Instance)!;
        int plains = Enumerable.Range(0, game.Map.TileCount).First(t => game.Owner[t] == b.Id);
        var (loss, defLoss, frac) = ((double, double, double))logic.Invoke(game, new object[] { attack, plains, b, 50_000.0, 100 })!;

        // OpenFront by hand: plains mag 80, tileCost 16.5; ratio = 100k / 50k = 2.
        double density = 100_000.0 / b.TileCount;
        double la = 1 - 0.7 * Sig(a.TileCount), ld = 1 - 0.3 * Sig(b.TileCount), las = 1 - 0.73 * Sig(a.TileCount);
        double expectLoss = 80 * 2 * (0.463 * la * ld + 0.0039 * density);
        double expectFrac = 2 * 1 / 8.55 * 16.5 * las * ld / 100;
        Assert.Equal(density, defLoss, 6);
        Assert.Equal(expectLoss, loss, 6);
        Assert.Equal(expectFrac, frac, 9);

        // Empty land: 80/5 troops a tile; pace from 2000 * 16.5 / troops, clamped to 5..100, over twice the front.
        var (tnLoss, _, tnFrac) = ((double, double, double))logic.Invoke(game, new object[] { attack, plains, null!, 3_000.0, 40 })!;
        Assert.Equal(16, tnLoss, 6);
        Assert.Equal(Math.Clamp(2000 * 16.5 / 3000, 5, 100) / 80, tnFrac, 9);

        static double Sig(int n) => 1 / (1 + Math.Exp(-2.5 * (Math.Log(n) - Math.Log(300_000))));
    }
}
