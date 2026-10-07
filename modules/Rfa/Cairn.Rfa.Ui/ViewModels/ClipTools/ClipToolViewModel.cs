using System.Globalization;
using System.Windows.Threading;
using Cairn.Ui.Mvvm;
using Cairn.Rfa.Ui.Services;
using Cairn.Rfa.Formats.Rfa;

namespace Cairn.Rfa.Ui.ViewModels.ClipTools;

/// <summary>What a clip tool computed: the clip OK would commit, its undo label, and the plain-language summary.</summary>
/// <param name="Clip">The result (the original instance when nothing would change).</param>
/// <param name="Label">The undo step's label, e.g. "Trim to 12–40".</param>
/// <param name="Summary">Lines for the "what will change" card.</param>
/// <param name="Extra">Tool-specific facts the view-model reads back on the UI thread (counts, errors).</param>
public sealed record ClipToolResult(RfaClip Clip, string Label, IReadOnlyList<string> Summary, object? Extra = null);

/// <summary>Which bones a bone-scoped tool works on.</summary>
public enum BoneScope
{
    /// <summary>Every bone of the clip.</summary>
    All,
    /// <summary>The bones selected in the viewport (<c>doc.Selection</c>).</summary>
    Selected,
    /// <summary>The bones that own a selected key (<c>doc.KeySelection</c>).</summary>
    KeyBones,
}

/// <summary>
/// What every Clip menu dialog shows: a heading and description, a summary card, an inline error, the
/// document's transport (so the live preview can be watched while the dialog is modal), and OK/Cancel.
/// <see cref="ClipToolViewModel"/> adds the preview machinery shared by the editing tools;
/// <see cref="CompareDialogViewModel"/> uses the same shell without an undo step.
/// </summary>
public abstract class ClipDialogViewModel : ObservableObject
{
    private IReadOnlyList<string> _summary = [];
    private string? _error;
    private bool _isBusy;

