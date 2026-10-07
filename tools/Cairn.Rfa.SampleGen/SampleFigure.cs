using System.Collections.Immutable;
using System.Numerics;
using Cairn.Formats;
using Cairn.Rfa.Formats.V3d;
using Cairn.Formats.Maths;

namespace Cairn.Rfa.SampleGen;

/// <summary>
/// The sample character: a blocky 15-bone biped about 1.77 m tall, built from boxes that are each
/// rigidly skinned to one bone, with one 64x64 texture atlas. RF space: left-handed, +Y up, the
/// figure faces +Z with its feet on y = 0, so its right side is +X. Every bone's rest rotation is the
/// identity, which keeps the clips readable: a key's rotation is simply the joint's turn in its
/// parent's frame, and a position key is the joint's offset from its parent's joint.
/// </summary>
public static class SampleFigure
{
    /// <summary>The texture's edge length in pixels.</summary>
    public const int TextureSize = 64;

    /// <summary>Atlas tiles are this many pixels square (a 4 x 4 grid).</summary>
    private const int Tile = 16;

    /// <summary>One bone of the rig: its name, parent index and joint position in model space.</summary>
    public readonly record struct BoneSpec(string Name, int Parent, Vector3 Joint);

    /// <summary>Bone indices, in the order every sample clip addresses them.</summary>
    public const int Pelvis = 0, Spine = 1, Head = 2,
        UpperArmL = 3, ForearmL = 4, HandL = 5,
        UpperArmR = 6, ForearmR = 7, HandR = 8,
        ThighL = 9, ShinL = 10, FootL = 11,
        ThighR = 12, ShinR = 13, FootR = 14;

    /// <summary>The rig. Left is -X, right is +X (the figure faces +Z).</summary>
    public static ImmutableArray<BoneSpec> Bones { get; } =
    [
        new("pelvis", -1, new Vector3(0f, 0.95f, 0f)),
        new("spine", Pelvis, new Vector3(0f, 1.04f, 0f)),
        new("head", Spine, new Vector3(0f, 1.50f, 0f)),
        new("upper_arm_l", Spine, new Vector3(-0.25f, 1.46f, 0f)),
        new("forearm_l", UpperArmL, new Vector3(-0.25f, 1.17f, 0f)),
        new("hand_l", ForearmL, new Vector3(-0.25f, 0.94f, 0f)),
        new("upper_arm_r", Spine, new Vector3(0.25f, 1.46f, 0f)),
        new("forearm_r", UpperArmR, new Vector3(0.25f, 1.17f, 0f)),
        new("hand_r", ForearmR, new Vector3(0.25f, 0.94f, 0f)),
        new("thigh_l", Pelvis, new Vector3(-0.10f, 0.90f, 0f)),
        new("shin_l", ThighL, new Vector3(-0.10f, 0.49f, 0f)),
        new("foot_l", ShinL, new Vector3(-0.10f, 0.08f, 0f)),
        new("thigh_r", Pelvis, new Vector3(0.10f, 0.90f, 0f)),
        new("shin_r", ThighR, new Vector3(0.10f, 0.49f, 0f)),
        new("foot_r", ShinR, new Vector3(0.10f, 0.08f, 0f)),
    ];

    /// <summary>The bones as the file stores them (inverse bind; identity rotations).</summary>
    public static ImmutableArray<V3dBone> StoredBones { get; } =
        [.. Bones.Select(b => V3dBuilder.BoneFromRestWorld(b.Name, b.Parent, new Rigid(Quaternion.Identity, b.Joint)))];

    /// <summary>The atlas tiles, by (column, row) in the 4 x 4 grid.</summary>
    private enum Skin
    {
        Shirt,
        ShirtFront,
        Flesh,
        Face,
        Trousers,
        Boot,
        Belt,
        Hair,
        Glove,
    }

