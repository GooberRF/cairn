using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Cairn.Atx.Ui.ViewModels;

namespace Cairn.Atx.Ui.Views.Dialogs;

/// <summary>
/// Add Frames from VPP…: browse the .vpp archives the document's resolver can see, tick the images
/// wanted, and insert them through the same path Add Frames… uses, so a whole batch is one Ctrl+Z.
///
/// WPF's TreeView has no multi-select, so the selection lives in check boxes on the image rows and
/// this code-behind supplies the gestures a list would have given for free: Space toggles the
/// focused row, Shift+click and Shift+Up/Down extend a range inside one archive, and Ctrl+A ticks
/// everything the filter is showing in the archive the focus is in. The view-model owns what is
/// ticked; this file only translates input into calls on it.
/// </summary>
public partial class VppBrowserDialog : Window
{
    private const string LayoutWidth = "vppBrowserWidth";
    private const string LayoutHeight = "vppBrowserHeight";

    private readonly DocumentViewModel _document;
    private readonly VppBrowserViewModel _model;

    private VppBrowserDialog(DocumentViewModel document, VppBrowserMode mode)
    {
        _document = document;
        _model = new VppBrowserViewModel(document, mode);
        InitializeComponent();
        DataContext = _model;

        if (mode == VppBrowserMode.PickOne)
        {
            Title = "Choose an image from a VPP";
            Heading.Text = "Choose an image from a .vpp archive";
            Subheading.Text =
                "The alpha mask stores just the file name, so the image has to live somewhere the "
                + "game will look.";
            // Nothing about insert position or extraction applies to picking one name.
            Options.Visibility = Visibility.Collapsed;
            OkButton.Content = "_Choose";
            AutomationProperties.SetName(OkButton, "Choose this image");
        }
        else if (mode == VppBrowserMode.PickVbm)
        {
            Title = "Import VBM from VPP";
            Heading.Text = "Choose a .vbm to import";
            Subheading.Text =
                "Only .vbm entries are listed. The next step writes each of its frames out as a "
                + "TGA and generates an .atx to play them.";
            Options.Visibility = Visibility.Collapsed;
            OkButton.Content = "_Continue";
            AutomationProperties.SetName(OkButton, "Continue to the import options");
        }

        _model.ImportRequested += OnImportRequested;
        RestoreSize(document.Shell);
        Closed += (_, _) =>
        {
            _model.ImportRequested -= OnImportRequested;
            RememberSize(document.Shell);
            _model.Dispose();
        };
        Loaded += (_, _) => FilterBox.Focus();

        // Ctrl+F is where a user looks for a filter box, wherever the focus happens to be.
        InputBindings.Add(new KeyBinding(
            new RelayCommand(() => { FilterBox.Focus(); FilterBox.SelectAll(); }),
            Key.F, ModifierKeys.Control));
    }

    /// <summary>
    /// How the Add Frames browser ended. It has two ways out: frames added, or the user following
    /// the "Import as ATX…" link on a multi-frame VBM, which closes the browser and leaves the
    /// entry for the caller to hand to the import dialog.
    /// </summary>
    /// <param name="FramesAdded">True when frames were inserted into the document.</param>
    /// <param name="ImportRequest">The VBM to import instead, or null.</param>
    public sealed record AddFramesResult(
        bool FramesAdded, (string ArchivePath, string EntryName)? ImportRequest);

    /// <summary>Shows the browser in multi-select mode.</summary>
    /// <param name="owner">Window to centre on.</param>
    /// <param name="document">The document the frames go into.</param>
    /// <summary>The add-frames browser, not shown, for screenshot runs.</summary>
    internal static Window CreateForCapture(Window owner, DocumentViewModel document) =>
        new VppBrowserDialog(document, VppBrowserMode.AddFrames) { Owner = owner };

    public static AddFramesResult ShowAddFrames(Window? owner, DocumentViewModel document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var dialog = Run(owner, document, VppBrowserMode.AddFrames, out bool accepted);
        return new AddFramesResult(accepted, dialog.ImportRequest);
    }

    /// <summary>
    /// Shows the browser in single-select mode and returns the chosen entry name, or null when the
    /// user cancelled. Used by the alpha-mask row.
    /// </summary>
    /// <param name="owner">Window to centre on.</param>
    /// <param name="document">The document whose search path is browsed.</param>
    public static string? PickOne(Window? owner, DocumentViewModel document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var model = Run(owner, document, VppBrowserMode.PickOne, out bool accepted);
        return accepted ? model.PickedName ?? string.Empty : null;
    }

