using Cairn.Ui.Mvvm;
using Cairn.Workspace;

namespace Cairn.Rfa.Ui.ViewModels;

/// <summary>How the mesh is drawn.</summary>
public enum MeshDisplayMode
{
    /// <summary>With its textures (a neutral material where one is missing).</summary>
    Textured,
    /// <summary>One flat neutral material.</summary>
    Flat,
    /// <summary>Not drawn (skeleton and overlays only).</summary>
    Off,
}


/// <summary>
/// The viewport's display toggles, shared by every document's viewport and persisted in the settings
/// (<see cref="AppSettings.Values"/> key <see cref="SettingsKey"/>). The Settings dialog edits the same
/// values as the viewport toolbar: they are the "viewport defaults".
/// </summary>
public sealed class ViewportDisplaySettings : ObservableObject
{
    /// <summary>The settings key.</summary>
    public const string SettingsKey = "rfa.viewport";

    private MeshDisplayMode _meshMode = MeshDisplayMode.Textured;
    private bool _fullBright;
    private bool _showSkeleton = true;
    private bool _showBoneNames;
    private bool _showGrid = true;
    private bool _showSpheres;
    private bool _showProps;
    private bool _bindPose;
    private bool _showRootPath;
    private bool _perspective = true;
    private ViewportBackground _background = ViewportBackground.Theme;
    private bool _autoKey = true;
    private bool _ghostSavedClip;

    /// <summary>Raised after any toggle changes.</summary>
    public event EventHandler? Changed;

    public MeshDisplayMode MeshMode { get => _meshMode; set => SetAndNotify(ref _meshMode, value); }

    /// <summary>Unlit: the texture's own colours (the engine's look for emissive materials).</summary>
    public bool FullBright { get => _fullBright; set => SetAndNotify(ref _fullBright, value); }

    public bool ShowSkeleton { get => _showSkeleton; set => SetAndNotify(ref _showSkeleton, value); }

    public bool ShowBoneNames { get => _showBoneNames; set => SetAndNotify(ref _showBoneNames, value); }

    public bool ShowGrid { get => _showGrid; set => SetAndNotify(ref _showGrid, value); }

    public bool ShowSpheres { get => _showSpheres; set => SetAndNotify(ref _showSpheres, value); }

    public bool ShowProps { get => _showProps; set => SetAndNotify(ref _showProps, value); }

    /// <summary>Show the mesh's bind (rest) pose instead of the clip.</summary>
    public bool BindPose { get => _bindPose; set => SetAndNotify(ref _bindPose, value); }

    public bool ShowRootPath { get => _showRootPath; set => SetAndNotify(ref _showRootPath, value); }

    /// <summary>Perspective (true) or orthographic projection.</summary>
    public bool Perspective { get => _perspective; set => SetAndNotify(ref _perspective, value); }

    public ViewportBackground Background { get => _background; set => SetAndNotify(ref _background, value); }

    /// <summary>Pose editing: a manipulation keys the bone at the playhead (on) or offsets every key (off, a layer edit).</summary>
    public bool AutoKey { get => _autoKey; set => SetAndNotify(ref _autoKey, value); }

    /// <summary>Draws the clip as last saved as a dimmed ghost skeleton while it has unsaved changes.</summary>
    public bool GhostSavedClip { get => _ghostSavedClip; set => SetAndNotify(ref _ghostSavedClip, value); }

    public bool IsMeshTextured { get => _meshMode == MeshDisplayMode.Textured; set { if (value) MeshMode = MeshDisplayMode.Textured; } }

    public bool IsMeshFlat { get => _meshMode == MeshDisplayMode.Flat; set { if (value) MeshMode = MeshDisplayMode.Flat; } }

    public bool IsMeshOff { get => _meshMode == MeshDisplayMode.Off; set { if (value) MeshMode = MeshDisplayMode.Off; } }

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

    private void SetAndNotify<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!Set(ref field, value, name)) return;
        if (name == nameof(MeshMode)) RaiseAll(nameof(IsMeshTextured), nameof(IsMeshFlat), nameof(IsMeshOff));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Copies every value from <paramref name="other"/>.</summary>
    public void CopyFrom(ViewportDisplaySettings other)
    {
        ArgumentNullException.ThrowIfNull(other);
        MeshMode = other.MeshMode;
        FullBright = other.FullBright;
        ShowSkeleton = other.ShowSkeleton;
        ShowBoneNames = other.ShowBoneNames;
        ShowGrid = other.ShowGrid;
        ShowSpheres = other.ShowSpheres;
        ShowProps = other.ShowProps;
        BindPose = other.BindPose;
        ShowRootPath = other.ShowRootPath;
        Perspective = other.Perspective;
        Background = other.Background;
        AutoKey = other.AutoKey;
        GhostSavedClip = other.GhostSavedClip;
    }

    /// <summary>Reads the persisted values (defaults for anything missing).</summary>
    public void Load(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var dto = settings.Get<Dto>(SettingsKey);
        if (dto is null) return;
        MeshMode = Enum.IsDefined(dto.MeshMode) ? dto.MeshMode : MeshDisplayMode.Textured;
        FullBright = dto.FullBright;
        ShowSkeleton = dto.ShowSkeleton;
        ShowBoneNames = dto.ShowBoneNames;
        ShowGrid = dto.ShowGrid;
        ShowSpheres = dto.ShowSpheres;
        ShowProps = dto.ShowProps;
        BindPose = dto.BindPose;
        ShowRootPath = dto.ShowRootPath;
        Perspective = dto.Perspective;
        Background = Enum.IsDefined(dto.Background) ? dto.Background : ViewportBackground.Theme;
        AutoKey = dto.AutoKey;
        GhostSavedClip = dto.GhostSavedClip;
    }

    /// <summary>Writes the values into <paramref name="settings"/> (the caller saves the file).</summary>
    public void Store(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Set(SettingsKey, new Dto
        {
            MeshMode = MeshMode, FullBright = FullBright, ShowSkeleton = ShowSkeleton, ShowBoneNames = ShowBoneNames,
            ShowGrid = ShowGrid, ShowSpheres = ShowSpheres, ShowProps = ShowProps, BindPose = BindPose,
            ShowRootPath = ShowRootPath, Perspective = Perspective, Background = Background,
            AutoKey = AutoKey, GhostSavedClip = GhostSavedClip,
        });
    }

    private sealed class Dto
    {
        public MeshDisplayMode MeshMode { get; set; } = MeshDisplayMode.Textured;
        public bool FullBright { get; set; }
        public bool ShowSkeleton { get; set; } = true;
        public bool ShowBoneNames { get; set; }
        public bool ShowGrid { get; set; } = true;
        public bool ShowSpheres { get; set; }
        public bool ShowProps { get; set; }
        public bool BindPose { get; set; }
        public bool ShowRootPath { get; set; }
        public bool Perspective { get; set; } = true;
        public ViewportBackground Background { get; set; } = ViewportBackground.Theme;
        public bool AutoKey { get; set; } = true;
        public bool GhostSavedClip { get; set; }
    }
}