    /// <summary>One box of the figure: its corners in model space, the bone it follows, and the tile of each side.</summary>
    private sealed record Part(int Bone, Vector3 Min, Vector3 Max, Skin Sides, Skin? Front = null, Skin? Back = null, Skin? Top = null);

    private static readonly Part[] Parts =
    [
        new(Pelvis, new(-0.16f, 0.86f, -0.10f), new(0.16f, 1.04f, 0.10f), Skin.Belt),
        new(Spine, new(-0.18f, 1.04f, -0.11f), new(0.18f, 1.48f, 0.11f), Skin.Shirt, Front: Skin.ShirtFront),
        new(Head, new(-0.11f, 1.50f, -0.12f), new(0.11f, 1.77f, 0.12f), Skin.Flesh, Front: Skin.Face, Back: Skin.Hair, Top: Skin.Hair),
        Arm(UpperArmL, -0.25f, 1.18f, 1.47f, 0.055f, Skin.Shirt),
        Arm(ForearmL, -0.25f, 0.94f, 1.18f, 0.048f, Skin.Flesh),
        new(HandL, new(-0.28f, 0.80f, -0.045f), new(-0.22f, 0.94f, 0.045f), Skin.Glove),
        Arm(UpperArmR, 0.25f, 1.18f, 1.47f, 0.055f, Skin.Shirt),
        Arm(ForearmR, 0.25f, 0.94f, 1.18f, 0.048f, Skin.Flesh),
        new(HandR, new(0.22f, 0.80f, -0.045f), new(0.28f, 0.94f, 0.045f), Skin.Glove),
        Leg(ThighL, -0.10f, 0.49f, 0.90f, 0.075f, 0.08f),
        Leg(ShinL, -0.10f, 0.08f, 0.49f, 0.065f, 0.07f),
        new(FootL, new(-0.16f, 0f, -0.07f), new(-0.04f, 0.08f, 0.17f), Skin.Boot),
        Leg(ThighR, 0.10f, 0.49f, 0.90f, 0.075f, 0.08f),
        Leg(ShinR, 0.10f, 0.08f, 0.49f, 0.065f, 0.07f),
        new(FootR, new(0.04f, 0f, -0.07f), new(0.16f, 0.08f, 0.17f), Skin.Boot),
    ];

    private static Part Arm(int bone, float x, float bottom, float top, float half, Skin skin) =>
        new(bone, new(x - half, bottom, -half), new(x + half, top, half), skin);

    private static Part Leg(int bone, float x, float bottom, float top, float halfX, float halfZ) =>
        new(bone, new(x - halfX, bottom, -halfZ), new(x + halfX, top, halfZ), Skin.Trousers);

    /// <summary>The mesh as primary data, ready for <see cref="V3dBuilder.Build"/>.</summary>
    public static V3dMeshDescription Description(string submeshName, string textureName)
    {
        var vertices = ImmutableArray.CreateBuilder<V3dMeshVertex>();
        var triangles = ImmutableArray.CreateBuilder<V3dMeshTriangle>();
        foreach (var part in Parts) AddBox(part, vertices, triangles);

        var material = new V3dMaterial(
            FixedString.FromText(textureName, V3dMaterial.NameSize), 0f, 0f, 0f, 0f,
            FixedString.FromText("", V3dMaterial.NameSize), 1u);

        return new V3dMeshDescription
        {
            Kind = V3dKind.Character,
            Bones = StoredBones,
            CollisionSpheres =
            [
                // Centres are relative to the bone (whose rest rotation is the identity).
                new V3dCollisionSphere(FixedString.FromText("head", V3dCollisionSphere.NameSize), Head, new Vector3(0f, 0.13f, 0f), 0.16f, []),
                new V3dCollisionSphere(FixedString.FromText("torso", V3dCollisionSphere.NameSize), Spine, new Vector3(0f, 0.20f, 0f), 0.30f, []),
                new V3dCollisionSphere(FixedString.FromText("legs", V3dCollisionSphere.NameSize), Pelvis, new Vector3(0f, -0.45f, 0f), 0.40f, []),
            ],
            PropPoints =
            [
                // Where a held item attaches: in the right fist, relative to the hand bone.
                new V3dPropPoint(FixedString.FromText("hand_grip", V3dPropPoint.NameSize), Quaternion.Identity, new Vector3(0f, -0.07f, 0f), HandR),
            ],
            Submeshes =
            [
                new V3dSubmeshDescription
                {
                    Name = FixedString.FromText(submeshName, V3dSubmesh.NameSize),
                    Materials = [material],
                    Lods =
                    [
                        new V3dLodDescription
                        {
                            Distance = 0f,
                            Groups = [new V3dMaterialGroup { Material = 0, Vertices = vertices.ToImmutable(), Triangles = triangles.ToImmutable() }],
                        },
                    ],
                },
            ],
        };
    }

