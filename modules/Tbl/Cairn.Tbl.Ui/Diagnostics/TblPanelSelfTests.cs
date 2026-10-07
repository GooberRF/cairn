using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cairn.Assets;
using Cairn.Previews;
using Cairn.Tbl.Assist;
using Cairn.Tbl.Compare;
using Cairn.Tbl.Model;
using Cairn.Tbl.Ui.Navigation;
using Cairn.Tbl.Ui.References;
using Cairn.Ui.Diagnostics;
using Cairn.Ui.Services;
using TblDocument = Cairn.Tbl.Ui.Documents.TblDocument;
using TblToken = Cairn.Tbl.Ui.Documents.TblToken;

namespace Cairn.Tbl.Ui.Diagnostics;

/// <summary>
/// Self-tests of the navigation half of the table module on the game's own tables (read only; edited copies go to a
/// temp folder): reference preview of each kind of file, entry references, outline, go to definition, find usages,
/// compare with stock, and that every panel is released when its document closes. Screenshots go to
/// <c>--preview-shots &lt;folder&gt;</c> (else a temp folder).
/// </summary>
internal static class TblPanelSelfTests
{
    /// <summary>Review findings 16, 20, 22: index keys follow Save As, closed documents get no stored panels, and
    /// changing a table's definitions re-lints the other open tables.</summary>
    [SelfTest("tbl.index-lifecycle", Order = 525)]
    public static async Task IndexLifecycle(SelfTestContext ctx)
    {
        var shell = ctx.Shell;
        var module = shell.Modules.OfType<TblModule>().FirstOrDefault();
        if (module is null) { ctx.Skip("the table module is not loaded"); return; }
        string temp = Path.Combine(Path.GetTempPath(), "cairn-tbl-lifecycle-" + Environment.ProcessId);
        Directory.CreateDirectory(temp);
        static async Task Settle(TblDocument d) { for (int i = 0; i < 100 && !d.WhenModelCurrent.IsCompleted; i++) await Task.Delay(50); }
        var ammo = (TblDocument)module.Kind.OpenBytes(System.Text.Encoding.ASCII.GetBytes("#Ammo\r\n$Name: \"cairn_lc_foo\"\r\n$HUD Icon Filename: \"\"\r\n#End\r\n"), "ammo.tbl", "self-test");
        var user = (TblDocument)module.Kind.OpenBytes(System.Text.Encoding.ASCII.GetBytes("// user\r\n"), "zz_user.tbl", "self-test");
        try
        {
            await Settle(ammo);
            string oldKey = ammo.IndexKey;
            ctx.Check(module.Index.Sources.Any(s => s.Key == oldKey && s.FileName == "ammo.tbl" && s.DisplayLocation.StartsWith("unsaved", StringComparison.Ordinal)),
                "an unsaved table is indexed under its tab name, marked unsaved");
            string path = Path.Combine(temp, "ammo.tbl");
            ammo.SaveTo(path);
            ctx.Check(!module.Index.Sources.Any(s => s.Key == oldKey), "Save As drops the old index key");
            // 22: a definition change schedules a re-lint of the other open table.
            await Settle(user);
            int before = user.ParseRequests;
            ammo.Text.Replace(ammo.Text.Text.IndexOf("cairn_lc_foo", StringComparison.Ordinal), "cairn_lc_foo".Length, "cairn_lc_bar");
            for (int i = 0; i < 60 && user.ParseRequests == before; i++) await Task.Delay(50);
            ctx.Check(user.ParseRequests > before, $"renaming a definition re-lints the other open tables ({before} -> {user.ParseRequests} parse requests)");
        }
        finally
        {
            ammo.Dispose();
            user.Dispose();
        }
        // 20: panels asked for after the close are not kept.
        var weak = new WeakReference(module.PanelsFor(ammo));
        ctx.Check(!ReferenceEquals(module.PanelsFor(ammo), weak.Target), "a closed document's panels are never stored");
        try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
    }

