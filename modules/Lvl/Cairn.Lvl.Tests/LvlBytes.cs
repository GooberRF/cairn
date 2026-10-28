using System.Text;
using Cairn.Formats;
using Cairn.Formats.Rfl;
using Cairn.Lvl.Model;

namespace Cairn.Lvl.Tests;

/// <summary>
/// Test-side writer for synthetic levels and group files. It writes each record the way the format notes describe it,
/// with the version gates spelled out, so the reader is checked against an independent encoding of the layout.
/// </summary>
internal sealed class LvlBytes(int version)
{
    private readonly BinaryBuilder _b = new();

    public int V { get; } = version;
    public int Length => _b.Length;
    public byte[] ToArray() => _b.ToArray();

    public LvlBytes U8(int v) { _b.WriteByte((byte)v); return this; }
    public LvlBytes U16(int v) { _b.WriteUInt16((ushort)v); return this; }
    public LvlBytes I32(int v) { _b.WriteInt32(v); return this; }
    public LvlBytes U32(uint v) { _b.WriteUInt32(v); return this; }
    public LvlBytes F32(float v) { _b.WriteSingle(v); return this; }
    public LvlBytes Vec2(Vector2 v) { _b.WriteSingle(v.X); _b.WriteSingle(v.Y); return this; }
    public LvlBytes Vec3(Vector3 v) { _b.WriteVector3(v); return this; }
    public LvlBytes Color(RflColor c) { _b.WriteByte(c.R); _b.WriteByte(c.G); _b.WriteByte(c.B); _b.WriteByte(c.A); return this; }
    public LvlBytes Bytes(ReadOnlySpan<byte> bytes) { _b.WriteBytes(bytes); return this; }

    public LvlBytes Str(string s)
    {
        var bytes = Encoding.Latin1.GetBytes(s);
        _b.WriteUInt16((ushort)bytes.Length);
        _b.WriteBytes(bytes);
        return this;
    }

    /// <summary>Stock sections: forward, right, up.</summary>
    public LvlBytes StockMatrix(RfMatrix m) => Vec3(m.Forward).Vec3(m.Right).Vec3(m.Up);

    /// <summary>Alpine chunks: right, up, forward.</summary>
    public LvlBytes AlpineMatrix(RfMatrix m) => Vec3(m.Right).Vec3(m.Up).Vec3(m.Forward);

    public LvlBytes Uids(params int[] uids)
    {
        I32(uids.Length);
        foreach (int u in uids) I32(u);
        return this;
    }

    public LvlBytes Head(int uid, string cls, Vector3 pos, string script = "", bool hidden = false) =>
        I32(uid).Str(cls).Vec3(pos).StockMatrix(RfMatrix.Identity).Str(script).U8(hidden ? 1 : 0);

    /// <summary>A section: id, length, body.</summary>
    public LvlBytes Section(uint id, Action<LvlBytes> body)
    {
        U32(id);
        int lengthAt = _b.Length;
        I32(0);
        int start = _b.Length;
        body(this);
        _b.PatchInt32(lengthAt, _b.Length - start);
        return this;
    }

    // ---------- whole files ----------

    /// <summary>A level header for this version; the caller adds sections and <see cref="EndMarker"/>.</summary>
    public static LvlBytes Level(int version, string levelName = "Synthetic", string mod = "")
    {
        var w = new LvlBytes(version);
        w.U32(RflReader.Magic).I32(version);
        if (version >= 0x72) w.U32(1_700_000_000);
        w.I32(0).I32(0);
        if (version >= 0xA0) w.I32(0).I32(0);
        if (version >= 0xAA) w.Str(levelName);
        if (version >= 0xB2) w.Str(mod);
        return w;
    }

    public LvlBytes EndMarker() => U32(0).I32(0);

    // ---------- records ----------

    /// <summary>A face for <see cref="Solid"/>.</summary>
    public sealed record Face(int Texture, int Surface, int FaceId, uint Flags, (int Vertex, Vector2 Uv, Vector2 Lm)[] Corners, int Room = -1, uint Smoothing = 0, int Portal = 0);

