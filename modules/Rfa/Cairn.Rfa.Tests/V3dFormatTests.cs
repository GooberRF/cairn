using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using Cairn.Formats;
using Cairn.Rfa.Formats.V3d;

namespace Cairn.Rfa.Tests;

public class V3dFormatTests
{
    private static FixedString Name(string text, int length) => FixedString.FromText(text, length);

    /// <summary>A small synthetic character: one submesh, two LODs, a skinned triangle batch, three bones.</summary>
    internal static V3dFile SampleCharacter()
    {
        var bones = ImmutableArray.Create(
            new V3dBone(Name("pelvis", 24), Quaternion.Identity, new Vector3(0, -1, 0), 2),
            new V3dBone(Name("spine", 24), new Quaternion(0, 0.7071068f, 0, 0.7071068f), new Vector3(0, -1.5f, 0), 0),
            new V3dBone(Name("root", 24), Quaternion.Identity, Vector3.Zero, -1));

        var batch = new V3dBatch
        {
            HeaderReserved0 = [.. Enumerable.Range(1, 32).Select(i => (byte)i)],
            TextureIndex = 0,
            HeaderReserved1 = [.. Enumerable.Range(100, 20).Select(i => (byte)i)],
            Positions = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)],
            Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            TexCoords = [new(0, 0), new(1, 0), new(0, 1)],
            Triangles = [new V3dTriangle(0, 1, 2, 0)],
            SamePositionOffsets = [0, 0, 0],
            BoneLinks =
            [
                new V3dBoneLink(255, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF),
                new V3dBoneLink(128, 127, 0, 0, 0, 1, 0xFF, 0xFF),
                new V3dBoneLink(255, 0, 0, 0, 1, 0xFF, 0xFF, 0xFF),
            ],
            MorphMap = [0, 1, 2, 2],
            Sizes = new V3dBatchSizes(48, 8, 10, 32, 32), // stock-style padding of the same-position offsets
            RenderFlags = 0x518C41,
        };
        var lod0 = new V3dLod
        {
            Flags = V3dLod.FlagMorphVerticesMap | V3dLod.FlagCharacter,
            VertexCount = 4,
            Batches = [batch],
            Unknown1 = -1,
            PropPoints = [new V3dPropPoint(FixedString.FromBytes([.. "muzzle_1\0_1"u8, .. new byte[0x44 - 11]]),
                Quaternion.Identity, new Vector3(0.1f, 0.2f, 0.3f), 1)],
            Textures = [new V3dLodTexture(0, "skin.tga")],
        };
        var lod1 = new V3dLod
        {
            Flags = V3dLod.FlagTrianglePlanes,
            VertexCount = 3,
            Batches = [batch with
            {
                MorphMap = [],
                Planes = [new V3dPlane(Vector3.UnitZ, 0)],
                Sizes = V3dBatchSizes.Canonical(3, 1, hasBoneLinks: true),
            }],
            Unknown1 = 0,
            Textures = [new V3dLodTexture(0, "skin.tga"), new V3dLodTexture(1, "eyes.vbm")],
        };
        var submesh = new V3dSubmesh
        {
            Name = Name("body", 24),
            ParentName = Name("None", 24),
            LodDistances = [0f, 10f],
            Offset = new Vector3(0, 0.5f, 0),
            Radius = 2f,
            AabbMin = new Vector3(-1),
            AabbMax = new Vector3(1),
            Lods = [lod0, lod1],
            Materials =
            [
                new V3dMaterial(Name("skin.tga", 32), 0f, 0f, 0f, 0f, Name("", 32), 0x11),
                new V3dMaterial(Name("eyes.vbm", 32), 1f, 0f, 0f, 0.5f, Name("refl.tga", 32), 0x9),
            ],
            Trailers = [new V3dSubmeshTrailer(Name("body", 24), 0f)],
        };
        return new V3dFile
        {
            Header = new V3dHeader(V3dHeader.CharacterSignature, V3dHeader.CurrentVersion, 1, 0, 0, 0, 2, 0, 0, 1),
            Sections =
            [
                submesh,
                new V3dCollisionSphere(Name("head", 24), 1, new Vector3(0, 0.2f, 0), 0.3f, []),
                new V3dBoneSection(bones, []),
                new V3dDumbSection([.. Name("group", 24).Bytes, .. new byte[32]]),
                new V3dUnknownSection(0x58595A57, [1, 2, 3]),
            ],
        };
    }

    [Fact]
    public void SyntheticMeshesRoundTrip()
    {
        var mesh = SampleCharacter();
        byte[] bytes = V3dWriter.Write(mesh);
        var read = V3dReader.Read(bytes, "synthetic.v3c");
        ModelAssert.Equal(mesh, read);
        Assert.Equal(bytes, V3dWriter.Write(read));
        Assert.Equal(V3dKind.Character, read.Kind);
        Assert.Equal(3, read.Bones.Length);
        Assert.Equal("group", read.Sections.OfType<V3dDumbSection>().Single().GroupName);
        Assert.Equal("muzzle_1", read.Submeshes.Single().Lods[0].PropPoints[0].Name.Text);
        Assert.True(read.Submeshes.Single().Lods[0].PropPoints[0].Name.HasTrailingBytes);
    }

    [Fact]
    public void ExtraBytesInKnownSectionsAndAfterTheEndAreKept()
    {
        var mesh = SampleCharacter();
        var sections = mesh.Sections.ToBuilder();
        sections[1] = ((V3dCollisionSphere)sections[1]) with { Extra = [9, 8, 7, 6] };
        sections[2] = ((V3dBoneSection)sections[2]) with { Extra = [5, 5] };
        mesh = mesh with { Sections = sections.ToImmutable(), EndSizeField = 4, TrailingBytes = [1, 2, 3, 4] };
        var read = V3dReader.Read(V3dWriter.Write(mesh), "x.v3c");
        ModelAssert.Equal(mesh, read);
    }

    [Fact]
    public void StreamSlackAndPaddingAreRelativeToTheDataBlock()
    {
        var mesh = SampleCharacter();
        byte[] bytes = V3dWriter.Write(mesh);
        // The LOD 0 data block starts after the header, section header, names, version, LOD count,
        // 2 distances, offset/radius/box and the LOD's own 14-byte prefix.
        int blockStart = 40 + 8 + 48 + 8 + 8 + 40 + 14;
        int dataSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(blockStart - 4));
        // 1 header (0x38 -> 0x40) + positions 48 + normals 48 + uvs 32 + tris 8 (->16) + same 10 (->16)
        // + links 32 + morph map 8 (->16), then one 0x64 prop point.
        Assert.Equal(0x40 + 48 + 48 + 32 + 16 + 16 + 32 + 16 + 0x64, dataSize);
    }

    [Fact]
    public void TheWriterRejectsInconsistentBatches()
    {
        var mesh = SampleCharacter();
        var sub = mesh.Submeshes.Single();
        var badBatch = sub.Lods[0].Batches[0] with { Normals = [Vector3.UnitZ] };
        var badLod = sub.Lods[0] with { Batches = [badBatch] };
        var bad = mesh with { Sections = mesh.Sections.SetItem(0, sub with { Lods = [badLod, sub.Lods[1]] }) };
        Assert.Throws<ArgumentException>(() => V3dWriter.Write(bad));

        var tooSmall = sub.Lods[0].Batches[0] with { Sizes = new V3dBatchSizes(12, 8, 6, 24, 24) };
        bad = mesh with { Sections = mesh.Sections.SetItem(0, sub with { Lods = [sub.Lods[0] with { Batches = [tooSmall] }, sub.Lods[1]] }) };
        Assert.Throws<ArgumentException>(() => V3dWriter.Write(bad));

        bad = mesh with { Sections = mesh.Sections.SetItem(0, sub with { LodDistances = [0f] }) };
        Assert.Throws<ArgumentException>(() => V3dWriter.Write(bad));
    }

    [Fact]
    public void FixedStringsKeepEveryByteAndValidateText()
    {
        var name = FixedString.FromBytes([.. "abc\0xyz"u8, 0]);
        Assert.Equal("abc", name.Text);
        Assert.True(name.HasTrailingBytes);
        Assert.Equal(8, name.Length);
        var clean = name.WithText("hello");
        Assert.False(clean.HasTrailingBytes);
        Assert.Equal("hello", clean.Text);
        Assert.Throws<ArgumentException>(() => FixedString.FromText(new string('x', 24), 24));
        Assert.Equal(FixedString.FromText("a", 4), FixedString.FromText("a", 4));
    }

    [Fact]
    public void ProbeReportsStructureAndBones()
    {
        byte[] bytes = V3dWriter.Write(SampleCharacter());
        var probe = V3dProbe.Probe(bytes, "x.v3c");
        Assert.True(probe.StructureReadable);
        Assert.Equal(V3dKind.Character, probe.Kind);
        Assert.Equal(["body"], probe.SubmeshNames.ToArray());
        Assert.Equal([2], probe.LodCounts.ToArray());
        Assert.Equal(["pelvis", "spine", "root"], probe.BoneNames.ToArray());
        Assert.Equal([2, 0, -1], probe.BoneParents.ToArray());
        Assert.Equal(1, probe.CollisionSphereCount);
        // triangles of each submesh's first LOD, from the batch headers alone, as the full reader counts them
        var full = V3dReader.Read(bytes, "x.v3c");
        Assert.Equal(full.Sections.OfType<V3dSubmesh>().Select(s => s.Lods[0].Batches.Sum(b => b.TriangleCount)), probe.TriangleCounts);
        Assert.True(probe.TriangleCounts[0] > 0);
    }

    [Fact]
    public void ProbeFallsBackToScanningForTheBoneSection()
    {
        byte[] bytes = V3dWriter.Write(SampleCharacter());
        // Break the first LOD's data size so the section walk fails; the bones are still found.
        int sizeAt = 40 + 8 + 48 + 8 + 8 + 40 + 10;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(sizeAt), 0x7FFFFFF0);
        var probe = V3dProbe.Probe(bytes, "x.v3c");
        Assert.False(probe.StructureReadable);
        Assert.Equal(["pelvis", "spine", "root"], probe.BoneNames.ToArray());
        Assert.Throws<AssetFormatException>(() => V3dReader.Read(bytes, "x.v3c"));
    }

    // ── Malformed input ──────────────────────────────────────────────────────

    public static TheoryData<string, Func<byte[], byte[]>> Corruptions => new()
    {
        { "empty", _ => [] },
        { "short header", b => b[..20] },
        { "bad signature", b => Patch(b, 0, 0x11111111) },
        { "bad version", b => Patch(b, 4, 0x30000) },
        { "no end section", b => b[..(b.Length - 8 - 3 - 8)] },
        { "huge LOD count", b => Patch(b, 40 + 8 + 48 + 4, 0x40000000) },
        { "negative LOD count", b => Patch(b, 40 + 8 + 48 + 4, -2) },
        { "huge data block", b => Patch(b, 40 + 8 + 48 + 8 + 8 + 40 + 10, int.MaxValue) },
        { "truncated", b => b[..(b.Length / 2)] },
    };

    [Theory]
    [MemberData(nameof(Corruptions))]
    public void MalformedMeshesAreRejectedWithOneExceptionType(string what, Func<byte[], byte[]> corrupt)
    {
        byte[] bytes = corrupt(V3dWriter.Write(SampleCharacter()));
        var ex = Assert.Throws<AssetFormatException>(() => V3dReader.Read(bytes, "bad.v3c"));
        Assert.Contains("bad.v3c", ex.Message);
        Assert.False(string.IsNullOrWhiteSpace(what));
    }

    [Fact]
    public void FuzzedMeshesNeverThrowAnythingElse()
    {
        byte[] good = V3dWriter.Write(SampleCharacter());
        var rng = new Random(4321);
        for (int i = 0; i < 2000; i++)
        {
            byte[] b = (byte[])good.Clone();
            int flips = rng.Next(1, 6);
            for (int f = 0; f < flips; f++) b[rng.Next(b.Length)] = (byte)rng.Next(256);
            if (rng.Next(4) == 0) b = b[..rng.Next(b.Length)];
            try
            {
                V3dReader.Read(b, "fuzz.v3c");
            }
            catch (AssetFormatException)
            {
            }
            try
            {
                V3dProbe.Probe(b, "fuzz.v3c");
            }
            catch (AssetFormatException)
            {
            }
        }
    }

    private static byte[] Patch(byte[] b, int offset, int value)
    {
        var copy = (byte[])b.Clone();
        if (offset + 4 <= copy.Length) BinaryPrimitives.WriteInt32LittleEndian(copy.AsSpan(offset), value);
        return copy;
    }
}
