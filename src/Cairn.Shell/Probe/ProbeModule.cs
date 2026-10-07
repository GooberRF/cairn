using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using Cairn.Ui.Documents;
using Cairn.Ui.Modules;
using Cairn.Ui.Mvvm;
using Cairn.Workspace;

namespace Cairn.Shell.Probe;

/// <summary>
/// Diagnostics-only module (enabled by <c>--probe-module</c>): a plain-text document kind that
/// exercises the whole module contract so the shell self-tests can run without any real module.
/// </summary>
public sealed class ProbeModule : ModuleBase
{
    private readonly ProbeKind _kind = new();
    private readonly List<MenuContribution> _menus = [];
    private readonly List<ShortcutInfo> _shortcuts = [];

    public override string Id => "probe";
    public override string DisplayName => "Probe";
    public override IReadOnlyList<IDocumentKind> DocumentKinds => [_kind];
    public override IReadOnlyList<MenuContribution> Menus => _menus;
    public override IReadOnlyList<ShortcutInfo> Shortcuts => _shortcuts;
    public override IReadOnlyList<PanelContribution> Panels { get; } =
    [
        // Listed out of order on purpose: the shell must sort tabs (and pick the default bottom tab) by Order.
        new("probe.lines", "Lines", PanelSide.Bottom, 1, d => d is ProbeDocument { Kind.Id: "probe" } p ? new TextBlock { Text = $"{p.Text.Split('\n').Length} lines", Margin = new Thickness(8) } : null),
        new("probe.length", "Probe", PanelSide.Bottom, 0, d => d is ProbeDocument p && !p.Text.StartsWith("#nopanel", StringComparison.Ordinal) ? new TextBlock { Text = $"{p.Text.Length} characters", Margin = new Thickness(8) } : null),
    ];
    public override IReadOnlyList<ISettingsPage> SettingsPages { get; } = [new ProbeSettingsPage()];
    public override IReadOnlyList<HelpTopic> HelpTopics { get; } =
        [new("probe", "Probe Documents", () => new FlowDocument(new Paragraph(new Run("Probe documents are plain text used by the self-tests."))))];

    public ICommand ShoutCommand { get; }

    /// <summary>The documents open when the shell applied the diagnostic options (null until then); checked by <c>probe.options-after-files</c>.</summary>
    internal static int? DocumentsAtOptions { get; private set; }
    public override void ApplyDiagnosticOptions(IReadOnlyDictionary<string, string> options) => DocumentsAtOptions = Shell.Documents.Count;

    public ProbeModule()
    {
        static bool IsProbe(IDocument? d) => d is ProbeDocument { Kind.Id: "probe" };
        ShoutCommand = new RelayCommand(() => (Shell.ActiveDocument as ProbeDocument)?.Apply("Shout", t => t + "!"));
        var menu = new MenuItem { Header = "_Probe", Name = "ProbeMenu" };
        menu.Items.Add(new MenuItem { Header = "_Shout", Command = ShoutCommand, InputGestureText = "Ctrl+Shift+P" });
        _menus.Add(new(MenuSlot.TopLevel, 0, menu, IsProbe));
        _shortcuts.Add(new("Probe", "Append an exclamation mark", Key.P, ModifierKeys.Control | ModifierKeys.Shift, ShoutCommand, IsProbe));
        // Gestures without modifiers (a KeyBinding cannot carry these): Space and a letter, as a player or viewport would use.
        _shortcuts.Add(new("Probe", "Append an underscore", Key.Space, ModifierKeys.None,
            new RelayCommand(() => (Shell.ActiveDocument as ProbeDocument)?.Apply("Space", t => t + "_")), IsProbe));
        _shortcuts.Add(new("Probe", "Append F", Key.F, ModifierKeys.None,
            new RelayCommand(() => (Shell.ActiveDocument as ProbeDocument)?.Apply("Frame", t => t + "F")), IsProbe));
    }

    public override void Initialize(IShellContext shell)
    {
        base.Initialize(shell);
        _kind.Shell = shell;
    }
}

