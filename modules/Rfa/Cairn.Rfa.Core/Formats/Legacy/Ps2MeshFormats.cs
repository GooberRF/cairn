using Cairn.Rfa.Formats.V3d;

namespace Cairn.Rfa.Formats.Legacy;

/// <summary>
/// Static mesh, <c>.rfm</c>: Red Faction's PlayStation 2 build of a <c>.v3d</c>, or a Red Faction II static mesh
/// (same magic, version 0x114). Converts to a <c>.v3m</c> description. Red Faction II's older prototype versions
/// (5, 8, 9, 10) are refused with a clear message.
/// </summary>
public sealed class RfmFormat : ILegacyMeshFormat
{
    /// <inheritdoc />
    public string Extension => ".rfm";

    /// <inheritdoc />
    /// <remarks>False for Red Faction II prototypes; <see cref="TryRead"/> explains those.</remarks>
    public bool Recognises(ReadOnlySpan<byte> head) =>
        Rf2MeshParser.Rf2VersionOf(head) is not null ? Rf2MeshParser.IsSkinned(head) == false : Ps2MeshParser.IsCharacter(head, out _) == false;

    /// <inheritdoc />
    public bool TryRead(ReadOnlySpan<byte> data, string name, out LegacyMesh? mesh, out LegacyMeshError? error) =>
        Rf2MeshParser.Rf2VersionOf(data) is not null
            ? Rf2Mesh.TryRead(data, name, expectCharacter: false, out mesh, out error)
            : Ps2Mesh.TryRead(data, name, expectCharacter: false, out mesh, out error);
}

/// <summary>
/// Character mesh, <c>.rfc</c>: Red Faction's PlayStation 2 build of a <c>.vcm</c>, or a Red Faction II character
/// (version 0x114). Converts to a <c>.v3c</c> description with its skeleton, weights, collision spheres and prop
/// points. Red Faction II's older prototype versions are refused with a clear message.
/// </summary>
public sealed class RfcFormat : ILegacyMeshFormat
{
    /// <inheritdoc />
    public string Extension => ".rfc";

    /// <inheritdoc />
    /// <remarks>False for Red Faction II prototypes; <see cref="TryRead"/> explains those.</remarks>
    public bool Recognises(ReadOnlySpan<byte> head) =>
        Rf2MeshParser.Rf2VersionOf(head) is not null ? Rf2MeshParser.IsSkinned(head) == true : Ps2MeshParser.IsCharacter(head, out _) == true;

    /// <inheritdoc />
    public bool TryRead(ReadOnlySpan<byte> data, string name, out LegacyMesh? mesh, out LegacyMeshError? error) =>
        Rf2MeshParser.Rf2VersionOf(data) is not null
            ? Rf2Mesh.TryRead(data, name, expectCharacter: true, out mesh, out error)
            : Ps2Mesh.TryRead(data, name, expectCharacter: true, out mesh, out error);
}

/// <summary>The read path of a Red Faction II mesh (either extension).</summary>
internal static class Rf2Mesh
{
    public static bool TryRead(ReadOnlySpan<byte> data, string name, bool expectCharacter, out LegacyMesh? mesh, out LegacyMeshError? error)
    {
        mesh = null;
        name ??= "";
        if (!Rf2MeshParser.TryParse(data, name, out var file, out error))
        {
            // a damaged 0x114 file says which game's mesh it is; a prototype's message already does
            if (error is not null && Rf2MeshParser.Rf2VersionOf(data) == Rf2MeshFile.Version)
                error = error with { Message = "Red Faction II mesh: " + error.Message };
            return false;
        }
        for (int i = 0; i < file!.Bones.Length; i++)
        {
            int parent = file.Bones[i].Parent;
            if (parent < -1 || parent >= file.Bones.Length || parent == i)
            {
                error = new LegacyMeshError($"Red Faction II mesh: {name}: bone {i} ('{file.Bones[i].Name}') has parent {parent}, outside the skeleton's {file.Bones.Length} bones.");
                return false;
            }
        }
        if (file.Bones.Length > V3dBoneSection.MaxBones)
        {
            error = new LegacyMeshError($"Red Faction II mesh: {name} has {file.Bones.Length} bones; a PC character holds at most {V3dBoneSection.MaxBones}.");
            return false;
        }
        try
        {
            mesh = Rf2MeshConverter.Convert(file, name);
            // Whatever is returned must compile: a description the builder refuses is reported here.
            V3dBuilder.Build(mesh.Description);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OverflowException or IndexOutOfRangeException)
        {
            mesh = null;
            error = new LegacyMeshError($"Red Faction II mesh: {name}: the mesh could not be converted ({ex.Message}).");
            return false;
        }
        if (file.IsSkinned != expectCharacter)
        {
            mesh = mesh with
            {
                Notes = mesh.Notes.Insert(0, file.IsSkinned
                    ? "The file is a character mesh (.rfc) although its name says .rfm; it was read as a character."
                    : "The file is a static mesh (.rfm) although its name says .rfc; it was read as a static mesh."),
            };
        }
        return true;
    }
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