    /// <summary>A solid with the given content; rooms are plain (no liquid) unless <paramref name="liquidRoom"/>.</summary>
    public LvlBytes Solid(string[] textures, Vector3[] vertices, Face[] faces, (int FaceId, Vector2 Speed)[]? scrolls = null,
        int rooms = 0, bool liquidRoom = false, int portals = 0, int surfaces = 0)
    {
        scrolls ??= [];
        if (V >= 0xC8) U32(0).U32(0).Str("");
        else Str("").U32(0);
        I32(textures.Length);
        foreach (var t in textures) Str(t);
        if (V > 0xB4) Scrolls(scrolls);
        else I32(0); // the old 41-byte list, empty
        I32(rooms);
        for (int i = 0; i < rooms; i++) Room(i, liquidRoom && i == 0);
        if (V >= 0x71) I32(0); // sub-room lists
        I32(portals);
        for (int i = 0; i < portals; i++) I32(0).I32(1).Vec3(Vector3.Zero).Vec3(Vector3.One);
        I32(vertices.Length);
        foreach (var v in vertices) Vec3(v);
        I32(faces.Length);
        foreach (var f in faces)
        {
            if (V >= 0xA7) Vec3(Vector3.UnitY).F32(0);
            I32(f.Texture).I32(f.Surface).I32(f.FaceId).I32(-1).I32(-1).I32(f.Portal).U32(f.Flags).U32(f.Smoothing).I32(f.Room);
            I32(f.Corners.Length);
            bool lm = (short)(f.Surface & 0xFFFF) >= 0;
            foreach (var (vertex, uv, lmuv) in f.Corners)
            {
                I32(vertex).Vec2(uv);
                if (lm) Vec2(lmuv);
            }
        }
        I32(surfaces);
        for (int i = 0; i < surfaces; i++) Surface(i);
        if (V <= 0xB4) Scrolls(scrolls);
        return this;
    }

    private void Scrolls((int FaceId, Vector2 Speed)[] scrolls)
    {
        I32(scrolls.Length);
        foreach (var (id, speed) in scrolls) I32(id).Vec2(speed);
    }

    private void Room(int id, bool liquid)
    {
        I32(id).Vec3(new Vector3(-1)).Vec3(new Vector3(1));
        U8(id == 1 ? 1 : 0); // sky on the second room
        U8(0).U8(0).U8(0); // cold, outside, airlock
        U8(liquid ? 1 : 0).U8(liquid ? 1 : 0); // liquid, ambient
        U8(0).U8(0); // subroom, alpha
        F32(-1);
        Str(liquid ? "eax_water" : "");
        if (liquid)
        {
            F32(2).Color(new RflColor(0, 0, 255, 255)).Str("water.tga").F32(10).I32(1).I32(60);
            U8(1).I32(64).I32(64).F32(180).I32(2).F32(0.5f).F32(0.25f);
            Color(new RflColor(10, 20, 30, 255));
        }
    }

    private void Surface(int i)
    {
        I32(0).U8(i).U8(0).U8(8).U8(8);
        Vec2(new Vector2(16, 16)).Vec3(Vector3.Zero).Vec3(Vector3.One);
        Vec3(Vector3.UnitY).F32(0).I32(1);
        I32(0);
        I32(1).I32(0).I32(2).Vec2(Vector2.Zero).Vec2(Vector2.One);
        I32(0);
    }

    public LvlBytes Brush(int uid, Vector3 pos, RfMatrix orient, uint flags, Action<LvlBytes> solid, int life = -1, int state = 0)
    {
        I32(uid).Vec3(pos).StockMatrix(orient);
        solid(this);
        U32(flags);
        if (V < 0x80) I32(7);
        if (V >= 0x7A) I32(life);
        I32(state);
        return this;
    }

    public LvlBytes Light(int uid, Vector3 pos, uint flags, float range)
    {
        Head(uid, "Light", pos);
        U32(flags).Color(new RflColor(255, 200, 100, 255)).F32(range);
        F32(45).F32(5).F32(0.1f).I32(1).F32(0.5f);
        F32(1).F32(0).F32(0).F32(0).F32(0).F32(0);
        return this;
    }

    public LvlBytes Event(int uid, string cls, Vector3 pos, RfMatrix? orient, string str1 = "", params int[] links)
    {
        I32(uid).Str(cls).Vec3(pos).Str("script").U8(0).F32(1.5f).U8(1).U8(0).I32(3).I32(4).F32(0.25f).F32(0.75f);
        Str(str1).Str("");
        Uids(links);
        if (orient is { } m) StockMatrix(m);
        if (V >= 0xB0) Color(new RflColor(1, 2, 3, 255));
        return this;
    }

