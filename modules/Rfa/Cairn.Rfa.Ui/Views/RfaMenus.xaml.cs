using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Cairn.Rfa.Editing;
using Cairn.Rfa.Ui.ViewModels;

namespace Cairn.Rfa.Ui.Views;

/// <summary>Click handlers of the Clip and Mesh menus (moved from RFA Workbench's main window).</summary>
public partial class RfaMenus : ResourceDictionary
{
    public RfaMenus() => InitializeComponent();

    private static RfaWorkspace Model => RfaModule.Workspace;

    private void OnChoosePreviewMesh(object sender, RoutedEventArgs e)
    {
        if (Model.ActiveDocument is ClipDocumentViewModel clip)
            clip.RequestPreviewPicker();
    }

    private void OnSelectAllBones(object sender, RoutedEventArgs e)
    {
        if (Model.ActiveDocument is { } d) d.Selection.Set(Enumerable.Range(0, d.Scene.Skeleton.Count));
    }

    private void OnClearBones(object sender, RoutedEventArgs e) => Model.ActiveDocument?.Selection.Clear();

    private void OnChoosePreviewClip(object sender, RoutedEventArgs e)
    {
        if (Model.ActiveDocument?.View is not { } view) return;
        if (FindDescendant<ComboBox>(view, c => AutomationProperties.GetName(c) == "Preview clip") is { } box)
        {
            box.Focus();
            box.IsDropDownOpen = true;
        }
    }

    private void OnConvertVersion(object sender, RoutedEventArgs e)
    {
        if (Model.ActiveDocument is ClipDocumentViewModel clip && sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out int version))
        {
            if (clip.Current.Version == version)
            {
                clip.ShowStatus($"The clip is already version {version}.");
                return;
            }
            clip.Apply($"Convert to version {version}", c => ClipEdit.ConvertVersion(c, version));
        }
    }

    private void OnStripMorph(object sender, RoutedEventArgs e)
    {
        if (Model.ActiveDocument is not ClipDocumentViewModel clip) return;
        if (clip.Current.Morph.IsEmpty)
        {
            clip.ShowStatus("The clip has no morph data.");
            return;
        }
        clip.Apply("Strip morph data", ClipEdit.StripMorph);
    }

    private void OnLodClick(object sender, RoutedEventArgs e)
    {
        if (Model.ActiveDocument is { } d && sender is MenuItem { DataContext: string label }
            && int.TryParse(label.Replace("LOD ", "", StringComparison.Ordinal), out int lod))
        {
            d.Scene.Lod = lod;
        }
    }

    private static T? FindDescendant<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found && match(found)) return found;
            if (FindDescendant(child, match) is { } deeper) return deeper;
        }
        return null;
    }
}
