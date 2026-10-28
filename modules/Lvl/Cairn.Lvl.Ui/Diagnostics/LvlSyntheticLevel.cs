using System.Text;
using Cairn.Formats;
using Cairn.Formats.Rfl;

namespace Cairn.Lvl.Ui.Diagnostics;

/// <summary>
/// A small level written byte by byte for the self-tests (format version 200): one room (a box whose faces look
/// inwards, one face per class), a solid brush outside it, two lights, an item and the player start. Public for the
/// shell's integration self-tests.
/// </summary>
public static class LvlSyntheticLevel
{
    /// <summary>The room's half size; its centre is the origin.</summary>
    public static readonly Vector3 RoomHalf = new(4, 2, 4);

    /// <summary>Where the brush is.</summary>
    public static readonly Vector3 BrushPosition = new(12, 0, 0);

    /// <summary>The texture names the room uses (face k uses texture k % 3).</summary>
    public static readonly string[] Textures = ["wall.tga", "floor.tga", "sky.tga"];

    /// <summary>
    /// The room's face flags in face order (+X, -X, +Y, -Y, +Z, -Z): ordinary, sky, invisible, full bright, portal
    /// (portal index + 2 instead of a flag), alpha.
    /// </summary>
    private static readonly (uint Flags, int Portal)[] RoomFaces = [(0, 0), (0x01, 0), (0x2000, 0), (0x20, 0), (0, 2), (0x40, 0)];

    /// <summary>Triangles the default preview draws (portal and invisible faces hidden): four quads.</summary>
    public const int DefaultTriangles = 8;

    public static byte[] Bytes(string levelName = "Synthetic")
    {
        var w = new Writer();
        w.U32(RflReader.Magic).I32(200).U32(1_700_000_000).I32(0).I32(0).I32(0).I32(0).Str(levelName).Str("");
        w.Section(0x900, s => s.Str("rock02.tga").I32(50).Color(new RflColor(90, 80, 70, 255)).U8(0).Color(new RflColor(0, 0, 0, 255)).F32(0).F32(0));
        w.Section(0x100, s => Solid(s, Box(RoomHalf, inward: true), rooms: 1, faces: RoomFaces));
        w.Section(0x300, s =>
        {
            s.I32(2);
            Light(s, 10, new Vector3(0, 1.5f, 0), 0x18);
            Light(s, 11, new Vector3(2, 1, 2), 0x28);
        });
        w.Section(0x40000, s => s.I32(1).Head(50, "Medical Kit", new Vector3(-2, -1.5f, 1)).I32(1).I32(30).I32(0));
        w.Section(0x70000, s => s.Vec3(new Vector3(0, -1, -2)).Matrix());
        w.Section(0x02000000, s =>
        {
            s.I32(1).I32(100).Vec3(BrushPosition).Matrix();
            Solid(s, Box(new Vector3(1, 1, 1), inward: false), rooms: 0, faces: null);
            s.U32(0).I32(-1).I32(0); // solid flags, life, state
        });
        w.U32(0).I32(0);
        return w.ToArray();
    }

