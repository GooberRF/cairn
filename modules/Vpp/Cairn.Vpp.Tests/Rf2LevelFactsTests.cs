using System.Text;
using Cairn.Vpp.Facts;
using Cairn.Vpp.Model;
using Cairn.Vpp.Ps2;

namespace Cairn.Vpp.Tests;

/// <summary>
/// The packfile details of a Red Faction II level: the whole summary (identity, fog, the RF2 sun and hardlight,
/// statistics, sections), with its textures looked up in the .peg packs of its packfile and nothing called missing.
/// </summary>
public sealed class Rf2LevelFactsTests
{
    private static void Str(BinaryWriter w, string s)
    {
        var b = Encoding.Latin1.GetBytes(s);
        w.Write((ushort)b.Length);
        w.Write(b);
    }

    private static void Section(BinaryWriter w, uint id, Action<BinaryWriter> body)
    {
        var m = new MemoryStream();
        body(new BinaryWriter(m));
        w.Write(id);
        w.Write((int)m.Length);
        w.Write(m.ToArray());
    }

    /// <summary>An RF2 level: the bitmap list, level properties with fog and the RF2 tail, geometry naming two textures, level info.</summary>
    private static byte[] Level()
    {
        var m = new MemoryStream();
        var w = new BinaryWriter(m);
        w.Write(0xD4BADA55); w.Write(0x127); w.Write(1_040_000_000); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
        Str(w, "lvl");
        Section(w, 0x7000, b => b.Write(0));
        Section(w, 0x900, b =>
        {
            Str(b, "rck_rock02.tga"); b.Write(50); b.Write(new byte[] { 10, 20, 30, 255 }); b.Write((byte)0); b.Write(new byte[] { 100, 90, 80, 255 }); b.Write(5f); b.Write(120f);
            b.Write(new byte[] { 255, 250, 240, 255 }); b.Write(60f); b.Write(65f); b.Write(0.5f); b.Write(89.9f); b.Write(new byte[] { 0, 255, 0, 255 }); b.Write(6); b.Write(1.2f);
        });
        Section(w, 0x100, b =>
        {
            Str(b, ""); b.Write(0u);
            b.Write(2); Str(b, "wall.tga"); Str(b, "nothere.tga");
            b.Write(0); b.Write(0); b.Write(0); b.Write(0); b.Write(0); b.Write(0); // rooms, sub-rooms, links, portals, vertices, faces
        });
        Section(w, 0x01000000, b => { b.Write(1); Str(b, "Test Level"); Str(b, "Tester"); Str(b, "Monday"); b.Write((byte)0); b.Write((byte)1); });
        w.Write(0); w.Write(0);
        return m.ToArray();
    }

    [Fact]
    public void Rf2_level_details_show_the_summary_with_textures_found_in_the_packfiles_pegs()
    {
        byte[] level = Level();
        byte[] peg = SyntheticPeg.V6(SyntheticPeg.Texture("wall.tga", 8, 4, 3, 0, 1, 2, (_, _, x, _) => x == 0 ? 0x801Fu : 0x0000u));
        var item = new VppItem("lvl.rfl", new MemorySource(level), VppItemState.Added);
        var package = new VppPackage(null, [item, new VppItem("lvl.peg", new MemorySource(peg), VppItemState.Added)]);
        var sheet = VppFacts.Describe(item, new VppFactsContext(package));

        Assert.Empty(sheet.Warnings);
        Assert.Equal("Test Level", sheet["Name"]);
        Assert.Equal("Tester", sheet["Author"]);
        Assert.Equal("Red Faction II", sheet["Loads in"]);
        Assert.Equal("Multiplayer", sheet["Mode"]);
        Assert.Equal("colour 100, 90, 80, from 5 to 120", sheet["Properties: Fog"]);
        Assert.Equal("yaw 60, pitch 65", sheet["Properties: Sun direction (light bake)"]);
        Assert.Equal("0, 255, 0", sheet["Properties: Hardlight colour"]);
        Assert.Equal("1.2", sheet["Properties: Lightmap multiplier"]);
        Assert.Equal("0", sheet["Statistics: Rooms"]);
        Assert.Equal("2", sheet["Statistics: Geometry textures"]);
        Assert.Equal("texture; level geometry; in this packfile", sheet["References: wall.tga"]);
        // a texture in no pack of the packfile is listed without a location, not called missing
        Assert.Equal("texture; level geometry", sheet["References: nothere.tga"]);
        Assert.Null(sheet["References: Missing"]);
        Assert.Contains(sheet.Rows, r => r.Label == "Sections: Level properties" && r.Value.Contains("read", StringComparison.Ordinal));
        Assert.Contains(sheet.Rows, r => r.Label == "Sections: Static geometry");
    }
}
