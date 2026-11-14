namespace Cairn.Vpp.Facts;

/// <summary>Broad kinds of packfile entries, for icons and the type filter.</summary>
public enum VppFileCategory
{
    Image,
    Audio,
    Mesh,
    Animation,
    Effect,
    Level,
    Table,
    Text,
    Font,
    Other,
}

/// <summary>Whether the game asks for files of a type from packfiles.</summary>
public enum VppGameSupport
{
    /// <summary>Stock RF 1.2 loads it (and so does Alpine Faction).</summary>
    Stock,
    /// <summary>Only Alpine Faction loads it.</summary>
    AlpineOnly,
    /// <summary>Nothing in the game asks for it (source art, editor files, notes).</summary>
    NotLoaded,
}

/// <summary>One known entry type.</summary>
/// <param name="Extension">Lower case with the dot.</param>
/// <param name="DisplayName">Friendly type name ("Targa image").</param>
/// <param name="Category">Broad kind.</param>
/// <param name="CairnOpens">True when a Cairn module opens this type in a tab.</param>
/// <param name="Support">Whether the game loads this type from a packfile.</param>
public sealed record VppFileType(string Extension, string DisplayName, VppFileCategory Category, bool CairnOpens, VppGameSupport Support)
{
    /// <summary>True when the game (Alpine Faction, the baseline) loads this type.</summary>
    public bool GameLoads => Support != VppGameSupport.NotLoaded;

    /// <summary>The first Alpine Faction version that loads an Alpine-added type, when known (shown as information only).</summary>
    public string? AlpineSince { get; init; }
}

/// <summary>The registry of entry types by extension.</summary>
public static class VppFileTypes
{
    private const VppGameSupport Stock = VppGameSupport.Stock, Alpine = VppGameSupport.AlpineOnly, No = VppGameSupport.NotLoaded;

