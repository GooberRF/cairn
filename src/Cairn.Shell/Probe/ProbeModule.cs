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
public sealed class ProbeModule : ModuleBase, IAssetConverter, IAssetPreviewProvider, IArchiveBatchConverter
{
    // ---- Convert to… (shell.convert-to): a probe document converts to plain text, or "shouted" (lossy) ----

    private static bool IsProbeName(string name) => name.EndsWith(".cairnprobe", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<ConversionTarget> TargetsFor(string sourceName) => IsProbeName(sourceName)
        ? [new(".txt", "Plain text (.txt)", "Write the probe text as a .txt file", false), new(".shout", "Shouted text (.shout)", "Upper-case the text into a .shout file", true)]
        : [];

    public bool CanConvert(string sourceName, string targetExtension) =>
        (IsProbeName(sourceName) && targetExtension is ".txt" or ".shout") || ProbeClaims.CanConvert('A', sourceName, targetExtension);

    // ---- content claims (shell.content-claims): ".cairnclaim" files are A's or the second module's B's by content ----

    public IReadOnlyList<ConversionTarget> TargetsFor(string sourceName, ReadOnlySpan<byte> head) =>
        ProbeClaims.IsClaimName(sourceName) ? ProbeClaims.TargetsFor('A', head) : TargetsFor(sourceName);

    public int HeadLengthFor(string sourceName) => ProbeClaims.HeadLengthFor(sourceName);

    public bool CanPreview(string fileName) => ProbeClaims.IsClaimName(fileName);

    public FrameworkElement? CreatePreview(byte[] bytes, string fileName) => ProbeClaims.CreatePreview(Id, bytes);

    int IAssetPreviewProvider.Claim(string fileName, ReadOnlySpan<byte> data) => ProbeClaims.Claim('A', data);

    public string CommandText => "Convert claim probes _A...";

    public string CommandToolTip => "Records the .cairnclaim entries of A (self-tests)";

    bool IArchiveBatchConverter.CanConvert(string entryName) => ProbeClaims.IsClaimName(entryName);

    Task<string?> IArchiveBatchConverter.ConvertAsync(ArchiveBatchRequest request) => ProbeClaims.RecordBatch(Id, request);

    int IArchiveBatchConverter.Claim(string entryName, ReadOnlySpan<byte> data) => ProbeClaims.Claim('A', data);

    /// <summary>The last request converted (self-tests).</summary>
    internal static AssetConversionRequest? LastRequest { get; set; }

    /// <summary>Writes the text next to the output folder (non-interactive only; an interactive request just records itself).</summary>
    public Task<string?> ConvertAsync(AssetConversionRequest request)
    {
        LastRequest = request;
        if (request.Interactive || request.OutputFolder is not { } folder) return Task.FromResult<string?>(null);
        string text = Encoding.UTF8.GetString(request.Bytes);
        string path = Path.Combine(folder, Path.GetFileNameWithoutExtension(request.FileName) + request.TargetExtension);
        File.WriteAllText(path, request.TargetExtension == ".shout" ? text.ToUpperInvariant() : text);
        return Task.FromResult<string?>(path);
    }

    private readonly ProbeKind _kind = new();
    private readonly ProbeKind _claimKind = new("probe.claim", "Claim probe A", ProbeClaims.Extension, canCreateNew: false, claimPrefix: 'A');
    private readonly List<MenuContribution> _menus = [];
    private readonly List<ShortcutInfo> _shortcuts = [];

    public override string Id => "probe";
    public override string DisplayName => "Probe";
    public override IReadOnlyList<IDocumentKind> DocumentKinds => [_kind, _claimKind];
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
        _claimKind.Shell = shell;
    }
}

/// <summary>
/// Second diagnostics module (also enabled by <c>--probe-module</c>): a kind "probe2" whose shortcut uses the same
/// gesture as the probe module's, so the self-tests can check that the active document picks the module. It also
/// takes <c>.cairnclaim</c> files whose content is B's, against the probe module's A (shell.content-claims).
/// </summary>
public sealed class ProbeTwoModule : ModuleBase, IAssetConverter, IAssetPreviewProvider, IArchiveBatchConverter
{
    private readonly ProbeKind _kind = new("probe2", "Second probe document", ".cairnprobe2", canCreateNew: false);
    private readonly ProbeKind _claimKind = new("probe2.claim", "Claim probe B", ProbeClaims.Extension, canCreateNew: false, claimPrefix: 'B');
    private readonly List<ShortcutInfo> _shortcuts = [];

    public override string Id => "probe2";
    public override string DisplayName => "Probe Two";
    public override IReadOnlyList<IDocumentKind> DocumentKinds => [_kind, _claimKind];

    public bool CanConvert(string sourceName, string targetExtension) => ProbeClaims.CanConvert('B', sourceName, targetExtension);

    public Task<string?> ConvertAsync(AssetConversionRequest request) => Task.FromResult<string?>(null);

