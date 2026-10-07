using Cairn.Ui.Modules;
using Cairn.Vpp.Ui.Documents;
using Cairn.Vpp.Ui.Library;
using Cairn.Vpp.Ui.List;

namespace Cairn.Vpp.Ui;

/// <summary>
/// Pane contributions: the left-pane type filter, shown instead of the asset libraries while a packfile is active, and
/// the Packfiles browser: the first tab of the start page, and the second tab (after File types) beside a packfile.
/// </summary>
public sealed partial class VppModule
{
    /// <summary>The Packfiles browser's panel id.</summary>
    public const string LibraryPanelId = "vpp.library";

    private static readonly PanelContribution TypesPanel =
        new("vpp.types", "File types", PanelSide.Left, 10, d => d is VppDocument v ? VppTypesPanel.For(v) : null, Exclusive: true);

    private IReadOnlyList<PanelContribution>? _panels;
    private VppLibraryPanel? _library;

    /// <summary>The Packfiles browser (one instance, created when first shown).</summary>
    public VppLibraryPanel Library => _library ??= new VppLibraryPanel(Shell);

    // One tab, two places: the same id (and panel) is offered with no document (lowest Order: the start page's
    // default tab) and, exclusive like File types, beside a packfile after File types. Other documents never get it.
    public override IReadOnlyList<PanelContribution> Panels => _panels ??=
    [
        TypesPanel,
        new(LibraryPanelId, "Packfiles", PanelSide.Left, 5, d => d is null ? Library : null),
        new(LibraryPanelId, "Packfiles", PanelSide.Left, 20, d => d is VppDocument ? Library : null, Exclusive: true),
    ];
}
