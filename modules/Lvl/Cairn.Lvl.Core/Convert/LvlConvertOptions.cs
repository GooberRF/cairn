using Cairn.Assets;
using Cairn.Formats.Imaging;
using Cairn.Lvl.Model;

namespace Cairn.Lvl.Conversion;

/// <summary>What a level or group is converted to.</summary>
public enum LvlConvertTarget
{
    /// <summary>glTF 2.0 as .gltf + .bin (+ PNG textures beside it).</summary>
    Gltf,
    /// <summary>glTF 2.0 as one binary .glb (textures inside).</summary>
    Glb,
    /// <summary>Wavefront .obj + .mtl (+ PNG textures beside it).</summary>
    Obj,
    /// <summary>A static mesh (.v3m).</summary>
    V3m,
    /// <summary>An editor group file (.rfg).</summary>
    Rfg,
}

/// <summary>Which of a level's two geometries is converted.</summary>
public enum LvlGeometryChoice
{
    /// <summary>The target's default: compiled geometry for glTF, OBJ and V3M; brushes for .rfg.</summary>
    Default,
    /// <summary>The compiled geometry the game draws (rooms), plus the movers.</summary>
    Compiled,
    /// <summary>The editor brushes (mover brushes included).</summary>
    Brushes,
}

/// <summary>Where brush transforms go in glTF output (OBJ and V3M always apply them).</summary>
public enum LvlBrushTransformMode
{
    /// <summary>Vertices stay in the brush's own space and the node carries the brush's position and rotation.</summary>
    NodeTransform,
    /// <summary>Vertices are moved into world space; nodes have no transform.</summary>
    Apply,
}

/// <summary>How V3M output groups geometry into submeshes.</summary>
public enum LvlV3mGrouping
{
    /// <summary>One submesh per brush (or per room and mover for compiled geometry).</summary>
    PerBrush,
    /// <summary>One submesh for the whole level (split only where the engine's texture limit requires).</summary>
    WholeLevel,
}

/// <summary>Kinds of objects a conversion carries (glTF nodes; every kind for .rfg).</summary>
[Flags]
public enum LvlObjectKinds
{
    None = 0,
    Lights = 0x1,
    Items = 0x2,
    Clutter = 0x4,
    Entities = 0x8,
    Triggers = 0x10,
    Events = 0x20,
    /// <summary>Multiplayer respawn points and the player start.</summary>
    Spawns = 0x40,
    /// <summary>Nav points and the old waypoint lists.</summary>
    NavPoints = 0x80,
    /// <summary>Geomod, gas, push and climbing regions.</summary>
    Regions = 0x100,
    /// <summary>Ambient sounds and EAX effects.</summary>
    Sounds = 0x200,
    /// <summary>Particle and bolt emitters, decals and room effects.</summary>
    Effects = 0x400,
    /// <summary>Cutscene cameras, cutscene path nodes and targets.</summary>
    Cameras = 0x800,
    /// <summary>Alpine static meshes.</summary>
    AlpineMeshes = 0x1000,
    /// <summary>Alpine coronas.</summary>
    Coronas = 0x2000,
    /// <summary>Moving-group keyframes.</summary>
    Keyframes = 0x4000,
    All = 0x7FFF,
}

/// <summary>
/// Red Faction II level options (texture translation, light and corona handling). The RF2 level support fills this
/// in; an RF1 or Alpine conversion ignores it.
/// </summary>
public sealed partial record LvlRf2Options;

/// <summary>
/// Everything a level or group conversion can be told. Immutable; the convert dialog keeps one and replaces it with
/// <c>with</c> expressions.
/// </summary>
public sealed record LvlConvertOptions
{
    /// <summary>The output format.</summary>
    public LvlConvertTarget Target { get; init; } = LvlConvertTarget.Gltf;

    /// <summary>Compiled geometry or brushes (group files always have brushes only).</summary>
    public LvlGeometryChoice Geometry { get; init; } = LvlGeometryChoice.Default;

    /// <summary>Faces in any of these classes are left out (see <see cref="RfFaceClasses"/>).</summary>
    public RfFaceClass ExcludedFaceClasses { get; init; } = DefaultExcludedFaceClasses;

    /// <summary>Include faces in no class at all (ordinary faces).</summary>
    public bool IncludeNormalFaces { get; init; } = true;

