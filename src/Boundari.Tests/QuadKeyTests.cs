using System;
using Xunit;

public class QuadKeyTests
{
    [Fact]
    public void FromCoords_ToCoords_Roundtrips_ForSamplePoints()
    {
        var depth = Quadtree<int>.MaxSupportedDepth;
        int size = 1 << depth;

        (int x, int y)[] samples = new[]
        {
            (0, 0),
            (1, 0),
            (0, 1),
            (1234, 5678),
            (size - 1, size - 1),
        };

        foreach (var (x, y) in samples)
        {
            var key = QuadKey.FromCoords(x, y);
            Assert.Equal(depth, key.Depth);
            Assert.Equal(depth, key.Quadrants.Length);
            var coords = key.ToCoords();
            Assert.Equal(x, coords.X);
            Assert.Equal(y, coords.Y);
        }
    }

    [Fact]
    public void QuadKey_WithCustomDepth_ConvertsToCoords()
    {
        byte depth = 3;
        int max = (1 << depth) - 1;
        int x = 5 & max; // within 0..7
        int y = 2 & max;

        // Build quadrants the same way FromCoords does for a given depth.
        var quadrants = new Quadrant[depth];
        for (int i = 0; i < depth; i++)
        {
            int bitPos = depth - 1 - i;
            int xb = (x >> bitPos) & 1;
            int yb = (y >> bitPos) & 1;
            quadrants[i] = (Quadrant)((yb << 1) | xb);
        }

        var key = new QuadKey(depth, quadrants);
        Assert.Equal(depth, key.Depth);
        Assert.Equal(quadrants.Length, key.Quadrants.Length);
        // Quadrants property returns a copy, but values should match.
        for (int i = 0; i < depth; i++)
            Assert.Equal(quadrants[i], key.Quadrants[i]);

        var coords = key.ToCoords();
        Assert.Equal(x, coords.X);
        Assert.Equal(y, coords.Y);
    }

    [Fact]
    public void FromCoords_InvalidCoordinates_Throw()
    {
        // negative coordinates
        Assert.Throws<ArgumentOutOfRangeException>(() => QuadKey.FromCoords(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => QuadKey.FromCoords(0, -1));

        var depth = Quadtree<int>.MaxSupportedDepth;
        int size = 1 << depth;
        // out of range (too large)
        Assert.Throws<ArgumentOutOfRangeException>(() => QuadKey.FromCoords(size, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => QuadKey.FromCoords(0, size));
    }
}
