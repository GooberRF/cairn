using Cairn.Formats.Imaging;

namespace Cairn.Atx.Schema;

/// <summary>Where a key may appear.</summary>
public enum AtxKeyScope
{
    /// <summary>The <c>[header]</c> table.</summary>
    Header,
    /// <summary>A <c>[[frame]]</c> entry.</summary>
    Frame,
}

/// <summary>The TOML value kinds the ATX format uses.</summary>
public enum AtxValueKind
{
    String,
    Integer,
    Boolean,
}

/// <summary>One key in the ATX schema, with everything the UI and linter need to describe it.</summary>
/// <param name="Name">The TOML key, e.g. <c>frame_time</c>.</param>
/// <param name="Scope">Where the key is valid.</param>
/// <param name="Kind">The TOML type the game reads.</param>
/// <param name="Order">Sort order used when emitting or inserting keys.</param>
/// <param name="Label">Short GUI label.</param>
/// <param name="Summary">One-line description for tooltips and the Help reference.</param>
/// <param name="Details">Longer explanation, including defaults and clamping rules.</param>
/// <param name="DefaultText">The effective default, phrased for a level designer, or null if none.</param>
/// <param name="RuntimeEvent">Related Alpine Faction level event, if any.</param>
/// <param name="Minimum">Inclusive lower bound the game clamps to, for integer keys.</param>
public sealed record AtxKeyInfo(
    string Name,
    AtxKeyScope Scope,
    AtxValueKind Kind,
    int Order,
    string Label,
    string Summary,
    string Details,
    string? DefaultText = null,
    string? RuntimeEvent = null,
    long? Minimum = null);

/// <summary>A <c>format</c> token, its aliases, and the engine format it selects.</summary>
public sealed record AtxFormatToken(string Token, string Alias, EngineFormat Format, string Description)
{
    /// <summary>Both spellings of the token, canonical first.</summary>
    public IEnumerable<string> AllSpellings => [Token, Alias];
}

/// <summary>A surface material token and its stock RF material index.</summary>
public sealed record AtxMaterialToken(string Token, int Index, string Description);

/// <summary>How the controller advances frames. Values match <c>AtxSpec::AnimationMode</c>.</summary>
public enum AtxAnimationMode
{
    /// <summary>No auto playback; frames change only through <c>ATX_Set_Frame</c>.</summary>
    Static = 0,
    /// <summary>Forward to the last frame, then backward to the first, repeating.</summary>
    PingPong = 1,
    /// <summary>Forward; wraps to frame 0 after the last, repeating.</summary>
    Loop = 2,
    /// <summary>Forward; stops and holds on the last frame.</summary>
    PlayOnce = 3,
}

/// <summary>An animation mode with its user-facing text.</summary>
public sealed record AtxAnimationModeInfo(AtxAnimationMode Mode, string Label, string Description);

/// <summary>
/// The single source of truth for the ATX format: every key, type, default, limit and enum token,
/// plus the user-facing documentation for each. The linter, completion, hover docs, GUI labels and
/// the Help reference all read from here — when the format grows, this file changes first.
/// </summary>
public static class AtxSchema
{
    /// <summary>The <c>[header]</c> table name.</summary>
    public const string HeaderTable = "header";

    /// <summary>The <c>[[frame]]</c> array-of-tables name.</summary>
    public const string FrameArray = "frame";

    // Key names (mirrors spec.h).
    public const string KeyFrameTime = "frame_time";
    public const string KeyInitiallyOn = "initially_on";
    public const string KeyAnimationMode = "animation_mode";
    public const string KeyFormat = "format";
    public const string KeyAlphaMask = "alpha_mask";
    public const string KeyMaterial = "material";
    public const string KeyFile = "file";

    /// <summary>Lower bound the parser clamps frame times to (<c>ATX_MIN_FRAME_TIME_MS</c>).</summary>
    public const int MinFrameTimeMs = 1;

    /// <summary>Default time between frames when <c>frame_time</c> is absent.</summary>
    public const int DefaultFrameTimeMs = 100;

    /// <summary>Default for <c>initially_on</c>.</summary>
    public const bool DefaultInitiallyOn = true;

    /// <summary>Default animation mode when <c>animation_mode</c> is absent.</summary>
    public const AtxAnimationMode DefaultAnimationMode = AtxAnimationMode.Static;

    /// <summary>
    /// Longest bitmap name the engine can hold: its name buffer is 32 bytes including the
    /// terminator, so 31 characters is the practical limit.
    /// </summary>
    public const int MaxBitmapNameLength = 31;

    /// <summary>
    /// Every extension <c>bm_read_header</c> can resolve a texture from, in precedence order
    /// (<c>g_texture_extensions</c> in bmpman.cpp).
    /// </summary>
    public static IReadOnlyList<string> TextureExtensions { get; } =
        [".atx", ".dds", ".png", ".jpg", ".jpeg", ".vbm", ".tga", ".pcx", ".vaf", ".m2v"];

