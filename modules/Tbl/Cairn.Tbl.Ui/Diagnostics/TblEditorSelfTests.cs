using System.Text;
using Cairn.Assets;
using Cairn.Tbl.Assist;
using Cairn.Tbl.Linting;
using Cairn.Tbl.Model;
using Cairn.Tbl.Text;
using Cairn.Tbl.Ui.Editor;
using Cairn.Ui.Diagnostics;
using Cairn.Workspace;
using UiDocument = Cairn.Tbl.Ui.Documents.TblDocument;

namespace Cairn.Tbl.Ui.Diagnostics;

/// <summary>Self-tests of the table document and editor (run with <c>Cairn.exe --selftest</c>).</summary>
internal static class TblEditorSelfTests
{
    private static void Pump(int ms = 100) => SelfTestPump.Pump(ms);

    private static TblModule? Module(SelfTestContext ctx) => ctx.Shell.Modules.OfType<TblModule>().FirstOrDefault();

    private static string? StockFolder() =>
        LocalPaths.Research is { } r && Directory.Exists(Path.Combine(r, "rfa_workbench", "rf_decomp", "tables"))
            ? Path.Combine(r, "rfa_workbench", "rf_decomp", "tables") : null;

    /// <summary>Pumps until the document's model reflects its text (10 s limit).</summary>
    private static bool WaitModel(UiDocument doc)
    {
        // Pump first: index changes queued at idle may ask for a re-lint, which the loop then waits for.
        Pump(30);
        for (int i = 0; i < 200 && !doc.WhenModelCurrent.IsCompleted; i++) Pump(50);
        return doc.WhenModelCurrent.IsCompleted;
    }

    private static UiDocument OpenBytes(TblModule module, byte[] bytes, string name) =>
        (UiDocument)module.Kind.OpenBytes(bytes, name, name + " (self-test)");

    /// <summary>Every non-blank character is covered by a classified span, spans are ordered and inside the text.</summary>
    private static bool CoversText(Documents.TblModel model) => Uncovered(model) is null;

    /// <summary>Where the classes first fail to cover the text (null when they cover it), for the log.</summary>
    private static string? Uncovered(Documents.TblModel model)
    {
        int last = 0;
        var covered = new bool[model.Text.Length];
        foreach (var cs in model.Classes)
        {
            if (cs.Span.Start < last || cs.Span.End > model.Text.Length) return $"span {cs.Span} out of order";
            last = cs.Span.End;
            for (int i = cs.Span.Start; i < cs.Span.End; i++) covered[i] = true;
        }
        for (int i = 0; i < covered.Length; i++)
        {
            char c = model.Text[i];
            // Quotes around file/reference/entry names are left to the plain text colour.
            if (!covered[i] && !char.IsWhiteSpace(c) && c != '"' && c != '﻿')
            {
                return $"offset {i} '{model.Text.Substring(i, Math.Min(20, model.Text.Length - i)).ReplaceLineEndings(" ")}'";
            }
        }
        return null;
    }

    private static int ExpectedFolds(UiDocument doc) => TblFolding.Build(doc.Model.Parsed, doc.Text).Count;

    // Without a game directory nothing resolves.
    private static bool IgnoreWithoutGame(TblDiagnostic d) => LocalPaths.GameDirectory is null && d.Code is "TBL201" or "TBL202";

