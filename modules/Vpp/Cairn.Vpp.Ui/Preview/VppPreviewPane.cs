using Cairn.Previews;
using Cairn.Ui.Modules;
using Cairn.Vpp.Model;

namespace Cairn.Vpp.Ui.Preview;

/// <summary>
/// The preview of the primary selected packfile entry, shown by the shared <see cref="AssetPreviewPane"/>: the
/// entry's bytes come from its source, the packfile's other entries are the siblings, and "Open in Cairn" goes
/// through the packfile's work copy when someone handles <see cref="OpenInCairnRequested"/>.
/// </summary>
public sealed class VppPreviewPane : AssetPreviewPane
{
    public VppPreviewPane(IShellContext? shell) : base(shell) => BusyLabel = "vpp preview";

    /// <summary>The entry the current or pending preview is for.</summary>
    public VppItem? Item { get; private set; }

    /// <summary>
    /// "Open in Cairn" on a module preview. When nobody handles it, the pane opens the bytes through the
    /// document kind that takes the extension.
    /// </summary>
    public event EventHandler<VppItem>? OpenInCairnRequested;

    /// <summary>Shows the selection now (callers debounce).</summary>
    /// <param name="primary">The entry to preview.</param>
    /// <param name="selection">Every selected entry (a summary is shown for more than one).</param>
    /// <param name="package">The packfile as it is now: other modules' previews find textures, frames and meshes in it.</param>
    /// <param name="force">Preview even above <see cref="AssetPreviewPane.SizeCap"/>.</param>
    public void Show(VppItem? primary, IReadOnlyList<VppItem> selection, VppPackage? package = null, bool force = false)
    {
        if (selection.Count > 1)
        {
            ShowSummary([.. selection.Select(i => (i.Name, i.Size))]);
            Item = primary;
            return;
        }
        if (primary is null)
        {
            Clear();
            Item = null;
            return;
        }
        var item = primary;
        var source = new AssetPreviewSource(item.Name, _ => Task.FromResult(item.Source.ReadAll()))
        {
            Size = item.Size,
            ReadHead = (count, _) => Task.FromResult(ReadHead(item, count)),
            // The packfile's other entries (pending changes included) come first when the preview resolves textures, frames or meshes.
            Siblings = package is null ? null : new VppSiblings(package),
            Origin = item.Source.Describe(),
            // the stored offsets are only valid in the file as it was opened: never preview bytes from a changed packfile
            Unavailable = package is null ? null : Documents.VppDocument.StaleProblem(package, [item]),
            OpenInCairn = OpenInCairnRequested is null ? null : _ => OpenInCairnRequested?.Invoke(this, item),
        };
        Show(source, force);
        Item = primary;
    }

    private static byte[] ReadHead(VppItem item, int count)
    {
        using var stream = item.Source.Open();
        var buffer = new byte[(int)Math.Min(count, Math.Max(0, item.Size))];
        int read = 0;
        while (read < buffer.Length)
        {
            int n = stream.Read(buffer, read, buffer.Length - read);
            if (n <= 0) break;
            read += n;
        }
        return read == buffer.Length ? buffer : buffer[..read];
    }

    protected override void OnDisposed() => Item = null;
}