    /// <summary>
    /// Extensions the engine probes as siblings before falling back to the literal name — the
    /// supersede chain minus <c>.atx</c> (nesting is rejected).
    /// </summary>
    public static IReadOnlyList<string> SupersedeProbeExtensions { get; } =
        [".dds", ".png", ".jpg", ".jpeg"];

    /// <summary>
    /// Extensions ATX Workbench can decode itself, so it can show a thumbnail and check the size
    /// and format. The engine resolves more than these (.pcx, .vaf, .m2v); those are accepted by
    /// ATX028 but cannot be previewed or compared against frame 0.
    /// </summary>
    public static IReadOnlyList<string> ReadableExtensions { get; } =
        [".dds", ".png", ".jpg", ".jpeg", ".vbm", ".tga"];

    /// <summary>Accepted <c>format</c> tokens (case-insensitive), in schema order.</summary>
    public static IReadOnlyList<AtxFormatToken> FormatTokens { get; } =
    [
        new("565", "565_rgb", EngineFormat.Rgb565,
            "16-bit colour, no transparency. Smallest, slight colour banding."),
        new("4444", "4444_argb", EngineFormat.Argb4444,
            "16-bit colour with 16 levels of transparency. Good for soft-edged effects on a budget."),
        new("1555", "1555_argb", EngineFormat.Argb1555,
            "16-bit colour with on/off transparency only. Good for cut-out shapes."),
        new("888", "888_rgb", EngineFormat.Rgb888,
            "24-bit colour, no transparency. Full colour fidelity."),
        new("8888", "8888_argb", EngineFormat.Argb8888,
            "32-bit colour with 256 levels of transparency. Best quality, most memory."),
    ];

    /// <summary>Accepted <c>material</c> tokens (case-insensitive) with their RF material indices.</summary>
    public static IReadOnlyList<AtxMaterialToken> Materials { get; } =
    [
        new("default", 0, "Let the engine decide from the texture name."),
        new("rock", 1, "Stone, concrete, cave walls."),
        new("metal", 2, "Panels, grates, machinery."),
        new("flesh", 3, "Organic surfaces."),
        new("water", 4, "Water surfaces."),
        new("lava", 5, "Lava surfaces."),
        new("solid", 6, "Generic hard surface."),
        new("sand", 7, "Sand, gravel, dirt."),
        new("ice", 8, "Ice and frost."),
        new("glass", 9, "Glass and transparent panels."),
    ];

    /// <summary>The four animation modes with their user-facing descriptions.</summary>
    public static IReadOnlyList<AtxAnimationModeInfo> AnimationModes { get; } =
    [
        new(AtxAnimationMode.Static, "Static",
            "Frames never advance on their own. Change frames from level events with ATX_Set_Frame."),
        new(AtxAnimationMode.PingPong, "Ping-Pong",
            "Plays forward to the last frame, then backward to the first, over and over."),
        new(AtxAnimationMode.Loop, "Loop",
            "Plays forward and starts again at frame 0 after the last frame."),
        new(AtxAnimationMode.PlayOnce, "Play Once",
            "Plays forward once and holds on the last frame."),
    ];

