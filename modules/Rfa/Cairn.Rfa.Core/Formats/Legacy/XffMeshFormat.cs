using Cairn.Formats.XForge;

namespace Cairn.Rfa.Formats.Legacy;

/// <summary>
/// Red Faction for N-Gage scenes (<c>.xff</c>) of kind Model or Character, read through <see cref="XffScene"/> and
/// converted by <see cref="XffMeshConverter"/> at the default scale. A level is refused: the Levels module opens it.
/// </summary>
internal sealed class XffMeshFormat : ILegacyMeshFormat
{
    public string Extension => XffFile.Extension;

    public bool Recognises(ReadOnlySpan<byte> head) => XffFile.LooksLikeXff(head);

    public bool TryRead(ReadOnlySpan<byte> data, string name, out LegacyMesh? mesh, out LegacyMeshError? error)
    {
        mesh = null;
        error = null;
        try
        {
            mesh = XffMeshConverter.Convert(data.ToArray(), name);
            return true;
        }
        catch (AssetFormatException ex)
        {
            error = new LegacyMeshError(ex.Message);
            return false;
        }
    }
}
