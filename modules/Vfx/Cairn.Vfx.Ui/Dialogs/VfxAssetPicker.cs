using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Cairn.Assets;
using Cairn.Ui.Modules;

namespace Cairn.Vfx.Ui.Dialogs;

/// <summary>A file picked from the game's archives and folders or from disk.</summary>
public sealed record VfxPickedAsset(byte[] Bytes, string Name, string Source);

/// <summary>
/// Searchable list of every file with the given extensions that the asset host sees (archives and loose
/// folders, shadowed copies included), with "Browse disk..." for anything else.
/// </summary>
public static class VfxAssetPicker
{
    public sealed record Item(AssetLocation Location, string Name, string Source)
    {
        public override string ToString() => $"{Name}    {Source}";
    }

    /// <summary>Every candidate the asset host sees, sorted by name.</summary>
    public static IReadOnlyList<Item> List(AssetResolver resolver, string[] extensions)
    {
        try
        {
            return [.. resolver.EnumerateAll(extensions).Select(l => new Item(l, l.ResolvedName, l.DisplayLocation))
                .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)];
        }
        catch { return []; }
    }

    /// <summary>Builds the dialog; <paramref name="result"/> reads the choice after OK (null when nothing is chosen).</summary>
    public static VfxForm Build(IShellContext shell, string title, string fileFilter, string[] extensions, out Func<VfxPickedAsset?> result)
    {
        var form = new VfxForm(title, shell.MainWindow, "_Choose") { Width = 640 };
        var all = List(shell.Assets.Resolver, extensions);
        var filter = form.Text("_Search:", "", "Type part of a file name or archive name");
        var list = form.Row("_Files:", new ListBox { Height = 260 }, "Files in the game folder, its archives and the mod folder");
        VfxPickedAsset? fromDisk = null;
        var browse = form.Row("Or:", new Button { Content = new AccessText { Text = "_Browse disk..." }, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 2, 10, 2) },
            "Choose a file anywhere on disk");
        browse.Click += (_, _) =>
        {
            if (shell.Dialogs.OpenFiles(null, title, fileFilter + "|All files (*.*)|*.*", false).FirstOrDefault() is not { } path) return;
            try { fromDisk = new(File.ReadAllBytes(path), Path.GetFileName(path), path); form.DialogResult = true; }
            catch (Exception ex) { shell.Dialogs.ShowError("Cannot read file", ex.Message); }
        };
        void Apply()
        {
            var f = filter.Text.Trim();
            var shown = all.Where(i => f.Length == 0 || i.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || i.Source.Contains(f, StringComparison.OrdinalIgnoreCase)).Take(2000).ToList();
            list.ItemsSource = shown;
            if (shown.Count > 0 && list.SelectedIndex < 0) list.SelectedIndex = 0;
        }
        filter.TextChanged += (_, _) => Apply();
        filter.PreviewKeyDown += (_, e) => { if (e.Key == Key.Down) { list.Focus(); e.Handled = true; } };
        list.SelectionChanged += (_, _) => form.Refresh();
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem is not null) form.DialogResult = true; };
        form.Changed += (_, _) =>
        {
            form.CanAccept = list.SelectedItem is Item;
            form.Summary = list.SelectedItem is Item i ? $"{i.Name}\nfrom {i.Source}"
                : all.Count == 0 ? "No matching files in the game folder: set it in Settings, or browse the disk." : "Choose a file from the list.";
        };
        Apply();
        result = () =>
        {
            if (fromDisk is not null) return fromDisk;
            if (list.SelectedItem is not Item i) return null;
            return new(i.Location.ReadAllBytes(), i.Name, i.Source);
        };
        return form;
    }

    /// <summary>Shows the picker; null when cancelled or the file cannot be read.</summary>
    public static VfxPickedAsset? Pick(IShellContext shell, string title, string fileFilter, string[] extensions)
    {
        var form = Build(shell, title, fileFilter, extensions, out var result);
        if (form.ShowDialog() != true) return null;
        try { return result(); }
        catch (Exception ex) { shell.Dialogs.ShowError("Cannot read file", ex.Message); return null; }
    }
}
