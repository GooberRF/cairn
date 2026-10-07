using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cairn.Workspace;

/// <summary>Which theme the app follows.</summary>
public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>Saved window placement.</summary>
public sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
    /// <summary>False until the window has been placed once, so the app can centre on first run.</summary>
    public bool IsSet { get; set; }
}

/// <summary>
/// Everything the app remembers between runs. Persisted as JSON in
/// <c>%APPDATA%\Cairn\settings.json</c>; every property has a usable default so a missing or
/// damaged file costs nothing. Settings a later feature needs without a typed property go in
/// <see cref="Values"/> through <see cref="Get{T}"/> and <see cref="Set{T}"/>.
/// </summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions ValueOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>The Red Faction install directory, auto-detected on first run.</summary>
    public string? GameDirectory { get; set; }

    /// <summary>Extra folders searched for clips, meshes, tables and textures, in order.</summary>
    public List<string> SearchFolders { get; set; } = [];

    /// <summary>Most recently opened files, newest first.</summary>
    public List<string> RecentFiles { get; set; } = [];

    /// <summary>How many recent files to keep.</summary>
    public int MaxRecentFiles { get; set; } = 12;

    /// <summary>Splitter positions and other sizes, keyed by a name the UI chooses.</summary>
    public Dictionary<string, double> Layout { get; set; } = [];

    /// <summary>Panel visibility flags, keyed by a name the UI chooses.</summary>
    public Dictionary<string, bool> Panels { get; set; } = [];

    public WindowPlacement Window { get; set; } = new();

    /// <summary>
    /// Where the last Save As or export wrote. Offered again for a document that came out of a .vpp,
    /// which has no folder of its own to suggest — and deliberately never the game directory.
    /// </summary>
    public string? LastSaveFolder { get; set; }

    /// <summary>
    /// The free-form store: any JSON value under any key. Later features keep their settings here
    /// (viewport defaults, per-clip preview meshes, retarget profiles) without a schema change.
    /// </summary>
    public Dictionary<string, JsonElement> Values { get; set; } = [];

    /// <summary>The value stored under <paramref name="key"/>, or <paramref name="fallback"/> when absent or not a <typeparamref name="T"/>.</summary>
    public T? Get<T>(string key, T? fallback = default)
    {
        if (!Values.TryGetValue(key, out var element)) return fallback;
        try
        {
            return element.Deserialize<T>(ValueOptions) ?? fallback;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return fallback;
        }
    }

    /// <summary>Stores <paramref name="value"/> under <paramref name="key"/> (null removes the key).</summary>
    public void Set<T>(string key, T? value)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (value is null)
        {
            Values.Remove(key);
            return;
        }
        Values[key] = JsonSerializer.SerializeToElement(value, ValueOptions);
    }

    /// <summary>Returns an independent copy, so a settings dialog can cancel cleanly.</summary>
    public AppSettings Clone() => new()
    {
        Theme = Theme,
        GameDirectory = GameDirectory,
        SearchFolders = [.. SearchFolders],
        RecentFiles = [.. RecentFiles],
        MaxRecentFiles = MaxRecentFiles,
        Layout = new Dictionary<string, double>(Layout),
        Panels = new Dictionary<string, bool>(Panels),
        Window = new WindowPlacement
        {
            Left = Window.Left,
            Top = Window.Top,
            Width = Window.Width,
            Height = Window.Height,
            Maximized = Window.Maximized,
            IsSet = Window.IsSet,
        },
        LastSaveFolder = LastSaveFolder,
        // JsonElements produced by SerializeToElement / Deserialize are immutable and self-contained.
        Values = new Dictionary<string, JsonElement>(Values),
    };
}

/// <summary>
/// Loads and saves <see cref="AppSettings"/>. Reading never throws: a missing, unreadable or corrupt
/// file simply yields defaults, because losing preferences must never block startup.
/// </summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary><c>%APPDATA%\Cairn</c>.</summary>
    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cairn");

    /// <summary><c>%APPDATA%\Cairn\settings.json</c>.</summary>
    public static string DefaultPath { get; } = Path.Combine(DefaultDirectory, "settings.json");

    /// <summary>Reads settings, returning defaults for anything missing or malformed.</summary>
    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return new AppSettings();
            string json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
            // A hand-edited "null" for a collection must not become a crash later.
            settings.SearchFolders ??= [];
            settings.RecentFiles ??= [];
            settings.Layout ??= [];
            settings.Panels ??= [];
            settings.Window ??= new WindowPlacement();
            settings.Values ??= [];
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or NotSupportedException or ArgumentException)
        {
            return new AppSettings();
        }
    }

    /// <summary>Writes settings atomically. Returns false when the file could not be written.</summary>
    public static bool Save(AppSettings settings, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(settings, Options));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}
