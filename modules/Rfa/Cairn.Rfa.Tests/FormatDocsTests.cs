using System.Text.RegularExpressions;
using Cairn.Rfa.Docs;

namespace Cairn.Rfa.Tests;

public class FormatDocsTests
{
    [Fact]
    public void EveryIdIsUniqueAndWellFormed()
    {
        var ids = FormatDocs.AllFields.Select(f => f.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var sectionIds = FormatDocs.Rfa.Sections.Concat(FormatDocs.V3c.Sections).Select(s => s.Id).ToList();
        Assert.Equal(sectionIds.Count, sectionIds.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var shape = new Regex("^(rfa|v3c)(\\.[a-z0-9_]+){1,2}$");
        Assert.All(ids, id => Assert.Matches(shape, id));
        Assert.All(FormatDocs.Rfa.Fields, f => Assert.StartsWith("rfa.", f.Id));
        Assert.All(FormatDocs.V3c.Fields, f => Assert.StartsWith("v3c.", f.Id));
    }

    [Fact]
    public void EveryFieldIsDocumented()
    {
        Assert.All(FormatDocs.AllFields, f =>
        {
            Assert.False(string.IsNullOrWhiteSpace(f.Summary), f.Id);
            Assert.False(string.IsNullOrWhiteSpace(f.EngineUse), f.Id);
            Assert.False(string.IsNullOrWhiteSpace(f.Name), f.Id);
            Assert.False(string.IsNullOrWhiteSpace(f.Type), f.Id);
            Assert.Contains(f.Format, new[] { "RFA", "V3C" });
            Assert.NotNull(FormatDocs.FindSection(f.Section));
            Assert.Contains(f.Summary, f.Tooltip);
        });
        Assert.All(FormatDocs.Rfa.Sections.Concat(FormatDocs.V3c.Sections), s => Assert.NotEmpty(s.Fields));
        Assert.All(FormatDocs.EngineTopics, t => Assert.False(string.IsNullOrWhiteSpace(t.Text), t.Id));
        Assert.Equal(FormatDocs.EngineTopics.Length, FormatDocs.EngineTopics.Select(t => t.Id).Distinct().Count());
    }

    [Fact]
    public void FindWorksAndTheInspectorIdsExist()
    {
        Assert.All(FormatDocs.InspectorIds, id => Assert.NotNull(FormatDocs.Find(id)));
        Assert.Equal("Ramp in", FormatDocs.Find("RFA.RAMP_IN")!.Name);
        Assert.Null(FormatDocs.Find("rfa.nothing"));
        Assert.Null(FormatDocs.Find(null!));
        Assert.Equal("rfa.bone", FormatDocs.Find("rfa.bone.weight")!.Section);
        Assert.NotNull(FormatDocs.FindTopic("engine.weights"));
        Assert.Contains("Ramp", FormatDocs.FindSection("rfa.header")!.Fields.Select(f => f.Name).First(n => n.StartsWith("Ramp", StringComparison.Ordinal)));
    }

    [Fact]
    public void TheReferenceCoversEveryStoredField()
    {
        string[] required =
        [
            // RFA header, in file order
            "rfa.signature", "rfa.version", "rfa.pos_reduction", "rfa.rot_reduction", "rfa.start_time", "rfa.end_time",
            "rfa.num_bones", "rfa.num_morph_vertices", "rfa.num_morph_keyframes", "rfa.ramp_in", "rfa.ramp_out",
            "rfa.total_rotation", "rfa.total_translation", "rfa.morph_vertices_offset", "rfa.morph_keyframes_offset", "rfa.bone_offsets",
            // records
            "rfa.bone.weight", "rfa.bone.num_rot_keys", "rfa.bone.num_pos_keys",
            "rfa.rotkey.time", "rfa.rotkey.rotation", "rfa.rotkey.ease_in", "rfa.rotkey.ease_out", "rfa.rotkey.pad",
            "rfa.poskey.time", "rfa.poskey.position", "rfa.poskey.in_ctrl", "rfa.poskey.out_ctrl",
            "rfa.morph.vertex_indices", "rfa.morph.times", "rfa.morph.aabb", "rfa.morph.positions_v8", "rfa.morph.positions_v7",
            // V3C
            "v3c.signature", "v3c.version", "v3c.num_submeshes", "v3c.num_all_materials", "v3c.num_colspheres",
            "v3c.section.type", "v3c.section.size",
            "v3c.submesh.name", "v3c.submesh.num_lods", "v3c.submesh.lod_distances", "v3c.submesh.radius",
            "v3c.lod.flags", "v3c.lod.num_vertices", "v3c.lod.num_batches", "v3c.lod.textures",
            "v3c.batch.texture_index", "v3c.batch.num_vertices", "v3c.batch.bone_links", "v3c.batch.morph_map",
            "v3c.material.diffuse_map", "v3c.material.emissive", "v3c.material.flags",
            "v3c.prop.name", "v3c.prop.parent", "v3c.csphere.radius", "v3c.csphere.bone",
            "v3c.bone.num_bones", "v3c.bone.name", "v3c.bone.rotation", "v3c.bone.position", "v3c.bone.parent",
        ];
        Assert.All(required, id => Assert.NotNull(FormatDocs.Find(id)));

        // Header offsets are given and ascend.
        var offsets = FormatDocs.FindSection("rfa.header")!.Fields.Where(f => f.Offset is not null)
            .Select(f => Convert.ToInt32(f.Offset, 16)).ToList();
        Assert.Equal(offsets.Order(), offsets);
        Assert.Equal(0x50, offsets[^1]);
    }

    [Fact]
    public void TheEngineFactsAreStated()
    {
        Assert.False(FormatDocs.Find("rfa.total_rotation")!.ReadByGame);
        Assert.False(FormatDocs.Find("rfa.total_translation")!.ReadByGame);
        Assert.False(FormatDocs.Find("rfa.pos_reduction")!.ReadByGame);
        Assert.False(FormatDocs.Find("rfa.rot_reduction")!.ReadByGame);
        Assert.True(FormatDocs.Find("rfa.ramp_in")!.ReadByGame);
        Assert.Contains("Not read by the game", FormatDocs.Find("rfa.total_rotation")!.Tooltip);
        Assert.Contains("unused", FormatDocs.Find("rfa.total_rotation")!.EngineUse);
        Assert.Contains("(10 - w) / 10", FormatDocs.Find("rfa.bone.weight")!.EngineUse);
        Assert.Contains("0.00001", FormatDocs.Find("rfa.bone.weight")!.EngineUse);
        Assert.Contains("1 - dot <= 1e-6", FormatDocs.Find("rfa.rotkey.rotation")!.EngineUse);
        Assert.Contains("/ 255", FormatDocs.Find("rfa.morph.positions_v8")!.EngineUse);
        Assert.Contains("1 to 3", FormatDocs.Find("v3c.submesh.num_lods")!.Limits);
        Assert.Contains("50", FormatDocs.Find("v3c.bone.num_bones")!.Limits);
        Assert.Contains("7", FormatDocs.Find("v3c.lod.textures")!.Limits);
        Assert.Contains("255", FormatDocs.Find("v3c.batch.bone_links")!.Limits);
        Assert.False(FormatDocs.Find("v3c.material.flags")!.ReadByGame);
    }
}
