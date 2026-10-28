using Cairn.Formats.Rfl;
using Cairn.Lvl.Model;

namespace Cairn.Lvl.Tests;

/// <summary>Synthetic levels for the converter tests: boxes with known per-corner UVs, transforms and flags.</summary>
internal static class LvlConvertTestData
{
    /// <summary>The six face directions of a box, in face order.</summary>
    public static readonly Vector3[] FaceDirections = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];

    /// <summary>The UV every corner of a box gets: unique per (face, corner), so a converter that shares UVs per vertex fails.</summary>
    public static Vector2 CornerUv(int face, int corner) => new(face + corner * 0.25f, face * 2 + corner * 0.5f);

    /// <summary>
    /// A box from <c>-half</c> to <c>half</c>: 8 shared vertices, 6 quads wound like RF faces (Newell normal pointing
    /// out, as the stored planes of real levels do). Face k uses texture <c>k % textures.Length</c>.
    /// </summary>
    public static RfGeometry Box(Vector3 half, string[] textures, Func<int, RfFace, RfFace>? tweak = null, Vector3? center = null, int room = -1)
    {
        var c = center ?? Vector3.Zero;
        var vertices = new Vector3[8];
        for (int i = 0; i < 8; i++)
            vertices[i] = c + new Vector3((i & 1) != 0 ? half.X : -half.X, (i & 2) != 0 ? half.Y : -half.Y, (i & 4) != 0 ? half.Z : -half.Z);
        var faces = ImmutableArray.CreateBuilder<RfFace>();
        for (int k = 0; k < 6; k++)
        {
            var dir = FaceDirections[k];
            var idx = Enumerable.Range(0, 8).Where(i => Vector3.Dot(vertices[i] - c, dir) > 0).ToList();
            // Order the four corners around the face, then make the winding point out.
            var centroid = idx.Aggregate(Vector3.Zero, (s, i) => s + vertices[i]) / 4f;
            var u = Vector3.Normalize(vertices[idx[0]] - centroid);
            var v = Vector3.Cross(dir, u);
            idx.Sort((a, b) => Angle(vertices[a] - centroid, u, v).CompareTo(Angle(vertices[b] - centroid, u, v)));
            if (Vector3.Dot(Newell(idx.Select(i => vertices[i]).ToArray()), dir) < 0) idx.Reverse();
            var face = new RfFace
            {
                Texture = k % textures.Length,
                FaceId = 100 + k,
                Room = room,
                Plane = new RfPlane(dir, -Vector3.Dot(dir, c + dir * Vector3.Abs(half))),
                Corners = [.. idx.Select((vi, j) => new RfCorner(vi, CornerUv(k, j)))],
            };
            faces.Add(tweak is null ? face : tweak(k, face));
        }
        return new RfGeometry { Textures = [.. textures], Vertices = [.. vertices], Faces = faces.ToImmutable() };
    }

    private static float Angle(Vector3 d, Vector3 u, Vector3 v) => MathF.Atan2(Vector3.Dot(d, v), Vector3.Dot(d, u));

    public static Vector3 Newell(Vector3[] p)
    {
        var n = Vector3.Zero;
        for (int i = 0; i < p.Length; i++)
        {
            var a = p[i];
            var b = p[(i + 1) % p.Length];
            n.X += (a.Y - b.Y) * (a.Z + b.Z);
            n.Y += (a.Z - b.Z) * (a.X + b.X);
            n.Z += (a.X - b.X) * (a.Y + b.Y);
        }
        return n;
    }

    /// <summary>A rotation (yaw, pitch, roll in radians) as an RF orientation.</summary>
    public static RfMatrix Rotation(float yaw, float pitch, float roll)
    {
        var m = Matrix4x4.CreateFromYawPitchRoll(yaw, pitch, roll);
        return new RfMatrix(new Vector3(m.M11, m.M12, m.M13), new Vector3(m.M21, m.M22, m.M23), new Vector3(m.M31, m.M32, m.M33));
    }

    /// <summary>Face flags set on a face, raw value kept equal (an RF1 face).</summary>
    public static RfFace WithFlags(RfFace f, RfFaceFlags flags) => f with { Flags = flags, RawFlags = (uint)flags };

    public static RfBrush Brush(int uid, RfGeometry geometry, Vector3 position, RfMatrix? orientation = null, RfSolidFlags solid = RfSolidFlags.None, int life = -1, int state = 0) => new()
    {
        Uid = uid,
        Position = position,
        Orientation = orientation ?? RfMatrix.Identity,
        Geometry = geometry,
        SolidFlags = solid,
        Life = life,
        State = state,
    };

    public static RfLevel Level(RfGeometry? compiled = null, ImmutableArray<RfBrush>? brushes = null, RfObjectSet? objects = null,
        ImmutableArray<RfLevelGroup>? groups = null, ImmutableArray<RfBrush>? movers = null) => new()
    {
        Name = "synthetic.rfl",
        Header = new RfLevelHeader { Version = 200, LevelName = "Synthetic" },
        StaticGeometry = compiled ?? RfGeometry.Empty,
        Brushes = brushes ?? [],
        Movers = movers ?? [],
        Objects = objects ?? RfObjectSet.Empty,
        Groups = groups ?? [],
        Properties = new RfLevelProperties { AmbientColor = new RflColor(10, 20, 30, 255), FogFar = 100f },
    };

    /// <summary>A compiled geometry with two rooms: room 0 a box, room 1 a box whose faces carry one flag each.</summary>
    public static RfGeometry CompiledTwoRooms()
    {
        var a = Box(new Vector3(2, 1, 2), ["floor.tga", "wall.tga"], room: 0);
        var b = Box(new Vector3(1, 1, 1), ["floor.tga", "wall.tga"], center: new Vector3(10, 0, 0), room: 1, tweak: (k, f) => k switch
        {
            0 => f with { PortalPlus2 = 2 },                             // portal (left out by default)
            1 => WithFlags(f, RfFaceFlags.Invisible),                    // invisible (left out by default)
            2 => WithFlags(f, RfFaceFlags.ShowSky),                      // sky
            3 => WithFlags(f, RfFaceFlags.FullBright),                   // full bright
            4 => WithFlags(f, RfFaceFlags.ScrollTexture) with { Scroll = new Vector2(0.5f, -0.25f) },
            _ => WithFlags(f, RfFaceFlags.HasAlpha),                     // alpha
        });
        // One solid: room b's vertices follow room a's.
        int offset = a.Vertices.Length;
        var faces = a.Faces.AddRange(b.Faces.Select(f => f with { Corners = [.. f.Corners.Select(c => c with { Vertex = c.Vertex + offset })] }));
        return a with
        {
            Vertices = a.Vertices.AddRange(b.Vertices),
            Faces = faces,
            Rooms = [new RfRoom { Id = 1 }, new RfRoom { Id = 2, IsSky = true }],
        };
    }

    /// <summary>A grid of <paramref name="n"/> x <paramref name="n"/> quads in the XZ plane (one texture).</summary>
    public static RfGeometry Grid(int n, string texture)
    {
        var vertices = new List<Vector3>();
        for (int z = 0; z <= n; z++)
            for (int x = 0; x <= n; x++)
                vertices.Add(new Vector3(x, 0, z));
        var faces = ImmutableArray.CreateBuilder<RfFace>();
        for (int z = 0; z < n; z++)
        {
            for (int x = 0; x < n; x++)
            {
                int i0 = z * (n + 1) + x;
                int[] idx = [i0, i0 + n + 1, i0 + n + 2, i0 + 1];
                faces.Add(new RfFace
                {
                    Texture = 0,
                    Plane = new RfPlane(Vector3.UnitY, 0),
                    // A distinct UV per corner so no vertex is shared.
                    Corners = [.. idx.Select((vi, j) => new RfCorner(vi, new Vector2(x * 4 + j, z)))],
                });
            }
        }
        return new RfGeometry { Textures = [texture], Vertices = [.. vertices], Faces = faces.ToImmutable() };
    }
}