    protected ClipDialogViewModel(ClipDocumentViewModel document)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
        Shell = document.Shell;
        Unit = Shell.TimeUnit;
        SelectedBones = [.. document.Selection.Bones.Where(b => b >= 0 && b < document.Current.BoneCount).Order()];
        KeyBones = [.. document.KeySelection.Validate(document.Current).SelectedBones.Order()];
    }

    /// <summary>The clip document the tool works on.</summary>
    public ClipDocumentViewModel Document { get; }

    /// <summary>The shell (library, dispatcher, time unit).</summary>
    public RfaWorkspace Shell { get; }

    /// <summary>The document's transport: the dialog's play/scrub row binds here.</summary>
    public PlaybackViewModel Playback => Document.Playback;

    /// <summary>The id used by diagnostics ("trim", "reduce"…).</summary>
    public abstract string ToolId { get; }

    /// <summary>The window title.</summary>
    public abstract string Title { get; }

    /// <summary>The dialog's heading.</summary>
    public virtual string Heading => Title;

    /// <summary>One or two sentences under the heading: what the tool does.</summary>
    public abstract string Description { get; }

    /// <summary>The OK button's text.</summary>
    public virtual string ApplyText => "OK";

    /// <summary>The OK button's tooltip.</summary>
    public virtual string ApplyToolTip => "Apply the change to the clip as one undo step (Ctrl+Z reverts it)";

    /// <summary>The line under the transport row.</summary>
    public virtual string PreviewHint => "The viewport plays the result while this dialog is open; the clip itself changes only when you press OK.";

    /// <summary>"What will change" lines.</summary>
    public IReadOnlyList<string> SummaryLines
    {
        get => _summary;
        protected set => Set(ref _summary, value);
    }

    /// <summary>Why the tool cannot apply (a Core refusal or a missing input), shown inline; null when fine.</summary>
    public string? Error
    {
        get => _error;
        protected set
        {
            if (!Set(ref _error, value)) return;
            Raise(nameof(HasError));
            Raise(nameof(CanApply));
        }
    }

    /// <summary>True when <see cref="Error"/> is set.</summary>
    public bool HasError => _error is not null;

    /// <summary>True while a preview is being computed.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        protected set
        {
            if (!Set(ref _isBusy, value)) return;
            Raise(nameof(CanApply));
        }
    }

    /// <summary>True when OK would do something.</summary>
    public abstract bool CanApply { get; }

    /// <summary>Carries out the change (OK). Returns true when the dialog may close.</summary>
    public abstract bool Commit();

    /// <summary>Ends the dialog's live effects (preview, pending work). Idempotent; called on every close.</summary>
    public abstract void End();

    // ── Units and names ──────────────────────────────────────────────────────

    /// <summary>The time unit the dialog's fields use (the global setting when it opened).</summary>
    public TimeUnit Unit { get; }

    /// <summary>"f", "s" or "ticks".</summary>
    public string UnitSuffix => TimeFormat.Suffix(Unit);

    /// <summary>Decimals for a time field in <see cref="Unit"/>.</summary>
    public int UnitDecimals => TimeFormat.Decimals(Unit);

    /// <summary>One step of a time field: a frame in every unit.</summary>
    public double UnitStep => TimeFormat.ToUnit(RfaClip.TicksPerFrame, Unit);

    /// <summary>A value in <see cref="Unit"/> to ticks.</summary>
    protected int ToTicks(double value) => TimeFormat.FromUnit(value, Unit);

    /// <summary>Ticks to a value in <see cref="Unit"/>.</summary>
    protected double ToUnit(double ticks) => TimeFormat.ToUnit(ticks, Unit);

    /// <summary>"12" (no suffix).</summary>
    protected string Num(double ticks) => TimeFormat.Number(ticks, Unit);

    /// <summary>"12 f".</summary>
    protected string Time(double ticks) => TimeFormat.Format(ticks, Unit);

    /// <summary>A bone's display name (the preview mesh's when it fits, else "Bone N").</summary>
    protected string BoneName(int bone) => Document.BoneDisplayName(bone);

    // ── Bone scope (tools that work on some bones) ─────────────────────────────

    private BoneScope _boneScope;

    /// <summary>True for a tool whose dialog shows the bone scope group.</summary>
    public virtual bool SupportsBoneScope => false;

    /// <summary>The viewport's bone selection when the dialog opened, ascending.</summary>
    public IReadOnlyList<int> SelectedBones { get; }

    /// <summary>The bones that own a selected key when the dialog opened, ascending.</summary>
    public IReadOnlyList<int> KeyBones { get; }

    public bool HasSelectedBones => SelectedBones.Count > 0;

    public bool HasKeyBones => KeyBones.Count > 0;

    public string SelectedBonesLabel => SelectedBones.Count switch
    {
        0 => "Selected bones (none selected)",
        1 => $"Selected bone ({BoneName(SelectedBones[0])})",
        _ => $"Selected bones ({SelectedBones.Count})",
    };

    public string KeyBonesLabel => KeyBones.Count switch
    {
        0 => "Bones with selected keys (no keys selected)",
        1 => $"Bone with selected keys ({BoneName(KeyBones[0])})",
        _ => $"Bones with selected keys ({KeyBones.Count})",
    };

    /// <summary>Which bones the tool works on.</summary>
    public BoneScope BoneScope
    {
        get => _boneScope;
        set
        {
            if (!Set(ref _boneScope, value)) return;
            RaiseAll(nameof(IsScopeAll), nameof(IsScopeSelected), nameof(IsScopeKeys));
            OnParametersChanged();
        }
    }

    public bool IsScopeAll
    {
        get => _boneScope == BoneScope.All;
        set { if (value) BoneScope = BoneScope.All; }
    }

    public bool IsScopeSelected
    {
        get => _boneScope == BoneScope.Selected;
        set { if (value) BoneScope = BoneScope.Selected; }
    }

    public bool IsScopeKeys
    {
        get => _boneScope == BoneScope.KeyBones;
        set { if (value) BoneScope = BoneScope.KeyBones; }
    }

    /// <summary>The bones in scope: null for every bone.</summary>
    protected IReadOnlyCollection<int>? ScopeBones => _boneScope switch
    {
        BoneScope.Selected => SelectedBones,
        BoneScope.KeyBones => KeyBones,
        _ => null,
    };

    /// <summary>"every bone", "3 selected bones"…</summary>
    protected string ScopeText => _boneScope switch
    {
        BoneScope.Selected => SelectedBones.Count == 1 ? BoneName(SelectedBones[0]) : $"{SelectedBones.Count} selected bones",
        BoneScope.KeyBones => KeyBones.Count == 1 ? BoneName(KeyBones[0]) : $"{KeyBones.Count} bones with selected keys",
        _ => "every bone",
    };

    /// <summary>Called when a parameter changes (the editing tools recompute their preview).</summary>
    protected virtual void OnParametersChanged() { }

    /// <summary>Sets a parameter field and recomputes when it changed.</summary>
    protected bool SetParameter<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!Set(ref field, value, name)) return false;
        OnParametersChanged();
        return true;
    }

    /// <summary>An exception's message without the " (Parameter 'x')" suffix and the actual-value line.</summary>
    protected static string UserMessage(Exception ex)
    {
        string message = ex.Message;
        if (ex is ArgumentException { ParamName: { } p })
        {
            int at = message.LastIndexOf($" (Parameter '{p}')", StringComparison.Ordinal);
            if (at > 0) message = message[..at];
        }
        int newline = message.IndexOf('\n', StringComparison.Ordinal);
        if (newline > 0 && ex is ArgumentOutOfRangeException) message = message[..newline].TrimEnd('\r');
        return message;
    }

    /// <summary>"3,000" in the current culture.</summary>
    protected static string N(int value) => value.ToString("N0", CultureInfo.CurrentCulture);
}

