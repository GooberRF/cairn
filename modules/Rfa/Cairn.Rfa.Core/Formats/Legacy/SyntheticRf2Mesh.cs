using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Cairn.Rfa.Formats.Legacy;

/// <summary>
/// Red Faction II meshes (<c>.rfm</c>/<c>.rfc</c> version 0x114) and <c>.peg</c> texture packs built in code for
/// self-tests: no game bytes. The layouts are the ones <see cref="RfmFormat"/>, <see cref="RfcFormat"/> and
/// <see cref="PegCodec"/> read.
/// </summary>
public static class SyntheticRf2Mesh
{
    /// <summary>
    /// A static crate (<c>.rfm</c>): one object "crate" at (0, 0.5, 0) with two levels of detail (switching at 12 m), a
    /// box of six faces using <c>crate_side.tga</c> and <c>crate_top.tga</c>, a collision sphere and a prop point.
    /// </summary>
    public static byte[] SampleStatic()
    {
        var w = new Writer();
        w.Header(skinned: false);
        w.I(1); w.Z("csphere_crate"); w.I(-1); w.V(Vector3.Zero); w.F(0.9f);
        w.I(1); w.Z("smoke"); w.I(-1); w.V(new Vector3(0, 0.5f, 0)); w.Q(Quaternion.Identity);
        w.I(0); // bones (not stored for a static mesh)
        w.I(1); w.I(2); // objects, most LODs
        w.I(2); w.F(12f); w.F(float.MaxValue);
        w.LodHeader("crate", 2);
        w.LodHeader("crate", 1);
        // LOD 0: the sides and the top/bottom as two geometries
        var (sides, caps) = Box(0.5f);
        w.I(2); w.Z("crate_side.tga"); w.Z("crate_top.tga");
        w.U(2); w.Material("crate_side.tga"); w.Material("crate_top.tga");
        w.I(2);
        w.StaticGeometry(sides, 0, 0);
        w.StaticGeometry(caps, 1, 1);
        w.Tail(new Vector3(0, 0.5f, 0), 0.5f);
        // LOD 1: the sides only
        w.I(1); w.Z("crate_side.tga");
        w.U(1); w.Material("crate_side.tga");
        w.I(1);
        w.StaticGeometry(sides, 0, 0);
        w.Tail(new Vector3(0, 0.5f, 0), 0.5f);
        return w.ToArray();
    }

