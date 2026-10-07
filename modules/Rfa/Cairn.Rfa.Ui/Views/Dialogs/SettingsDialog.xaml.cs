using System.ComponentModel;
using System.Windows;
using Cairn.Rfa.Ui.ViewModels;

namespace Cairn.Rfa.Ui.Views.Dialogs;

/// <summary>Tools &gt; Settings…; the view-model edits a copy and OK applies it.</summary>
public partial class SettingsDialog : Window
{
    private readonly SettingsViewModel _model;
    private bool _committed;

    private SettingsDialog(RfaWorkspace shell)
    {
        _model = new SettingsViewModel(shell);
        InitializeComponent();
        DataContext = _model;
        // The size is remembered between sessions, as the other resizable dialogs do.
        Gltf.GltfWindowSize.Restore(this, shell, "settingsDialog");
        Closing += (_, _) => Gltf.GltfWindowSize.Remember(this, shell, "settingsDialog");
        Closing += OnClosing;
        Loaded += (_, _) => ThemeSystem.Focus();
    }

    /// <summary>
    /// The library, viewport and time-display sections, taken out of a dialog that is never shown, for the shell's
    /// settings page (theme, game folder and search folders are the shell's own page).
    /// </summary>
    internal static FrameworkElement TakeModuleSections(RfaWorkspace shell)
    {
        var dialog = new SettingsDialog(shell);
        var sections = dialog.ModuleSections;
        ((System.Windows.Controls.Panel)sections.Parent).Children.Remove(sections);
        sections.DataContext = null;
        return sections;
    }

    /// <summary>A dialog instance for the diagnostic capture (never shown modally).</summary>
    internal static Window CreateForCapture(RfaWorkspace shell) => new SettingsDialog(shell);

    /// <summary>Shows the dialog. Returns true when the settings were saved.</summary>
    public static bool Show(Window? owner, RfaWorkspace shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        var dialog = new SettingsDialog(shell) { Owner = owner is { IsLoaded: true } ? owner : null };
        if (dialog.Owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        using (Cairn.Ui.Services.ModalScope.Enter()) return dialog.ShowDialog() == true;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        _model.Apply();
        _committed = true;
        DialogResult = true;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_committed) _model.Revert();
    }
}
