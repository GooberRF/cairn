using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Cairn.Rfa.Formats.V3d;

namespace Cairn.Rfa.Formats.Legacy;

/// <summary>
/// Builds exporter meshes (.v3d / .vcm layout) in code for tests and self-tests: no game bytes. Each call adds one
/// section; <see cref="ToArray"/> writes the header with the totals filled in, as the exporter did.
/// </summary>
public sealed class SyntheticExporterMesh(bool character)
{
    private readonly List<byte[]> _sections = [];
    private int _submeshes, _vertices, _faces, _normals, _materials, _lods, _dumbs, _spheres;

    public sealed record Face(int A, int B, int C, Vector2 Ua, Vector2 Ub, Vector2 Uc, int Material);

    public sealed record Material(string Texture, float Emissive = 0f, uint Flags = 0, float Reflection = 0f, string ReflectionMap = "");

    public SyntheticExporterMesh Submesh(string name, Vector3[] positions, Face[] faces, Material[] materials,
        (string Name, float Distance)[]? lods = null, Vector3[]? storedNormals = null)
    {
        var b = new Writer();
        b.Name(name, 24);
        b.Name("None", 24);
        b.I(positions.Length);
        foreach (var p in positions) b.V(p);
        // One stored normal per face corner (the exporter's layout), pointing along +Z unless given.
        b.I(faces.Length * 3);
        int normalIndex = 0;
        var cornerNormals = new int[faces.Length * 3];
        foreach (var f in faces)
        {
            foreach (int v in new[] { f.A, f.B, f.C })
            {
                b.V(storedNormals?[v] ?? Vector3.UnitZ);
                b.I(v);
                cornerNormals[normalIndex] = normalIndex;
                normalIndex++;
            }
        }
        b.I(materials.Length);
        foreach (var m in materials)
        {
            b.Name(m.Texture, 32);
            b.F(m.Emissive); b.F(0); b.F(0); b.F(m.Reflection);
            b.Name(m.ReflectionMap, 32);
            b.U(m.Flags);
        }
        b.I(faces.Length);
        for (int i = 0; i < faces.Length; i++)
        {
            var f = faces[i];
            b.I(f.A); b.I(f.B); b.I(f.C);
            b.I(i * 3); b.I(i * 3 + 1); b.I(i * 3 + 2);
            b.F(f.Ua.X); b.F(f.Ua.Y); b.F(f.Ub.X); b.F(f.Ub.Y); b.F(f.Uc.X); b.F(f.Uc.Y);
            b.I(f.Material);
        }
        var min = positions.Aggregate(Vector3.Min);
        var max = positions.Aggregate(Vector3.Max);
        var center = (min + max) / 2;
        b.V(center); b.F(positions.Max(p => (p - center).Length())); b.V(min); b.V(max);
        lods ??= [];
        b.I(lods.Length);
        foreach (var (lodName, distance) in lods)
        {
            b.Name(lodName, 24);
            b.F(distance);
        }
        Add(0x5355424D, b.ToArray());
        _submeshes++;
        _vertices += positions.Length;
        _faces += faces.Length;
        _normals += faces.Length * 3;
        _materials += materials.Length;
        _lods += lods.Length;
        return this;
    }

    public SyntheticExporterMesh Prop(string name, int parent, Quaternion rotation, Vector3 position)
    {
        var b = new Writer();
        b.Name(name, 24); b.I(parent); b.Q(rotation); b.V(position);
        Add(0x44554D42, b.ToArray());
        _dumbs++;
        return this;
    }

    public SyntheticExporterMesh Sphere(string name, int bone, Vector3 position, float radius)
    {
        var b = new Writer();
        b.Name(name, 24); b.I(bone); b.V(position); b.F(radius);
        Add(0x43535048, b.ToArray());
        _spheres++;
        return this;
    }

    public SyntheticExporterMesh Bones(params (string Name, Quaternion Rotation, Vector3 Position, int Parent)[] bones)
    {
        var b = new Writer();
        b.I(bones.Length);
        foreach (var bone in bones)
        {
            b.Name(bone.Name, 24); b.Q(bone.Rotation); b.V(bone.Position); b.I(bone.Parent);
        }
        Add(0x424F4E45, b.ToArray());
        return this;
    }

    /// <summary>A WAIT section: per vertex up to four (weight byte, bone) pairs, unused slots (0, 0xFF).</summary>
    public SyntheticExporterMesh Weights(params (byte Weight, byte Bone)[][] vertices)
    {
        var b = new Writer();
        b.I(vertices.Length);
        foreach (var v in vertices)
        {
            for (int s = 0; s < 4; s++)
            {
                b.Byte(s < v.Length ? v[s].Weight : (byte)0);
                b.Byte(s < v.Length ? v[s].Bone : (byte)0xFF);
            }
        }
        Add(0x57414954, b.ToArray());
        return this;
    }

    public SyntheticExporterMesh Raw(int type, byte[] body)
    {
        Add(type, body);
        return this;
    }

    private void Add(int type, byte[] body)
    {
        var b = new Writer();
        b.I(type); b.I(body.Length); b.Bytes(body);
        _sections.Add(b.ToArray());
    }

