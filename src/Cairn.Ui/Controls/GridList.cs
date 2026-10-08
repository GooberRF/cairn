using System.Windows;
using System.Windows.Controls;

namespace Cairn.Ui.Controls;

/// <summary>
/// A <see cref="ListView"/> for a <see cref="GridView"/> (columns with a header row), themed like the rest of the app.
/// Use it instead of a plain ListView: the Fluent theme's implicit ListView and ListViewItem styles do not support
/// GridView, so a plain one loses its header and shows each row as its <c>ToString()</c>. Being a subclass keeps the
/// Fluent ListView style (keyed by the exact type) away, so the GridView template applies; rows and headers get the
/// <c>GridListItem</c> and <c>GridListHeader</c> styles. Columns are added with <see cref="Column"/>. While it has no
/// rows the list is hidden (<see cref="HideWhenEmpty"/>), so a panel's empty-state text never sits over its headings.
/// </summary>
public class GridList : ListView
{
    /// <summary>An empty list with an empty <see cref="GridView"/> (<see cref="Columns"/>).</summary>
    public GridList()
    {
        BorderThickness = new Thickness(0);
        SetResourceReference(BackgroundProperty, "App.PaneBackground");
        SetResourceReference(ForegroundProperty, "App.Text");
        SetResourceReference(ItemContainerStyleProperty, "GridListItem");
        View = new GridView { ColumnHeaderContainerStyle = Application.Current?.TryFindResource("GridListHeader") as Style };
        UpdateEmpty();
    }

    /// <summary>
    /// True (the default) to hide the whole list, header row included, while it has no rows. An empty grid would only
    /// show a bare header row, and a panel's own "nothing found" text placed over the list would overlap its headings:
    /// panels show their empty-state text in the list's place instead.
    /// </summary>
    public bool HideWhenEmpty
    {
        get => _hideWhenEmpty;
        set { _hideWhenEmpty = value; UpdateEmpty(); }
    }

    private bool _hideWhenEmpty = true;

    /// <inheritdoc/>
    protected override void OnItemsChanged(System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        base.OnItemsChanged(e);
        UpdateEmpty();
    }

    private void UpdateEmpty() => Visibility = _hideWhenEmpty && Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The grid's columns.</summary>
    public GridViewColumnCollection Columns => ((GridView)View).Columns;

    /// <summary>Adds a text column bound to <paramref name="path"/>.</summary>
    public GridViewColumn Column(string header, double width, string path)
    {
        var column = new GridViewColumn { Header = header, Width = width, DisplayMemberBinding = new System.Windows.Data.Binding(path) };
        Columns.Add(column);
        return column;
    }
}
