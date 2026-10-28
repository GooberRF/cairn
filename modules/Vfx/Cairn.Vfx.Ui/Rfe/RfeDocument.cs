using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Cairn.Ui.Documents;
using Cairn.Ui.Modules;
using Cairn.Ui.Mvvm;
using Cairn.Viewport;
using Cairn.Vfx.Rfe;
using Cairn.Workspace;

namespace Cairn.Vfx.Ui.Rfe;

/// <summary>
/// A Red Faction II effect (<c>.rfe</c>) open read-only: its facts and an animated preview (meshes exactly as the game
/// moves them, particles through the effect simulator). Nothing can be edited or saved; File › Convert to and the banner's
/// Convert… make a Red Faction <c>.vfx</c> of it.
/// </summary>
public sealed class RfeDocument : DocumentBase, IDocument, IDocumentContent
{
    /// <summary>4800 ticks per second, 160 per frame (30 fps, the game's particle step).</summary>
    public static readonly TimeBase TimeBase = new(4800, 160);

    private readonly byte[] _bytes;
    private TimeUnit _unit = TimeUnit.Seconds;
    private readonly RfeLoopClock _clock = new();
    private RfeTextureSource? _textures;

    public RfeDocument(IShellContext shell, IDocumentKind kind, byte[] bytes, string name, string? path, string? origin = null)
        : base(shell, kind, name, path, origin)
    {
        _bytes = bytes;
        Effect = RfeReader.Read(bytes, name);
        Scene = new RfeScene(Effect, name);
        Playback = NewPlayback(Effect.Duration, () => _unit);
        Playback.UnitCycleRequested += (_, _) => { _unit = _unit == TimeUnit.Seconds ? TimeUnit.Frames : TimeUnit.Seconds; Playback.RefreshText(); };
        Playback.TimeChanged += (_, _) => OnTime();
        Playback.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PlaybackViewModel.Loop)) { Scene.Loop = Playback.Loop; OnTime(); } };
        ConvertCommand = new RelayCommand(Convert);
        foreach (var fact in RfeFacts.Describe(Effect, _ => "looking…")) Facts.Add(fact);
        OnTime();
    }

    /// <summary>A transport for an effect of <paramref name="duration"/> seconds.</summary>
    public static PlaybackViewModel NewPlayback(float duration, Func<TimeUnit>? unit = null)
    {
        var playback = new PlaybackViewModel(unit ?? (() => TimeUnit.Seconds), TimeBase) { Loop = true };
        playback.SetClip(new PlaybackRange(0, Math.Max(1, (int)Math.Round(duration * TimeBase.TicksPerSecond)), 0, 0, Array.Empty<int>()));
        return playback;
    }

    /// <summary>The effect.</summary>
    public RfeFile Effect { get; }

    public RfeScene Scene { get; }

    /// <summary>The transport.</summary>
    public PlaybackViewModel Playback { get; }

    /// <summary>The facts (texture locations filled in once the packs are found).</summary>
    public ObservableCollection<RfeFact> Facts { get; } = [];

    /// <summary>Converts the effect to a Red Faction .vfx (the banner's Convert…).</summary>
    public RelayCommand ConvertCommand { get; }

    /// <summary>Raised when the shown time changes.</summary>
    public event EventHandler? FrameChanged;

    /// <summary>Completes when the texture locations are known.</summary>
    public Task TexturesFound { get; private set; } = Task.CompletedTask;

    public RfeTextureSource Textures => _textures ??= CreateTextures();

    /// <summary>The packs, once found (diagnostics).</summary>
    public Assets.PegTextureSet? Packs => TexturesFound.IsCompleted ? _textures?.Packs : null;

    private RfeTextureSource CreateTextures()
    {
        // a work copy: the rest of its packfile and the install of that packfile; a file on disk: its folder and install
        var path = FilePath;
        var siblings = AssetSiblings.For(Shell, path);
        string? archive = path is not null ? Shell.Modules.OfType<IWorkCopyProvider>().FirstOrDefault(p => p.IsWorkCopy(path))?.ArchiveEntryOf(path)?.ArchivePath : null;
        var around = siblings is not null ? RfeSurroundings.For(Shell, siblings, archive)
            : path is not null ? RfeSurroundings.ForFolder(path) : RfeSurroundings.None;
        var textures = new RfeTextureSource(Effect, DisplayName, around);
        TexturesFound = FindTexturesAsync(textures);
        return textures;
    }

    private async Task FindTexturesAsync(RfeTextureSource textures)
    {
        using var busy = Cairn.Ui.Services.BusyTracker.Begin("effect preview textures");
        var names = Effect.Textures.Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var where = await Task.Run(() => names.ToDictionary(n => n, textures.Where, StringComparer.OrdinalIgnoreCase));
        Facts.Clear();
        foreach (var fact in RfeFacts.Describe(Effect, n => where.TryGetValue(n, out var w) ? w : "not found")) Facts.Add(fact);
        Raise(nameof(StatusItems));
    }

    private void OnTime()
    {
        Scene.Seek(_clock.Elapsed(Playback, Effect.Duration));
        FrameChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows effect time <paramref name="seconds"/> (diagnostics, <c>--time</c>).</summary>
    public void SeekSeconds(double seconds)
    {
        Playback.Pause();
        Playback.Seek((float)(Math.Clamp(seconds, 0, Effect.Duration) * TimeBase.TicksPerSecond));
    }

    private async void Convert()
    {
        var choice = AssetConversions.TargetsFor(Shell, this).FirstOrDefault(c => c.Target.Extension == ".vfx");
        if (choice is null) return;
        AssetConversionRequest request;
        try { request = AssetConversions.RequestFor(Shell, this, ".vfx"); }
        catch (InvalidOperationException ex) { Shell.Dialogs.ShowError($"Could not convert {DisplayName}", ex.Message); return; }
        await AssetConversions.RunAsync(Shell, choice, request);
    }

    // ---- read-only document ----
    public override bool IsReadOnly => true;
    /// <summary>Nothing can be written: Cairn never writes .rfe files.</summary>
    public bool CanSave => false;
    public override bool IsDirty => false;
    public override bool CanUndo => false;
    public override bool CanRedo => false;
    public override string? UndoLabel => null;
    public override string? RedoLabel => null;
    public override void Undo() { }
    public override void Redo() { }
    public override void SaveTo(string path) => throw new InvalidOperationException("Red Faction II effects are read-only in Cairn; convert this one to a Red Faction .vfx instead.");
    public override byte[]? CaptureRecovery() => null;
    protected override void LoadBytes(byte[] bytes, bool keepDirty) => ShowStatus("The effect changed on disk; close and reopen it to see the change.");
    protected override bool MatchesSaved(byte[] bytes) => bytes.AsSpan().SequenceEqual(_bytes);
    protected override FrameworkElement CreateView() => new RfeDocumentView(this);

    // ---- IDocumentContent ----
    public string FileName => DisplayName.EndsWith(RfeReader.Extension, StringComparison.OrdinalIgnoreCase) ? DisplayName : DisplayName + RfeReader.Extension;
    public byte[] CurrentBytes() => _bytes;
    public ReadOnlyMemory<byte> ContentHead => _bytes.AsMemory(0, Math.Min(16, _bytes.Length));

    public override IReadOnlyList<StatusItem> StatusItems =>
    [
        new("RF2 effect", "Red Faction II effect (.rfe version 3): read-only"),
        new($"{Effect.Duration:0.###} s", "Effect length; the game steps particles every 1/30 s"),
        new($"{Effect.Meshes.Length} mesh{(Effect.Meshes.Length == 1 ? "" : "es")}, {Effect.Emitters.Length} emitter{(Effect.Emitters.Length == 1 ? "" : "s")}", "Mesh objects and particle emitters"),
        new(Playback.Loop ? "Loop" : "Once", "Playback mode (the transport's loop button)"),
    ];

    public override void OnDeactivated() { Playback.Pause(); base.OnDeactivated(); }

    public override void Dispose() { Playback.Pause(); base.Dispose(); }
}

