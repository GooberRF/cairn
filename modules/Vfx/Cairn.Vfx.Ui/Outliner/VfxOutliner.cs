using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Ui.Documents;

namespace Cairn.Vfx.Ui.Outliner;

/// <summary>
/// Object tree: hierarchy by parent name (parents not in the file shown as external roots), a Materials
/// group, kind glyphs, a show-in-preview check box per object, selection synced with the document.
/// </summary>
public sealed partial class VfxOutliner : TreeView
{
    private readonly VfxDocument _doc;
    private readonly Dictionary<int, TreeViewItem> _items = [];
    private bool _syncing;

    /// <summary>True when the tree row for <paramref name="section"/> is the selected row.</summary>
    public bool ShowsSelected(int section) => _items.TryGetValue(section, out var item) && item.IsSelected;

    /// <summary>Selects the row for <paramref name="section"/> as a click would (self-tests).</summary>
    public void SelectRow(int section) { if (_items.TryGetValue(section, out var item)) item.IsSelected = true; }

    public VfxOutliner(VfxDocument doc)
    {
        _doc = doc;
        AutomationProperties.SetName(this, "Effect objects");
        BorderThickness = new Thickness(0);
        SetResourceReference(BackgroundProperty, "App.PaneBackground");
        Build();
        AttachEditing();
        doc.SceneChanged += (_, _) => Build();
        doc.Selection.Changed += (_, _) => SyncFromDocument();
        SelectedItemChanged += (_, _) =>
        {
            if (_syncing || SelectedItem is not TreeViewItem { Tag: int section }) return;
            _syncing = true;
            if (section >= 0 && _doc.Current.Sections[section] is VfxMaterial) _doc.Selection.Material = VfxSections.OrdinalOf(_doc.Current, section);
            _doc.Selection.Select(section);
            _syncing = false;
        };
    }

    private void Build()
    {
        Items.Clear(); _items.Clear();
        var f = _doc.Current;
        var byName = new Dictionary<string, TreeViewItem>(StringComparer.OrdinalIgnoreCase);
        var objects = Enumerable.Range(0, f.Sections.Length).Where(i => VfxSections.KindOf(f.Sections[i]) is not (VfxSectionKind.Material or VfxSectionKind.Other)).ToList();
        foreach (int i in objects) { var item = Make(i); _items[i] = item; byName.TryAdd(VfxSections.NameOf(f.Sections[i]), item); }
        var external = new Dictionary<string, TreeViewItem>(StringComparer.OrdinalIgnoreCase);
        foreach (int i in objects)
        {
            string parent = VfxSections.ParentOf(f.Sections[i]);
            if (string.IsNullOrEmpty(parent) || parent.Equals("None", StringComparison.OrdinalIgnoreCase) || !byName.TryGetValue(parent, out var host) || host == _items[i])
            {
                if (string.IsNullOrEmpty(parent) || parent.Equals("None", StringComparison.OrdinalIgnoreCase) || byName.ContainsKey(parent)) { Items.Add(_items[i]); continue; }
                if (!external.TryGetValue(parent, out host))
                {
                    host = Group($"{parent} (external)", "", $"Parent \"{parent}\" is not in this file (attached by the game)");
                    external[parent] = host; Items.Add(host);
                }
            }
            host.Items.Add(_items[i]);
        }
        var mats = Group("Materials", VfxSections.GlyphOf(VfxSectionKind.Material), "Materials used by the meshes and particle systems");
        for (int i = 0; i < f.Sections.Length; i++)
            if (f.Sections[i] is VfxMaterial) { var it = Make(i); _items[i] = it; mats.Items.Add(it); }
        if (mats.Items.Count > 0) Items.Add(mats);
        if (Items.Count == 0)
        {
            var empty = new TextBlock { Text = "No effect objects yet: use Effect > Add", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 6, 4, 0), FontStyle = FontStyles.Italic };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
            Items.Add(new TreeViewItem { Header = empty, Tag = -1, Focusable = false, IsHitTestVisible = false });
        }
        SyncFromDocument();
    }

    private static TreeViewItem Styled(TreeViewItem item) { item.SetResourceReference(StyleProperty, "PaneTreeViewItem"); return item; }

    private TreeViewItem Group(string header, string glyph, string tip) =>
        Styled(new() { Header = Row(glyph, header, null), Tag = -1, IsExpanded = true, ToolTip = tip });

    private TreeViewItem Make(int section)
    {
        var s = _doc.Current.Sections[section];
        var kind = VfxSections.KindOf(s);
        string name = kind == VfxSectionKind.Material ? $"Material {VfxSections.OrdinalOf(_doc.Current, section) + 1}" : VfxSections.NameOf(s);
        CheckBox? box = null;
        if (kind != VfxSectionKind.Material)
        {
            box = new CheckBox { IsChecked = !_doc.HiddenSections.Contains(section), ToolTip = "Show in the preview (does not change the file)", Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center,
                LayoutTransform = new ScaleTransform(0.7, 0.7) };
            AutomationProperties.SetName(box, $"Show {name} in preview");
            box.Click += (_, _) => _doc.SetHidden(section, box.IsChecked != true);
        }
        var item = Styled(new TreeViewItem { Header = Row(VfxSections.GlyphOf(kind), string.IsNullOrEmpty(name) ? "(unnamed)" : name, box), Tag = section, IsExpanded = true, ToolTip = $"{VfxSections.KindText(kind)}: {name}" });
        AutomationProperties.SetName(item, $"{VfxSections.KindText(kind)} {name}");
        return item;
    }

    private static StackPanel Row(string glyph, string text, CheckBox? box)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (box is not null) row.Children.Add(box);
        var g = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        g.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        row.Children.Add(g);
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        t.SetResourceReference(TextBlock.ForegroundProperty, "App.Text");
        row.Children.Add(t);
        return row;
    }

    private void SyncFromDocument()
    {
        if (_syncing) return;
        _syncing = true;
        if (_items.TryGetValue(_doc.Selection.Primary, out var item)) { item.IsSelected = true; item.BringIntoView(); }
        else if (SelectedItem is TreeViewItem cur) cur.IsSelected = false;
        _syncing = false;
    }
}
