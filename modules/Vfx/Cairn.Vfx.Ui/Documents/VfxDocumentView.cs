using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Cairn.Viewport;
using Cairn.Vfx.Ui.Inspectors;
using Cairn.Vfx.Ui.Outliner;
using Cairn.Vfx.Ui.Viewport;

namespace Cairn.Vfx.Ui.Documents;

/// <summary>Outliner | (convert bar, preview, transport) | inspectors, with resizable side columns.</summary>
public sealed class VfxDocumentView : Grid
{
    public VfxDocumentView(VfxDocument doc)
    {
        Document = doc;
        SetResourceReference(BackgroundProperty, "App.WindowBackground");
        // Viewport gets the room; side widths are remembered per module (settings "vfx.outlinerWidth" / "vfx.inspectorWidth").
        var store = Cairn.Vfx.Ui.VfxGizmoPrefs.Store;
        double W(string key, double fallback) { double w = store?.Get(key, fallback) ?? fallback; return double.IsFinite(w) && w >= 120 && w <= 900 ? w : fallback; }
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(W("outlinerWidth", 200)), MinWidth = 120 });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 240 });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(W("inspectorWidth", 310)), MinWidth = 200 });

        Outliner = new VfxOutliner(doc);
        Add(Outliner, 0);
        var left = Splitter("Resize the object list");
        left.DragCompleted += (_, _) => Cairn.Vfx.Ui.VfxGizmoPrefs.Store?.Set("outlinerWidth", ColumnDefinitions[0].ActualWidth);
        Add(left, 1);

        var centre = new DockPanel();
        var bar = ConvertBar(doc);
        DockPanel.SetDock(bar, Dock.Top);
        centre.Children.Add(bar);
        var transport = new TransportBar { DataContext = doc.Playback };
        DockPanel.SetDock(transport, Dock.Bottom);
        centre.Children.Add(transport);
        Viewport = new VfxViewport(doc, new TextureService(() => doc.Resolver));
        centre.Children.Add(Viewport);
        Add(centre, 2);

        var right = Splitter("Resize the inspector");
        right.DragCompleted += (_, _) => Cairn.Vfx.Ui.VfxGizmoPrefs.Store?.Set("inspectorWidth", ColumnDefinitions[4].ActualWidth);
        Add(right, 3);
        Inspectors = new VfxInspectors(doc);
        Add(Inspectors, 4);
    }

    public VfxDocument Document { get; }
    public VfxOutliner Outliner { get; }
    public VfxViewport Viewport { get; }
    public VfxInspectors Inspectors { get; }

    private void Add(UIElement e, int column) { SetColumn(e, column); Children.Add(e); }

    private static GridSplitter Splitter(string name)
    {
        var s = new GridSplitter { Width = 4, HorizontalAlignment = HorizontalAlignment.Stretch, ToolTip = name, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        s.SetResourceReference(BackgroundProperty, "App.SubtleBorder");
        AutomationProperties.SetName(s, name);
        return s;
    }

    private static Border ConvertBar(VfxDocument doc)
    {
        var text = new TextBlock { Text = "This effect uses an older format version. It can be viewed and saved unchanged; convert it to the current version to edit it.", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Banner.Foreground");
        var button = new Button { Content = "Convert", Command = doc.ConvertCommand, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2), ToolTip = "Convert to the current format version (undoable)" };
        AutomationProperties.SetName(button, "Convert to current format");
        var dock = new DockPanel();
        DockPanel.SetDock(button, Dock.Right);
        dock.Children.Add(button); dock.Children.Add(text);
        var border = new Border { Child = dock, Padding = new Thickness(8, 4, 8, 4), BorderThickness = new Thickness(0, 0, 0, 1) };
        border.SetResourceReference(Border.BackgroundProperty, "Banner.InfoBackground");
        border.SetResourceReference(Border.BorderBrushProperty, "Banner.InfoBorder");
        border.SetBinding(VisibilityProperty, new Binding(nameof(VfxDocument.IsOlderVersion)) { Source = doc, Converter = new BooleanToVisibilityConverter() });
        return border;
    }
}