    [SelfTest("tbl.editor.stock-tables")]
    public static void StockTables(SelfTestContext ctx)
    {
        var module = Module(ctx);
        if (module is null || StockFolder() is not { } folder) { ctx.Skip("no table module or no research stock tables"); return; }
        module.IndexReady.Wait(TimeSpan.FromSeconds(60));
        foreach (string path in Directory.EnumerateFiles(folder, "*.tbl").Order(StringComparer.OrdinalIgnoreCase))
        {
            string name = Path.GetFileName(path);
            UiDocument? doc = null;
            try
            {
                doc = (UiDocument)module.Kind.Open(path);
                _ = doc.View;
                bool current = WaitModel(doc);
                var controller = doc.DocumentView!.Controller;
                ctx.Check(current && CoversText(doc.Model), $"{name}: highlighting covers the text ({doc.Model.Classes.Length} spans) {Uncovered(doc.Model)}" + (current ? "" : $" [model v{doc.Model.Version} not current: {doc.WhenModelCurrent.Status}, {doc.ParseRequests} requests, text {doc.Model.Text.Length}/{doc.Text.TextLength}]") + (doc.LastParseError is { } err ? " ERROR " + err[..Math.Min(600, err.Length)] : ""));
                ctx.Check(controller.Foldings.Count == ExpectedFolds(doc) && controller.Foldings.Count >= doc.Model.Parsed.Sections.Count(s => s.HasHeader && s.Entries.Length > 0),
                    $"{name}: {controller.Foldings.Count} fold regions match sections/entries");
                if (doc.Model.Schema is not null)
                {
                    // None at all with the game folder set: a reference the stock game itself leaves unresolved (files
                    // that exist nowhere, Volition's typos) is information, since the game tolerates it.
                    var bad = doc.Model.Diagnostics.Where(d => d.Severity != TblSeverity.Information && !IgnoreWithoutGame(d)).ToList();
                    ctx.Check(bad.Count == 0, $"{name}: 0 errors or warnings" + (bad.Count > 0 ? " (got " + string.Join("; ", bad.Take(3)) + ")" : ""));
                    int tolerated = doc.Model.Diagnostics.Count(d => d.Code.StartsWith("TBL2", StringComparison.Ordinal) && d.Severity == TblSeverity.Information);
                    if (tolerated > 0) ctx.Log($"{name}: {tolerated} references also missing in the stock game (information)");
                }
            }
            catch (Exception ex) { ctx.Check(false, $"{name}: no exception ({ex.GetType().Name}: {ex.Message})"); }
            finally { doc?.Dispose(); }
        }
    }

