using System.Windows;
using System.Windows.Input;
using Cairn.Rfa.Ui.Services;
using Cairn.Rfa.Ui.ViewModels.MeshEditing;

namespace Cairn.Rfa.Ui.Views.Dialogs.MeshEditing;

/// <summary>
/// The reorder warning (Mesh › Reorder Bones…, Move Bone Up / Down): the new order, the warning that
/// clips address bones by index, and the open clips to conform in the same action. Enter applies, Esc cancels.
/// </summary>
public partial class ReorderBonesDialog : Window
{
    private readonly ReorderBonesViewModel _model;
    private bool _modal;

    private ReorderBonesDialog(ReorderBonesViewModel model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        InitializeComponent();
        DataContext = model;
        Loaded += (_, _) =>
        {
            if (model.Selected is { } s) BoneList.ScrollIntoView(s);
            BoneList.Focus();
        };
    }

    /// <summary>Shows the dialog modally. Returns true when the reorder was applied.</summary>
    public static bool ShowModal(Window? owner, ReorderBonesViewModel model)
    {
        var dialog = new ReorderBonesDialog(model) { Owner = owner is { IsLoaded: true } ? owner : null, _modal = true };
        if (dialog.Owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        using (ModalScope.Enter()) return dialog.ShowDialog() == true;
    }

    /// <summary>A window for the diagnostic capture (shown non-modally by the caller; closing it changes nothing).</summary>
    internal static ReorderBonesDialog CreateForCapture(ReorderBonesViewModel model) => new(model);

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!_model.Apply()) return;
        if (_modal) DialogResult = true;
        else Close();
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Alt) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var command = key switch
        {
            Key.Up => _model.MoveUpCommand,
            Key.Down => _model.MoveDownCommand,
            _ => null,
        };
        if (command is null) return;
        if (command.CanExecute(null)) command.Execute(null);
        if (_model.Selected is { } s) BoneList.ScrollIntoView(s);
        e.Handled = true;
    }
}
