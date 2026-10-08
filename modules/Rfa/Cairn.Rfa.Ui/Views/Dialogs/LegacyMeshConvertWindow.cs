using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Cairn.Rfa.Ui.Views.Dialogs;

/// <summary>Where converted meshes go.</summary>
public enum LegacyConvertTarget
{
    /// <summary>Into the packfile the meshes came from, as new entries (one undo step).</summary>
    IntoPackfile,
    /// <summary>Into the source's own folder (a packfile entry: the packfile's folder).</summary>
    NextToSource,
    /// <summary>Into a chosen folder.</summary>
    Folder,
}

/// <summary>What the Convert window settled on.</summary>
/// <param name="Target">Where the files go.</param>
/// <param name="Folder">The folder for <see cref="LegacyConvertTarget.Folder"/> and <see cref="LegacyConvertTarget.NextToSource"/>.</param>
/// <param name="Replace">True to replace files or entries of the same name.</param>
public sealed record LegacyConvertChoice(LegacyConvertTarget Target, string? Folder, bool Replace);

/// <summary>One mesh the window lists: its name, what it becomes and what converting it approximates.</summary>
/// <param name="SourceName">The legacy file's name.</param>
/// <param name="OutputName">The .v3m/.v3c it becomes.</param>
/// <param name="Report">What the conversion approximates (empty: nothing), or null when not known before converting.</param>
/// <param name="Error">Why it cannot be converted, or null.</param>
public sealed record LegacyConvertItem(string SourceName, string OutputName, IReadOnlyList<string>? Report, string? Error = null);

/// <summary>
/// "Convert to .v3m/.v3c": where the converted meshes go (into the packfile, next to the source, or a folder), what
/// happens to taken names, the names that result, and for one mesh what the conversion approximates.
/// </summary>
public sealed class LegacyMeshConvertWindow : Window
{
    private readonly IDialogService _dialogs;
    private readonly IReadOnlyList<LegacyConvertItem> _items;
    private readonly string? _packfile, _nextTo;
    private readonly RadioButton _intoPackfile = new() { GroupName = "meshTarget", Margin = new Thickness(0, 2, 0, 2) };
    private readonly RadioButton _nextToSource = new() { GroupName = "meshTarget", Margin = new Thickness(0, 2, 0, 2) };
    private readonly RadioButton _toFolder = new() { Content = new TextBlock { Text = "Folder:" }, GroupName = "meshTarget", Margin = new Thickness(0, 2, 8, 2), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _folder = new() { MinWidth = 300 };
    private readonly CheckBox _replace = new() { Content = "_Replace files or entries of the same name (else a free name is chosen)", Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _plan = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Button _convert = new() { Content = "_Convert", IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Convert with these choices" };

    /// <param name="dialogs">For the folder picker.</param>
    /// <param name="items">The meshes.</param>
    /// <param name="packfile">The packfile the meshes are entries of, or null.</param>
    /// <param name="nextTo">The source's folder (or its packfile's), or null when it has none.</param>
    /// <param name="defaults">The choice to start from.</param>
    /// <param name="noNextToReason">Why "next to the source" is off when <paramref name="nextTo"/> is null ("it has no folder").</param>
    public LegacyMeshConvertWindow(IDialogService dialogs, IReadOnlyList<LegacyConvertItem> items, string? packfile, string? nextTo, LegacyConvertChoice defaults,
        string? noNextToReason = null)
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(defaults);
        _dialogs = dialogs;
        _items = items;
        _packfile = packfile;
        _nextTo = nextTo;
        Title = items.Count == 1 ? $"Convert {items[0].SourceName} to {Path.GetExtension(items[0].OutputName)}" : string.Format(CultureInfo.CurrentCulture, "Convert {0:N0} meshes to .v3m/.v3c", items.Count);
        Width = 640; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "App.WindowBackground");
        SetResourceReference(ForegroundProperty, "App.Text");

        // plain text blocks: names and folders may hold underscores (no access keys there)
        _intoPackfile.Content = new TextBlock { Text = packfile is null ? "Into the packfile (only for meshes opened from a packfile)" : $"Into {packfile} as new entries (one undo step)" };
        _intoPackfile.IsEnabled = packfile is not null;
        _nextToSource.Content = new TextBlock { Text = nextTo is null ? $"Next to the source ({noNextToReason ?? "it has no folder"})" :$"Next to the source ({nextTo})", TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 580 };
        _nextToSource.IsEnabled = nextTo is not null;
        _folder.Text = defaults.Folder is { Length: > 0 } f ? f : nextTo ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        _replace.IsChecked = defaults.Replace;
        var target = defaults.Target;
        if (target == LegacyConvertTarget.IntoPackfile && packfile is null) target = nextTo is null ? LegacyConvertTarget.Folder : LegacyConvertTarget.NextToSource;
        if (target == LegacyConvertTarget.NextToSource && nextTo is null) target = LegacyConvertTarget.Folder;
        (target switch { LegacyConvertTarget.IntoPackfile => _intoPackfile, LegacyConvertTarget.NextToSource => _nextToSource, _ => _toFolder }).IsChecked = true;
        AutomationProperties.SetName(_folder, "Folder");
        _replace.ToolTip = "Off: a converted mesh whose name is taken gets a free one (name (2).v3m)";
        Content = Build();
        _folder.TextChanged += (_, _) => { _toFolder.IsChecked = true; Update(); };
        foreach (var radio in new[] { _intoPackfile, _nextToSource, _toFolder }) radio.Checked += (_, _) => Update();
        _replace.Click += (_, _) => Update();
        Update();
    }

    /// <summary>The choice made with Convert, or null.</summary>
    public LegacyConvertChoice? Result { get; private set; }

    /// <summary>The plan line (for self-tests and captures).</summary>
    public string Plan => _plan.Text;

    /// <summary>The choice the window describes now (also for tests), or null when the folder is not usable.</summary>
    public LegacyConvertChoice? Current()
    {
        bool replace = _replace.IsChecked == true;
        if (_intoPackfile.IsChecked == true && _packfile is not null) return new LegacyConvertChoice(LegacyConvertTarget.IntoPackfile, null, replace);
        if (_nextToSource.IsChecked == true && _nextTo is not null) return new LegacyConvertChoice(LegacyConvertTarget.NextToSource, _nextTo, replace);
        string folder = _folder.Text.Trim();
        if (folder.Length == 0) return null;
        try { folder = Path.GetFullPath(folder); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        return new LegacyConvertChoice(LegacyConvertTarget.Folder, folder, replace);
    }

    private UIElement Build()
    {
        var panel = new StackPanel { Margin = new Thickness(14) };
        void Label(string text)
        {
            var t = new TextBlock { Text = text, Margin = new Thickness(0, panel.Children.Count == 0 ? 0 : 12, 0, 4), FontWeight = FontWeights.SemiBold };
            t.SetResourceReference(TextBlock.ForegroundProperty, "App.Text");
            panel.Children.Add(t);
        }
        TextBlock Hint(string text)
        {
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 2, 0, 2) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
            return t;
        }
        var intro = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "Cairn reads these meshes but does not save them. Converting makes the .v3m (static) or .v3c (character) the PC game loads, "
                + "the way the game's own mesh compiler did; the source is left as it is.",
        };
        intro.SetResourceReference(TextBlock.ForegroundProperty, "App.Text");
        panel.Children.Add(intro);
        Label("Where");
        panel.Children.Add(_intoPackfile);
        panel.Children.Add(_nextToSource);
        var browse = new Button { Content = "_Browse...", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2), ToolTip = "Pick the folder" };
        browse.SetResourceReference(StyleProperty, "PushButton");
        browse.Click += (_, _) => { if (_dialogs.PickFolder(_folder.Text, "Convert meshes into") is { } picked) _folder.Text = picked; };
        var folderRow = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        DockPanel.SetDock(_toFolder, Dock.Left);
        DockPanel.SetDock(browse, Dock.Right);
        folderRow.Children.Add(_toFolder);
        folderRow.Children.Add(browse);
        folderRow.Children.Add(_folder);
        panel.Children.Add(folderRow);
        panel.Children.Add(_replace);
        _plan.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        panel.Children.Add(_plan);