/// <summary>
/// The base of every editing clip tool: parameters live on the subclass; whenever one changes the
/// preview is recomputed (debounced, off the UI thread, cancellable) from the snapshot the dialog opened
/// on, shown in the main viewport through <see cref="ClipDocumentViewModel.SetPreviewClip"/> (the
/// transport covers the preview's range meanwhile), and OK commits exactly that result as one labelled
/// undo step through <see cref="DocumentViewModel{T}.Apply"/>. Core refusals become <see cref="ClipDialogViewModel.Error"/>.
/// The document is never touched before OK, and <see cref="End"/> (every close path) clears the preview.
/// </summary>
public abstract class ClipToolViewModel : ClipDialogViewModel
{
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _cts;
    private int _generation;
    private bool _pending;
    private bool _ended;
    private bool _started;
    private bool _playbackChanged;
    private ClipToolResult? _result;

    protected ClipToolViewModel(ClipDocumentViewModel document) : base(document)
    {
        Original = document.Current;
        var original = Original;
        _originalBytes = new Lazy<byte[]?>(() =>
        {
            try
            {
                return RfaWriter.Write(original);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }, LazyThreadSafetyMode.ExecutionAndPublication);
        _debounce = new DispatcherTimer(DispatcherPriority.Background, Shell.Dispatcher) { Interval = TimeSpan.FromMilliseconds(180) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            StartCompute();
        };
    }

    /// <summary>The snapshot the dialog opened on; every preview is computed from it.</summary>
    public RfaClip Original { get; }

    /// <summary>The latest computed result (null before the first one or after a refusal).</summary>
    public ClipToolResult? Result => _result;

    /// <summary>True while a recompute is scheduled or running.</summary>
    public bool IsPending => _pending;

    /// <summary>True once the dialog has closed (OK or cancel).</summary>
    public bool IsEnded => _ended;

    public override bool CanApply =>
        !_ended && !_pending && Error is null && _result is not null && !ReferenceEquals(_result.Clip, Original);

    /// <summary>
    /// Captures the parameters on the UI thread and returns the work to run off it. Throw
    /// <see cref="ArgumentException"/> for parameters that cannot work (shown inline).
    /// </summary>
    protected abstract Func<CancellationToken, Task<ClipToolResult>> Prepare();

    /// <summary>Wraps synchronous work for <see cref="Prepare"/>.</summary>
    protected static Func<CancellationToken, Task<ClipToolResult>> Work(Func<CancellationToken, ClipToolResult> work) =>
        ct => Task.FromResult(work(ct));

    /// <summary>A result that changes nothing (OK stays disabled), with an explanation.</summary>
    protected ClipToolResult Nothing(string why) => new(Original, Title, [why]);

    /// <summary><see cref="Prepare"/>'s answer when the parameters ask for no change.</summary>
    protected Func<CancellationToken, Task<ClipToolResult>> NothingWork(string why)
    {
        var nothing = Nothing(why);
        return _ => Task.FromResult(nothing);
    }

    /// <summary>Called on the UI thread with each successful result (read <see cref="ClipToolResult.Extra"/> here).</summary>
    protected virtual void OnResult(ClipToolResult result) { }

    /// <summary>Computes the first preview. Subclasses call it at the end of their constructor.</summary>
    protected void Start()
    {
        _started = true;
        StartCompute();
    }

    protected override void OnParametersChanged()
    {
        if (_ended || !_started) return;
        _pending = true;
        Raise(nameof(IsPending));
        Raise(nameof(CanApply));
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>Skips the debounce: recomputes now (tests, and a dialog that wants an instant refresh).</summary>
    public void RecomputeNow()
    {
        if (_ended) return;
        _debounce.Stop();
        StartCompute();
    }

    /// <summary>Waits (without blocking the UI thread) until no recompute is scheduled or running.</summary>
    public async Task<bool> SettleAsync(int timeoutMs = 60_000)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (_pending && !_ended)
        {
            if (clock.ElapsedMilliseconds > timeoutMs) return false;
            await Task.Delay(15).ConfigureAwait(true);
        }
        return true;
    }

    private async void StartCompute()
    {
        if (_ended) return;
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        int generation = ++_generation;
        _pending = true;
        IsBusy = true;
        Raise(nameof(IsPending));
        Raise(nameof(CanApply));

        Func<CancellationToken, Task<ClipToolResult>> work;
        try
        {
            work = Prepare();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Fail(generation, ex);
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ErrorLog.Write("clip tool " + ToolId, ex);
            Fail(generation, new InvalidOperationException($"The tool failed unexpectedly ({ex.GetType().Name}: {ex.Message}). Details were written to the error log."));
            return;
        }

        using var busy = BusyTracker.Begin("clip tool " + ToolId);
        try
        {
            var token = cts.Token;
            var result = await Task.Run(async () => NoOpGuard(await work(token).ConfigureAwait(false)), token).ConfigureAwait(true);
            if (generation != _generation || _ended) return;
            Succeed(result);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer recompute, or the dialog closed.
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException
            or Cairn.Formats.AssetFormatException or ArithmeticException)
        {
            Fail(generation, ex);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A bug, not a refusal: say so plainly rather than take the app down.
            ErrorLog.Write("clip tool " + ToolId, ex);
            Fail(generation, new InvalidOperationException($"The tool failed unexpectedly ({ex.GetType().Name}: {ex.Message}). Details were written to the error log."));
        }
    }

    private readonly Lazy<byte[]?> _originalBytes;

    /// <summary>
    /// A result that is a new instance but byte-identical to the original (a trim to the clip's own
    /// range, a reduce that removes nothing) is no change at all: OK would push an empty undo step.
    /// Runs off the UI thread.
    /// </summary>
    private ClipToolResult NoOpGuard(ClipToolResult result)
    {
        if (ReferenceEquals(result.Clip, Original) || _originalBytes.Value is not { } before) return result;
        try
        {
            if (!RfaWriter.Write(result.Clip).AsSpan().SequenceEqual(before)) return result;
        }
        catch (ArgumentException)
        {
            return result;
        }
        return result with { Clip = Original, Summary = [.. result.Summary, "Nothing would change: the result is identical to the current clip."] };
    }

    private void Succeed(ClipToolResult result)
    {
        _result = result;
        _pending = false;
        Error = null;
        SummaryLines = result.Summary;
        OnResult(result);
        ShowPreview(ReferenceEquals(result.Clip, Original) ? null : result.Clip);
        IsBusy = false;
        RaiseAll(nameof(Result), nameof(IsPending), nameof(CanApply));
    }

    private void Fail(int generation, Exception ex)
    {
        if (generation != _generation || _ended) return;
        _result = null;
        _pending = false;
        SummaryLines = [];
        Error = UserMessage(ex);
        ShowPreview(null);
        IsBusy = false;
        RaiseAll(nameof(Result), nameof(IsPending), nameof(CanApply));
    }

    private void ShowPreview(RfaClip? clip)
    {
        if (_ended) return;
        Document.SetPreviewClip(clip);
        // The transport covers the preview's range (a trimmed or retimed clip plays its own length).
        if (clip is not null || _playbackChanged)
        {
            Document.Playback.SetClip(clip ?? Document.Current);
            _playbackChanged = clip is not null;
        }
    }

    public override bool Commit()
    {
        if (!CanApply || _result is not { } result) return false;
        var original = Original;
        bool applied = Document.Apply(result.Label, c => ReferenceEquals(c, original)
            ? result.Clip
            : throw new InvalidOperationException("The clip changed while the dialog was open; open the tool again."));
        if (!applied)
        {
            Error = Document.StatusMessage ?? "The change could not be applied.";
            return false;
        }
        End();
        return true;
    }

    public override void End()
    {
        if (_ended) return;
        _ended = true;
        _debounce.Stop();
        _cts?.Cancel();
        _pending = false;
        Document.SetPreviewClip(null);
        if (_playbackChanged) Document.Playback.SetClip(Document.Current);
        _playbackChanged = false;
        IsBusy = false;
        OnEnded();
        RaiseAll(nameof(IsEnded), nameof(IsPending), nameof(CanApply));
    }

    /// <summary>Called once when the dialog closes.</summary>
    protected virtual void OnEnded() { }
}
