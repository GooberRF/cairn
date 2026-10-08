using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Cairn.Ui.Diagnostics;

/// <summary>
/// Self-test helper for a <see cref="ListView"/> in <see cref="GridView"/> mode: counts what is really drawn (header
/// rows, row presenters, cells), so a test can tell columns from rows that fell back to their text.
/// </summary>
public static class GridListCheck
{
    /// <summary>Header row presenters, row presenters and cells (children of the row presenters) in the list's visual tree.</summary>
    public static (int Headers, int Rows, int Cells) Count(ListView list)
    {
        ArgumentNullException.ThrowIfNull(list);
        list.UpdateLayout();
        int headers = 0, rows = 0, cells = 0;
        Walk(list, d =>
        {
            if (d is GridViewHeaderRowPresenter) headers++;
            else if (d is GridViewRowPresenter row) { rows++; cells += VisualTreeHelper.GetChildrenCount(row); }
        });
        return (headers, rows, cells);
    }

    /// <summary>The list's style, template and first container, for a self-test log.</summary>
    public static string Describe(ListView list)
    {
        ArgumentNullException.ThrowIfNull(list);
        var s = new StringBuilder();
        s.Append($"ListView: View {list.View?.GetType().Name ?? "none"}, Style {Chain(list.Style)} ({DependencyPropertyHelper.GetValueSource(list, FrameworkElement.StyleProperty).BaseValueSource}), Template target {list.Template?.TargetType?.Name ?? "none"}, ItemContainerStyle {Chain(list.ItemContainerStyle)}");
        if (list.ItemContainerGenerator.ContainerFromIndex(0) is ListViewItem item)
        {
            s.Append($"; first item: Style {Chain(item.Style)} ({DependencyPropertyHelper.GetValueSource(item, FrameworkElement.StyleProperty).BaseValueSource}), Template target {item.Template?.TargetType?.Name ?? "none"}");
            int presenters = 0;
            Walk(item, d => { if (d is ContentPresenter or GridViewRowPresenter) presenters++; });
            s.Append($", {presenters} presenters");
        }
        return s.ToString();
    }

    private static string Chain(Style? style)
    {
        if (style is null) return "none";
        var parts = new List<string>();
        for (var s = style; s is not null && parts.Count < 8; s = s.BasedOn) parts.Add(s.TargetType?.Name ?? "?");
        return string.Join(" <- ", parts);
    }

    private static void Walk(DependencyObject root, Action<DependencyObject> visit)
    {
        visit(root);
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(root, i), visit);
    }
}
