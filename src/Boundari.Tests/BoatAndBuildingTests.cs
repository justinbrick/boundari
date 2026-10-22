using System.Linq;
using Boundari.Map;
using Boundari.Sim;
using Xunit;

public class BoatAndBuildingTests
{
    private static (Game Game, Player Me, GameMap Map) StartOnCoast(ulong seed = 21)
    {
        var map = MapGenerator.Generate(seed, new MapGenSettings { Width = 400, Height = 250, MinIslandTiles = 8, MinLakeTiles = 30 });
        var game = new Game(map, 1, spawnPhaseTicks: 2) { BoatsAllowed = true };
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
    public void BoatsAndWarships_AreOffByDefault()
    {
        var (game, me, map) = StartOnCoast();
        game.BoatsAllowed = false; // the dots game's default
        int home = map.LandmassId[game.Owner.ToList().IndexOf(me.Id)];
        int target = Enumerable.Range(0, map.TileCount)
            .First(t => map.IsLand(t) && map.LandmassId[t] != home && map.LandmassSizes[map.LandmassId[t]] > 300);
        long before = me.Troops;
        game.Submit(new BoatIntent(me.Id, target, 50));
        game.Step();
        Assert.Empty(game.Boats);
        Assert.True(me.Troops >= before, "no troops should have boarded");
        Assert.False(game.CanSendBoat(me));
        Assert.DoesNotContain(Enumerable.Range(0, map.TileCount), t => map.IsWater(t) && game.CanBuild(me, BuildingType.Warship, t));
        Assert.False(new Game(map, 1).BoatsAllowed);
    }

    [Fact]
    public void CalledBackBoat_SailsHome_AndLandsNothing()
    {
        var (game, me, map) = StartOnCoast();
        int home = map.LandmassId[game.Owner.ToList().IndexOf(me.Id)];
        int target = Enumerable.Range(0, map.TileCount)
            .First(t => map.IsLand(t) && map.LandmassId[t] != home && map.LandmassSizes[map.LandmassId[t]] > 300);
        game.Submit(new BoatIntent(me.Id, target, 50));
        game.Step();
        var boat = Assert.Single(game.Boats);
        for (int t = 0; t < 10; t++)
            game.Step();
        game.Submit(new BoatRetreatIntent(me.Id, boat.Id));
        for (int t = 0; t < 2000 && game.Boats.Count > 0; t++)
            game.Step();
        Assert.Empty(game.Boats);
        Assert.True(boat.Retreating);
        int landed = Enumerable.Range(0, map.TileCount).Count(t => game.Owner[t] == me.Id && map.LandmassId[t] == map.LandmassId[target]);
        Assert.Equal(0, landed);
    }

    [Fact]
    public void Boat_LandsOnTheClickedPlayersOwnCoast_OrNotAtAll()
    {
        var map = MapGenerator.Generate(21, new MapGenSettings { Width = 400, Height = 250, MinIslandTiles = 8, MinLakeTiles = 30 });
        var game = new Game(map, 1, spawnPhaseTicks: 3) { BoatsAllowed = true };
        var me = game.AddPlayer("Me");
        var inland = game.AddPlayer("Inland");
        var coastal = game.AddPlayer("Coastal");
        var byArea = Enumerable.Range(0, map.LandmassSizes.Length).OrderByDescending(i => map.LandmassSizes[i]).ToArray();
        int home = byArea[0], other = byArea[1];
        int myStart = Enumerable.Range(0, map.TileCount).First(t => map.LandmassId[t] == home && game.IsCoastal(t));
        // On the other landmass: one player far from any coast, one right on it.
        int DistToCoast(int t)
        {
            int x = t % map.Width, y = t / map.Width, best = int.MaxValue;
            for (int dy = -25; dy <= 25; dy++)
                for (int dx = -25; dx <= 25; dx++)
                    if (map.InBounds(x + dx, y + dy) && !map.IsLand(map.Index(x + dx, y + dy)))
                        best = System.Math.Min(best, System.Math.Abs(dx) + System.Math.Abs(dy));
            return best;
        }
        int deep = Enumerable.Range(0, map.TileCount).Where(t => map.LandmassId[t] == other && t % 7 == 0).OrderByDescending(DistToCoast).First();
        int shore = Enumerable.Range(0, map.TileCount).First(t => map.LandmassId[t] == other && game.IsCoastal(t)
            && System.Math.Abs(t % map.Width - deep % map.Width) + System.Math.Abs(t / map.Width - deep / map.Width) > 40);
        game.Submit(new SpawnIntent(me.Id, myStart));
        game.Submit(new SpawnIntent(inland.Id, deep));
        game.Submit(new SpawnIntent(coastal.Id, shore));
        for (int t = 0; t < 5; t++)
            game.Step();
        Assert.True(DistToCoast(deep) > Balance.SpawnRadius + 2, "test needs an inland spot");

        game.Submit(new BoatIntent(me.Id, deep, 50)); // a landlocked player: no coast of theirs to land on
        game.Step();
        Assert.Empty(game.Boats);

        game.Submit(new BoatIntent(me.Id, shore, 50));
        game.Step();
        var boat = Assert.Single(game.Boats);
        Assert.Equal(coastal.Id, game.Owner[boat.LandingTile]);
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
        Assert.InRange(me.Gold, 5_000_000 - 125_000, 5_000_000 - 125_000 + 1000); // first port costs 125K, plus a tick of income
        Assert.Equal(250_000, game.BuildingCost(me, BuildingType.Port));    // the next one doubles
        Assert.Equal(250_000, game.BuildingCost(me, BuildingType.Factory)); // ports and factories share a count
        Assert.False(game.CanBuild(me, BuildingType.City, coast)); // too close to the port
    }

    private static int FirstNotMine(Game game, Player me) =>
        Enumerable.Range(0, game.Map.TileCount).First(t => game.Map.IsLand(t) && game.Owner[t] != me.Id);

    private static void GiveGold(Player p, long gold) =>
        typeof(Player).GetProperty(nameof(Player.Gold))!.SetValue(p, gold);
}

public class TradeTests
{
    [Fact]
    public void TradeGold_PunishesShortRoutes_AndKeepsGrowingWithDistance()
    {
        Assert.True(TradeRules.Gold(50) < 5_000);
        Assert.True(TradeRules.Gold(300) > 35_000 && TradeRules.Gold(300) < 60_000);
        Assert.True(TradeRules.Gold(1000) > TradeRules.Gold(600));
    }

