using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Cairn.Previews;
using Cairn.Ui.Diagnostics;
using Cairn.Vpp.Editing;
using Cairn.Vpp.Model;
using Cairn.Vpp.Ui.Details;
using Cairn.Vpp.Ui.Documents;
using Cairn.Vpp.Ui.List;
using Cairn.Vpp.Validation;
using Cairn.Vpp.Writing;

namespace Cairn.Vpp.Ui.Diagnostics;

/// <summary>
/// Self-tests of long asset names (VPP027: the Problems filter, the list marker, "Rename to fit...") and of
/// "Open in Cairn" on plain and module previews of packfile entries. Packfiles are generated in a temp folder.
/// </summary>
internal static class VppNamesSelfTests
{
    private static string Name(int length, string ext) => "n" + new string('x', length - ext.Length - 1) + ext;

    private static string Generate(string folder, string fileName, IEnumerable<(string Name, byte[] Bytes)> entries)
    {
        var package = VppEdit.AddSources(VppPackage.Empty, [.. entries.Select(e => (e.Name, (VppSource)new MemorySource(e.Bytes)))], VppClashPolicy.KeepBoth).Package;
        string path = Path.Combine(folder, fileName);
        VppSaver.Save(package, path, null, CancellationToken.None, VppSaveOptions.Default);
        return path;
    }

    private static string NewFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "cairn-vpp-names-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    [SelfTest("vpp.long-names")]
    public static void LongNames(SelfTestContext ctx)
    {
        var module = ctx.Shell.Modules.OfType<VppModule>().First();
        string folder = NewFolder();
        string n31 = Name(31, ".tga"), n32 = Name(32, ".tga"), n59 = Name(59, ".wav"), mesh = Name(45, ".v3m"), short31 = Name(31, ".wav");
        string path = Generate(folder, "names.vpp", [(n31, [1]), (n32, [2]), (n59, [3]), (mesh, [4]), (short31, [5]), ("plain.tbl", [6])]);
        var doc = (VppDocument)module.Kind.Open(path);
        try
        {
            var fired = doc.Problems.Where(p => p.Code == "VPP027").Select(p => p.EntryName!).Order(StringComparer.Ordinal).ToList();
            ctx.Check(fired.SequenceEqual(new[] { n32, n59 }.Order(StringComparer.Ordinal)), $"VPP027 fires for the 32- and 59-character texture/sound names only ({string.Join(", ", fired)})");
            ctx.Check(doc.Problems.All(p => p.Code != "VPP027" || p.Severity == VppSeverity.Warning), "VPP027 is a warning");

            var rows = doc.List.AllRows;
            ctx.Check(rows.Where(r => r.NameTooLong).Select(r => r.Name).Order(StringComparer.Ordinal).SequenceEqual(fired), "the list rows flag exactly those");
            var row32 = rows.First(r => r.Name == n32);
            ctx.Check(row32.ProblemGlyph.Length > 0 && row32.StateText == "name too long" && row32.ProblemToolTip?.Contains("31", StringComparison.Ordinal) == true,
                $"a flagged row shows the marker and 'name too long' ({row32.StateText})");
            ctx.Check(rows.First(r => r.Name == n31).StateText.Length == 0 && rows.First(r => r.Name == mesh).ProblemGlyph.Length == 0, "31 characters and a long mesh name are not flagged");

            var details = VppDetailsPane.Build(doc.Current.Find(n32)!, doc.Current, doc.Problems, null, CancellationToken.None);
            ctx.Check(details.Rows.Any(r => r.Label == "Problems" && r.Value.Contains("VPP027", StringComparison.Ordinal)), "the details pane's Problems row shows VPP027");

            // the File types panel's Problems group
            var panel = VppTypesPanel.For(doc);
            ctx.Check(panel.LongNames.Content is string text && text.Contains("Names longer than 31 characters", StringComparison.Ordinal) && text.Contains("(2)", StringComparison.Ordinal),
                $"the panel offers 'Names longer than 31 characters (2)' ({panel.LongNames.Content})");
            panel.LongNames.IsChecked = true; // as a click would
            var visible = doc.List.Visible.Select(r => r.Name).Order(StringComparer.Ordinal).ToList();
            ctx.Check(visible.SequenceEqual(fired), $"ticking it lists exactly the long names ({string.Join(", ", visible)})");
            ctx.Check(doc.List.IsFiltered, "... and counts as a filter");
            doc.List.ShowOnlyTypes([".wav"]);
            ctx.Check(doc.List.Visible.Select(r => r.Name).SequenceEqual([n59]), "combined with the .wav type filter only the long sound remains");
            doc.List.ShowOnlyTypes([]);
            panel.LongNames.IsChecked = false;
            ctx.Check(doc.List.Visible.Count == rows.Count, "unticking lists every entry again");

            // Rename to fit: one undo step for the selection
            doc.SelectNames([n32, n59, n31]);
            ctx.Check(doc.Commands.CanRenameToFit, "Rename to fit is offered for a selection with long names");
            byte[] before = doc.Serialize();
            var done = doc.Commands.RenameToFit(confirm: false);
            ctx.Check(done.Count == 2 && done.All(r => r.NewName.Length <= VppValidator.MaxAssetNameLength), $"two entries renamed to 31 characters or fewer ({string.Join(", ", done.Select(d => d.NewName))})");
            ctx.Check(done.All(r => Path.GetExtension(r.NewName) == Path.GetExtension(r.OldName)), "extensions are kept");
            ctx.Check(!doc.Problems.Any(p => p.Code == "VPP027") && doc.List.LongNameOption.Count == 0, "no long names remain");
            ctx.Check(panel.LongNames.Visibility == System.Windows.Visibility.Collapsed, "... and the panel's long-names box is hidden");
            ctx.Check(doc.UndoLabel?.Contains("to fit", StringComparison.Ordinal) == true, $"one undo step ({doc.UndoLabel})");
            byte[] after = doc.Serialize();
            doc.Undo();
            ctx.Check(doc.Serialize().AsSpan().SequenceEqual(before) && doc.Current.Contains(n32) && doc.Current.Contains(n59), "one Undo restores both names");
            doc.Redo();
            ctx.Check(doc.Serialize().AsSpan().SequenceEqual(after), "Redo renames both again");
            doc.Undo();
            doc.SelectNames([n31, mesh]);
            ctx.Check(!doc.Commands.CanRenameToFit && doc.Commands.RenameToFit(confirm: false).Count == 0, "nothing to do for names that fit");

            // proposals avoid names already taken
            string taken = VppEdit.ShortName(n32, _ => false);
            ctx.Check(VppEdit.ShortName(n32, n => n.Equals(taken, StringComparison.OrdinalIgnoreCase)) is { Length: <= 31 } other && other != taken && other.EndsWith(".tga", StringComparison.Ordinal),
                "a taken proposal gets a ~1 suffix within 31 characters");
        }
        finally
        {
            doc.Dispose();
            Work.VppWorkFolder.TryDeleteFolder(folder);
        }
    }