    /// <summary>An axis-aligned direction: which component (0 X, 1 Y, 2 Z) and which way.</summary>
    private readonly record struct Axis(int Index, int Sign)
    {
        public Vector3 Vector => Index switch
        {
            0 => new Vector3(Sign, 0, 0),
            1 => new Vector3(0, Sign, 0),
            _ => new Vector3(0, 0, Sign),
        };
    }

    /// <summary>
    /// The six sides of a box: outward normal, and the directions that are "right" and "up" on the
    /// texture for someone looking at that side from outside (left-handed: right = up x view).
    /// </summary>
    private static readonly (Axis Normal, Axis Right, Axis Up)[] Sides =
    [
        (new(2, +1), new(0, -1), new(1, +1)), // front (+Z)
        (new(2, -1), new(0, +1), new(1, +1)), // back
        (new(0, +1), new(2, +1), new(1, +1)), // right side (+X)
        (new(0, -1), new(2, -1), new(1, +1)), // left side
        (new(1, +1), new(0, +1), new(2, +1)), // top (texture up = forward)
        (new(1, -1), new(0, -1), new(2, +1)), // bottom
    ];

    private static void AddBox(Part part, ImmutableArray<V3dMeshVertex>.Builder vertices, ImmutableArray<V3dMeshTriangle>.Builder triangles)
    {
        ImmutableArray<V3dBoneInfluence> skin = [new V3dBoneInfluence(part.Bone, 1f)];
        var p = new float[3];
        for (int s = 0; s < Sides.Length; s++)
        {
            var (normal, right, up) = Sides[s];
            var tile = s switch
            {
                0 => part.Front ?? part.Sides,
                1 => part.Back ?? part.Sides,
                4 => part.Top ?? part.Sides,
                _ => part.Sides,
            };
            var (u0, v0, u1, v1) = TileUv(tile);

            // Corners top-left, top-right, bottom-right, bottom-left as seen from outside. Every
            // coordinate is taken from the box's min or max, so shared corners are bit-identical.
            int first = vertices.Count;
            (int R, int U, float Tu, float Tv)[] corners = [(-1, +1, u0, v0), (+1, +1, u1, v0), (+1, -1, u1, v1), (-1, -1, u0, v1)];
            foreach (var (r, u, tu, tv) in corners)
            {
                p[normal.Index] = Pick(part, normal.Index, normal.Sign > 0);
                p[right.Index] = Pick(part, right.Index, r * right.Sign > 0);
                p[up.Index] = Pick(part, up.Index, u * up.Sign > 0);
                vertices.Add(new V3dMeshVertex(new Vector3(p[0], p[1], p[2]), normal.Vector, new Vector2(tu, tv), skin));
            }

            // RF's front faces are the ones whose cross(B - A, C - A) points out of the solid (the
            // stock exporter's plane rule; the engine culls the others). Check rather than assume.
            var a = vertices[first].Position;
            var cross = Vector3.Cross(vertices[first + 1].Position - a, vertices[first + 2].Position - a);
            if (Vector3.Dot(cross, normal.Vector) > 0f)
            {
                triangles.Add(new V3dMeshTriangle(first, first + 1, first + 2));
                triangles.Add(new V3dMeshTriangle(first, first + 2, first + 3));
            }
            else
            {
                triangles.Add(new V3dMeshTriangle(first, first + 2, first + 1));
                triangles.Add(new V3dMeshTriangle(first, first + 3, first + 2));
            }
        }
    }