    [Fact]
    public void SpawnOdds_PityTimerAndFleetDamping()
    {
        Assert.True(TradeRules.SpawnOdds(5, 0) < TradeRules.SpawnOdds(0, 0));   // failed rolls raise the odds
        Assert.True(TradeRules.SpawnOdds(0, 600) > TradeRules.SpawnOdds(0, 50)); // big fleets slow spawning
        Assert.Equal(int.MaxValue, TradeRules.SpawnOdds(0, TradeRules.MaxTradeShips));
    }
}

public class CombatTests
{
    [Fact]
    public void Costs_FollowOpenFrontScaling()
    {
        Assert.Equal(125_000, Balance.BuildingCost(BuildingType.City, 0));
        Assert.Equal(500_000, Balance.BuildingCost(BuildingType.City, 2));
        Assert.Equal(1_000_000, Balance.BuildingCost(BuildingType.City, 9));
        Assert.Equal(150_000, Balance.BuildingCost(BuildingType.Defense, 2));
        Assert.Equal(250_000, Balance.BuildingCost(BuildingType.Defense, 20));
        Assert.Equal(1_000_000, Balance.BuildingCost(BuildingType.Silo, 5));
        Assert.Equal(3_000_000, Balance.BuildingCost(BuildingType.Sam, 1));
        Assert.Equal(500_000, Balance.BuildingCost(BuildingType.Warship, 1));
    }

