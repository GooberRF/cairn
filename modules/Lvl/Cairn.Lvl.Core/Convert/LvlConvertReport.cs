namespace Cairn.Lvl.Conversion;

/// <summary>How much a report entry matters.</summary>
public enum LvlIssueSeverity
{
    /// <summary>Something was converted or assumed; worth knowing.</summary>
    Info,
    /// <summary>The output differs from the source in a way that may matter.</summary>
    Warning,
    /// <summary>The output breaks an engine limit, or could not be made.</summary>
    Error,
}

/// <summary>What happened to the data an entry is about.</summary>
public enum LvlIssueKind
{
    /// <summary>A plain note (what was written, where it went).</summary>
    Note,
    /// <summary>Kept, but only approximately (a light's intensity, a render state).</summary>
    Approximated,
    /// <summary>Left out of the output.</summary>
    Dropped,
    /// <summary>Split into several pieces (vertices, polygons, submeshes).</summary>
    Split,
    /// <summary>The output breaks an engine limit (texture count, name length, batch size).</summary>
    LimitBroken,
}

/// <summary>One report entry.</summary>
/// <param name="Severity">How much it matters.</param>
/// <param name="Kind">What happened to the data.</param>
/// <param name="Code">A stable code (LC001 ...).</param>
/// <param name="Message">What happened, in plain language.</param>
public sealed record LvlConvertIssue(LvlIssueSeverity Severity, LvlIssueKind Kind, string Code, string Message)
{
    public override string ToString() => $"{Code} {Severity}: {Message}";
}

/// <summary>Counts of what a conversion read and wrote.</summary>
public sealed record LvlConvertStats
{
    /// <summary>Faces looked at.</summary>
    public int SourceFaces { get; init; }
    /// <summary>Faces written.</summary>
    public int Faces { get; init; }
    /// <summary>Triangles written (glTF, V3M, triangulated OBJ); for polygon OBJ the triangles the faces make.</summary>
    public int Triangles { get; init; }
    /// <summary>Vertices written after splitting by (position, UV, normal).</summary>
    public int Vertices { get; init; }
    /// <summary>Distinct source vertices the written faces use.</summary>
    public int SourceVertices { get; init; }
    /// <summary>Brushes, rooms and movers written as nodes, objects or submeshes.</summary>
    public int Parts { get; init; }
    /// <summary>Materials written.</summary>
    public int Materials { get; init; }
    /// <summary>Texture images written.</summary>
    public int Textures { get; init; }
    /// <summary>Level objects written.</summary>
    public int Objects { get; init; }
}

/// <summary>
/// What a conversion did: the same kind of list as the mesh import pre-flight (approximated, dropped, split, engine
/// limits broken), plus counts.
/// </summary>
public sealed record LvlConvertReport
{
    public ImmutableArray<LvlConvertIssue> Issues { get; init; } = [];
    public LvlConvertStats Stats { get; init; } = new();
    /// <summary>Texture names that could not be found or decoded.</summary>
    public ImmutableArray<string> MissingTextures { get; init; } = [];

    /// <summary>True when any entry is an error.</summary>
    public bool HasErrors => Issues.Any(i => i.Severity == LvlIssueSeverity.Error);

    /// <summary>Entries with one code.</summary>
    public IEnumerable<LvlConvertIssue> WithCode(string code) => Issues.Where(i => i.Code == code);

    /// <summary>The report as text, one entry per line, errors first.</summary>
    public string ToText() => string.Join(Environment.NewLine,
        Issues.OrderByDescending(i => i.Severity).Select(i => i.ToString()));
}

