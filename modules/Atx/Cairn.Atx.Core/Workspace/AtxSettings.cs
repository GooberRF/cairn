namespace Cairn.Atx.Workspace;

/// <summary>Which starter text File > New gives an .atx document.</summary>
public enum NewFileTemplateKind
{
    /// <summary>The bare minimum the game needs.</summary>
    Minimal,

    /// <summary>Every key, with a comment explaining it.</summary>
    Commented,
}

/// <summary>What the preview draws behind transparent pixels.</summary>
public enum PreviewBackground
{
    /// <summary>The usual grey checkerboard.</summary>
    Checkerboard,

    /// <summary>Solid black.</summary>
    Black,

    /// <summary>Solid white.</summary>
    White,

    /// <summary><see cref="AtxSettings.PreviewCustomColor"/>.</summary>
    Custom,
}

/// <summary>
/// The ATX module's own settings, kept in the shared <see cref="AppSettings.Values"/> bag under
/// keys prefixed <c>atx.</c> so the suite's settings file needs no ATX-specific fields.
/// </summary>
/// <param name="settings">The settings instance to read and write.</param>
public sealed class AtxSettings(AppSettings settings)
{
    /// <summary>Key of <see cref="NewFileTemplate"/>.</summary>
    public const string NewFileTemplateKey = "atx.NewFileTemplate";

    /// <summary>Key of <see cref="PreviewBackground"/>.</summary>
    public const string PreviewBackgroundKey = "atx.PreviewBackground";

    /// <summary>Key of <see cref="PreviewCustomColor"/>.</summary>
    public const string PreviewCustomColorKey = "atx.PreviewCustomColor";

    /// <summary>Key of <see cref="LastImportFolder"/>.</summary>
    public const string LastImportFolderKey = "atx.LastImportFolder";

    /// <summary>The colour <see cref="PreviewCustomColor"/> starts as.</summary>
    public const string DefaultPreviewCustomColor = "#202020";

    /// <summary>The settings this accessor reads and writes.</summary>
    public AppSettings Settings { get; } = settings ?? throw new ArgumentNullException(nameof(settings));

    /// <summary>The starter text File > New uses.</summary>
    public NewFileTemplateKind NewFileTemplate
    {
        get => GetEnum(NewFileTemplateKey, NewFileTemplateKind.Minimal);
        set => Settings.Set(NewFileTemplateKey, value.ToString());
    }

    /// <summary>What the preview draws behind transparent pixels.</summary>
    public PreviewBackground PreviewBackground
    {
        get => GetEnum(PreviewBackgroundKey, PreviewBackground.Checkerboard);
        set => Settings.Set(PreviewBackgroundKey, value.ToString());
    }

    /// <summary>The colour behind the preview when <see cref="PreviewBackground"/> is Custom, as #RRGGBB.</summary>
    public string PreviewCustomColor
    {
        get => GetString(PreviewCustomColorKey) ?? DefaultPreviewCustomColor;
        set => Settings.Set(PreviewCustomColorKey, string.IsNullOrWhiteSpace(value) ? null : value);
    }

    /// <summary>The folder the last image or VBM import came from, or null.</summary>
    public string? LastImportFolder
    {
        get => GetString(LastImportFolderKey);
        set => Settings.Set(LastImportFolderKey, string.IsNullOrWhiteSpace(value) ? null : value);
    }

    // Enums are stored by name so the file stays readable, and a hand-edited or stale value
    // falls back to the default instead of failing the whole settings load.
    private T GetEnum<T>(string key, T fallback) where T : struct, Enum =>
        GetString(key) is { } text && Enum.TryParse(text, ignoreCase: true, out T value) && Enum.IsDefined(value)
            ? value
            : fallback;

    // AppSettings.Get already answers null for a value of the wrong JSON shape.
    private string? GetString(string key) => Settings.Get<string>(key);
}