    [SelfTest("tbl.panels", Order = 520)]
    public static async Task Panels(SelfTestContext ctx)
    {
        var shell = ctx.Shell;
        var module = shell.Modules.OfType<TblModule>().FirstOrDefault();
        if (module is null) { ctx.Skip("the table module is not loaded"); return; }
        string? gameDir = shell.Settings.GameDirectory;
        if (string.IsNullOrWhiteSpace(gameDir) || TblCompare.FindStock(gameDir, "weapons.tbl") is not { } stockWeapons) { ctx.Skip("no game directory with tables.vpp"); return; }
        await shell.Assets.ArchivesIndexed;
        await module.IndexReady;
        string temp = Path.Combine(Path.GetTempPath(), "cairn-tbl-selftest-" + Environment.ProcessId);
        Directory.CreateDirectory(temp);
        try
        {
            // Game-data weapons.tbl, read-only from its packfile, as go to definition would open it.
            ctx.Check(shell.OpenLocation(stockWeapons.Location), "weapons.tbl opens from the game's packfile");
            if (shell.ActiveDocument is not TblDocument weapons) { ctx.Check(false, "weapons.tbl is a table document"); return; }
            await ReadyAsync(weapons);
            var panels = module.PanelsFor(weapons);
            ctx.Check(ReferenceEquals(weapons.DocumentView?.RightPaneHost.Content, panels.Reference), "reference pane hosted in the table view's right pane");

            await ReferencePreviewAsync(ctx, weapons, panels, temp);
            await OutlineAsync(ctx, weapons, panels);
            await NavigationAsync(ctx, module, weapons);
            await CompareAsync(ctx, module, weapons, stockWeapons.Text, temp);
            await ShotsAsync(ctx, weapons, panels);
            await ReleaseAsync(ctx, module, temp);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // ── 1. Reference preview ────────────────────────────────────────────────────────────────────────────────────

    private static async Task ReferencePreviewAsync(SelfTestContext ctx, TblDocument doc, TblDocumentPanels panels, string temp)
    {
        var pane = panels.Reference;
        var files = TblValueRoles.All(doc.Model.Parsed).Where(r => r.Role == TblValueRole.File).ToList();
        ctx.Log($"  weapons.tbl: {files.Count} file references, {doc.Model.EntryCount} entries");
        foreach (var (ext, want, resolvedExt) in new[]
        {
            (".tga", AssetPreviewKind.Image, ""), (".v3d", AssetPreviewKind.Module, ".v3m"), (".wav", AssetPreviewKind.Audio, ".wav"),
            (".vfx", AssetPreviewKind.Module, ".vfx"), (".mvf", AssetPreviewKind.Module, ".rfa"),
        })
        {
            var r = files.FirstOrDefault(f => f.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
            if (r is null) { ctx.Check(false, $"weapons.tbl references a {ext} file"); continue; }
            Click(doc, r.Span, TblTextClass.FileName);
            await SettleAsync(pane);
            var lookup = pane.Lookup ?? pane.Preview.Lookup;
            ctx.Log($"  {r.Name} -> {pane.Preview.Kind}, {lookup?.Describe()}; {pane.Details.Rows.Count} detail rows");
            ctx.Check(pane.Kind == TblReferenceKind.File && pane.ShownName == r.Name, $"{ext}: clicking '{r.Name}' previews it");
            ctx.Check(pane.Preview.Kind == want, $"{ext}: preview kind {pane.Preview.Kind} (want {want})");
            if (resolvedExt.Length > 0)
                ctx.Check(lookup?.Location?.ResolvedName.EndsWith(resolvedExt, StringComparison.OrdinalIgnoreCase) == true, $"{ext}: resolved as {lookup?.Location?.ResolvedName} (engine name mapping)");
            ctx.Check(pane.Details.Rows.Any(x => x.Label == "Found in") && pane.Details.Rows.Any(x => x.Label == "Size") && pane.Details.Rows.Any(x => x.Label == "Used"), $"{ext}: details list location, size and uses");
        }

        // A texture written as .tga that exists only as .dds (beside the table): a 4x4 DXT1 file made here.
        File.WriteAllBytes(Path.Combine(temp, "cairn_selftest_dds_only.dds"), TinyDds());
        pane.ShowFile("cairn_selftest_dds_only.tga", null, temp);
        await SettleAsync(pane);
        ctx.Check(pane.Preview.Kind == AssetPreviewKind.Image && pane.Lookup?.Location?.ResolvedName.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) == true,
            $".tga name found as .dds: {pane.Preview.Kind}, {pane.Lookup?.Describe()}");
        // The type facts come from the module that describes files (the packfile module's IAssetFactsProvider).
        if (ctx.Shell.Modules.OfType<Cairn.Ui.Modules.IAssetFactsProvider>().Any())
        {
            await pane.Details.Pending;
            ctx.Check(pane.Details.Rows.Any(r => r.Section == "Image"), "details list the image facts (" + string.Join(", ", pane.Details.Rows.Select(r => r.Section + "/" + r.Label).Distinct().Take(12)) + ")");
        }

        pane.ShowFile("cairn_no_such_texture.tga", null, temp);
        await SettleAsync(pane);
        ctx.Check(pane.Preview.Kind == AssetPreviewKind.NotFound && pane.Lookup is { Found: false, Searched.Count: > 0 } && pane.Details.Warnings.Count > 0,
            $"missing name: {pane.Preview.Kind}, looked in {pane.Lookup?.Searched.Count} places");
        ctx.Check(!pane.CanOpen, "missing name: no Open in Cairn");

        // A name from another table: weapons.tbl's $Ammo Type "12mm" shows ammo.tbl's entry.
        var ammo = TblValueRoles.All(doc.Model.Parsed).FirstOrDefault(r => r.Role == TblValueRole.Ref && r.Name == "12mm");
        if (ammo is null) { ctx.Check(false, "weapons.tbl refers to the ammo '12mm'"); return; }
        Click(doc, ammo.Span, TblTextClass.RefName);
        await pane.Pending;
        await IdleAsync();
        ctx.Check(pane.Kind == TblReferenceKind.Definition && pane.Definition?.Source.FileName.Equals("ammo.tbl", StringComparison.OrdinalIgnoreCase) == true,
            $"'12mm' shows its definition: {pane.DefinitionHeader}");
        ctx.Check(pane.Snippet.Text.Contains("12mm", StringComparison.Ordinal) && pane.Snippet.Text.Contains("$Name", StringComparison.Ordinal),
            $"definition text shown ({pane.Snippet.Text.Split('\n').Length} lines)");
    }

    // ── 2. Outline ──────────────────────────────────────────────────────────────────────────────────────────────

    private static async Task OutlineAsync(SelfTestContext ctx, TblDocument doc, TblDocumentPanels panels)
    {
        var outline = panels.Outline;
        var parsed = doc.Model.Parsed;
        ctx.Check(outline.EntryCount == parsed.Entries.Count(), $"outline entries {outline.EntryCount} = model entries {parsed.Entries.Count()}");
        ctx.Check(outline.Rows.Count(r => r.IsSection) == parsed.Sections.Length, $"outline sections {outline.Rows.Count(r => r.IsSection)} = {parsed.Sections.Length}");
        var row = outline.Rows.Where(r => !r.IsSection).Skip(5).FirstOrDefault();
        if (row is null) { ctx.Check(false, "outline has entries"); return; }
        outline.Activate(row);
        await IdleAsync();
        var editor = doc.Editor!;
        ctx.Check(editor.SelectionStart == row.Start && editor.SelectionLength == row.Length, $"clicking '{row.Label}' selects it in the editor ({editor.SelectionStart}/{editor.SelectionLength})");
        await IdleAsync();
        ctx.Check(outline.Current?.Label == row.Label, $"outline highlights the entry under the caret ({outline.Current?.Label})");
        outline.Filter = "12mm";
        ctx.Check(outline.Rows.Where(r => !r.IsSection).All(r => r.Label.Contains("12mm", StringComparison.OrdinalIgnoreCase)) && outline.Rows.Any(r => !r.IsSection), $"filter '12mm': {outline.Rows.Count(r => !r.IsSection)} entries");
        outline.Filter = "";
    }

    // ── 3. Go to definition, find usages ────────────────────────────────────────────────────────────────────────

    private static async Task NavigationAsync(SelfTestContext ctx, TblModule module, TblDocument weapons)
    {
        var ammo = TblValueRoles.All(weapons.Model.Parsed).First(r => r.Role == TblValueRole.Ref && r.Name == "12mm");
        var target = await module.GoToDefinitionAsync(weapons, ammo.Span.Start + 1);
        if (target is not TblDocument ammoDoc) { ctx.Check(false, "go to definition of '12mm' opens a table"); return; }
        string selected = ammoDoc.Editor?.SelectedText ?? "";
        ctx.Check(ammoDoc.DisplayName.Equals("ammo.tbl", StringComparison.OrdinalIgnoreCase) && ammoDoc.IsReadOnly, $"go to definition opened {ammoDoc.DisplayName} (read-only {ammoDoc.IsReadOnly})");
        ctx.Check(selected == "12mm" && ammoDoc.Model.Parsed.EntryAt(ammoDoc.Editor!.SelectionStart)?.Name == "12mm", $"at the entry '12mm' (selected '{selected}')");
        var again = await module.GoToDefinitionAsync(weapons, ammo.Span.Start + 1);
        ctx.Check(ReferenceEquals(again, ammoDoc), "a second jump reuses the open tab");

        // Find usages of the ammo from its definition.
        module.FindUsages(ammoDoc, ammoDoc.Editor!.SelectionStart);
        var usages = module.PanelsFor(ammoDoc).Usages;
        await usages.Pending;
        var weaponRows = usages.Rows.Where(r => r.Table.Equals("weapons.tbl", StringComparison.OrdinalIgnoreCase)).ToList();
        ctx.Log($"  usages: {usages.Header} weapons: {string.Join(", ", weaponRows.Select(r => r.Entry).Distinct())}");
        ctx.Check(weaponRows.Any(r => r.Entry == "12mm handgun" && r.Field.StartsWith("$Ammo Type", StringComparison.Ordinal) && r.Snippet.Contains("12mm", StringComparison.Ordinal)),
            "find usages of '12mm' lists the 12mm handgun ($Ammo Type, with its line)");
        var use = weaponRows.FirstOrDefault(r => r.Entry == "12mm handgun");
        if (use is not null)
        {
            var opened = await module.OpenAtAsync(use.Reference.Source, use.Reference.Span.Start, use.Reference.Span.Length);
            ctx.Check(opened is TblDocument t && t.Editor?.SelectedText == "12mm" && t.Model.Parsed.EntryAt(t.Editor.SelectionStart)?.Name == "12mm handgun",
                $"clicking the usage opens {opened?.DisplayName} at it");
        }
        ctx.Shell.Activate(weapons);
        await IdleAsync();
    }

    // ── 4. Compare with stock ───────────────────────────────────────────────────────────────────────────────────

    private static async Task CompareAsync(SelfTestContext ctx, TblModule module, TblDocument weapons, string stockText, string temp)
    {
        module.CompareWithStock(weapons);
        var compare = module.PanelsFor(weapons).Compare;
        await compare.Pending;
        ctx.Check(compare.Comparison is { IsIdentical: true } && compare.Rows.Count == 0, $"stock weapons.tbl against itself: {compare.Header}");

        // A hand-edited copy: one value changed, one entry renamed (= removed + added).
        var regex = new Regex("(\\$Ammo Type:\\s*)\"12mm\"");
        string edited = regex.Replace(stockText, "$1\"shotgun\"", 1).Replace("\"Undercover 12mm handgun\"", "\"Cairn test handgun\"", StringComparison.Ordinal);
        string path = Path.Combine(temp, "weapons.tbl");
        File.WriteAllText(path, edited, System.Text.Encoding.Latin1);
        if (!ctx.Shell.OpenFile(path) || ctx.Shell.ActiveDocument is not TblDocument copy) { ctx.Check(false, "edited copy opens"); return; }
        await ReadyAsync(copy);
        module.CompareWithStock(copy);
        var c2 = module.PanelsFor(copy).Compare;
        await c2.Pending;
        var cmp = c2.Comparison;
        ctx.Log($"  edited copy: {c2.Header}");
        ctx.Check(cmp is { Added: 1, Removed: 1, Changed: 1 }, $"edits found: {cmp?.Added} added, {cmp?.Removed} removed, {cmp?.Changed} changed");
        var field = c2.Rows.FirstOrDefault(r => r.IsField && r.Entry == "12mm handgun" && r.Field.Contains("Ammo Type", StringComparison.Ordinal));
        ctx.Check(field is { Stock: "\"12mm\"" or "12mm", Modded: "\"shotgun\"" or "shotgun" }, $"field change: {field?.Field} {field?.Stock} -> {field?.Modded}");
        if (field?.ModdedSpan is { } span)
        {
            c2.Activate(field);
            await IdleAsync();
            ctx.Check(copy.Editor!.SelectionStart == span.Start, "clicking the change jumps to it");
        }
        var removed = c2.Rows.FirstOrDefault(r => r.Kind == "removed");
        if (removed is not null) c2.Activate(removed);
        ctx.Check(c2.StockText.Contains("Undercover 12mm handgun", StringComparison.Ordinal), "a removed entry shows its stock text");
        c2.OnlyChanges = false;
        ctx.Check(c2.Rows.Count(r => r.Kind == "same") == copy.Model.EntryCount - 2, $"'Only changes' off lists the unchanged entries too ({c2.Rows.Count} rows)");
        c2.OnlyChanges = true;

        string custom = Path.Combine(temp, "cairn_custom.tbl");
        File.WriteAllText(custom, "#Things\r\n$Name: \"a\"\r\n#End\r\n");
        if (ctx.Shell.OpenFile(custom) && ctx.Shell.ActiveDocument is TblDocument customDoc)
        {
            await ReadyAsync(customDoc);
            module.CompareWithStock(customDoc);
            var c3 = module.PanelsFor(customDoc).Compare;
            await c3.Pending;
            ctx.Check(c3.Comparison is null && c3.Header.Contains("no stock table", StringComparison.OrdinalIgnoreCase), $"custom table: {c3.Header}");
        }
        ctx.Shell.Activate(weapons);
        await IdleAsync();
    }

    // ── Screenshots ─────────────────────────────────────────────────────────────────────────────────────────────

    private static async Task ShotsAsync(SelfTestContext ctx, TblDocument weapons, TblDocumentPanels panels)
    {
        string dir = ctx.Options.TryGetValue("preview-shots", out var d) && !string.IsNullOrWhiteSpace(d) ? d : Path.Combine(Path.GetTempPath(), "cairn-preview-shots");
        Directory.CreateDirectory(dir);
        ctx.Shell.Activate(weapons);
        var texture = TblValueRoles.All(weapons.Model.Parsed).FirstOrDefault(r => r.Role == TblValueRole.File && r.Name.EndsWith(".tga", StringComparison.OrdinalIgnoreCase));
        if (texture is null || weapons.Editor is not { } editor) return;
        editor.TextArea.Caret.Offset = texture.Span.Start + 2;
        editor.TextArea.Caret.BringCaretToView();
        await Task.Delay(400);
        await SettleAsync(panels.Reference);
        ctx.Check(panels.Reference.ShownName == texture.Name && panels.Reference.Preview.Kind == AssetPreviewKind.Image, $"caret on '{texture.Name}' previews it (debounced)");
        Save(ctx, dir, "tbl-weapons-dark.png");
        var theme = ctx.Shell.Theme;
        var before = theme.Requested;
        theme.Apply(Cairn.Workspace.AppTheme.Light);
        try
        {
            await Task.Delay(200);
            await IdleAsync();
            Save(ctx, dir, "tbl-weapons-light.png");
        }
        finally { theme.Apply(before); }
        // The definition view, dark.
        var ammo = TblValueRoles.All(weapons.Model.Parsed).First(r => r.Role == TblValueRole.Ref && r.Name == "12mm");
        editor.TextArea.Caret.Offset = ammo.Span.Start + 1;
        editor.TextArea.Caret.BringCaretToView();
        await Task.Delay(400);
        await panels.Reference.Pending;
        await IdleAsync();
        Save(ctx, dir, "tbl-definition-dark.png");
    }

    // ── 5. Release on close ─────────────────────────────────────────────────────────────────────────────────────

    private static async Task ReleaseAsync(SelfTestContext ctx, TblModule module, string temp)
    {
        string path = Path.Combine(temp, "release.tbl");
        File.WriteAllText(path, "#Things\r\n$Name: \"a\"\r\n$Texture: \"x.tga\"\r\n#End\r\n");
        var weak = await OpenAndCloseAsync(ctx, module, path);
        // Close every table so the shell's panes let go of the others as well.
        foreach (var d in ctx.Shell.Documents.OfType<TblDocument>().ToList()) ctx.Shell.Close(d);
        await IdleAsync(4);
        bool collected = await CollectedAsync(weak);
        ctx.Check(collected, "document panels (reference pane, outline, usages, compare) collectable after close");
        if (!collected) foreach (var w in weak.Where(w => w.IsAlive)) LogRoot(ctx, w);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<List<WeakReference>> OpenAndCloseAsync(SelfTestContext ctx, TblModule module, string path)
    {
        if (!ctx.Shell.OpenFile(path) || ctx.Shell.ActiveDocument is not TblDocument doc) { ctx.Check(false, "release.tbl opens"); return []; }
        await ReadyAsync(doc);
        var p = module.PanelsFor(doc);
        p.PreviewAt(doc.Text.Text.IndexOf("x.tga", StringComparison.Ordinal) + 1, force: true);
        module.FindUsages(doc, doc.Text.Text.IndexOf("x.tga", StringComparison.Ordinal) + 1);
        module.CompareWithStock(doc);
        await SettleAsync(p.Reference);
        await p.Compare.Pending;
        var weak = new List<WeakReference> { new(p), new(p.Reference), new(p.Outline), new(p.Usages), new(p.Compare), new(doc) };
        ctx.Check(ctx.Shell.Close(doc), "release.tbl closes");
        return weak;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LogRoot(SelfTestContext ctx, WeakReference weak)
    {
        if (weak.Target is { } o) ctx.Log($"  GC root of {o.GetType().Name}:{Environment.NewLine}{ctx.DescribeRoot(o)}");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────────

    private static void Click(TblDocument doc, Cairn.Tbl.Text.TextSpan span, TblTextClass cls)
    {
        var symbol = TblAssist.SymbolAt(doc.Model.Parsed, span.Start);
        doc.RaiseTokenActivated(new TblToken(cls, span, doc.Model.Text.Substring(span.Start, span.Length), symbol), false);
    }

    private static async Task ReadyAsync(TblDocument doc)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(10) && !(doc.Editor is { IsLoaded: true } && doc.WhenModelCurrent.IsCompleted)) await Task.Delay(30);
        await IdleAsync();
    }

    private static async Task SettleAsync(TblReferencePane pane)
    {
        await IdleAsync();
        await pane.Preview.Pending;
        await pane.Details.Pending;
        var watch = Stopwatch.StartNew();
        await Task.Delay(150);
        while (watch.Elapsed < TimeSpan.FromSeconds(15) && BusyTracker.Describe().Any(d => d.Contains("preview", StringComparison.OrdinalIgnoreCase) || d.Contains("details", StringComparison.OrdinalIgnoreCase) || d.StartsWith("texture", StringComparison.OrdinalIgnoreCase)))
            await Task.Delay(30);
        await IdleAsync();
    }

    private static async Task IdleAsync(int times = 2)
    {
        for (int i = 0; i < times; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static async Task<bool> CollectedAsync(List<WeakReference> weak)
    {
        for (int i = 0; i < 10 && weak.Any(w => w.IsAlive); i++)
        {
            await IdleAsync();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (weak.Any(w => w.IsAlive)) await Task.Delay(80);
        }
        return weak.Count > 0 && !weak.Any(w => w.IsAlive);
    }

    private static void Save(SelfTestContext ctx, string dir, string name)
    {
        if (ctx.MainWindow.Content is not FrameworkElement root) return;
        root.UpdateLayout();
        int w = (int)Math.Max(1, root.ActualWidth), h = (int)Math.Max(1, root.ActualHeight);
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        string path = Path.Combine(dir, name);
        using (var stream = File.Create(path)) encoder.Save(stream);
        ctx.Log($"  screenshot {path}");
    }

    /// <summary>A 4x4 DXT1 DDS (red), enough for the texture decoder.</summary>
    private static byte[] TinyDds()
    {
        var b = new byte[128 + 8];
        void U32(int at, uint v) => BitConverter.TryWriteBytes(b.AsSpan(at), v);
        "DDS "u8.CopyTo(b);
        U32(4, 124);
        U32(8, 0x1 | 0x2 | 0x4 | 0x1000 | 0x80000);
        U32(12, 4);
        U32(16, 4);
        U32(20, 8);
        U32(76, 32);
        U32(80, 0x4);
        "DXT1"u8.CopyTo(b.AsSpan(84));
        U32(108, 0x1000);
        b[128] = 0x00; b[129] = 0xF8; // colour 0: red (565)
        b[130] = 0x00; b[131] = 0xF8;
        return b;
    }
}
