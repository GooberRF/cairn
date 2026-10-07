using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Cairn.Ui.Mvvm;
using Cairn.Atx.Editing;
using Cairn.Formats.Imaging;
using Cairn.Atx.Schema;

namespace Cairn.Atx.Ui.ViewModels;

/// <summary>One read-only fact about a frame's resolved image.</summary>
/// <param name="Label">What the fact is, e.g. "Size".</param>
/// <param name="Value">The fact, already formatted for display.</param>
public sealed record ImageFact(string Label, string Value)
{
    /// <summary>Reads as "Size: 64 x 64" — this is what a screen reader announces for the row.</summary>
    public override string ToString() => $"{Label}: {Value}";
}

/// <summary>
/// The frame inspector. It acts on the whole selection, which makes it the app's quick bulk edit:
/// a value typed here is written to every selected frame in one undo step, and a value that differs
/// across the selection shows as indeterminate until the user sets it.
/// </summary>
public sealed class FrameInspectorViewModel : ObservableObject
{
    private readonly DocumentViewModel _document;
    private bool _suppress;

    private string _fileName = string.Empty;
    private bool _isFileMixed;
    private bool? _useDefaultTime = true;
    private int _frameTimeMs = AtxSchema.DefaultFrameTimeMs;
    private bool? _useDefaultMaterial = true;
    private TokenOption _material;