/// <summary>
/// The effect time a looping transport stands for, unwrapped across loops while it plays (so particles live on from one
/// loop into the next, as in the game); paused or seeking, the transport's own time.
/// </summary>
public sealed class RfeLoopClock
{
    private int _loops;
    private float _lastTicks;

    /// <summary>The elapsed seconds for <paramref name="playback"/>'s time now (call on each time change).</summary>
    public double Elapsed(PlaybackViewModel playback, float duration)
    {
        ArgumentNullException.ThrowIfNull(playback);
        float t = playback.Time;
        if (playback.IsPlaying && playback.Loop && t + 1 < _lastTicks) _loops++;
        else if (!playback.IsPlaying) _loops = 0;
        _lastTicks = t;
        return _loops * (double)duration + t / RfeDocument.TimeBase.TicksPerSecond;
    }
}

/// <summary>The .rfe document kind (read-only).</summary>
public sealed class RfeKind : IDocumentKind
{
    public IShellContext Shell { get; set; } = null!;
    public string Id => "vfx.rfe";
    public string DisplayName => "Red Faction II effect";
    public IReadOnlyList<string> Extensions { get; } = [RfeReader.Extension];
    public string FileFilter => "Red Faction II effects (*.rfe)|*.rfe";
    public bool CanCreateNew => false;
    public string AssociationDescription => "Red Faction II effect";
    public IDocument? CreateNew() => null;
    public IDocument Open(string path) => new RfeDocument(Shell, this, AtomicFile.ReadAllBytes(path), Path.GetFileName(path), path);
    public IDocument OpenBytes(byte[] bytes, string displayName, string originText) => new RfeDocument(Shell, this, bytes, displayName, null, originText);
    public IDocument Restore(RecoverySnapshot snapshot) => new RfeDocument(Shell, this, snapshot.Data, snapshot.DisplayName, snapshot.OriginalPath);
}

