using System.Windows.Controls;
using System.Windows.Input;
using Cairn.Rfa.Ui.ViewModels;

namespace Cairn.Rfa.Ui.Views;

/// <summary>
/// The Problems panel. Selecting a row (click, arrows, Enter) selects what the problem points at —
/// the bone, the header field, the key's time or the mesh node.
/// </summary>
public partial class ProblemsView : UserControl
{
    public ProblemsView()
    {
        InitializeComponent();
        List.SelectionChanged += (_, _) => Reveal();
        List.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Reveal();
                e.Handled = true;
            }
        };
    }

    private void Reveal()
    {
        if (List.SelectedItem is DiagnosticViewModel item) item.RevealCommand.Execute(null);
    }
}
