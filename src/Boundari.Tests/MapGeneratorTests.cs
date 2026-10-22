using System.Linq;
using Boundari.Map;
using Xunit;

public class MapGeneratorTests
{
    // Smaller than the real map so the tests stay fast.
    private static MapGenSettings Small() => new() { Width = 640, Height = 400, MinIslandTiles = 8, MinLakeTiles = 30 };

    [Fact]
    public void SameSeed_GivesIdenticalMap()
    {
        var a = MapGenerator.Generate(42, Small());
        var b = MapGenerator.Generate(42, Small());

        Assert.Equal(a.Terrain, b.Terrain);
        Assert.Equal(a.CoastField, b.CoastField);
        Assert.Equal(a.Elevation, b.Elevation);
    }

    [Fact]
    public void DifferentSeeds_GiveDifferentMaps()
    {
        var a = MapGenerator.Generate(1, Small());
        var b = MapGenerator.Generate(2, Small());

        Assert.NotEqual(a.Terrain, b.Terrain);
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(777UL)]
    [InlineData(12345UL)]
    [InlineData(2024UL)]
    [InlineData(99999UL)]
    public void Map_HasSeveralContinents_AndSensibleLandShare(ulong seed)
    {
        var map = MapGenerator.Generate(seed, Small());

        double landShare = (double)map.LandTileCount / map.TileCount;
        Assert.InRange(landShare, 0.18, 0.5);
        Assert.True(map.CountLandmasses((int)(map.LandTileCount * 0.04)) >= 2);
    }

    [Fact]
    public void MapEdge_IsAlwaysOcean()
    {
        var map = MapGenerator.Generate(5, Small());

        for (int x = 0; x < map.Width; x++)
        {
            Assert.Equal(TerrainType.Ocean, map.Terrain[map.Index(x, 0)]);
            Assert.Equal(TerrainType.Ocean, map.Terrain[map.Index(x, map.Height - 1)]);
        }
        for (int y = 0; y < map.Height; y++)
        {
            Assert.Equal(TerrainType.Ocean, map.Terrain[map.Index(0, y)]);
            Assert.Equal(TerrainType.Ocean, map.Terrain[map.Index(map.Width - 1, y)]);
        }
    }

    [Fact]
    public void LandTiles_MatchCoastField_AndHaveALandmass()
    {
        var map = MapGenerator.Generate(8, Small());

        for (int i = 0; i < map.TileCount; i++)
        {
            Assert.Equal(map.CoastField[i] > 0, map.IsLand(i));
            Assert.Equal(map.IsLand(i), map.LandmassId[i] != GameMap.NoLandmass);
        }
        Assert.Equal(map.LandTileCount, map.LandmassSizes.Sum());
    }

    [Fact]
    public void AllTerrainTypes_Appear()
    {
        var map = MapGenerator.Generate(3, Small());

        Assert.Contains(TerrainType.Plains, map.Terrain);
        Assert.Contains(TerrainType.Highlands, map.Terrain);
        Assert.Contains(TerrainType.Mountains, map.Terrain);
    }
}

public class MapFileTests
{
    [Fact]
    public void WriteThenRead_KeepsTheMap()
    {
        var map = MapGenerator.Generate(11, new MapGenSettings { Width = 300, Height = 200, MinIslandTiles = 8, MinLakeTiles = 30 });
        map.Name = "Test";

        using var stream = new System.IO.MemoryStream();
        MapFile.Write(map, stream);
        stream.Position = 0;
        var loaded = MapFile.Read(stream);

        Assert.Equal("Test", loaded.Name);
        Assert.Equal(map.Width, loaded.Width);
        Assert.Equal(map.Terrain, loaded.Terrain);
        Assert.Equal(map.LandTileCount, loaded.LandTileCount);
        Assert.Equal(map.LandmassSizes, loaded.LandmassSizes);
        // The coast field is stored as 16-bit floats, elevation as 16-bit fractions (finer:
        // under 0.1 m even on high ground), so allow a little rounding.
        for (int i = 0; i < map.TileCount; i++)
        {
            Assert.Equal(map.CoastField[i], loaded.CoastField[i], 0.002f);
            Assert.Equal(map.Elevation[i], loaded.Elevation[i], 0.00001f);
        }
    }

