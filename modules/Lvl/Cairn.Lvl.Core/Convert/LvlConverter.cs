using Cairn.Formats.Gltf;
using Cairn.Formats.Obj;
using Cairn.Rfa.Formats.V3d;
using Cairn.Workspace;

namespace Cairn.Lvl.Conversion;

/// <summary>
/// The result of a conversion before anything is written: the output document (glTF, OBJ + MTL + images, mesh or
/// whatever another target makes) and its report. <see cref="WriteTo"/> writes it.
/// </summary>
public sealed partial class LvlConversion
{
    private readonly Func<string, IReadOnlyList<string>>? _write;
    private readonly Func<string, IReadOnlyList<string>>? _plan;

    /// <summary>A conversion with its own writer (for targets that live elsewhere, such as .rfg).</summary>
    /// <param name="target">The target.</param>
    /// <param name="report">The report.</param>
    /// <param name="output">The output document, or null when it could not be made.</param>
    /// <param name="write">Writes the output for a main path and returns every file written, the main one first.</param>
    /// <param name="plan">The files <paramref name="write"/> would write, the main one first.</param>
    public LvlConversion(LvlConvertTarget target, LvlConvertReport report, object? output,
        Func<string, IReadOnlyList<string>>? write, Func<string, IReadOnlyList<string>>? plan)
    {
        Target = target;
        Report = report;
        Output = output;
        _write = write;
        _plan = plan;
    }

    public LvlConvertTarget Target { get; }
    public LvlConvertReport Report { get; }
    /// <summary>The output document (<see cref="GltfDocument"/>, <see cref="LvlObjOutput"/>, <see cref="V3dFile"/> ...), or null.</summary>
    public object? Output { get; }

    /// <summary>True when there is something to write.</summary>
    public bool HasOutput => Output is not null && _write is not null;

    public GltfDocument? Gltf => Output as GltfDocument;
    public LvlObjOutput? Obj => Output as LvlObjOutput;
    public V3dFile? Mesh => Output as V3dFile;

    /// <summary>Every file <see cref="WriteTo"/> would write for <paramref name="path"/> (so a caller can ask before replacing any).</summary>
    public IReadOnlyList<string> PlannedFiles(string path) => _plan is null || Output is null ? [] : _plan(path);

    /// <summary>Writes the output (each file atomically) and returns the files written, the main one first.</summary>
    /// <exception cref="InvalidOperationException">There is no output (see the report's errors).</exception>
    public IReadOnlyList<string> WriteTo(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!HasOutput) throw new InvalidOperationException("The conversion produced nothing to write; its report says why.");
        return _write!(path);
    }
}

/// <summary>OBJ output: the documents and the PNG textures written beside them.</summary>
public sealed record LvlObjOutput(ObjDocument Obj, MtlDocument Mtl, IReadOnlyList<LvlImageFile> Images, ObjWriteOptions WriteOptions);

/// <summary>A finished conversion: its report and the files written.</summary>
public sealed record LvlConvertResult(LvlConvertReport Report, ImmutableArray<string> Files)
{
    public bool Succeeded => Files.Length > 0;
}

