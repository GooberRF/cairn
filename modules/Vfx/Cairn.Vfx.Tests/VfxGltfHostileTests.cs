using Cairn.Formats;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Interchange;

namespace Cairn.Vfx.Tests;

/// <summary>
/// Hostile glTF input (review-findings-2 #1, #2, #5, #6): every import finishes within a hard timeout and fails, if at
/// all, with <see cref="AssetFormatException"/>; never another exception type, a hang or a silently corrupted file.
/// </summary>
public sealed class VfxGltfHostileTests
{
    // One triangle; positions accessor 0, indices accessor 1, optional animation input accessor 2 / output 3.
    private static string Gltf(float[] pos, ushort[] idx, string nodes, float animEnd = -1, int idxCount = -1)
    {
        var bytes = new List<byte>();
        foreach (var f in pos) bytes.AddRange(BitConverter.GetBytes(f));
        int idxOff = bytes.Count;
        foreach (var i in idx) bytes.AddRange(BitConverter.GetBytes(i));
        while (bytes.Count % 4 != 0) bytes.Add(0);
        int animOff = bytes.Count;
        bytes.AddRange(BitConverter.GetBytes(0f)); bytes.AddRange(BitConverter.GetBytes(animEnd));
        int outOff = bytes.Count;
        for (int k = 0; k < 6; k++) bytes.AddRange(BitConverter.GetBytes(0f));
        string b64 = Convert.ToBase64String(bytes.ToArray());
        string end = float.IsFinite(animEnd) ? animEnd.ToString(System.Globalization.CultureInfo.InvariantCulture) : "1";
        string anim = animEnd < 0 ? "" : $@",""animations"":[{{""channels"":[{{""sampler"":0,""target"":{{""node"":0,""path"":""translation""}}}}],""samplers"":[{{""input"":2,""output"":3}}]}}]";
        return $@"{{""asset"":{{""version"":""2.0""}},""scene"":0,""scenes"":[{{""nodes"":[0]}}],""nodes"":{nodes},
""meshes"":[{{""primitives"":[{{""attributes"":{{""POSITION"":0}},""indices"":1}}]}}],
""buffers"":[{{""byteLength"":{bytes.Count},""uri"":""data:application/octet-stream;base64,{b64}""}}],
""bufferViews"":[{{""buffer"":0,""byteOffset"":0,""byteLength"":{idxOff}}},{{""buffer"":0,""byteOffset"":{idxOff},""byteLength"":{idx.Length * 2}}},{{""buffer"":0,""byteOffset"":{animOff},""byteLength"":8}},{{""buffer"":0,""byteOffset"":{outOff},""byteLength"":24}}],
""accessors"":[{{""bufferView"":0,""componentType"":5126,""count"":{pos.Length / 3},""type"":""VEC3"",""min"":[0,0,0],""max"":[1,1,1]}},
{{""bufferView"":1,""componentType"":5123,""count"":{(idxCount < 0 ? idx.Length : idxCount)},""type"":""SCALAR""}},
{{""bufferView"":2,""componentType"":5126,""count"":2,""type"":""SCALAR"",""min"":[0],""max"":[{end}]}},
{{""bufferView"":3,""componentType"":5126,""count"":2,""type"":""VEC3""}}]{anim}}}";
    }

    private static readonly float[] Tri = [0, 0, 0, 1, 0, 0, 0, 1, 0];
    private const string OneNode = @"[{""mesh"":0,""name"":""Tri""}]";

    /// <summary>"ok: ..." on success, "AssetFormatException: ..." on a clean rejection; anything else fails the test.</summary>
    private static string Run(string json, int ms = 60000)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cairn_hostile_{Guid.NewGuid():N}.gltf");
        File.WriteAllText(path, json);
        using var cts = new CancellationTokenSource();
        // A dedicated thread: under parallel test load a pool task could wait seconds to start, and the bound
        // below is a hang detector (the deterministic check is the result kind), so it is generous.
        var t = Task.Factory.StartNew(() =>
        {
            try
            {
                var r = VfxGltfImport.Import(path, null, cts.Token);
                VfxWriter.Write(r.File);
                var m = r.File.Sections.OfType<VfxMesh>().First();
                return $"ok: frames={m.Frames.Length} faces={m.Faces.Length} | {string.Join(" | ", r.Messages)}";
            }
            catch (AssetFormatException e) { return "AssetFormatException: " + e.Message; }
            catch (Exception e) { return "BAD " + e.GetType().Name + ": " + e.Message; }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        bool done = t.Wait(ms);
        cts.Cancel(); // abandons a runaway import instead of leaving it spinning
        try { File.Delete(path); } catch (IOException) { }
        Assert.True(done, $"HANG (> {ms} ms)");
        Assert.False(t.Result.StartsWith("BAD", StringComparison.Ordinal), t.Result);
        return t.Result;
    }

    [Fact]
    public void Baseline_Imports() => Assert.StartsWith("ok", Run(Gltf(Tri, [0, 1, 2], OneNode)));

    [Fact]
    public void CyclicHierarchy_IsRejected()
    {
        var r = Run(Gltf(Tri, [0, 1, 2], @"[{""mesh"":0,""name"":""Tri""},{""name"":""B"",""children"":[0,2]},{""name"":""C"",""children"":[1]}]"));
        Assert.StartsWith("AssetFormatException", r);
        Assert.Contains("cyclic", r);
    }

    [Fact]
    public void HugeAnimationEnd_IsClippedWithMessage()
    {
        var r = Run(Gltf(Tri, [0, 1, 2], OneNode, animEnd: 2_000_000f), 90000);
        Assert.StartsWith($"ok: frames={VfxGltfImport.MaxSampledFrames} ", r);
        Assert.Contains("clipped", r);
    }

    [Fact]
    public void InfiniteAnimationEnd_IsRejected() =>
        Assert.StartsWith("AssetFormatException", Run(Gltf(Tri, [0, 1, 2], OneNode, animEnd: float.PositiveInfinity)));

    [Fact]
    public void IndexOutOfRange_IsAssetFormatException() =>
        Assert.StartsWith("AssetFormatException", Run(Gltf(Tri, [0, 1, 7], OneNode)));

    [Fact]
    public void IndexCountBeyondView_IsAssetFormatException() =>
        Assert.StartsWith("AssetFormatException", Run(Gltf(Tri, [0, 1, 2], OneNode, idxCount: 3000000)));

    [Fact]
    public void NaNPosition_IsRejected() =>
        Assert.StartsWith("AssetFormatException", Run(Gltf([float.NaN, 0, 0, 1, 0, 0, 0, 1, 0], [0, 1, 2], OneNode)));

    [Fact]
    public void Cancellation_AbandonsImport()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cairn_hostile_{Guid.NewGuid():N}.gltf");
        File.WriteAllText(path, Gltf(Tri, [0, 1, 2], OneNode, animEnd: 2_000_000f));
        try { Assert.ThrowsAny<OperationCanceledException>(() => VfxGltfImport.Import(path, null, new CancellationToken(true))); }
        finally { File.Delete(path); }
    }
}