    [Fact]
    public void PocketsSurroundedByOnePlayer_AreAbsorbed()
    {
        // A holds the west half of the continent and B the east; A also keeps a small pocket
        // deep inside B's half. B walls the pocket in, so it's handed to B (OpenFront's clusters).
        var map = MapGenerator.Generate(21, new MapGenSettings { Width = 400, Height = 250, MinIslandTiles = 8, MinLakeTiles = 30 });
        var game = new Game(map, 1, spawnPhaseTicks: 2) { BoatsAllowed = true };
        var a = game.AddPlayer("A");
        var b = game.AddPlayer("B");
        int biggest = Enumerable.Range(0, map.LandmassSizes.Length).OrderByDescending(i => map.LandmassSizes[i]).First();
        int[] land = Enumerable.Range(0, map.TileCount).Where(t => map.LandmassId[t] == biggest).ToArray();
        game.Submit(new SpawnIntent(a.Id, land[0]));
        game.Submit(new SpawnIntent(b.Id, land[^1]));
        for (int t = 0; t < 3; t++)
            game.Step();
        int midX = (int)land.Select(t => t % map.Width).Average();
        int pocket = land.Where(t => t % map.Width > midX + 20 && !game.IsCoastal(t)).OrderByDescending(t => DistanceToCoast(game, t)).First();
        int px = pocket % map.Width, py = pocket / map.Width;
        var setOwner = typeof(Game).GetMethod("SetOwner", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        foreach (int t in land)
        {
            int dx = t % map.Width - px, dy = t / map.Width - py;
            bool inPocket = dx * dx + dy * dy <= 16;
            setOwner.Invoke(game, new object[] { t, inPocket || t % map.Width < midX ? a.Id : b.Id });
        }
        for (int t = 0; t < 30; t++)
            game.Step();
        Assert.Equal(b.Id, game.Owner[pocket]);
        Assert.True(a.TileCount > 100, "A's main body (on the coast) is left alone");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BigCutOffPockets_StayWithTheirOwner_UnlessClassic(bool keepCutOffLand)
    {
        // As above, but the pocket is bigger than ClusterRules.MaxHandOverTiles: Boundari
        // leaves it with A (to be conquered the usual way); classic OpenFront hands it to B.
        var map = MapGenerator.Generate(21, new MapGenSettings { Width = 400, Height = 250, MinIslandTiles = 8, MinLakeTiles = 30 });
        var game = new Game(map, 1, spawnPhaseTicks: 2) { KeepCutOffLand = keepCutOffLand, BoatsAllowed = true };
        var a = game.AddPlayer("A");
        var b = game.AddPlayer("B");
        int biggest = Enumerable.Range(0, map.LandmassSizes.Length).OrderByDescending(i => map.LandmassSizes[i]).First();
        int[] land = Enumerable.Range(0, map.TileCount).Where(t => map.LandmassId[t] == biggest).ToArray();
        game.Submit(new SpawnIntent(a.Id, land[0]));
        game.Submit(new SpawnIntent(b.Id, land[^1]));
        for (int t = 0; t < 3; t++)
            game.Step();
        int midX = (int)land.Select(t => t % map.Width).Average();
        int pocket = land.Where(t => t % map.Width > midX + 20 && !game.IsCoastal(t)).OrderByDescending(t => DistanceToCoast(game, t)).First();
        int px = pocket % map.Width, py = pocket / map.Width;
        var setOwner = typeof(Game).GetMethod("SetOwner", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        int pocketTiles = 0;
        foreach (int t in land)
        {
            int dx = t % map.Width - px, dy = t / map.Width - py;
            bool inPocket = dx * dx + dy * dy <= 8 * 8;
            pocketTiles += inPocket ? 1 : 0;
            setOwner.Invoke(game, new object[] { t, inPocket || t % map.Width < midX ? a.Id : b.Id });
        }
        Assert.True(pocketTiles > game.Rules.A(ClusterRules.MaxHandOverTiles), $"the pocket should be big: {pocketTiles} tiles");
        for (int t = 0; t < 30; t++)
            game.Step();
        Assert.Equal(keepCutOffLand ? a.Id : b.Id, game.Owner[pocket]);
    }

    [Fact]
    public void APlayerWalledInByOneEnemy_IsAnnexed()
    {
        // B's whole land is a disc inside A's, with no coast: OpenFront hands all of it to A.
        var map = MapGenerator.Generate(21, new MapGenSettings { Width = 400, Height = 250, MinIslandTiles = 8, MinLakeTiles = 30 });
        var game = new Game(map, 1, spawnPhaseTicks: 2) { BoatsAllowed = true };
        var a = game.AddPlayer("A");
        var b = game.AddPlayer("B");
        int biggest = Enumerable.Range(0, map.LandmassSizes.Length).OrderByDescending(i => map.LandmassSizes[i]).First();
        int[] land = Enumerable.Range(0, map.TileCount).Where(t => map.LandmassId[t] == biggest).ToArray();
        int centre = land.Where(t => !game.IsCoastal(t)).OrderByDescending(t => DistanceToCoast(game, t)).First();
        game.Submit(new SpawnIntent(a.Id, land[0]));
        game.Submit(new SpawnIntent(b.Id, centre));
        for (int t = 0; t < 3; t++)
            game.Step();
        int cx = centre % map.Width, cy = centre / map.Width;
        var setOwner = typeof(Game).GetMethod("SetOwner", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        foreach (int t in land)
        {
            int dx = t % map.Width - cx, dy = t / map.Width - cy;
            setOwner.Invoke(game, new object[] { t, dx * dx + dy * dy < 20 * 20 ? b.Id : a.Id });
        }
        for (int t = 0; t < 30; t++)
            game.Step();
        Assert.Equal(0, b.TileCount);
        Assert.Equal(a.Id, game.Owner[centre]);
    }

    [Fact]
    public void OpposingAttacks_CancelOut()
    {
        var map = MapGenerator.Generate(21, new MapGenSettings { Width = 400, Height = 250, MinIslandTiles = 8, MinLakeTiles = 30 });
        var game = new Game(map, 1, spawnPhaseTicks: 2) { BoatsAllowed = true };
        var a = game.AddPlayer("A");
        var b = game.AddPlayer("B");
        int biggest = Enumerable.Range(0, map.LandmassSizes.Length).OrderByDescending(i => map.LandmassSizes[i]).First();
        int[] land = Enumerable.Range(0, map.TileCount).Where(t => map.LandmassId[t] == biggest).ToArray();
        game.Submit(new SpawnIntent(a.Id, land[0]));
        game.Submit(new SpawnIntent(b.Id, land[^1]));
        for (int t = 0; t < 4; t++)
            game.Step();
        // Split the landmass down the middle: west half A, east half B.
        int midX = (int)land.Select(t => t % map.Width).Average();
        var setOwner = typeof(Game).GetMethod("SetOwner", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        foreach (int t in land)
            setOwner.Invoke(game, new object[] { t, t % map.Width < midX ? a.Id : b.Id });

        game.Submit(new AttackIntent(a.Id, b.Id, 50));
        game.Step();
        long sentByA = game.Attacks.Single(x => x.Attacker == a).Troops;
        long bBefore = b.Troops;
        game.Submit(new AttackIntent(b.Id, a.Id, 10)); // smaller than A's attack
        game.Step();

        var aAttack = game.Attacks.Single(x => x.Attacker == a);
        Assert.True(aAttack.Troops < sentByA, "B's counter-attack should have eaten into A's attack");
        Assert.DoesNotContain(game.Attacks, x => x.Attacker == b);  // B's whole army was absorbed
        Assert.True(b.Troops < bBefore);
    }

    [Fact]
    public void RivalWarships_FightEachOther()
    {
        var map = MapGenerator.Generate(21, new MapGenSettings { Width = 400, Height = 250, MinIslandTiles = 8, MinLakeTiles = 30 });
        var game = new Game(map, 1, spawnPhaseTicks: 2) { BoatsAllowed = true };
        var a = game.AddPlayer("A");
        var b = game.AddPlayer("B");
        int biggest = Enumerable.Range(0, map.LandmassSizes.Length).OrderByDescending(i => map.LandmassSizes[i]).First();
        int[] coast = Enumerable.Range(0, map.TileCount).Where(t => map.LandmassId[t] == biggest && game.IsCoastal(t)).ToArray();
        int ca = coast[0];
        int cb = coast.First(t => Dist(map, t, ca) is > 40 and < 70);
        game.Submit(new SpawnIntent(a.Id, ca));
        game.Submit(new SpawnIntent(b.Id, cb));
        for (int t = 0; t < 4; t++)
            game.Step();
        var gold = typeof(Player).GetProperty(nameof(Player.Gold))!;
        foreach (var (p, home) in new[] { (a, ca), (b, cb) })
        {
            gold.SetValue(p, 10_000_000L);
            game.Submit(new BuildIntent(p.Id, BuildingType.Port, home));
        }
        for (int t = 0; t <= Balance.ConstructionTicks(BuildingType.Port); t++)
            game.Step(); // ports take a few seconds to build
        Assert.Equal(1, game.CountBuildings(a, BuildingType.Port));
        Assert.Equal(1, game.CountBuildings(b, BuildingType.Port));

        foreach (var (p, home) in new[] { (a, ca), (b, cb) })
        {
            int water = Enumerable.Range(0, map.TileCount).First(t => map.IsWater(t) && Dist(map, t, home) < 6 && game.CanBuild(p, BuildingType.Warship, t));
            game.Submit(new BuildIntent(p.Id, BuildingType.Warship, water));
        }
        game.Step();
        Assert.Equal(2, game.Warships.Count);

        bool fought = false;
        for (int t = 0; t < 1200 && !fought; t++)
        {
            game.Step();
            fought = game.Warships.Count < 2 || game.Warships.Any(w => w.Health < WarshipRules.MaxHealth - 50) || game.Shells.Count > 0;
        }
        Assert.True(fought, "warships within range of each other should open fire");
    }

    private static int Dist(GameMap map, int a, int b) =>
        System.Math.Abs(a % map.Width - b % map.Width) + System.Math.Abs(a / map.Width - b / map.Width);

    private static int DistanceToCoast(Game game, int tile)
    {
        int w = game.Map.Width, x = tile % w, y = tile / w, best = int.MaxValue;
        for (int t = 0; t < game.Map.TileCount; t += 7)
            if (game.IsCoastal(t))
                best = System.Math.Min(best, System.Math.Abs(t % w - x) + System.Math.Abs(t / w - y));
        return best;
    }
}
