using Cairn.Formats;
using Cairn.Lvl.Model;
using Cairn.Lvl.Reading;

namespace Cairn.Lvl.Tests;

public class RfgReaderTests
{
    private static readonly RfMatrix Turned = new(new Vector3(0, 0, -1), Vector3.UnitY, new Vector3(1, 0, 0));

    private static readonly Vector3[] Tri = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)];

    private static void Solid(LvlBytes b) =>
        b.Solid(["rock.tga"], Tri, [new(0, -1, -1, 0, [(0, new(0, 0), default), (1, new(2, 0), default), (2, new(0, 2), default)])]);

    /// <summary>Writes one group: name, moving data, brushes, then every object list with its version gate.</summary>
    internal static void Group(LvlBytes w, string name, bool moving, int brushes, Action<LvlBytes>? lights = null,
        Action<LvlBytes>? events = null, int stopBeforeList = int.MaxValue)
    {
        int v = w.V;
        w.Str(name);
        if (v >= 0x5B) w.U8(moving ? 1 : 0);
        if (moving) w.MovingData(900, 901);
        w.I32(brushes);
        for (int i = 0; i < brushes; i++) w.Brush(100 + i, new Vector3(i, 0, 0), Turned, (uint)(i == 0 ? 0x04 : 0x02), Solid);
        int list = 0;
        void Empty(int gate = 0, bool below = false)
        {
            bool present = below ? v < gate : v >= gate;
            if (!present) return;
            if (list++ >= stopBeforeList) return;
            w.I32(0);
        }
        Empty();                                   // geo regions
        if (list++ < stopBeforeList)
        {
            if (lights is null) w.I32(0); else lights(w);
        }
        Empty();                                   // cutscene cameras
        Empty(0x8C);                               // cutscene path nodes
        Empty();                                   // ambient sounds
        if (list++ < stopBeforeList)
        {
            if (events is null) w.I32(0); else events(w);
        }
        Empty();                                   // respawn points
        Empty(0x8C, below: true);                  // legacy waypoint lists
        foreach (var _ in Enumerable.Range(0, 10)) Empty(); // nav points .. room effects
        Empty(0xB4);                               // EAX effects
        Empty(); Empty(); Empty();                 // bolt emitters, targets, push regions
    }

    private static byte[] AlpineGroups(Action<LvlBytes>? chunks = null, int version = 300)
    {
        var w = new LvlBytes(version);
        w.U32(RfgReader.Magic).I32(version).I32(2);
        Group(w, "door", moving: true, brushes: 2,
            lights: l => l.I32(1).Light(7, Vector3.One, 0x18, 5),
            events: e => e.I32(2).Event(8, "Teleport_Player", Vector3.Zero, Turned).Event(9, "Anchor_Marker_Orient", Vector3.Zero, Turned));
        Group(w, "static", moving: false, brushes: 1);
        w.Section(0x0AFBAE01, s => s.U32(1).AlpineMesh(200, Vector3.Zero, Turned, clutter: false).U8(0).U8(1).Str("").F32(1));
        w.Section(0x0AFBAE03, s => s.U32(1).Corona(300, Vector3.UnitY, ""));
        w.Section(0x0AFBAE05, s => s.U32(1).U16(6).U32(2).U32(0).U8(1).U8(0).U32(1).U8(2).U8(0x83));
        w.Section(0x0AFBAE02, s => s.U32(0));
        chunks?.Invoke(w);
        return w.ToArray();
    }

    [Fact]
    public void Alpine_group_file_reads_groups_objects_and_chunks()
    {
        var file = RfgReader.Read(AlpineGroups(), "door.rfg");

        Assert.Equal(300, file.Version);
        Assert.Equal(RfGroupFallbacks.None, file.Fallbacks);
        Assert.Empty(file.Problems);
        Assert.Null(file.UnknownDataOffset);
        Assert.Equal(2, file.Groups.Length);

        var door = file.Groups[0];
        Assert.True(door.IsMoving);
        Assert.Equal(2, door.Moving!.Keyframes.Length);
        Assert.Equal(2, door.Brushes.Length);
        Assert.Equal(Turned, door.Brushes[0].Orientation);
        Assert.Equal(RfSolidFlags.Detail, door.Brushes[0].SolidFlags);
        Assert.Equal(new Vector2(2, 0), door.Brushes[0].Geometry.Faces[0].Corners[1].Uv);
        Assert.Equal(7, Assert.Single(door.Objects.Lights).Uid);
        Assert.All(door.Objects.Events, e => Assert.Equal(Turned, e.Orientation));

        // Alpine objects and brush data belong to the first group.
        var mesh = Assert.Single(door.Objects.AlpineMeshes);
        Assert.Equal(Turned, mesh.Orientation);
        Assert.Equal((byte)0, mesh.TrailingFlags);
        Assert.Equal((byte)1, mesh.BrushGeometrySource);
        Assert.Single(door.Objects.Coronas);
        Assert.Equal(RfAlpineBrushFlags.Geoable, door.Brushes[0].Alpine!.Flags);
        Assert.Equal(RfAlpineBrushFlags.Breakable, door.Brushes[1].Alpine!.Flags);
        Assert.Equal(0x83, door.Brushes[1].Alpine!.Material);
        Assert.Equal(2, file.BrushMetadata!.Entries.Length);

        var notes = Assert.Single(file.OpaqueChunks);
        Assert.Equal(0x0AFBAE02u, notes.Id);

        var plain = file.Groups[1];
        Assert.False(plain.IsMoving);
        Assert.Single(plain.Brushes);
        Assert.Empty(plain.Objects.AlpineMeshes);
    }

    [Fact]
    public void Reading_stops_at_an_unknown_chunk()
    {
        var file = RfgReader.Read(AlpineGroups(t => t.Section(0x0AFB0001, s => s.U8(1)).Bytes([1, 2, 3])), "early.rfg");
        Assert.NotNull(file.UnknownDataOffset);
        Assert.Contains(file.Problems, p => p.Contains("0x0AFB0001"));
        Assert.Single(file.Groups[0].Objects.AlpineMeshes);
    }

    [Fact]
    public void Old_group_files_without_a_count_and_ending_early_read()
    {
        // 0x60: no group count, brushes carry the extra i32, legacy waypoint lists, no cutscene path nodes; the file
        // ends before its last lists, which RED reads as empty.
        var w = new LvlBytes(0x60);
        w.U32(RfgReader.Magic).I32(0x60);
        Group(w, "old", moving: false, brushes: 1, stopBeforeList: 18);
        var file = RfgReader.Read(w.ToArray(), "old.rfg");

        var g = Assert.Single(file.Groups);
        Assert.Equal(7, g.Brushes[0].LegacyExtra);
        Assert.Empty(g.Objects.PushRegions);
        Assert.Empty(file.Problems);
    }

    [Fact]
    public void Stock_version_with_alpine_event_matrices_reads_with_a_fallback()
    {
        // Alpine RED saving with a stock version still writes the Alpine event matrix (the 12.rfg case).
        var w = new LvlBytes(0xC8);
        w.U32(RfgReader.Magic).I32(0xC8).I32(1);
        Group(w, "g", moving: false, brushes: 0, events: e => e.I32(1).Event(1, "Anchor_Marker_Orient", Vector3.Zero, Turned));
        var file = RfgReader.Read(w.ToArray(), "12.rfg");
        Assert.Equal(RfGroupFallbacks.AlpineEventMatricesBelow300, file.Fallbacks);
        Assert.Equal(Turned, file.Groups[0].Objects.Events[0].Orientation);
    }

    [Fact]
    public void Redux_event_matrices_read_with_a_fallback_and_a_warning()
    {
        var w = new LvlBytes(300);
        w.U32(RfgReader.Magic).I32(300).I32(1);
        Group(w, "g", moving: false, brushes: 0, events: e => e.I32(1).Event(1, "Play_Explosion", Vector3.Zero, Turned));
        var file = RfgReader.Read(w.ToArray(), "redux.rfg");
        Assert.Equal(RfGroupFallbacks.ReduxEventMatrices, file.Fallbacks);
        Assert.Contains(file.Problems, p => p.Contains("REDUX"));
    }

    [Fact]
    public void Bad_group_files_are_refused_with_a_clear_message()
    {
        Assert.Contains("not a Red Faction group", Assert.Throws<AssetFormatException>(() => RfgReader.Read(new byte[12], "a.rfg")).Message);
        var bytes = AlpineGroups();
        var cut = bytes[..200];
        var e = Assert.Throws<AssetFormatException>(() => RfgReader.Read(cut, "cut.rfg"));
        Assert.Contains("damaged", e.Message);
        Assert.Contains("group 1", e.Message);
    }
}
