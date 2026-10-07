using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Cairn.Shell.Dialogs;

/// <summary>The File associations settings page: rows grouped by module (see <see cref="AssociationsModel"/>).</summary>
public partial class AssociationsPageView : UserControl
{
    public AssociationsPageView(AssociationsModel model)
    {
        InitializeComponent();
        DataContext = model;
        var rows = new ListCollectionView(model.Rows.ToList());
        rows.GroupDescriptions.Add(new PropertyGroupDescription(nameof(AssociationRow.Group)));
        List.ItemsSource = rows;
        model.Owner = () => Window.GetWindow(this);
    }
}
