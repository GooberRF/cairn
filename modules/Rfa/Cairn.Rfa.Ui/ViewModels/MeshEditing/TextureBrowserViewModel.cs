using System.Collections.ObjectModel;
using System.Globalization;
using Cairn.Ui.Mvvm;
using Cairn.Rfa.Ui.Services;
using Cairn.Assets;
using Cairn.Rfa.Formats.V3d;

namespace Cairn.Rfa.Ui.ViewModels.MeshEditing;

/// <summary>One texture the resolver can see.</summary>
/// <param name="Name">The file name as the material would store it.</param>
/// <param name="Location">Where it is (folder or archive).</param>
public sealed record TextureEntry(string Name, string Location)
{
    /// <summary>True when the name does not fit a material's 32-byte field.</summary>
    public bool TooLong => Name.Length > V3dMaterial.NameSize - 1;

    public string ToolTip => TooLong
        ? $"{Name} ({Location}) is {Name.Length} characters; a material holds at most {V3dMaterial.NameSize - 1}."
        : $"{Name} in {Location}";
}

/// <summary>
/// The texture browser behind a material's Browse… button: every texture file the document's resolver
/// sees (its folder, the search folders, the game's loose files and archives), listed off the UI thread
/// once the archives are indexed, filtered as you type.
/// </summary>
public sealed class TextureBrowserViewModel : ObservableObject
{
    /// <summary>The texture extensions listed (what the engine and the preview load).</summary>
    public static readonly string[] Extensions = [".tga", ".vbm", ".dds"];

    private const int MaxShown = 3000;
    private readonly RfaWorkspace _shell;
    private readonly AssetResolver _resolver;
    private List<TextureEntry> _all = [];
    private string _filter = string.Empty;
    private TextureEntry? _selected;
    private bool _isLoading = true;
    private string _status = "Looking through the game's folders and archives…";

    public TextureBrowserViewModel(RfaWorkspace shell, AssetResolver resolver, string current)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        Current = current ?? string.Empty;
    }

    /// <summary>The material's texture when the browser opened.</summary>
    public string Current { get; }

    public string Title => "Choose a texture";

    public ObservableCollection<TextureEntry> Items { get; } = [];

    public string Filter
    {
        get => _filter;
        set
        {
            if (Set(ref _filter, value ?? string.Empty)) ApplyFilter();
        }
    }

    public TextureEntry? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value)) RaiseAll(nameof(CanPick), nameof(SelectedNote));
        }
    }

    /// <summary>True when a texture whose name fits is selected.</summary>
    public bool CanPick => _selected is { TooLong: false };

    public string SelectedNote => _selected is null ? string.Empty : _selected.ToolTip;

    public bool IsLoading
    {
        get => _isLoading;
        private set => Set(ref _isLoading, value);
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>Lists the textures (off the UI thread); the list fills when done.</summary>
    public async Task LoadAsync()
    {
        using var busy = BusyTracker.Begin("texture browser");
        try
        {
            await _shell.Assets.ArchivesIndexed.ConfigureAwait(true);
            var resolver = _resolver;
            var found = await Task.Run(() => resolver.Enumerate(Extensions)
                .Select(l => new TextureEntry(Path.GetFileName(l.ResolvedName), l.DisplayLocation))
                .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList()).ConfigureAwait(true);
            _all = found;
            IsLoading = false;
            ApplyFilter();
            Selected ??= Items.FirstOrDefault(e => string.Equals(e.Name, Current, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            IsLoading = false;
            Status = "The textures could not be listed: " + ex.Message;
        }
    }

    /// <summary>Fills the list from entries given directly (self-tests).</summary>
    internal void SetEntries(IEnumerable<TextureEntry> entries)
    {
        _all = [.. entries];
        IsLoading = false;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (IsLoading) return;
        string f = _filter.Trim();
        var matches = f.Length == 0 ? _all : _all.Where(e => e.Name.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        var keep = _selected;
        Items.Clear();
        foreach (var e in matches.Take(MaxShown)) Items.Add(e);
        if (keep is not null && Items.Contains(keep)) Selected = keep;
        var ci = CultureInfo.CurrentCulture;
        Status = _all.Count == 0 ? "No texture files were found; check the game directory and search folders in Settings."
            : matches.Count > MaxShown ? $"{matches.Count.ToString("N0", ci)} of {_all.Count.ToString("N0", ci)} textures match; the first {MaxShown.ToString("N0", ci)} are shown, type more to narrow it."
            : f.Length == 0 ? $"{_all.Count.ToString("N0", ci)} textures."
            : $"{matches.Count.ToString("N0", ci)} of {_all.Count.ToString("N0", ci)} textures match.";
    }
}
