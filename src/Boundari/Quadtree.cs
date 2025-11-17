using System;

public sealed class Quadtree<T>
    where T : struct
{
    /// <summary>
    /// The maximum supported depth of the quadtree.
    /// </summary>
    public const byte MaxSupportedDepth = 14;

    /// <summary>
    /// The maximum depth of this quadtree.
    /// </summary>
    public readonly byte MaxDepth;

    /// <summary>
    /// The bounds of this Quadtree, calculated as (2^MaxDepth, 2^MaxDepth).
    /// </summary>
    public readonly (int X, int Y) Size;

    public Quadtree(byte maxDepth)
    {
        if (maxDepth > MaxSupportedDepth)
            throw new ArgumentOutOfRangeException(
                nameof(maxDepth),
                $"Max depth cannot exceed ${MaxSupportedDepth}."
            );
        if (maxDepth <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(maxDepth),
                "Max depth must be at least 1."
            );

        MaxDepth = maxDepth;
        Size = (1 << maxDepth, 1 << maxDepth);
    }

    // A node can be either an internal node, or a leaf.
    public interface INode;

    public record struct InternalNode(INode[] Children) : INode;

    public record struct LeafNode(T Value) : INode;

    private INode Root = new LeafNode(default);
}

/// <summary>
/// The quadrants of a quadtree node.
/// </summary>
public enum Quadrant : byte
{
    NW = 0b10,
    NE = 0b11,
    SW = 0b00,
    SE = 0b01,
}

/// <summary>
/// Represents the key to a node in the quadtree.
/// </summary>
public struct QuadKey
{
    private int Data;
    public byte Depth
    {
        readonly get => (byte)(Data & 0b1111);
        set => Data = (Data & ~0b1111) | (value & 0b1111);
    }

    public Quadrant[] Quadrants
    {
        readonly get
        {
            var quadrants = new Quadrant[Depth];
            for (int i = 0; i < Depth; i++)
            {
                quadrants[i] = (Quadrant)((Data >> (4 + i * 2)) & 0b11);
            }
            return quadrants;
        }
        set
        {
            Data &= 0b1111; // Clear existing quadrants.
            for (int i = 0; i < value.Length; i++)
            {
                Data |= ((int)value[i] & 0b11) << (4 + i * 2);
            }
        }
    }

    // Construct data as int from depth and quadrants.
    public QuadKey(byte depth, Quadrant[] quadrants)
    {
        Depth = depth;
        Quadrants = quadrants;
    }

    /// <summary>
    /// Construct a NodePath from integer coordinates. This produces a path to the
    /// leaf at the repository's maximum supported depth (MSB-first bit mapping).
    /// </summary>
    /// <param name="x">X coordinate (0 .. 2^MaxDepth - 1)</param>
    /// <param name="y">Y coordinate (0 .. 2^MaxDepth - 1)</param>
    public static QuadKey FromCoords(int x, int y)
    {
        // Use the project's max supported depth constant. Quadtree<T>.MaxSupportedDepth
        // is a compile-time constant shared across generic instantiations; referencing
        // via a concrete generic is fine.
        const byte depth = Quadtree<int>.MaxSupportedDepth;
        if (x < 0 || y < 0)
            throw new ArgumentOutOfRangeException(nameof(x), "Coordinates must be non-negative.");
        int size = 1 << depth;
        if (x >= size || y >= size)
            throw new ArgumentOutOfRangeException(
                $"Coordinates must be less than {size} for depth {depth}."
            );

        var quadrants = new Quadrant[depth];
        // For each level from root (most-significant bit) to leaf (least), pick bits
        // at position (depth - 1 - i) so quadrants[0] is the top-level quadrant.
        for (int i = 0; i < depth; i++)
        {
            int bitPos = depth - 1 - i;
            int xb = (x >> bitPos) & 1;
            int yb = (y >> bitPos) & 1;
            // Map bits to Quadrant: (yb << 1) | xb
            quadrants[i] = (Quadrant)((yb << 1) | xb);
        }

        return new QuadKey(depth, quadrants);
    }

    /// <summary>
    /// Convert this QuadKey back to integer coordinates at its depth.
    /// Returns a tuple (x, y) where 0 <= x,y < 2^Depth.
    /// </summary>
    public readonly (int X, int Y) ToCoords()
    {
        int x = 0;
        int y = 0;
        // Extract quadrant bits directly from Data to avoid allocating the
        // Quadrants array. Quadrant bits start at bit 4, two bits per level.
        for (int i = 0; i < Depth; i++)
        {
            int q = (Data >> (4 + i * 2)) & 0b11;
            int xb = q & 0b1;
            int yb = (q >> 1) & 0b1;
            int bitPos = Depth - 1 - i;
            x |= xb << bitPos;
            y |= yb << bitPos;
        }

        return (x, y);
    }
}
