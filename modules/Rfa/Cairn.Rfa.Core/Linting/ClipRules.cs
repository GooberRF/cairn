using System.Collections.Immutable;

namespace Cairn.Rfa.Linting;

/// <summary>What a lint rule needs besides the document itself.</summary>
public enum RuleContext
{
    /// <summary>Decided from the document alone (synchronous, runs after every edit).</summary>
    None,
    /// <summary>Needs the preview mesh (its skeleton or LOD 0).</summary>
    PreviewMesh,
    /// <summary>Needs the game's tables (the clip usage index).</summary>
    Tables,
    /// <summary>Needs the asset library (other clips and meshes the resolver can see).</summary>
    Library,
    /// <summary>Needs the file name the document is saved under.</summary>
    FileName,
    /// <summary>Needs the asset resolver (textures).</summary>
    Resolver,
}

/// <summary>One rule's identity and policy, for the Problems panel's filters and the Help reference.</summary>
/// <param name="Code">Stable id.</param>
/// <param name="Severity">Severity the rule reports.</param>
/// <param name="Title">Short name of the rule.</param>
/// <param name="Context">What it needs to run.</param>
public sealed record RuleInfo(string Code, DiagnosticSeverity Severity, string Title, RuleContext Context);

/// <summary>
/// Every clip rule id, so call sites never spell a code by hand. Severity policy (DESIGN.md
/// "Problems"): Error = the engine misbehaves or the clip cannot be written; Warning = loads but not as
/// intended; Info = tidy-up. Each rule was checked against all 1009 stock clips: none reports an Error
/// on stock data when the clip is checked against its own skeleton.
/// <code>
/// Code    Severity  Rule                                                     Context
/// RFA001  Error     Version other than 7 or 8                                -
/// RFA002  Error     Bone count differs from the preview mesh                 preview mesh
/// RFA003  Error     Bone without position keys (collapses onto its parent)   -
/// RFA004  Warning   Bone without rotation keys (identity rotation)           -
/// RFA005  Error     Key times not strictly increasing                        -
/// RFA006  Error     Key outside [start, end]                                 -
/// RFA007  Error     More than 50 bones                                       -
/// RFA008  Error     End time before start time                               -
/// RFA009  Error     Cannot be written (morph arrays inconsistent, &gt;32767 keys) -
/// RFA010  Error     Morph vertex index beyond the mesh's LOD 0 vertices      preview mesh
/// RFA011  Error     Non-finite number (NaN or infinity)                      -
/// RFA012  Error     Rotation key with all four components zero               -
/// RFA013  Error     The tables play the clip on a mesh with another bone count  tables + library
/// RFA014  Error     File name longer than 59 characters                      file name
/// RFA015  Error     Exactly one rotation key (the engine reads past the track) -
/// RFA016  Error     Morph data the engine cannot time (no keyframes; v7 with no length) -
/// RFA020  Warning   Bezier control points left at (0,0,0)                    -
/// RFA021  Warning   Non-unit quaternion (off by more than 0.002)             -
/// RFA022  Warning   Ramps longer than the clip                               -
/// RFA023  Warning   Bone lengths differ from the mesh's other clips          library
/// RFA024  Warning   Base name collides with another clip the game can see    library
/// RFA025  Warning   Bone weight above 10                                     -
/// RFA026  Warning   Zero-length clip (start = end)                           -
/// RFA027  Warning   Negative ramp                                            -
/// RFA028  Warning   Rotation segment the engine does not interpolate (snaps)  -
/// RFA030  Info      Consecutive rotation keys in opposite hemispheres        -
/// RFA031  Info      Non-zero pad word in a rotation key                      -
/// </code>
/// </summary>
public static class ClipRules
{
    /// <summary>RFA001.</summary>
    public const string UnsupportedVersion = "RFA001";
    /// <summary>RFA002.</summary>
    public const string BoneCountMismatch = "RFA002";
    /// <summary>RFA003.</summary>
    public const string NoPositionKeys = "RFA003";
    /// <summary>RFA004.</summary>
    public const string NoRotationKeys = "RFA004";
    /// <summary>RFA005.</summary>
    public const string KeyTimesNotIncreasing = "RFA005";
    /// <summary>RFA006.</summary>
    public const string KeyOutsideRange = "RFA006";
    /// <summary>RFA007.</summary>
    public const string TooManyBones = "RFA007";
    /// <summary>RFA008.</summary>
    public const string EndBeforeStart = "RFA008";
    /// <summary>RFA009.</summary>
    public const string NotWritable = "RFA009";
    /// <summary>RFA010.</summary>
    public const string MorphIndexBeyondMesh = "RFA010";
    /// <summary>RFA011.</summary>
    public const string NonFinite = "RFA011";
    /// <summary>RFA012.</summary>
    public const string ZeroQuaternion = "RFA012";
    /// <summary>RFA013.</summary>
    public const string TableMeshBoneCount = "RFA013";
    /// <summary>RFA014.</summary>
    public const string FileNameTooLong = "RFA014";
    /// <summary>RFA015.</summary>
    public const string SingleRotationKey = "RFA015";
    /// <summary>RFA016.</summary>
    public const string MorphUnplayable = "RFA016";
    /// <summary>RFA020.</summary>
    public const string ZeroControlPoints = "RFA020";
    /// <summary>RFA021.</summary>
    public const string NonUnitQuaternion = "RFA021";
    /// <summary>RFA022.</summary>
    public const string RampsLongerThanClip = "RFA022";
    /// <summary>RFA023.</summary>
    public const string ProportionsDiffer = "RFA023";
    /// <summary>RFA024.</summary>
    public const string NameCollision = "RFA024";
    /// <summary>RFA025.</summary>
    public const string WeightAboveTen = "RFA025";
    /// <summary>RFA026.</summary>
    public const string ZeroLength = "RFA026";
    /// <summary>RFA027.</summary>
    public const string NegativeRamp = "RFA027";
    /// <summary>RFA028.</summary>
    public const string SegmentSnaps = "RFA028";
    /// <summary>
    /// RFA028 fires when the engine's slerp would snap a segment whose keys are further apart than this
    /// (degrees). Stock data never exceeds 0.021 degrees.
    /// </summary>
    public const float SnapReportDegrees = 0.05f;
    /// <summary>RFA030.</summary>
    public const string SignDiscontinuity = "RFA030";
    /// <summary>RFA031.</summary>
    public const string NonZeroPad = "RFA031";