    /// <summary>A box's corners and its six quads (corner indices), wound to look inwards or outwards.</summary>
    private static (Vector3[] Vertices, int[][] Faces) Box(Vector3 half, bool inward)
    {
        var vertices = new Vector3[8];
        for (int i = 0; i < 8; i++) vertices[i] = new Vector3((i & 1) != 0 ? half.X : -half.X, (i & 2) != 0 ? half.Y : -half.Y, (i & 4) != 0 ? half.Z : -half.Z);
        Vector3[] directions = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];
        var faces = new int[6][];
        for (int k = 0; k < 6; k++)
        {
            var dir = directions[k];
            var idx = Enumerable.Range(0, 8).Where(i => Vector3.Dot(vertices[i], dir) > 0).ToList();
            var centroid = idx.Aggregate(Vector3.Zero, (sum, i) => sum + vertices[i]) / 4f;
            var u = Vector3.Normalize(vertices[idx[0]] - centroid);
            var v = Vector3.Cross(dir, u);
            idx.Sort((a, b) => Angle(vertices[a] - centroid, u, v).CompareTo(Angle(vertices[b] - centroid, u, v)));
            // Newell's normal of the corner order is the side the face is seen from
            var n = Newell([.. idx.Select(i => vertices[i])]);
            bool outwards = Vector3.Dot(n, dir) > 0;
            if (outwards == inward) idx.Reverse();
            faces[k] = [.. idx];
        }
        return (vertices, faces);
    }

    private static float Angle(Vector3 d, Vector3 u, Vector3 v) => MathF.Atan2(Vector3.Dot(d, v), Vector3.Dot(d, u));

    private static Vector3 Newell(Vector3[] p)
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

    /// <summary>A solid in the version-200 layout; faces have no lightmap surface.</summary>
    private static void Solid(Writer s, (Vector3[] Vertices, int[][] Faces) box, int rooms, (uint Flags, int Portal)[]? faces)
    {
        s.U32(0).U32(0).Str("");
        s.I32(Textures.Length);
        foreach (var t in Textures) s.Str(t);
        s.I32(0); // scroll list
        s.I32(rooms);
        for (int r = 0; r < rooms; r++)
        {
            s.I32(r).Vec3(-RoomHalf).Vec3(RoomHalf);
            s.U8(0).U8(0).U8(0).U8(0).U8(0).U8(0).U8(0).U8(0); // sky, cold, outside, airlock, liquid, ambient, subroom, alpha
            s.F32(-1).Str("");
        }
        s.I32(0); // sub-room lists
        s.I32(0); // portals
        s.I32(box.Vertices.Length);
        foreach (var v in box.Vertices) s.Vec3(v);
        s.I32(box.Faces.Length);
        for (int k = 0; k < box.Faces.Length; k++)
        {
            var (flags, portal) = faces is null ? (0u, 0) : faces[k];
            s.Vec3(Vector3.UnitY).F32(0);
            s.I32(k % Textures.Length).I32(-1).I32(100 + k).I32(-1).I32(-1).I32(portal).U32(flags).U32(0).I32(rooms > 0 ? 0 : -1);
            var corners = box.Faces[k];
            s.I32(corners.Length);
            for (int c = 0; c < corners.Length; c++) s.I32(corners[c]).Vec2(new Vector2(c is 1 or 2 ? 2 : 0, c >= 2 ? 1 : 0));
        }
        s.I32(0); // surfaces
    }

    private static void Light(Writer s, int uid, Vector3 position, uint flags)
    {
        s.Head(uid, "Light", position);
        s.U32(flags).Color(new RflColor(255, 220, 160, 255)).F32(8);
        s.F32(45).F32(5).F32(0.1f).I32(1).F32(0.5f);
        s.F32(1).F32(0).F32(0).F32(0).F32(0).F32(0);
    }

    private sealed class Writer
    {
        private readonly BinaryBuilder _b = new();

        public byte[] ToArray() => _b.ToArray();
        public Writer U8(int v) { _b.WriteByte((byte)v); return this; }
        public Writer I32(int v) { _b.WriteInt32(v); return this; }
        public Writer U32(uint v) { _b.WriteUInt32(v); return this; }
        public Writer F32(float v) { _b.WriteSingle(v); return this; }
        public Writer Vec2(Vector2 v) { _b.WriteSingle(v.X); _b.WriteSingle(v.Y); return this; }
        public Writer Vec3(Vector3 v) { _b.WriteVector3(v); return this; }
        public Writer Color(RflColor c) { _b.WriteByte(c.R); _b.WriteByte(c.G); _b.WriteByte(c.B); _b.WriteByte(c.A); return this; }

        public Writer Str(string s)
        {
            var bytes = Encoding.Latin1.GetBytes(s);
            _b.WriteUInt16((ushort)bytes.Length);
            _b.WriteBytes(bytes);
            return this;
        }

        /// <summary>The identity orientation as stock sections store it (forward, right, up).</summary>
        public Writer Matrix() => Vec3(Vector3.UnitZ).Vec3(Vector3.UnitX).Vec3(Vector3.UnitY);

        /// <summary>The common object head: uid, class, position, orientation, script name, hidden.</summary>
        public Writer Head(int uid, string cls, Vector3 position) => I32(uid).Str(cls).Vec3(position).Matrix().Str("").U8(0);

        public Writer Section(uint id, Action<Writer> body)
        {
            U32(id);
            int lengthAt = _b.Length;
            I32(0);
            int start = _b.Length;
            body(this);
            _b.PatchInt32(lengthAt, _b.Length - start);
            return this;
        }
    }
}
