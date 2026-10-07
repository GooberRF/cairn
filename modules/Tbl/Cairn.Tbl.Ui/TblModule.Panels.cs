using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Cairn.Previews;
using Cairn.Tbl.Assist;
using Cairn.Tbl.Index;
using Cairn.Tbl.Ui.Documents;
using Cairn.Tbl.Ui.Navigation;
using Cairn.Tbl.Ui.References;
using Cairn.Ui.Documents;
using Cairn.Ui.Modules;
using Cairn.Ui.Mvvm;

namespace Cairn.Tbl.Ui;

// The navigation half of the module: outline (left pane), reference preview (right pane of the table view), usages and
// comparison (bottom pane), go to definition / find usages / go to entry / compare with stock (Table menu, F12...).
public sealed partial class TblModule
{
    /// <summary>Panel ids (also the shell's remembered-tab keys).</summary>
    internal const string OutlinePanelId = "tbl.outline", UsagesPanelId = "tbl.usages", ComparePanelId = "tbl.compare";

    private readonly Dictionary<TblDocument, TblDocumentPanels> _panelsByDoc = [];
    // Game-data tables opened by go to definition / usages, by index key, so a second jump reuses the tab.
    private readonly Dictionary<string, TblDocument> _openedSources = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The navigation panels of <paramref name="document"/> (created on first use, disposed when it closes).</summary>
    internal TblDocumentPanels PanelsFor(TblDocument document)
    {
        // A closed document gets throwaway panels that are never stored, so nothing keeps it alive (review finding 20).
        if (document.IsDisposed) return _panelsByDoc.TryGetValue(document, out var stale) ? stale : new TblDocumentPanels(document, null, OpenTextOf, () => null);
        if (!_panelsByDoc.TryGetValue(document, out var panels))
        {
            panels = new TblDocumentPanels(document, Shell, OpenTextOf, () => Shell?.Settings.GameDirectory, s => OpenDocumentFor(s) is not null);
            panels.DefinitionRequested += (p, offset) => _ = GoToDefinitionAsync(p.Document, offset);
            panels.Reference.GoToDefinitionRequested += (_, def) => _ = OpenAtAsync(def.Source, def.Span.Start, def.Span.Length);
            panels.Usages.UsageActivated += (_, r) => _ = OpenAtAsync(r.Source, r.Span.Start, r.Span.Length);
            _panelsByDoc[document] = panels;
        }
        return panels;
    }

    /// <summary>The current text of the open table whose index key is <paramref name="key"/>, or null (UI thread).</summary>
    private string? OpenTextOf(string key) =>
        _documents.FirstOrDefault(d => string.Equals(d.IndexKey, key, StringComparison.OrdinalIgnoreCase)) is { } d ? d.Text.Text : null;

    partial void AddPanels(List<PanelContribution> panels)
    {
        panels.Add(new PanelContribution(OutlinePanelId, "Outline", PanelSide.Left, 10, d => d is TblDocument t ? PanelsFor(t).Outline : null, Exclusive: true));
        panels.Add(new PanelContribution(UsagesPanelId, "Usages", PanelSide.Bottom, 20, d => d is TblDocument t ? PanelsFor(t).Usages : null));
        panels.Add(new PanelContribution(ComparePanelId, "Compare", PanelSide.Bottom, 30, d => d is TblDocument t ? PanelsFor(t).Compare : null));
    }

    partial void AddShortcuts(List<ShortcutInfo> shortcuts)
    {
        shortcuts.Add(new ShortcutInfo("Table navigation", "Go to definition", Key.F12, ModifierKeys.None, GoToDefinitionCommand, IsTable, AllowInTextInput: true));
        shortcuts.Add(new ShortcutInfo("Table navigation", "Find usages", Key.F12, ModifierKeys.Shift, FindUsagesCommand, IsTable, AllowInTextInput: true));
        shortcuts.Add(new ShortcutInfo("Table navigation", "Go to entry", Key.O, ModifierKeys.Control | ModifierKeys.Shift, GoToEntryCommand, IsTable, AllowInTextInput: true));
    }

