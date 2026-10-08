namespace Cairn.Rfa.Formats.Legacy;

/// <summary>
/// The 3ds Max exporter's static mesh, <c>.v3d</c>: the uncompiled source of a <c>.v3m</c> ("RF3D", version
/// 0x40000, the same header as a compiled mesh). Converts to a <c>.v3m</c> description.
/// </summary>
public sealed class ExporterV3dFormat : ILegacyMeshFormat
{
    /// <inheritdoc />
    public string Extension => ".v3d";

    /// <inheritdoc />
    public bool Recognises(ReadOnlySpan<byte> head) => ExporterMeshReader.LooksLikeExporterMesh(head);

    /// <inheritdoc />
    public bool TryRead(ReadOnlySpan<byte> data, string name, out LegacyMesh? mesh, out LegacyMeshError? error) =>
        ExporterMesh.TryRead(data, name, "v3d", out mesh, out error);
}

/// <summary>
/// The 3ds Max exporter's character mesh, <c>.vcm</c>: the uncompiled source of a <c>.v3c</c> ("RFCM", version
/// 0x10000) with its bones, collision spheres, prop points and per-vertex weights. Converts to a <c>.v3c</c>
/// description.
/// </summary>
public sealed class ExporterVcmFormat : ILegacyMeshFormat
{
    /// <inheritdoc />
    public string Extension => ".vcm";

    /// <inheritdoc />
    public bool Recognises(ReadOnlySpan<byte> head) => ExporterMeshReader.LooksLikeExporterMesh(head);

    /// <inheritdoc />
    public bool TryRead(ReadOnlySpan<byte> data, string name, out LegacyMesh? mesh, out LegacyMeshError? error) =>
        ExporterMesh.TryRead(data, name, "vcm", out mesh, out error);
}

/// <summary>The shared read path of <see cref="ExporterV3dFormat"/> and <see cref="ExporterVcmFormat"/>.</summary>
internal static class ExporterMesh
{
    public static bool TryRead(ReadOnlySpan<byte> data, string name, string sourceFormat, out LegacyMesh? mesh, out LegacyMeshError? error)
    {
        mesh = null;
        error = null;
        name ??= "";
        ExporterMeshFile file;
        try
        {
            file = ExporterMeshReader.Read(data.ToArray(), name);
        }
        catch (AssetFormatException ex)
        {
            error = new LegacyMeshError(ex.Message);
            return false;
        }
        try
        {
            mesh = ExporterMeshConverter.Convert(file, name, sourceFormat);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException or IndexOutOfRangeException)
        {
            // The reader validates every index the converter uses; this is a last line of defence.
            error = new LegacyMeshError($"'{name}': the mesh could not be converted ({ex.Message}).");
            return false;
        }
        bool character = file.Kind == V3d.V3dKind.Character;
        if (character != (sourceFormat == "vcm"))
        {
            mesh = mesh with
            {
                Notes = mesh.Notes.Insert(0, character
                    ? "The file is a character mesh (.vcm) although its name says otherwise; it was read as a character."
                    : "The file is a static mesh (.v3d) although its name says otherwise; it was read as a static mesh."),
            };
        }
        return true;
    }
}
