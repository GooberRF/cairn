using System.Buffers.Binary;
using System.Collections;
using Cairn.Formats;
using Cairn.Vfx.Animation;
using Cairn.Vfx.Formats;
using Cairn.Workspace;
using Xunit.Abstractions;

namespace Cairn.Vfx.Tests;

public sealed class VfxFormatTests(ITestOutputHelper output)
{
    private const int StockFileCount = 61;

    /// <summary>The stock files, or null when the research corpus is not present on this machine.</summary>
    private static string[]? StockFiles()
    {
        if (LocalPaths.Corpus is not { } dir || !Directory.Exists(dir)) return null;
        var files = Directory.GetFiles(dir, "*.vfx").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.Equal(StockFileCount, files.Length);
        return files;
    }

    [Fact]
    public void StockFilesRoundTripByteIdentically()
    {
        if (StockFiles() is not { } files) return;
        var failures = new List<string>();
        var versions = new SortedDictionary<int, int>();
        foreach (var path in files)
        {
            byte[] data = File.ReadAllBytes(path);
            var vfx = VfxReader.Read(data, Path.GetFileName(path));
            versions[vfx.Version] = versions.GetValueOrDefault(vfx.Version) + 1;
            byte[] again = VfxWriter.Write(vfx);
            int diff = FirstDifference(data, again);
            if (diff >= 0) failures.Add($"{Path.GetFileName(path)}: first difference at offset {diff} (lengths {data.Length}/{again.Length})");
        }
        output.WriteLine($"{files.Length} files: " + string.Join(", ", versions.Select(kv => $"0x{kv.Key:X}x{kv.Value}")));
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void StoredHeaderCountsEqualRecomputedOnes()
    {
        if (StockFiles() is not { } files) return;
        int sections = 0;
        foreach (var path in files)
        {
            var vfx = VfxReader.Read(File.ReadAllBytes(path), Path.GetFileName(path));
            Assert.True(vfx.Notes.IsEmpty, $"{Path.GetFileName(path)}: {string.Join("; ", vfx.Notes)}");
            Assert.Equal(vfx.StoredCounts, VfxHeaderCounts.Compute(vfx));
            Assert.DoesNotContain(vfx.Sections, s => s is VfxOpaqueSection);
            var probe = VfxProbe.Read(File.ReadAllBytes(path), Path.GetFileName(path));
            Assert.Equal(vfx.StoredCounts, probe.Counts);
            sections += vfx.Sections.Length;
        }
        output.WriteLine($"{files.Length} files, {sections} sections, all header counts match");
    }

    [Fact]
    public void MutatedStockFilesOnlyThrowAssetFormatException()
    {
        if (StockFiles() is not { } files) return;
        int scale = int.TryParse(Environment.GetEnvironmentVariable("CAIRN_FUZZ_SCALE"), out int s) && s > 0 ? s : 1;
        var rng = new Random(20261006);
        int reads = 0, rejected = 0;
        foreach (var path in files)
        {
            byte[] original = File.ReadAllBytes(path);
            for (int i = 0; i < 30 * scale; i++)
            {
                byte[] data;
                switch (i % 3)
                {
                    case 0: data = original.AsSpan(0, rng.Next(original.Length)).ToArray(); break;
                    case 1:
                        data = (byte[])original.Clone();
                        for (int k = rng.Next(1, 5); k > 0; k--) data[rng.Next(8, data.Length)] ^= (byte)rng.Next(1, 256);
                        break;
                    default:
                        data = (byte[])original.Clone();
                        int at = rng.Next(2, data.Length / 4) * 4;
                        int old = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at));
                        int[] big = [int.MaxValue, int.MinValue, -1, 0x10000000, old * 1000 + 1, old + 1];
                        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(at), big[rng.Next(big.Length)]);
                        break;
                }
                long before = GC.GetAllocatedBytesForCurrentThread();
                try
                {
                    var vfx = VfxReader.Read(data, "fuzz.vfx");
                    VfxWriter.Write(vfx);
                }
                catch (AssetFormatException) { rejected++; }
                catch (Exception e) { Assert.Fail($"{Path.GetFileName(path)} mutation {i}: {e.GetType().Name}: {e.Message}"); }
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.True(allocated < 64L * 1024 * 1024 + 64L * data.Length, $"{Path.GetFileName(path)} mutation {i} allocated {allocated} bytes");
                reads++;
            }
        }
        output.WriteLine($"{reads} mutated reads, {rejected} rejected with AssetFormatException");
    }

    [Fact]
    public void RejectsOldVersionsAndFlagsEngineFatalOnes()
    {
        byte[] old = new byte[200];
        "VSFX"u8.CopyTo(old);
        BinaryPrimitives.WriteInt32LittleEndian(old.AsSpan(4), 0x20000);
        Assert.Throws<AssetFormatException>(() => VfxReader.Read(old, "old.vfx"));
        Assert.Throws<AssetFormatException>(() => VfxReader.Read(new byte[3], "short.vfx"));

        var fatal = new VfxFile(0x40002, 0, 0, null, 0, []);
        var back = VfxReader.Read(VfxWriter.Write(fatal), "fatal.vfx");
        Assert.True(back.IsEngineFatalVersion);
        Assert.False(VfxReader.Read(VfxWriter.Write(fatal with { Version = VfxVersion.Current }), "ok.vfx").IsEngineFatalVersion);
    }

    [Fact]
    public void HandBuiltCurrentVersionFileRoundTrips()
    {
        var file = BuildSample();
        byte[] first = VfxWriter.Write(file);
        var read = VfxReader.Read(first, "sample.vfx");
        Assert.Empty(read.Notes);
        Assert.False(read.IsEngineFatalVersion);
        AssertDeepEqual(file with { StoredCounts = read.StoredCounts, Notes = read.Notes }, read, "file");
        Assert.Equal(first, VfxWriter.Write(read));
        Assert.Equal(2, read.StoredCounts!.MeshFrames);
        Assert.Equal(1, read.StoredCounts.UvFrames);
        Assert.Equal(0, read.StoredCounts.MeshTransformFrames);
    }

    [Fact]
    public void WriterRejectsFieldsThatContradictTheVersionGates()
    {
        var file = BuildSample();
        var mesh = (VfxMesh)file.Sections[0];
        var noFps = file with { Sections = file.Sections.SetItem(0, mesh with { Fps = null }) };
        Assert.Contains("Fps", Assert.Throws<InvalidOperationException>(() => VfxWriter.Write(noFps)).Message);
        var extraTrs = mesh.Frames.SetItem(1, mesh.Frames[1] with { Transform = new(Vector3.Zero, Quaternion.Identity, Vector3.One) });
        var bad = file with { Sections = file.Sections.SetItem(0, mesh with { Frames = extraTrs }) };
        Assert.Contains("Transform", Assert.Throws<InvalidOperationException>(() => VfxWriter.Write(bad)).Message);
        Assert.Throws<InvalidOperationException>(() => VfxWriter.Write(file with { LegacyUnk1 = 3 }));
    }

    [Fact]
    public void PositionCodecRoundTripsWithinQuantisationError()
    {
        Assert.Equal(0x364CCE67, BitConverter.SingleToInt32Bits(VfxPositionCodec.FloorMultiplier));
        var rng = new Random(7);
        for (int trial = 0; trial < 50; trial++)
        {
            float extent = trial % 5 == 0 ? 0.01f : (float)(rng.NextDouble() * 200);
            var pts = Enumerable.Range(0, 40).Select(_ => new Vector3(
                (float)(rng.NextDouble() - 0.5) * extent + 10, (float)(rng.NextDouble() - 0.5) * extent - 3, (float)(rng.NextDouble() - 0.5) * extent)).ToArray();
            var enc = VfxPositionCodec.Encode(pts);
            var dec = VfxPositionCodec.Decode(enc);
            Assert.All(enc.Raw, r => Assert.InRange(r, (short)-32767, (short)32767));
            for (int i = 0; i < pts.Length; i++)
            {
                var tol = enc.Multiplier * 0.5001f + new Vector3(1e-5f * (1 + extent + 10));
                var err = Vector3.Abs(dec[i] - pts[i]);
                Assert.True(err.X <= tol.X && err.Y <= tol.Y && err.Z <= tol.Z, $"trial {trial} point {i}: error {err} > {tol}");
            }
        }
        var single = VfxPositionCodec.Encode([new Vector3(1, 2, 3)]);
        Assert.Equal(new Vector3(VfxPositionCodec.FloorMultiplier), single.Multiplier);
        Assert.Equal(4800, VfxTime.TicksPerSecond);
    }

    private static VfxFile BuildSample()
    {
        Vector3[] f0 = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)], f1 = [new(0, 0, 0), new(2, 0, 0), new(0, 2, -0.5f)];
        var p0 = VfxPositionCodec.Encode(f0);
        var white = Vector3.One;
        var face = new VfxFace(0, 1, 2, null, white, white, white, new(0, 0, 1), new(1 / 3f, 1 / 3f, 0), 0.75f, 0, 1, 0, 1, 2);
        var fvs = Enumerable.Range(0, 3).Select(i => new VfxFaceVertex(1, i, 0xCDCDCDCD, 0xCDCDCDCD, [0])).ToImmutableArray();
        var frames = ImmutableArray.Create(
            new VfxMeshFrame(p0, null, null, [new(0, 0), new(1, 0), new(0, 1)], null, null, null),
            new VfxMeshFrame(VfxPositionCodec.Encode(f1), null, null, null, null, null, null));
        var mesh = new VfxMesh("blob", "Scene Root", 0, 3, null, [face], 15, 0f, 1f / 15, null, null, [0], null,
            new(0.5f, 0.5f, 0), 0.8f, null, VfxMeshFlags.Morph, null, fvs, 0, frames, null, null);
        var material = new VfxMaterial(0, 15, 0, new VfxTexture("fx_blob.tga", 0, 1f, 2), null, null, null,
            Vector3.Zero, "", null, [1f], [1f, -0f]);
        var pframe = new VfxParticleFrame(new(0, 1, 0), Quaternion.Identity, 0.5f, 0.5f, 0, 2f, 0.1f, 10f, null);
        var particles = new VfxParticleSystem("sparks", "blob", 0, 0x2, ["wind"], 0, 0, null, 20, 0, 480, 0.25f, 1, null,
            new Vector2(0, 1), null, null, new Vector2(0, 1), null, null, [pframe, pframe with { Speed = 3f }]);
        var dummy = new VfxDummy("tip", "Scene Root", 0, new(0, 2, 0), Quaternion.Identity,
            [new(new(0, 2, 0), Quaternion.Identity), new(new(0, 2.5f, 0), Quaternion.Identity)]);
        return new VfxFile(VfxVersion.Current, 0, 1, null, 0, [mesh, particles, dummy, material]);
    }

    private static int FirstDifference(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++) if (a[i] != b[i]) return i;
        return a.Length == b.Length ? -1 : n;
    }

    private static void AssertDeepEqual(object? a, object? b, string path)
    {
        if (a is null || b is null) { Assert.True(a is null && b is null, $"{path}: {a ?? "null"} vs {b ?? "null"}"); return; }
        Assert.Equal(a.GetType(), b.GetType());
        var t = a.GetType();
        if (t.IsPrimitive || a is string || a is Vector2 || a is Vector3 || a is Quaternion || t.IsEnum) { Assert.True(a.Equals(b), $"{path}: {a} vs {b}"); return; }
        if (a is IEnumerable ea)
        {
            var la = ea.Cast<object?>().ToList(); var lb = ((IEnumerable)b).Cast<object?>().ToList();
            Assert.True(la.Count == lb.Count, $"{path}: length {la.Count} vs {lb.Count}");
            for (int i = 0; i < la.Count; i++) AssertDeepEqual(la[i], lb[i], $"{path}[{i}]");
            return;
        }
        foreach (var p in t.GetProperties().Where(p => p.GetIndexParameters().Length == 0 && p.Name != "EqualityContract"))
            AssertDeepEqual(p.GetValue(a), p.GetValue(b), $"{path}.{p.Name}");
    }
}
