using System.Globalization;
using Cairn.Ui.Modules;
using Cairn.Vfx.Ui.Rfe;

namespace Cairn.Vfx.Ui;

// Red Faction II effects (.rfe): the read-only document kind, the packfile preview, Convert to › Red Faction effect and
// the packfile batch "Convert effects...".
public sealed partial class VfxModule : IArchiveBatchConverter
{
    private readonly RfeKind _rfeKind = new();
    private RfeConversionUi? _rfeTools;

    /// <summary>The Red Faction II effect conversion (its last result, for diagnostics).</summary>
    public RfeConversionUi RfeTools => _rfeTools ??= new RfeConversionUi(Shell);

    private static bool IsRfeConversion(string sourceName, string targetExtension) =>
        RfeConversionUi.IsRfeName(sourceName) && string.Equals(targetExtension, ".vfx", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public string CommandText => "Convert _effects...";

    /// <inheritdoc/>
    public string CommandToolTip => "Convert the selected Red Faction II effects (.rfe) to Red Faction effects (.vfx), with their textures translated or written from the .peg packs: into a folder (or the packfile as new entries)";

    /// <inheritdoc/>
    public bool CanConvert(string entryName) => RfeConversionUi.IsRfeName(entryName);

    /// <inheritdoc/>
    public Task<string?> ConvertAsync(ArchiveBatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RfeTools.ConvertBatchAsync(request);
    }

    /// <summary><c>--time seconds</c> and <c>--camera yaw,pitch[,distance]</c> for a Red Faction II effect tab (screenshots).</summary>
    private static void ApplyRfeOptions(RfeDocument doc, IReadOnlyDictionary<string, string> options)
    {
        if (options.TryGetValue("time", out var time) && double.TryParse(time, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) doc.SeekSeconds(seconds);
        if (options.TryGetValue("camera", out var cam) && doc.View is RfeDocumentView view)
        {
            var parts = cam.Split(',').Select(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0).ToArray();
            if (parts.Length >= 2)
            {
                view.Dispatcher.InvokeAsync(() =>
                {
                    view.Viewport.FrameAll();
                    view.Viewport.Camera.SetView(parts[0], parts[1]);
                    if (parts.Length >= 3 && parts[2] > 0) view.Viewport.Camera.Distance *= parts[2];
                    view.Viewport.Camera.NotifyChanged();
                    view.Viewport.Invalidate();
                }, System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }
    }
}
