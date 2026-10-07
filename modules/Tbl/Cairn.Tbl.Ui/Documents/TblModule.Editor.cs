using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Cairn.Tbl.Ui.Documents;
using Cairn.Tbl.Ui.Editor;
using Cairn.Ui.Documents;
using Cairn.Ui.Modules;
using Cairn.Ui.Mvvm;

namespace Cairn.Tbl.Ui;

// The editor half of the module: problems panel, editor shortcuts and menu items, help, settings, resources.
public sealed partial class TblModule
{
    private readonly Dictionary<TblDocument, TblProblemsView> _problems = [];
    private ResourceDictionary? _resources;
    private TblSettingsPage? _settingsPage;

    /// <summary>The shell (null before <see cref="Initialize"/>).</summary>
    internal IShellContext? ShellContext => Shell;

    private static bool IsTable(IDocument? document) => document is TblDocument;

    private TblDocument? Active => Shell?.ActiveDocument as TblDocument;

    private TblEditorController? ActiveController => Active?.DocumentView?.Controller;

    private void AddEditorPanels(List<PanelContribution> panels)
    {
        panels.Add(new PanelContribution(ProblemsPanelId, "Problems", PanelSide.Bottom, 10, document => document is TblDocument table ? ProblemsFor(table) : null));
    }

    /// <summary>The problems view of <paramref name="document"/> (one per document, created on first use).</summary>
    internal TblProblemsView ProblemsFor(TblDocument document)
    {
        if (!_problems.TryGetValue(document, out var view))
        {
            view = new TblProblemsView(document);
            _problems[document] = view;
        }
        return view;
    }

    internal void ForgetProblems(TblDocument document)
    {
        if (_problems.Remove(document, out var view)) view.Dispose();
    }

    /// <summary>The bottom Problems tab's id.</summary>
    internal const string ProblemsPanelId = "tbl.problems";

    /// <summary>Brings the problems list forward (status-bar counts).</summary>
    internal void ShowProblems(TblDocument document)
    {
        var view = ProblemsFor(document);
        if (Shell?.ShowPanel(ProblemsPanelId) == true)
        {
            // The tab's content is laid out after the selection: focus the first row once it is visible.
            Shell.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => { if (view.IsVisible) view.FocusFirst(); });
        }
        else if (view.IsVisible) view.FocusFirst();
        else Shell?.ShowStatus("The problems are listed in the bottom pane's Problems tab (View > Bottom Pane).");
    }

    private void AddEditorShortcuts(List<ShortcutInfo> shortcuts)
    {
        ShortcutInfo Make(string description, Key key, ModifierKeys modifiers, Action<TblEditorController> run) =>
            new("Table editor", description, key, modifiers, new RelayCommand(() => { if (ActiveController is { } c) run(c); }, () => ActiveController is not null), IsTable, AllowInTextInput: true);

        shortcuts.Add(Make("Quick fixes for the problem at the caret", Key.OemPeriod, ModifierKeys.Control, c => c.ShowQuickFixes()));
        shortcuts.Add(Make("Toggle // comment on the selected lines", Key.OemQuestion, ModifierKeys.Control, c => c.ToggleComment()));
        shortcuts.Add(Make("Go to line", Key.G, ModifierKeys.Control, c => c.PromptGoToLine()));
        shortcuts.Add(Make("Find", Key.F, ModifierKeys.Control, c => Active?.DocumentView?.OpenFind(replace: false)));
        shortcuts.Add(Make("Replace", Key.H, ModifierKeys.Control, c => Active?.DocumentView?.OpenFind(replace: true)));
        shortcuts.Add(Make("Find next", Key.F3, ModifierKeys.None, c => c.FindNext(forward: true)));
        shortcuts.Add(Make("Find previous", Key.F3, ModifierKeys.Shift, c => c.FindNext(forward: false)));
        shortcuts.Add(Make("Next problem", Key.F8, ModifierKeys.None, c => Active?.GoToProblem(forward: true)));
        shortcuts.Add(Make("Previous problem", Key.F8, ModifierKeys.Shift, c => Active?.GoToProblem(forward: false)));
        // Ctrl+Space stays with the editor (the shell yields it to text input); listed for the shortcut table.
        shortcuts.Add(new ShortcutInfo("Table editor", "Complete (fields, values, names, files)", Key.Space, ModifierKeys.Control,
            new RelayCommand(() => ActiveController?.ShowCompletion(), () => ActiveController is not null), IsTable));
    }

    private void AddEditorMenus(List<MenuContribution> menus)
    {
        MenuItem Item(string header, string gesture, string tip, Action<TblEditorController> run)
        {
            var item = new MenuItem
            {
                Header = header, InputGestureText = gesture, ToolTip = tip,
                Command = new RelayCommand(() => { if (ActiveController is { } c) run(c); }, () => ActiveController is not null),
            };
            AutomationPropertiesHelper.Name(item, header.Replace("_", string.Empty, StringComparison.Ordinal));
            return item;
        }

        var newTable = new MenuItem { Header = "_Table", ToolTip = "A new empty table with a comment header", Command = new RelayCommand(() => { if (Kind.CreateNew() is { } d) Shell?.AddDocument(d); }) };
        menus.Add(new MenuContribution(MenuSlot.FileNew, 50, newTable));
        menus.Add(new MenuContribution(MenuSlot.Edit, 50, new Separator(), IsTable));
        menus.Add(new MenuContribution(MenuSlot.Edit, 51, Item("_Find...", "Ctrl+F", "Find in the table", _ => Active?.DocumentView?.OpenFind(replace: false)), IsTable));
        menus.Add(new MenuContribution(MenuSlot.Edit, 52, Item("_Replace...", "Ctrl+H", "Find and replace in the table", _ => Active?.DocumentView?.OpenFind(replace: true)), IsTable));
        menus.Add(new MenuContribution(MenuSlot.Edit, 53, Item("_Go to Line...", "Ctrl+G", "Jump to a line number", c => c.PromptGoToLine()), IsTable));
        menus.Add(new MenuContribution(MenuSlot.Edit, 54, Item("Toggle _Comment", "Ctrl+/", "Comment or uncomment the selected lines with //", c => c.ToggleComment()), IsTable));
        menus.Add(new MenuContribution(MenuSlot.Edit, 55, Item("_Quick Fixes...", "Ctrl+.", "Fixes for the problem at the caret", c => c.ShowQuickFixes()), IsTable));
        menus.Add(new MenuContribution(MenuSlot.Edit, 56, Item("Co_mplete", "Ctrl+Space", "Fields, values, names and files valid at the caret", c => c.ShowCompletion()), IsTable));
    }

    public override IReadOnlyList<HelpTopic> HelpTopics => [new HelpTopic("tbl.syntax", "Table syntax", TblHelp.Build)];

    public override IReadOnlyList<ISettingsPage> SettingsPages => [_settingsPage ??= new TblSettingsPage(this)];

    public override IReadOnlyList<ResourceDictionary> Resources => [_resources ??= new ResourceDictionary
    {
        Source = new Uri("pack://application:,,,/Cairn.Tbl.Ui;component/Themes/TblResources.xaml", UriKind.Absolute),
    }];
}
