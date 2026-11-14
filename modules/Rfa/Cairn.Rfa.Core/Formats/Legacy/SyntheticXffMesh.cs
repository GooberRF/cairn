using System.Numerics;
using Cairn.Formats.XForge;

namespace Cairn.Rfa.Formats.Legacy;

/// <summary>
/// Small Red Faction for N-Gage scenes for tests and self-tests (written with <see cref="SyntheticXff"/>; Cairn never
/// writes <c>.xff</c> otherwise): a crate model, a two-part character with its walk loop, and a level. Boxes are built
/// the N-Gage way round: each triangle's <c>cross(B − A, C − A)</c> and its stored normals point into the box.
/// </summary>
public static class SyntheticXffMesh
{
    /// <summary>The texture the samples name for their textured parts.</summary>
    public const string Texture = "lvl0_crate_01.pcx";

    /// <summary>
    /// A model: a textured box <c>crate</c> (one unit, sitting on the origin) and an untextured, additive box <c>glow</c>
    /// above it in an orange vertex colour.
    /// </summary>
    public static byte[] SampleModel()
    {
        var x = new SyntheticXff();
        uint root = x.Root();
        uint marker = x.Marker(2020, root, "gr_crate");
        Box(x, marker, "crate", new Vector3(0, 0.5f, 0), 0.5f, Texture);
        Box(x, marker, "glow", new Vector3(0, 1.25f, 0), 0.2f, null, blendDest: 2);
        return x.ToArray();
    }

    /// <summary>
    /// A character (an enemy marker): a body box on track <c>anim_body</c> (0 to 533 ms, bobbing), a head box on track
    /// <c>anim_head</c> under it (−266 to 267 ms, turning), and an IK helper track without geometry, at 30 keys a second.
    /// </summary>
    public static byte[] SampleCharacter()
    {
        var x = new SyntheticXff();
        uint root = x.Root();
        uint marker = x.Marker(3202, root, "player");
        uint ik = x.Track(root, "anim_IK Chain01", 0, 533, 30f, [Key(Vector3.Zero, Quaternion.Identity), Key(Vector3.One, Quaternion.Identity)]);
        x.Helper(ik, "IK Chain01", Vector3.Zero);
        var bodyKeys = Enumerable.Range(0, 17).Select(k => Key(new Vector3(0, 0.3f + 0.03f * MathF.Sin(k / 16f * MathF.Tau), 0), Quaternion.Identity)).ToList();
        uint body = x.Track(marker, "anim_body", 0, 533, 30f, bodyKeys);
        uint bodyNode = Box(x, body, "body", Vector3.Zero, 0.15f, Texture);
        var headKeys = Enumerable.Range(0, 17).Select(k => Key(new Vector3(0, 0.25f, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.6f * MathF.Sin(k / 16f * MathF.Tau)))).ToList();
        uint head = x.Track(bodyNode, "anim_head", -266, 267, 30f, headKeys);
        Box(x, head, "head", Vector3.Zero, 0.08f, null);
        return x.ToArray();
    }

    /// <summary>A level: an area marker with a floor box (the Levels module's, which the meshes module declines).</summary>
    public static byte[] SampleLevel()
    {
        var x = new SyntheticXff();
        uint root = x.Root();
        uint area = x.Marker(XffMarkers.Area, root, "room01");
        Box(x, area, "floor", Vector3.Zero, 2f, Texture);
        return x.ToArray();
    }

    private static XffKey Key(Vector3 position, Quaternion rotation) => new(position, rotation, Vector3.One);

    /// <summary>A box of half-size <paramref name="half"/> at <paramref name="centre"/> under <paramref name="parent"/>, wound the N-Gage way; returns its node.</summary>
    public static uint Box(SyntheticXff x, uint parent, string name, Vector3 centre, float half, string? texture, byte blendSource = 2, byte blendDest = 1)
    {
        ArgumentNullException.ThrowIfNull(x);
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var indices = new List<ushort>();
        var uvs = new List<Vector2>();
        var colours = new List<XffColour>();
        Vector3[] axes = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
        Vector2[] corner = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        foreach (var axis in axes)
        {
            foreach (float sign in new[] { 1f, -1f })
            {
                var n = axis * sign;
                var u = axis == Vector3.UnitY ? Vector3.UnitX : Vector3.UnitY;
                var v = Vector3.Cross(n, u);
                int first = positions.Count;
                for (int c = 0; c < 4; c++)
                {
                    var (a, b) = (corner[c].X * 2 - 1, corner[c].Y * 2 - 1);
                    positions.Add(centre + (n + u * a + v * b) * half);
                    normals.Add(-n); // stored pointing into the box, as the game's files
                }
                foreach (var (p, q, r) in new[] { (0, 1, 2), (0, 2, 3) })
                {
                    int ia = first + p, ib = first + q, ic = first + r;
                    // N-Gage winding: cross(B − A, C − A) points away from the visible side
                    if (Vector3.Dot(Vector3.Cross(positions[ib] - positions[ia], positions[ic] - positions[ia]), n) > 0) (ib, ic) = (ic, ib);
                    foreach (int i in new[] { ia, ib, ic })
                    {
                        indices.Add((ushort)i);
                        uvs.Add(corner[i - first]);
                        colours.Add(new XffColour(230, 150, 40, 0));
                    }
                }
            }
        }
        uint node = x.Object(parent, name);
        uint tex = texture is null ? XffFile.NoReference : x.Texture(texture);
        uint material = x.Material(byte0: blendSource, byte1: blendDest);
        uint vb = x.VertexBuffer(positions, normals);
        uint il = x.IndexList(indices);
        uint fa = x.FaceAttributes(indices.Count / 3, colours, texture is null ? null : uvs);
        x.MeshInstance(node, "mesh_" + name, [SyntheticXff.Submesh(vb, il, indices.Count, fa, material, tex)]);
        return node;
    }
}
