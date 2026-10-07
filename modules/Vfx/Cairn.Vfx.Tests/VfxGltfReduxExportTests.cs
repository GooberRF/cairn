using System.Text.Json.Nodes;
using Cairn.Formats.Gltf;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Interchange;
using Cairn.Workspace;
using Xunit.Abstractions;

namespace Cairn.Vfx.Tests;

/// <summary>Cairn's VFX -> glTF export against REDUX's extras schema and REDUX's own glTF -> VFX importer.</summary>
public sealed class VfxGltfReduxExportTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("cairn-vfx-gltf-redux-").FullName;

    public void Dispose() { try { Directory.Delete(_temp, true); } catch (IOException) { } }

    private static readonly Dictionary<string, string[]> RequiredKeys = new()
    {
        ["vfx"] = ["rf_version", "rf_header_flags", "rf_end_frame", "rf_selset_object_count", "rf_section_order", "rf_material_table"],
        ["vfx_mesh"] = ["rf_name", "rf_parent_name", "rf_save_parent", "rf_flags", "rf_fps", "rf_start_time", "rf_end_time", "rf_num_frames",
            "rf_vertex_count", "rf_material_indices", "rf_material_names", "rf_bounding_center", "rf_bounding_radius", "rf_is_keyframed",
            "rf_smoothing_groups", "rf_face_material_index", "rf_face_indices", "rf_face_colors", "rf_face_normals", "rf_face_centers",
            "rf_face_radii", "rf_face_vertex_indices", "rf_face_vertex_raw", "rf_pos_frames", "rf_gltf_vertex_source", "rf_flag_morph"],
        ["vfx_dummy"] = ["rf_name", "rf_parent_name", "rf_save_parent", "rf_pos", "rf_orient", "rf_frames"],
        ["vfx_particle_system"] = ["rf_name", "rf_parent_name", "rf_save_parent", "rf_flags", "rf_warps", "rf_start_time", "rf_num_frames",
            "rf_material_index", "rf_particle_count", "rf_start", "rf_lifetime", "rf_lifetime_variation", "rf_emitter_type",
            "rf_shrink_at_birth", "rf_shrink_at_death", "rf_fade_at_birth", "rf_fade_at_death", "rf_frame_pos", "rf_frame_orient",
            "rf_frame_width", "rf_frame_height", "rf_frame_drop_size", "rf_frame_speed", "rf_frame_speed_variation", "rf_frame_birth_rate"],
        ["vfx_light"] = ["rf_name", "rf_parent_name", "rf_save_parent", "rf_params", "rf_frames"],
        ["vfx_spacewarp"] = ["rf_name", "rf_parent_name", "rf_warp_type", "rf_frame_pos", "rf_frame_orient", "rf_frame_strength",
            "rf_frame_decay", "rf_frame_turbulence", "rf_frame_frequency", "rf_frame_scale"],
        ["vfx_material_modifier"] = ["rf_material_index"],
        ["vfx_unknown"] = ["rf_section_type", "rf_raw_base64"],
        ["vfx_material"] = ["rf_material_index", "rf_mat_type", "rf_mat_type_id", "rf_additive", "rf_fps", "rf_mix_frames", "rf_specular_level",
            "rf_glossiness", "rf_reflection_amount", "rf_refl_tex_name", "rf_solid_color", "rf_self_illumination", "rf_opacity"],
    };

    private static IEnumerable<(string Path, VfxFile File)> Stock(bool current)
    {
        if (LocalPaths.Corpus is not { } dir || !Directory.Exists(dir)) yield break;
        foreach (var path in Directory.GetFiles(dir, "*.vfx").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var vfx = VfxReader.Read(File.ReadAllBytes(path), Path.GetFileName(path));
            if ((vfx.Version == VfxVersion.Current) == current) yield return (path, vfx);
        }
    }

    [Fact]
    public void Export_carries_REDUX_key_set_per_node_type()
    {
        int files = 0;
        foreach (var (path, vfx) in Stock(true))
        {
            var doc = VfxGltfExport.Export(vfx);
            var sections = vfx.Sections.Count(s => s is not VfxMaterial);
            Assert.Equal(sections + 1, doc.Nodes.Count);
            var objects = doc.Nodes.Select(n => n.Extras as JsonObject).Concat(doc.Materials.Select(m => m.Extras as JsonObject))
                .Concat(((JsonArray)doc.Nodes[0].Extras!["rf_material_table"]!).Select(e => e as JsonObject));
            foreach (var x in objects)
            {
                Assert.NotNull(x);
                string type = x["rf_type"]!.GetValue<string>();
                Assert.True(RequiredKeys.TryGetValue(type, out var keys), $"{Path.GetFileName(path)}: unexpected rf_type {type}");
                foreach (var k in keys!) Assert.True(x.ContainsKey(k), $"{Path.GetFileName(path)}: {type} lacks {k}");
            }
            // Every section node hangs under node 0 or under another section node, exactly once.
            Assert.Equal(sections, doc.Nodes.Sum(n => n.Children.Count));
            files++;
        }
        output.WriteLine($"{files} files checked");
    }

    /// <summary>Cairn export -> REDUX glTF -> VFX must reproduce every stock 0x40006 file byte for byte.</summary>
    [Fact]
    public void Redux_imports_Cairn_export_byte_identical()
    {
        if (ReduxCli.Exe is null || LocalPaths.Corpus is not { } dir || !Directory.Exists(dir)) return;
        var failures = new List<string>();
        int n = 0;
        foreach (var (path, vfx) in Stock(true))
        {
            n++;
            byte[] back = ViaRedux(path, vfx, out string work);
            byte[] original = File.ReadAllBytes(path);
            if (!back.AsSpan().SequenceEqual(original)) failures.Add(Describe(path, original, back));
            Directory.Delete(work, true);
        }
        output.WriteLine($"{n - failures.Count}/{n} byte-identical");
        foreach (var f in failures) output.WriteLine(f);
        Assert.Empty(failures);
    }

    /// <summary>Older files: REDUX's import of Cairn's export must equal REDUX's own .vfx -> .gltf -> .vfx of the original.</summary>
    [Fact]
    public void Redux_imports_Cairn_export_of_older_versions_like_its_own_round_trip()
    {
        if (ReduxCli.Exe is null || LocalPaths.Corpus is not { } dir || !Directory.Exists(dir)) return;
        var failures = new List<string>();
        var upgradeOnly = new List<string>();
        int n = 0, same = 0;
        foreach (var (path, vfx) in Stock(false))
        {
            n++;
            byte[] back = ViaRedux(path, vfx, out string work);
            string own = Path.Combine(_temp, "own-" + n);
            string gltf = ReduxCli.Convert(path, "gltf", own);
            string again = Path.Combine(own, "again");
            byte[] reference = File.ReadAllBytes(ReduxCli.Convert(gltf, "vfx", again, Path.ChangeExtension(gltf, ".bin")));
            // Equal to REDUX's own round trip. Only files with a documented engine-correct upgrade deviation
            // (VfxUpgradeReduxTests.Deviations: spiketribeam's start-aligned opacity ramp) may instead equal Cairn's own
            // upgraded bytes, i.e. REDUX kept Cairn's model exactly and only the two readers' legacy upgrade differs.
            if (back.AsSpan().SequenceEqual(reference)) same++;
            else if (VfxUpgradeReduxTests.Deviations.ContainsKey(Path.GetFileName(path))
                     && back.AsSpan().SequenceEqual(VfxWriter.Write(Cairn.Vfx.Editing.VfxUpgrade.ToCurrent(vfx))))
                upgradeOnly.Add(Describe(path, reference, back));
            else failures.Add(Describe(path, reference, back));
            Directory.Delete(work, true);
            Directory.Delete(own, true);
        }
        output.WriteLine($"{same}/{n} equal to REDUX's own round trip; {upgradeOnly.Count} differ only by a documented upgrade deviation (REDUX kept Cairn's model exactly)");
        foreach (var f in upgradeOnly) output.WriteLine("upgrade deviation: " + f);
        foreach (var f in failures) output.WriteLine(f);
        Assert.Empty(failures);
    }

    private byte[] ViaRedux(string path, VfxFile vfx, out string work)
    {
        work = Path.Combine(_temp, Path.GetFileNameWithoutExtension(path));
        Directory.CreateDirectory(work);
        string gltf = Path.Combine(work, Path.GetFileNameWithoutExtension(path) + ".gltf");
        VfxGltfExport.Save(vfx, gltf);
        return File.ReadAllBytes(ReduxCli.Convert(gltf, "vfx", Path.Combine(work, "redux"), Path.ChangeExtension(gltf, ".bin")));
    }

    private static string Describe(string path, byte[] expected, byte[] actual)
    {
        int i = 0;
        while (i < expected.Length && i < actual.Length && expected[i] == actual[i]) i++;
        return $"{Path.GetFileName(path)}: {expected.Length} vs {actual.Length} bytes, first difference at 0x{i:X}";
    }
}
