using System.Linq;
using System.Reflection;
using Boundari.Map;
using Boundari.Sim;
using Xunit;

/// <summary>OpenFront's severed-land rule: land fallout cuts off from the main body, with no way out, goes to the enemy around it.</summary>
public class SeveredLandTests
{
    private static readonly MethodInfo SetOwnerMethod =
        typeof(Game).GetMethod("SetOwner", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static void SetOwner(Game game, int tile, ushort owner) => SetOwnerMethod.Invoke(game, new object[] { tile, owner });

    private static void AddFallout(Game game, int tile)
    {
        SetOwner(game, tile, Game.Nobody);
        game.Fallout[tile] = true;
        var count = typeof(Game).GetProperty(nameof(Game.FalloutTiles))!;
        count.SetValue(game, game.FalloutTiles + 1);
    }

    /// <summary>
    /// A owns the west of the biggest landmass and B the east. Right at their border, a
    /// 4x6 block of A's land is ringed (west, north, south) by a one-tile band that's either
    /// fallout or plain empty land; the ring also touches the rest of A's land.
    /// </summary>
    private static (Game Game, Player A, Player B, int[] Piece) Setup(bool fallout)
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
        int w = map.Width, mid = (int)land.Select(t => t % w).Average();
        foreach (int t in land)
            SetOwner(game, t, t % w >= mid ? b.Id : a.Id);

        // Somewhere along the border with land (not coast) all around the block and ring.
        bool Inland(int x, int y) => map.InBounds(x, y) && map.LandmassId[map.Index(x, y)] == biggest && !game.IsCoastal(map.Index(x, y));
        int y0 = Enumerable.Range(10, map.Height - 20).First(y =>
            Enumerable.Range(mid - 10, 14).All(x => Enumerable.Range(y - 4, 14).All(yy => Inland(x, yy))));
        var piece = (from x in Enumerable.Range(mid - 4, 4) from y in Enumerable.Range(y0, 6) select map.Index(x, y)).ToArray();
        foreach (int x in Enumerable.Range(mid - 5, 5)) // the ring: west column, north and south rows
            foreach (int y in Enumerable.Range(y0 - 1, 8))
            {
                int t = map.Index(x, y);
                if (piece.Contains(t))
                    continue;
                if (fallout)
                    AddFallout(game, t);
                else
                    SetOwner(game, t, Game.Nobody);
            }
        return (game, a, b, piece);
    }

    [Fact]
    public void LandCutOffByFallout_GoesToTheEnemyAroundIt()
    {
        var (game, a, b, piece) = Setup(fallout: true);
        for (int t = 0; t < 45; t++)
            game.Step();
        Assert.All(piece, t => Assert.Equal(b.Id, game.Owner[t]));
        Assert.True(a.TileCount > 1000, "the main body stays A's");
    }

    [Fact]
    public void LandBehindPlainEmptyLand_IsNotTaken()
    {
        var (game, a, _, piece) = Setup(fallout: false);
        for (int t = 0; t < 45; t++)
            game.Step();
        Assert.All(piece, t => Assert.Equal(a.Id, game.Owner[t]));
    }
}
