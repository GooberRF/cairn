using System.Buffers.Binary;
using System.Text;
using Cairn.Formats.Rfl;
using Cairn.Lvl.Model;

namespace Cairn.Lvl.Tests;

/// <summary>
/// Synthetic Red Faction II levels, written field by field from the format notes (independently of the reader), with
/// the cases the real files need: a 6-corner face with inline scroll periods, a sky face with periods, a corona
/// without a bitmap, a push region of shape 1, a brush with the extra liquid block, moving groups with a parent.
/// </summary>
internal static class Rf2LevelTestData
{
    public static readonly RflColor Neutral = new(0x80, 0x80, 0x80, 0xFF);

    /// <summary>The static geometry's textures: one with a fixed Red Faction translation, one with none.</summary>
    public static readonly string[] StaticTextures = ["rck_invisible.tga", "zzz_unknown.tga"];

    /// <summary>A Red Faction II level header (no mod name).</summary>
    public static LvlBytes Header(string levelName = "Synthetic RF2")
    {
        var w = new LvlBytes(0x127);
        w.U32(RflReader.Magic).I32(0x127).U32(1_040_000_000).I32(0).I32(0).I32(99).I32(0x7FFFFFFF).Str(levelName);
        return w;
    }

    /// <summary>The RF2 geometry body.</summary>
    /// <param name="faces">Per face: texture, flags, scroll periods (written when flags has 0x8000), corners (vertex, uv, colour).</param>
    public static LvlBytes Geometry(this LvlBytes w, string[] textures, Vector3[] vertices,
        (int Texture, uint Flags, Vector2 Period, (int V, Vector2 Uv, RflColor C)[] Corners)[] faces, int rooms = 0)
    {
        w.Str("").U32(0);
        w.I32(textures.Length);
        foreach (var t in textures) w.Str(t);
        w.I32(rooms);
        for (int i = 0; i < rooms; i++)
        {
            w.I32(100 + i).Vec3(new Vector3(-10)).Vec3(new Vector3(10)).U8(0x80).U8(0).U8(0).U8(0x80).F32(-1).Str(i == 0 ? "eax_room" : "");
            w.F32(1).F32(2).F32(3).I32(4).F32(5).I32(6).F32(7).F32(8).F32(9).F32(10);
        }
        w.I32(rooms > 1 ? 1 : 0);
        if (rooms > 1) w.I32(0).Uids(1);
        w.I32(1).I32(0x11223344).I32(0x55667788); // one room link
        w.I32(0); // portals
        w.I32(vertices.Length);
        foreach (var v in vertices) w.Vec3(v);
        w.I32(faces.Length);
        for (int i = 0; i < faces.Length; i++)
        {
            var f = faces[i];
            w.Vec3(Vector3.UnitY).F32(0);
            w.I32(f.Texture).I32(i).I32(1000 + i).I32(7).U32(f.Flags).U32(i == 0 ? 1u : 0u);
            if ((f.Flags & 0x8000) != 0) w.Vec2(f.Period);
            w.U8(0xFF).U8(0xB4).U8(0x5A).F32(i == 0 ? 1.4f : 0f);
            w.I32(rooms > 0 ? 0 : -1);
            w.I32(f.Corners.Length);
            foreach (var (v, uv, c) in f.Corners) w.I32(v).Vec2(uv).Color(c);
        }
        return w;
    }

    /// <summary>A unit hexagon in the XZ plane (6 vertices).</summary>
    public static Vector3[] Hexagon(float y = 0) => [.. Enumerable.Range(0, 6).Select(i => new Vector3(MathF.Cos(i * MathF.PI / 3), y, MathF.Sin(i * MathF.PI / 3)))];

    private static (int, Vector2, RflColor)[] Corners(params int[] vertices) => [.. vertices.Select(v => (v, new Vector2(v * 0.25f, 1 - v * 0.25f), Neutral))];

