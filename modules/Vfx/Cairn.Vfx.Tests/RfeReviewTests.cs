using Cairn.Formats;
using Cairn.Rfa.Formats.Legacy;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Rfe;
using Cairn.Workspace;
using Xunit.Abstractions;

namespace Cairn.Vfx.Tests;

/// <summary>
/// Regressions from the review of the Red Faction II effect support: over-long durations, camera-facing quads that lie in
/// their XZ plane, and the per-group texture index.
/// </summary>
public sealed class RfeReviewTests(ITestOutputHelper output)
{
    /// <summary>The synthetic sample with its header duration replaced.</summary>
    private static byte[] WithDuration(float seconds)
    {
        var bytes = SyntheticRfe.Write(SyntheticRfe.Sample());
        BitConverter.GetBytes(seconds).CopyTo(bytes, 12);
        return bytes;
    }

    [Theory]
    [InlineData(100_000f)]
    [InlineData(1e9f)]
    [InlineData(130_000f)]
    [InlineData(3600f)]
    [InlineData(600.5f)]
    [InlineData(float.MaxValue)]
    public void ADurationAboveTheCapIsRefused(float seconds)
    {
        var ex = Assert.Throws<AssetFormatException>(() => RfeReader.Read(WithDuration(seconds), "long.rfe"));
        Assert.Contains("duration", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLongestDurationAllowedConvertsAndReadsBack()
    {
        Assert.Equal(600, RfeReader.MaxDuration);
        var effect = RfeReader.Read(WithDuration(RfeReader.MaxDuration), "long.rfe");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var conversion = RfeToVfx.Convert(effect, "long.rfe");
        var back = VfxReader.Read(conversion.Bytes, conversion.OutputName);
        output.WriteLine($"{RfeReader.MaxDuration} s: converted and read back in {watch.ElapsedMilliseconds} ms, end frame {back.EndFrame}");
        Assert.Equal(9_000, back.EndFrame);
    }

    [Fact]
    public void AnOverLongEffectBuiltInMemoryIsRefusedRatherThanOverflowing()
    {
        foreach (float seconds in new[] { 1e9f, 1e5f, float.MaxValue })
        {
            var effect = SyntheticRfe.Sample() with { Duration = seconds };
            var ex = Record.Exception(() => RfeToVfx.Convert(effect, "long.rfe"));
            Assert.True(ex is AssetFormatException, $"{seconds} s: {ex?.GetType().Name ?? "no exception"}: {ex?.Message}");
        }
    }

    /// <summary>A square of <paramref name="size"/> metres lying in the XZ plane (its local Y extent is 0).</summary>
    private static Rf2StaticBody XzQuad(string texture, float size)
    {
        var quad = SyntheticRfe.Quad(texture, size);
        var g = quad.Geometries[0];
        var flat = g with { Positions = [.. g.Positions.Select(p => new Vector3(p.X, 0, p.Y))], Normals = [.. g.Normals.Select(_ => Vector3.UnitY)] };
        return quad with { Geometries = [flat], BoxMin = new(-size / 2, 0, -size / 2), BoxMax = new(size / 2, 0, size / 2) };
    }

    /// <summary>The sample with its glow (mesh 2) given <paramref name="facing"/> and an XZ-plane quad.</summary>
    private static RfeFile FlatGlow(uint facing)
    {
        var sample = SyntheticRfe.Sample();
        return sample with { Meshes = [sample.Meshes[0], sample.Meshes[1] with { Facing = facing, Body = XzQuad("glow.tga", 0.6f) }] };
    }

    private static bool IsFacing(VfxMesh mesh) => (mesh.Flags & (VfxMeshFlags.Facing | VfxMeshFlags.FacingRod)) != 0;

    [Fact]
    public void AnAxisFacingQuadLyingAcrossItsAxisConvertsToAPlainMeshThatShows()
    {
        // facing 2 turns the quad about its own Y axis, so a quad across that axis only spins in its own plane: the game
        // shows it flat; a facing rod of height 0 would show nothing
        var conversion = RfeToVfx.Convert(FlatGlow(2), "flat.rfe");
        var mesh = conversion.File.Sections.OfType<VfxMesh>().ElementAt(1);
        Assert.False(IsFacing(mesh), $"flags 0x{mesh.Flags:X}, size {mesh.Frames[0].FacingSize}");
        // Red Faction II draws effect meshes from both sides; Red Faction only the front: the back faces are added
        Assert.Equal(4, mesh.Faces.Length);
        Assert.Equal((mesh.Faces[0].V0, mesh.Faces[0].V2, mesh.Faces[0].V1), (mesh.Faces[2].V0, mesh.Faces[2].V1, mesh.Faces[2].V2));
        Assert.Contains(conversion.Notes, n => n.Kind == RfeNoteKind.Approximated && n.Text.Contains("across its axis", StringComparison.Ordinal));
    }

    [Fact]
    public void ACameraFacingQuadInItsXzPlaneIsReportedAsEdgeOn()
    {
        // facing 1 turns the mesh's local Z to the camera, so a quad in its XZ plane is edge-on in the game too
        var conversion = RfeToVfx.Convert(FlatGlow(1), "flat.rfe");
        var mesh = conversion.File.Sections.OfType<VfxMesh>().ElementAt(1);
        Assert.True(IsFacing(mesh));
        Assert.Contains(conversion.Notes, n => n.Kind != RfeNoteKind.Exact && n.Text.Contains("edge-on", StringComparison.Ordinal));
        // an upright quad is not reported
        Assert.DoesNotContain(RfeToVfx.Convert(SyntheticRfe.Sample(), "sample.rfe").Notes, n => n.Text.Contains("edge-on", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryStockFacingMeshConvertsWithASizeOrIsReportedEdgeOn()
    {
        if (LocalPaths.Rf2GameDirectory is not { } game || !Directory.Exists(Path.Combine(game, "pc_media"))) { output.WriteLine("No Red Faction II install."); return; }
        int facing = 0, plain = 0, edgeOn = 0;
        foreach (string toc in Directory.EnumerateFiles(Path.Combine(game, "pc_media"), "*.toc_group").Order(StringComparer.OrdinalIgnoreCase))
        {
            var group = new Rf2GroupFiles(toc);
            foreach (string name in group.Names.Where(RfeReader.HasExtension))
            {
                var bytes = group.Read(name, int.MaxValue)!;
                if (RfeReader.Identify(bytes) != RfeLayout.Pc) continue;
                var effect = RfeReader.Read(bytes, name);
                var conversion = RfeToVfx.Convert(effect, name);
                var meshes = conversion.File.Sections.OfType<VfxMesh>().ToList();
                bool reported = conversion.Notes.Any(n => n.Text.Contains("edge-on", StringComparison.Ordinal));
                for (int m = 0; m < effect.Meshes.Length; m++)
                {
                    if (effect.Meshes[m].FacingMode == RfeFacing.None) continue;
                    facing++;
                    var mesh = meshes[m];
                    if (!IsFacing(mesh)) { plain++; continue; }
                    var size = mesh.Frames[0].FacingSize ?? Vector2.Zero;
                    if (size.X > 1e-4f && size.Y > 1e-4f) continue;
                    edgeOn++;
                    Assert.True(reported && effect.Meshes[m].FacingMode == RfeFacing.Billboard, $"{name} mesh {m} (facing {effect.Meshes[m].Facing}) converts to a facing size of {size} and the report does not say why");
                }
            }
        }
        output.WriteLine($"{facing} facing meshes: {plain} became plain meshes, {edgeOn} edge-on (reported)");
        // the stock files hold four distinct facing quads in their XZ plane (two of each mode), some in several groups
        Assert.True(plain >= 2 && edgeOn >= 2, $"{plain} plain, {edgeOn} edge-on");
    }

    [Fact]
    public void FacingObjectsTurnTheirLocalZToTheCameraAsTheGameDoes()
    {
        var tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.5f);
        var at = new Vector3(0, 1.5f, 0);
        var eye = new Vector3(3, 2, 4);
        var toEye = Vector3.Normalize(eye - at);
        var axis = Vector3.Transform(Vector3.UnitY, tilt);
        static Vector3 Row(Matrix4x4 m, int i) => i switch { 0 => new(m.M11, m.M12, m.M13), 1 => new(m.M21, m.M22, m.M23), _ => new(m.M31, m.M32, m.M33) };
        // facing 1: +Z at the camera, X across the keyed Y (so Y as near it as can be), a rotation (no mirroring)
        var billboard = RfeSampler.FacingRotation(RfeFacing.Billboard, tilt, at, eye);
        Assert.Equal(1, Vector3.Dot(Row(billboard, 2), toEye), 5);
        Assert.Equal(0, Vector3.Dot(Row(billboard, 0), axis), 5);
        Assert.True(Vector3.Dot(Row(billboard, 1), axis) > 0);
        Assert.Equal(1, billboard.GetDeterminant(), 4);
        // facing 2: Y stays the keyed axis, +Z turns as near the camera as it can
        var rod = RfeSampler.FacingRotation(RfeFacing.Axial, tilt, at, eye);
        var across = Vector3.Normalize(toEye - axis * Vector3.Dot(toEye, axis));
        Assert.Equal(1, Vector3.Dot(Row(rod, 1), axis), 5);
        Assert.Equal(1, Vector3.Dot(Row(rod, 2), across), 5);
        Assert.Equal(1, rod.GetDeterminant(), 4);
        // looking down the axis, or with no facing, the keyed rotation stays
        Assert.Equal(Matrix4x4.CreateFromQuaternion(tilt), RfeSampler.FacingRotation(RfeFacing.Axial, tilt, at, at + axis * 5));
        Assert.Equal(Matrix4x4.CreateFromQuaternion(tilt), RfeSampler.FacingRotation(RfeFacing.None, tilt, at, eye));
        // the XZ-plane quads: edge-on for facing 1, across the axis for facing 2; the sample's upright quads show
        Assert.Equal(RfeFacingLie.EdgeOn, RfeSampler.Lie(FlatGlow(1).Meshes[1]));
        Assert.Equal(RfeFacingLie.AcrossAxis, RfeSampler.Lie(FlatGlow(2).Meshes[1]));
        Assert.Equal(RfeFacingLie.Upright, RfeSampler.Lie(SyntheticRfe.Sample().Meshes[1]));
    }

    [Fact]
    public void TheGroupTextureIndexBelongsToItsSourceNotToItsLabel()
    {
        // two folders of the same name whose packs have the same names but hold different textures
        byte[] red = SyntheticRf2Mesh.SamplePeg(("one.tga", 4, 4, 0xFFFF0000));
        byte[] green = SyntheticRf2Mesh.SamplePeg(("two.tga", 4, 4, 0xFF00FF00));
        string[] names = ["fx.rfe", "p1.peg", "p2.peg"];
        static Func<string, int, byte[]?> Reader(Dictionary<string, byte[]> files) =>
            (n, c) => files.TryGetValue(n, out var b) ? (c >= b.Length ? b : b[..c]) : null;
        string label = "effects-" + Guid.NewGuid().ToString("N")[..8];
        var first = RfeTextures.PacksFor("fx.rfe", ["two.tga"], names, Reader(new() { ["p1.peg"] = red, ["p2.peg"] = green }), label, null);
        Assert.True(first.Contains("two.tga"));
        var second = RfeTextures.PacksFor("fx.rfe", ["two.tga"], names, Reader(new() { ["p1.peg"] = green, ["p2.peg"] = red }), label, null);
        Assert.True(second.Contains("two.tga"), $"the second folder's two.tga is in its p1.peg; packs searched: {string.Join(", ", second.PackNames)}");
        // kept per full path: two folders of the same name each keep their own index
        string a = Path.Combine(Path.GetTempPath(), label, "a", "effects"), b = Path.Combine(Path.GetTempPath(), label, "b", "effects");
        Assert.True(RfeTextures.PacksFor("fx.rfe", ["two.tga"], names, Reader(new() { ["p1.peg"] = red, ["p2.peg"] = green }), "effects", null, a).Contains("two.tga"));
        Assert.True(RfeTextures.PacksFor("fx.rfe", ["two.tga"], names, Reader(new() { ["p1.peg"] = green, ["p2.peg"] = red }), "effects", null, b).Contains("two.tga"));
        Assert.True(RfeTextures.PacksFor("fx.rfe", ["two.tga"], names, Reader(new() { ["p1.peg"] = red, ["p2.peg"] = green }), "effects", null, a).Contains("two.tga"));
    }
}