    /// <summary>The longest clip file name (with extension) the engine's 60-byte name buffer in <c>skeleton_find</c> holds.</summary>
    public const int MaxFileNameLength = 59;

    /// <summary>How far a stored quaternion's length (after /16383) may be from 1 before RFA021 fires.</summary>
    public const float UnitTolerance = 0.002f;

    /// <summary>Every clip rule with its policy.</summary>
    public static ImmutableArray<RuleInfo> All { get; } =
    [
        new(UnsupportedVersion, DiagnosticSeverity.Error, "Unsupported version", RuleContext.None),
        new(BoneCountMismatch, DiagnosticSeverity.Error, "Bone count differs from the preview mesh", RuleContext.PreviewMesh),
        new(NoPositionKeys, DiagnosticSeverity.Error, "Bone without position keys", RuleContext.None),
        new(NoRotationKeys, DiagnosticSeverity.Warning, "Bone without rotation keys", RuleContext.None),
        new(KeyTimesNotIncreasing, DiagnosticSeverity.Error, "Key times not increasing", RuleContext.None),
        new(KeyOutsideRange, DiagnosticSeverity.Error, "Key outside the clip's range", RuleContext.None),
        new(TooManyBones, DiagnosticSeverity.Error, "More than 50 bones", RuleContext.None),
        new(EndBeforeStart, DiagnosticSeverity.Error, "End before start", RuleContext.None),
        new(NotWritable, DiagnosticSeverity.Error, "Clip cannot be written", RuleContext.None),
        new(MorphIndexBeyondMesh, DiagnosticSeverity.Error, "Morph vertex beyond the mesh", RuleContext.PreviewMesh),
        new(NonFinite, DiagnosticSeverity.Error, "Not a number", RuleContext.None),
        new(ZeroQuaternion, DiagnosticSeverity.Error, "Zero rotation key", RuleContext.None),
        new(TableMeshBoneCount, DiagnosticSeverity.Error, "Table plays the clip on a different skeleton", RuleContext.Tables),
        new(FileNameTooLong, DiagnosticSeverity.Error, "File name too long", RuleContext.FileName),
        new(SingleRotationKey, DiagnosticSeverity.Error, "Only one rotation key", RuleContext.None),
        new(MorphUnplayable, DiagnosticSeverity.Error, "Morph data the engine cannot time", RuleContext.None),
        new(ZeroControlPoints, DiagnosticSeverity.Warning, "Control points at zero", RuleContext.None),
        new(NonUnitQuaternion, DiagnosticSeverity.Warning, "Non-unit quaternion", RuleContext.None),
        new(RampsLongerThanClip, DiagnosticSeverity.Warning, "Ramps longer than the clip", RuleContext.None),
        new(ProportionsDiffer, DiagnosticSeverity.Warning, "Bone lengths differ from the mesh's other clips", RuleContext.Library),
        new(NameCollision, DiagnosticSeverity.Warning, "Clip name collides", RuleContext.Library),
        new(WeightAboveTen, DiagnosticSeverity.Warning, "Weight above 10", RuleContext.None),
        new(ZeroLength, DiagnosticSeverity.Warning, "Zero-length clip", RuleContext.None),
        new(NegativeRamp, DiagnosticSeverity.Warning, "Negative ramp", RuleContext.None),
        new(SegmentSnaps, DiagnosticSeverity.Warning, "Segment does not interpolate", RuleContext.None),
        new(SignDiscontinuity, DiagnosticSeverity.Info, "Sign flip between keys", RuleContext.None),
        new(NonZeroPad, DiagnosticSeverity.Info, "Non-zero pad", RuleContext.None),
    ];

    /// <summary>The policy of one rule, or null for an unknown code.</summary>
    public static RuleInfo? Find(string code) => All.FirstOrDefault(r => r.Code == code);
}