    public LvlBytes Entity(int uid, string cls, Vector3 pos, bool customRange)
    {
        Head(uid, cls, pos);
        I32(2).I32(1).I32(0).Str("path1").Str("loop").U8(0).U8(0);
        U8(1).U8(0).U8(0).U8(0).I32(10).I32(20).U8(0); // ready to fire .. ignore terrain
        U8(0).U8(1).F32(100).F32(50).I32(90);           // unknown2, crouched, life, armor, fov
        Str("12mm handgun").Str("").Str("Medical Kit").Str("").Str("").Str("skin1").Str("");
        U8(2).U8(1);   // AI mode, attack style
        I32(0);        // class list
        I32(-1).I32(-1).I32(-1);
        for (int i = 0; i < 14; i++) U8(i == 0 ? 1 : 0);
        U8(0).U8(1);   // fade corpse, never collide
        U8(customRange ? 1 : 0);
        if (customRange) F32(42);
        Str("none").Str("flashlight");
        return this;
    }

    public LvlBytes Item(int uid, string cls, Vector3 pos) => Head(uid, cls, pos).I32(5).I32(30).I32(0);

    public LvlBytes Clutter(int uid, string cls, Vector3 pos) => Head(uid, cls, pos).I32(0).Str("skin").Uids(1, 2);

    public LvlBytes Trigger(int uid, int shape, Vector3 pos)
    {
        I32(uid).Str("trig").U8(0).I32(shape).F32(2).I32(-1).U8(0).Str("").U8(1).U8(0).U8(0).U8(1).U8(0);
        if (shape == 0) Vec3(pos).F32(3);
        else Vec3(pos).StockMatrix(RfMatrix.Identity).Vec3(new Vector3(1, 2, 3)).U8(1);
        I32(-1).I32(-1).I32(-1).U8(0).F32(0).F32(0);
        if (V >= 0xB1) I32(-1);
        Uids(9);
        return this;
    }

    public LvlBytes Keyframe(int uid, Vector3 pos)
    {
        I32(uid).Vec3(pos).StockMatrix(RfMatrix.Identity).Str("").U8(0).F32(1).F32(2).F32(3).F32(0.1f).F32(0.2f);
        I32(-1).I32(-1).I32(-1).F32(90);
        return this;
    }

    public LvlBytes MovingData(params int[] keyframeUids)
    {
        I32(keyframeUids.Length);
        for (int i = 0; i < keyframeUids.Length; i++) Keyframe(keyframeUids[i], new Vector3(i, 0, 0));
        I32(1).I32(500).Vec3(Vector3.UnitY).StockMatrix(RfMatrix.Identity);
        U8(1).U8(0).U8(0).U8(0).U8(0); // door, rotate in place, backwards, travel time, force orient
        if (V >= 0xAE) U8(1);
        I32(3).I32(0);
        Str("start.wav").F32(1).Str("loop.wav").F32(0.5f).Str("stop.wav").F32(1).Str("close.wav").F32(1);
        return this;
    }

    public LvlBytes AlpineMesh(int uid, Vector3 pos, RfMatrix orient, bool clutter)
    {
        I32(uid).Vec3(pos).AlpineMatrix(orient).Str("").Str("crate.v3m").Str("").U8(2);
        U8(1).U8(0).Str("override.tga");
        I32(3).U8(clutter ? 1 : 0);
        if (clutter)
        {
            F32(100).Str("debris.v3m").Str("explosion").F32(2).F32(5);
            for (int i = 0; i < 11; i++) F32(1);
            Str("corpse.v3m").Str("").U8(1).U8(0xFF);
        }
        return this;
    }

    public LvlBytes Corona(int uid, Vector3 pos, string volumetric)
    {
        I32(uid).Vec3(pos).AlpineMatrix(RfMatrix.Identity).Str("").Color(new RflColor(255, 255, 0, 255)).Str("corona.tga");
        F32(30).F32(1).F32(10).F32(1).F32(50).Str(volumetric);
        if (volumetric.Length > 0) F32(2).F32(8);
        return this;
    }
}
