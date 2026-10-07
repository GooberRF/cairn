using System.Windows.Media;
using Cairn.Ui.Mvvm;
using Cairn.Workspace;

namespace Cairn.Viewport;

/// <summary>The viewport background.</summary>
public enum ViewportBackground
{
    /// <summary>The theme's viewport colour.</summary>
    Theme,
    Black,
    DarkGrey,
    MidGrey,
    LightGrey,
    White,
}

/// <summary>The persisted shape of the generic toggles; a module derives it to add its own.</summary>
public class ViewportDisplaySettingsDto
{
    public bool FullBright { get; set; }
    public bool ShowGrid { get; set; } = true;
    public bool Perspective { get; set; } = true;
    public ViewportBackground Background { get; set; } = ViewportBackground.Theme;
}

/// <summary>
/// The generic viewport display toggles (grid, background, projection, fullbright), shared by every
/// document's viewport of a module and persisted in the settings under <see cref="SettingsKey"/>. A module
/// derives it for its own toggles, extending <see cref="ViewportDisplaySettingsDto"/> and overriding
/// <see cref="Load"/> / <see cref="Store"/> / <see cref="CopyFrom"/>.
/// </summary>
public class ViewportDisplaySettingsBase : ObservableObject
{
    private bool _fullBright;
    private bool _showGrid = true;
    private bool _perspective = true;
    private ViewportBackground _background = ViewportBackground.Theme;

    /// <param name="settingsKey">The <see cref="AppSettings"/> key the values persist under.</param>
    public ViewportDisplaySettingsBase(string settingsKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsKey);
        SettingsKey = settingsKey;
    }

    /// <summary>The settings key.</summary>
    public string SettingsKey { get; }

    /// <summary>Raised after any toggle changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Unlit: the texture's own colours (the engine's look for emissive materials).</summary>
    public bool FullBright { get => _fullBright; set => SetAndNotify(ref _fullBright, value); }

    public bool ShowGrid { get => _showGrid; set => SetAndNotify(ref _showGrid, value); }

    /// <summary>Perspective (true) or orthographic projection.</summary>
    public bool Perspective { get => _perspective; set => SetAndNotify(ref _perspective, value); }

    public ViewportBackground Background { get => _background; set => SetAndNotify(ref _background, value); }

    /// <summary>The background choices in the order the menus list them.</summary>
    public static IReadOnlyList<(ViewportBackground Value, string Label)> BackgroundChoices { get; } =
    [
        (ViewportBackground.Theme, "Theme colour"),
        (ViewportBackground.Black, "Black"),
        (ViewportBackground.DarkGrey, "Dark grey"),
        (ViewportBackground.MidGrey, "Mid grey"),
        (ViewportBackground.LightGrey, "Light grey"),
        (ViewportBackground.White, "White"),
    ];

    /// <summary>The fixed colour of a background choice; null for <see cref="ViewportBackground.Theme"/> (use the theme's brush).</summary>
    public static Color? BackgroundColor(ViewportBackground background) => background switch
    {
        ViewportBackground.Black => Colors.Black,
        ViewportBackground.DarkGrey => Color.FromRgb(0x30, 0x31, 0x36),
        ViewportBackground.MidGrey => Color.FromRgb(0x6E, 0x70, 0x76),
        ViewportBackground.LightGrey => Color.FromRgb(0xC8, 0xCA, 0xCE),
        ViewportBackground.White => Colors.White,
        _ => null,
    };

    /// <summary>Sets a toggle; on a change raises <see cref="OnToggleChanged"/> and <see cref="Changed"/>.</summary>
    protected void SetAndNotify<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!Set(ref field, value, name)) return;
        OnToggleChanged(name);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Called after a toggle changed, before <see cref="Changed"/> (raise dependent properties here).</summary>
    protected virtual void OnToggleChanged(string? name)
    {
    }

    /// <summary>Copies every value from <paramref name="other"/>.</summary>
    public virtual void CopyFrom(ViewportDisplaySettingsBase other)
    {
        ArgumentNullException.ThrowIfNull(other);
        FullBright = other.FullBright;
        ShowGrid = other.ShowGrid;
        Perspective = other.Perspective;
        Background = other.Background;
    }

    /// <summary>Reads the persisted values (defaults for anything missing).</summary>
    public virtual void Load(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Get<ViewportDisplaySettingsDto>(SettingsKey) is { } dto) ReadFrom(dto);
    }

    /// <summary>Writes the values into <paramref name="settings"/> (the caller saves the file).</summary>
    public virtual void Store(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var dto = new ViewportDisplaySettingsDto();
        WriteTo(dto);
        settings.Set(SettingsKey, dto);
    }

    /// <summary>Applies the generic values of a loaded DTO.</summary>
    protected void ReadFrom(ViewportDisplaySettingsDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        FullBright = dto.FullBright;
        ShowGrid = dto.ShowGrid;
        Perspective = dto.Perspective;
        Background = Enum.IsDefined(dto.Background) ? dto.Background : ViewportBackground.Theme;
    }

    /// <summary>Fills the generic values of a DTO about to be stored.</summary>
    protected void WriteTo(ViewportDisplaySettingsDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        dto.FullBright = FullBright;
        dto.ShowGrid = ShowGrid;
        dto.Perspective = Perspective;
        dto.Background = Background;
    }
}