    /// <summary>Every key in the format, header keys first, each in emit order.</summary>
    public static IReadOnlyList<AtxKeyInfo> Keys { get; } =
    [
        new(KeyFrameTime, AtxKeyScope.Header, AtxValueKind.Integer, 0,
            "Frame time",
            "Default time each frame is shown, in milliseconds.",
            "Applies to every frame that does not set its own frame_time. Values below 1 are "
            + "raised to 1 by the game. Omit the key to get 100 ms.",
            "100 ms", "ATX_Set_Frame_Time", MinFrameTimeMs),

        new(KeyInitiallyOn, AtxKeyScope.Header, AtxValueKind.Boolean, 1,
            "Start playing on level load",
            "Whether the animation is already playing when the level loads.",
            "Ignored when animation_mode is Static, because Static never advances by itself. "
            + "Set it to false to have the animation wait for an ATX_Play event.",
            "true", "ATX_Play / ATX_Pause"),

        new(KeyAnimationMode, AtxKeyScope.Header, AtxValueKind.Integer, 2,
            "Animation mode",
            "How the frames advance: 0 Static, 1 Ping-Pong, 2 Loop, 3 Play Once.",
            "Any other number falls back to Static and the game logs a warning.",
            "0 (Static)", "ATX_Set_Frame", 0),

        new(KeyFormat, AtxKeyScope.Header, AtxValueKind.String, 3,
            "Pixel format",
            "Converts every frame into this pixel format after loading.",
            "Only works on uncompressed images. DXT-compressed frames (.dds) cannot be converted "
            + "and the ATX will fail to load. Omit the key to keep each frame's source format.",
            "keep the source format"),

        new(KeyAlphaMask, AtxKeyScope.Header, AtxValueKind.String, 4,
            "Alpha mask",
            "An 8-bit greyscale image used as the transparency of every frame.",
            "Its width, height and mip count must match frame 0. If the chosen format has no "
            + "alpha the game promotes it automatically (565 becomes 4444, 888 becomes 8888).",
            "none"),

        new(KeyMaterial, AtxKeyScope.Header, AtxValueKind.String, 5,
            "Material",
            "Surface material reported for footsteps, bullet impacts and decals.",
            "Overrides whatever the engine would derive from the texture name. A frame can "
            + "override this for itself.",
            "not set (engine decides)"),

        new(KeyFile, AtxKeyScope.Frame, AtxValueKind.String, 0,
            "File",
            "The image file for this frame.",
            "Required. Must be a bare filename with no folder path, because RF's file system is "
            + "flat. It cannot be another .atx file.",
            null),

        new(KeyFrameTime, AtxKeyScope.Frame, AtxValueKind.Integer, 1,
            "Frame time",
            "How long this one frame is shown, in milliseconds.",
            "Overrides the texture's frame_time for this frame only. Values below 1 are raised to 1.",
            "inherit the texture frame time", "ATX_Set_Frame_Time", MinFrameTimeMs),

        new(KeyMaterial, AtxKeyScope.Frame, AtxValueKind.String, 2,
            "Material",
            "Surface material reported while this frame is showing.",
            "Overrides the texture-wide material for this frame only.",
            "inherit the texture material"),
    ];

    /// <summary>Keys valid in <c>[header]</c>, in emit order.</summary>
    public static IReadOnlyList<AtxKeyInfo> HeaderKeys { get; } =
        [.. Keys.Where(k => k.Scope == AtxKeyScope.Header).OrderBy(k => k.Order)];

    /// <summary>Keys valid in a <c>[[frame]]</c> entry, in emit order.</summary>
    public static IReadOnlyList<AtxKeyInfo> FrameKeys { get; } =
        [.. Keys.Where(k => k.Scope == AtxKeyScope.Frame).OrderBy(k => k.Order)];

    /// <summary>Looks a key up by name and scope. TOML keys are case-sensitive, and so is this.</summary>
    public static AtxKeyInfo? FindKey(AtxKeyScope scope, string name) =>
        Keys.FirstOrDefault(k => k.Scope == scope && k.Name == name);

    /// <summary>Emit order for a key, or <see cref="int.MaxValue"/> if it is not in the schema.</summary>
    public static int KeyOrder(AtxKeyScope scope, string name) =>
        FindKey(scope, name)?.Order ?? int.MaxValue;

    /// <summary>
    /// Port of <c>atx_parse_format_token</c>. Case-insensitive; returns null for unknown tokens.
    /// </summary>
    public static AtxFormatToken? ParseFormatToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        return FormatTokens.FirstOrDefault(f =>
            string.Equals(f.Token, token, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(f.Alias, token, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Port of <c>parse_material_name</c>. Case-insensitive; returns null for unknown tokens.</summary>
    public static AtxMaterialToken? ParseMaterial(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        return Materials.FirstOrDefault(m =>
            string.Equals(m.Token, token, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Maps an in-range integer to an animation mode; out-of-range values return null.</summary>
    public static AtxAnimationMode? ParseAnimationMode(long value) =>
        value is >= 0 and <= 3 ? (AtxAnimationMode)(int)value : null;

    /// <summary>Human-readable label for a mode, e.g. "Ping-Pong".</summary>
    public static string AnimationModeLabel(AtxAnimationMode mode) =>
        AnimationModes.First(m => m.Mode == mode).Label;

    /// <summary>Every accepted <c>format</c> spelling, for "valid tokens are …" messages.</summary>
    public static IReadOnlyList<string> AllFormatSpellings { get; } =
        [.. FormatTokens.SelectMany(f => f.AllSpellings)];

    /// <summary>Every accepted <c>material</c> token.</summary>
    public static IReadOnlyList<string> AllMaterialTokens { get; } =
        [.. Materials.Select(m => m.Token)];

    /// <summary>True when <paramref name="fileName"/> ends with a recognised texture extension.</summary>
    public static bool HasTextureExtension(string fileName) =>
        TextureExtensions.Any(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>Port of <c>bm_strip_texture_ext</c>: removes a recognised texture extension.</summary>
    public static string StripTextureExtension(string fileName)
    {
        foreach (string ext in TextureExtensions)
        {
            if (fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return fileName[..^ext.Length];
        }
        return fileName;
    }
}
