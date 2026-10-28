using System.Globalization;
using Cairn.Lvl.Conversion;
using Cairn.Lvl.Model;

namespace Cairn.Lvl.Ui.Preview;

/// <summary>The kinds of object marker the preview draws, each with its own toggle.</summary>
[Flags]
internal enum LvlMarkerKinds
{
    None = 0,
    Lights = 0x1,
    /// <summary>The player start and multiplayer respawn points.</summary>
    Spawns = 0x2,
    Items = 0x4,
    Entities = 0x8,
    Clutter = 0x10,
    /// <summary>Triggers and events.</summary>
    Logic = 0x20,
    /// <summary>Everything else (sounds, emitters, regions, nav points, cameras, Alpine meshes...).</summary>
    Other = 0x40,
    All = 0x7F,
    /// <summary>What a new view shows: lights, spawns and items (a level can hold thousands of nav points and events).</summary>
    Default = Lights | Spawns | Items,
}

/// <summary>One object marker: where it is, its kind, what it is, and the record it came from.</summary>
/// <param name="Position">World position (RF space).</param>
/// <param name="Kind">Its toggle.</param>
/// <param name="Title">"Light 12", "Item 40 Medical Kit".</param>
/// <param name="Details">Lines shown when it is picked.</param>
internal sealed record LvlMarker(Vector3 Position, LvlMarkerKinds Kind, string Title, IReadOnlyList<string> Details);

/// <summary>The object markers of a level or group file.</summary>
internal static class LvlMarkers
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Every kind with its menu text, in menu order.</summary>
    public static IReadOnlyList<(LvlMarkerKinds Kind, string Text)> Kinds { get; } =
    [
        (LvlMarkerKinds.Lights, "Lights"),
        (LvlMarkerKinds.Spawns, "Player start and respawn points"),
        (LvlMarkerKinds.Items, "Items"),
        (LvlMarkerKinds.Entities, "Entities"),
        (LvlMarkerKinds.Clutter, "Clutter"),
        (LvlMarkerKinds.Logic, "Triggers and events"),
        (LvlMarkerKinds.Other, "Other objects"),
    ];

    /// <summary>The markers of every object the source places (editor-only lights included).</summary>
    public static IReadOnlyList<LvlMarker> From(LvlSource source)
    {
        var markers = new List<LvlMarker>();
        if (source.Level is { } level)
        {
            foreach (var o in level.Objects.All()) Add(markers, o, null);
            foreach (var light in level.EditorLights) Add(markers, light, "editor only");
            if (level.PlayerStart is { } start)
                markers.Add(new LvlMarker(start.Position, LvlMarkerKinds.Spawns, "Player start", [$"Position {Vec(start.Position)}"]));
        }
        else if (source.GroupFile is { } file)
        {
            foreach (var group in file.Groups)
                foreach (var o in group.Objects.All()) Add(markers, o, group.Name.Length > 0 ? "group " + group.Name : null);
        }
        return markers;
    }

    private static void Add(List<LvlMarker> markers, RfObject o, string? note)
    {
        if (!Finite(o.Position)) return;
        string type = TypeTitle(o);
        string cls = o is RfPlacedObject { ClassName.Length: > 0 } p && !p.ClassName.Equals(type, StringComparison.OrdinalIgnoreCase) ? " " + p.ClassName : "";
        string title = $"{type} {o.Uid.ToString(Inv)}{cls}";
        var details = new List<string> { $"Position {Vec(o.Position)}" };
        if (o is RfPlacedObject { ScriptName.Length: > 0 } named) details.Add("Script name " + named.ScriptName);
        switch (o)
        {
            case RfLight light:
                details.Add($"{LightType(light)} light, range {Num(light.Range)}, colour {light.Color}");
                break;
            case RfEvent ev:
                details.Add("Event " + ev.ClassName + (ev.ScriptName.Length > 0 ? ", script name " + ev.ScriptName : ""));
                if (ev.Links.Length > 0) details.Add("Links " + string.Join(", ", ev.Links.Take(12).Select(l => l.ToString(Inv))) + (ev.Links.Length > 12 ? ", ..." : ""));
                break;
            case RfTrigger trigger:
                if (trigger.Links.Length > 0) details.Add("Links " + string.Join(", ", trigger.Links.Take(12).Select(l => l.ToString(Inv))) + (trigger.Links.Length > 12 ? ", ..." : ""));
                break;
            case RfAlpineMesh mesh:
                details.Add("Mesh " + mesh.MeshFile);
                break;
        }
        if (o.HiddenInEditor) details.Add("Hidden in the editor");
        if (note is not null) details.Add(char.ToUpperInvariant(note[0]) + note[1..]);
        markers.Add(new LvlMarker(o.Position, KindOf(o), title, details));
    }

    /// <summary>The marker toggle an object falls under.</summary>
    public static LvlMarkerKinds KindOf(RfObject o) => o switch
    {
        RfLight => LvlMarkerKinds.Lights,
        RfRespawnPoint => LvlMarkerKinds.Spawns,
        RfItem => LvlMarkerKinds.Items,
        RfEntity => LvlMarkerKinds.Entities,
        RfClutter => LvlMarkerKinds.Clutter,
        RfTrigger or RfEvent => LvlMarkerKinds.Logic,
        _ => LvlMarkerKinds.Other,
    };

    /// <summary>"Light", "Respawn point", "Alpine mesh": the object type in words.</summary>
    public static string TypeTitle(RfObject o)
    {
        string name = LvlExtras.TypeName(o).Replace('_', ' ');
        return name.Length == 0 ? "Object" : char.ToUpperInvariant(name[0]) + name[1..];
    }

    private static string LightType(RfLight light) => light.Type switch
    {
        RfLightType.Omni => "Omni",
        RfLightType.Spot => "Spot",
        RfLightType.Tube => "Tube",
        _ => "A",
    };

    internal static string Vec(Vector3 v) => $"{Num(v.X)}, {Num(v.Y)}, {Num(v.Z)}";

    internal static string Num(float v) => v.ToString("0.###", Inv);

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