    /// <summary>
    /// A two-bone character (<c>.rfc</c>): a root and an arm turned a quarter turn about Y in the bind pose, a quad bound
    /// to both with <c>guard_body.tga</c>, a sphere and a prop point on the arm, welded source indices.
    /// </summary>
    public static byte[] SampleCharacter()
    {
        var w = new Writer();
        w.Header(skinned: true);
        w.I(1); w.Z("csphere_arm"); w.I(1); w.V(new Vector3(0, 0.1f, 0)); w.F(0.25f);
        w.I(1); w.Z("primary_weapon"); w.I(1); w.V(new Vector3(0.2f, 0, 0)); w.Q(Quaternion.Identity);
        w.I(2);
        w.Z("root"); w.I(-1); foreach (float f in new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 }) w.F(f); w.V(Vector3.Zero);
        w.Z("arm"); w.I(0); foreach (float f in new float[] { 0, 0, -1, 0, 1, 0, 1, 0, 0 }) w.F(f); w.V(new Vector3(0, -1, 0));
        w.I(1); w.I(1);
        w.I(1); w.F(float.MaxValue);
        w.LodHeader("guard", 1);
        w.I(1); w.Z("guard_body.tga");
        w.U(1); w.Material("guard_body.tga");
        w.I(1);
        // two triangles over the four skinned vertices (winding along the +Y normals)
        w.I(2);
        foreach (var (v, uv) in new (int, Vector2)[] { (0, new(0, 0)), (2, new(0, 1)), (1, new(1, 0)), (2, new(0, 1)), (3, new(1, 1)), (1, new(1, 0)) })
        {
            w.I(v); w.F(uv.X); w.F(uv.Y);
        }
        w.I(0); w.I(0); w.F(0); w.U(0xCDCDCDCD);
        w.Tail(Vector3.Zero, 1f);
        w.I(4); w.I(4);
        var n = Vector3.UnitY;
        (Vector3 P, (int, float)[] W)[] skin =
        [
            (new(0, 1, 0), [(0, 1f)]), (new(1, 1, 0), [(0, 0.5f), (1, 0.5f)]), (new(0, 1, 1), [(1, 1f)]), (new(1, 1, 1), [(1, 1f)]),
        ];
        for (int i = 0; i < skin.Length; i++)
        {
            w.V(skin[i].P); w.V(n); w.H((ushort)i);
            for (int s = 0; s < 4; s++)
            {
                if (s < skin[i].W.Length) { w.I(skin[i].W[s].Item1); w.F(skin[i].W[s].Item2); }
                else { w.I(Rf2MeshFile.NoBone); w.F(0); }
            }
        }
        return w.ToArray();
    }

    /// <summary>A one-bone character (<c>.rfc</c>) with one triangle per texture, each on its own three vertices.</summary>
    public static byte[] SampleManyTextures(IReadOnlyList<string> textures)
    {
        ArgumentNullException.ThrowIfNull(textures);
        var w = new Writer();
        w.Header(skinned: true);
        w.I(0);
        w.I(0);
        w.I(1);
        w.Z("root"); w.I(-1); foreach (float f in new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 }) w.F(f); w.V(Vector3.Zero);
        w.I(1); w.I(1);
        w.I(1); w.F(float.MaxValue);
        w.LodHeader("busy", textures.Count);
        w.I(textures.Count);
        foreach (string t in textures) w.Z(t);
        w.U((uint)textures.Count);
        foreach (string t in textures) w.Material(t);
        w.I(textures.Count);
        for (int i = 0; i < textures.Count; i++)
        {
            w.I(1);
            foreach (var (v, uv) in new (int, Vector2)[] { (i * 3, new(0, 0)), (i * 3 + 1, new(0, 1)), (i * 3 + 2, new(1, 0)) })
            {
                w.I(v); w.F(uv.X); w.F(uv.Y);
            }
            w.I(i); w.I(i); w.F(0); w.U(0xCDCDCDCD);
        }
        w.Tail(Vector3.Zero, 1f);
        w.I(textures.Count * 3); w.I(textures.Count * 3);
        for (int i = 0; i < textures.Count * 3; i++)
        {
            w.V(new Vector3(i / 3, i % 3 == 1 ? 1 : 0, i % 3 == 2 ? 1 : 0)); w.V(Vector3.UnitY); w.H((ushort)i);
            w.I(0); w.F(1f);
            for (int s = 1; s < 4; s++) { w.I(Rf2MeshFile.NoBone); w.F(0); }
        }
        return w.ToArray();
    }

    /// <summary>
    /// A Red Faction II texture pack (GEKV version 6) of 32-bit textures, each a two-colour checker of the given colour
    /// (BGRA) and a darker one, so a preview shows which texture went where.
    /// </summary>
    public static byte[] SamplePeg(params (string Name, int Width, int Height, uint Bgra)[] textures)
    {
        ArgumentNullException.ThrowIfNull(textures);
        const int header = 32, entry = 64;
        var offsets = new int[textures.Length];
        int at = header + textures.Length * entry;
        for (int i = 0; i < textures.Length; i++) { offsets[i] = at; at += (textures[i].Width * textures[i].Height * 4 + 15) & ~15; }
        var b = new byte[at];
        BinaryPrimitives.WriteUInt32LittleEndian(b, PegCodec.Signature);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), 6);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), textures.Length * entry);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(16), textures.Length);
        for (int i = 0; i < textures.Length; i++)
        {
            var (name, width, height, colour) = textures[i];
            int e = header + i * entry;
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(e), (ushort)width);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(e + 2), (ushort)height);
            b[e + 4] = 7; // 32-bit
            b[e + 7] = 1; // frames
            b[e + 9] = 1; // mips
            Encoding.Latin1.GetBytes(name).AsSpan(0, Math.Min(name.Length, 47)).CopyTo(b.AsSpan(e + 12));
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(e + 60), offsets[i]);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int o = offsets[i] + (y * width + x) * 4;
                    bool dark = ((x * 4 / Math.Max(1, width)) + (y * 4 / Math.Max(1, height))) % 2 == 1;
                    b[o] = (byte)((colour & 0xFF) >> (dark ? 1 : 0));
                    b[o + 1] = (byte)(((colour >> 8) & 0xFF) >> (dark ? 1 : 0));
                    b[o + 2] = (byte)(((colour >> 16) & 0xFF) >> (dark ? 1 : 0));
                    b[o + 3] = 0x80; // opaque in the RF2 packs' alpha scale
                }
            }
        }
        return b;
    }

    /// <summary>A unit box's four sides and its top and bottom, as (position, normal) quads with UVs, each face its own vertices.</summary>
    private static (List<(Vector3 P, Vector3 N, Vector2 Uv)[]> Sides, List<(Vector3 P, Vector3 N, Vector2 Uv)[]> Caps) Box(float h)
    {
        (Vector3 P, Vector3 N, Vector2 Uv)[] Quad(Vector3 n, Vector3 u, Vector3 v)
        {
            // corners counter-clockwise seen from outside: c, c+u, c+u+v, c+v around the face centre n*h
            var c = n * h - (u + v) * h;
            return [(c, n, new(0, 1)), (c + 2 * h * u, n, new(1, 1)), (c + 2 * h * (u + v), n, new(1, 0)), (c + 2 * h * v, n, new(0, 0))];
        }
        var sides = new List<(Vector3, Vector3, Vector2)[]>
        {
            Quad(Vector3.UnitZ, Vector3.UnitY, Vector3.UnitX), Quad(-Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY),
            Quad(Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY), Quad(-Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ),
        };
        var caps = new List<(Vector3, Vector3, Vector2)[]> { Quad(Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ), Quad(-Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX) };
        return (sides, caps);
    }

    private sealed class Writer
    {
        private readonly List<byte> _b = [];

        public void U(uint v) { Span<byte> s = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(s, v); _b.AddRange(s.ToArray()); }
        public void I(int v) => U((uint)v);
        public void H(ushort v) { _b.Add((byte)v); _b.Add((byte)(v >> 8)); }
        public void F(float v) => I(BitConverter.SingleToInt32Bits(v));
        public void V(Vector3 v) { F(v.X); F(v.Y); F(v.Z); }
        public void Q(Quaternion q) { F(q.X); F(q.Y); F(q.Z); F(q.W); }
        public void Z(string s) { _b.AddRange(Encoding.Latin1.GetBytes(s)); _b.Add(0); }

        public void Header(bool skinned) { U(Rf2MeshFile.Magic); I(Rf2MeshFile.Version); _b.Add((byte)(skinned ? 1 : 0)); }

        public void LodHeader(string name, int materials) { Z(name); Z("None"); U(0); U((uint)materials); }

        private void Name65(string s)
        {
            H(65);
            var buffer = new byte[65];
            Encoding.Latin1.GetBytes(s).AsSpan(0, Math.Min(s.Length, 64)).CopyTo(buffer);
            _b.AddRange(buffer);
        }

        /// <summary>A material record naming <paramref name="texture"/> (the other fields as the stock files hold them).</summary>
        public void Material(string texture)
        {
            U(0); U(1); _b.Add(0); U(0xFFFFFFFF); U(2);
            _b.Add(1); U(0); Name65(texture); U(0); F(1); U(0);
            _b.Add(0);
            U(15); I(0); F(0); F(0); F(0); Name65(""); U(0xFFFFFFFF); I(0); F(0); I(0);
        }

        /// <summary>A static geometry of quads: each quad's four vertices, two triangles per quad.</summary>
        public void StaticGeometry(List<(Vector3 P, Vector3 N, Vector2 Uv)[]> quads, int texture, int material)
        {
            I(quads.Count * 4);
            foreach (var q in quads) foreach (var (p, n, _) in q) { V(p); V(n); }
            I(quads.Count * 2);
            for (int k = 0; k < quads.Count; k++)
            {
                foreach (int c in new[] { 0, 2, 1, 0, 3, 2 })
                {
                    I(k * 4 + c); F(quads[k][c].Uv.X); F(quads[k][c].Uv.Y);
                }
            }
            I(texture); I(material); F(0);
        }

        public void Tail(Vector3 offset, float half) { V(offset); F(half * 1.8f); V(new Vector3(-half)); V(new Vector3(half)); }

        public byte[] ToArray() => [.. _b];
    }
}
