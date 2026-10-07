using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using Cairn.Ui.Mvvm;
using Cairn.Rfa.Ui.Services;
using Cairn.Rfa.Ui.ViewModels.ClipTools;
using Cairn.Assets;
using Cairn.Formats;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Retarget;

namespace Cairn.Rfa.Ui.ViewModels.Retargeting;

/// <summary>Where the clip to retarget comes from.</summary>
/// <param name="Document">An open clip document, or null.</param>
/// <param name="Library">A library clip, or null.</param>
/// <param name="Label">What the picker shows.</param>
/// <param name="Note">"active tab", "24 bones"…</param>
public sealed record SourceClipOption(ClipDocumentViewModel? Document, LibraryClip? Library, string Label, string Note)
{
    public string Name => Document?.DisplayName ?? Library?.Name ?? Label;

    public string ToolTip => Document is not null ? $"{Document.DisplayName} (open tab, as it is now)" : Library is { } l ? $"{l.Name} · {l.BoneCount} bones · {l.Location.DisplayLocation}" : Label;

    public override string ToString() => Label;
}

/// <summary>A report check as a row.</summary>
public sealed record ReportCheckRow(bool Passed, string Name, string Message)
{
    public string Glyph => Passed ? "\uE73E" : "\uE783";

    public string BrushKey => Passed ? "Severity.Info" : "Severity.Error";
}

/// <summary>One joint IK held (a pinned contact): what it was held to and how far it ended up from it.</summary>
/// <param name="Name">The held joint, e.g. foot-l.</param>
/// <param name="HeldTo">"the ground contact", "the source's position", "the main hand (two-handed grip)".</param>
/// <param name="ErrorText">"0.02 cm".</param>
/// <param name="Note">"leg fully stretched at 3 of 30 samples", or empty.</param>
/// <param name="ErrorCm">The worst distance, cm.</param>
public sealed record PinnedContactRow(string Name, string HeldTo, string ErrorText, string Note, double ErrorCm)
{
    public bool HasNote => Note.Length > 0;
}

/// <summary>A hand, foot or head against the source's in model space: informational (proportions), or pinned.</summary>
public sealed record JointOffsetRow(string Name, string OffsetText, string Note, double OffsetCm);

/// <summary>One mapped joint of the report.</summary>
public sealed record JointRow(string Bone, string PelvisMax, string PelvisMean, string ModelMax, string SegmentDirection);

/// <summary>
/// Clip › Retarget…: the source clip and the shared setup (<see cref="RetargetSetupViewModel"/>), the
/// output name and folder, the live result — recomputed off the UI thread (debounced, cancellable)
/// whenever anything changes and shown in the dialog's own viewport on the target mesh with the source
/// skeleton as a ghost (or side by side) — and the report. <see cref="Retarget"/> opens the result as a
/// new unsaved clip document; <see cref="SaveAs"/> writes it.
/// </summary>
public sealed class RetargetDialogViewModel : ObservableObject, IDisposable
{
    private const string FolderKey = "rfa.retargetOutputFolder";
    private readonly RfaWorkspace _shell;
    private readonly DispatcherTimer _debounce;
    private RfaClip? _sourceClipFile;
    private int _clipRequest;
    private int _loadingClip;
    private CancellationTokenSource? _cts;
    private int _generation;
    private bool _pending;
    private bool _busy;
    private bool _disposed;
    private bool _sideBySide;
    private string _outputName = string.Empty;
    private bool _nameEdited;
    private string _outputFolder;
    private string? _error;
    private RetargetResult? _result;
    private RetargetInputs? _resultInputs;
    private RetargetReport? _report;
    private string _statusText = "Choose what to retarget.";

    /// <param name="shell">The shell.</param>
    /// <param name="document">The clip document to retarget (null: pick from the library).</param>
    /// <param name="libraryClip">A library clip to start with (the library's Retarget…), or null.</param>
    public RetargetDialogViewModel(RfaWorkspace shell, ClipDocumentViewModel? document, LibraryClip? libraryClip = null)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _outputFolder = shell.DefaultOutputFolder(shell.Settings.Get<string>(FolderKey));
        Preview = new ViewportPreviewHost(shell);
        SourcePreview = new ViewportPreviewHost(shell);
        Preview.Playback.TimeChanged += (_, _) => SyncSourcePreview();