    public FrameInspectorViewModel(DocumentViewModel document)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        MaterialOptions = [.. AtxSchema.Materials.Select(m => new TokenOption(m.Token, m.Token, m.Description))];
        _material = MaterialOptions[0];
        BrowseFileCommand = new RelayCommand(BrowseFile, () => SelectionCount == 1 && CanEdit);
        LocateFileCommand = new RelayCommand(LocateFile, () => IsImageMissing && CanEdit);
        SearchFoldersCommand = new RelayCommand(_document.OpenSearchSettings);
    }

    /// <summary>The ten materials (the inspector's "use texture default" is a separate checkbox).</summary>
    public IReadOnlyList<TokenOption> MaterialOptions { get; }

    /// <summary>True when at least one frame is selected.</summary>
    public bool HasSelection => SelectionCount > 0;

    /// <summary>How many frames the inspector is acting on.</summary>
    public int SelectionCount => _document.Frames.SelectedIndices.Count;

    /// <summary>True when the document allows structural edits.</summary>
    public bool CanEdit => _document.CanEditStructure;

    /// <summary>The panel's subtitle: "Frame 3" or "5 frames selected".</summary>
    public string Title => SelectionCount switch
    {
        0 => "No frame selected",
        1 => $"Frame {_document.Frames.PrimarySelectedIndex}",
        _ => $"{SelectionCount} frames selected",
    };

    /// <summary>Guidance shown when nothing is selected.</summary>
    public string EmptyText => "Select a frame in the list to see and edit its settings.";

    // ── File ──────────────────────────────────────────────────────────────────

    /// <summary>The frame's image file name; empty and disabled for a multi-selection.</summary>
    public string FileName
    {
        get => _fileName;
        set
        {
            string name = (value ?? string.Empty).Trim();
            if (!Set(ref _fileName, name) || _suppress || name.Length == 0) return;
            var indices = _document.Frames.SelectedIndices;
            if (indices.Count != 1) { Refresh(); return; }
            if (!Apply(e => e.SetFrameValue(indices, AtxSchema.KeyFile, AtxValue.String(name)))) return;
            _document.Frames.ClearThumbnails();
        }
    }

    /// <summary>True when the selected frames use different files, so the box is blank.</summary>
    public bool IsFileMixed
    {
        get => _isFileMixed;
        private set => Set(ref _isFileMixed, value);
    }

    /// <summary>True when the file box is usable (exactly one frame selected).</summary>
    public bool IsFileEditable => SelectionCount == 1 && CanEdit;

    public RelayCommand BrowseFileCommand { get; }

    public string FileTooltip { get; } = Describe(AtxKeyScope.Frame, AtxSchema.KeyFile);

    // ── Frame time ────────────────────────────────────────────────────────────

    /// <summary>
    /// True when the selection inherits the texture frame time, false when it overrides it, null
    /// when the selection is mixed.
    /// </summary>
    public bool? UseDefaultTime
    {
        get => _useDefaultTime;
        set
        {
            if (!Set(ref _useDefaultTime, value) || _suppress) return;
            var indices = _document.Frames.SelectedIndices;
            if (indices.Count == 0) { Refresh(); return; }
            if (value == true)
            {
                if (!Apply(e => e.RemoveFrameKey(indices, AtxSchema.KeyFrameTime))) return;
            }
            else if (value == false)
            {
                if (!Apply(e => e.SetFrameValue(
                        indices, AtxSchema.KeyFrameTime, AtxValue.Integer(_frameTimeMs)))) return;
            }
            Raise(nameof(IsTimeEditable));
        }
    }

    /// <summary>The per-frame time in milliseconds.</summary>
    public int FrameTimeMs
    {
        get => _frameTimeMs;
        set
        {
            int clamped = Math.Clamp(value, AtxSchema.MinFrameTimeMs, 600000);
            if (!Set(ref _frameTimeMs, clamped) || _suppress) return;
            var indices = _document.Frames.SelectedIndices;
            if (indices.Count == 0) { Refresh(); return; }
            if (!Apply(e => e.SetFrameValue(
                    indices, AtxSchema.KeyFrameTime, AtxValue.Integer(clamped)))) return;
            _useDefaultTime = false;
            Raise(nameof(UseDefaultTime));
            Raise(nameof(IsTimeEditable));
        }
    }

    /// <summary>The checkbox caption, naming the texture default it falls back to.</summary>
    public string UseDefaultTimeText =>
        $"Use texture default ({_document.Model?.Header.EffectiveFrameTimeMs ?? AtxSchema.DefaultFrameTimeMs} ms)";

    /// <summary>True when the frame-time box accepts input.</summary>
    public bool IsTimeEditable => CanEdit && HasSelection && _useDefaultTime != true;

    public string FrameTimeTooltip { get; } = Describe(AtxKeyScope.Frame, AtxSchema.KeyFrameTime);

    // ── Material ──────────────────────────────────────────────────────────────

    /// <summary>True when the selection inherits the texture material; null when mixed.</summary>
    public bool? UseDefaultMaterial
    {
        get => _useDefaultMaterial;
        set
        {
            if (!Set(ref _useDefaultMaterial, value) || _suppress) return;
            var indices = _document.Frames.SelectedIndices;
            if (indices.Count == 0) { Refresh(); return; }
            if (value == true)
            {
                if (!Apply(e => e.RemoveFrameKey(indices, AtxSchema.KeyMaterial))) return;
            }
            else if (value == false)
            {
                if (!Apply(e => e.SetFrameValue(
                        indices, AtxSchema.KeyMaterial, AtxValue.String(_material.Token!)))) return;
            }
            Raise(nameof(IsMaterialEditable));
        }
    }

    /// <summary>The per-frame material.</summary>
    public TokenOption Material
    {
        get => _material;
        set
        {
            if (value is null || !Set(ref _material, value) || _suppress) return;
            var indices = _document.Frames.SelectedIndices;
            if (indices.Count == 0 || value.Token is null) { Refresh(); return; }
            if (!Apply(e => e.SetFrameValue(
                    indices, AtxSchema.KeyMaterial, AtxValue.String(value.Token)))) return;
            _useDefaultMaterial = false;
            Raise(nameof(UseDefaultMaterial));
            Raise(nameof(IsMaterialEditable));
        }
    }

    /// <summary>The checkbox caption, naming the texture material it falls back to.</summary>
    public string UseDefaultMaterialText
    {
        get
        {
            string? material = _document.Model?.Header.EffectiveMaterial;
            return material is null
                ? "Use texture default (engine decides)"
                : $"Use texture default ({material})";
        }
    }

    /// <summary>True when the material list accepts input.</summary>
    public bool IsMaterialEditable => CanEdit && HasSelection && _useDefaultMaterial != true;

    public string MaterialTooltip { get; } = Describe(AtxKeyScope.Frame, AtxSchema.KeyMaterial);

    // ── Missing image ─────────────────────────────────────────────────────────

    /// <summary>
    /// True when one frame is selected and its image was not found anywhere the engine looks. The
    /// panel then shows the warning row, which is where "Locate file…" is discoverable for a
    /// designer who never opens the Problems panel.
    /// </summary>
    public bool IsImageMissing { get; private set; }

    /// <summary>The warning row's sentence, naming the file that is missing.</summary>
    public string MissingImageText { get; private set; } = string.Empty;

    /// <summary>Picks the missing image from disk (the same flow as the ATX010 quick fix).</summary>
    public RelayCommand LocateFileCommand { get; }

    /// <summary>Opens Settings, where the extra search folders live.</summary>
    public RelayCommand SearchFoldersCommand { get; }

    // ── Read-only image facts ─────────────────────────────────────────────────

    /// <summary>True when the asset pass has something to say about the selected frame.</summary>
    public bool HasImageFacts => ImageFacts.Count > 0;

    /// <summary>Label/value pairs describing the resolved image, for the facts table.</summary>
    public IReadOnlyList<ImageFact> ImageFacts { get; private set; } = [];

    /// <summary>A sentence explaining a superseding sibling, or null.</summary>
    public string? SupersedeNote { get; private set; }

    // ── Refresh ───────────────────────────────────────────────────────────────

    /// <summary>Pushes the current selection into the controls, with change handlers suppressed.</summary>
    public void Refresh()
    {
        var model = _document.Model;
        var indices = _document.Frames.SelectedIndices;

        _suppress = true;
        try
        {
            if (model is null || indices.Count == 0)
            {
                _fileName = string.Empty;
                _isFileMixed = false;
                _useDefaultTime = true;
                _useDefaultMaterial = true;
                _frameTimeMs = model?.Header.EffectiveFrameTimeMs ?? AtxSchema.DefaultFrameTimeMs;
                _material = MaterialOptions[0];
            }
            else
            {
                var frames = indices.Where(i => i >= 0 && i < model.Frames.Count)
                    .Select(i => model.Frames[i]).ToList();

                var names = frames.Select(f => f.EffectiveFile ?? string.Empty).Distinct(StringComparer.Ordinal)
                    .ToList();
                _isFileMixed = names.Count > 1;
                _fileName = names.Count == 1 ? names[0] : string.Empty;

                var overrides = frames.Select(f => f.FrameTimeOverrideMs).Distinct().ToList();
                _useDefaultTime = overrides.All(o => o is null) ? true
                    : overrides.All(o => o is not null) ? false
                    : null;
                _frameTimeMs = overrides.Count == 1 && overrides[0] is { } ms
                    ? ms
                    : model.Header.EffectiveFrameTimeMs;

                var materials = frames.Select(f => f.MaterialOverride).Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                _useDefaultMaterial = materials.All(m => m is null) ? true
                    : materials.All(m => m is not null) ? false
                    : null;
                _material = materials.Count == 1 && materials[0] is { } token
                    ? MaterialOptions.FirstOrDefault(o =>
                          string.Equals(o.Token, AtxSchema.ParseMaterial(token)?.Token, StringComparison.Ordinal))
                      ?? MaterialOptions[0]
                    : MaterialOptions[0];
            }
        }
        finally { _suppress = false; }

        BuildImageFacts(indices);

        RaiseAll(
            nameof(HasSelection), nameof(SelectionCount), nameof(Title), nameof(CanEdit),
            nameof(FileName), nameof(IsFileMixed), nameof(IsFileEditable),
            nameof(UseDefaultTime), nameof(UseDefaultTimeText), nameof(FrameTimeMs), nameof(IsTimeEditable),
            nameof(UseDefaultMaterial), nameof(UseDefaultMaterialText), nameof(Material),
            nameof(IsMaterialEditable), nameof(ImageFacts), nameof(HasImageFacts), nameof(SupersedeNote),
            nameof(IsImageMissing), nameof(MissingImageText));
        BrowseFileCommand.RaiseCanExecuteChanged();
        LocateFileCommand.RaiseCanExecuteChanged();
    }

    private void BuildImageFacts(IReadOnlyList<int> indices)
    {
        SupersedeNote = null;
        IsImageMissing = false;
        MissingImageText = string.Empty;
        if (indices.Count != 1)
        {
            ImageFacts = [];
            return;
        }

        var resolved = _document.ResolvedFrame(indices[0]);
        if (resolved is null)
        {
            ImageFacts = [];
            return;
        }

        var facts = new List<ImageFact>();
        if (resolved.Location is null)
        {
            // The warning row says this in full, with the two actions that fix it, so repeating it
            // as a fact would only say the same thing twice.
            IsImageMissing = _document.IsFrameImageMissing(indices[0]);
            MissingImageText = IsImageMissing
                ? $"'{resolved.RequestedName}' was not found next to this .atx, in your search "
                  + "folders, or in the game's archives."
                : string.Empty;
            if (!IsImageMissing) facts.Add(new ImageFact("Image", "not found in any search location"));
        }
        else
        {
            if (resolved.Info is { } info)
            {
                facts.Add(new ImageFact("Size", $"{info.Width} x {info.Height}"));
                facts.Add(new ImageFact("Format", EngineFormats.DisplayName(info.Format)));
                // A TGA carries no mip chain, so the engine builds one at load time. Saying so
                // beats a number we would be inventing, and it explains why the mip half of the
                // "must match frame 0" rule cannot always be checked here.
                facts.Add(new ImageFact("Mip levels", info.MipLevels is { } m
                    ? m.ToString(CultureInfo.CurrentCulture)
                    : "none stored — the game generates them"));
                facts.Add(new ImageFact("File type", info.ContainerLabel));
                if (info.Note is { Length: > 0 } note) facts.Add(new ImageFact("Detail", note));
            }
            else
            {
                facts.Add(new ImageFact("Image", resolved.Error ?? "could not be read"));
            }

            string where = resolved.Location.ArchivePath is { } archive
                ? Path.GetFileName(archive)
                : resolved.Location.DisplayLocation;
            facts.Add(new ImageFact("Found in", where.Length == 0 ? "the .atx folder" : where));

            if (resolved.Location.IsSupersede)
            {
                SupersedeNote =
                    $"The game loads '{resolved.Location.ResolvedName}' instead of "
                    + $"'{resolved.RequestedName}', because that extension takes priority.";
            }
        }

        ImageFacts = facts;
    }

    /// <summary>
    /// Applies an edit and puts the file's real values back into the controls when the document
    /// refuses it — an edit is refused while the TOML is broken, and for a header or frame written
    /// in a layout <see cref="AtxEditor"/> will not rewrite. Without this the inspector goes on
    /// showing a value the file does not contain, and neither the preview nor the Problems panel
    /// agrees with it.
    /// </summary>
    /// <returns>True when the document actually changed.</returns>
    private bool Apply(Func<AtxEditor, Cairn.Atx.Text.TextEditBatch> edit)
    {
        if (_document.Edit(edit)) return true;
        Refresh();
        return false;
    }

    private void LocateFile()
    {
        var indices = _document.Frames.SelectedIndices;
        if (indices.Count != 1) return;
        _document.LocateFrameImage(indices[0]);
    }

    private void BrowseFile()
    {
        string? chosen = _document.Shell.Dialogs.OpenImageFile(
            _document.AtxFolder, "Choose the frame image", _fileName);
        if (chosen is null) return;
        var indices = _document.Frames.SelectedIndices;
        if (indices.Count != 1) return;
        _document.AddFrameImageFromPath(chosen, indices[0]);
    }

    private static string Describe(AtxKeyScope scope, string key)
    {
        var info = AtxSchema.FindKey(scope, key);
        if (info is null) return string.Empty;
        var parts = new List<string> { info.Summary, info.Details };
        if (info.DefaultText is { Length: > 0 } d) parts.Add($"Default: {d}.");
        if (info.RuntimeEvent is { Length: > 0 } e) parts.Add($"Level event: {e}.");
        return string.Join("\n\n", parts);
    }
}
