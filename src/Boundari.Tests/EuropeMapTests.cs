using System;
using System.IO;
using Boundari.Map;
using Boundari.Sim;
using Xunit;

/// <summary>Checks on the built Europe map (maps/europe.bmap).</summary>
public class EuropeMapTests
{
    private static GameMap Europe()
    {
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "Boundari.sln")) || !Directory.Exists(Path.Combine(dir, "src")))
            dir = Path.GetDirectoryName(dir)!;
        using var file = File.OpenRead(Path.Combine(dir, "src", "Boundari", "maps", "europe.bmap"));
        return MapFile.Read(file);
    }

    [Fact]
    public void EveryNation_StartsOnPlayableLand_AndThePlayAreaHasItsField()
    {
        var map = Europe();
        var geo = map.Geo!;
        foreach (var nation in MapNations.Europe)
        {
            // (a home may sit on a lake: the nation starts on the nearest land)
            int x = (int)geo.TileX(nation.Lon, map.Width), y = (int)geo.TileY(nation.Lat, map.Height);
            bool near = false;
            for (int dy = -15; dy <= 15 && !near; dy++)
                for (int dx = -15; dx <= 15 && !near; dx++)
                    near = map.InBounds(x + dx, y + dy) && map.IsLand(map.Index(x + dx, y + dy));
            Assert.True(near, $"{nation.Name}'s home has no playable land near it");
        }
        Assert.NotNull(map.PlayField);
        int disagree = 0;
        for (int i = 0; i < map.TileCount; i++)
        {
            bool land = map.CoastField[i] > 0;
            int value = map.PlayField![i * 2 + (land ? 0 : 1)] - 128;
            if (map.IsOutside(i) ? value > 1 : value < -1)
                disagree++;
        }
        Assert.True(disagree < map.TileCount / 10000, $"{disagree} tiles where the drawn edge and the playable tiles disagree");
    }
}