        var failed = _items.Where(i => i.Error is not null).ToList();
        if (failed.Count > 0)
        {
            Label("Cannot be converted");
            foreach (var item in failed.Take(6)) panel.Children.Add(Hint($"• {item.SourceName}: {item.Error}"));
            if (failed.Count > 6) panel.Children.Add(Hint(string.Format(CultureInfo.CurrentCulture, "... and {0:N0} more", failed.Count - 6)));
        }
        if (_items.Count == 1 && _items[0].Report is { } report && _items[0].Error is null)
        {
            Label("What the conversion changes");
            if (report.Count == 0) panel.Children.Add(Hint("Nothing: the converted mesh holds exactly what this one does."));
            foreach (var note in report.Take(12)) panel.Children.Add(Hint("• " + note));
            if (report.Count > 12) panel.Children.Add(Hint(string.Format(CultureInfo.CurrentCulture, "... and {0:N0} more", report.Count - 12)));
        }
        else if (_items.Count > 1)
        {
            panel.Children.Add(Hint("What each conversion approximates is listed in the report when it is done."));
        }

        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, ToolTip = "Close without converting" };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        foreach (var b in new[] { _convert, cancel }) { b.SetResourceReference(StyleProperty, "DialogButton"); buttons.Children.Add(b); }
        AutomationProperties.SetName(_convert, "Convert");
        _convert.Click += (_, _) => { Result = Current(); if (Result is not null) DialogResult = true; };
        panel.Children.Add(buttons);
        return panel;
    }

    private void Update()
    {
        var choice = Current();
        var outputs = _items.Where(i => i.Error is null).Select(i => i.OutputName).ToList();
        _convert.IsEnabled = choice is not null && outputs.Count > 0;
        if (choice is null) { _plan.Text = "Choose a folder."; return; }
        if (outputs.Count == 0) { _plan.Text = "None of the meshes can be converted."; return; }
        string where = choice.Target == LegacyConvertTarget.IntoPackfile ? "into " + _packfile : "to " + choice.Folder;
        _plan.Text = outputs.Count == 1
            ? $"Writes {outputs[0]} {where}."
            : string.Format(CultureInfo.CurrentCulture, "Writes {0:N0} files {1}: {2} ... {3}.", outputs.Count, where, outputs[0], outputs[^1]);
    }
}
