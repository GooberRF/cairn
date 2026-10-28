using System.Numerics;

namespace Cairn.Formats.Obj;

/// <summary>
/// Splits a polygon into triangles that keep its winding. Polygons are projected onto the plane of
/// their Newell normal and ear-clipped, so concave n-gons (an L-shaped cap, a letter outline) come out
/// right; a polygon that is degenerate, self-intersecting or very large falls back to a fan from the
/// first corner. Used by <see cref="ObjWriter"/> and by importers that need triangles.
/// </summary>
public static class ObjTriangulator
{
    /// <summary>Above this many corners ear clipping (quadratic or worse) gives way to a fan.</summary>
    public const int MaxEarClippingCorners = 512;

    /// <summary>The triangles of <paramref name="face"/> as indices into its <see cref="ObjFace.Corners"/>.</summary>
    /// <exception cref="ArgumentException">A corner's position index is outside <paramref name="positions"/>.</exception>
    public static IReadOnlyList<(int A, int B, int C)> Triangulate(IReadOnlyList<Vector3> positions, ObjFace face)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(face);
        var polygon = new Vector3[face.Corners.Count];
        for (int i = 0; i < polygon.Length; i++)
        {
            int index = face.Corners[i].Position;
            if ((uint)index >= (uint)positions.Count)
                throw new ArgumentException($"Corner {i} names position {index}, but there are {positions.Count}.", nameof(face));
            polygon[i] = positions[index];
        }
        return Triangulate(polygon);
    }

    /// <summary>The triangles of a polygon given by its corner positions, as indices into <paramref name="polygon"/>.</summary>
    public static IReadOnlyList<(int A, int B, int C)> Triangulate(ReadOnlySpan<Vector3> polygon)
    {
        int n = polygon.Length;
        if (n < 3) return [];
        if (n == 3) return [(0, 1, 2)];
        if (n <= MaxEarClippingCorners && EarClip(polygon) is { } ears) return ears;
        return Fan(n);
    }

    private static (int, int, int)[] Fan(int n)
    {
        var triangles = new (int, int, int)[n - 2];
        for (int i = 1; i < n - 1; i++) triangles[i - 1] = (0, i, i + 1);
        return triangles;
    }

    /// <summary>Ear clipping in the polygon's own plane; null when no consistent set of ears exists.</summary>
    private static List<(int, int, int)>? EarClip(ReadOnlySpan<Vector3> polygon)
    {
        int n = polygon.Length;
        // Newell's method: robust for non-planar and concave polygons.
        double nx = 0, ny = 0, nz = 0;
        for (int i = 0; i < n; i++)
        {
            Vector3 a = polygon[i], b = polygon[(i + 1) % n];
            nx += (double)(a.Y - b.Y) * (a.Z + b.Z);
            ny += (double)(a.Z - b.Z) * (a.X + b.X);
            nz += (double)(a.X - b.X) * (a.Y + b.Y);
        }
        double ax = Math.Abs(nx), ay = Math.Abs(ny), az = Math.Abs(nz);
        if (ax + ay + az < 1e-20) return null;

        // Drop the dominant axis; order the remaining two so the polygon is counter-clockwise in 2D.
        var points = new (double X, double Y)[n];
        for (int i = 0; i < n; i++)
        {
            Vector3 p = polygon[i];
            points[i] = az >= ax && az >= ay ? (nz > 0 ? (p.X, p.Y) : (p.Y, p.X))
                : ax >= ay ? (nx > 0 ? (p.Y, p.Z) : (p.Z, p.Y))
                : (ny > 0 ? (p.Z, p.X) : (p.X, p.Z));
        }
        double scale = 0;
        foreach (var (x, y) in points) scale = Math.Max(scale, Math.Max(Math.Abs(x), Math.Abs(y)));
        double epsilon = Math.Max(scale * scale, 1e-30) * 1e-12;

        var remaining = new List<int>(n);
        for (int i = 0; i < n; i++) remaining.Add(i);
        var triangles = new List<(int, int, int)>(n - 2);
        int guard = 0;
        int cursor = 0;
        while (remaining.Count > 3)
        {
            int count = remaining.Count;
            bool clipped = false;
            for (int step = 0; step < count; step++)
            {
                int k = (cursor + step) % count;
                int prev = remaining[(k + count - 1) % count], current = remaining[k], next = remaining[(k + 1) % count];
                if (!IsEar(points, remaining, prev, current, next, epsilon)) continue;
                triangles.Add((prev, current, next));
                remaining.RemoveAt(k);
                cursor = k % remaining.Count;
                clipped = true;
                break;
            }
            if (!clipped || ++guard > n) return null;
        }
        triangles.Add((remaining[0], remaining[1], remaining[2]));
        return triangles;
    }

    private static bool IsEar((double X, double Y)[] points, List<int> remaining, int prev, int current, int next, double epsilon)
    {
        var a = points[prev];
        var b = points[current];
        var c = points[next];
        if (Cross(a, b, c) <= epsilon) return false; // reflex or collinear
        foreach (int other in remaining)
        {
            if (other == prev || other == current || other == next) continue;
            var p = points[other];
            // A corner that coincides with the ear's own corners (a polygon with a bridge) does not block it.
            if (p == a || p == b || p == c) continue;
            if (Cross(a, b, p) >= -epsilon && Cross(b, c, p) >= -epsilon && Cross(c, a, p) >= -epsilon) return false;
        }
        return true;
    }

    private static double Cross((double X, double Y) a, (double X, double Y) b, (double X, double Y) c) =>
        (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
}