    /// <summary>Compiled geometry only: include the movers (they are compiled separately from the rooms).</summary>
    public bool IncludeMovers { get; init; } = true;

    /// <summary>Objects to carry as glTF nodes (OBJ and V3M carry none; the report says so).</summary>
    public LvlObjectKinds Objects { get; init; } = LvlObjectKinds.All;

    /// <summary>Decode textures to PNG beside the output (inside a .glb). Off writes texture names only.</summary>
    public bool ExportTextures { get; init; } = true;

    /// <summary>Finds texture files by name (the document's folder, search folders, the game).</summary>
    public AssetResolver? TextureResolver { get; init; }

    /// <summary>
    /// Loads a texture by name, tried before <see cref="TextureResolver"/> (for example a packfile's own textures or an
    /// RF2 level's .peg). Null when it does not have the texture.
    /// </summary>
    public Func<string, BgraImage?>? TextureLoader { get; init; }

    /// <summary>Renames textures on the way out (for example RF2 to RF1 names); null keeps them.</summary>
    public Func<string, string>? TextureNameMap { get; init; }

    /// <summary>glTF: where brush transforms go.</summary>
    public LvlBrushTransformMode BrushTransforms { get; init; } = LvlBrushTransformMode.NodeTransform;

    /// <summary>glTF: write lightmap coordinates as TEXCOORD_1 (the lightmap images are not exported).</summary>
    public bool IncludeLightmapUvs { get; init; } = true;

    /// <summary>OBJ: write triangles instead of the faces' polygons.</summary>
    public bool ObjTriangulate { get; init; }

    /// <summary>OBJ: write Cairn's <c># rf_...</c> comment lines (flags, brush data) that Cairn reads back.</summary>
    public bool ObjMetadata { get; init; } = true;

    /// <summary>V3M: submesh grouping.</summary>
    public LvlV3mGrouping V3mGrouping { get; init; } = LvlV3mGrouping.PerBrush;

    /// <summary>Red Faction II options.</summary>
    public LvlRf2Options Rf2 { get; init; } = new();

    /// <summary>Red Faction for N-Gage options (scale, texture copies).</summary>
    public LvlXffOptions Xff { get; init; } = new();

    /// <summary>The asset generator string of glTF output.</summary>
    public string Generator { get; init; } = "Cairn";

    /// <summary>Portal faces and invisible faces are not drawn by the game, so they are left out by default.</summary>
    public const RfFaceClass DefaultExcludedFaceClasses = RfFaceClass.Portal | RfFaceClass.Invisible;

    /// <summary>The geometry a conversion uses once <see cref="LvlGeometryChoice.Default"/> is resolved.</summary>
    public LvlGeometryChoice ResolvedGeometry => Geometry != LvlGeometryChoice.Default ? Geometry
        : Target == LvlConvertTarget.Rfg ? LvlGeometryChoice.Brushes : LvlGeometryChoice.Compiled;

    /// <summary>The same options with one face class included or left out.</summary>
    public LvlConvertOptions WithFaceClass(RfFaceClass faceClass, bool include) => faceClass == RfFaceClass.Normal
        ? this with { IncludeNormalFaces = include }
        : this with { ExcludedFaceClasses = include ? ExcludedFaceClasses & ~faceClass : ExcludedFaceClasses | faceClass };

    /// <summary>True when a face in <paramref name="classes"/> is kept.</summary>
    public bool Keeps(RfFaceClass classes) => classes == RfFaceClass.Normal ? IncludeNormalFaces : (classes & ExcludedFaceClasses) == 0;

    /// <summary>The usual file extension of a target.</summary>
    public static string ExtensionOf(LvlConvertTarget target) => target switch
    {
        LvlConvertTarget.Gltf => ".gltf",
        LvlConvertTarget.Glb => ".glb",
        LvlConvertTarget.Obj => ".obj",
        LvlConvertTarget.V3m => ".v3m",
        LvlConvertTarget.Rfg => ".rfg",
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };

    /// <summary>The target an extension names, or null.</summary>
    public static LvlConvertTarget? TargetOf(string extension) => extension.ToLowerInvariant() switch
    {
        ".gltf" => LvlConvertTarget.Gltf,
        ".glb" => LvlConvertTarget.Glb,
        ".obj" => LvlConvertTarget.Obj,
        ".v3m" => LvlConvertTarget.V3m,
        ".rfg" => LvlConvertTarget.Rfg,
        _ => null,
    };
}