    partial void AddMenus(List<MenuContribution> menus)
    {
        var table = new MenuItem { Header = "_Table", ToolTip = "Navigation in tables" };
        System.Windows.Automation.AutomationProperties.SetName(table, "Table");
        table.Items.Add(MenuItemFor("Go to _Definition", "F12", "Open the table that defines the name at the caret, at its entry (Ctrl+click works too)", GoToDefinitionCommand));
        table.Items.Add(MenuItemFor("Find _Usages", "Shift+F12", "List every use of the name at the caret across the indexed tables", FindUsagesCommand));
        table.Items.Add(new Separator());
        table.Items.Add(MenuItemFor("Go to _Entry...", "Ctrl+Shift+O", "Pick an entry of this table by name", GoToEntryCommand));
        table.Items.Add(MenuItemFor("_Compare with Stock", "", "Compare this table with the game's own table of the same name", CompareCommand));
        menus.Add(new MenuContribution(MenuSlot.TopLevel, 50, table, IsTable));
    }

    private static MenuItem MenuItemFor(string header, string gesture, string tip, ICommand command)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture, ToolTip = tip, Command = command };
        System.Windows.Automation.AutomationProperties.SetName(item, header.Replace("_", string.Empty, StringComparison.Ordinal).TrimEnd('.'));
        return item;
    }

    private RelayCommand? _goToDefinition, _findUsages, _goToEntry, _compare;
    private RelayCommand GoToDefinitionCommand => _goToDefinition ??= new(() => { if (Active is { } d) _ = GoToDefinitionAsync(d, d.CaretOffset); }, () => Active is not null);
    private RelayCommand FindUsagesCommand => _findUsages ??= new(() => { if (Active is { } d) FindUsages(d, d.CaretOffset); }, () => Active is not null);
    private RelayCommand GoToEntryCommand => _goToEntry ??= new(() => { if (Active is { } d) GoToEntry(d); }, () => Active is not null);
    private RelayCommand CompareCommand => _compare ??= new(() => { if (Active is { } d) CompareWithStock(d); }, () => Active is not null);

    partial void OnDocumentViewCreated(TblDocument document, TblDocumentView view)
    {
        var panels = PanelsFor(document);
        view.RightPaneHost.Content = panels.Reference;
        if (document.Editor is { } editor && editor.ContextMenu is null) editor.ContextMenu = BuildEditorMenu(document, editor);
        panels.PreviewAt(document.CaretOffset);
    }

    partial void OnDocumentClosed(TblDocument document)
    {
        if (_panelsByDoc.Remove(document, out var panels)) panels.Dispose();
        foreach (var key in _openedSources.Where(p => ReferenceEquals(p.Value, document)).Select(p => p.Key).ToList()) _openedSources.Remove(key);
    }

    /// <summary>The editor's context menu: navigation plus the clipboard (right-click moves the caret first).</summary>
    private ContextMenu BuildEditorMenu(TblDocument document, ICSharpCode.AvalonEdit.TextEditor editor)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItemFor("Go to _Definition", "F12", "Open the definition of the name under the caret", GoToDefinitionCommand));
        menu.Items.Add(MenuItemFor("Find _Usages", "Shift+F12", "List every use of the name under the caret", FindUsagesCommand));
        menu.Items.Add(new Separator());
        foreach (var (cmd, header) in new[] { (ApplicationCommands.Cut, "Cu_t"), (ApplicationCommands.Copy, "_Copy"), (ApplicationCommands.Paste, "_Paste") })
        {
            var item = new MenuItem { Header = header, Command = cmd, CommandTarget = editor.TextArea };
            System.Windows.Automation.AutomationProperties.SetName(item, header.Replace("_", string.Empty, StringComparison.Ordinal));
            menu.Items.Add(item);
        }
        editor.ContextMenuOpening += (_, _) =>
        {
            // Act on the clicked word, not wherever the caret was, unless the click is inside the selection.
            if (editor.GetPositionFromPoint(Mouse.GetPosition(editor)) is not { } pos) return;
            int offset = document.Text.GetOffset(pos.Location);
            if (editor.SelectionLength > 0 && offset >= editor.SelectionStart && offset <= editor.SelectionStart + editor.SelectionLength) return;
            editor.TextArea.Caret.Offset = offset;
        };
        return menu;
    }

    // ── Commands ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Go to definition of the name at <paramref name="offset"/>: an entry of another table opens that table (an open
    /// tab, a file on disk, or a game-data table read-only from its packfile) at the entry; a file name opens the file
    /// when a module edits it, else previews it. Returns the document jumped to, or null.
    /// </summary>
    internal async Task<IDocument?> GoToDefinitionAsync(TblDocument document, int offset)
    {
        await WaitForModel(document);
        if (document.IsDisposed) return null; // closed while waiting (review finding 20)
        var panels = PanelsFor(document);
        string? folder = document.FilePath is { } p ? Path.GetDirectoryName(p) : null;
        var siblings = panels.Siblings;
        var resolver = AssetLookup.ResolverFor(Shell, siblings, folder);
        var model = document.Model;
        var symbol = TblAssist.SymbolAt(model.Parsed, offset);
        if (symbol is null) { Shell?.ShowStatus("Place the caret on a file name or on a name from another table."); return null; }
        if (symbol.Kind == TblSymbolKind.EntryName) { Shell?.ShowStatus($"'{symbol.Name}' is defined here. Find usages (Shift+F12) lists where it is used."); return null; }
        var target = TblAssist.FindDefinition(model.Parsed, offset, Index, name => AssetLookup.Resolve(resolver, name, siblings).Location);
        if (target?.Definition is { } def) return await OpenAtAsync(def.Source, def.Span.Start, def.Span.Length);
        if (target?.File is { } file)
        {
            string ext = Path.GetExtension(file.ResolvedName).ToLowerInvariant();
            bool openable = Shell is not null && Shell.Modules.Any(m => m.DocumentKinds.Any(k => k.Extensions.Contains(ext)));
            if (openable && Shell!.OpenLocation(file)) return Shell.ActiveDocument;
            panels.PreviewAt(offset, force: true);
            Shell?.ShowStatus($"{file.ResolvedName} is shown in the reference preview (no editor in Cairn for {ext} files).");
            return null;
        }
        panels.PreviewAt(offset, force: true);
        Shell?.ShowStatus(symbol.Kind == TblSymbolKind.File ? $"'{symbol.Name}' was not found in the game data, the search folders or beside the table."
            : $"No indexed table defines the {symbol.IndexKind} '{symbol.Name}'.");
        return null;
    }

    /// <summary>
    /// The open tab that shows <paramref name="source"/>: the document itself, a table this module opened from that
    /// packfile entry, or one opened from it elsewhere (its archive origin reads "name in packfile.vpp"); else null.
    /// </summary>
    internal TblDocument? OpenDocumentFor(TblSource source)
    {
        var doc = _documents.FirstOrDefault(d => string.Equals(d.IndexKey, source.Key, StringComparison.OrdinalIgnoreCase));
        if (doc is null && _openedSources.TryGetValue(source.Key, out var opened) && _documents.Contains(opened)) doc = opened;
        if (doc is null && source.Location is { IsArchived: true } l)
        {
            string origin = $"{l.ResolvedName} in {Path.GetFileName(l.ArchivePath)}";
            doc = _documents.FirstOrDefault(d => d.FilePath is null && string.Equals(d.ArchiveOriginText, origin, StringComparison.OrdinalIgnoreCase));
        }
        return doc;
    }

    /// <summary>Opens (or activates) the table of <paramref name="source"/> and selects the span. Returns the document, or null.</summary>
    internal async Task<IDocument?> OpenAtAsync(TblSource source, int start, int length)
    {
        if (Shell is null) return null;
        TblDocument? target = OpenDocumentFor(source);
        if (target is not null) Shell.Activate(target);
        else
        {
            bool ok;
            if (source.Location is { IsArchived: true } location) ok = Shell.OpenLocation(location);
            else if (source.Kind == TblSourceKind.GameData && source.Location is { FilePath: { } gamePath } && File.Exists(gamePath))
            {
                // Review finding 18: a loose table in the game folder opens read-only (Save becomes Save As).
                try
                {
                    Shell.AddDocument(Kind.OpenBytes(File.ReadAllBytes(gamePath), Path.GetFileName(gamePath), gamePath + " (game folder)"));
                    ok = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ok = false; }
            }
            else
            {
                string path = source.Location?.FilePath ?? source.Key;
                ok = File.Exists(path) && Shell.OpenFile(path);
            }
            target = ok ? Shell.ActiveDocument as TblDocument : null;
            if (target is null) { Shell.ShowStatus($"{source.FileName} could not be opened."); return null; }
            if (target.FilePath is null)
            {
                _openedSources[source.Key] = target;
                if (source.Location?.ArchivePath is { } archive) PanelsFor(target).Siblings = new TblArchiveSiblings(archive);
            }
        }
        await NavigateWhenReady(target, start, length);
        return target;
    }

    /// <summary>Selects the span once the document's editor is on screen (a new tab's view is created on first show).</summary>
    private async Task NavigateWhenReady(TblDocument document, int start, int length)
    {
        var dispatcher = Shell?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        for (int i = 0; i < 100 && !(document.Editor is { IsLoaded: true }); i++)
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        document.NavigateTo(start, length);
        // Once more after layout so the line is scrolled into view in a tab that was just created.
        await dispatcher.InvokeAsync(() => document.NavigateTo(start, length), DispatcherPriority.Loaded);
    }

    /// <summary>Find usages of the name at <paramref name="offset"/> into the Usages panel (and brings it forward).</summary>
    internal void FindUsages(TblDocument document, int offset)
    {
        var panels = PanelsFor(document);
        var model = document.Model;
        var symbol = TblAssist.SymbolAt(model.Parsed, Math.Clamp(offset, 0, model.Text.Length));
        if (symbol is null || symbol.Kind is TblSymbolKind.Field or TblSymbolKind.Section)
            panels.Usages.ShowMessage("Place the caret on an entry name, a name from another table or a file name, then Find usages (Shift+F12).");
        else
        {
            string what = symbol.Kind == TblSymbolKind.File ? $"the file '{symbol.Name}'" : $"{symbol.IndexKind ?? "the name"} '{symbol.Name}'";
            // A game-data table open in a tab is listed once, as the tab (its live text), not again from its packfile.
            var uses = TblAssist.FindUsages(model.Parsed, offset, Index)
                .Where(r => r.Source.Kind == TblSourceKind.OpenDocument || OpenDocumentFor(r.Source) is null).ToImmutableArray();
            panels.Usages.Show(what, uses, OpenTextOf);
            if (!Index.Sources.Any() || !IndexReady.IsCompleted) Shell?.ShowStatus("The table index is still being built; usages may be incomplete.");
        }
        BringForward(document, UsagesPanelId, panels.Usages);
    }

    /// <summary>Compares the document with the stock table of the same name (Compare panel).</summary>
    internal void CompareWithStock(TblDocument document)
    {
        var panels = PanelsFor(document);
        panels.Compare.Run(document.FilePath is { } p ? Path.GetFileName(p) : document.DisplayName, document.Model);
        BringForward(document, ComparePanelId, panels.Compare);
    }

    /// <summary>Go to entry: a quick pick of the table's entries.</summary>
    internal void GoToEntry(TblDocument document)
    {
        if (Shell is null) return;
        var window = new TblGoToEntryWindow(document.Model.Parsed) { Owner = Shell.MainWindow };
        if (window.ShowDialog() == true && window.Result is { } r) document.NavigateTo(r.Start, r.Length);
    }

    /// <summary>Makes <paramref name="panel"/>'s bottom tab the selected one (now, and when the kind is next activated).</summary>
    private void BringForward(TblDocument document, string panelId, FrameworkElement panel)
    {
        if (Shell is null) return;
        _ = panel;
        if (Shell.ShowPanel(panelId)) return;
        // A tab that is not there yet (its content was just created): once the shell re-queried the panels.
        Shell.RefreshCommands();
        Shell.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!Shell.ShowPanel(panelId)) Shell.ShowStatus("Results are in the bottom pane (View > Bottom Pane).");
        });
    }

    private static async Task WaitForModel(TblDocument document)
    {
        var current = document.WhenModelCurrent;
        if (!current.IsCompleted) await Task.WhenAny(current, Task.Delay(2000));
    }
}