/// <summary>Report codes. Every message a converter writes uses one of these.</summary>
public static class LvlConvertCodes
{
    /// <summary>Faces left out by the face-class filter.</summary>
    public const string FilteredFaces = "LC001";
    /// <summary>Faces skipped because a corner names a vertex that does not exist.</summary>
    public const string BadVertexFaces = "LC002";
    /// <summary>Faces skipped because they have fewer than three corners.</summary>
    public const string ShortFaces = "LC003";
    /// <summary>Faces with no area kept.</summary>
    public const string DegenerateFaces = "LC004";
    /// <summary>Polygons triangulated.</summary>
    public const string Triangulated = "LC005";
    /// <summary>Vertices split by (position, UV, normal).</summary>
    public const string VertexSplit = "LC006";
    /// <summary>Normals made from smoothing groups.</summary>
    public const string SmoothingNormals = "LC007";
    /// <summary>Texture not found.</summary>
    public const string MissingTexture = "LC010";
    /// <summary>Texture found but not readable.</summary>
    public const string UnreadableTexture = "LC011";
    /// <summary>Textures written as PNG.</summary>
    public const string TexturesWritten = "LC012";
    /// <summary>Texture names only (no images).</summary>
    public const string TextureNamesOnly = "LC013";
    /// <summary>Faces without a texture.</summary>
    public const string Untextured = "LC014";
    /// <summary>Scrolling textures.</summary>
    public const string Scroll = "LC020";
    /// <summary>Lightmap coordinates and images.</summary>
    public const string Lightmaps = "LC021";
    /// <summary>Face flags with no target equivalent (kept in extras/comments, or dropped).</summary>
    public const string FaceFlags = "LC022";
    /// <summary>Full-bright faces.</summary>
    public const string FullBright = "LC023";
    /// <summary>Alpha and hole faces.</summary>
    public const string Transparency = "LC024";
    /// <summary>Brush data (solid flags, life, state) kept in extras/comments or dropped.</summary>
    public const string BrushData = "LC025";
    /// <summary>RF2 vertex colours.</summary>
    public const string VertexColors = "LC026";
    /// <summary>Objects written.</summary>
    public const string Objects = "LC030";
    /// <summary>Objects not written.</summary>
    public const string ObjectsDropped = "LC031";
    /// <summary>An orientation that is not a pure rotation.</summary>
    public const string NotARotation = "LC032";
    /// <summary>Lights as KHR_lights_punctual.</summary>
    public const string Lights = "LC033";
    /// <summary>Groups and moving groups.</summary>
    public const string Groups = "LC034";
    /// <summary>V3M submeshes split at the engine's texture limit.</summary>
    public const string SubmeshSplit = "LC040";
    /// <summary>V3M batches split at the 16-bit size limits.</summary>
    public const string BatchSplit = "LC041";
    /// <summary>A name longer than the format holds.</summary>
    public const string NameTooLong = "LC042";
    /// <summary>A mesh linter finding on the V3M output.</summary>
    public const string MeshLint = "LC043";
    /// <summary>The V3M could not be built.</summary>
    public const string MeshBuild = "LC044";
    /// <summary>The target is not available.</summary>
    public const string TargetUnavailable = "LC050";
    /// <summary>The chosen geometry does not exist in the source (a group file has no compiled geometry).</summary>
    public const string GeometryChoice = "LC051";
    /// <summary>Nothing to convert.</summary>
    public const string Empty = "LC052";
    /// <summary>The source was read with problems.</summary>
    public const string SourceProblems = "LC053";
    /// <summary>Cairn metadata in OBJ comments.</summary>
    public const string ObjMetadata = "LC060";
    /// <summary>Red Faction II texture names translated or kept, and texture files written beside the output.</summary>
    public const string Rf2Textures = "LC090";
    /// <summary>Red Faction II lights rescaled for Red Faction's falloff.</summary>
    public const string Rf2Lights = "LC091";
    /// <summary>Red Faction II coronas turned into lights or kept as Alpine coronas.</summary>
    public const string Rf2Coronas = "LC092";
    /// <summary>Fill lights added for baked light no light explains.</summary>
    public const string Rf2FillLights = "LC093";
    /// <summary>Red Faction II ambient, fog and hardlight: what to set by hand.</summary>
    public const string Rf2Ambient = "LC094";
    /// <summary>Red Faction II brush flags (geoable, RF2-only bits, breakable invisible trigger brushes).</summary>
    public const string Rf2Brushes = "LC095";
    /// <summary>Red Faction II objects (clutter, jump pads, classes, sections that are not converted).</summary>
    public const string Rf2Objects = "LC096";
    /// <summary>Red Faction II face flags and scroll periods.</summary>
    public const string Rf2Faces = "LC097";
    /// <summary>The output is too large for the format.</summary>
    public const string TooLarge = "LC080";
}

/// <summary>Collects issues and counters while a converter runs.</summary>
internal sealed class LvlReportBuilder
{
    private readonly List<LvlConvertIssue> _issues = [];
    private readonly SortedSet<string> _missing = new(StringComparer.OrdinalIgnoreCase);

    public int SourceFaces;
    public int Faces;
    public int Triangles;
    public int Vertices;
    public int SourceVertices;
    public int Parts;
    public int Materials;
    public int Textures;
    public int Objects;

    public void Add(LvlIssueSeverity severity, LvlIssueKind kind, string code, string message) =>
        _issues.Add(new LvlConvertIssue(severity, kind, code, message));

    public void Info(string code, string message, LvlIssueKind kind = LvlIssueKind.Note) => Add(LvlIssueSeverity.Info, kind, code, message);

    public void Warning(string code, string message, LvlIssueKind kind) => Add(LvlIssueSeverity.Warning, kind, code, message);

    public void Error(string code, string message, LvlIssueKind kind = LvlIssueKind.LimitBroken) => Add(LvlIssueSeverity.Error, kind, code, message);

    public void Missing(string texture) => _missing.Add(texture);

    public bool HasErrors => _issues.Any(i => i.Severity == LvlIssueSeverity.Error);

    public LvlConvertReport Build() => new()
    {
        Issues = [.. _issues],
        MissingTextures = [.. _missing],
        Stats = new LvlConvertStats
        {
            SourceFaces = SourceFaces,
            Faces = Faces,
            Triangles = Triangles,
            Vertices = Vertices,
            SourceVertices = SourceVertices,
            Parts = Parts,
            Materials = Materials,
            Textures = Textures,
            Objects = Objects,
        },
    };

    /// <summary>"1 face" / "3 faces".</summary>
    public static string Count(int n, string singular, string? plural = null) =>
        n == 1 ? $"1 {singular}" : $"{n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} {plural ?? singular + "s"}";
}