        // Source clips: the open clip tabs first, then the library.
        var options = new List<SourceClipOption>();
        foreach (var d in shell.Documents.OfType<ClipDocumentViewModel>())
            options.Add(new SourceClipOption(d, null, d.DisplayName, ReferenceEquals(d, document) ? "active tab" : "open tab"));
        foreach (var c in shell.Assets.Snapshot.Clips.Where(c => c.IsReadable).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            options.Add(new SourceClipOption(null, c, c.Name, $"{c.BoneCount} bones"));
        SourceClips = new FilteredList<SourceClipOption>(options, o => o.Label);

        string? sourceMesh = document?.PreviewLibraryMesh?.Name ?? document?.Scene.MeshName;
        if (sourceMesh is null && libraryClip is not null)
            sourceMesh = shell.Assets.Snapshot.DefaultPreviewMesh(libraryClip.Name, shell.Assets.Usage)?.Name;
        Setup = new RetargetSetupViewModel(shell, sourceMesh, document?.PreviewLibraryMesh is null ? document?.PreviewMesh : null);
        Setup.Changed += (_, _) => OnInputsChanged();

        _debounce = new DispatcherTimer(DispatcherPriority.Background, shell.Dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            StartCompute();
        };

        RetargetCommand = new RelayCommand(() => Retarget(), () => CanRetarget);
        SaveAsCommand = new RelayCommand(() => SaveAs(), () => CanRetarget);
        BrowseFolderCommand = new RelayCommand(BrowseFolder);
        ToggleSideBySideCommand = new RelayCommand(() => IsSideBySide = !IsSideBySide);

        SourceClips.SelectionChanged += (_, _) => LoadSourceClip();
        SourceClips.Selected = options.FirstOrDefault(o => ReferenceEquals(o.Document, document) && document is not null)
            ?? (libraryClip is not null ? options.FirstOrDefault(o => ReferenceEquals(o.Library, libraryClip)) ?? options.FirstOrDefault(o => o.Library?.Name == libraryClip.Name) : null)
            ?? options.FirstOrDefault();
    }

    /// <summary>The shell.</summary>
    public RfaWorkspace Shell => _shell;

    /// <summary>The shared setup (meshes, profiles, map, options).</summary>
    public RetargetSetupViewModel Setup { get; }

    /// <summary>The target mesh playing the result, with the source skeleton as a ghost.</summary>
    public ViewportPreviewHost Preview { get; }

    /// <summary>The source mesh playing the source clip (side-by-side mode), in step with <see cref="Preview"/>.</summary>
    public ViewportPreviewHost SourcePreview { get; }

    /// <summary>Open clip tabs, then library clips.</summary>
    public FilteredList<SourceClipOption> SourceClips { get; }

    /// <summary>The loaded source clip, or null.</summary>
    public RfaClip? SourceClip => _sourceClipFile;

    /// <summary>Shows the source mesh beside the target instead of the ghost.</summary>
    public bool IsSideBySide
    {
        get => _sideBySide;
        set
        {
            if (!Set(ref _sideBySide, value)) return;
            UpdatePreview();
        }
    }

    /// <summary>The output file name (defaults to <c>af_{rig}_{clip}.rfa</c>).</summary>
    public string OutputName
    {
        get => _outputName;
        set
        {
            string v = value?.Trim() ?? string.Empty;
            if (!Set(ref _outputName, v)) return;
            _nameEdited = true;
            RaiseAll(nameof(OutputNameProblem), nameof(CanRetarget));
            RefreshCommands();
        }
    }

    /// <summary>Why the output name will not do (empty, too long for the engine, a stock clip's name), or null.</summary>
    public string? OutputNameProblem => NameProblem(_outputName, _shell);

