using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Cairn.Assets;
using Cairn.Ui.Modules;
using Cairn.Ui.Mvvm;
using Cairn.Vfx.Formats;

namespace Cairn.Vfx.Ui.Library;

/// <summary>One .vfx visible to the asset host.</summary>
public sealed record VfxLibraryEntry(AssetLocation Location, string Name, string Source, string Version, int Frames, int Objects, string UsedBy, bool Shadowed)
{
    public string Line1 => Shadowed ? $"{Name}  (unused copy)" : Name;
    public string Line2 => $"{Source}{(UsedBy.Length > 0 ? " · used by " + UsedBy : "")} · {Count(Frames, "frame")} · {Count(Objects, "object")} · version {Version}{(Shadowed ? " · another copy loads first" : "")}";
    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}

/// <summary>Left "Effects" panel: every .vfx in loose folders and archives, listed off the UI thread, with a filter box.</summary>
public sealed class VfxLibraryPanel : DockPanel
{
    private readonly IShellContext _shell;
    private readonly TextBox _filter = new() { Margin = new Thickness(4), ToolTip = "Filter by name, source or table" };
    private readonly ListBox _list = new();
    private readonly TextBlock _status = new() { Margin = new Thickness(6, 2, 6, 4), TextWrapping = TextWrapping.Wrap };
    private int _generation;

    public IReadOnlyList<VfxLibraryEntry> Entries { get; private set; } = [];
    public event EventHandler? Loaded2;

    public VfxLibraryPanel(IShellContext shell)
    {
        _shell = shell;
        AutomationProperties.SetName(_filter, "Filter effects");
        AutomationProperties.SetName(_list, "Effects");
        _status.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        _list.ItemTemplate = BuildTemplate();
        _list.MouseDoubleClick += (_, _) => Open();
        _list.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Open(); e.Handled = true; } };
        _list.ContextMenu = new ContextMenu
        {
            Items =
            {
                new MenuItem { Header = "_Open", Command = new RelayCommand(Open) },
                new MenuItem { Header = "_Extract to folder...", Command = new RelayCommand(Extract) },
                new MenuItem { Header = "_Copy name", Command = new RelayCommand(() => { if (Selected is { } s) Clipboard.SetText(s.Name); }) },
            },
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.BorderThickness = new Thickness(0);
        _list.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "PaneListBoxItem");
        _list.SetResourceReference(BackgroundProperty, "App.PaneBackground");
        var placeholder = new TextBlock { Text = "Filter by name, source or table", IsHitTestVisible = false, Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        placeholder.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        _filter.TextChanged += (_, _) => { placeholder.Visibility = _filter.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; ApplyFilter(); };
        var filterHost = new Grid();
        filterHost.Children.Add(_filter); filterHost.Children.Add(placeholder);
        DockPanel.SetDock(filterHost, Dock.Top); DockPanel.SetDock(_status, Dock.Bottom);
        Children.Add(filterHost); Children.Add(_status); Children.Add(_list);
        shell.Assets.Changed += (_, _) => Dispatcher.InvokeAsync(Reload);
        Reload();
    }

    private VfxLibraryEntry? Selected => _list.SelectedItem as VfxLibraryEntry;

    private static DataTemplate BuildTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 1, 0, 2));
        panel.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding(nameof(VfxLibraryEntry.Line2)));
        var a = new FrameworkElementFactory(typeof(TextBlock));
        a.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(VfxLibraryEntry.Line1)));
        a.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        a.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        var b = new FrameworkElementFactory(typeof(TextBlock));
        b.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(VfxLibraryEntry.Line2)));
        b.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        b.SetValue(TextBlock.FontSizeProperty, 11.0);
        b.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        panel.AppendChild(a); panel.AppendChild(b);
        return new DataTemplate { VisualTree = panel };
    }

    public async void Reload()
    {
        int generation = ++_generation;
        _status.Text = "Listing effects...";
        var resolver = _shell.Assets.Resolver;
        IReadOnlyList<VfxLibraryEntry> entries;
        // screenshots wait for the listing (and the table usage it gathers) to finish
        using var busy = Cairn.Ui.Services.BusyTracker.Begin("effects library listing");
        try { entries = await Task.Run(() => Scan(resolver)); }
        catch (Exception ex) { _status.Text = "Could not list effects: " + ex.Message; return; }
        if (generation != _generation) return;
        Entries = entries;
        ApplyFilter();
        Loaded2?.Invoke(this, EventArgs.Empty);
    }

    internal static IReadOnlyList<VfxLibraryEntry> Scan(AssetResolver resolver)
    {
        string? ReadTable(string name)
        {
            try { return resolver.Resolve(name) is { } loc ? Encoding.Latin1.GetString(loc.ReadAllBytes()) : null; } catch { return null; }
        }
        var refs = VfxTableUsage.Find(ReadTable);
        var winners = resolver.Enumerate([".vfx"]).ToHashSet();
        var list = new List<VfxLibraryEntry>();
        foreach (var loc in resolver.EnumerateAll([".vfx"]))
        {
            string version = "?"; int frames = 0, objects = 0;
            try
            {
                var file = VfxReader.Read(loc.ReadAllBytes(), loc.ResolvedName);
                version = $"0x{file.Version:X}"; frames = file.EndFrame + 1;
                objects = file.Sections.Count(s => s is not VfxMaterial);
            }
            catch { version = "unreadable"; }
            var used = VfxTableUsage.ReferencesTo(refs, loc.ResolvedName).Select(r => r.Table).Distinct(StringComparer.OrdinalIgnoreCase);
            list.Add(new(loc, loc.ResolvedName, loc.DisplayLocation, version, frames, objects, string.Join(", ", used), !winners.Contains(loc)));
        }
        return [.. list.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Shadowed)];
    }

    private void ApplyFilter()
    {
        var f = _filter.Text.Trim();
        var shown = Entries.Where(e => f.Length == 0 || e.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || e.Line2.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        _list.ItemsSource = shown;
        _status.Text = Entries.Count == 0 ? "No effects found: set the game folder in Settings." : $"{shown.Count} of {Entries.Count} effects";
    }

    private void Open() { if (Selected is { } s) _shell.OpenLocation(s.Location); }

    private void Extract()
    {
        if (Selected is not { } s || _shell.Dialogs.PickFolder(null, "Extract effect to folder") is not { } folder) return;
        try { File.WriteAllBytes(Path.Combine(folder, s.Name), s.Location.ReadAllBytes()); _shell.ShowStatus($"Extracted {s.Name}"); }
        catch (Exception ex) { _shell.Dialogs.ShowError("Extract failed", ex.Message); }
    }
}