/// <summary>
/// Second diagnostics module (also enabled by <c>--probe-module</c>): a kind "probe2" whose shortcut uses the same
/// gesture as the probe module's, so the self-tests can check that the active document picks the module.
/// </summary>
public sealed class ProbeTwoModule : ModuleBase
{
    private readonly ProbeKind _kind = new("probe2", "Second probe document", ".cairnprobe2", canCreateNew: false);
    private readonly List<ShortcutInfo> _shortcuts = [];

    public override string Id => "probe2";
    public override string DisplayName => "Probe Two";
    public override IReadOnlyList<IDocumentKind> DocumentKinds => [_kind];
    public override IReadOnlyList<ShortcutInfo> Shortcuts => _shortcuts;
    public ProbeKind Kind => _kind;

    public ProbeTwoModule()
    {
        // Unscoped on purpose: for a probe document both modules' Ctrl+Shift+P apply and the owner must win.
        _shortcuts.Add(new("Probe two", "Append a question mark", Key.P, ModifierKeys.Control | ModifierKeys.Shift,
            new RelayCommand(() => (Shell.ActiveDocument as ProbeDocument)?.Apply("Ask", t => t + "?"))));
        // Same gesture as the shell's left pane toggle: wins over the shell for probe2 documents only.
        _shortcuts.Add(new("Probe two", "Append a less-than sign", Key.L, ModifierKeys.Control | ModifierKeys.Shift,
            new RelayCommand(() => (Shell.ActiveDocument as ProbeDocument)?.Apply("Less", t => t + "<")), d => d is ProbeDocument { Kind.Id: "probe2" }));
    }

    public override void Initialize(IShellContext shell)
    {
        base.Initialize(shell);
        _kind.Shell = shell;
    }
}

public sealed class ProbeKind(string id = "probe", string displayName = "Probe document", string extension = ".cairnprobe", bool canCreateNew = true) : IDocumentKind
{
    public IShellContext Shell { get; set; } = null!;
    public string Id => id;
    public string DisplayName => displayName;
    public IReadOnlyList<string> Extensions { get; } = [extension];
    public string FileFilter => $"{displayName}s (*{extension})|*{extension}";
    public bool CanCreateNew => canCreateNew;
    public string AssociationDescription => "Cairn " + displayName.ToLowerInvariant();
    public IDocument? CreateNew() { var d = new ProbeDocument(Shell, this, string.Empty, "Untitled" + extension, null); d.MarkAsNew(); return d; }
    public IDocument Open(string path) => new ProbeDocument(Shell, this, File.ReadAllText(path), Path.GetFileName(path), path);
    public IDocument OpenBytes(byte[] bytes, string displayName, string originText) => new ProbeDocument(Shell, this, Encoding.UTF8.GetString(bytes), displayName, null, originText);
    public IDocument Restore(RecoverySnapshot snapshot)
    {
        var d = new ProbeDocument(Shell, this, Encoding.UTF8.GetString(snapshot.Data), snapshot.DisplayName, snapshot.OriginalPath);
        d.MarkAsNew();
        return d;
    }
}

public sealed class ProbeDocument(IShellContext shell, IDocumentKind kind, string text, string name, string? path, string? origin = null)
    : SnapshotDocument<string>(shell, kind, text, name, path, origin)
{
    public string Text { get => Current; set => Apply("Edit text", _ => value); }

    public override IReadOnlyList<StatusItem> StatusItems => [new($"{Current.Length} chars", "Length of the probe text")];

    protected override string Parse(byte[] bytes, string name) => Encoding.UTF8.GetString(bytes);
    protected override byte[] Write(string snapshot) => Encoding.UTF8.GetBytes(snapshot);
    protected override void OnSnapshotChanged() => RaiseAll(nameof(Text), nameof(StatusItems));

    protected override FrameworkElement CreateView()
    {
        var box = new TextBox { AcceptsReturn = true, Margin = new Thickness(8), ToolTip = "Probe text" };
        box.SetBinding(TextBox.TextProperty, new Binding(nameof(Text)) { Source = this, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
        System.Windows.Automation.AutomationProperties.SetName(box, "Probe text");
        return box;
    }
}

public sealed class ProbeSettingsPage : ISettingsPage
{
    private readonly CheckBox _box = new() { Content = "Probe option", Margin = new Thickness(16) };
    public string Title => "Probe";
    public FrameworkElement View => _box;
    public void Load() => _box.IsChecked = false;
    public void Commit() { }
}
