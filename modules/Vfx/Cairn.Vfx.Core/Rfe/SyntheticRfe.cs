using System.IO;
using System.Text;
using Cairn.Rfa.Formats.Legacy;

namespace Cairn.Vfx.Rfe;

/// <summary>
/// Small Red Faction II effects made in memory for tests and self-tests: <see cref="Write"/> lays an <see cref="RfeFile"/>
/// out the way the game's loader reads it. Cairn never offers to write <c>.rfe</c> files; this exists so the reader,
/// sampler, preview and conversion can be checked on deterministic data.
/// </summary>
public static class SyntheticRfe
{
    /// <summary>The bytes of <paramref name="file"/> in the PC layout (version 3).</summary>
    public static byte[] Write(RfeFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.Latin1, leaveOpen: true);
        w.Write(Encoding.ASCII.GetBytes("FEFR"));
        w.Write(RfeReader.Version);
        w.Write(1);
        w.Write(file.Duration);
        w.Write(file.Radius);
        w.Write(file.Textures.Length);
        foreach (string t in file.Textures)
        {
            var name = new byte[24];
            Encoding.Latin1.GetBytes(t.AsSpan(0, Math.Min(t.Length, 23)), name);
            w.Write(name);
        }
        w.Write(file.Materials.Length);
        foreach (var m in file.Materials)
        {
            w.Write(m.Texture);
            w.Write(m.Blend);
            Keys(w, m.SelfIllumination);
            Keys(w, m.Opacity);
        }
        w.Write(file.Meshes.Length);
        foreach (var o in file.Meshes)
        {
            w.Write(o.Facing);
            w.Write(o.Material);
            Vectors(w, o.Positions);
            Quaternions(w, o.Rotations);
            Vectors(w, o.Scales);
            Body(w, o.Body);
        }
        w.Write(file.Emitters.Length);
        foreach (var e in file.Emitters)
        {
            w.Write(e.Shape); w.Write(e.Flags); w.Write(e.Material); w.Write(e.MaxParticles);
            foreach (float f in new[] { e.Radius, e.Streak, e.SpeedVariance, e.Spread, e.Lifetime, e.LifetimeVariance, e.FadeIn, e.FadeOut, e.Grow, e.Shrink, e.Width, e.Height, e.SpawnCone, e.Unknown44 })
                w.Write(f);
            Vector(w, e.Acceleration);
            Vectors(w, e.Positions);
            Quaternions(w, e.Rotations);
            Keys(w, e.BirthRate);
            Keys(w, e.Speed);
        }
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>A square of <paramref name="size"/> metres in the XY plane (facing +Z), textured 0..1 (V stored negated, as the stock files do).</summary>
    public static Rf2StaticBody Quad(string texture, float size = 1)
    {
        float h = size / 2;
        Vector3 n = Vector3.UnitZ;
        ImmutableArray<Vector3> positions = [new(-h, h, 0), new(h, -h, 0), new(h, h, 0), new(-h, -h, 0)];
        ImmutableArray<Rf2StaticTriangle> triangles =
        [
            new(0, 1, 2, new(0, 0), new(1, -1), new(1, 0)),
            new(1, 0, 3, new(1, -1), new(0, 0), new(0, -1)),
        ];
        return new Rf2StaticBody([texture], 0, [new Rf2StaticGeometry(positions, [n, n, n, n], triangles, 0, -1)], Vector3.Zero, h * 1.4143f, new(-h, -h, 0), new(h, h, 0));
    }

