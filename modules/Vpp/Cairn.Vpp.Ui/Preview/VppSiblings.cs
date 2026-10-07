using Cairn.Formats;
using Cairn.Ui.Modules;
using Cairn.Vpp.Model;

namespace Cairn.Vpp.Ui.Preview;

/// <summary>
/// The entries of one packfile snapshot as the files beside a previewed entry (<see cref="IAssetSiblings"/>), so a
/// module preview finds the textures, frames and meshes the packfile carries itself. The snapshot is immutable, so
/// pending added, replaced and renamed entries are included exactly as they are in it, and lookups are safe from any
/// thread. Names compare as the engine does (<see cref="Editing.VppNames.Comparer"/>); an entry that can no longer be
/// read (its file changed or the packfile moved) reads as null.
/// </summary>
public sealed class VppSiblings : IAssetSiblings
{
    private readonly VppPackage _package;

    /// <param name="package">The packfile snapshot, usually the open document's <c>Current</c>.</param>
    public VppSiblings(VppPackage package)
    {
        _package = package ?? throw new ArgumentNullException(nameof(package));
        Label = package.Path is { } path ? Path.GetFileName(path) : "this packfile";
        Names = [.. package.Items.Select(i => i.Name)];
    }

    /// <inheritdoc/>
    public string Label { get; }

    /// <inheritdoc/>
    public IReadOnlyList<string> Names { get; }

    /// <inheritdoc/>
    public bool Contains(string name) => !string.IsNullOrWhiteSpace(name) && _package.Contains(Path.GetFileName(name.Trim()));

    /// <inheritdoc/>
    public byte[]? Read(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || _package.Find(Path.GetFileName(name.Trim())) is not { } item) return null;
        try
        {
            return item.Source.ReadAll();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AssetFormatException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
