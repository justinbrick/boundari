using System.Linq;
using Boundari.Map;
using Boundari.Sim;
using Boundari.View;
using Xunit;

/// <summary>Light fog of war: only you and your allies show troops and gold.</summary>
public class FogTests
{
    [Fact]
    public void OnlyYouAndAllies_AreSeen()
    {
        var map = MapGenerator.Generate(21, new MapGenSettings { Width = 400, Height = 250, MinIslandTiles = 8, MinLakeTiles = 30 });
        var game = new Game(map, 1, spawnPhaseTicks: 2);
        var me = game.AddPlayer("Me");
        var ally = game.AddPlayer("Ally");
        var other = game.AddPlayer("Other");
        int biggest = Enumerable.Range(0, map.LandmassSizes.Length).OrderByDescending(i => map.LandmassSizes[i]).First();
        int[] land = Enumerable.Range(0, map.TileCount).Where(t => map.LandmassId[t] == biggest).ToArray();
        game.Submit(new SpawnIntent(me.Id, land[0]));
        game.Submit(new SpawnIntent(ally.Id, land[land.Length / 2]));
        game.Submit(new SpawnIntent(other.Id, land[^1]));
        for (int t = 0; t < 4; t++)
            game.Step();
        game.Submit(new AllianceIntent(me.Id, ally.Id));
        game.Step();
        game.Submit(new AllianceIntent(ally.Id, me.Id));
        game.Step();
        Assert.True(me.IsAlliedWith(ally));

        var intel = new TroopIntel();
        intel.SetGame(game, me);
        Assert.True(intel.Sees(me));
        Assert.True(intel.Sees(ally));
        Assert.False(intel.Sees(other));
        Assert.False(intel.ShowsGold(other));

        game.FogOfWar = false;
        Assert.True(intel.Sees(other));
    }
}
