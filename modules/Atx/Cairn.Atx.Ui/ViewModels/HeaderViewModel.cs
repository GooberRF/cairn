using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Cairn.Ui.Mvvm;
using Cairn.Atx.Editing;
using Cairn.Atx.Linting;
using Cairn.Atx.Schema;

namespace Cairn.Atx.Ui.ViewModels;

/// <summary>One entry in the pixel-format or material list, including the "not set" entry.</summary>
/// <param name="Token">The TOML token, or null for the entry that removes the key.</param>
/// <param name="Label">What the list shows.</param>
/// <param name="Description">The plain-language explanation under the label.</param>
public sealed record TokenOption(string? Token, string Label, string Description)
{
    /// <summary>
    /// The label alone. The drop-down draws the label and the description on two lines through a
    /// template rather than a <c>DisplayMemberPath</c>, which leaves this as what the combo box
    /// reports as its value and what a screen reader reads for the row.
    /// </summary>
    public override string ToString() => Label;
}

/// <summary>One animation-mode segment.</summary>
/// <param name="Mode">The mode.</param>
/// <param name="Label">Segment caption.</param>
/// <param name="Description">The sentence shown under the control when selected.</param>
public sealed record ModeOption(AtxAnimationMode Mode, string Label, string Description)
{
    /// <summary>The segment's caption, for anything that reads the option rather than draws it.</summary>
    public override string ToString() => Label;
}

/// <summary>
/// The Texture settings panel: the six <c>[header]</c> keys. Every setter turns into an
/// <see cref="AtxEditor"/> operation on the document text — nothing here holds state of its own, and
/// <see cref="Refresh"/> pushes the model back into the controls with change handlers suppressed so
/// the two directions can never chase each other.
/// </summary>
public sealed class HeaderViewModel : ObservableObject
{
    private readonly DocumentViewModel _document;
    private bool _suppress;

    private int _frameTimeMs = AtxSchema.DefaultFrameTimeMs;
    private AtxAnimationMode _animationMode = AtxSchema.DefaultAnimationMode;
    private bool _initiallyOn = AtxSchema.DefaultInitiallyOn;
    private TokenOption _format;
    private TokenOption _material;
    private string _alphaMask = string.Empty;
    private System.Windows.Media.Imaging.BitmapSource? _maskThumbnail;

