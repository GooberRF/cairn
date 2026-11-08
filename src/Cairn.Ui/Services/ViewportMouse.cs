using System.Text.Json.Serialization;
using Cairn.Workspace;

namespace Cairn.Ui.Services;

/// <summary>
/// The mouse options every 3D viewport shares (Tools › Settings… › General › 3D VIEWPORT MOUSE), persisted under
/// <see cref="SettingsKey"/>. Orbit (left-drag) and fly-mode mouse look are inverted separately; panning always
/// follows the cursor.
/// </summary>
public sealed class ViewportMouse
{
    /// <summary>The <see cref="AppSettings"/> key the options persist under.</summary>
    public const string SettingsKey = "viewportMouse";

    /// <summary>The options in effect; <see cref="Load"/> sets them at startup and the Settings dialog after OK.</summary>
    public static ViewportMouse Current { get; private set; } = new();

    /// <summary>Left-drag orbit: dragging right turns the view the other way.</summary>
    public bool InvertOrbitX { get; set; }

    /// <summary>Left-drag orbit: dragging down turns the view the other way.</summary>
    public bool InvertOrbitY { get; set; }

    /// <summary>Fly-mode mouse look: dragging right turns the view the other way.</summary>
    public bool InvertFlyX { get; set; }

    /// <summary>Fly-mode mouse look: dragging down turns the view the other way.</summary>
    public bool InvertFlyY { get; set; }

    /// <summary>-1 when <see cref="InvertOrbitX"/>, otherwise 1.</summary>
    [JsonIgnore]
    public double OrbitX => InvertOrbitX ? -1 : 1;

    /// <summary>-1 when <see cref="InvertOrbitY"/>, otherwise 1.</summary>
    [JsonIgnore]
    public double OrbitY => InvertOrbitY ? -1 : 1;

    /// <summary>-1 when <see cref="InvertFlyX"/>, otherwise 1.</summary>
    [JsonIgnore]
    public double FlyX => InvertFlyX ? -1 : 1;

    /// <summary>-1 when <see cref="InvertFlyY"/>, otherwise 1.</summary>
    [JsonIgnore]
    public double FlyY => InvertFlyY ? -1 : 1;

    /// <summary>The options stored in <paramref name="settings"/>, or the defaults (nothing inverted).</summary>
    public static ViewportMouse Read(AppSettings settings) => settings.Get<ViewportMouse>(SettingsKey) ?? new();

    /// <summary>Makes the options stored in <paramref name="settings"/> the ones in effect.</summary>
    public static void Load(AppSettings settings) => Current = Read(settings);

    /// <summary>Stores <paramref name="options"/> in <paramref name="settings"/> and puts them in effect.</summary>
    public static void Apply(AppSettings settings, ViewportMouse options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var copy = new ViewportMouse
        {
            InvertOrbitX = options.InvertOrbitX,
            InvertOrbitY = options.InvertOrbitY,
            InvertFlyX = options.InvertFlyX,
            InvertFlyY = options.InvertFlyY,
        };
        settings.Set(SettingsKey, copy);
        Current = copy;
    }
}
