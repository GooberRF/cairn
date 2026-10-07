using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Cairn.Ui.Modules;

namespace Cairn.Shell.Dialogs;

/// <summary>
/// The non-modal Help window: the keyboard-shortcut table first, then every module's topics.
/// Single-instance: asking for a topic while it is open selects it and brings the window forward,
/// because a reference you keep beside the editor is the point.
/// </summary>
public partial class HelpWindow : Window
{
    /// <summary>The topic id that opens the shortcut table.</summary>
    public const string ShortcutsId = "shortcuts";

    private static HelpWindow? _open;
    private readonly Dictionary<string, FlowDocument> _built = new(StringComparer.Ordinal);

    private HelpWindow(ShellViewModel shell)
    {
        InitializeComponent();
        var topics = new List<HelpTopic> { new(ShortcutsId, "Keyboard Shortcuts", () => Shortcuts(shell)) };
        topics.AddRange(shell.Modules.SelectMany(m => m.HelpTopics));
        Topics.ItemsSource = topics;
        Closed += (_, _) => { if (_open == this) _open = null; };
    }

    /// <summary>The open window, if any (for the diagnostic capture and tests).</summary>
    internal static HelpWindow? Current => _open;

    /// <summary>Shows the window on <paramref name="topicId"/>, reusing the open one.</summary>
    /// <param name="shell">Source of the topics and shortcuts.</param>
    /// <param name="topicId">A module topic id, or <see cref="ShortcutsId"/>.</param>
    public static HelpWindow Show(ShellViewModel shell, string topicId)
    {
        if (_open is null)
        {
            var owner = shell.MainWindow;
            _open = new HelpWindow(shell) { Owner = owner is { IsLoaded: true } ? owner : null };
            if (_open.Owner is null) _open.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            _open.Select(topicId);
            _open.Show();
        }
        else
        {
            _open.Select(topicId);
            if (_open.WindowState == WindowState.Minimized) _open.WindowState = WindowState.Normal;
            _open.Activate();
        }
        return _open;
    }

    /// <summary>Closes the window if it is open; used when the app shuts down.</summary>
    public static void CloseAll() => _open?.Close();

    private void Select(string topicId)
    {
        var topics = (List<HelpTopic>)Topics.ItemsSource;
        Topics.SelectedItem = topics.FirstOrDefault(t => t.Id == topicId) ?? topics[0];
        Topics.ScrollIntoView(Topics.SelectedItem);
    }

    private void OnTopicChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Topics.SelectedItem is not HelpTopic topic) return;
        if (!_built.TryGetValue(topic.Id, out var document))
        {
            document = topic.Build();
            document.SetResourceReference(FlowDocument.BackgroundProperty, "App.WindowBackground");
            document.SetResourceReference(FlowDocument.ForegroundProperty, "App.Text");
            _built[topic.Id] = document;
        }
        Viewer.Document = document;
        Title = "Cairn Help - " + topic.Title;
        Footer.Text = topic.Id == ShortcutsId
            ? "Generated from the same table that installs the key bindings."
            : "Select text and press Ctrl+C to copy it.";
    }

    /// <summary>The shortcut table: <see cref="ShellViewModel.AllShortcuts"/> grouped by category.</summary>
    public static FlowDocument Shortcuts(ShellViewModel shell)
    {
        var doc = new FlowDocument { FontFamily = new System.Windows.Media.FontFamily("Segoe UI"), FontSize = 13, PagePadding = new Thickness(24, 16, 24, 16) };
        doc.Blocks.Add(new Paragraph(new Run("Keyboard Shortcuts")) { FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        // Shell first, then each module (its own shortcuts, including display-only rows), each split by category.
        var owners = new List<(string Name, IReadOnlyList<ShortcutInfo> Rows)> { ("Shell", shell.ShellShortcuts) };
        owners.AddRange(shell.Modules.Where(m => m.Shortcuts.Count > 0).Select(m => (m.DisplayName, m.Shortcuts)));
        foreach (var (name, rows) in owners)
        {
            doc.Blocks.Add(new Paragraph(new Run(name)) { FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 0) });
            foreach (var group in rows.GroupBy(s => s.Category))
            {
                var category = new Paragraph(new Run(group.Key)) { FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) };
                category.SetResourceReference(TextElement.ForegroundProperty, "App.SecondaryText");
                doc.Blocks.Add(category);
                var table = new Table { CellSpacing = 0 };
                table.Columns.Add(new TableColumn { Width = new GridLength(200) });
                table.Columns.Add(new TableColumn());
                var group_rows = new TableRowGroup();
                foreach (var s in group.DistinctBy(s => (s.Key, s.Modifiers, s.Description)))
                {
                    var key = new Paragraph(new Run(Gesture(s.Key, s.Modifiers))) { Margin = new Thickness(0, 2, 0, 2), FontWeight = FontWeights.SemiBold };
                    var description = new Paragraph(new Run(s.Description)) { Margin = new Thickness(0, 2, 0, 2) };
                    group_rows.Rows.Add(new TableRow { Cells = { new TableCell(key), new TableCell(description) } });
                }
                table.RowGroups.Add(group_rows);
                doc.Blocks.Add(table);
            }
        }
        return doc;
    }

    /// <summary>"Ctrl+Shift+S", with readable names for the punctuation keys ("Ctrl+/" rather than "Ctrl+OemQuestion").</summary>
    internal static string Gesture(Key key, ModifierKeys modifiers) =>
        (modifiers == ModifierKeys.None ? string.Empty : modifiers.ToString().Replace(", ", "+", StringComparison.Ordinal).Replace("Control", "Ctrl", StringComparison.Ordinal) + "+") + KeyName(key);

    private static string KeyName(Key key) => key switch
    {
        Key.OemQuestion => "/", Key.OemPlus => "+", Key.OemMinus => "-", Key.OemComma => ",", Key.OemPeriod => ".",
        Key.OemOpenBrackets => "[", Key.OemCloseBrackets => "]", Key.OemSemicolon => ";", Key.OemQuotes => "'",
        Key.OemPipe => "\\", Key.OemTilde => "`", Key.Add => "Num +", Key.Subtract => "Num -",
        Key.Next => "PageDown", Key.Prior => "PageUp", Key.Return => "Enter", Key.Back => "Backspace",
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => key.ToString(),
    };

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
