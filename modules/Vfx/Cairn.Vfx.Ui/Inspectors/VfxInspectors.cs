using System.Windows.Automation;
using System.Windows.Controls;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Ui.Documents;

namespace Cairn.Vfx.Ui.Inspectors;

/// <summary>
/// The right-hand inspector area: Effect, Object and Material tabs (each a <see cref="VfxInspectorPage"/>).
/// Selecting an object shows the Object tab; selecting a material shows the Material tab.
/// </summary>
public sealed class VfxInspectors : TabControl
{
    public VfxInspectors(VfxDocument doc)
    {
        AutomationProperties.SetName(this, "Inspector");
        SetResourceReference(StyleProperty, "PanelTabControl");
        EffectTab = new VfxEffectTab(doc); Object = new VfxObjectTab(doc); Material = new VfxMaterialTab(doc);
        Items.Add(new TabItem { Header = "Effect", ToolTip = "The whole effect: version, end frame, counts", Content = EffectTab });
        Items.Add(new TabItem { Header = "Object", ToolTip = "The selected object(s)", Content = Object });
        Items.Add(new TabItem { Header = "Material", ToolTip = "The selected material, or the material of the selected object", Content = Material });
        doc.Selection.Changed += (_, _) =>
        {
            if (doc.Selection.IsEmpty) return;
            SelectedIndex = doc.SelectedSection is VfxMaterial ? 2 : 1;
        };
    }

    public VfxEffectTab EffectTab { get; }
    public VfxObjectTab Object { get; }
    public VfxMaterialTab Material { get; }
    public ItemCollection Tabs => Items;

    /// <summary>Selects a tab by header (case-insensitive); used by the <c>--inspector</c> diagnostic option.</summary>
    public void Show(string header)
    {
        for (int i = 0; i < Items.Count; i++)
            if (Items[i] is TabItem { Header: string h } && h.Equals(header, StringComparison.OrdinalIgnoreCase)) SelectedIndex = i;
    }
}