    [SelfTest("tbl.editor.modded-tables")]
    public static void ModdedTables(SelfTestContext ctx)
    {
        var module = Module(ctx);
        if (module is null || LocalPaths.GameDirectory is null) { ctx.Skip("no table module or no game directory"); return; }
        var resolver = ctx.Shell.Assets.Resolver;
        var locations = resolver.Enumerate([".tbl"]).Where(l => l.ArchivePath is { } a && !Path.GetFileName(a).Equals("tables.vpp", StringComparison.OrdinalIgnoreCase))
            .GroupBy(l => l.ArchivePath, StringComparer.OrdinalIgnoreCase).SelectMany(g => g.Where(l => !l.ResolvedName.EndsWith("_text.tbl", StringComparison.OrdinalIgnoreCase)).Take(2))
            .Take(60).ToList();
        // Mods the game loads with -mod and client mods: packfiles the asset resolver does not search.
        int fromMods = 0;
        foreach (string folder in new[] { "mods", "client_mods" }.Select(f => Path.Combine(LocalPaths.GameDirectory, f)).Where(Directory.Exists))
        {
            var packfiles = folder.EndsWith("client_mods", StringComparison.OrdinalIgnoreCase)
                ? Directory.EnumerateFiles(folder, "*.vpp")
                : Directory.EnumerateDirectories(folder).SelectMany(d => Directory.EnumerateFiles(d, "*.vpp"));
            foreach (string vpp in packfiles.Order(StringComparer.OrdinalIgnoreCase))
            {
                if (fromMods >= 40) break;
                try
                {
                    var archive = Cairn.Formats.Vpp.VppArchive.Open(vpp);
                    foreach (var entry in archive.Entries.Where(e => e.Name.EndsWith(".tbl", StringComparison.OrdinalIgnoreCase) && !e.Name.EndsWith("_text.tbl", StringComparison.OrdinalIgnoreCase)).Take(1))
                    {
                        locations.Add(new Cairn.Assets.AssetLocation(entry.Name, entry.Name, Cairn.Assets.AssetSourceKind.SearchFolderArchive, null, vpp, entry));
                        fromMods++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Cairn.Formats.AssetFormatException) { }
            }
        }
        ctx.Log($"{fromMods} tables from packfiles under mods\\* and client_mods");
        if (locations.Count == 0) { ctx.Skip("no modded tables in the game folder's packfiles"); return; }
        int withProblems = 0;
        foreach (var location in locations)
        {
            UiDocument? doc = null;
            try
            {
                doc = OpenBytes(module, location.ReadAllBytes(), location.ResolvedName);
                _ = doc.View;
                ctx.Check(WaitModel(doc) && CoversText(doc.Model), $"{location.ResolvedName} in {Path.GetFileName(location.ArchivePath)}: opens, highlighting covers the text {Uncovered(doc.Model)}");
                if (doc.Model.ErrorCount + doc.Model.WarningCount > 0 && withProblems++ == 0)
                {
                    // Kept for the screenshots: a real modded table with problems.
                    string sample = TempFile("modded-" + location.ResolvedName);
                    Directory.CreateDirectory(Path.GetDirectoryName(sample)!);
                    File.WriteAllBytes(sample, location.ReadAllBytes());
                    ctx.Log($"modded table with problems: {sample} ({doc.Model.ErrorCount} errors, {doc.Model.WarningCount} warnings)");
                }
            }
            catch (Exception ex) { ctx.Check(false, $"{location.ResolvedName}: no exception ({ex.GetType().Name}: {ex.Message})"); }
            finally { doc?.Dispose(); }
        }
        ctx.Log($"modded tables: {locations.Count} opened, {withProblems} with errors or warnings");

        // "Open in Cairn" from the packfile module goes through the shell's OpenLocation -> the kind's OpenBytes.
        var first = locations[0];
        ctx.Check(ctx.Shell.OpenLocation(first) && ctx.Shell.ActiveDocument is UiDocument opened && opened.IsReadOnly && opened.FilePath is null,
            "OpenLocation of a packfile table opens a read-only table document");
        // Its references resolve in its own packfile first (the shell passes the location through OpenEntry).
        ctx.Check(ctx.Shell.ActiveDocument is UiDocument withSiblings && withSiblings.Siblings is { } siblings
            && siblings.Label.Equals(Path.GetFileName(first.ArchivePath), StringComparison.OrdinalIgnoreCase) && siblings.Contains(first.ResolvedName),
            "a packfile table's references look in its own packfile first (siblings = " + (ctx.Shell.ActiveDocument as UiDocument)?.Siblings?.Label + ")");
        if (ctx.Shell.ActiveDocument is UiDocument active) ctx.Shell.Close(active);
    }

    private static byte[]? StockBytes(string name) =>
        StockFolder() is { } f && File.Exists(Path.Combine(f, name)) ? File.ReadAllBytes(Path.Combine(f, name)) : null;

    [SelfTest("tbl.editor.did-you-mean")]
    public static void DidYouMean(SelfTestContext ctx)
    {
        var module = Module(ctx);
        if (module is null || StockBytes("weapons.tbl") is not { } bytes) { ctx.Skip("no weapons.tbl"); return; }
        using var doc = OpenBytes(module, bytes, "weapons.tbl");
        _ = doc.View;
        WaitModel(doc);
        string original = doc.Text.Text;
        const string field = "$Fire Wait:";
        // The first real field (the table's header comments mention field names too).
        int at = doc.Model.Parsed.AllFields.FirstOrDefault(f => f.Is(field))?.MarkerSpan.Start ?? -1;
        if (at < 0) { ctx.Skip("weapons.tbl has no $Fire Wait:"); return; }
        doc.Text.Replace(at + 1, 4, "Fier");
        ctx.Check(WaitModel(doc), "model follows the edit");
        var diagnostic = doc.Model.Diagnostics.FirstOrDefault(d => d.Code == "TBL103" && d.Span.Start <= at + 1 && d.Span.End >= at + 1);
        ctx.Check(diagnostic is not null && diagnostic.Message.Contains("did you mean", StringComparison.OrdinalIgnoreCase), "misspelled field shows TBL103 'did you mean'"
            + (diagnostic is null ? " (near it: " + string.Join("; ", doc.Model.Diagnostics.Where(d => Math.Abs(d.Span.Start - at) < 200).Take(3)) + ")" : ": " + diagnostic.Message));
        var fix = diagnostic is null ? null : UiDocument.FixesFor(diagnostic).FirstOrDefault();
        ctx.Check(fix is not null, "the diagnostic has a quick fix: " + fix?.Title);
        if (diagnostic is null || fix is null) return;
        string typo = doc.Text.Text;
        doc.ApplyQuickFix(diagnostic, fix);
        ctx.Check(doc.Text.Text == original, "the quick fix restores the field name");
        doc.Undo();
        ctx.Check(doc.Text.Text == typo, "one Undo reverts the quick fix");
        doc.Undo();
        ctx.Check(doc.Text.Text == original && !doc.IsDirty, "a second Undo reverts the typo; not dirty");

        // Typing after the lint (before the next parse) shifts every offset: the fix is found again in the
        // current text and lands on the field, not on the stale offset.
        doc.Text.Replace(at + 1, 4, "Fier");
        ctx.Check(WaitModel(doc), "model follows the second edit");
        var stale = doc.Model.Diagnostics.FirstOrDefault(d => d.Code == "TBL103" && d.Span.Start <= at + 1 && d.Span.End >= at + 1);
        var staleFix = stale is null ? null : UiDocument.FixesFor(stale).FirstOrDefault();
        if (stale is null || staleFix is null) { ctx.Check(false, "the misspelling is reported again"); return; }
        const string typed = "// typed\r\n";
        doc.Text.Insert(0, typed);
        doc.ApplyQuickFix(stale, staleFix);
        ctx.Check(doc.Text.Text == typed + original, "a quick fix from a stale model lands on the current text");
    }

    [SelfTest("tbl.editor.completion-and-hover")]
    public static void CompletionAndHover(SelfTestContext ctx)
    {
        var module = Module(ctx);
        if (module is null || StockBytes("weapons.tbl") is not { } bytes) { ctx.Skip("no weapons.tbl"); return; }
        using var doc = OpenBytes(module, bytes, "weapons.tbl");
        _ = doc.View;
        WaitModel(doc);
        var controller = doc.DocumentView!.Controller;
        var section = doc.Model.Parsed.Sections.First(s => s.Entries.Length > 0);
        var entry = section.Entries[0];
        var nameField = entry.EntryField!;

        // Hover on the entry's name field gives its documentation.
        var (_, hover) = controller.HoverAt(nameField.MarkerSpan.Start + 2);
        ctx.Check(hover is not null && hover.Lines.Length > 0, "hover on $Name: returns the field's doc: " + hover?.Title);

        // Completion after '$' on a new line after the entry's last field offers only fields the engine reads later.
        var schemaFields = section.Schema?.Fields ?? [];
        int IndexOf(string marker) => schemaFields.IndexOf(schemaFields.FirstOrDefault(s => s.Name.Equals(marker.Trim(), StringComparison.OrdinalIgnoreCase))!);
        var lastField = entry.Fields.Last();
        int lastIndex = IndexOf(lastField.Marker);
        var line = doc.Text.GetLineByOffset(Math.Max(lastField.FullSpan.Start, lastField.FullSpan.End - 1));
        doc.Text.Insert(line.EndOffset, "\r\n$");
        controller.Editor.TextArea.Caret.Offset = line.EndOffset + 3;
        var completion = controller.CompletionAtCaret();
        var fields = completion.Items.Where(i => i.Kind == TblCompletionKind.Field).OrderBy(i => i.Priority).ToList();
        var positions = fields.Where(f => !f.Label.Equals(nameField.Marker, StringComparison.OrdinalIgnoreCase)).Select(f => IndexOf(f.Label)).ToList();
        ctx.Log($"completion after $ following {lastField.Marker} (engine index {lastIndex}): " + string.Join(", ", fields.Take(6).Select(f => f.Label)));
        ctx.Check(positions.Count > 0 && positions.All(p => p > lastIndex), "completion after $ in a weapons entry offers only fields the engine reads after the last one");
        ctx.Check(positions.SequenceEqual(positions.Order()), "the fields are listed in engine order");
        var window = controller.ShowCompletion();
        Pump(100);
        ctx.Check(window is not null, "Ctrl+Space opens the completion list");
        window?.Close();
        doc.Undo();
    }

    private static string TempFile(string name) => Path.Combine(Path.GetTempPath(), "Cairn", "tbl-selftest", name);

    [SelfTest("tbl.editor.save-preserves-bytes")]
    public static void SavePreservesBytes(SelfTestContext ctx)
    {
        var module = Module(ctx);
        if (module is null || StockBytes("ammo.tbl") is not { } ammo) { ctx.Skip("no stock tables"); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(TempFile("x"))!);
        string crlf = Encoding.Latin1.GetString(ammo);
        var variants = new (string Name, byte[] Bytes)[]
        {
            ("ansi-crlf.tbl", ammo),
            ("lf.tbl", Encoding.Latin1.GetBytes(LineEndings.Normalize(crlf, LineEndingKind.Lf))),
            ("utf8-bom.tbl", [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("// été 中\r\n" + crlf)]),
            ("latin-mixed.tbl", Encoding.Latin1.GetBytes("// café\r\n#Foo\n$Name: \"x\"\r\n#End\r\n")),
        };
        foreach (var (name, bytes) in variants)
        {
            string source = TempFile("src-" + name), target = TempFile("out-" + name);
            File.WriteAllBytes(source, bytes);
            using var doc = (UiDocument)module.Kind.Open(source);
            ctx.Check(!doc.IsDirty, $"{name}: opens clean ({doc.Encoding}, {doc.LineEnding})");
            doc.SaveTo(target);
            ctx.Check(File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes), $"{name}: saved byte-for-byte unchanged");
        }
    }

