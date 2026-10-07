using Cairn.Ui.Mvvm;
using Cairn.Rfa.Editing;
using Cairn.Workspace;

namespace Cairn.Rfa.Ui.ViewModels.PoseEditing;

/// <summary>
/// The pose-editing switches that persist across sessions and are shared by every clip document: the
/// tool (Q / E / W), the gizmo space and the IK toggle (settings key <see cref="SettingsKey"/>). Auto-key
/// is a viewport display setting (<see cref="ViewportDisplaySettings.AutoKey"/>), persisted with those.
/// One instance lives on the shell (<see cref="RfaWorkspace.PoseSettings"/>); each document's
/// <see cref="PoseEditController"/> reads and writes it, so a change in one tab applies to all of them
/// and a new document starts with the last values used.
/// </summary>
public sealed class PoseEditSettings : ObservableObject
{
    /// <summary>The settings key.</summary>
    public const string SettingsKey = "rfa.poseEditing";

    private PoseTool _tool = PoseTool.Select;
    private OffsetSpace _space = OffsetSpace.Local;
    private bool _ikEnabled = true;

    /// <summary>Raised after any value changes (the shell stores and saves the settings).</summary>
    public event EventHandler? Changed;

    /// <summary>The active tool.</summary>
    public PoseTool Tool { get => _tool; set => SetAndNotify(ref _tool, Enum.IsDefined(value) ? value : PoseTool.Select); }

    /// <summary>The frame gizmo axes and offsets are in.</summary>
    public OffsetSpace Space { get => _space; set => SetAndNotify(ref _space, Enum.IsDefined(value) ? value : OffsetSpace.Local); }

    /// <summary>Two-bone IK drag with the move tool on a limb's end.</summary>
    public bool IkEnabled { get => _ikEnabled; set => SetAndNotify(ref _ikEnabled, value); }

    private void SetAndNotify<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (Set(ref field, value, name)) Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Reads the persisted values (defaults for anything missing or unreadable).</summary>
    public void Load(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Get<Dto>(SettingsKey) is not { } dto) return;
        _tool = Enum.IsDefined(dto.Tool) ? dto.Tool : PoseTool.Select;
        _space = Enum.IsDefined(dto.Space) ? dto.Space : OffsetSpace.Local;
        _ikEnabled = dto.Ik;
    }

    /// <summary>Writes the values into <paramref name="settings"/> (the caller saves the file).</summary>
    public void Store(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Set(SettingsKey, new Dto { Tool = Tool, Space = Space, Ik = IkEnabled });
    }

    /// <summary>The stored values as <see cref="AppSettings"/> holds them (diagnostics).</summary>
    public static (PoseTool Tool, OffsetSpace Space, bool Ik)? Read(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Get<Dto>(SettingsKey) is { } dto ? (dto.Tool, dto.Space, dto.Ik) : null;
    }

    private sealed class Dto
    {
        public PoseTool Tool { get; set; } = PoseTool.Select;
        public OffsetSpace Space { get; set; } = OffsetSpace.Local;
        public bool Ik { get; set; } = true;
    }
}
