using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Cairn.Rfa.Ui.ViewModels;

namespace Cairn.Rfa.Ui.Views;

/// <summary>
/// One document's view (kept alive per tab by the main window): banners, the viewport with its
/// preview-partner picker and transport, and the inspector tab host. This file keeps the inspector's
/// width in the saved layout and shows or hides it with View &gt; Inspector.
/// </summary>
public partial class DocumentView : UserControl
{
    private ComboBox? _previewBox;
    private DocumentViewModel? _document;

    public DocumentView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as DocumentViewModel);
        IsVisibleChanged += (_, _) => { if (IsVisible) RestoreLayout(); };
        InspectorSplitter.DragCompleted += (_, _) => SaveLayout();
        Loaded += (_, _) => RestoreLayout();
        // The inspector gives way before the viewport does (its saved width comes back when there is room).
        SizeChanged += (_, _) => InspectorColumn.MaxWidth = Math.Max(MinInspectorWidth, ActualWidth - 6 - MinViewportWidth);
    }

    /// <summary>The viewport width the inspector leaves when it can: the transport then fits on one line.</summary>
    public const double MinViewportWidth = 420;

    /// <summary>The narrowest the inspector gets on its own (it can still be hidden with View › Inspector).</summary>
    public const double MinInspectorWidth = 280;

    /// <summary>The document's viewport.</summary>
    public Viewport.ViewportControl ViewportControl => Viewport;

    private RfaWorkspace? Shell => _document?.Shell;

    private void Attach(DocumentViewModel? document)
    {
        if (_document is ClipDocumentViewModel oldClip) oldClip.PreviewPickerRequested -= OnPreviewPickerRequested;
        // weak: the workspace lives for the whole session and would otherwise keep every closed document's view alive
        if (_document is not null) PropertyChangedEventManager.RemoveHandler(_document.Shell, OnShellPropertyChanged, nameof(RfaWorkspace.IsInspectorVisible));
        _document = document;
        if (_document is ClipDocumentViewModel clip) clip.PreviewPickerRequested += OnPreviewPickerRequested;
        if (_document is not null) PropertyChangedEventManager.AddHandler(_document.Shell, OnShellPropertyChanged, nameof(RfaWorkspace.IsInspectorVisible));
        RestoreLayout();
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RfaWorkspace.IsInspectorVisible)) RestoreLayout();
    }

    private void OnPreviewBoxLoaded(object sender, RoutedEventArgs e) => _previewBox = sender as ComboBox;

    private void OnPreviewPickerRequested(object? sender, EventArgs e)
    {
        if (_previewBox is null) return;
        _previewBox.Focus();
        _previewBox.IsDropDownOpen = true;
    }

    private void RestoreLayout()
    {
        if (Shell is not { } shell) return;
        bool visible = shell.IsInspectorVisible;
        InspectorPane.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        InspectorSplitter.Visibility = InspectorPane.Visibility;
        InspectorColumn.Width = visible ? new GridLength(shell.LayoutSize(RfaWorkspace.LayoutInspectorWidth, 330)) : new GridLength(0);
    }

    private void SaveLayout()
    {
        if (Shell is not { } shell || !shell.IsInspectorVisible) return;
        shell.SetLayoutSize(RfaWorkspace.LayoutInspectorWidth, InspectorColumn.ActualWidth);
        shell.SaveSettingsSoon();
    }
}
