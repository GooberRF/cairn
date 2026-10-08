using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cairn.Ui.Services;

namespace Cairn.Vbm.Ui.Dialogs;

/// <summary>What the export window settled on.</summary>
/// <param name="Folder">Where the images go.</param>
/// <param name="BaseName">The stem of each name ("glow" gives glow_00.tga...).</param>
/// <param name="Format">TGA or PNG.</param>
/// <param name="Frames">The frames to write (0-based).</param>
public sealed record VbmExportRequest(string Folder, string BaseName, VbmExportFormat Format, IReadOnlyList<int> Frames);

/// <summary>"Export frames": folder, file name stem, TGA or PNG, every frame or the selection, and the names that result.</summary>
public sealed class VbmExportWindow : Window
{
    private readonly IDialogService _dialogs;
    private readonly int _frameCount;
    private readonly IReadOnlyList<int> _selected;
    private readonly TextBox _folder = new() { MinWidth = 300 }, _name = new() { MinWidth = 200 };
    private readonly RadioButton _tga = new() { Content = "TGA", GroupName = "format", Margin = new Thickness(0, 0, 16, 0) },
        _png = new() { Content = "PNG", GroupName = "format" },
        _all = new() { GroupName = "scope", Margin = new Thickness(0, 0, 16, 0) },
        _selection = new() { GroupName = "scope" };
    private readonly TextBlock _names = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Button _export = new() { Content = "_Export", IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Write the images" };

    /// <param name="dialogs">For the folder picker.</param>
    /// <param name="fileName">The bitmap's name (the default stem).</param>
    /// <param name="frameCount">Frames in the bitmap.</param>
    /// <param name="selected">The selected frames.</param>
    /// <param name="folder">The folder offered.</param>
    /// <param name="format">The format offered.</param>
    public VbmExportWindow(IDialogService dialogs, string fileName, int frameCount, IReadOnlyList<int> selected, string folder, VbmExportFormat format)
    {
        _dialogs = dialogs;
        _frameCount = frameCount;
        _selected = selected;
        Title = "Export frames";
        Width = 560; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "App.WindowBackground");
        SetResourceReference(ForegroundProperty, "App.Text");

        _folder.Text = folder;
        _name.Text = VbmFrameExport.DefaultBaseName(fileName);
        (format == VbmExportFormat.Png ? _png : _tga).IsChecked = true;
        _all.Content = string.Format(CultureInfo.CurrentCulture, "All {0} frames", frameCount);
        _selection.Content = selected.Count == 1
            ? string.Format(CultureInfo.CurrentCulture, "Selected frame ({0})", selected[0] + 1)
            : string.Format(CultureInfo.CurrentCulture, "Selected frames ({0})", selected.Count);
        _selection.IsEnabled = selected.Count < frameCount;
        (selected.Count > 1 && selected.Count < frameCount ? _selection : _all).IsChecked = true;
        AutomationProperties.SetName(_folder, "Folder");
        AutomationProperties.SetName(_name, "File name");
        _tga.ToolTip = "Targa images, the format the game's own frames use (32-bit when the bitmap has alpha)";
        _png.ToolTip = "PNG images, with alpha";
        Content = Build();
        foreach (var box in new[] { _folder, _name }) box.TextChanged += (_, _) => Update();
        foreach (var radio in new[] { _tga, _png, _all, _selection }) radio.Checked += (_, _) => Update();
        Update();
        Loaded += (_, _) => { _name.Focus(); _name.SelectAll(); };
    }

    /// <summary>The settings chosen with Export, or null.</summary>
    public VbmExportRequest? Result { get; private set; }

    /// <summary>The request the window describes now (also for tests).</summary>
    public VbmExportRequest? Current()
    {
        string folder = _folder.Text.Trim(), name = _name.Text.Trim();
        if (folder.Length == 0 || name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        try { folder = Path.GetFullPath(folder); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        IReadOnlyList<int> frames = _selection.IsChecked == true ? _selected : [.. Enumerable.Range(0, _frameCount)];
        return new VbmExportRequest(folder, name, _png.IsChecked == true ? VbmExportFormat.Png : VbmExportFormat.Tga, frames);
    }

    private UIElement Build()
    {
        var grid = new Grid { Margin = new Thickness(14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var browse = new Button { Content = "_Browse...", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2), ToolTip = "Pick the folder" };
        browse.SetResourceReference(StyleProperty, "PushButton");
        browse.Click += (_, _) =>
        {
            if (_dialogs.PickFolder(_folder.Text, "Export frames to") is { } picked) _folder.Text = picked;
        };
        var folderRow = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        folderRow.Children.Add(browse);
        folderRow.Children.Add(_folder);

        int row = 0;
        void Add(string label, UIElement content)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 5, 12, 5) };
            text.SetResourceReference(StyleProperty, "DialogLabel");
            Grid.SetRow(text, row);
            Grid.SetRow(content, row);
            Grid.SetColumn(content, 1);
            if (content is FrameworkElement fe) fe.Margin = new Thickness(0, 4, 0, 4);
            grid.Children.Add(text);
            grid.Children.Add(content);
            row++;
        }
        Add("Folder", folderRow);
        Add("File name", _name);
        Add("Format", new StackPanel { Orientation = Orientation.Horizontal, Children = { _tga, _png } });
        Add("Frames", new StackPanel { Orientation = Orientation.Horizontal, Children = { _all, _selection } });

        _names.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_names, row++);
        Grid.SetColumnSpan(_names, 2);
        grid.Children.Add(_names);

        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, ToolTip = "Close without exporting" };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        foreach (var b in new[] { _export, cancel }) { b.SetResourceReference(StyleProperty, "DialogButton"); buttons.Children.Add(b); }
        _export.Click += (_, _) => { Result = Current(); if (Result is not null) DialogResult = true; };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(buttons, row);
        Grid.SetColumnSpan(buttons, 2);
        grid.Children.Add(buttons);
        return grid;
    }

    private void Update()
    {
        var request = Current();
        _export.IsEnabled = request is not null;
        if (request is null)
        {
            _names.Text = "Choose a folder and a file name (without characters such as \\ / : * ? \" < > |).";
            return;
        }
        var plan = VbmFrameExport.Plan(request.Folder, request.BaseName, request.Frames, _frameCount, request.Format);
        string first = Path.GetFileName(plan[0].Path), last = Path.GetFileName(plan[^1].Path);
        int existing = VbmFrameExport.Existing(plan).Count;
        _names.Text = (plan.Count == 1
            ? string.Format(CultureInfo.CurrentCulture, "Writes {0}.", first)
            : string.Format(CultureInfo.CurrentCulture, "Writes {0} images: {1} ... {2}.", plan.Count, first, last))
            + (existing > 0 ? string.Format(CultureInfo.CurrentCulture, " {0} of them already exist{1} and will be replaced if you confirm.", existing, existing == 1 ? "s" : "") : "");
    }
}