    /// <summary>A box brush of six quads, every face with <paramref name="texture"/>.</summary>
    public static LvlBytes BoxBrush(this LvlBytes w, int uid, Vector3 position, uint solidFlags, int life, string texture, byte[]? extra = null)
    {
        Vector3[] v = [.. Enumerable.Range(0, 8).Select(i => new Vector3(i & 1, (i >> 1) & 1, (i >> 2) & 1))];
        int[][] quads = [[0, 2, 3, 1], [4, 5, 7, 6], [0, 1, 5, 4], [2, 6, 7, 3], [0, 4, 6, 2], [1, 3, 7, 5]];
        w.I32(uid).Vec3(position).StockMatrix(RfMatrix.Identity);
        w.Geometry([texture], v, [.. quads.Select(q => (0, 0u, Vector2.Zero, Corners(q)))]);
        w.U32(solidFlags).I32(life).I32(0);
        if ((solidFlags & 0xC) == 0xC) w.Bytes(extra ?? new byte[29]);
        return w;
    }

    /// <summary>A Red Faction II level with every section the reader models, plus one it keeps opaque.</summary>
    public static byte[] Level()
    {
        var w = Header();
        w.Section(0x7000, s => s.I32(2).Str("rck_invisible.tga").Str("zzz_unknown.tga"));
        w.Section(0x7001, s => s.I32(1).Str("burke.vcm").Str("").I32(3));
        w.Section(0x7002, s => s.I32(0));
        w.Section(0x7003, s => s.I32(1).Str("ex_5ton.v3d").I32(1));
        w.Section(0x7004, s => s.I32(0));
        w.Section(0x7005, s => s.I32(1).Str("synthetic.peg").I32(1));
        w.Section(0x900, s => s.Str("geo_rock.tga").I32(50).Color(new RflColor(40, 50, 60, 255)).U8(1).Color(new RflColor(1, 2, 3, 255)).F32(10).F32(200)
            .Color(new RflColor(255, 240, 200, 255)).F32(30).F32(45).F32(0.75f).F32(89.9f).Color(new RflColor(9, 8, 7, 255)).I32(3).F32(2f));
        w.Section(0x100, s => s.Geometry(StaticTextures, [.. Hexagon(), .. Hexagon(5)],
        [
            // A hexagon face: full bright, scrolling with periods (2, 0), an RF2-only flag 0x1000.
            (0, 0x8000u | 0x10 | 0x20 | 0x1000, new Vector2(2, 0), Corners(0, 1, 2, 3, 4, 5)),
            // A sky face with periods: its scroll is dropped.
            (1, 0x8000u | 0x1, new Vector2(4, 4), Corners(6, 7, 8)),
            // The scroll flag without periods.
            (1, 0x10u | 0x8, Vector2.Zero, Corners(9, 10, 11)),
        ], rooms: 2));
        w.Section(0x300, s => s.I32(1).Head(10, "Light", new Vector3(0, 3, 0)).U32(0x8 | (1 << 4)).Color(new RflColor(255, 128, 0, 255))
            .F32(5).F32(0).F32(0).F32(0).I32(0).F32(0).F32(0.5f).F32(1).F32(0).F32(0).F32(1).F32(0));
        w.Section(0x500, s => s.I32(1).I32(11).Vec3(new Vector3(1, 1, 1)).U8(0).Str("amb_wind.wav").F32(2).F32(0.8f).F32(0.1f).I32(0));
        w.Section(0x600, s =>
        {
            s.I32(2);
            s.I32(12).Str("Teleport").Vec3(new Vector3(2, 0, 0)).Str("tp").U8(0).F32(1.5f).I32(-1).I32(0).U8(1).U8(0).I32(5).I32(6).I32(7).I32(8)
                .F32(0.25f).F32(0.5f).I32(0).Str("s1").Str("s2").Str("").Uids(10).StockMatrix(RfMatrix.Identity).Color(new RflColor(1, 2, 3, 4));
            s.I32(13).Str("Msg_Box").Vec3(new Vector3(3, 0, 0)).Str("").U8(1).F32(0).I32(0).I32(0).U8(0).U8(1).I32(0).I32(0).I32(0).I32(0)
                .F32(0).F32(0).I32(0).Str("hello").Str("").Str("third").Uids().Color(new RflColor(5, 6, 7, 8));
        });
        w.Section(0x700, s => s.I32(1).I32(14).Vec3(new Vector3(4, 0, 0)).StockMatrix(RfMatrix.Identity).Str("").U8(0).I32(1).U8(1).U8(0).U8(1));
        w.Section(0x7678, s =>
        {
            s.I32(3);
            // Forward, up, right: a corona looking straight down with a 30 degree (stored halved) cone.
            s.I32(15).Str("Corona").Vec3(new Vector3(0, 4, 0)).Vec3(-Vector3.UnitY).Vec3(Vector3.UnitZ).Vec3(Vector3.UnitX).Str("c1").U8(0)
                .Color(new RflColor(255, 0, 0, 0xCD)).Bytes([1, 0, 0, 0, 0]).Str("corona_red.tga").F32(30).F32(2).F32(1).F32(1).F32(50)
                .Str("beam.tga").F32(3).F32(4).F32(5).Bytes([9, 9, 9, 9, 9]);
            // No bitmap: none of the five glare values is stored.
            s.I32(16).Str("Corona").Vec3(new Vector3(8, 4, 0)).Vec3(Vector3.UnitZ).Vec3(Vector3.UnitY).Vec3(Vector3.UnitX).Str("").U8(1)
                .Color(new RflColor(0, 255, 0, 255)).Bytes([0, 0, 0, 0, 0]).Str("").Str("").Bytes([0, 0, 0, 0, 0]);
            // A wide cone (a point light) whose colour was never set (CD CD CD).
            s.I32(9).Str("Corona").Vec3(new Vector3(0, 4, 8)).Vec3(Vector3.UnitZ).Vec3(Vector3.UnitY).Vec3(Vector3.UnitX).Str("").U8(0)
                .Color(new RflColor(0xCD, 0xCD, 0xCD, 0xCD)).Bytes([0, 0, 0, 0, 0]).Str("corona_white.tga").F32(90).F32(1).F32(1).F32(1).F32(10)
                .Str("").Bytes([0, 0, 0, 0, 0]);
        });
        w.Section(0x1000, s => s.I32(1).Head(17, "Decal", new Vector3(0, 0, 1)).Vec3(new Vector3(1, 1, 0.1f)).Str("dec_scorch.tga").I32(200).U8(1).I32(1).F32(2)
            .F32(0.5f).I32(1).I32(2));
        w.Section(0x1100, s =>
        {
            s.I32(2);
            s.Head(18, "Push Region", new Vector3(0, 0, 2)).I32(1).F32(3).F32(10).U32(0x40);       // shape 1: one float; a jump pad
            s.Head(19, "Push Region", new Vector3(0, 0, 3)).I32(3).Vec3(new Vector3(1, 2, 3)).F32(5).U32(0x0002_0010);
        });
        w.Section(0x7900, s =>
        {
            s.I32(5).I32(1).I32(1);
            s.Bytes(Encoding.Latin1.GetBytes("rck_invisible.tga\0"));
            s.I32(1).F32(0).F32(0); // one light probe
            s.I32(77);              // the group's lightmap id
            s.I32(1);               // one entry
            s.I32(3).I32(1).I32(0).I32(0).I32(0);
            s.Vec3(Vector3.Zero).Vec3(Vector3.UnitX).Vec3(Vector3.UnitZ);
            s.Vec3(Vector3.Zero).Vec3(Vector3.One);
            s.I32(0).I32(1).I32(2);
            foreach (int c in (int[])[300, 20, 10, 100, 100, 100, -5, 50, 60]) s.I32(c);
            for (int i = 0; i < 6; i++) s.F32(i);
            s.I32(255).Vec3(Vector3.Zero);
        });
        w.Section(0x7901, s => s.U8(0));
        w.Section(0x30000, s => s.I32(1).Bytes([1, 2, 3, 4, 5, 6]));
        w.Section(0x70000, s => s.Vec3(new Vector3(1, 2, 3)).StockMatrix(RfMatrix.Identity));
        w.Section(0x10000, s => s.I32(1).Str("path").Uids(1, 2));
        w.Section(0x40000, s => s.I32(1).Head(20, "inven_HealthKit", new Vector3(5, 0, 0)).I32(1).I32(30).I32(0).Bytes([0xFF, 0xFF, 0xFF, 0xFF, 0, 0]));
        w.Section(0x50000, s =>
        {
            s.I32(2);
            s.Head(21, "minelight01", new Vector3(6, 0, 0)).Str("skin_a").Uids(20);
            s.Head(22, "no_such_class", new Vector3(7, 0, 0)).Str("").Uids();
        });
        w.Section(0x60000, s => s.I32(1).I32(23).Str("trig").U8(0).I32(1).F32(2).I32(-1).U8(1).U8(3).Bytes(Encoding.Latin1.GetBytes("key")).U8(3)
            .Vec3(new Vector3(8, 0, 0)).StockMatrix(RfMatrix.Identity).Vec3(new Vector3(1, 2, 3)).I32(-1).I32(-1).I32(21).U8(0).F32(0.5f)
            .F32(1e9f).U8(0).U16(0x1234).U32(0x8).Uids(12));
        w.Section(0x2000, s => s.I32(1).BoxBrush(24, new Vector3(0, 10, 0), 0x4, -1, "mtl_door.tga"));
        w.Section(0x3000, s =>
        {
            s.I32(1);
            s.Str("door").U8(0).Bytes([1, 0, 1, 0]).U8(1);
            s.I32(1).I32(25).Vec3(new Vector3(0, 10, 0)).StockMatrix(RfMatrix.Identity).Str("").U8(0).F32(1).F32(2).F32(3).F32(0.5f).F32(0.5f)
                .I32(-1).I32(-1).I32(-1).F32(90);
            s.I32(1).I32(24).Vec3(Vector3.Zero).StockMatrix(RfMatrix.Identity);
            s.U8(1).U8(0).U8(0).U8(1).U8(0).U8(0).U8(7).I32(2).I32(0);
            s.Str("start.wav").F32(1).Str("").F32(1).Str("stop.wav").F32(0.5f).Str("").F32(1);
            s.I32(42).Uids(25).Uids(24).I32(26).Vec3(new Vector3(1, 1, 1)).StockMatrix(RfMatrix.Identity);
            for (int i = 0; i < 9; i++) s.I32(i);
        });
        w.Section(0x01000000, s =>
        {
            s.I32(1).Str("No Level Name").Str("author").Str("date").U8(1).U8(0);
            for (int i = 0; i < 4; i++)
            {
                s.I32(i == 0 ? 0 : 1);
                if (i != 0) s.F32(1);
                s.Vec3(Vector3.Zero).StockMatrix(RfMatrix.Identity);
            }
            var filler = new byte[2080];
            Array.Fill(filler, (byte)0xCD);
            s.Bytes(filler);
        });
        w.Section(0x02000000, s =>
        {
            s.I32(4);
            s.BoxBrush(27, new Vector3(1, 0, 0), 0x0, -1, "rck_invisible.tga");
            s.BoxBrush(28, new Vector3(2, 0, 0), 0x4 | 0x20, 50, "zzz_unknown.tga");              // geoable detail
            s.BoxBrush(29, new Vector3(3, 0, 0), 0x4 | 0x8, -1, "wtr_water.tga", [.. Enumerable.Range(1, 29).Select(i => (byte)i)]); // liquid block
            s.BoxBrush(30, new Vector3(4, 0, 0), 0x4 | 0x200, 10, "mtl_invisible.tga");           // breakable invisible trigger
        });
        w.Section(0x03000000, s => s.Bytes([0xCD, 0xCD, 0xCD, 0xCD]));
        w.EndMarker();
        return w.ToArray();
    }

