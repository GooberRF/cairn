using Cairn.Rfa.Ui.ViewModels.PoseEditing;

namespace Cairn.Rfa.Ui.ViewModels;

/// <summary>The app-wide, persisted pose-editing switches (tool, space, IK) and the session's bind-editing options.</summary>
public sealed partial class RfaWorkspace
{
    private PoseEditSettings? _poseSettings;

    /// <summary>
    /// The bind-editing options (children follow) the bone editor and the mesh gizmos share; kept for the
    /// session, not persisted.
    /// </summary>
    public BindEditOptions BindOptions { get; } = new();

    /// <summary>
    /// The pose tool, gizmo space and IK toggle every clip document shares, read from the settings on
    /// first use; a change is stored in <see cref="Settings"/> at once and saved soon (never in a
    /// diagnostic run, which <see cref="SaveSettingsSoon"/> enforces).
    /// </summary>
    public PoseEditSettings PoseSettings
    {
        get
        {
            if (_poseSettings is not null) return _poseSettings;
            var settings = new PoseEditSettings();
            settings.Load(Settings);
            settings.Changed += (_, _) =>
            {
                settings.Store(Settings);
                SaveSettingsSoon();
            };
            return _poseSettings = settings;
        }
    }
}
