using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cairn.Vfx.Animation;
using Cairn.Vfx.Editing;
using Cairn.Vfx.Formats;
using Cairn.Workspace;
using Xunit.Abstractions;

namespace Cairn.Vfx.Tests;

/// <summary><see cref="VfxUpgrade.ToCurrent"/> against REDUX's .vfx re-save, and against what the older file plays.</summary>
public sealed class VfxUpgradeReduxTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("cairn-vfx-upgrade-").FullName;

    public void Dispose() { try { Directory.Delete(_temp, true); } catch (IOException) { } }

    /// <summary>Documented engine-correct deviations from REDUX's upgrade (file -> reason).</summary>
    internal static readonly Dictionary<string, string> Deviations = new(StringComparer.OrdinalIgnoreCase)
    {
        // Meshes start at legacy frame 8 with a varying per-frame opacity ramp. Mesh opacity was read at the mesh's
        // local frame, a material track is read at effect time with no start offset, so Cairn prepends 8 copies of the
        // first value (40 -> 48 keys); REDUX copies the ramp as is, which plays it 8 frames early.
        ["spiketribeam.vfx"] = "opacity ramp aligned to the mesh start frame",
    };

    private static IEnumerable<(string Path, VfxFile File)> Older()
    {
        if (LocalPaths.Corpus is not { } dir || !Directory.Exists(dir)) yield break;
        foreach (var path in Directory.GetFiles(dir, "*.vfx").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var vfx = VfxReader.Read(File.ReadAllBytes(path), Path.GetFileName(path));
            if (vfx.Version != VfxVersion.Current) yield return (path, vfx);
        }
    }

    [Fact]
    public void Upgrade_matches_REDUX_resave()
    {
        if (ReduxCli.Exe is null || LocalPaths.Corpus is null) return;
        var failures = new List<string>();
        int n = 0, same = 0;
        foreach (var (path, vfx) in Older())
        {
            n++;
            string name = Path.GetFileName(path);
            byte[] reference = File.ReadAllBytes(ReduxCli.Convert(path, "vfx", Path.Combine(_temp, "r" + n)));
            byte[] ours = VfxWriter.Write(VfxUpgrade.ToCurrent(vfx));
            if (ours.AsSpan().SequenceEqual(reference)) { same++; continue; }
            string diff = Diff(VfxReader.Read(reference, name), VfxReader.Read(ours, name));
            if (Deviations.TryGetValue(name, out var why)) output.WriteLine($"{name}: allowed ({why}): {diff}");
            else failures.Add($"{name} (0x{vfx.Version:X}): {diff}");
        }
        output.WriteLine($"{same}/{n} byte-identical to REDUX; {Deviations.Count} documented deviations");
        foreach (var f in failures) output.WriteLine(f);
        Assert.Empty(failures);
    }

    /// <summary>Upgrading must not change what plays: mesh positions, visibility and effective opacity per frame.</summary>
    [Fact]
    public void Upgrade_preserves_playback()
    {
        var failures = new List<string>();
        int n = 0;
        foreach (var (path, vfx) in Older())
        {
            n++;
            var up = VfxReader.Read(VfxWriter.Write(VfxUpgrade.ToCurrent(vfx)), Path.GetFileName(path));
            var a = new VfxSampler(vfx);
            var b = new VfxSampler(up);
            string? fail = null;
            int end = Math.Max(vfx.EndFrame, up.EndFrame) + 2;
            for (int m = 0; m < a.Meshes.Count && fail is null; m++)
            {
                VfxMeshSample sa = new(), sb = new();
                // Before 0x3000E a keyframed mesh also stores a frame-0 transform; the sampler applies it to the
                // legacy file the same way the upgrade folds it into the pivot, so both poses compare directly.
                for (float f = 0; f <= end && fail is null; f += 0.5f)
                {
                    bool aa = a.SampleMesh(m, f, sa), ab = b.SampleMesh(m, f, sb);
                    if (aa != ab) { fail = $"mesh {m} frame {f}: visible {aa} vs {ab}"; break; }
                    if (!aa) continue;
                    for (int i = 0; i < sa.Positions.Length; i++)
                    {
                        var pa = sa.Positions[i];
                        if (Vector3.Distance(pa, sb.Positions[i]) > 1e-3f * (1 + pa.Length()))
                        { fail = $"mesh {m} frame {f}: vertex {i} {pa} vs {sb.Positions[i]}"; break; }
                    }
                    if (fail is not null) break;
                    var slotsA = a.Meshes[m].MaterialSlots; var slotsB = b.Meshes[m].MaterialSlots;
                    for (int s = 0; s < slotsA.Length; s++)
                    {
                        float oa = Opacity(a, slotsA[s], f, sa), ob = Opacity(b, slotsB[s], f, sb);
                        if (MathF.Abs(oa - ob) > 1e-3f) { fail = $"mesh {m} slot {s} frame {f}: opacity {oa} vs {ob}"; break; }
                    }
                }
            }
            if (fail is not null) failures.Add($"{Path.GetFileName(path)} (0x{vfx.Version:X}): {fail}");
        }
        output.WriteLine($"{n - failures.Count}/{n} older files play the same after upgrade");
        foreach (var f in failures) output.WriteLine(f);
        Assert.Empty(failures);
    }

    /// <summary>Effective mesh opacity: per-frame mesh opacity where the version stores it, else the material track.</summary>
    private static float Opacity(VfxSampler s, int material, float frame, VfxMeshSample sample) =>
        sample.Opacity ?? (material < 0 ? 1f : s.SampleMaterial(material, frame).Opacity);

    private static readonly JsonSerializerOptions Json = new()
    {
        IncludeFields = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        IgnoreReadOnlyProperties = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>First differing field per differing section (header first), compact.</summary>
    internal static string Diff(VfxFile expected, VfxFile actual)
    {
        var parts = new List<string>();
        if (expected.EndFrame != actual.EndFrame) parts.Add($"EndFrame {expected.EndFrame} vs {actual.EndFrame}");
        if (expected.Sections.Length != actual.Sections.Length) parts.Add($"sections {expected.Sections.Length} vs {actual.Sections.Length}");
        for (int i = 0; i < Math.Min(expected.Sections.Length, actual.Sections.Length) && parts.Count < 6; i++)
        {
            var e = JsonSerializer.SerializeToNode(expected.Sections[i], expected.Sections[i].GetType(), Json);
            var a = JsonSerializer.SerializeToNode(actual.Sections[i], actual.Sections[i].GetType(), Json);
            if (First(e, a, "") is { } d) parts.Add($"[{i} {expected.Sections[i].GetType().Name[3..]}] {d}");
        }
        return parts.Count == 0 ? "same model, different bytes" : string.Join("; ", parts);
    }

    private static string? First(JsonNode? e, JsonNode? a, string path)
    {
        if (e is JsonObject eo && a is JsonObject ao)
        {
            foreach (var k in eo.Select(p => p.Key).Union(ao.Select(p => p.Key)))
                if (First(eo[k], ao[k], path + "." + k) is { } d) return d;
            return null;
        }
        if (e is JsonArray ea && a is JsonArray aa)
        {
            if (ea.Count != aa.Count) return $"{path} len {ea.Count} vs {aa.Count}" + (ea.Count < 8 ? $" ({Short(e)} vs {Short(a)})" : "");
            for (int i = 0; i < ea.Count; i++)
                if (First(ea[i], aa[i], $"{path}[{i}]") is { } d) return d;
            return null;
        }
        return JsonNode.DeepEquals(e, a) ? null : $"{path} {Short(e)} vs {Short(a)}";
    }

    private static string Short(JsonNode? n) => n?.ToJsonString() is { } s ? (s.Length > 80 ? s[..80] + "..." : s) : "null";
}
