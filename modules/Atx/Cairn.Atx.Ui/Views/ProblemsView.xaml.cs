using System.Windows.Controls;
using System.Windows.Input;
using Cairn.Atx.Ui.ViewModels;

namespace Cairn.Atx.Ui.Views;

/// <summary>
/// The Problems panel. Selecting a row selects the problem's span in the source editor and the
/// frame it belongs to in the GUI, which is the one behaviour the list cannot express in XAML.
/// </summary>
public partial class ProblemsView : UserControl
{
    public ProblemsView()
    {
        InitializeComponent();
        List.SelectionChanged += OnSelectionChanged;
        List.MouseDoubleClick += (_, _) => Activate();
        List.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Activate(); e.Handled = true; }
        };
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (List.SelectedItem is DiagnosticViewModel item) item.RevealCommand.Execute(null);
    }

    private void Activate()
    {
        if (List.SelectedItem is DiagnosticViewModel item) item.GoToCommand.Execute(null);
    }
}
