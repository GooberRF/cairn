using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;

namespace Cairn.Shell.Dialogs;

/// <summary>Tools &gt; Settings…: a General page, then one page per module <see cref="Cairn.Ui.Modules.ISettingsPage"/>.</summary>
public partial class SettingsDialog : Window
{
    private readonly SettingsViewModel _model;
    private readonly List<TabItem> _moduleTabs = [];
    private bool _committed;

    private SettingsDialog(ShellViewModel shell, string? pageTitle, IAssociationStore? associations = null)
    {
        _model = new SettingsViewModel(shell, associations);
        InitializeComponent();
        DataContext = _model;
        foreach (var page in _model.Pages)
        {
            page.Load();
            // Pages sit at the top and scroll, as the General page does.
            var scroller = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(12, 12, 12, 0),
                Content = new StackPanel { Children = { page.View } },
                // Not the tab header's small semibold font (TabItem fonts reach the page through the logical tree).
                FontWeight = FontWeights.Normal,
            };
            scroller.SetBinding(FontSizeProperty, new System.Windows.Data.Binding(nameof(FontSize)) { Source = this });
            scroller.SetResourceReference(ForegroundProperty, "App.Text");
            var tab = new TabItem { Header = page.Title, Content = scroller, ToolTip = page.Title + " settings" };
            AutomationProperties.SetName(tab, page.Title + " settings");
            _moduleTabs.Add(tab);
            Tabs.Items.Add(tab);
        }
        Tabs.SelectedItem = _moduleTabs.FirstOrDefault(t => string.Equals((string)t.Header, pageTitle, StringComparison.OrdinalIgnoreCase)) ?? Tabs.Items[0];
        Closing += OnClosing;
        // A module's page view outlives the dialog: detach it so the next dialog can host it again.
        Closed += (_, _) => { foreach (var tab in _moduleTabs) ((StackPanel)((ScrollViewer)tab.Content).Content).Children.Clear(); };
        Loaded += (_, _) => { if (Tabs.SelectedIndex == 0) ThemeSystem.Focus(); };
    }

    /// <summary>A dialog instance for the diagnostic capture (never shown modally, never applied); <paramref name="associations"/> replaces the real registry.</summary>
    internal static SettingsDialog CreateForCapture(ShellViewModel shell, string? pageTitle = null, IAssociationStore? associations = null) =>
        new(shell, pageTitle, associations);

    /// <summary>The dialog's model (self-tests).</summary>
    internal SettingsViewModel Model => _model;

    /// <summary>Shows the dialog; <paramref name="pageTitle"/> picks the initial page. Returns true when applied.</summary>
    public static bool Show(ShellViewModel shell, string? pageTitle = null)
    {
        ArgumentNullException.ThrowIfNull(shell);
        var dialog = new SettingsDialog(shell, pageTitle) { Owner = shell.MainWindow is { IsLoaded: true } owner ? owner : null };
        if (dialog.Owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog.ShowDialog() == true;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        _model.Apply();
        _committed = true;
        DialogResult = true;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_committed) _model.Revert();
    }
}