    [Fact]
    public void Ardennes_IsAllClaimableLandWithDrawnRivers()
    {
        string dir = System.AppContext.BaseDirectory;
        while (!System.IO.File.Exists(System.IO.Path.Combine(dir, "Boundari.sln")) || !System.IO.Directory.Exists(System.IO.Path.Combine(dir, "src")))
            dir = System.IO.Path.GetDirectoryName(dir)!;
        using var file = System.IO.File.OpenRead(System.IO.Path.Combine(dir, "src", "Boundari", "maps", "ardennes.bmap"));
        var map = MapFile.Read(file);

        Assert.Equal("Ardennes", map.Name);
        Assert.Equal(1200, map.Width);
        Assert.Equal(1.0, map.TileScale);
        Assert.Equal(map.TileCount, map.LandTileCount); // no sea or lakes: rivers are only drawn
        Assert.Single(map.LandmassSizes);
        Assert.All(map.CoastField, v => Assert.True(float.IsFinite(v) && v > 0));
        var cover = Assert.IsType<LandCover>(map.Cover);
        double water = cover.Cells.Count(c => c == CoverType.Water) / (double)cover.Cells.Length;
        Assert.InRange(water, 0.003, 0.02); // the Ourthe, Sure, Wiltz, Clerve and a few lakes
    }
}

public class MapGeoTests
{
    [Fact]
    public void MapFile_KeepsRealWorldLocation()
    {
        var map = MapGenerator.Generate(4, new MapGenSettings { Width = 200, Height = 150, MinIslandTiles = 8, MinLakeTiles = 30 });
        map.Geo = new MapGeo(-25, 45, 33, 72, 50);

        using var stream = new System.IO.MemoryStream();
        MapFile.Write(map, stream);
        stream.Position = 0;

        Assert.Equal(map.Geo, MapFile.Read(stream).Geo);
    }

    [Fact]
    public void Geo_ConvertsBetweenTilesAndDegrees()
    {
        var geo = new MapGeo(-25, 45, 33, 72, 50);
        int width = 2000, height = geo.HeightFor(width);

        Assert.Equal(-25, geo.LonAt(0, width), 6);
        Assert.Equal(72, geo.LatAt(0, height), 6);
        Assert.Equal(1000, geo.TileX(geo.LonAt(1000, width), width), 6);
        Assert.Equal(500, geo.TileY(geo.LatAt(500, height), height), 6);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(120f)]
    [InlineData(350f)]
    [InlineData(1500f)]
    [InlineData(4800f)]
    public void ElevationScale_RoundTrips(float metres)
    {
        Assert.Equal(metres, ElevationScale.ToMetres(ElevationScale.ToElevation(metres)), 0.5f);
    }
}

public class LandCoverTests
{
    [Fact]
    public void MapFile_KeepsLandCover()
    {
        var map = MapGenerator.Generate(6, new MapGenSettings { Width = 120, Height = 80, MinIslandTiles = 8, MinLakeTiles = 30 });
        map.Cover = new LandCover(4, map.Width, map.Height);
        for (int i = 0; i < map.Cover.Cells.Length; i++)
            map.Cover.Cells[i] = (CoverType)(i % 7);

        using var stream = new System.IO.MemoryStream();
        MapFile.Write(map, stream);
        stream.Position = 0;
        var loaded = MapFile.Read(stream).Cover;

        Assert.NotNull(loaded);
        Assert.Equal(4, loaded!.Scale);
        Assert.Equal(map.Width * 4, loaded.Width);
        Assert.Equal(map.Cover.Cells, loaded.Cells);
    }
}
