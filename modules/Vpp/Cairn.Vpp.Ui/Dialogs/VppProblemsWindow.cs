using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Cairn.Vpp.Ui.Documents;
using Cairn.Vpp.Validation;

namespace Cairn.Vpp.Ui.Dialogs;

/// <summary>The packfile's problems (non-modal); double-click or Enter selects the entry a problem is about.</summary>
public sealed class VppProblemsWindow : Window
{
    private readonly ListBox _list;

    private VppProblemsWindow(VppDocument doc, IReadOnlyList<VppProblem> problems)
    {
        Title = $"Problems in {doc.DisplayName}";
        Width = 640; Height = 380; MinWidth = 360; MinHeight = 200;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "App.WindowBackground");
        var root = new DockPanel { Margin = new Thickness(12) };
        var heading = new TextBlock
        {
            Text = problems.Count == 0 ? "No problems: the game can load this packfile." :
                $"{problems.Count(p => p.Severity == VppSeverity.Error)} error(s) stop saving; warnings and notes do not. Double-click a line to select its entry.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "App.Text");
        DockPanel.SetDock(heading, Dock.Top);
        root.Children.Add(heading);
        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true, MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        close.SetResourceReference(StyleProperty, "DialogButton");
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(close);
        _list = new ListBox { ItemsSource = problems.OrderByDescending(p => p.Severity).ThenBy(p => p.EntryName, StringComparer.OrdinalIgnoreCase).ToList() };
        AutomationProperties.SetName(_list, "Problems");
        _list.SetResourceReference(BackgroundProperty, "App.PaneBackground");
        _list.SetResourceReference(ForegroundProperty, "App.Text");
        _list.SetResourceReference(BorderBrushProperty, "App.Border");
        _list.ItemTemplate = (DataTemplate)FindResource("Vpp.ProblemTemplate");
        _list.MouseDoubleClick += (_, _) => Reveal(doc);
        _list.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Reveal(doc); e.Handled = true; } };
        root.Children.Add(_list);
        Content = root;
    }

    private void Reveal(VppDocument doc)
    {
        if (_list.SelectedItem is VppProblem { EntryName: { } name }) doc.SelectNames([name]);
    }

    /// <summary>Shows the problems of <paramref name="doc"/> (the live list unless <paramref name="problems"/> is given).</summary>
    public static Window Show(VppDocument doc, IReadOnlyList<VppProblem>? problems = null)
    {
        var window = new VppProblemsWindow(doc, problems ?? doc.Problems) { Owner = doc.Shell.MainWindow };
        window.Show();
        return window;
    }
}