/// <summary>Banner | (viewport, transport) | facts.</summary>
public sealed class RfeDocumentView : DockPanel
{
    private readonly RfeDocument _doc;
    private readonly StackPanel _facts = new() { Margin = new Thickness(10, 8, 10, 10) };

    public RfeDocumentView(RfeDocument doc)
    {
        _doc = doc;
        SetResourceReference(BackgroundProperty, "App.WindowBackground");
        var banner = Banner(doc);
        SetDock(banner, Dock.Top);
        Children.Add(banner);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 240 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(380), MinWidth = 200 });
        var centre = new DockPanel();
        var transport = new TransportBar { DataContext = doc.Playback };
        SetDock(transport, Dock.Bottom);
        centre.Children.Add(transport);
        Viewport = new RfeViewport(doc.Scene, doc.Textures);
        centre.Children.Add(Viewport);
        doc.FrameChanged += (_, _) => Viewport.Invalidate();
        Grid.SetColumn(centre, 0);
        grid.Children.Add(centre);
        var splitter = new GridSplitter { Width = 4, HorizontalAlignment = HorizontalAlignment.Stretch, ToolTip = "Resize the facts", ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        splitter.SetResourceReference(BackgroundProperty, "App.SubtleBorder");
        Grid.SetColumn(splitter, 1);
        grid.Children.Add(splitter);
        var scroll = new ScrollViewer { Content = _facts, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        AutomationProperties.SetName(scroll, "Effect facts");
        Grid.SetColumn(scroll, 2);
        grid.Children.Add(scroll);
        Children.Add(grid);
        doc.Facts.CollectionChanged += (_, _) =>
        {
            if (_refill) return;
            _refill = true;
            Dispatcher.BeginInvoke(FillFacts, System.Windows.Threading.DispatcherPriority.Background);
        };
        FillFacts();
    }

    /// <summary>The viewport.</summary>
    public RfeViewport Viewport { get; }

    private bool _refill;

    private void FillFacts()
    {
        _refill = false;
        _facts.Children.Clear();
        string? section = null;
        foreach (var fact in _doc.Facts)
        {
            if (fact.Section != section) { section = fact.Section; _facts.Children.Add(Heading(section)); }
            _facts.Children.Add(Row(fact.Label, fact.Value));
        }
        _facts.Children.Add(Heading("Preview approximations"));
        foreach (string note in RfeFacts.PreviewApproximations) _facts.Children.Add(Row("•", note));
    }

    private static TextBlock Heading(string text)
    {
        var t = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 3) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "App.Text");
        return t;
    }

    private static FrameworkElement Row(string label, string value)
    {
        var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(label == "•" ? 14 : 120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var l = new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = label, Margin = new Thickness(0, 0, 6, 0) };
        l.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        var v = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
        v.SetResourceReference(TextBlock.ForegroundProperty, value is "not found" ? "App.Accent" : "App.Text");
        Grid.SetColumn(v, 1);
        grid.Children.Add(l);
        grid.Children.Add(v);
        return grid;
    }

    private static Border Banner(RfeDocument doc)
    {
        var text = new TextBlock
        {
            Text = "Red Faction II effect: read-only. Cairn shows it and converts it to a Red Faction effect (.vfx), but never writes .rfe files.",
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Banner.Foreground");
        var button = new Button { Content = "Convert…", Command = doc.ConvertCommand, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2), ToolTip = "Convert to a Red Faction effect (.vfx) with a report of what was approximated" };
        AutomationProperties.SetName(button, "Convert to a Red Faction effect");
        var dock = new DockPanel();
        SetDock(button, Dock.Right);
        dock.Children.Add(button);
        dock.Children.Add(text);
        var border = new Border { Child = dock, Padding = new Thickness(8, 4, 8, 4), BorderThickness = new Thickness(0, 0, 0, 1) };
        border.SetResourceReference(Border.BackgroundProperty, "Banner.InfoBackground");
        border.SetResourceReference(Border.BorderBrushProperty, "Banner.InfoBorder");
        return border;
    }
}
