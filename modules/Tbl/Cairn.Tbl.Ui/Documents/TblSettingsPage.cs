using System.Windows;
using System.Windows.Controls;
using Cairn.Ui.Modules;

namespace Cairn.Tbl.Ui.Documents;

/// <summary>The "Tables" settings page: which problems to report, completion pop-up, word wrap.</summary>
public sealed class TblSettingsPage : ISettingsPage
{
    private readonly TblModule _module;
    private readonly CheckBox _errors = Box("Report errors", "Problems that stop the game from loading the table");
    private readonly CheckBox _warnings = Box("Report warnings", "Likely mistakes the game tolerates (unknown names, values out of range, missing files)");
    private readonly CheckBox _info = Box("Report information", "Notes such as duplicate names and text the game skips");
    private readonly CheckBox _auto = Box("Open completion while typing", "After $ or + at the start of a line, and after a field's colon");
    private readonly CheckBox _wrap = Box("Wrap long lines", "Wrap lines in the table editor instead of scrolling sideways");
    private StackPanel? _view;

    internal TblSettingsPage(TblModule module) => _module = module;

    public string Title => "Tables";

    public FrameworkElement View => _view ??= BuildView();

    private StackPanel BuildView()
    {
        var panel = new StackPanel { Margin = new Thickness(4) };
        panel.Children.Add(Header("PROBLEMS"));
        panel.Children.Add(_errors); panel.Children.Add(_warnings); panel.Children.Add(_info);
        panel.Children.Add(Header("EDITOR"));
        panel.Children.Add(_auto); panel.Children.Add(_wrap);
        return panel;
    }

    private static TextBlock Header(string text)
    {
        var block = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 6), Opacity = 0.8 };
        block.SetResourceReference(TextBlock.ForegroundProperty, "App.Text");
        return block;
    }

    private static CheckBox Box(string text, string tip)
    {
        var box = new CheckBox { Content = text, ToolTip = tip, Margin = new Thickness(0, 0, 0, 6) };
        System.Windows.Automation.AutomationProperties.SetName(box, text);
        return box;
    }

    public void Load()
    {
        var s = _module.Settings;
        _errors.IsChecked = s.LintErrors; _warnings.IsChecked = s.LintWarnings; _info.IsChecked = s.LintInformation;
        _auto.IsChecked = s.CompletionAutoPopup; _wrap.IsChecked = s.WordWrap;
    }

    public void Commit()
    {
        var s = _module.Settings;
        s.LintErrors = _errors.IsChecked == true; s.LintWarnings = _warnings.IsChecked == true; s.LintInformation = _info.IsChecked == true;
        s.CompletionAutoPopup = _auto.IsChecked == true; s.WordWrap = _wrap.IsChecked == true;
        s.RaiseChanged();
    }
}
