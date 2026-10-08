using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Cairn.Ui.Modules;

namespace Cairn.Shell;

/// <summary>One module's creatable kinds on the welcome view.</summary>
public sealed record WelcomeGroup(string Module, IReadOnlyList<WelcomeKind> Kinds);

/// <summary>A "New ..." button: the kind is the shell's <c>NewCommand</c> parameter.</summary>
public sealed record WelcomeKind(string Label, string ToolTip, IDocumentKind Kind);

/// <summary>
/// A recent file: name (for a packfile entry "packfile › entry"), folder, the item passed to <c>OpenRecentCommand</c>
/// (a path or an entry reference) and the tooltip.
/// </summary>
public sealed record WelcomeRecent(string Name, string Folder, string Path, string ToolTip);

/// <summary>A loaded module and the file types it handles.</summary>
public sealed record WelcomeModule(string Name, string Handles);

/// <summary>Shown in the document area while no document is open: new, open, recent files, loaded modules.</summary>
public partial class WelcomeView : UserControl
{
    private ShellViewModel? _shell;

    public WelcomeView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as ShellViewModel);
        IsVisibleChanged += (_, _) => { if (IsVisible) RefreshRecent(); };
    }

    private void Attach(ShellViewModel? shell)
    {
        if (_shell != null) _shell.PropertyChanged -= OnShellChanged;
        _shell = shell;
        if (shell == null) return;
        shell.PropertyChanged += OnShellChanged;
        FillModules(shell);
        RefreshRecent();
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.RecentFiles) or null) RefreshRecent();
    }

    private void FillModules(ShellViewModel shell)
    {
        var modules = shell.Modules.Where(m => m.DocumentKinds.Count > 0 || m.Importers.Count > 0 || m.Menus.Count > 0 || m.Panels.Count > 0).ToList();
        NewGroups.ItemsSource = modules
            .Select(m => new WelcomeGroup(m.DisplayName, [.. m.DocumentKinds.Where(k => k.CanCreateNew)
                .Select(k => new WelcomeKind($"New {k.DisplayName}…", $"Start a new {k.DisplayName} ({Extensions(k)})", k))]))
            .Where(g => g.Kinds.Count > 0).ToList();
        NewSection.Visibility = NewGroups.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        ModuleList.ItemsSource = modules.Select(m => new WelcomeModule(m.DisplayName,
            m.DocumentKinds.Count == 0 ? "Tools only" : string.Join(" · ", m.DocumentKinds.Select(k => $"{k.DisplayName} ({Extensions(k)})")))).ToList();
        NoModules.Visibility = modules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var all = shell.Kinds.SelectMany(k => k.Extensions).Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // The full list is too long for one line and wraps mid-extension, so it goes in the tooltip; the Modules
        // section below names every type anyway.
        DropHint.Text = "Drop files here to open them.";
        DropHint.ToolTip = all.Count == 0 ? null : $"Cairn opens {Join(all)} files.";
        Summary.Text = modules.Count == 0
            ? "A workbench for Red Faction files. Modules add the file types it can open and edit."
            : $"A workbench for Red Faction files: {Join([.. modules.Select(m => m.DisplayName)], "and")}.";
    }

    /// <summary>Re-reads the shell's recent files; called when they change and whenever the view becomes visible.</summary>
    public void RefreshRecent()
    {
        if (_shell == null) return;
        var recent = _shell.RecentFiles.Select(item =>
        {
            var (name, folder) = ShellViewModel.DescribeRecent(item);
            return new WelcomeRecent(name, folder, item, Cairn.Workspace.RecentFilesList.TryParseEntry(item, out var archive, out var entry) ? $"{entry} in {archive}" : item);
        }).ToList();
        RecentList.ItemsSource = recent;
        RecentSection.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSettings(object sender, RoutedEventArgs e) => _shell?.ShowSettings();

    private static string Normalize(string ext) => ext.StartsWith('.') ? ext.ToLowerInvariant() : "." + ext.ToLowerInvariant();

    private static string Extensions(IDocumentKind kind) => string.Join(", ", kind.Extensions.Select(Normalize));

    /// <summary>"a, b or c" (the drop hint), or with <paramref name="conjunction"/> "and" for the module list.</summary>
    private static string Join(IReadOnlyList<string> items, string conjunction = "or") =>
        items.Count <= 1 ? string.Join("", items) : string.Join(", ", items.Take(items.Count - 1)) + $" {conjunction} " + items[^1];
}