    public byte[] ToArray()
    {
        var b = new Writer();
        b.U(character ? V3dHeader.CharacterSignature : V3dHeader.StaticSignature);
        b.I(character ? 0x10000 : 0x40000);
        b.I(_submeshes); b.I(_vertices); b.I(_faces); b.I(_normals); b.I(_materials); b.I(_lods); b.I(_dumbs); b.I(_spheres);
        foreach (var s in _sections) b.Bytes(s);
        b.I(0); b.I(0);
        return b.ToArray();
    }

    private sealed class Writer
    {
        private readonly MemoryStream _s = new();
        public void I(int v) { Span<byte> x = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(x, v); _s.Write(x); }
        public void U(uint v) { Span<byte> x = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(x, v); _s.Write(x); }
        public void F(float v) { Span<byte> x = stackalloc byte[4]; BinaryPrimitives.WriteSingleLittleEndian(x, v); _s.Write(x); }
        public void V(Vector3 v) { F(v.X); F(v.Y); F(v.Z); }
        public void Q(Quaternion q) { F(q.X); F(q.Y); F(q.Z); F(q.W); }
        public void Byte(byte v) => _s.WriteByte(v);
        public void Bytes(byte[] v) => _s.Write(v);
        public void Name(string text, int size)
        {
            var bytes = new byte[size];
            Encoding.Latin1.GetBytes(text, bytes);
            _s.Write(bytes);
        }
        public byte[] ToArray() => _s.ToArray();
    }

    /// <summary>A unit cube's corners.</summary>
    public static Vector3[] Cube(float half = 0.5f, Vector3 offset = default) =>
    [
        new Vector3(-half, -half, -half) + offset, new Vector3(half, -half, -half) + offset,
        new Vector3(half, half, -half) + offset, new Vector3(-half, half, -half) + offset,
        new Vector3(-half, -half, half) + offset, new Vector3(half, -half, half) + offset,
        new Vector3(half, half, half) + offset, new Vector3(-half, half, half) + offset,
    ];

    /// <summary>The cube's 12 triangles (outward), the +Z face on material 1, the rest on material 0.</summary>
    public static Face[] CubeFaces(Vector2? uvOffset = null, bool oneMaterial = false)
    {
        var o = uvOffset ?? Vector2.Zero;
        Vector2 a = new Vector2(0, 0) + o, b = new Vector2(1, 0) + o, c = new Vector2(1, 1) + o, d = new Vector2(0, 1) + o;
        var quads = new (int, int, int, int, int)[]
        {
            (0, 3, 2, 1, 0), (4, 5, 6, 7, 1), (0, 1, 5, 4, 0), (2, 3, 7, 6, 0), (1, 2, 6, 5, 0), (3, 0, 4, 7, 0),
        };
        var faces = new List<Face>();
        foreach (var (p, q, r, s, m) in quads)
        {
            int material = oneMaterial ? 0 : m;
            faces.Add(new Face(p, q, r, a, b, c, material));
            faces.Add(new Face(r, s, p, c, d, a, material));
        }
        return [.. faces];
    }

    /// <summary>
    /// A sample static mesh (.v3d): a box on two materials (one double-sided and full bright), its UVs a tile down, a
    /// lower level of detail, a prop point before the submesh and a collision sphere.
    /// </summary>
    public static byte[] SampleStatic() => new SyntheticExporterMesh(character: false)
        .Prop("corona_1", -1, Quaternion.Identity, new Vector3(0, 1, 0))
        .Submesh("Box01", Cube(0.5f, new Vector3(0, 0.5f, 0)), CubeFaces(new Vector2(0, -1)),
            [new("box.tga"), new("screen.tga", Emissive: 1f, Flags: ExporterMeshConverter.ExporterTwoSided)], [("Box01_lod", 12f)])
        .Submesh("Box01_lod", Cube(0.5f, new Vector3(0, 0.5f, 0)), CubeFaces(oneMaterial: true), [new("box.tga")])
        .Sphere("csphere_1", -1, new Vector3(0, 0.5f, 0), 0.9f)
        .ToArray();

    /// <summary>A sample character mesh (.vcm): a box skinned to two bones, a collision sphere, a prop point and weights.</summary>
    public static byte[] SampleCharacter()
    {
        var mesh = new SyntheticExporterMesh(character: true)
            .Submesh("Body", Cube(), CubeFaces(), [new("body.tga"), new("body.tga", Flags: 1)])
            .Bones(("Root-BDBN-Pelvis", Quaternion.Identity, Vector3.Zero, -1),
                ("Root-BDBN-Spine", new Quaternion(-0.1f, -0.2f, -0.3f, -0.9273618f), new Vector3(0, -0.5f, 0), 0))
            .Sphere("head", 1, new Vector3(0, 0.2f, 0), 0.3f)
            .Prop("eye", 1, Quaternion.Identity, new Vector3(0, 0.3f, 0.1f));
        var weights = new (byte, byte)[8][];
        for (int i = 0; i < 8; i++) weights[i] = i < 4 ? [(255, 0)] : [(200, 1), (54, 0)];
        return mesh.Weights(weights).ToArray();
    }
}
