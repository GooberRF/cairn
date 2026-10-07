using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Cairn.Atx.Ui.ViewModels;

namespace Cairn.Atx.Ui.Views;

/// <summary>
/// One tab's content: the banners, the three splitter panes and the collapsible problems panel.
/// The code here keeps the splitter sizes in the saved layout and collapses the problems row
/// without losing the height the user chose.
///
/// Sizes are written the moment a splitter is let go, whichever tab it was, rather than only on
/// close: with several tabs open the one that shuts down last is not the one whose panes the user
/// just resized.
/// </summary>
public partial class DocumentView : UserControl
{
    /// <summary>The most of the left column the texture settings may take before they scroll.</summary>
    private const double SettingsCapShare = 0.55;

    /// <summary>The frames list is never worth less than this; below it the list stops being usable.</summary>
    private const double FramesMinHeight = 220;

    /// <summary>
    /// The least the settings pane keeps: what it is given once the user has sized it by hand, and
    /// what it holds on to on a window too short for both minimums, so it still reads as a form
    /// that scrolls rather than as a sliver.
    /// </summary>
    private const double SettingsMinHeight = 96;

    private GridLength _problemsHeight = new(170);
    private AtxWorkspace? _shell;

    /// <summary>
    /// True once the settings/frames split is the user's own: either they dragged the splitter, or
    /// a share from an earlier run was restored. Until then the settings pane sizes to its content.
    /// </summary>
    private bool _settingsShareIsUser;

    public DocumentView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => SaveLayout();
        LeftPane.SizeChanged += (_, _) => UpdateSettingsBounds();

