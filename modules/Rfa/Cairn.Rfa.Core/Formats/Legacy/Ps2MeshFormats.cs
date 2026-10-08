using Cairn.Rfa.Formats.V3d;

namespace Cairn.Rfa.Formats.Legacy;

/// <summary>
/// Red Faction (PlayStation 2) static mesh, <c>.rfm</c>: the PS2 build of a <c>.v3d</c>. Converts to a
/// <c>.v3m</c> description. Red Faction II's <c>.rfm</c> (same magic, version 0x114) is refused with a
/// clear message.
/// </summary>
public sealed class RfmFormat : ILegacyMeshFormat
{
    /// <inheritdoc />
    public string Extension => ".rfm";

    /// <inheritdoc />
    /// <remarks>False for Red Faction II files; <see cref="TryRead"/> explains those.</remarks>
    public bool Recognises(ReadOnlySpan<byte> head) => Ps2MeshParser.IsCharacter(head, out _) == false;

    /// <inheritdoc />
    public bool TryRead(ReadOnlySpan<byte> data, string name, out LegacyMesh? mesh, out LegacyMeshError? error) =>
        Ps2Mesh.TryRead(data, name, expectCharacter: false, out mesh, out error);
}

/// <summary>
/// Red Faction (PlayStation 2) character mesh, <c>.rfc</c>: the PS2 build of a <c>.vcm</c>. Converts to a
/// <c>.v3c</c> description with its skeleton, weights, collision spheres and prop points. Red Faction
/// II's <c>.rfc</c> (version 0x114) is refused with a clear message.
/// </summary>
public sealed class RfcFormat : ILegacyMeshFormat
{
    /// <inheritdoc />
    public string Extension => ".rfc";

    /// <inheritdoc />
    /// <remarks>False for Red Faction II files; <see cref="TryRead"/> explains those.</remarks>
    public bool Recognises(ReadOnlySpan<byte> head) => Ps2MeshParser.IsCharacter(head, out _) == true;

    /// <inheritdoc />
    public bool TryRead(ReadOnlySpan<byte> data, string name, out LegacyMesh? mesh, out LegacyMeshError? error) =>
        Ps2Mesh.TryRead(data, name, expectCharacter: true, out mesh, out error);
}

/// <summary>The shared read path of <see cref="RfmFormat"/> and <see cref="RfcFormat"/>.</summary>
internal static class Ps2Mesh
{
    public static bool TryRead(ReadOnlySpan<byte> data, string name, bool expectCharacter, out LegacyMesh? mesh, out LegacyMeshError? error)
    {
        mesh = null;
        name ??= "";
        if (!Ps2MeshParser.TryParse(data, name, out var file, out error)) return false;
        for (int i = 0; i < file!.Bones.Length; i++)
        {
            int parent = file.Bones[i].ParentIndex;
            if (parent < -1 || parent >= file.Bones.Length)
            {
                error = new LegacyMeshError($"{name}: bone {i} ('{file.Bones[i].Name.Text}') has parent {parent}, outside the skeleton's {file.Bones.Length} bones.");
                return false;
            }
        }
        try
        {
            mesh = Ps2MeshConverter.Convert(file, name);
            // Whatever is returned must compile: a description the builder refuses is reported here.
            V3dBuilder.Build(mesh.Description);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException or IndexOutOfRangeException)
        {
            // The parser validates every index the converter uses; this is a last line of defence.
            mesh = null;
            error = new LegacyMeshError($"{name}: the mesh could not be converted ({ex.Message}).");
            return false;
        }
        if (file.IsCharacter != expectCharacter)
        {
            mesh = mesh with
            {
                Notes = mesh.Notes.Insert(0, file.IsCharacter
                    ? "The file is a character mesh (.rfc) although its name says .rfm; it was read as a character."
                    : "The file is a static mesh (.rfm) although its name says .rfc; it was read as a static mesh."),
            };
        }
        return true;
    }
}
