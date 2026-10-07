using System.Windows.Controls;
using System.Windows.Input;
using Cairn.Ui.Modules;
using Cairn.Ui.Mvvm;
using Cairn.Vfx.Ui.Commands;
using Cairn.Vfx.Ui.Documents;

namespace Cairn.Vfx.Ui;

/// <summary>Editing registrations: the Effect menu's Edit group and the editing shortcuts.</summary>
public sealed partial class VfxModule
{
    private RelayCommand Cmd(Action<VfxDocument> run, bool needsSelection = true) =>
        new(() => { if (Active is { } d) run(d); }, () => Active is { } d && (!needsSelection || !d.Selection.IsEmpty));

    private void AddEditingItems(MenuItem menu)
    {
        menu.Items.Add(new Separator());
        var edit = new MenuItem { Header = "_Edit", ToolTip = "Operations on the selected objects" };
        foreach (var (header, gesture, tip, cmd) in new (string, string, string, RelayCommand)[]
        {
            ("_Rename", "F2", "Rename the selected object", Cmd(d => (d.View as VfxDocumentView)?.Outliner.BeginRename())),
            ("_Duplicate", "Ctrl+D", "Copy the selected objects", Cmd(d => VfxObjectCommands.Duplicate(d))),
            ("De_lete", "Del", "Remove the selected objects or materials", Cmd(d => VfxObjectCommands.Delete(d))),
            ("Move _up", "Alt+Up", "Move earlier in file order", Cmd(d => VfxObjectCommands.Move(d, -1))),
            ("Move do_wn", "Alt+Down", "Move later in file order", Cmd(d => VfxObjectCommands.Move(d, 1))),
            ("Select all of this _type", "", "Select every object (or material) of the same kind", Cmd(VfxObjectCommands.SelectAllOfType)),
            ("_Isolate in preview", "", "Hide every other object in the preview", Cmd(VfxObjectCommands.Isolate)),
            ("_Show all in preview", "", "Show every object in the preview", Cmd(VfxObjectCommands.ShowAll, false)),
        })
            edit.Items.Add(new MenuItem { Header = header, InputGestureText = gesture, ToolTip = tip, Command = cmd });
        menu.Items.Add(edit);
    }

    /// <summary>Diagnostic option <c>--inspector effect|object|material</c>: shows that inspector tab (after <c>--select</c>).</summary>
    internal static void ApplyEditingOptions(VfxDocument doc, IReadOnlyDictionary<string, string> options)
    {
        if (options.TryGetValue("select-kind", out var kind))
        {
            var secs = doc.Current.Sections;
            int i = Enumerable.Range(0, secs.Length).FirstOrDefault(i => secs[i].GetType().Name.Contains(kind, StringComparison.OrdinalIgnoreCase), -1);
            if (i >= 0) doc.Selection.Select(i);
        }
        if (options.TryGetValue("inspector", out var tab) && doc.View is VfxDocumentView view)
            view.Dispatcher.InvokeAsync(() => view.Inspectors.Show(tab), System.Windows.Threading.DispatcherPriority.Loaded);
        // --inspector-width N: inspector column width for narrow-layout captures (not remembered).
        if (options.TryGetValue("inspector-width", out var iw) && double.TryParse(iw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w)
            && doc.View is VfxDocumentView wv && wv.ColumnDefinitions.Count > 4)
            wv.ColumnDefinitions[4].Width = new System.Windows.GridLength(Math.Max(200, w));
    }

    /// <summary>Editing shortcuts (they yield to text input per the shell's routing rule).</summary>
    public override IReadOnlyList<ShortcutInfo> Shortcuts =>
    [
        new("Effect editing", "Duplicate selected objects", Key.D, ModifierKeys.Control, Cmd(d => VfxObjectCommands.Duplicate(d)), d => d is VfxDocument),
        new("Effect editing", "Delete selected objects or materials", Key.Delete, ModifierKeys.None, Cmd(d => VfxObjectCommands.Delete(d)), d => d is VfxDocument),
        new("Effect editing", "Rename selected object", Key.F2, ModifierKeys.None, Cmd(d => (d.View as VfxDocumentView)?.Outliner.BeginRename()), d => d is VfxDocument),
        .. TimelineShortcuts, // VfxModule.Timeline.cs
    ];
}
