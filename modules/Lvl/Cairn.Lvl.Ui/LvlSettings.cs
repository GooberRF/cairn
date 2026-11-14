using Cairn.Lvl.Conversion;
using Cairn.Lvl.Model;
using Cairn.Lvl.Ui.Preview;
using Cairn.Ui.Modules;

namespace Cairn.Lvl.Ui;

/// <summary>How the 3D preview lights the faces (no lightmaps are drawn).</summary>
public enum LvlLighting
{
    /// <summary>Every face at its texture's own colours.</summary>
    FullBright,
    /// <summary>Every face at the level's ambient light (full-bright faces at their own colours).</summary>
    LevelAmbient,
    /// <summary>A light from the camera's side, to read the shapes (full-bright faces at their own colours).</summary>
    Shaded,
}

/// <summary>Whether the 3D preview draws the faces' edges.</summary>
public enum LvlEdges
{
    /// <summary>The faces only.</summary>
    Off,
    /// <summary>The faces with their edges over them.</summary>
    OverFaces,
    /// <summary>The edges only (a wireframe).</summary>
    Only,
}

/// <summary>Where the Convert window writes.</summary>
public enum LvlOutputPlace
{
    /// <summary>Into the packfile the source is an entry of, as new entries (one undo step).</summary>
    IntoPackfile,
    /// <summary>Into the source's own folder (or its packfile's).</summary>
    NextToSource,
    /// <summary>Into a chosen folder.</summary>
    Folder,
}

/// <summary>
/// The levels module's remembered choices ("lvl." keys): the preview's toggles and size limit, and what the Convert
/// window offers first. Diagnostic runs never write them (the module checks before setting).
/// </summary>
public sealed class LvlSettings(Func<ModuleSettings?> store)
{
    /// <summary>The preview's default size limit: above this many triangles it asks before building.</summary>
    public const int DefaultTriangleLimit = 300_000;

    /// <summary>Above this many triangles the preview asks before building.</summary>
    public int TriangleLimit
    {
        get => Math.Clamp(store()?.Get("triangleLimit", DefaultTriangleLimit) ?? DefaultTriangleLimit, 1_000, 50_000_000);
        set => store()?.Set("triangleLimit", Math.Clamp(value, 1_000, 50_000_000));
    }

    /// <summary>The face classes the preview hides.</summary>
    public RfFaceClass HiddenFaceClasses
    {
        get => (RfFaceClass)(store()?.Get("hiddenFaceClasses", (int)LvlSceneSettings.Default.HiddenClasses) ?? (int)LvlSceneSettings.Default.HiddenClasses) & AllClasses;
        set => store()?.Set("hiddenFaceClasses", (int)(value & AllClasses));
    }

    /// <summary>True when the preview draws faces in no class.</summary>
    public bool ShowNormalFaces
    {
        get => store()?.Get("showNormalFaces", true) ?? true;
        set => store()?.Set("showNormalFaces", value);
    }

    /// <summary>The object markers the preview draws.</summary>
    internal LvlMarkerKinds Markers
    {
        get => (LvlMarkerKinds)(store()?.Get("markers", (int)LvlMarkerKinds.Default) ?? (int)LvlMarkerKinds.Default) & LvlMarkerKinds.All;
        set => store()?.Set("markers", (int)(value & LvlMarkerKinds.All));
    }

    /// <summary>How the preview lights the faces.</summary>
    public LvlLighting Lighting
    {
        get => store()?.Get("lighting", LvlLighting.FullBright) is { } l && Enum.IsDefined(l) ? l : LvlLighting.FullBright;
        set => store()?.Set("lighting", value);
    }

    /// <summary>True to draw both sides of each face (the game draws the front only).</summary>
    public bool BothSides
    {
        get => store()?.Get("bothSides", false) ?? false;
        set => store()?.Set("bothSides", value);
    }

    /// <summary>Whether the preview draws the faces' edges.</summary>
    public LvlEdges Edges
    {
        get => store()?.Get("edges", LvlEdges.Off) is { } e && Enum.IsDefined(e) ? e : LvlEdges.Off;
        set => store()?.Set("edges", value);
    }

    /// <summary>True to show the counts (faces, triangles, brushes…) in a corner of the preview.</summary>
    public bool ShowStatistics
    {
        get => store()?.Get("statistics", false) ?? false;
        set => store()?.Set("statistics", value);
    }

    /// <summary>The Convert window's last target.</summary>
    public LvlConvertTarget Target
    {
        get => store()?.Get("target", LvlConvertTarget.Glb) is { } t && Enum.IsDefined(t) ? t : LvlConvertTarget.Glb;
        set => store()?.Set("target", value);
    }

