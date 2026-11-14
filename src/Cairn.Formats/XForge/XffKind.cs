namespace Cairn.Formats.XForge;

/// <summary>What an X-Forge scene holds, as the meshes and levels modules divide them (<see cref="XffClassifier"/>).</summary>
public enum XffKind
{
    /// <summary>A prop, item, weapon, door, button or anything else that is not a level or a character.</summary>
    Model,
    /// <summary>An enemy or a player: rigid parts animated by tracks, with an enemy or player marker.</summary>
    Character,
    /// <summary>A level: rooms, areas, spawns and triggers.</summary>
    Level,
}

/// <summary>The game-object marker types (chunk types 2000 and up) that tell what a scene is.</summary>
public static class XffMarkers
{
    /// <summary>An area node in a level (<c>starthall</c>, <c>room01</c>).</summary>
    public const ushort Area = 2013;
    /// <summary>The level end.</summary>
    public const ushort LevelEnd = 2025;
    /// <summary>The player (first-person) scene's own marker.</summary>
    public const ushort Player = 2011;
    /// <summary>The multiplayer client player's marker.</summary>
    public const ushort PlayerClient = 3101;
    /// <summary>The multiplayer server player's marker.</summary>
    public const ushort PlayerServer = 3102;
    /// <summary>A deathmatch spawn point.</summary>
    public const ushort DeathmatchSpawn = 3103;
    /// <summary>A script trigger (<c>gr_entrig</c> runs <c>C01_entrig.txt</c>).</summary>
    public const ushort ScriptTrigger = 3106;
    /// <summary>A button scene's own marker.</summary>
    public const ushort Button = 3107;
    /// <summary>A door scene's own marker.</summary>
    public const ushort Door = 3108;
    /// <summary>A wall scene's own marker.</summary>
    public const ushort Wall = 3109;
    /// <summary>An enemy group in a level.</summary>
    public const ushort EnemyGroup = 3116;
    /// <summary>The torpedo enemy's own marker (<c>enemy_trpd</c>).</summary>
    public const ushort Torpedo = 4015;

    /// <summary>
    /// True for a marker found only in levels: areas (2013), the level end (2025), 3103–3106, 3111, 3112, 3116–3118, and
    /// the enemy spawn points 4200–4299.
    /// </summary>
    public static bool IsLevelMarker(ushort type) => type switch
    {
        Area or LevelEnd => true,
        >= 3103 and <= 3106 => true,
        3111 or 3112 => true,
        >= 3116 and <= 3118 => true,
        >= 4200 and <= 4299 => true,
        _ => false,
    };

    /// <summary>True for an enemy type's own marker (3200–3299, in <c>enemy_*.xff</c> and the sentry gun).</summary>
    public static bool IsEnemyMarker(ushort type) => type is >= 3200 and <= 3299;

    /// <summary>True for a player's own marker (2011, 3101, 3102).</summary>
    public static bool IsPlayerMarker(ushort type) => type is Player or PlayerClient or PlayerServer;

    /// <summary>
    /// True for the marker that gives an object scene its own type: a game object (2011–2071: the player, camera,
    /// weapons, items, ammo, vehicles, lasers), a player (3101, 3102), an object (3107–3110, 3113–3115: buttons, doors,
    /// walls, pumps, props, lasers, the barrel), an enemy (3200–3299) or the torpedo (4015).
    /// </summary>
    public static bool IsObjectMarker(ushort type) => type switch
    {
        >= 2011 and <= 2071 and not Area and not LevelEnd => true,
        PlayerClient or PlayerServer => true,
        >= 3107 and <= 3110 => true,
        >= 3113 and <= 3115 => true,
        >= 3200 and <= 3299 => true,
        Torpedo => true,
        _ => false,
    };

    /// <summary>A spawn point's enemy type (4202 → 3202), or null when <paramref name="type"/> is not a spawn point (4200–4299). The pairing is likely, not proven.</summary>
    public static ushort? SpawnedEnemyType(ushort type) => type is >= 4200 and <= 4299 ? (ushort)(type - 1000) : null;
}

/// <summary>
/// Sorts a scene into <see cref="XffKind.Level"/>, <see cref="XffKind.Character"/> or <see cref="XffKind.Model"/> by its
/// content (the file name never decides):
/// <list type="number">
/// <item>a <b>level</b> has a level marker (<see cref="XffMarkers.IsLevelMarker"/>), or <c>room</c> properties and no
/// object marker of its own (<see cref="XffMarkers.IsObjectMarker"/>: a button that carries a stray <c>room</c> property
/// is still a button);</item>
/// <item>a <b>character</b> has a mesh node animated by a track (a track above it in the tree) and an enemy or player
/// marker;</item>
/// <item>anything else is a <b>model</b>.</item>
/// </list>
/// Over the 264 scenes of Red Faction for N-Gage this gives the 35 <c>level_*</c> files as levels; 25 characters (the
/// enemies with animated parts, the sentry gun, and the three players); and 204 models (the torpedo enemy, which has no
/// tracks, among them).
/// </summary>
public static class XffClassifier
{
    /// <summary>The property text that marks a room node.</summary>
    public const string RoomProperty = "room";

    /// <summary>What <paramref name="scene"/> is.</summary>
    public static XffKind Classify(XffScene scene) => Explain(scene).Kind;

    /// <summary>What <paramref name="scene"/> is, and why (for facts and reports).</summary>
    public static (XffKind Kind, string Reason) Explain(XffScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var types = scene.Markers.Select(m => m.Type).ToHashSet();
        if (types.FirstOrDefault(XffMarkers.IsLevelMarker) is ushort level and > 0)
            return (XffKind.Level, $"it has level markers (type {level})");
        int rooms = scene.File.All<XffProperty>().Count(p => p.Text == RoomProperty);
        bool ownObject = types.Any(XffMarkers.IsObjectMarker);
        if (rooms > 0 && !ownObject) return (XffKind.Level, $"it has {rooms} room properties");
        int animated = scene.Meshes.Count(m => m.Node.IsAnimated);
        var actor = types.Where(t => XffMarkers.IsEnemyMarker(t) || XffMarkers.IsPlayerMarker(t)).Order().ToList();
        if (animated > 0 && actor.Count > 0)
            return (XffKind.Character, $"it has {animated} animated mesh nodes and {(XffMarkers.IsPlayerMarker(actor[0]) ? "a player" : "an enemy")} marker (type {actor[0]})");
        return (XffKind.Model, rooms > 0 ? "it is an object (its own marker says so) despite a room property" : "it is not a level or a character");
    }

    /// <summary>
    /// What the scene in <paramref name="data"/> is, or null when it cannot be read as a scene (cheap enough for a
    /// content claim: one walk and one scene build). Never throws for bad data.
    /// </summary>
    public static XffKind? TryClassify(ReadOnlySpan<byte> data, string name)
    {
        if (!XffFile.LooksLikeXff(data)) return null;
        try
        {
            return Classify(XffScene.Build(XffFile.Read(data.ToArray(), name)));
        }
        catch (AssetFormatException)
        {
            return null;
        }
    }
}
