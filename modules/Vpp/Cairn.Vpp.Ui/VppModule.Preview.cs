using Cairn.Vpp.Ui.Documents;
using Cairn.Vpp.Ui.Preview;

namespace Cairn.Vpp.Ui;

public sealed partial class VppModule
{
    /// <summary>Fills a new packfile tab's preview and details hosts and keeps them following its selection.</summary>
    partial void AttachPanes(VppDocument document, VppDocumentView view) => VppPreviewWiring.Attach(document, view);
}
