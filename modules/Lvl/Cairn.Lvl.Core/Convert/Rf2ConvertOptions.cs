using Cairn.Assets;

namespace Cairn.Lvl.Conversion;

/// <summary>What a Red Faction II level conversion does with the RF2 texture names.</summary>
public enum LvlRf2TextureMode
{
    /// <summary>Rename textures to the Red Faction names of REDUX's translation tables (names without a translation stay).</summary>
    Translate,
    /// <summary>Keep the RF2 names and write the RF2 textures from the level's <c>.peg</c> packs beside the output.</summary>
    KeepOriginals,
}

/// <summary>
/// Red Faction II level options: texture names, how RF2's lights and coronas become Red Faction lights, and what
/// happens to RF2-only brushes and objects. The defaults are REDUX's (with texture translation on). Every change a
/// conversion makes because of these is listed in its report.
/// </summary>
public sealed partial record LvlRf2Options
{
    /// <summary>REDUX's jump pad scale: RF2's gravity is 15 against Red Faction's 9.8, so the same jump height needs sqrt(9.8 / 15) of the push.</summary>
    public const float DefaultJumpPadScale = 0.8083f;

    /// <summary>The corona light range used when the per-level fit has too few samples (REDUX's 40 m).</summary>
    public const float FallbackCoronaRange = 40f;

    /// <summary>Texture names: translated (the default) or kept with the textures written beside the output.</summary>
    public LvlRf2TextureMode Textures { get; init; } = LvlRf2TextureMode.Translate;

    /// <summary>
    /// The <c>.peg</c> texture packs the level's textures are read from, its own <c>&lt;level&gt;.peg</c> first (see
    /// <see cref="Rf2LevelTextures"/>). Null: no RF2 pixels are available, so only texture names are converted.
    /// </summary>
    public PegTextureSet? TexturePacks { get; init; }

    /// <summary>Multiplies every RF2 light's intensity (REDUX's <c>-rf2lightscale</c>, default 1).</summary>
    public float LightScale { get; init; } = 1f;

    /// <summary>
    /// Multiplies every RF2 light's range: RF2's range is where light falls to half (inverse square), Red Faction's
    /// where it reaches zero (linear); 3 puts the cutoff where RF2 is down to about 10% (REDUX).
    /// </summary>
    public float LightRangeScale { get; init; } = 3f;

    /// <summary>Multiply every light's intensity by the level's lightmap multiplier (REDUX does).</summary>
    public bool ApplyLightmapMultiplier { get; init; } = true;

    /// <summary>Add a Red Faction light for each coloured corona (RF2 bakes coronas into its lighting; REDUX does this).</summary>
    public bool CoronasToLights { get; init; } = true;

    /// <summary>The range of corona-derived lights; null fits it per level to the baked vertex colours (REDUX's <c>-rf2coronarange</c>).</summary>
    public float? CoronaRange { get; init; }

    /// <summary>The intensity scale of corona-derived lights; null fits it per level (REDUX's <c>-rf2coronascale</c>).</summary>
    public float? CoronaScale { get; init; }

    /// <summary>Add up to eight fill lights where the baked colours show light that no light or corona explains (REDUX does).</summary>
    public bool FillLights { get; init; } = true;

    /// <summary>Keep the coronas as Alpine coronas, with RF2's halved cone angle doubled (REDUX's <c>-alpinecoronas</c>, default on).</summary>
    public bool KeepCoronas { get; init; } = true;

    /// <summary>Turn clutter into Alpine static meshes, each class's mesh taken from <see cref="ClutterClasses"/> (REDUX does, with the class name as the mesh).</summary>
    public bool ClutterAsMeshes { get; init; } = true;

    /// <summary>The RF2 clutter classes (from RF2's <c>clutter.tbl</c>, see <see cref="Rf2ClutterTable"/>); null keeps clutter as clutter.</summary>
    public IReadOnlyDictionary<string, Rf2ClutterClass>? ClutterClasses { get; init; }

    /// <summary>Clear the detail flag of geoable detail brushes, so Red Faction can geomod them (REDUX's <c>-geonodetail</c>, default off).</summary>
    public bool GeoableWithoutDetail { get; init; }

    /// <summary>Leave out RF2's breakable invisible trigger brushes, which Red Faction has no use for (REDUX does).</summary>
    public bool DropInvisibleTriggerBrushes { get; init; } = true;

    /// <summary>Multiplies the strength of jump pads (push regions with the jump pad flag).</summary>
    public float JumpPadScale { get; init; } = DefaultJumpPadScale;
}