    public HeaderViewModel(DocumentViewModel document)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));

        FormatOptions =
        [
            new TokenOption(null, "Keep source format", "Every frame stays in the format it was exported in."),
            .. AtxSchema.FormatTokens.Select(f => new TokenOption(f.Token, f.Token, f.Description)),
        ];
        MaterialOptions =
        [
            new TokenOption(null, "Not set (engine decides)",
                "The engine picks a material from the texture name, as it does for ordinary textures."),
            .. AtxSchema.Materials.Select(m => new TokenOption(m.Token, m.Token, m.Description)),
        ];
        ModeOptions = [.. AtxSchema.AnimationModes.Select(m => new ModeOption(m.Mode, m.Label, m.Description))];
        _format = FormatOptions[0];
        _material = MaterialOptions[0];

        ResetFrameTimeCommand = new RelayCommand(() => RemoveKey(AtxSchema.KeyFrameTime), () => HasFrameTimeKey);
        ResetAnimationModeCommand = new RelayCommand(
            () => RemoveKey(AtxSchema.KeyAnimationMode), () => HasAnimationModeKey);
        ResetInitiallyOnCommand = new RelayCommand(
            () => RemoveKey(AtxSchema.KeyInitiallyOn), () => HasInitiallyOnKey);
        ResetFormatCommand = new RelayCommand(() => RemoveKey(AtxSchema.KeyFormat), () => HasFormatKey);
        ResetMaterialCommand = new RelayCommand(() => RemoveKey(AtxSchema.KeyMaterial), () => HasMaterialKey);
        ClearAlphaMaskCommand = new RelayCommand(() => RemoveKey(AtxSchema.KeyAlphaMask), () => HasAlphaMaskKey);
        BrowseAlphaMaskCommand = new RelayCommand(BrowseAlphaMask, () => _document.CanEditValues);
        BrowseAlphaMaskFromVppCommand = new RelayCommand(
            BrowseAlphaMaskFromVpp, () => _document.CanEditValues);
        LocateAlphaMaskCommand = new RelayCommand(
            _document.LocateAlphaMask, () => IsAlphaMaskMissing && _document.CanEditValues);
        SearchFoldersCommand = new RelayCommand(_document.OpenSearchSettings);
    }

    /// <summary>The five formats plus "keep source format".</summary>
    public IReadOnlyList<TokenOption> FormatOptions { get; }

    /// <summary>The ten materials plus "not set".</summary>
    public IReadOnlyList<TokenOption> MaterialOptions { get; }

    /// <summary>The four animation modes, in order.</summary>
    public IReadOnlyList<ModeOption> ModeOptions { get; }

    /// <summary>True when the header can be edited at all (the TOML parses).</summary>
    public bool IsEnabled => _document.CanEditValues;

    // ── Frame time ────────────────────────────────────────────────────────────

    /// <summary>The texture-wide frame time in milliseconds.</summary>
    public int FrameTimeMs
    {
        get => _frameTimeMs;
        set
        {
            int clamped = Math.Clamp(value, AtxSchema.MinFrameTimeMs, 600000);
            if (!Set(ref _frameTimeMs, clamped)) { Raise(nameof(FpsText)); return; }
            Raise(nameof(FpsText));
            if (_suppress) return;
            Apply(e => e.SetHeaderValue(AtxSchema.KeyFrameTime, AtxValue.Integer(clamped)));
        }
    }

    /// <summary>The live "≈ 12.5 fps" readout under the frame-time box.</summary>
    public string FpsText => _frameTimeMs <= 0
        ? string.Empty
        : "≈ " + (1000.0 / _frameTimeMs).ToString("0.#", CultureInfo.CurrentCulture) + " fps";

    /// <summary>True when <c>frame_time</c> is written in the file, so it can be reset.</summary>
    public bool HasFrameTimeKey => _document.Model?.Header.FrameTime is not null;

    public RelayCommand ResetFrameTimeCommand { get; }

    public string FrameTimeTooltip { get; } = Tooltip(AtxKeyScope.Header, AtxSchema.KeyFrameTime);

    public string? FrameTimeDiagnostic => DiagnosticText(AtxSchema.KeyFrameTime);

    /// <summary>Severity of the frame-time diagnostic, for the inline text colour.</summary>
    public DiagnosticSeverity? FrameTimeSeverity => DiagnosticSeverityFor(AtxSchema.KeyFrameTime);

    // ── Animation mode ────────────────────────────────────────────────────────

    /// <summary>How the frames advance.</summary>
    public AtxAnimationMode AnimationMode
    {
        get => _animationMode;
        set
        {
            if (!Set(ref _animationMode, value))
            {
                Raise(nameof(ModeDescription));
                return;
            }
            RaiseAll(nameof(ModeDescription), nameof(SelectedMode), nameof(IsInitiallyOnRelevant), nameof(InitiallyOnTooltip));
            if (_suppress) return;
            Apply(e => e.SetHeaderValue(AtxSchema.KeyAnimationMode, AtxValue.Integer((int)value)));
        }
    }

    /// <summary>The selected mode as a list entry, for the segmented control's binding.</summary>
    public ModeOption? SelectedMode
    {
        get => ModeOptions.FirstOrDefault(m => m.Mode == _animationMode);
        set { if (value is not null) AnimationMode = value.Mode; }
    }

    /// <summary>The one-line description of the selected mode.</summary>
    public string ModeDescription =>
        ModeOptions.FirstOrDefault(m => m.Mode == _animationMode)?.Description ?? string.Empty;

    public bool HasAnimationModeKey => _document.Model?.Header.AnimationMode is not null;

    public RelayCommand ResetAnimationModeCommand { get; }

    public string AnimationModeTooltip { get; } = Tooltip(AtxKeyScope.Header, AtxSchema.KeyAnimationMode);

    public string? AnimationModeDiagnostic => DiagnosticText(AtxSchema.KeyAnimationMode);

    /// <summary>Severity of the animation-mode diagnostic.</summary>
    public DiagnosticSeverity? AnimationModeSeverity => DiagnosticSeverityFor(AtxSchema.KeyAnimationMode);

    // ── Initially on ──────────────────────────────────────────────────────────

    /// <summary>Whether the animation is already playing when the level loads.</summary>
    public bool InitiallyOn
    {
        get => _initiallyOn;
        set
        {
            if (!Set(ref _initiallyOn, value) || _suppress) return;
            Apply(e => e.SetHeaderValue(AtxSchema.KeyInitiallyOn, AtxValue.Boolean(value)));
        }
    }

    /// <summary>False while the mode is Static, where the setting does nothing.</summary>
    public bool IsInitiallyOnRelevant => _animationMode != AtxAnimationMode.Static;

    /// <summary>Tooltip that explains the disabled state when the mode is Static.</summary>
    public string InitiallyOnTooltip => IsInitiallyOnRelevant
        ? Tooltip(AtxKeyScope.Header, AtxSchema.KeyInitiallyOn)
        : "Static textures never advance on their own, so this setting has no effect.\n\n"
          + Tooltip(AtxKeyScope.Header, AtxSchema.KeyInitiallyOn);

    public bool HasInitiallyOnKey => _document.Model?.Header.InitiallyOn is not null;

    public RelayCommand ResetInitiallyOnCommand { get; }

    public string? InitiallyOnDiagnostic => DiagnosticText(AtxSchema.KeyInitiallyOn);

    /// <summary>Severity of the start-playing diagnostic.</summary>
    public DiagnosticSeverity? InitiallyOnSeverity => DiagnosticSeverityFor(AtxSchema.KeyInitiallyOn);

    // ── Format ────────────────────────────────────────────────────────────────

    /// <summary>The target pixel format, or the "keep source format" entry.</summary>
    public TokenOption Format
    {
        get => _format;
        set
        {
            if (value is null || !Set(ref _format, value))
            {
                RaiseAll(nameof(FormatDescription), nameof(FormatHelp));
                return;
            }
            RaiseAll(nameof(FormatDescription), nameof(FormatHelp));
            if (_suppress) return;
            if (value.Token is null) RemoveKey(AtxSchema.KeyFormat);
            else Apply(e => e.SetHeaderValue(AtxSchema.KeyFormat, AtxValue.String(value.Token)));
        }
    }

    /// <summary>The plain-language description of the selected format.</summary>
    public string FormatDescription => _format.Description;

    /// <summary>
    /// The control tooltip: what the selected format means, then what the key does. The panel shows
    /// each option's description in the drop-down rather than under the closed control, so this is
    /// where the current choice explains itself.
    /// </summary>
    public string FormatHelp => Help(_format.Description, FormatTooltip);

    public bool HasFormatKey => _document.Model?.Header.Format is not null;

    public RelayCommand ResetFormatCommand { get; }

    public string FormatTooltip { get; } = Tooltip(AtxKeyScope.Header, AtxSchema.KeyFormat);

    public string? FormatDiagnostic => DiagnosticText(AtxSchema.KeyFormat);

    /// <summary>Severity of the pixel-format diagnostic.</summary>
    public DiagnosticSeverity? FormatSeverity => DiagnosticSeverityFor(AtxSchema.KeyFormat);

    // ── Alpha mask ────────────────────────────────────────────────────────────

    /// <summary>The alpha-mask file name, as written in the file.</summary>
    public string AlphaMask
    {
        get => _alphaMask;
        set
        {
            string text = (value ?? string.Empty).Trim();
            if (!Set(ref _alphaMask, text) || _suppress) return;
            if (text.Length == 0) RemoveKey(AtxSchema.KeyAlphaMask);
            else Apply(e => e.SetHeaderValue(AtxSchema.KeyAlphaMask, AtxValue.String(text)));
        }
    }

    public bool HasAlphaMaskKey => _document.Model?.Header.AlphaMask is not null;

    public RelayCommand ClearAlphaMaskCommand { get; }

    public RelayCommand BrowseAlphaMaskCommand { get; }

    /// <summary>Picks the alpha mask out of a .vpp archive rather than off disk.</summary>
    public RelayCommand BrowseAlphaMaskFromVppCommand { get; }

    public string AlphaMaskTooltip { get; } = Tooltip(AtxKeyScope.Header, AtxSchema.KeyAlphaMask);

    /// <summary>
    /// True when the named mask was looked for and not found. Driven by the diagnostic rather than
    /// by the asset pass directly, so the two actions appear and disappear with the message that
    /// explains them rather than a beat apart.
    /// </summary>
    public bool IsAlphaMaskMissing => DiagnosticFor(AtxSchema.KeyAlphaMask)?.Code == AtxRules.MaskNotFound;

    /// <summary>Picks the missing mask from disk (the same flow as the ATX012 quick fix).</summary>
    public RelayCommand LocateAlphaMaskCommand { get; }

    /// <summary>Opens Settings, where the extra search folders live.</summary>
    public RelayCommand SearchFoldersCommand { get; }

    public string? AlphaMaskDiagnostic => DiagnosticText(AtxSchema.KeyAlphaMask);

    /// <summary>Severity of the alpha-mask diagnostic.</summary>
    public DiagnosticSeverity? AlphaMaskSeverity => DiagnosticSeverityFor(AtxSchema.KeyAlphaMask);

    /// <summary>A decoded preview of the alpha mask, or null when there is none.</summary>
    public System.Windows.Media.Imaging.BitmapSource? MaskThumbnail
    {
        get => _maskThumbnail;
        private set { if (Set(ref _maskThumbnail, value)) Raise(nameof(HasMaskThumbnail)); }
    }

    /// <summary>True once the alpha-mask thumbnail has decoded.</summary>
    public bool HasMaskThumbnail => _maskThumbnail is not null;

    private void LoadMaskThumbnail() => _ = LoadMaskThumbnailAsync();

    private async Task LoadMaskThumbnailAsync()
    {
        string name = _alphaMask;
        if (name.Length == 0) { MaskThumbnail = null; return; }
        try
        {
            var bitmap = await _document.Shell.Thumbnails
                .GetAsync(_document.Resolver, name, 64, _document.ThumbnailToken)
                .ConfigureAwait(true);
            if (string.Equals(_alphaMask, name, StringComparison.Ordinal)) MaskThumbnail = bitmap;
        }
        catch (OperationCanceledException) { }
    }

    // ── Material ──────────────────────────────────────────────────────────────

    /// <summary>The texture-wide surface material, or the "not set" entry.</summary>
    public TokenOption Material
    {
        get => _material;
        set
        {
            if (value is null || !Set(ref _material, value))
            {
                RaiseAll(nameof(MaterialDescription), nameof(MaterialHelp));
                return;
            }
            RaiseAll(nameof(MaterialDescription), nameof(MaterialHelp));
            if (_suppress) return;
            if (value.Token is null) RemoveKey(AtxSchema.KeyMaterial);
            else Apply(e => e.SetHeaderValue(AtxSchema.KeyMaterial, AtxValue.String(value.Token)));
        }
    }

    /// <summary>The plain-language description of the selected material.</summary>
    public string MaterialDescription => _material.Description;

    /// <summary>The control tooltip: what the selected material means, then what the key does.</summary>
    public string MaterialHelp => Help(_material.Description, MaterialTooltip);

    public bool HasMaterialKey => _document.Model?.Header.Material is not null;

    public RelayCommand ResetMaterialCommand { get; }

    public string MaterialTooltip { get; } = Tooltip(AtxKeyScope.Header, AtxSchema.KeyMaterial);

    public string? MaterialDiagnostic => DiagnosticText(AtxSchema.KeyMaterial);

    /// <summary>Severity of the material diagnostic.</summary>
    public DiagnosticSeverity? MaterialSeverity => DiagnosticSeverityFor(AtxSchema.KeyMaterial);

    // ── Refresh ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Pushes the model into the controls. <see cref="_suppress"/> is what stops a control's change
    /// handler from emitting an edit for a value that came from the document in the first place.
    /// </summary>
    public void Refresh()
    {
        var header = _document.Model?.Header;
        _suppress = true;
        try
        {
            if (header is not null)
            {
                _frameTimeMs = header.EffectiveFrameTimeMs;
                _animationMode = header.EffectiveAnimationMode;
                _initiallyOn = header.EffectiveInitiallyOn;
                _alphaMask = header.EffectiveAlphaMask ?? string.Empty;

                string? formatToken = header.EffectiveFormat;
                _format = FormatOptions.FirstOrDefault(o =>
                              o.Token is not null && AtxSchema.ParseFormatToken(formatToken)?.Token == o.Token)
                          ?? FormatOptions[0];

                string? materialToken = header.EffectiveMaterial;
                _material = MaterialOptions.FirstOrDefault(o =>
                                o.Token is not null && AtxSchema.ParseMaterial(materialToken)?.Token == o.Token)
                            ?? MaterialOptions[0];
            }
        }
        finally { _suppress = false; }

        RaiseAll(
            nameof(FrameTimeMs), nameof(FpsText), nameof(AnimationMode), nameof(SelectedMode), nameof(ModeDescription),
            nameof(InitiallyOn), nameof(IsInitiallyOnRelevant), nameof(InitiallyOnTooltip),
            nameof(Format), nameof(FormatDescription), nameof(FormatHelp),
            nameof(Material), nameof(MaterialDescription), nameof(MaterialHelp),
            nameof(AlphaMask), nameof(IsEnabled),
            nameof(HasFrameTimeKey), nameof(HasAnimationModeKey), nameof(HasInitiallyOnKey),
            nameof(HasFormatKey), nameof(HasMaterialKey), nameof(HasAlphaMaskKey),
            nameof(FrameTimeDiagnostic), nameof(AnimationModeDiagnostic), nameof(InitiallyOnDiagnostic),
            nameof(FormatDiagnostic), nameof(MaterialDiagnostic), nameof(AlphaMaskDiagnostic),
            nameof(FrameTimeSeverity), nameof(AnimationModeSeverity), nameof(InitiallyOnSeverity),
            nameof(FormatSeverity), nameof(MaterialSeverity), nameof(AlphaMaskSeverity),
            nameof(IsAlphaMaskMissing));

        ResetFrameTimeCommand.RaiseCanExecuteChanged();
        ResetAnimationModeCommand.RaiseCanExecuteChanged();
        ResetInitiallyOnCommand.RaiseCanExecuteChanged();
        ResetFormatCommand.RaiseCanExecuteChanged();
        ResetMaterialCommand.RaiseCanExecuteChanged();
        ClearAlphaMaskCommand.RaiseCanExecuteChanged();
        BrowseAlphaMaskCommand.RaiseCanExecuteChanged();
        BrowseAlphaMaskFromVppCommand.RaiseCanExecuteChanged();
        LocateAlphaMaskCommand.RaiseCanExecuteChanged();
        LoadMaskThumbnail();
    }

    /// <summary>Starts a coalesced undo group for a wheel or arrow-key stepping gesture.</summary>
    public void BeginStepping() => _document.BeginInteraction();

    /// <summary>Closes the group opened by <see cref="BeginStepping"/>.</summary>
    public void EndStepping() => _document.EndInteraction();

    private void RemoveKey(string key) => Apply(e => e.RemoveHeaderKey(key));

    /// <summary>
    /// Applies a header edit, and pushes the file's real values back into the controls when the
    /// document refuses it. An edit is refused while the TOML is broken, and also when the header
    /// is written as an inline table or as dotted keys, which cannot take a new plain key line —
    /// without this the control would go on showing a value the file does not contain and neither
    /// the preview nor the Problems panel agrees with.
    /// </summary>
    private void Apply(Func<AtxEditor, Cairn.Atx.Text.TextEditBatch> edit)
    {
        if (!_document.Edit(edit)) Refresh();
    }

    private void BrowseAlphaMask()
    {
        string? chosen = _document.Shell.Dialogs.OpenImageFile(
            _document.AtxFolder, "Choose an alpha mask", _alphaMask);
        if (chosen is null) return;
        AlphaMask = System.IO.Path.GetFileName(chosen);
    }

    /// <summary>
    /// Picks the mask out of a .vpp instead of off disk — the same browser the frames list uses,
    /// in single-select mode, because a mask is as likely to live in an archive as beside the .atx.
    /// </summary>
    private void BrowseAlphaMaskFromVpp()
    {
        string? chosen = _document.Shell.Dialogs.PickImageFromVpp(_document);
        if (string.IsNullOrEmpty(chosen)) return;
        AlphaMask = System.IO.Path.GetFileName(chosen);
    }

    /// <summary>The worst diagnostic on a header key, or null when the field is clean.</summary>
    private Diagnostic? DiagnosticFor(string key) => _document.Diagnostics
        .Where(d => d.FrameIndex is null && d.Key == key)
        .OrderByDescending(d => d.Severity)
        .FirstOrDefault();

    private string? DiagnosticText(string key) => DiagnosticFor(key)?.Message;

    private DiagnosticSeverity? DiagnosticSeverityFor(string key) => DiagnosticFor(key)?.Severity;

    /// <summary>Joins the selected option's description to the key's own documentation.</summary>
    private static string Help(string description, string keyTooltip) =>
        description.Length == 0 ? keyTooltip
        : keyTooltip.Length == 0 ? description
        : description + "\n\n" + keyTooltip;

    /// <summary>Builds a control tooltip out of the schema's own documentation.</summary>
    private static string Tooltip(AtxKeyScope scope, string key)
    {
        var info = AtxSchema.FindKey(scope, key);
        if (info is null) return string.Empty;
        var parts = new List<string> { info.Summary, info.Details };
        if (info.DefaultText is { Length: > 0 } d) parts.Add($"Default: {d}.");
        if (info.RuntimeEvent is { Length: > 0 } e) parts.Add($"Level event: {e}.");
        return string.Join("\n\n", parts);
    }
}

