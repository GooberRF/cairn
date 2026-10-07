using Cairn.Vfx.Formats;

namespace Cairn.Vfx.Editing;

/// <summary>Ready-made 0x40006 meshes (Y up, using material slot 0 = the file material given as <c>material</c>).</summary>
public static class VfxPrimitives
{
    private static VfxMeshBuilder Grid(string name, int material, int cols, int rows, Func<float, float, Vector3> at, bool closeU = false)
    {
        var b = new VfxMeshBuilder { Name = name, MaterialIndices = [material] };
        var pos = new List<Vector3>();
        var uvs = new List<Vector2>();
        // A row whose points all coincide (sphere pole, disc centre, cone tip) becomes one vertex
        // closed with a triangle fan, so no zero-area faces are emitted.
        var grid = new Vector3[rows + 1, cols + 1];
        var first = new int[rows + 1];
        var pole = new bool[rows + 1];
        for (int r = 0; r <= rows; r++)
        {
            for (int c = 0; c <= cols; c++) grid[r, c] = at(c / (float)cols, r / (float)rows);
            pole[r] = Enumerable.Range(0, cols + 1).All(c => Vector3.DistanceSquared(grid[r, c], grid[r, 0]) < 1e-12f);
            first[r] = pos.Count;
            for (int c = 0; c <= (pole[r] ? 0 : cols); c++) pos.Add(grid[r, c]);
        }
        int Id(int c, int r) => first[r] + (pole[r] ? 0 : c);
        Vector2 Uv(int c, int r, int fanColumn) => new((pole[r] ? fanColumn + 0.5f : c) / cols, 1 - r / (float)rows);
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                if (!pole[r])
                {
                    b.Triangles.Add((Id(c, r), Id(c + 1, r + 1), Id(c + 1, r)));
                    uvs.AddRange([Uv(c, r, c), Uv(c + 1, r + 1, c), Uv(c + 1, r, c)]);
                }
                if (!pole[r + 1])
                {
                    b.Triangles.Add((Id(c, r), Id(c, r + 1), Id(c + 1, r + 1)));
                    uvs.AddRange([Uv(c, r, c), Uv(c, r + 1, c), Uv(c + 1, r + 1, c)]);
                }
            }
        b.Frames.Add([.. pos]);
        b.Uvs = [.. uvs];
        return b;
    }

    /// <summary>A flat plane in XZ facing +Y.</summary>
    public static VfxMesh Plane(string name = "Plane", float width = 1, float depth = 1, int material = 0) =>
        Grid(name, material, 1, 1, (u, v) => new Vector3((u - 0.5f) * width, 0, (v - 0.5f) * depth)).Build();

    /// <summary>A camera-facing quad (Facing flag) in XY.</summary>
    public static VfxMesh FacingQuad(string name = "Billboard", float size = 1, int material = 0)
    {
        var b = Grid(name, material, 1, 1, (u, v) => new Vector3((u - 0.5f) * size, (0.5f - v) * size, 0));
        b.Flags = VfxMeshFlags.Facing;
        b.FacingSize = new(size, size);
        return b.Build();
    }

    /// <summary>A facing rod along <paramref name="up"/>.</summary>
    public static VfxMesh FacingRod(string name = "Rod", float width = 0.2f, float length = 1, Vector3? up = null, int material = 0)
    {
        var b = Grid(name, material, 1, 1, (u, v) => new Vector3((u - 0.5f) * width, (0.5f - v) * length, 0));
        b.Flags = VfxMeshFlags.FacingRod;
        b.FacingSize = new(width, length);
        b.UpVector = up ?? Vector3.UnitY;
        return b.Build();
    }

    /// <summary>A disc (inner = 0) or ring in XZ facing +Y, UVs planar.</summary>
    public static VfxMesh Disc(string name = "Disc", float outer = 1, float inner = 0, int segments = 24, int material = 0)
    {
        var b = Grid(name, material, segments, 1, (u, v) =>
        {
            float a = u * MathF.Tau, r = inner + (outer - inner) * (1 - v); // +u keeps the front face up (+Y), like Plane
            return new Vector3(MathF.Cos(a) * r, 0, MathF.Sin(a) * r);
        });
        var p = b.Frames[0];
        b.Uvs = b.Triangles.SelectMany(t => new[] { t.A, t.B, t.C }).Select(i => new Vector2(p[i].X / (2 * outer) + 0.5f, p[i].Z / (2 * outer) + 0.5f)).ToArray();
        return b.Build();
    }

    /// <summary>An open cylinder (or cone when <paramref name="topRadius"/> is 0) along Y, base at y = 0.</summary>
    public static VfxMesh Cylinder(string name = "Cylinder", float radius = 0.5f, float topRadius = 0.5f, float height = 1, int segments = 16, int material = 0) =>
        Grid(name, material, segments, 1, (u, v) =>
        {
            float a = -u * MathF.Tau, r = topRadius + (radius - topRadius) * v;
            return new Vector3(MathF.Cos(a) * r, (1 - v) * height, MathF.Sin(a) * r);
        }).Build();

    /// <summary>A UV sphere.</summary>
    public static VfxMesh Sphere(string name = "Sphere", float radius = 0.5f, int segments = 16, int rings = 8, int material = 0) =>
        Grid(name, material, segments, rings, (u, v) =>
        {
            float a = -u * MathF.Tau, t = v * MathF.PI;
            return new Vector3(MathF.Cos(a) * MathF.Sin(t) * radius, MathF.Cos(t) * radius, MathF.Sin(a) * MathF.Sin(t) * radius);
        }).Build();

    /// <summary>An axis-aligned box centred on the origin; each side its own smoothing group and full UV square.</summary>
    public static VfxMesh Box(string name = "Box", Vector3? size = null, int material = 0)
    {
        var h = (size ?? Vector3.One) / 2;
        var b = new VfxMeshBuilder { Name = name, MaterialIndices = [material] };
        var pos = new List<Vector3>();
        var uvs = new List<Vector2>();
        var sg = new List<int>();
        Vector3[] normals = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];
        for (int s = 0; s < 6; s++)
        {
            var n = normals[s];
            var t = MathF.Abs(n.Y) > 0.5f ? Vector3.UnitX : Vector3.UnitY;
            var bt = Vector3.Cross(n, t);
            int o = pos.Count;
            Vector2[] q = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
            foreach (var c in q) pos.Add((n + (c.X * 2 - 1) * bt + (c.Y * 2 - 1) * t) * h);
            foreach (var (x, y, z) in new[] { (0, 2, 1), (0, 3, 2) })
            {
                b.Triangles.Add((o + x, o + y, o + z));
                uvs.AddRange([q[x], q[y], q[z]]);
                sg.Add(1 << s);
            }
        }
        b.Frames.Add([.. pos]);
        b.Uvs = [.. uvs];
        b.SmoothingGroups = [.. sg];
        return b.Build();
    }

    /// <summary>One of each primitive (unique names), for tests and galleries.</summary>
    public static IEnumerable<VfxMesh> All(int material = 0) =>
    [
        Plane(material: material), FacingQuad(material: material), FacingRod(material: material), Disc(material: material),
        Disc("Ring", 1, 0.5f, material: material), Cylinder(material: material), Cylinder("Cone", 0.5f, 0, material: material),
        Sphere(material: material), Box(material: material),
    ];
}