    [SelfTest("vpp.preview-open-in-cairn")]
    public static async Task PreviewOpenInCairn(SelfTestContext ctx)
    {
        var module = ctx.Shell.Modules.OfType<VppModule>().First();
        if (!module.CairnOpens("x.tbl")) { ctx.Skip("needs the table module in the build"); return; }
        string folder = NewFolder();
        byte[] table = Encoding.ASCII.GetBytes("#General\r\n$Name: \"cairn test\"\r\n#End\r\n");
        string path = Generate(folder, "open.vpp", [("cairn_test.tbl", table), ("other.txt", Encoding.ASCII.GetBytes("plain text\r\n"))]);
        var doc = (VppDocument)module.Kind.Open(path);
        ctx.Shell.AddDocument(doc);
        IDisposableDoc? opened = null;
        try
        {
            doc.SelectNames(["cairn_test.tbl"]);
            var pane = await WaitForPreviewAsync(doc, "cairn_test.tbl");
            ctx.Check(pane is not null, "the packfile tab previews the .tbl entry");
            if (pane is null) return;
            ctx.Log($"  .tbl preview kind: {pane.Kind}");
            var button = FindButton(pane.View, "Open in Cairn");
            ctx.Check(button is not null, $"the .tbl preview has an 'Open in Cairn' button ({pane.Kind})");
            if (button is null) return;
            var known = ctx.Shell.Documents.ToHashSet();
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Cairn.Ui.Documents.IDocument? table2 = null;
            for (int i = 0; i < 100 && table2 is null; i++)
            {
                await Task.Delay(100);
                table2 = ctx.Shell.Documents.FirstOrDefault(d => !known.Contains(d));
            }
            ctx.Check(table2 is not null, "clicking it opens a new tab");
            if (table2 is null) return;
            opened = new IDisposableDoc(ctx, table2);
            ctx.Check(table2.Kind.Extensions.Contains(".tbl", StringComparer.OrdinalIgnoreCase), $"the tab is the table module's ({table2.Kind.DisplayName})");
            ctx.Check(table2.FilePath is { } p && doc.HasWorkFolder && p.StartsWith(doc.Work.Root, StringComparison.OrdinalIgnoreCase),
                $"it opened the packfile's work copy ({table2.FilePath})");
        }
        finally
        {
            opened?.Dispose();
            if (ctx.Shell.Documents.Contains(doc)) ctx.Shell.Close(doc); else doc.Dispose();
            Work.VppWorkFolder.TryDeleteFolder(folder);
        }
    }

    private sealed class IDisposableDoc(SelfTestContext ctx, Cairn.Ui.Documents.IDocument doc) : IDisposable
    {
        public void Dispose() { if (ctx.Shell.Documents.Contains(doc)) ctx.Shell.Close(doc); }
    }

    /// <summary>The tab's preview pane once it shows <paramref name="name"/> (10 s at most).</summary>
    internal static async Task<AssetPreviewPane?> WaitForPreviewAsync(VppDocument doc, string name)
    {
        for (int i = 0; i < 100; i++)
        {
            if (doc.View is VppDocumentView { PreviewHost.Content: Preview.VppPreviewPane pane } && pane.Item?.Name == name
                && pane.Kind is not (AssetPreviewKind.Loading or AssetPreviewKind.Empty))
            {
                await pane.Pending;
                return pane;
            }
            await Task.Delay(100);
        }
        return null;
    }

    internal static Button? FindButton(DependencyObject? root, string text)
    {
        if (root is null) return null;
        if (root is Button { Content: string s } b && s == text) return b;
        if (root is FrameworkElement fe) fe.ApplyTemplate();
        int n = root is Visual ? VisualTreeHelper.GetChildrenCount(root) : 0;
        for (int i = 0; i < n; i++)
            if (FindButton(VisualTreeHelper.GetChild(root, i), text) is { } found) return found;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            if (FindButton(child, text) is { } found) return found;
        return null;
    }
}