/// <summary>
/// Level and group conversions: <c>.rfl</c> / <c>.rfg</c> to glTF (.gltf + .bin or .glb), Wavefront OBJ and static
/// mesh (.v3m), and the hook the group-file writer fills for .rfg. Every conversion applies the same rules: faces
/// are chosen by their face classes; vertices are split by (position, UV, normal) from the per-corner data; normals
/// come from the smoothing groups; brush transforms are applied (or, in glTF, kept as the node's transform); full
/// bright is not double-sided; materials are one per (texture, render state). Every conversion reports what it
/// approximated, dropped or split and every engine limit the output breaks.
/// </summary>
/// <remarks>Synchronous and thread-safe for distinct inputs: run it on a worker thread (levels can be hundreds of MB).</remarks>
public static partial class LvlConverter
{
    /// <summary>Converts in memory.</summary>
    /// <param name="input">The level or group file.</param>
    /// <param name="options">Target and choices; null uses the defaults (glTF of the compiled geometry).</param>
    /// <param name="outputFileName">The output's file name, when known (V3M checks its length against the engine's limit).</param>
    /// <param name="cancellationToken">Stops the conversion.</param>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static LvlConversion Convert(LvlConvertInput input, LvlConvertOptions? options = null, string? outputFileName = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        options ??= new LvlConvertOptions();
        // A Red Faction II level is first turned into the Red Faction level every target writes (textures, lights,
        // brushes, objects); what that changes leads the report.
        if (input.Level is { IsRf2: true } rf2)
        {
            var prepared = Rf2LevelConversion.Prepare(rf2, options, cancellationToken);
            return ConvertPrepared(input with { Level = prepared.Level }, prepared.Options, outputFileName, cancellationToken).WithRf2(prepared);
        }
        return ConvertPrepared(input, options, outputFileName, cancellationToken);
    }

    private static LvlConversion ConvertPrepared(LvlConvertInput input, LvlConvertOptions options, string? outputFileName, CancellationToken cancellationToken)
    {
        if (options.Target == LvlConvertTarget.Rfg)
        {
            LvlConversion? rfg = null;
            ConvertToRfg(input, options, cancellationToken, ref rfg);
            if (rfg is not null) return rfg;
            var r = new LvlReportBuilder();
            r.Error(LvlConvertCodes.TargetUnavailable, "Converting to a group file (.rfg) is not available in this version of Cairn.", LvlIssueKind.Dropped);
            return new LvlConversion(options.Target, r.Build(), null, null, null);
        }

        var report = new LvlReportBuilder();
        bool apply = options.Target is LvlConvertTarget.Obj or LvlConvertTarget.V3m || options.BrushTransforms == LvlBrushTransformMode.Apply;
        var scene = LvlScene.Build(input, options, apply, report, cancellationToken);
        var textures = new LvlTextureSet(options, report);
        string defaultName = Path.GetFileNameWithoutExtension(input.Name) + LvlConvertOptions.ExtensionOf(options.Target);
        if (scene.Parts.Count == 0 && (options.Target != LvlConvertTarget.Gltf && options.Target != LvlConvertTarget.Glb || scene.Objects.Count == 0))
        {
            report.Error(LvlConvertCodes.Empty, scene.SourceFaces == 0
                ? "The source has no geometry to convert."
                : "No face passes the face filter; nothing is left to convert.", LvlIssueKind.Dropped);
            return new LvlConversion(options.Target, report.Build(), null, null, null);
        }

        switch (options.Target)
        {
            case LvlConvertTarget.Gltf:
            case LvlConvertTarget.Glb:
            {
                GltfDocument doc;
                try
                {
                    doc = new LvlGltfConverter(scene, options, textures, report, cancellationToken).Run();
                }
                catch (InvalidOperationException e) when (e.Message.Contains("2 GB", StringComparison.Ordinal))
                {
                    report.Error(LvlConvertCodes.TooLarge, "The level's geometry is larger than one glTF buffer can hold (2 GB). Leave out face classes or convert brushes instead.");
                    return new LvlConversion(options.Target, report.Build(), null, null, null);
                }
                bool glb = options.Target == LvlConvertTarget.Glb;
                IReadOnlyList<string> Plan(string path)
                {
                    var files = new List<string> { Path.GetFullPath(path) };
                    if (!glb) files.AddRange(GltfWriter.SideFiles(doc, path));
                    return files;
                }
                return new LvlConversion(options.Target, report.Build(), doc,
                    path =>
                    {
                        if (glb) GltfWriter.WriteGlb(doc, path);
                        else GltfWriter.WriteGltf(doc, path);
                        return Plan(path);
                    },
                    Plan);
            }
            case LvlConvertTarget.Obj:
            {
                var (obj, mtl) = new LvlObjConverter(scene, options, textures, report, cancellationToken).Run();
                var output = new LvlObjOutput(obj, mtl, [.. textures.Images], new ObjWriteOptions());
                return new LvlConversion(options.Target, report.Build(), output, path => WriteObj(output, path), path => PlanObj(output, path));
            }
            case LvlConvertTarget.V3m:
            {
                var (mesh, _) = new LvlV3mConverter(scene, options, report, cancellationToken).Run(outputFileName ?? defaultName);
                if (mesh is null) return new LvlConversion(options.Target, report.Build(), null, null, null);
                return new LvlConversion(options.Target, report.Build(), mesh,
                    path =>
                    {
                        string full = Path.GetFullPath(path);
                        Directory.CreateDirectory(Path.GetDirectoryName(full) ?? ".");
                        AtomicFile.WriteAllBytes(full, V3dWriter.Write(mesh));
                        return [full];
                    },
                    path => [Path.GetFullPath(path)]);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(options), options.Target, "Unknown target.");
        }
    }

    /// <summary>Converts and writes <paramref name="path"/> (plus its side files). The target comes from the options.</summary>
    /// <exception cref="IOException">A file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">A file could not be written.</exception>
    public static LvlConvertResult ConvertToFile(LvlConvertInput input, string path, LvlConvertOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var conversion = Convert(input, options, Path.GetFileName(path), cancellationToken);
        if (!conversion.HasOutput) return new LvlConvertResult(conversion.Report, []);
        return new LvlConvertResult(conversion.Report, [.. conversion.WriteTo(path)]);
    }

    /// <summary>
    /// The group-file target. The .rfg writer implements this in its own file; when it leaves
    /// <paramref name="conversion"/> null the target is reported as unavailable.
    /// </summary>
    static partial void ConvertToRfg(LvlConvertInput input, LvlConvertOptions options, CancellationToken cancellationToken, ref LvlConversion? conversion);

    private static IReadOnlyList<string> PlanObj(LvlObjOutput output, string path)
    {
        string full = Path.GetFullPath(path);
        string folder = Path.GetDirectoryName(full) ?? ".";
        return [full, Path.ChangeExtension(full, ".mtl"), .. output.Images.Select(i => Path.Combine(folder, i.FileName))];
    }

    private static IReadOnlyList<string> WriteObj(LvlObjOutput output, string path)
    {
        string full = Path.GetFullPath(path);
        string folder = Path.GetDirectoryName(full) ?? ".";
        Directory.CreateDirectory(folder);
        // Images first, the .obj (which names the .mtl, which names the images) last.
        foreach (var image in output.Images) AtomicFile.WriteAllBytes(Path.Combine(folder, image.FileName), image.Png);
        ObjWriter.WriteFiles(full, output.Obj, output.Mtl, output.WriteOptions);
        return PlanObj(output, path);
    }
}
