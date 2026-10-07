using System.Windows.Controls;
using System.Windows.Input;
using Cairn.Ui.Modules;
using Cairn.Vpp.Model;
using Cairn.Vpp.Ui.Conversion;
using Cairn.Vpp.Ui.Documents;

namespace Cairn.Vpp.Ui;

// The DDS converter's menu item, shortcut and the run that follows the dialog.
public sealed partial class VppModule
{
    /// <summary>The module's settings store (the converter keeps its choices under <c>vpp.dds.</c>).</summary>
    internal ModuleSettings? ConvertSettingsStore => _store;

    /// <summary>Adds "Convert images to DDS..." to the Packfile menu and its shortcut (Ctrl+Shift+D).</summary>
    private void InitializeConvert()
    {
        var command = Cmd(d => d.Commands.Fire(() => ConvertToDdsAsync(d)), d => d.Current.Count > 0);
        var item = new MenuItem
        {
            Header = "Con_vert images to DDS...", InputGestureText = "Ctrl+Shift+D", Command = command,
            ToolTip = "Convert the selected TGA/PNG/JPG images (or all of them when none is selected) to DDS for Alpine Faction",
        };
        if (_menus.FirstOrDefault(m => m.Slot == MenuSlot.TopLevel && m.Item is MenuItem { Name: "PackfileMenu" })?.Item is MenuItem menu)
        {
            // Before the separator that precedes Validate.
            int at = menu.Items.Count - 2;
            menu.Items.Insert(Math.Max(at, 0), item);
        }
        _shortcuts.Add(new("Packfile", "Convert images to DDS", Key.D, ModifierKeys.Control | ModifierKeys.Shift, command, IsPackfile));
    }

    /// <summary>
    /// Opens the converter for the document's selected images (every image when the selection has none),
    /// then converts with the chosen settings. Non-images in the selection are listed as skipped.
    /// </summary>
    public async Task ConvertToDdsAsync(VppDocument doc)
    {
        var selection = doc.SelectedItems.Count > 0 ? doc.SelectedItems : doc.Current.Items.ToList();
        var (images, skipped) = DdsConversion.Split(selection);
        if (images.Count == 0)
        {
            doc.Shell.Dialogs.ShowError("Nothing to convert", "The selection has no TGA, PNG or JPG images.");
            return;
        }
        var window = VppDdsConvertWindow.Create(doc, images, skipped);
        if (window.ShowDialog() != true || window.Chosen is not { } settings) return;
        await RunConversionAsync(doc, images, settings);
    }

    /// <summary>
    /// Converts <paramref name="images"/> as the document's operation (progress, Cancel) and applies the
    /// result as one undo step, or writes it to the folder. Returns false when cancelled (nothing changed).
    /// </summary>
    public async Task<bool> RunConversionAsync(VppDocument doc, IReadOnlyList<VppItem> images, DdsConvertSettings settings,
        Action<Documents.VppOperation>? started = null)
    {
        if (settings.Output == DdsOutputMode.Folder && settings.Folder is null)
        {
            doc.Shell.Dialogs.ShowError("No output folder", "Choose the folder the .dds files are written to.");
            return false;
        }
        IReadOnlyList<DdsConvertResult>? results = null;
        bool finished = await doc.RunOperationAsync("Converting to DDS", async op =>
        {
            started?.Invoke(op);
            results = await Task.Run(() => DdsConversion.ConvertAllAsync(images, settings.Encode,
                (done, name) => op.Report((double)done / images.Count, name), op.Token));
        });
        if (!finished || results is null) return false;

        var failures = results.Where(r => r.Error is not null).Select(r => $"{r.Name}: {r.Error}").ToList();
        string summary;
        if (settings.Output == DdsOutputMode.Folder)
        {
            var (written, errors) = DdsConversion.WriteToFolder(settings.Folder!, results, name =>
                settings.Existing == DdsExistingPolicy.Replace
                && doc.Shell.Dialogs.Confirm($"{name} already exists", $"Replace {name} in {settings.Folder}?", "Replace"));
            failures.AddRange(errors);
            summary = $"Wrote {written.Count} .dds file(s) to {settings.Folder}.";
        }
        else
        {
            DdsApplyReport? report = null;
            int count = results.Count(r => r.Result is not null);
            doc.ApplyEdit(count == 1 ? $"Convert {results.First(r => r.Result is not null).Name} to DDS" : $"Convert {count} images to DDS", p =>
            {
                report = DdsConversion.Apply(p, results, settings.Output, settings.Existing);
                return report.Package;
            });
            summary = $"Converted {report?.Converted ?? 0} image(s) to DDS"
                + (report is { SkippedExisting.Count: > 0 } r ? $"; skipped {r.SkippedExisting.Count} whose .dds already exists" : "")
                + (settings.Output == DdsOutputMode.ReplaceOriginals ? " (the originals were replaced; Undo restores them)." : ".");
            doc.SelectNames(results.Where(r => r.Result is not null).Select(r => r.DdsName));
        }
        doc.Notice = summary;
        if (failures.Count > 0)
        {
            doc.Shell.Dialogs.ShowError($"{failures.Count} image(s) could not be converted",
                "The other images were converted.", string.Join(Environment.NewLine, failures));
        }
        return true;
    }
}
