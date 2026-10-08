using Cairn.Rfa.Formats.Legacy;
using Cairn.Rfa.Formats.V3d;

namespace Cairn.Rfa.Ui.ViewModels;

/// <summary>The meshes Cairn only reads and converts: .v3d and .vcm (exporter), .rfm and .rfc (PlayStation 2).</summary>
public sealed partial class RfaWorkspace
{
    private LegacyMeshTools? _legacyTools;

    /// <summary>Converting exporter and PS2 meshes to .v3m/.v3c.</summary>
    public LegacyMeshTools Legacy => _legacyTools ??= new LegacyMeshTools(this);

    /// <summary>True when the bytes (or, for unreadable bytes, the name) are an exporter or PS2 mesh.</summary>
    internal static bool IsLegacyMesh(byte[] bytes, string name) =>
        LegacyMeshSupport.IsLegacyContent(bytes) || LegacyMeshSupport.IsLegacyName(name) && !IsCompiledMesh(bytes);

    private static bool IsCompiledMesh(byte[] bytes) =>
        bytes.Length >= 4 && BitConverter.ToUInt32(bytes, 0) is V3dHeader.StaticSignature or V3dHeader.CharacterSignature;

    /// <summary>
    /// A read-only tab for a legacy mesh, showing the mesh converting it makes. A file that cannot be read (Red
    /// Faction II's meshes, damaged files) still opens, empty, with the reason in Problems and on the banner.
    /// </summary>
    private MeshDocumentViewModel CreateLegacyDocument(byte[] bytes, string name, string? path, AssetLocation? origin)
    {
        var format = LegacyMeshSupport.Identify(bytes, name);
        string sourceFormat = format?.Extension.TrimStart('.') ?? Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        LegacyMesh? mesh = null;
        V3dFile? compiled = null;
        string? error = null;
        if (LegacyMeshSupport.TryRead(bytes, name, out var read, out var readError))
        {
            try
            {
                compiled = LegacyMeshSupport.Compile(read!);
                mesh = read;
                sourceFormat = read!.SourceFormat;
            }
            catch (AssetFormatException ex)
            {
                error = ex.Message;
            }
        }
        else
        {
            error = readError?.Message ?? "the file could not be read.";
        }
        bool character = mesh?.Description.Kind == V3dKind.Character || sourceFormat is "vcm" or "rfc";
        compiled ??= new V3dFile
        {
            Header = new V3dHeader(character ? V3dHeader.CharacterSignature : V3dHeader.StaticSignature, V3dHeader.CurrentVersion, 0, 0, 0, 0, 0, 0, 0, 0),
        };
        return new MeshDocumentViewModel(this, compiled, name, path, origin, new LegacyMeshSource(sourceFormat, bytes, mesh, error));
    }
}