    /// <summary>Where the Convert window last wrote.</summary>
    public LvlOutputPlace Place
    {
        get => store()?.Get("place", LvlOutputPlace.Folder) is { } p && Enum.IsDefined(p) ? p : LvlOutputPlace.Folder;
        set => store()?.Set("place", value);
    }

    /// <summary>The folder last chosen ("" for none).</summary>
    public string Folder
    {
        get => store()?.Get("folder", string.Empty) ?? string.Empty;
        set => store()?.Set("folder", value ?? string.Empty);
    }

    /// <summary>The conversion options last used (target, geometry, faces, objects, per-target choices); never the resolver.</summary>
    public LvlConvertOptions Options
    {
        get
        {
            var dto = store()?.Get<LvlOptionsDto>("options");
            return dto is null ? new LvlConvertOptions { Target = Target } : dto.ToOptions(Target);
        }
        set
        {
            store()?.Set("options", LvlOptionsDto.From(value));
            Target = value.Target;
        }
    }

    /// <summary>
    /// Red Faction metres per Red Faction for N-Gage unit: what N-Gage levels are previewed and (unless the Convert
    /// window says otherwise) converted at. The Convert window's choice, remembered with its other options.
    /// </summary>
    public float XffScale => Options.Xff.Scale;

    /// <summary>What meshes, glTF and OBJ files last became in a group file (brush kind, level of detail, objects).</summary>
    public RfgImportOptions RfgImport
    {
        get
        {
            var kind = store()?.Get("rfgBrushKind", RfgBrushKind.Auto) is { } k && Enum.IsDefined(k) ? k : RfgBrushKind.Auto;
            int lod = Math.Clamp(store()?.Get("rfgLod", 0) ?? 0, 0, 16);
            var objects = (LvlObjectKinds)(store()?.Get("rfgObjects", (int)LvlObjectKinds.All) ?? (int)LvlObjectKinds.All) & LvlObjectKinds.All;
            return new RfgImportOptions { BrushKind = kind, Lod = lod, Objects = objects };
        }
        set
        {
            store()?.Set("rfgBrushKind", value.BrushKind);
            store()?.Set("rfgLod", Math.Clamp(value.Lod, 0, 16));
            store()?.Set("rfgObjects", (int)(value.Objects & LvlObjectKinds.All));
        }
    }

    private const RfFaceClass AllClasses =RfFaceClass.Portal | RfFaceClass.Detail | RfFaceClass.Sky | RfFaceClass.Invisible | RfFaceClass.Hole
        | RfFaceClass.LiquidSurface | RfFaceClass.Alpha | RfFaceClass.FullBright | RfFaceClass.Scrolling;

    /// <summary>The persisted shape of <see cref="LvlConvertOptions"/> (plain values only).</summary>
    public sealed class LvlOptionsDto
    {
        public LvlGeometryChoice Geometry { get; set; }
        public int ExcludedFaceClasses { get; set; } = (int)LvlConvertOptions.DefaultExcludedFaceClasses;
        public bool IncludeNormalFaces { get; set; } = true;
        public bool IncludeMovers { get; set; } = true;
        public int Objects { get; set; } = (int)LvlObjectKinds.All;
        public bool ExportTextures { get; set; } = true;
        public LvlBrushTransformMode BrushTransforms { get; set; }
        public bool IncludeLightmapUvs { get; set; } = true;
        public bool ObjTriangulate { get; set; }
        public bool ObjMetadata { get; set; } = true;
        public LvlV3mGrouping V3mGrouping { get; set; }
        public Rf2Dto? Rf2 { get; set; }
        public XffDto? Xff { get; set; }

        public static LvlOptionsDto From(LvlConvertOptions o) => new()
        {
            Geometry = o.Geometry,
            ExcludedFaceClasses = (int)o.ExcludedFaceClasses,
            IncludeNormalFaces = o.IncludeNormalFaces,
            IncludeMovers = o.IncludeMovers,
            Objects = (int)o.Objects,
            ExportTextures = o.ExportTextures,
            BrushTransforms = o.BrushTransforms,
            IncludeLightmapUvs = o.IncludeLightmapUvs,
            ObjTriangulate = o.ObjTriangulate,
            ObjMetadata = o.ObjMetadata,
            V3mGrouping = o.V3mGrouping,
            Rf2 = Rf2Dto.From(o.Rf2),
            Xff = new XffDto { Scale = o.Xff.Scale, CopyTextures = o.Xff.CopyTextures },
        };

