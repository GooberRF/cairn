using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Cairn.Vfx.Editing;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Ui.Documents;
using Cairn.Vfx.Ui.Commands;

namespace Cairn.Vfx.Ui.Outliner;

/// <summary>Outliner editing: context menu, F2 inline rename, Del, Ctrl+D, Alt+Up/Down, drag-drop reparent.</summary>
public sealed partial class VfxOutliner
{
    private Point? _dragStart;

    private void AttachEditing()
    {
        var menu = new ContextMenu();
        menu.Opened += (_, _) => FillMenu(menu);
        ContextMenu = menu;
        AllowDrop = true;
        PreviewKeyDown += (_, e) =>
        {
            if (e.OriginalSource is TextBox) return;
            bool ctrl = Keyboard.Modifiers == ModifierKeys.Control, alt = Keyboard.Modifiers == ModifierKeys.Alt;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            e.Handled = true;
            if (key == Key.F2) BeginRename();
            else if (key == Key.Delete) VfxObjectCommands.Delete(_doc);
            else if (ctrl && key == Key.D) VfxObjectCommands.Duplicate(_doc);
            else if (alt && key == Key.Up) VfxObjectCommands.Move(_doc, -1);
            else if (alt && key == Key.Down) VfxObjectCommands.Move(_doc, 1);
            else e.Handled = false;
        };
        PreviewMouseLeftButtonDown += (_, e) => _dragStart = e.GetPosition(this);
        PreviewMouseMove += (_, e) =>
        {
            if (_dragStart is not { } s || e.LeftButton != MouseButtonState.Pressed || (e.GetPosition(this) - s).Length < 6) return;
            _dragStart = null;
            if (SelectedItem is TreeViewItem { Tag: int section } && section >= 0) DragDrop.DoDragDrop(this, new DataObject("vfx-section", section), DragDropEffects.Move);
        };
        Drop += (_, e) =>
        {
            if (e.Data.GetData("vfx-section") is not int section) return;
            var target = (e.OriginalSource as DependencyObject) is { } d ? FindItem(d) : null;
            string parent = target is { Tag: int t } && t >= 0 && VfxTransplant.IsObject(_doc.Current.Sections[t]) ? VfxSections.NameOf(_doc.Current.Sections[t]) : "Scene Root";
            if (target?.Tag is int ti && ti == section) return;
            VfxObjectCommands.Reparent(_doc, [section], parent);
        };
    }

    private static TreeViewItem? FindItem(DependencyObject d)
    {
        while (d is not null and not TreeViewItem) d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        return d as TreeViewItem;
    }

    private void FillMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        bool any = !_doc.Selection.IsEmpty;
        MenuItem Item(string header, string gesture, string tip, Action run, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, InputGestureText = gesture, ToolTip = tip, IsEnabled = enabled };
            AutomationProperties.SetName(mi, header.Replace("_", ""));
            mi.Click += (_, _) => run();
            menu.Items.Add(mi);
            return mi;
        }
        Item("_Rename", "F2", "Rename the selected object; children and spacewarp lists follow", BeginRename, any);
        Item("_Duplicate", "Ctrl+D", "Copy the selected objects (with unique names)", () => VfxObjectCommands.Duplicate(_doc), any);
        Item("De_lete", "Del", "Remove the selected sections; children move to the parent", () => VfxObjectCommands.Delete(_doc), any);
        var parent = Item("_Parent", "", "Attach the selected objects to another object", () => { }, any);
        foreach (var name in VfxObjectCommands.ParentNames(_doc.Current))
        {
            var p = new MenuItem { Header = name, ToolTip = $"Parent to {name}" };
            p.Click += (_, _) => VfxObjectCommands.Reparent(_doc, _doc.Selection.Sections, name);
            parent.Items.Add(p);
        }
        Item("Move _up", "Alt+Up", "Move earlier in file order (draw/update order)", () => VfxObjectCommands.Move(_doc, -1), any);
        Item("Move do_wn", "Alt+Down", "Move later in file order", () => VfxObjectCommands.Move(_doc, 1), any);
        menu.Items.Add(new Separator());
        Item("Select all of this _type", "", "Select every object (or material) of the same kind", () => VfxObjectCommands.SelectAllOfType(_doc), any);
        Item("_Isolate in preview", "", "Hide every other object in the preview", () => VfxObjectCommands.Isolate(_doc), any);
        Item("_Show all in preview", "", "Show every object in the preview", () => VfxObjectCommands.ShowAll(_doc));
        if (VfxEditing.DisabledReason(_doc) is { } why)
            foreach (var mi in menu.Items.OfType<MenuItem>().Take(6)) { mi.IsEnabled = false; mi.ToolTip = why; ToolTipService.SetShowOnDisabled(mi, true); }
    }

    /// <summary>Inline rename of the selected row (Enter commits, Escape cancels).</summary>
    public void BeginRename()
    {
        if (SelectedItem is not TreeViewItem { Tag: int section } item || section < 0 || !VfxTransplant.IsObject(_doc.Current.Sections[section])) return;
        if (VfxEditing.DisabledReason(_doc) is { } why) { _doc.ShowStatus(why); return; }
        var old = item.Header;
        var box = new TextBox { Text = VfxSections.NameOf(_doc.Current.Sections[section]), MinWidth = 120 };
        AutomationProperties.SetName(box, "New name");
        bool done = false;
        void End(bool commit)
        {
            if (done) return; done = true;
            if (!(commit && VfxObjectCommands.Rename(_doc, section, box.Text.Trim()))) item.Header = old;
        }
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { End(true); e.Handled = true; } else if (e.Key == Key.Escape) { End(false); e.Handled = true; } };
        box.LostKeyboardFocus += (_, _) => End(true);
        item.Header = box;
        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
    }
}