    [SelfTest("tbl.editor.bom-quick-fix")]
    public static void BomQuickFix(SelfTestContext ctx)
    {
        var module = Module(ctx);
        if (module is null || StockBytes("ammo.tbl") is not { } ammo) { ctx.Skip("no stock tables"); return; }
        byte[] withBom = [0xEF, 0xBB, 0xBF, .. ammo];
        using var doc = OpenBytes(module, withBom, "ammo.tbl");
        WaitModel(doc);
        var bom = doc.Model.Diagnostics.FirstOrDefault(d => d.Code == TblRules.ByteOrderMark.Code);
        var fix = bom is null ? null : UiDocument.FixesFor(bom).FirstOrDefault();
        ctx.Check(bom is not null && fix?.Title == UiDocument.SaveWithoutBomTitle, "TBL010 with 'Save without byte-order mark'");
        if (bom is null || fix is null) return;
        doc.ApplyQuickFix(bom, fix);
        WaitModel(doc);
        ctx.Check(doc.Encoding == TblFileEncoding.Utf8 && doc.IsDirty && !doc.Model.Diagnostics.Any(d => d.Code == TblRules.ByteOrderMark.Code),
            "the fix switches to UTF-8 without BOM, marks dirty and clears TBL010");
        Directory.CreateDirectory(Path.GetDirectoryName(TempFile("x"))!);
        string target = TempFile("bom-fixed.tbl");
        doc.SaveTo(target);
        var saved = File.ReadAllBytes(target);
        ctx.Check(saved.AsSpan().SequenceEqual(ammo) && !doc.IsDirty, "saved without the BOM, everything else unchanged");
    }