        public LvlConvertOptions ToOptions(LvlConvertTarget target) => new()
        {
            Target = target,
            Geometry = Enum.IsDefined(Geometry) ? Geometry : LvlGeometryChoice.Default,
            ExcludedFaceClasses = (RfFaceClass)ExcludedFaceClasses & AllClasses,
            IncludeNormalFaces = IncludeNormalFaces,
            IncludeMovers = IncludeMovers,
            Objects = (LvlObjectKinds)Objects & LvlObjectKinds.All,
            ExportTextures = ExportTextures,
            BrushTransforms = Enum.IsDefined(BrushTransforms) ? BrushTransforms : LvlBrushTransformMode.NodeTransform,
            IncludeLightmapUvs = IncludeLightmapUvs,
            ObjTriangulate = ObjTriangulate,
            ObjMetadata = ObjMetadata,
            V3mGrouping = Enum.IsDefined(V3mGrouping) ? V3mGrouping : LvlV3mGrouping.PerBrush,
            Rf2 = Rf2?.ToOptions() ?? new LvlRf2Options(),
            Xff = Xff?.ToOptions() ?? new LvlXffOptions(),
        };
    }

    /// <summary>The persisted shape of <see cref="LvlXffOptions"/>.</summary>
    public sealed class XffDto
    {
        public float Scale { get; set; } = Cairn.Formats.XForge.XffSpace.RfScale;
        public bool CopyTextures { get; set; } = true;

        /// <summary>The options; a stored scale out of range (damaged or hand-edited) is not remembered, so the default is used, as the Meshes module does.</summary>
        public LvlXffOptions ToOptions() => new() { Scale = Cairn.Formats.XForge.XffSpace.IsValidScale(Scale) ? Scale : Cairn.Formats.XForge.XffSpace.RfScale, CopyTextures = CopyTextures };
    }

    /// <summary>The persisted shape of <see cref="LvlRf2Options"/> (its plain values; the packs and clutter table come from the source).</summary>
    public sealed class Rf2Dto
    {
        public LvlRf2TextureMode Textures { get; set; }
        public float LightScale { get; set; } = 1f;
        public float LightRangeScale { get; set; } = 3f;
        public bool ApplyLightmapMultiplier { get; set; } = true;
        public bool CoronasToLights { get; set; } = true;
        public float? CoronaRange { get; set; }
        public float? CoronaScale { get; set; }
        public bool FillLights { get; set; } = true;
        public bool KeepCoronas { get; set; } = true;
        public bool ClutterAsMeshes { get; set; } = true;
        public bool GeoableWithoutDetail { get; set; }
        public bool DropInvisibleTriggerBrushes { get; set; } = true;
        public float JumpPadScale { get; set; } = LvlRf2Options.DefaultJumpPadScale;

        public static Rf2Dto From(LvlRf2Options o) => new()
        {
            Textures = o.Textures,
            LightScale = o.LightScale,
            LightRangeScale = o.LightRangeScale,
            ApplyLightmapMultiplier = o.ApplyLightmapMultiplier,
            CoronasToLights = o.CoronasToLights,
            CoronaRange = o.CoronaRange,
            CoronaScale = o.CoronaScale,
            FillLights = o.FillLights,
            KeepCoronas = o.KeepCoronas,
            ClutterAsMeshes = o.ClutterAsMeshes,
            GeoableWithoutDetail = o.GeoableWithoutDetail,
            DropInvisibleTriggerBrushes = o.DropInvisibleTriggerBrushes,
            JumpPadScale = o.JumpPadScale,
        };

        public LvlRf2Options ToOptions()
        {
            // a damaged or hand-edited value falls back to REDUX's default
            var d = new LvlRf2Options();
            static float Pos(float v, float fallback) => float.IsFinite(v) && v > 0 ? v : fallback;
            static float? Opt(float? v) => v is { } x && float.IsFinite(x) && x > 0 ? x : null;
            return d with
            {
                Textures = Enum.IsDefined(Textures) ? Textures : d.Textures,
                LightScale = Pos(LightScale, d.LightScale),
                LightRangeScale = Pos(LightRangeScale, d.LightRangeScale),
                ApplyLightmapMultiplier = ApplyLightmapMultiplier,
                CoronasToLights = CoronasToLights,
                CoronaRange = Opt(CoronaRange),
                CoronaScale = Opt(CoronaScale),
                FillLights = FillLights,
                KeepCoronas = KeepCoronas,
                ClutterAsMeshes = ClutterAsMeshes,
                GeoableWithoutDetail = GeoableWithoutDetail,
                DropInvisibleTriggerBrushes = DropInvisibleTriggerBrushes,
                JumpPadScale = Pos(JumpPadScale, d.JumpPadScale),
            };
        }
    }
}
