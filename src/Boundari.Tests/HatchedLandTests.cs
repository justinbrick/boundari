using System.Linq;
using System.Reflection;
using Boundari.Map;
using Boundari.Sim;
using Xunit;

/// <summary>Hatched (occupied) land: land taken from a player settles after a while, or when its owner builds on it.</summary>
public class HatchedLandTests
{
    private static readonly MethodInfo SetOwnerMethod =
        typeof(Game).GetMethod("SetOwner", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static void SetOwner(Game game, int tile, ushort owner) => SetOwnerMethod.Invoke(game, new object[] { tile, owner });

    /// <summary>A owns the west of the biggest landmass and B the east, all plains; the match is running.</summary>
    private static (Game Game, Player A, Player B, int[] Land) Setup()
    {
        var map = MapGenerator.Generate(21, new MapGenSettings { Width = 400, Height = 250, MinIslandTiles = 8, MinLakeTiles = 30 });
        for (int t = 0; t < map.TileCount; t++)
            if (map.IsLand(t))
                map.Terrain[t] = TerrainType.Plains;
        var game = new Game(map, 1, spawnPhaseTicks: 2) { HatchedLand = false };
        var a = game.AddPlayer("A");
        var b = game.AddPlayer("B");
        int biggest = Enumerable.Range(0, map.LandmassSizes.Length).OrderByDescending(i => map.LandmassSizes[i]).First();
        int[] land = Enumerable.Range(0, map.TileCount).Where(t => map.LandmassId[t] == biggest).ToArray();
        game.Submit(new SpawnIntent(a.Id, land[0]));
        game.Submit(new SpawnIntent(b.Id, land[^1]));
        for (int t = 0; t < 4; t++)
            game.Step();
        int w = map.Width, mid = (int)land.Select(t => t % w).Average();
        foreach (int t in land)
            SetOwner(game, t, t % w >= mid ? b.Id : a.Id);
        game.HatchedLand = true;
        return (game, a, b, land);
    }

    /// <summary>B's tiles in a block of w x h just east of the border, starting this many rows down.</summary>
    private static int[] BlockOfB(Game game, Player b, int rowsDown, int width = 5, int height = 5)
    {
        var map = game.Map;
        int y0 = Enumerable.Range(0, map.Height).Where(y => Enumerable.Range(0, map.Width).Any(x => game.Owner[map.Index(x, y)] == b.Id)).Skip(rowsDown).First();
        var block = new System.Collections.Generic.List<int>();
        for (int y = y0; y < y0 + height; y++)
        {
            int first = Enumerable.Range(0, map.Width).First(x => game.Owner[map.Index(x, y)] == b.Id);
            for (int x = first; x < first + width; x++)
                if (game.Owner[map.Index(x, y)] == b.Id)
                    block.Add(map.Index(x, y));
        }
        return block.ToArray();
    }

    [Fact]
    public void LandTakenFromAPlayer_IsHatched_EmptyLandIsNot()
    {
        var (game, a, b, _) = Setup();
        int taken = BlockOfB(game, b, 40)[0];
        SetOwner(game, taken, a.Id);
        Assert.True(game.IsHatched(taken));
        Assert.Equal(1, a.HatchedTiles);

        int empty = Enumerable.Range(0, game.Map.TileCount).First(t => game.Map.IsLand(t) && game.Owner[t] == Game.Nobody);
        SetOwner(game, empty, a.Id);
        Assert.False(game.IsHatched(empty));
        Assert.Equal(1, a.HatchedTiles);
    }

    [Fact]
    public void HatchedLand_AddsNoTroopRoom_UntilItSettles()
    {
        var (game, a, b, _) = Setup();
        long capBefore = a.MaxTroops;
        var block = BlockOfB(game, b, 40, 20, 20);
        foreach (int t in block)
            SetOwner(game, t, a.Id);
        Assert.Equal(block.Length, a.HatchedTiles);
        Assert.Equal(capBefore, a.MaxTroops);

        for (int t = 0; t <= HatchRules.SettleTicks; t++) // settles at the start of tick capture + SettleTicks
            game.Step();
        Assert.Equal(0, a.HatchedTiles);
        Assert.All(block, t => Assert.False(game.IsHatched(t)));
        Assert.True(a.MaxTroops > capBefore);
    }

    [Fact]
    public void RetakingYourOwnHatchedLoss_IsNormalLandAtOnce()
    {
        var (game, a, b, _) = Setup();
        int tile = BlockOfB(game, b, 40)[0];
        SetOwner(game, tile, a.Id);
        SetOwner(game, tile, b.Id);
        Assert.False(game.IsHatched(tile));
        Assert.Equal(0, a.HatchedTiles);
        Assert.Equal(0, b.HatchedTiles);
    }

    [Fact]
    public void Building_SecuresTheWholeConnectedHatchedPatch_Only()
    {
        var (game, a, b, _) = Setup();
        game.SetCheats(a, infiniteGold: true, instantBuild: true);
        var patch = BlockOfB(game, b, 30, 6, 6);
        var other = BlockOfB(game, b, 60, 6, 6); // far from the first: a separate patch
        foreach (int t in patch.Concat(other))
            SetOwner(game, t, a.Id);
        Assert.Equal(patch.OrderBy(t => t), game.HatchedPatch(a, patch[^1]).OrderBy(t => t)); // what placing it would highlight
        Assert.Empty(game.HatchedPatch(b, patch[^1]));

        game.Submit(new BuildIntent(a.Id, BuildingType.City, patch[^1]));
        game.Step();
        Assert.NotNull(game.BuildingAt(patch[^1]));
        Assert.All(patch, t => Assert.False(game.IsHatched(t)));
        Assert.All(other, t => Assert.True(game.IsHatched(t)));
        Assert.Equal(other.Length, a.HatchedTiles);
    }

    [Fact]
    public void ClassicRules_HatchNothing()
    {
        var (game, a, b, _) = Setup();
        game.HatchedLand = false;
        foreach (int t in BlockOfB(game, b, 40))
            SetOwner(game, t, a.Id);
        Assert.Equal(0, a.HatchedTiles);
    }

    [Fact]
    public void AnAttack_LeavesItsConquestHatched_AndCountsMatch()
    {
        var (game, a, b, _) = Setup();
        game.Submit(new AttackIntent(a.Id, b.Id, 50));
        for (int t = 0; t < 30; t++)
            game.Step();
        int hatched = Enumerable.Range(0, game.Map.TileCount).Count(t => game.Owner[t] == a.Id && game.IsHatched(t));
        Assert.True(hatched > 0);
        Assert.Equal(hatched, a.HatchedTiles);
        Assert.Equal(0, b.HatchedTiles);
    }
}
