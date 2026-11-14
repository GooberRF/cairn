using Cairn.Lvl.Conversion;

namespace Cairn.Lvl.Model;

/// <summary>A script trigger of an N-Gage level (marker 3106): the volume and the cutscene script it runs.</summary>
/// <param name="TriggerName">The volume's name (<c>gr_entrig</c>).</param>
/// <param name="ScriptFile">The script it runs (<c>C01_entrig.txt</c>), or empty when the level has no number.</param>
/// <param name="Found">True when the script is beside the level, false when it is not, null when that was not checked.</param>
public sealed record XffScriptTrigger(string TriggerName, string ScriptFile, bool? Found);

/// <summary>N-Gage objects of one kind that have no place in the Red Faction model: the kind, how many, and some names.</summary>
/// <param name="What">What they are ("marker 3117 (volume, meaning unknown)", "enemy_lrbt.xff").</param>
/// <param name="Count">How many the level holds.</param>
/// <param name="Reason">Why they are not converted.</param>
/// <param name="Names">A few of their node names.</param>
public sealed record XffUnmapped(string What, int Count, string Reason, ImmutableArray<string> Names);

/// <summary>
/// What a Red Faction for N-Gage level (<c>.xff</c>) holds that the Red Faction model does not show by itself: the
/// scale it was read at, its areas and room volumes, the scenes it places as geometry, what has no counterpart, the
/// script triggers, and the reading notes every conversion's report starts with.
/// </summary>
public sealed record XffLevelData
{
    /// <summary>Red Faction metres per N-Gage unit the level was read with.</summary>
    public float Scale { get; init; }
    /// <summary>The level's number in script names ("01" for <c>level_01a</c>), or null.</summary>
    public string? LevelNumber { get; init; }
    /// <summary>The area names (marker 2013), one per room of the static geometry, in room order.</summary>
    public ImmutableArray<string> Areas { get; init; } = [];
    /// <summary>Walkable room volumes (<c>room</c> boxes).</summary>
    public int RoomBoxes { get; init; }
    /// <summary>Portal volumes (<c>portal:</c> boxes) that became portals.</summary>
    public int Portals { get; init; }
    /// <summary>The triangles drawn (the level's meshes and the scenes placed as geometry).</summary>
    public int Triangles { get; init; }
    /// <summary>The texture names the drawn geometry uses, as stored (<c>.pcx</c> and <c>.tga</c>).</summary>
    public ImmutableArray<string> Textures { get; init; } = [];
    /// <summary>Placed scenes drawn as part of the level geometry (doors, buttons, walls...), "file (what)", one per file.</summary>
    public ImmutableArray<string> PlacedGeometry { get; init; } = [];
    /// <summary>Placed scene files that were not found beside the level (their geometry is missing).</summary>
    public ImmutableArray<string> MissingFiles { get; init; } = [];
    /// <summary>What was not converted, by kind.</summary>
    public ImmutableArray<XffUnmapped> Unmapped { get; init; } = [];
    /// <summary>The script triggers and their scripts.</summary>
    public ImmutableArray<XffScriptTrigger> Scripts { get; init; } = [];
    /// <summary>Report entries from reading: how the objects were mapped, what was dropped and why (every target).</summary>
    public ImmutableArray<LvlConvertIssue> Notes { get; init; } = [];
    /// <summary>Report entries of the brush pre-flight (closed or open meshes, degenerate faces); for group files.</summary>
    public ImmutableArray<LvlConvertIssue> BrushNotes { get; init; } = [];

    /// <summary>What the level was read from, so a conversion can read it again at another scale.</summary>
    internal Reading.XffLevelSource? Source { get; init; }
}
