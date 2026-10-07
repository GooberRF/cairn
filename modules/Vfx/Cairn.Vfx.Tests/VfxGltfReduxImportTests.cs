using System.Text.Json.Nodes;
using Cairn.Formats.Gltf;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Interchange;
using Cairn.Workspace;
using Xunit.Abstractions;

namespace Cairn.Vfx.Tests;

/// <summary>Import of glTF files that carry only REDUX's <c>rf_*</c> extras.</summary>
public sealed class VfxGltfReduxImportTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("cairn-vfx-redux-").FullName;

    public void Dispose() { try { Directory.Delete(_temp, true); } catch (IOException) { } }

    private static string[] StockFiles() =>
        LocalPaths.Corpus is { } dir && Directory.Exists(dir)
            ? [.. Directory.GetFiles(dir, "*.vfx").OrderBy(f => f, StringComparer.OrdinalIgnoreCase)]
            : [];

    /// <summary>
    /// For every stock file: REDUX .vfx -> .gltf, then Cairn's import of that glTF must write the same bytes as
    /// REDUX's own .gltf -> .vfx, and (for current-version files) the original bytes.
    /// </summary>
    [Fact]
    public void ReduxGltf_ImportsLikeRedux()
    {
        if (ReduxCli.Exe is null) return;
        var files = StockFiles();
        if (files.Length == 0) return;
        var failures = new List<string>();
        int current = 0, sameAsRedux = 0, sameAsOriginal = 0;
        foreach (var path in files)
        {
            string stem = Path.GetFileNameWithoutExtension(path);
            byte[] original = File.ReadAllBytes(path);
            bool isCurrent = VfxProbe.Read(original, stem).Version == VfxVersion.Current;
            if (isCurrent) current++;
            string gltf = ReduxCli.Convert(path, "gltf", Path.Combine(_temp, stem, "gltf"));
            byte[] redux = File.ReadAllBytes(ReduxCli.Convert(gltf, "vfx", Path.Combine(_temp, stem, "vfx"), Path.ChangeExtension(gltf, ".bin")));
            byte[] cairn;
            try { cairn = VfxWriter.Write(VfxGltfImport.Import(gltf).File); }
            catch (Exception e) when (e is InvalidOperationException or FormatException or ArgumentException or Cairn.Formats.AssetFormatException)
            {
                failures.Add($"{stem}: import failed: {e.Message}");
                continue;
            }
            if (cairn.AsSpan().SequenceEqual(redux)) sameAsRedux++;
            else failures.Add($"{stem}: differs from REDUX output ({Diff(cairn, redux)})");
            if (isCurrent)
            {
                if (cairn.AsSpan().SequenceEqual(original)) sameAsOriginal++;
                else if (redux.AsSpan().SequenceEqual(original)) failures.Add($"{stem}: differs from the original ({Diff(cairn, original)})");
                else output.WriteLine($"{stem}: REDUX itself does not reproduce the original ({Diff(redux, original)})");
            }
        }
        output.WriteLine($"{sameAsRedux}/{files.Length} match REDUX; {sameAsOriginal}/{current} current-version files match the original.");
        foreach (var f in failures) output.WriteLine(f);
        Assert.Empty(failures);
    }

    /// <summary>Cairn's own export with every <c>cairn_*</c> key removed imports from the <c>rf_*</c> keys alone.</summary>
    [Fact]
    public void CairnExport_WithoutCairnKeys_RoundTrips()
    {
        var failures = new List<string>();
        foreach (var path in StockFiles())
        {
            byte[] data = File.ReadAllBytes(path);
            var vfx = VfxReader.Read(data, Path.GetFileName(path));
            if (vfx.Version != VfxVersion.Current) continue;
            var doc = VfxGltfExport.Export(vfx);
            foreach (var p in doc.Nodes.Cast<GltfProperty>().Concat(doc.Materials).Concat(doc.Meshes.SelectMany(m => m.Primitives)))
                Strip(p.Extras);
            byte[] again = VfxWriter.Write(VfxGltfImport.Import(GltfReader.Read(GltfWriter.ToGlb(doc), "x.glb", _ => null)).File);
            if (!again.AsSpan().SequenceEqual(data)) failures.Add($"{Path.GetFileName(path)}: {Diff(again, data)}");
        }
        foreach (var f in failures) output.WriteLine(f);
        Assert.Empty(failures);
    }

    private static void Strip(JsonNode? n)
    {
        if (n is not JsonObject o) return;
        foreach (var key in o.Select(p => p.Key).Where(k => k.StartsWith("cairn_", StringComparison.Ordinal)).ToList()) o.Remove(key);
    }

    private static string Diff(byte[] a, byte[] b)
    {
        int i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        return $"length {a.Length} vs {b.Length}, first difference at 0x{i:X} [{Convert.ToHexString(a, Math.Max(0, i - 8), Math.Min(24, a.Length - Math.Max(0, i - 8)))} vs {Convert.ToHexString(b, Math.Max(0, i - 8), Math.Min(24, b.Length - Math.Max(0, i - 8)))}]";
    }
}