    public IReadOnlyList<ConversionTarget> TargetsFor(string sourceName, ReadOnlySpan<byte> head) =>
        ProbeClaims.IsClaimName(sourceName) ? ProbeClaims.TargetsFor('B', head) : [];

    public int HeadLengthFor(string sourceName) => ProbeClaims.HeadLengthFor(sourceName);

    public bool CanPreview(string fileName) => ProbeClaims.IsClaimName(fileName);

    public FrameworkElement? CreatePreview(byte[] bytes, string fileName) => ProbeClaims.CreatePreview(Id, bytes);

    int IAssetPreviewProvider.Claim(string fileName, ReadOnlySpan<byte> data) => ProbeClaims.Claim('B', data);

    public string CommandText => "Convert claim probes _B...";

    public string CommandToolTip => "Records the .cairnclaim entries of B (self-tests)";

    bool IArchiveBatchConverter.CanConvert(string entryName) => ProbeClaims.IsClaimName(entryName);

    Task<string?> IArchiveBatchConverter.ConvertAsync(ArchiveBatchRequest request) => ProbeClaims.RecordBatch(Id, request);

    int IArchiveBatchConverter.Claim(string entryName, ReadOnlySpan<byte> data) => ProbeClaims.Claim('B', data);
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
        _claimKind.Shell = shell;
    }
}

/// <summary>
/// The content claims of the probe modules (shell.content-claims): a <c>.cairnclaim</c> file is A's when it starts
/// "A:" and B's when it starts "B:" (kinds, previews, batches), and Convert to lists A's or B's target only when the
/// whole file reaches the converter (its text ends "#A" or "#B", past the default 16-byte head).
/// </summary>
public static class ProbeClaims
{
    public const string Extension = ".cairnclaim";

    public static bool IsClaimName(string name) => name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    public static int Claim(char prefix, ReadOnlySpan<byte> data) =>
        data.Length >= 2 && data[0] == prefix && data[1] == ':' ? ContentClaims.Recognised : ContentClaims.NotMine;

    public static string TargetExtension(char prefix) => ".claim" + char.ToLowerInvariant(prefix);

    public static IReadOnlyList<ConversionTarget> TargetsFor(char prefix, ReadOnlySpan<byte> head) =>
        head.Length >= 2 && head[^2] == '#' && head[^1] == prefix
            ? [new(TargetExtension(prefix), $"Claim {prefix} text ({TargetExtension(prefix)})", "Write the text (self-tests)", false)]
            : [];

    public static bool CanConvert(char prefix, string sourceName, string targetExtension) =>
        IsClaimName(sourceName) && targetExtension == TargetExtension(prefix);

    public static int HeadLengthFor(string sourceName) => IsClaimName(sourceName) ? ContentClaims.WholeFile : ContentClaims.DefaultHeadLength;

    /// <summary>The module whose preview was created last.</summary>
    internal static string? LastPreviewBy { get; set; }

    public static FrameworkElement CreatePreview(string moduleId, byte[] bytes)
    {
        LastPreviewBy = moduleId;
        return new TextBlock { Text = moduleId + ": " + Encoding.UTF8.GetString(bytes), Margin = new Thickness(8) };
    }

    /// <summary>The module and entry names of the last batch run.</summary>
    internal static (string ModuleId, IReadOnlyList<string> Entries)? LastBatch { get; set; }

    public static Task<string?> RecordBatch(string moduleId, ArchiveBatchRequest request)
    {
        LastBatch = (moduleId, [.. request.Entries.Select(e => e.Name)]);
        return Task.FromResult<string?>($"{moduleId}: {request.Entries.Count} entries");
    }
}

public sealed class ProbeKind(string id = "probe", string displayName = "Probe document", string extension = ".cairnprobe", bool canCreateNew = true,
    char? claimPrefix = null) : IDocumentKind
{
    /// <summary>For a kind that shares its extension: the first characters ("A:") of the files that are its own.</summary>
    public char? ClaimPrefix => claimPrefix;
    public int Claim(string name, ReadOnlySpan<byte> data) => claimPrefix is { } c ? ProbeClaims.Claim(c, data) : ContentClaims.ByName;
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
    : SnapshotDocument<string>(shell, kind, text, name, path, origin), IDocumentContent
{
    public string Text { get => Current; set => Apply("Edit text", _ => value); }

    /// <summary>The name with the kind's extension (an untitled probe has it already).</summary>
    public string FileName => Kind.Extensions.Any(e => DisplayName.EndsWith(e, StringComparison.OrdinalIgnoreCase)) ? DisplayName : DisplayName + Kind.Extensions[0];

    public byte[] CurrentBytes() => Serialize();

    /// <summary>A claim probe gives its whole text, so the converters can tell whose it is (<see cref="IAssetConverter.HeadLengthFor"/>).</summary>
    public ReadOnlyMemory<byte> ContentHead => Kind is ProbeKind { ClaimPrefix: not null } ? Serialize() : ReadOnlyMemory<byte>.Empty;

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