        foreach (var splitter in new[]
                 { LeftSplitter, MiddleSplitter, SettingsSplitter, PreviewSplitter, ProblemsSplitter })
        {
            splitter.DragCompleted += OnSplitterDragCompleted;
        }
        SettingsSplitter.DragStarted += OnSettingsSplitterDragStarted;
        SettingsSplitter.PreviewKeyDown += OnSettingsSplitterKeyDown;
    }

    /// <summary>The source pane, so the window can route Ctrl+F and focus commands to it.</summary>
    public SourceEditorView SourceEditor => Source;

    /// <summary>
    /// Turns the content-sized settings row into a star share the moment the user starts resizing
    /// it. A splitter cannot move a row that is sized to its content and capped, and the gesture is
    /// the signal that the user wants the split to be theirs rather than the pane's own idea of the
    /// right height.
    /// </summary>
    private void BeginUserResize()
    {
        double settings = SettingsRow.ActualHeight;
        double frames = FramesRow.ActualHeight;
        if (settings > 0 && frames > 0)
        {
            SettingsPane.MaxHeight = double.PositiveInfinity;
            SettingsRow.Height = new GridLength(settings, GridUnitType.Star);
            FramesRow.Height = new GridLength(frames, GridUnitType.Star);
        }
        _settingsShareIsUser = true;
        UpdateSettingsBounds();
    }

    private void OnSettingsSplitterDragStarted(object sender, DragStartedEventArgs e) => BeginUserResize();

    /// <summary>
    /// A splitter also resizes from the keyboard, and that path raises no drag events — without
    /// this, a keyboard user's chosen split would be forgotten the moment the app restarted.
    /// </summary>
    private void OnSettingsSplitterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Up or Key.Down or Key.PageUp or Key.PageDown)) return;
        BeginUserResize();
        // The splitter moves the rows as it handles the key; record the result after that.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            SaveLayout();
            _shell?.SaveSettings();
        }));
    }

    private void OnSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        SaveLayout();
        // A splitter drag is a deliberate choice; persist it now rather than hoping this tab is the
        // one that closes last.
        _shell?.SaveSettings();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachShell();
        _shell = (e.NewValue as DocumentViewModel)?.Shell;
        if (_shell is not null)
        {
            _shell.PropertyChanged += OnShellPropertyChanged;
            RestoreLayout();
            ApplyProblemsVisibility();
            ApplySettingsVisibility();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Switching tabs unloads this view and loads it again without DataContextChanged firing,
        // so the shell has to be re-resolved here — otherwise the Problems panel springs back open,
        // Ctrl+Shift+M stops working for this tab, and splitter drags stop being saved.
        if (_shell is null && (DataContext as DocumentViewModel)?.Shell is { } shell)
        {
            _shell = shell;
            _shell.PropertyChanged += OnShellPropertyChanged;
        }
        RestoreLayout();
        ApplyProblemsVisibility();
        ApplySettingsVisibility();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        SaveLayout();
        DetachShell();
    }

    private void DetachShell()
    {
        if (_shell is not null) _shell.PropertyChanged -= OnShellPropertyChanged;
        _shell = null;
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AtxWorkspace.IsProblemsVisible)) ApplyProblemsVisibility();
        if (e.PropertyName == nameof(AtxWorkspace.IsSettingsExpanded)) ApplySettingsVisibility();
    }

    private void ApplyProblemsVisibility()
    {
        bool visible = _shell?.IsProblemsVisible ?? true;
        if (!visible && ProblemsRow.Height.IsAbsolute && ProblemsRow.Height.Value > 0)
            _problemsHeight = ProblemsRow.Height;

        ProblemsPane.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ProblemsSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ProblemsRow.MinHeight = visible ? 90 : 0;
        ProblemsRow.Height = visible ? _problemsHeight : new GridLength(0);
    }

    /// <summary>
    /// Shows or hides the texture settings form. Collapsed, the pane is just its header strip and
    /// the frames list has the whole column — what a designer working through a long list wants.
    /// </summary>
    private void ApplySettingsVisibility()
    {
        bool expanded = _shell?.IsSettingsExpanded ?? true;
        SettingsPane.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        SettingsSplitter.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;

        if (!expanded)
        {
            SettingsRow.MinHeight = 0;
            SettingsRow.Height = new GridLength(0);
            FramesRow.Height = new GridLength(1, GridUnitType.Star);
        }
        else
        {
            ApplySettingsSizing();
        }
        UpdateSettingsBounds();
    }

    /// <summary>
    /// Puts the settings row into the mode it belongs in: sized to its content until the user has
    /// chosen a split of their own, star-sized to that share afterwards.
    /// </summary>
    private void ApplySettingsSizing()
    {
        if (_settingsShareIsUser && _shell is not null)
        {
            double share = _shell.LayoutShare(AtxWorkspace.LayoutSettingsShare, 0.42);
            SettingsPane.MaxHeight = double.PositiveInfinity;
            SettingsRow.Height = new GridLength(share, GridUnitType.Star);
            FramesRow.Height = new GridLength(1 - share, GridUnitType.Star);
        }
        else
        {
            SettingsRow.MinHeight = 0;
            SettingsRow.Height = GridLength.Auto;
            FramesRow.Height = new GridLength(1, GridUnitType.Star);
        }
    }

    /// <summary>
    /// Keeps the two rows' limits in step with the column's height. Both minimums together can
    /// never exceed what there is to share, or the grid would overflow its pane and the frames list
    /// would be the thing cut off — so the frames minimum yields first, and only down to a floor.
    /// </summary>
    private void UpdateSettingsBounds()
    {
        // Measured from the pane border, not from the grid inside it: a Grid whose rows do not fit
        // reports the height its rows wanted, which would feed this calculation its own overflow.
        double available = LeftPane.ActualHeight
                           - LeftPane.BorderThickness.Top - LeftPane.BorderThickness.Bottom
                           - SettingsHeader.ActualHeight;
        if (SettingsSplitter.Visibility == Visibility.Visible)
            available -= SettingsSplitter.Height;
        if (available <= 0) return;

        // On a short window both minimums cannot be met; the frames list keeps its own until doing
        // so would leave the settings pane with less than a couple of rows.
        double framesMin = Math.Min(FramesMinHeight, Math.Max(SettingsMinHeight, available - SettingsMinHeight));
        FramesRow.MinHeight = framesMin;

        if (_shell?.IsSettingsExpanded == false) return;

        if (_settingsShareIsUser)
        {
            SettingsRow.MinHeight = Math.Min(SettingsMinHeight, Math.Max(0, available - framesMin));
        }
        else
        {
            // Auto up to the cap: whichever is smaller, just over half the column or whatever is
            // left once the frames list has its minimum. The cap goes on the pane rather than on
            // the row, because a row measures an Auto child against infinity and then clips what
            // does not fit — the pane would be cut off silently instead of scrolling.
            SettingsPane.MaxHeight =
                Math.Max(SettingsMinHeight, Math.Min(available * SettingsCapShare, available - framesMin));
        }
    }

    private void RestoreLayout()
    {
        if (_shell is null) return;
        LeftColumn.Width = new GridLength(_shell.LayoutSize(AtxWorkspace.LayoutLeftPane, 360));
        MiddleColumn.Width = new GridLength(_shell.LayoutSize(AtxWorkspace.LayoutMiddlePane, 340));
        _problemsHeight = new GridLength(_shell.LayoutSize(AtxWorkspace.LayoutProblemsHeight, 170));
        if (_shell.IsProblemsVisible) ProblemsRow.Height = _problemsHeight;

        // The settings/frames split is stored as a share of the left pane rather than a height, so
        // it survives a window that is a different size from the one it was saved on. No stored
        // share means the user has never sized this pane, and the pane sizes itself instead.
        _settingsShareIsUser = _shell.HasLayoutShare(AtxWorkspace.LayoutSettingsShare);
        ApplySettingsSizing();
        UpdateSettingsBounds();
    }

    private void SaveLayout()
    {
        if (_shell is null || !IsLoaded) return;
        _shell.SetLayoutSize(AtxWorkspace.LayoutLeftPane, LeftColumn.ActualWidth);
        _shell.SetLayoutSize(AtxWorkspace.LayoutMiddlePane, MiddleColumn.ActualWidth);

        // Only a split the user chose is worth remembering; writing the content-sized one back
        // would turn the Auto default into a fixed share after the first run.
        double total = SettingsRow.ActualHeight + FramesRow.ActualHeight;
        if (_settingsShareIsUser && _shell.IsSettingsExpanded && total > 1)
        {
            _shell.SetLayoutShare(AtxWorkspace.LayoutSettingsShare, SettingsRow.ActualHeight / total);
        }
        if (_shell.IsProblemsVisible && ProblemsPane.ActualHeight > 0)
            _shell.SetLayoutSize(AtxWorkspace.LayoutProblemsHeight, ProblemsRow.ActualHeight);
    }

    /// <summary>Writes the current splitter sizes into settings; the window calls this on close.</summary>
    public void PersistLayout() => SaveLayout();
}