    private static readonly Dictionary<string, VppFileType> ByExtension = new VppFileType[]
    {
        new(".tga", "Targa image", VppFileCategory.Image, false, Stock),
        new(".vbm", "Volition bitmap", VppFileCategory.Image, false, Stock),
        new(".dds", "DirectDraw Surface image", VppFileCategory.Image, false, Alpine),
        new(".png", "PNG image", VppFileCategory.Image, false, Alpine),
        new(".jpg", "JPEG image", VppFileCategory.Image, false, Alpine),
        new(".jpeg", "JPEG image", VppFileCategory.Image, false, Alpine),
        new(".atx", "Animated texture", VppFileCategory.Image, true, Alpine),
        // stock bmpman reads PCX, and Alpine's texture chain resolves it after .tga; Cairn reads it, never writes it
        new(".pcx", "PCX image", VppFileCategory.Image, false, Stock),
        new(".bmp", "Bitmap image", VppFileCategory.Image, false, No),
        new(".psd", "Photoshop document", VppFileCategory.Image, false, No),
        new(".wav", "WAVE audio", VppFileCategory.Audio, false, Stock),
        new(".ogg", "Ogg Vorbis audio", VppFileCategory.Audio, false, Alpine),
        new(".aif", "AIFF-C audio", VppFileCategory.Audio, false, No),
        new(".aiff", "AIFF audio", VppFileCategory.Audio, false, No),
        new(".aifc", "AIFF-C audio", VppFileCategory.Audio, false, No),
        new(".mp3", "MP3 audio", VppFileCategory.Audio, false, No),
        new(".v3m", "Static mesh", VppFileCategory.Mesh, true, Stock),
        new(".v3c", "Character mesh", VppFileCategory.Mesh, true, Stock),
        new(".v3d", "Static mesh (exporter)", VppFileCategory.Mesh, true, No),
        new(".gltf", "glTF model", VppFileCategory.Mesh, true, No),
        new(".glb", "glTF binary model", VppFileCategory.Mesh, true, No),
        new(".rfa", "Animation clip", VppFileCategory.Animation, true, Stock),
        new(".mvf", "Legacy motion (RFA v5)", VppFileCategory.Animation, false, No),
        new(".vfx", "Effect", VppFileCategory.Effect, true, Stock),
        new(".rfl", "Level", VppFileCategory.Level, false, Stock),
        new(".rfg", "Editor group", VppFileCategory.Level, false, No),
        new(".tbl", "Table", VppFileCategory.Table, false, Stock),
        new(".txt", "Text", VppFileCategory.Text, false, No),
        new(".log", "Log", VppFileCategory.Text, false, No),
        new(".ini", "Settings text", VppFileCategory.Text, false, No),
        new(".vf", "Bitmap font", VppFileCategory.Font, false, Stock),
        // The PlayStation 2 version's own types (its packfiles have the PC layout); the PC game loads none of them.
        new(".peg", "PS2 texture pack", VppFileCategory.Image, true, No),
        new(".rfm", "PS2 static mesh", VppFileCategory.Mesh, true, No),
        new(".rfc", "PS2 character mesh", VppFileCategory.Mesh, true, No),
        new(".vcm", "Character mesh (exporter)", VppFileCategory.Mesh, true, No),
        new(".vse", "PS2 sound effect", VppFileCategory.Audio, true, No),
        new(".vmu", "PS2 music", VppFileCategory.Audio, true, No),
        new(".xm", "FastTracker 2 music", VppFileCategory.Audio, true, No), // played and converted, never written
        // Red Faction for N-Gage's own types (Fathammer's X-Forge engine); the PC game loads none of them. Cairn reads
        // them and never writes them (its .pcx textures, .tga images and .wav sounds are the PC game's types).
        new(".xff", "X-Forge scene", VppFileCategory.Mesh, true, No),
        new(".pr3", "X-Forge particle system", VppFileCategory.Effect, false, No),
        new(".cfl", "X-Forge archive", VppFileCategory.Other, true, No),
        new(".arr", "PS2 texture arrangement list", VppFileCategory.Text, false, No),
        new(".ver", "Version text", VppFileCategory.Text, false, No),
    }.ToDictionary(t => t.Extension, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every registered type.</summary>
    public static IReadOnlyCollection<VppFileType> All => ByExtension.Values;

    /// <summary>The type for a name's extension, or null when it is not registered.</summary>
    public static VppFileType? Find(string name)
    {
        string ext = Editing.VppNames.ExtensionOf(name);
        return ext.Length > 0 && ByExtension.TryGetValue(ext, out var type) ? type : null;
    }

    // Red Faction II's own types and the names its shared extensions go by in an RF2 packfile group (the game is
    // RF2, so "loaded by the game" and the PS2 names do not apply there). CairnOpens follows what the modules read.
    private static readonly Dictionary<string, VppFileType> Rf2ByExtension = new VppFileType[]
    {
        new(".peg", "RF2 texture pack", VppFileCategory.Image, true, No),
        new(".rfm", "RF2 static mesh", VppFileCategory.Mesh, true, No),
        new(".rfc", "RF2 character mesh", VppFileCategory.Mesh, true, No),
        new(".rfa", "RF2 animation", VppFileCategory.Animation, true, No),
        new(".rfe", "RF2 effect", VppFileCategory.Effect, true, No),
        new(".rfl", "RF2 level", VppFileCategory.Level, false, No),
        new(".tbl", "RF2 table", VppFileCategory.Table, false, No),
        new(".vmu", "RF2 music", VppFileCategory.Audio, true, No),
        new(".csc", "RF2 cutscene script", VppFileCategory.Text, false, No),
        new(".bik", "Bink video", VppFileCategory.Other, false, No),
        new(".scc", "SourceSafe file", VppFileCategory.Other, false, No),
    }.ToDictionary(t => t.Extension, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The type for a name in a Red Faction II packfile group when <paramref name="rf2"/> (RF2's own names for its
    /// types), else as <see cref="Describe(string)"/>.
    /// </summary>
    public static VppFileType Describe(string name, bool rf2)
    {
        if (rf2 && Editing.VppNames.ExtensionOf(name) is { Length: > 0 } ext && Rf2ByExtension.TryGetValue(ext, out var type)) return type;
        return Describe(name);
    }

    /// <summary>The type for a name, or a generic "EXT file" of category Other.</summary>
    public static VppFileType Describe(string name)
    {
        if (Find(name) is { } known) return known;
        string ext = Editing.VppNames.ExtensionOf(name);
        return new VppFileType(ext, ext.Length > 0 ? $"{ext[1..].ToUpperInvariant()} file" : "File", VppFileCategory.Other, false, VppGameSupport.NotLoaded);
    }
}