    /// <summary>The folder Save as… starts in (never the game directory).</summary>
    public string OutputFolder
    {
        get => _outputFolder;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || !Set(ref _outputFolder, value)) return;
            Raise(nameof(OutputFolderProblem));
        }
    }

    /// <summary>A warning when the folder is the game directory, or null.</summary>
    public string? OutputFolderProblem => _shell.IsGameDirectory(_outputFolder)
        ? "That is the game directory: a loose file there changes what the game loads. Pick another folder."
        : null;

    /// <summary>Why the result cannot be made (a missing input, a Core refusal), or null.</summary>
    public string? Error
    {
        get => _error;
        private set
        {
            if (!Set(ref _error, value)) return;
            RaiseAll(nameof(HasError), nameof(CanRetarget));
            RefreshCommands();
        }
    }

    public bool HasError => _error is not null;

    /// <summary>True when the card under the settings has something to say (working, an error, warnings).</summary>
    public bool ShowResultCard => _busy || _error is not null || Warnings.Count > 0;

    /// <summary>True while a result is being computed.</summary>
    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (Set(ref _busy, value)) Raise(nameof(ShowResultCard));
        }
    }

    /// <summary>True while a recompute is scheduled or running.</summary>
    public bool IsPending => _pending;

    /// <summary>One line: what the preview shows.</summary>
    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    /// <summary>The latest successful result, or null.</summary>
    public RetargetResult? Result => _result;

    /// <summary>The report of <see cref="Result"/>, or null.</summary>
    public RetargetReport? Report => _report;

    /// <summary>The report's checklist.</summary>
    public ObservableCollection<ReportCheckRow> Checks { get; } = [];

    /// <summary>What IK held (hands, feet), worst distance from the wanted point.</summary>
    public ObservableCollection<PinnedContactRow> PinnedContacts { get; } = [];

    /// <summary>"Nothing was pinned: rotation only." and the like, or empty when there are contacts.</summary>
    public string PinnedContactsEmptyText => _result is null ? string.Empty
        : !_result.Contacts.IsDefaultOrEmpty ? string.Empty
        : "Nothing was pinned (no IK chain ran): every joint follows the source's rotations, so hands and feet land where the target's proportions put them.";

    /// <summary>Hands, feet and head against the source's in model space (proportions; pinned ones say so).</summary>
    public ObservableCollection<JointOffsetRow> JointOffsets { get; } = [];

    /// <summary>Per mapped joint.</summary>
    public ObservableCollection<JointRow> Joints { get; } = [];

    /// <summary>The result's warnings and the retargeter's report lines.</summary>
    public ObservableCollection<string> Warnings { get; } = [];

    /// <summary>The retargeter's own report lines (static bones, root placement, IK).</summary>
    public ObservableCollection<string> ReportLines { get; } = [];

    /// <summary>"structure ok; worst pelvis-relative joint …".</summary>
    public string ReportSummary => _report is { } r
        ? Capitalise(r.Summary) + "." + Environment.NewLine + Capitalise(r.ProportionSummary) + "."
        : HasError ? string.Empty : "The report appears once the result is computed.";

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>True when Retarget would produce a clip.</summary>
    public bool CanRetarget => !_disposed && !_pending && _error is null && _result is { Success: true, Clip: not null } && OutputNameProblem is null;

    public RelayCommand RetargetCommand { get; }

    public RelayCommand SaveAsCommand { get; }

    public RelayCommand BrowseFolderCommand { get; }

    public RelayCommand ToggleSideBySideCommand { get; }

    // ── Source clip ───────────────────────────────────────────────────────────

    private async void LoadSourceClip()
    {
        var option = SourceClips.Selected;
        int request = ++_clipRequest;
        _sourceClipFile = null;
        Raise(nameof(SourceClip));
        if (option is null)
        {
            OnInputsChanged();
            return;
        }
        if (option.Document is { } document)
        {
            _sourceClipFile = document.Current;
            if (document.PreviewLibraryMesh is { } m) Setup.Select(m.Name, null);
        }
        else if (option.Library is { } clip)
        {
            _loadingClip++;
            try
            {
                var loaded = await _shell.Assets.LoadClipAsync(clip).ConfigureAwait(true);
                if (request != _clipRequest || _disposed) return;
                _sourceClipFile = loaded;
                // A clip made for another skeleton than the current source mesh: switch to the mesh it plays on.
                if (Setup.SourceBoneCount > 0 && loaded.BoneCount != Setup.SourceBoneCount
                    && _shell.Assets.Snapshot.DefaultPreviewMesh(clip.Name, _shell.Assets.Usage) is { } mesh && mesh.BoneCount == loaded.BoneCount)
                    Setup.Select(mesh.Name, null);
            }
            catch (Exception ex) when (RfaWorkspace.IsReadFailure(ex) && ex is not OperationCanceledException)
            {
                if (request != _clipRequest) return;
                Error = $"{clip.Name} could not be read: {ex.Message}";
                return;
            }
            finally
            {
                _loadingClip--;
            }
        }
        Raise(nameof(SourceClip));
        // The preset follows the clip (seated for a vehicle seat or turret state, standing otherwise) until
        // the user picks one or edits an option.
        Setup.SuggestPresetFor(option.Name);
        if (!_nameEdited) SetDefaultName();
        OnInputsChanged();
    }

    private void SetDefaultName()
    {
        if (SourceClips.Selected is not { } option || Setup.TargetProfile is not { } target) return;
        string clipName = option.Name;
        var item = new BatchItem(clipName, Setup.Inputs?.Source ?? new RetargetRig(Cairn.Rfa.Animation.Skeleton.Empty, Setup.SourceProfile ?? RigProfiles.RigA),
            Setup.Inputs?.Target ?? new RetargetRig(Cairn.Rfa.Animation.Skeleton.Empty, target));
        _outputName = BatchRetarget.OutputName(item, BatchNamePattern(target));
        RaiseAll(nameof(OutputName), nameof(OutputNameProblem), nameof(CanRetarget));
    }

    /// <summary>The default pattern: <c>af_{rig}_{clip}</c> for a built-in target rig, else <c>{clip}_{target}</c>.</summary>
    internal static string BatchNamePattern(RigProfile target) =>
        RigProfiles.BuiltIn.Any(p => string.Equals(p.Name, target.Name, StringComparison.OrdinalIgnoreCase)) ? "af_{rig}_{clip}.rfa" : "{source}_{target}.rfa";

    /// <summary>
    /// Why a name will not do, or null: empty, characters a file name cannot have or the game may not load
    /// (outside printable ASCII: tables, archives and the engine's file calls are 8-bit), over 59 characters
    /// (the engine's 60-byte buffer), or another visible clip's name (clip identity is global). Shared by
    /// Retarget and New Clip.
    /// </summary>
    internal static string? NameProblem(string name, RfaWorkspace shell)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Give the clip a file name.";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "The name has characters a file name cannot have.";
        if (name.Any(c => c is < ' ' or > '~'))
            return "The name has characters outside plain ASCII; the game reads clip names as 8-bit text, so it may not load the clip. Use letters, digits, '_' and '-'.";
        string withExt = name.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase) ? name : name + ".rfa";
        if (withExt.Length > 59) return $"The name is {withExt.Length} characters; the game copies clip names into a 60-byte buffer, so keep it to 59.";
        if (shell.Assets.Snapshot.FindClip(withExt) is { } existing)
            return $"A clip called {withExt} already exists ({existing.Location.DisplayLocation}). Clip names are global in the game: pick another name.";
        return null;
    }

    // ── Live result ─────────────────────────────────────────────────────────

    private void OnInputsChanged()
    {
        if (_disposed) return;
        if (!_nameEdited) SetDefaultName();
        _pending = true;
        Raise(nameof(IsPending));
        RaiseAll(nameof(CanRetarget));
        RefreshCommands();
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>Skips the debounce (self-tests).</summary>
    public void RecomputeNow()
    {
        if (_disposed) return;
        _debounce.Stop();
        // Pending until this compute lands, so SettleAsync waits for it.
        _pending = true;
        Raise(nameof(IsPending));
        StartCompute();
    }

    /// <summary>Waits (without blocking the UI thread) until the setup has loaded and no recompute is pending.</summary>
    public async Task<bool> SettleAsync(int timeoutMs = 120_000)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while ((_pending || _loadingClip > 0 || Setup.IsLoading) && !_disposed)
        {
            if (clock.ElapsedMilliseconds > timeoutMs) return false;
            await Task.Delay(15).ConfigureAwait(true);
        }
        return true;
    }

    private async void StartCompute()
    {
        if (_disposed) return;
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        int generation = ++_generation;
        var inputs = Setup.Inputs;
        var clip = _sourceClipFile;
        if (inputs is null || clip is null)
        {
            Fail(generation, clip is null ? (SourceClips.Selected is null ? "Pick a clip to retarget." : "Loading the clip…") : Setup.NotReadyReason ?? "Not ready.");
            return;
        }
        if (clip.BoneCount != inputs.Source.Skeleton.Count)
        {
            Fail(generation, $"{SourceClips.Selected?.Name} has {clip.BoneCount} bones but the source mesh {inputs.SourceMeshName} has {inputs.Source.Skeleton.Count}. "
                + "The source mesh must be the one the clip was made for.");
            return;
        }
        IsBusy = true;
        StatusText = "Retargeting…";
        using var busy = BusyTracker.Begin("retarget preview");
        try
        {
            var (result, report) = await Task.Run(() =>
            {
                cts.Token.ThrowIfCancellationRequested();
                var r = Retargeter.Retarget(new RetargetRequest(clip, inputs.Source, inputs.Target) { BoneMap = inputs.Map, Options = inputs.Options });
                cts.Token.ThrowIfCancellationRequested();
                RetargetReport? rep = r is { Success: true, Clip: { } c, BoneMap: { } m }
                    ? RetargetReport.Build(clip, inputs.Source.Skeleton, inputs.Source.Profile, c, inputs.Target.Skeleton, inputs.Target.Profile, m, contacts: r.Contacts)
                    : null;
                return (r, rep);
            }, cts.Token).ConfigureAwait(true);
            if (generation != _generation || _disposed) return;
            if (!result.Success || result.Clip is null)
            {
                Fail(generation, result.Error ?? "The retarget could not run.", result);
                return;
            }
            Succeed(inputs, result, report, clip);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ArithmeticException or FormatException)
        {
            Fail(generation, ex.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ErrorLog.Write("retarget preview", ex);
            Fail(generation, $"The retarget failed unexpectedly ({ex.GetType().Name}: {ex.Message}). Details were written to the error log.");
        }
        finally
        {
            if (generation == _generation) IsBusy = false;
        }
    }

    private void Succeed(RetargetInputs inputs, RetargetResult result, RetargetReport? report, RfaClip source)
    {
        _result = result;
        _resultInputs = inputs;
        _report = report;
        _pending = false;
        Error = null;
        Checks.Clear();
        PinnedContacts.Clear();
        JointOffsets.Clear();
        Joints.Clear();
        Warnings.Clear();
        ReportLines.Clear();
        foreach (string w in result.Warnings) Warnings.Add(w);
        foreach (string line in result.ReportLines) ReportLines.Add(line);
        foreach (var c in result.Contacts.OrderByDescending(c => c.MaxErrorCm))
            PinnedContacts.Add(new PinnedContactRow(c.EndBone, c.HeldTo, RetargetSetupViewModel.F(c.MaxErrorCm, "0.00") + " cm", c.StretchNote ?? string.Empty, c.MaxErrorCm));
        if (report is not null)
        {
            foreach (var c in report.Checks) Checks.Add(new ReportCheckRow(c.Passed, c.Name, c.Message));
            foreach (var e in report.JointOffsets.OrderByDescending(e => e.ModelSpaceMaxCm))
                JointOffsets.Add(new JointOffsetRow(e.CanonicalName, double.IsNaN(e.ModelSpaceMaxCm) ? "—" : RetargetSetupViewModel.F(e.ModelSpaceMaxCm, "0.00") + " cm",
                    e.Pinned ? "pinned (see above)" : "proportions", e.ModelSpaceMaxCm));
            var names = inputs.Target.Skeleton.Names;
            foreach (var j in report.Joints)
            {
                Joints.Add(new JointRow(j.BoneName, Cm(j.PelvisRelativeMaxCm), Cm(j.PelvisRelativeMeanCm), Cm(j.ModelSpaceMaxCm),
                    j.SegmentDirectionMaxDegrees is { } d ? RetargetSetupViewModel.F(d, "0.0") + "°" + (j.PrimaryChild is { } pc ? $" (→ {pc})" : "") : "—"));
            }
        }
        var clip = result.Clip!;
        StatusText = string.Format(CultureInfo.CurrentCulture, "{0} on {1}: {2} bones, {3}{4}",
            SourceClips.Selected?.Name, inputs.TargetMeshName, clip.BoneCount, TimeFormat.Duration(clip.Duration),
            result.Warnings.Length > 0 ? $" · {result.Warnings.Length} warning{(result.Warnings.Length == 1 ? "" : "s")}" : string.Empty);
        UpdatePreview();
        RaiseAll(nameof(Result), nameof(Report), nameof(ReportSummary), nameof(IsPending), nameof(CanRetarget), nameof(ShowResultCard), nameof(PinnedContactsEmptyText));
        RefreshCommands();
        _ = source;
    }

    private static string Cm(double value) => double.IsNaN(value) ? "—" : RetargetSetupViewModel.F(value, "0.0");

    private void Fail(int generation, string message, RetargetResult? result = null)
    {
        if (generation != _generation || _disposed) return;
        _result = null;
        _report = null;
        _pending = false;
        Error = message;
        Checks.Clear();
        PinnedContacts.Clear();
        JointOffsets.Clear();
        Joints.Clear();
        Warnings.Clear();
        ReportLines.Clear();
        if (result is not null)
        {
            foreach (string w in result.Warnings) Warnings.Add(w);
        }
        StatusText = "No result.";
        UpdatePreview();
        IsBusy = false;
        RaiseAll(nameof(Result), nameof(Report), nameof(ReportSummary), nameof(IsPending), nameof(CanRetarget), nameof(ShowResultCard), nameof(PinnedContactsEmptyText));
        RefreshCommands();
    }

    private void UpdatePreview()
    {
        if (_disposed) return;
        var inputs = _resultInputs ?? Setup.Inputs;
        if (inputs is null)
        {
            Preview.SetMesh(null, null, null);
            Preview.SetClip(null);
            return;
        }
        if (!ReferenceEquals(Preview.Scene.Mesh, inputs.TargetMesh))
            Preview.SetMesh(inputs.TargetMesh, inputs.TargetMeshName, _shell.Assets.ResolverFor(inputs.TargetFolder));
        var result = _result?.Clip;
        Preview.SetClip(result);
        // The source skeleton plays the source clip as a ghost over the target (when not side by side). With the
        // hip-height root the source stands on its own floor in its own model space, so the ghost is moved onto
        // the target's ground for a like-for-like comparison.
        var ground = _result?.Ground;
        var ghostOffset = ground is null ? default : new System.Numerics.Vector3(0, (float)(ground.TargetGround - ground.SourceGround), 0);
        Preview.Scene.SetGhost("source", !_sideBySide && result is not null ? _sourceClipFile : null,
            "Source: " + (SourceClips.Selected?.Name ?? "clip") + (ground is null ? string.Empty : " (on the target's ground)"),
            "Viewport.GhostCompare", null, inputs.Source.Skeleton, ghostOffset);
        Raise(nameof(GhostCaption));
        if (_sideBySide)
        {
            if (!ReferenceEquals(SourcePreview.Scene.Mesh, inputs.SourceMesh))
                SourcePreview.SetMesh(inputs.SourceMesh, inputs.SourceMeshName, _shell.Assets.ResolverFor(inputs.SourceFolder));
            SourcePreview.SetClip(_sourceClipFile);
            SyncSourcePreview();
        }
        else
        {
            SourcePreview.Playback.Pause();
        }
        Raise(nameof(IsSideBySide));
    }

    /// <summary>The caption under the preview: what the ghost is and where it stands.</summary>
    public string GhostCaption => _result?.Ground is { } g
        ? $"The target mesh plays the result; the thin coloured skeleton is the source playing the original, in step, moved {Math.Abs((g.TargetGround - g.SourceGround) * 100):0.#} cm {(g.TargetGround >= g.SourceGround ? "up" : "down")} so it stands on the target's ground (as the hip-height root places the result). Drag to orbit, wheel to zoom."
        : "The target mesh plays the result; the thin coloured skeleton is the source playing the original, in step, in the source's own model space. Drag to orbit, wheel to zoom.";

    private void SyncSourcePreview()
    {
        if (_sideBySide && SourcePreview.Clip is not null) SourcePreview.Playback.Seek(Preview.Playback.Time);
    }

    private void RefreshCommands()
    {
        RetargetCommand.RaiseCanExecuteChanged();
        SaveAsCommand.RaiseCanExecuteChanged();
    }

    // ── Output ──────────────────────────────────────────────────────────────

    private string FileName => _outputName.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase) ? _outputName : _outputName + ".rfa";

    /// <summary>Opens the result as a new unsaved clip document (nothing written). Returns it, or null.</summary>
    public ClipDocumentViewModel? Retarget()
    {
        if (!CanRetarget || _result?.Clip is not { } clip || _resultInputs is not { } inputs) return null;
        var document = _shell.OpenNewClip(clip, FileName, inputs.TargetMesh, inputs.TargetMeshName, inputs.TargetFolder);
        document.ShowStatus($"Retargeted {SourceClips.Selected?.Name} onto {inputs.TargetMeshName}. Not saved yet.");
        Remember();
        return document;
    }

    /// <summary>Asks where to write the result, writes it and opens it. Returns the path, or null.</summary>
    public string? SaveAs()
    {
        if (!CanRetarget || _result?.Clip is not { } clip || _resultInputs is not { } inputs) return null;
        string folder = _shell.IsGameDirectory(_outputFolder) ? _shell.DefaultOutputFolder() : _outputFolder;
        string? path = _shell.Dialogs.SaveDocument(folder, FileName, ".rfa");
        if (path is null) return null;
        if (_shell.IsGameDirectory(Path.GetDirectoryName(path))
            && !_shell.Dialogs.Confirm("Write into the game directory?", "A loose clip there changes what the game loads, for every level. Write it anyway?", "_Write anyway"))
            return null;
        return WriteTo(path, clip, inputs);
    }

    /// <summary>Writes the result to <paramref name="path"/> and opens it (self-tests call this without a file dialog).</summary>
    public string? WriteTo(string path, RfaClip? clip = null, RetargetInputs? inputs = null)
    {
        clip ??= _result?.Clip;
        inputs ??= _resultInputs;
        if (clip is null || inputs is null) return null;
        // A tab already holding that file would silently keep (and later save over) the old content.
        if (_shell.DocumentAt(path) is not null)
        {
            _shell.Dialogs.ShowError($"'{Path.GetFileName(path)}' is open in a tab.",
                "Writing the result there would leave that tab out of date, and saving it would overwrite the result. Close that tab first, or pick another name.");
            return null;
        }
        try
        {
            Cairn.Workspace.AtomicFile.WriteAllBytes(path, RfaWriter.Write(clip));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _shell.Dialogs.ShowError("The clip could not be saved.", $"'{path}' could not be written.", ex.Message);
            return null;
        }
        OutputFolder = Path.GetDirectoryName(path) ?? _outputFolder;
        Remember();
        if (_shell.OpenFile(path) is ClipDocumentViewModel opened) opened.UsePreviewMesh(inputs.TargetMesh, inputs.TargetMeshName, inputs.TargetFolder);
        return path;
    }

    private void Remember()
    {
        if (_shell.IsDiagnosticRun) return;
        if (!_shell.IsGameDirectory(_outputFolder)) _shell.Settings.Set(FolderKey, _outputFolder);
        _shell.SaveSettingsSoon();
    }

    private void BrowseFolder()
    {
        string? chosen = _shell.Dialogs.PickFolder(_outputFolder, "Choose the folder retargeted clips are saved in");
        if (chosen is not null) OutputFolder = chosen;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debounce.Stop();
        _cts?.Cancel();
        Preview.Dispose();
        SourcePreview.Dispose();
        _pending = false;
    }
}