    /// <summary>
    /// A 3 s effect with a quad that moves 2 m along X, turns 90 degrees about Y and doubles in size (5, 3 and 2 keys), fading
    /// in and out; a camera-facing glow; and a rectangle emitter of 30 particles a second with gravity.
    /// </summary>
    public static RfeFile Sample() => new(3.0002f, 2.5f, ["crate_side.tga", "glow.tga", "spark.tga"],
        [
            new RfeMaterial(0, 0, [new(0, 0.5f)], [new(0, 0), new(0.5f, 1), new(2.5f, 1), new(3, 0)]),
            new RfeMaterial(1, 2, [new(0, 0)], [new(0, 1)]),
            new RfeMaterial(2, 2, [new(0, 0)], [new(0, 0.8f), new(3, 0.2f)]),
        ],
        [
            new RfeMeshObject(0, 0, [new(0, 0, 0), new(0.5f, 0, 0), new(1, 0, 0), new(1.5f, 0, 0), new(2, 0, 0)],
                [Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2)],
                [Vector3.One, new(2, 2, 2)], Quad("crate_side.tga")),
            new RfeMeshObject(1, 1, [new(0, 1.5f, 0)], [Quaternion.Identity], [Vector3.One], Quad("glow.tga", 0.6f)),
        ],
        [
            new RfeEmitter
            {
                Shape = 1, Flags = 1 | 2, Material = 2, MaxParticles = 60, Radius = 0.05f, SpeedVariance = 0.2f, Spread = 0.9f,
                Lifetime = 1.2f, LifetimeVariance = 0.25f, FadeIn = 0, FadeOut = 0.6f, Grow = 0.1f, Shrink = 0.7f, Width = 0.4f, Height = 0.4f,
                SpawnCone = 1, Positions = [new(0, 0.2f, 0)],
                // 180 degrees about X: the launch axis (local -Y) points up
                Rotations = [new Quaternion(1, 0, 0, 0)],
                BirthRate = [new(0, 0), new(0.1f, 30), new(2.9f, 30), new(2.95f, 0)],
                Speed = [new(0, 3)],
            },
        ]);

    /// <summary>The first bytes of an effect in the older layout the PC game refuses (version 3, then a float duration).</summary>
    public static byte[] OldLayout()
    {
        var bytes = new byte[0x40];
        Encoding.ASCII.GetBytes("FEFR").CopyTo(bytes, 0);
        BitConverter.GetBytes(3).CopyTo(bytes, 4);
        BitConverter.GetBytes(2.0f).CopyTo(bytes, 8);
        BitConverter.GetBytes(1.5f).CopyTo(bytes, 12);
        BitConverter.GetBytes(1).CopyTo(bytes, 0x10);
        BitConverter.GetBytes(0x38 - 0x14).CopyTo(bytes, 0x14);
        return bytes;
    }

    private static void Keys(BinaryWriter w, ImmutableArray<RfeKey> keys)
    {
        w.Write(keys.IsDefault ? 0 : keys.Length);
        if (keys.IsDefault) return;
        foreach (var k in keys) { w.Write(k.Time); w.Write(k.Value); }
    }

    private static void Vector(BinaryWriter w, Vector3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }

    private static void Vectors(BinaryWriter w, ImmutableArray<Vector3> keys)
    {
        w.Write(keys.IsDefault ? 0 : keys.Length);
        if (keys.IsDefault) return;
        foreach (var v in keys) Vector(w, v);
    }

    private static void Quaternions(BinaryWriter w, ImmutableArray<Quaternion> keys)
    {
        w.Write(keys.IsDefault ? 0 : keys.Length);
        if (keys.IsDefault) return;
        foreach (var q in keys) { w.Write(q.X); w.Write(q.Y); w.Write(q.Z); w.Write(q.W); }
    }

    private static void Body(BinaryWriter w, Rf2StaticBody body)
    {
        w.Write(body.Textures.Length);
        foreach (string t in body.Textures) { w.Write(Encoding.Latin1.GetBytes(t)); w.Write((byte)0); }
        w.Write(0); // no material records
        w.Write(body.Geometries.Length);
        foreach (var g in body.Geometries)
        {
            w.Write(g.Positions.Length);
            for (int i = 0; i < g.Positions.Length; i++) { Vector(w, g.Positions[i]); Vector(w, i < g.Normals.Length ? g.Normals[i] : Vector3.UnitY); }
            w.Write(g.Triangles.Length);
            foreach (var t in g.Triangles)
            {
                w.Write(t.A); w.Write(t.UvA.X); w.Write(t.UvA.Y);
                w.Write(t.B); w.Write(t.UvB.X); w.Write(t.UvB.Y);
                w.Write(t.C); w.Write(t.UvC.X); w.Write(t.UvC.Y);
            }
            w.Write(g.Texture);
            w.Write(g.Material);
            w.Write(0xCDCDCDCDu);
        }
        Vector(w, body.Offset);
        w.Write(body.Radius);
        Vector(w, body.BoxMin);
        Vector(w, body.BoxMax);
    }
}
