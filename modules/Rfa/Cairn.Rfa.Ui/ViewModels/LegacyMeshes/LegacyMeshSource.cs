using Cairn.Rfa.Formats.Legacy;

namespace Cairn.Rfa.Ui.ViewModels;

/// <summary>
/// The exporter (.v3d, .vcm) or PlayStation 2 (.rfm, .rfc) file a read-only mesh tab shows: its bytes, the mesh
/// read from them (shown as the .v3m/.v3c converting it makes), or why it could not be read.
/// </summary>
/// <param name="SourceFormat">"v3d", "vcm", "rfm" or "rfc" (by content when the name says otherwise).</param>
/// <param name="Bytes">The file as read.</param>
/// <param name="Mesh">The mesh, or null when it could not be read.</param>
/// <param name="Error">Why it could not be read, or null.</param>
public sealed record LegacyMeshSource(string SourceFormat, byte[] Bytes, LegacyMesh? Mesh, string? Error)
{
    /// <summary>Problems code of a legacy mesh that could not be read.</summary>
    public const string UnreadableCode = "LEG001";

    /// <summary>Problems code of a note on what converting the mesh approximates.</summary>
    public const string NoteCode = "LEG100";

    /// <summary>"Exporter static mesh (.v3d)" and so on, for the banner.</summary>
    public string FormatTitle
    {
        get
        {
            string name = LegacyMeshSupport.FormatName(SourceFormat);
            return char.ToUpperInvariant(name[0]) + name[1..] + " (." + SourceFormat + ")";
        }
    }
}