    /// <summary>A GEKV version 6 texture pack of 32-bit textures (each a grey ramp with alpha 0x80, i.e. opaque).</summary>
    public static byte[] Peg(params (string Name, int W, int H)[] textures)
    {
        const int header = 32, entry = 64;
        int dataStart = header + textures.Length * entry;
        var offsets = new List<int>();
        int at = dataStart;
        foreach (var (_, w, h) in textures)
        {
            offsets.Add(at);
            at += (w * h * 4 + 15) & ~15;
        }
        var b = new byte[at];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 0x564B4547);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), 6);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), textures.Length * entry);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(16), textures.Length);
        for (int i = 0; i < textures.Length; i++)
        {
            var (name, w, h) = textures[i];
            int e = header + i * entry;
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(e), (ushort)w);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(e + 2), (ushort)h);
            b[e + 4] = 7;
            b[e + 7] = 1;
            b[e + 9] = 1;
            Encoding.Latin1.GetBytes(name).CopyTo(b, e + 12);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(e + 60), offsets[i]);
            for (int p = 0; p < w * h; p++)
            {
                int o = offsets[i] + p * 4;
                b[o] = b[o + 1] = b[o + 2] = (byte)(p * 255 / Math.Max(1, w * h - 1));
                b[o + 3] = 0x80;
            }
        }
        return b;
    }
}