    private static float Pick(Part part, int axis, bool max)
    {
        var v = max ? part.Max : part.Min;
        return axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };
    }

    private static (int Column, int Row) TileCell(Skin skin) => ((int)skin % 4, (int)skin / 4);

    /// <summary>A tile's UV rectangle, inset by half a texel so filtering never reaches the next tile.</summary>
    private static (float U0, float V0, float U1, float V1) TileUv(Skin skin)
    {
        var (column, row) = TileCell(skin);
        // Multiples of 1/128: exact in binary, so the stored UVs do not depend on rounding.
        return ((column * Tile + 0.5f) / TextureSize, (row * Tile + 0.5f) / TextureSize,
            (column * Tile + Tile - 0.5f) / TextureSize, (row * Tile + Tile - 0.5f) / TextureSize);
    }

    // ── Texture ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The 64x64 atlas as BGR bytes, top row first: flat-coloured tiles with a dark outline (so the
    /// boxes' edges read in the viewport), a face on the head's front and a zip on the shirt's front so
    /// the facing is obvious, and a grey checker in the unused tiles.
    /// </summary>
    public static byte[] Texture()
    {
        var pixels = new byte[TextureSize * TextureSize * 3];
        for (int y = 0; y < TextureSize; y++)
        {
            for (int x = 0; x < TextureSize; x++)
            {
                int index = (y / Tile) * 4 + x / Tile;
                var (r, g, b) = index < Enum.GetValues<Skin>().Length
                    ? TilePixel((Skin)index, x % Tile, y % Tile)
                    : (((x / 4) + (y / 4)) & 1) == 0 ? (128, 128, 128) : (96, 96, 96);
                int i = (y * TextureSize + x) * 3;
                pixels[i] = (byte)b;
                pixels[i + 1] = (byte)g;
                pixels[i + 2] = (byte)r;
            }
        }
        return pixels;
    }

    private static (int R, int G, int B) TilePixel(Skin skin, int x, int y)
    {
        var (r, g, b) = skin switch
        {
            Skin.Shirt or Skin.ShirtFront => (70, 96, 140),
            Skin.Flesh or Skin.Face => (222, 176, 136),
            Skin.Trousers => (84, 92, 60),
            Skin.Boot => (70, 48, 34),
            Skin.Belt => (58, 54, 50),
            Skin.Hair => (60, 42, 30),
            _ => (44, 44, 48),
        };

        bool edge = x == 0 || y == 0 || x == Tile - 1 || y == Tile - 1;
        bool mark = skin switch
        {
            // Two eyes and a mouth.
            Skin.Face => (y is 5 or 6 && x is 4 or 5 or 10 or 11) || (y == 11 && x is >= 6 and <= 9),
            // A zip down the middle and a chest band.
            Skin.ShirtFront => x is 7 or 8 || y is 4 or 5,
            // A buckle.
            Skin.Belt => y is >= 2 and <= 5 && x is >= 6 and <= 9,
            _ => false,
        };

        if (edge) return Scale(r, g, b, 60);
        if (mark)
        {
            return skin switch
            {
                Skin.Face => (40, 30, 30),
                Skin.Belt => (190, 170, 90),
                _ => Scale(r, g, b, 140),
            };
        }
        // A little vertical shading so flat faces still read as surfaces.
        return Scale(r, g, b, 108 - y);
    }

    private static (int R, int G, int B) Scale(int r, int g, int b, int percent) =>
        (Math.Min(255, r * percent / 100), Math.Min(255, g * percent / 100), Math.Min(255, b * percent / 100));
}
