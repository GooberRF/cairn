using System.Windows;
using System.Windows.Input;
using Cairn.Rfa.Ui.Services;
using Cairn.Rfa.Ui.ViewModels.MeshEditing;

namespace Cairn.Rfa.Ui.Views.Dialogs.MeshEditing;

/// <summary>A filtered list of every texture the resolver sees; Enter or a double-click picks, Esc cancels.</summary>
public partial class TextureBrowserDialog : Window
{
    private readonly TextureBrowserViewModel _model;
    private bool _modal;

    private TextureBrowserDialog(TextureBrowserViewModel model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        InitializeComponent();
        DataContext = model;
        Loaded += (_, _) =>
        {
            FilterBox.Focus();
            _ = LoadAsync();
        };
    }

    private async Task LoadAsync()
    {
        await _model.LoadAsync().ConfigureAwait(true);
        if (_model.Selected is { } s) List.ScrollIntoView(s);
    }

    /// <summary>Shows the browser modally; returns the picked name or null.</summary>
    public static string? ShowModal(Window? owner, TextureBrowserViewModel model)
    {
        var dialog = new TextureBrowserDialog(model) { Owner = owner is { IsLoaded: true } ? owner : null, _modal = true };
        if (dialog.Owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        using (ModalScope.Enter()) return dialog.ShowDialog() == true ? model.Selected?.Name : null;
    }

    /// <summary>A window for the diagnostic capture (shown non-modally by the caller).</summary>
    internal static TextureBrowserDialog CreateForCapture(TextureBrowserViewModel model) => new(model);

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!_model.CanPick) return;
        if (_modal) DialogResult = true;
        else Close();
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_model.CanPick) OnOk(sender, e);
    }

    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Down || List.Items.Count == 0) return;
        if (List.SelectedIndex < 0) List.SelectedIndex = 0;
        List.UpdateLayout();
        (List.ItemContainerGenerator.ContainerFromIndex(List.SelectedIndex) as UIElement)?.Focus();
        e.Handled = true;
    }
}