    [SelfTest("tbl.editor.recovery")]
    public static void Recovery(SelfTestContext ctx)
    {
        var module = Module(ctx);
        if (module is null) { ctx.Skip("no table module"); return; }
        string text = "// récupération\r\n#Foo\r\n$Name: \"x\"\r\n#End\r\n";
        using var doc = OpenBytes(module, TblTextFiles.Encode(text, TblFileEncoding.Latin1), "rec.tbl");
        doc.Text.Insert(0, "// edited\r\n");
        var data = doc.CaptureRecovery();
        ctx.Check(data is not null, "a dirty table captures recovery bytes");
        if (data is null) return;
        using var restored = (UiDocument)module.Kind.Restore(new RecoverySnapshot("id", null, "rec.tbl", DateTime.UtcNow, "tbl", data));
        ctx.Check(restored.Text.Text == doc.Text.Text && restored.Encoding == doc.Encoding && restored.IsDirty,
            $"restore gives the same text and encoding ({restored.Encoding}), dirty");
    }

    [SelfTest("tbl.editor.preview-provider")]
    public static void PreviewProvider(SelfTestContext ctx)
    {
        var module = Module(ctx);
        if (module is null || StockBytes("weapons.tbl") is not { } bytes) { ctx.Skip("no weapons.tbl"); return; }
        var provider = ctx.Shell.Modules.OfType<Cairn.Ui.Modules.IAssetPreviewProvider>().FirstOrDefault(p => p.CanPreview("weapons.tbl"));
        ctx.Check(provider is TblModule, "the table module previews .tbl for other modules");
        WeakReference weak = Create(ctx, module, bytes);
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); Pump(50); }
        ctx.Check(!weak.IsAlive, "a disposed preview is collectable");

        static WeakReference Create(SelfTestContext ctx, TblModule module, byte[] bytes)
        {
            // Created and disposed straight away (as when arrowing through a list), then one shown until parsed.
            ((IDisposable)module.CreatePreview(bytes, "weapons.tbl")!).Dispose();
            var view = (TblPreviewView)module.CreatePreview(bytes, "weapons.tbl")!;
            var window = new System.Windows.Window { Content = view, Width = 600, Height = 400, ShowActivated = false, ShowInTaskbar = false, Left = -10000 };
            window.Show();
            for (int i = 0; i < 100 && view.Controller.Model is null; i++) Pump(50);
            ctx.Check(view.Controller.Model is { } m && m.Classes.Length > 0 && view.Editor.IsReadOnly, "the preview is read-only and highlighted");
            ctx.Check(view.Controller.Foldings.Count > 0, $"the preview folds ({view.Controller.Foldings.Count} regions)");
            window.Content = null;
            window.Close();
            view.Dispose();
            return new WeakReference(view);
        }
    }
}
