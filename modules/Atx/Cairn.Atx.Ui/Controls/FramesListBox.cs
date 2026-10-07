using System.Collections;
using System.Windows.Controls;

namespace Cairn.Atx.Ui.Controls;

/// <summary>
/// The frames list, with one addition to the stock <see cref="ListBox"/>: a way to replace the
/// whole selection in a single operation.
///
/// It matters because the list is virtualised. A <c>ListBoxItem.IsSelected</c> binding only reaches
/// the containers that happen to be realised, so a selection made in the view-model and a selection
/// made with the mouse were two different answers — and Delete used the wrong one. The list box is
/// the authority now, and this is how the view-model asks it to change its mind without raising
/// <c>SelectionChanged</c> once per row (two thousand inspector refreshes on a long sequence).
/// </summary>
public sealed class FramesListBox : ListBox
{
    /// <summary>Makes the selection exactly <paramref name="items"/>, in one change.</summary>
    /// <param name="items">The items to select; empty clears the selection.</param>
    public void SetSelection(IEnumerable items) => SetSelectedItems(items);
}