    /// <summary>
    /// Shows the browser filtered to <c>.vbm</c> entries and returns the chosen one, or null when
    /// the user cancelled. Used by File &gt; Import VBM from VPP….
    /// </summary>
    /// <param name="owner">Window to centre on.</param>
    /// <param name="document">The document whose search path is browsed.</param>
    public static (string ArchivePath, string EntryName)? PickVbm(
        Window? owner, DocumentViewModel document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var model = Run(owner, document, VppBrowserMode.PickVbm, out bool accepted);
        return accepted ? model.PickedEntry : null;
    }

    private static VppBrowserViewModel Run(
        Window? owner, DocumentViewModel document, VppBrowserMode mode, out bool accepted)
    {
        var dialog = new VppBrowserDialog(document, mode)
        {
            Owner = owner is { IsLoaded: true } ? owner : null,
        };
        if (dialog.Owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        using (Cairn.Ui.Services.ModalScope.Enter())
        {
            accepted = dialog.ShowDialog() == true;
            return dialog._model;
        }
    }

    /// <summary>
    /// The "Import as ATX…" link. The browser closes without adding anything; the request is what
    /// the caller reads, so the import dialog opens after this window is gone rather than on top
    /// of it.
    /// </summary>
    private void OnImportRequested(object? sender, EventArgs e) => DialogResult = false;

    // ── Size ──────────────────────────────────────────────────────────────────

    private void RestoreSize(AtxWorkspace shell)
    {
        double width = shell.LayoutSize(LayoutWidth, Width);
        double height = shell.LayoutSize(LayoutHeight, Height);
        if (width >= MinWidth) Width = Math.Min(width, SystemParameters.VirtualScreenWidth);
        if (height >= MinHeight) Height = Math.Min(height, SystemParameters.VirtualScreenHeight);
    }

    private void RememberSize(AtxWorkspace shell)
    {
        if (WindowState != WindowState.Normal) return;
        shell.SetLayoutSize(LayoutWidth, ActualWidth);
        shell.SetLayoutSize(LayoutHeight, ActualHeight);
        shell.SaveSettings();
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    private void OnOpenArchive(object sender, RoutedEventArgs e)
    {
        string? chosen = _document.Shell.Dialogs.OpenVppFile(_document.AtxFolder);
        if (chosen is not null) _model.OpenArchive(chosen);
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!_model.CanApply) return;
        if (!_model.Apply()) return;
        DialogResult = true;
    }

    private void OnSelectAllVisible(object sender, RoutedEventArgs e) =>
        _model.SelectAllVisibleInFocusedArchive();

    // ── Tree input ────────────────────────────────────────────────────────────

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        _model.OnFocusChanged(e.NewValue as VppNodeViewModel);

    private void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        bool control = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

        switch (e.Key)
        {
            case Key.Space:
                _model.ToggleFocused();
                e.Handled = true;
                break;
            case Key.A when control:
                _model.SelectAllVisibleInFocusedArchive();
                e.Handled = true;
                break;
            case Key.Up or Key.Down when shift:
                // Let the tree move the focus first, then extend the range to wherever it landed.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_model.Focused is { } landed) _model.SelectRangeTo(landed);
                }), System.Windows.Threading.DispatcherPriority.Input);
                break;
            case Key.Enter when _model.IsSingleSelect && _model.CanApply:
                OnOk(sender, e);
                e.Handled = true;
                break;
        }
    }

    private void OnTreeMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ImageNodeAt(e.OriginalSource as DependencyObject) is not { } image) return;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            _model.SelectRangeTo(image);
            // The tree would otherwise move the focus and lose the anchor the range was measured
            // from; the range has already been applied, so the click has done its job.
            e.Handled = true;
        }
        else _model.SetRangeAnchor(image);
    }

    private void OnTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!_model.IsSingleSelect) return;
        if (ImageNodeAt(e.OriginalSource as DependencyObject) is null) return;
        if (!_model.CanApply) return;
        OnOk(sender, e);
        e.Handled = true;
    }

    /// <summary>The image row under a hit-tested element, or null when the click missed one.</summary>
    private static VppImageNodeViewModel? ImageNodeAt(DependencyObject? source)
    {
        while (source is not null and not TreeViewItem)
        {
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return (source as TreeViewItem)?.DataContext as VppImageNodeViewModel;
    }
}
