using Cairn.Ui.Diagnostics;
using Cairn.Ui.Services;
using Cairn.Vpp.Editing;
using Cairn.Vpp.Model;
using Cairn.Vpp.Ui.Commands;
using Cairn.Vpp.Ui.Documents;
using Cairn.Vpp.Ui.Work;
using Cairn.Vpp.Writing;
using Cairn.Workspace;

namespace Cairn.Vpp.Ui.Diagnostics;

/// <summary>Self-tests for the independent review's data-safety findings (work folder clean-up, extraction, recovery).</summary>
public static class VppSafetySelfTests
{
    private static string NewFolder() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "cairn-vpp-safety-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    private static string Packfile(string folder, string name, params (string Name, int Size)[] entries)
    {
        var items = entries.Select(e => (e.Name, (VppSource)new MemorySource(Enumerable.Range(0, e.Size).Select(i => (byte)(i * 7 + e.Name.Length)).ToArray())));
        string path = Path.Combine(folder, name);
        VppSaver.Save(VppEdit.AddSources(VppPackage.Empty, [.. items], VppClashPolicy.KeepBoth).Package, path, null, CancellationToken.None, VppSaveOptions.Default);
        return path;
    }

    [SelfTest("vpp.work-root-cleanup")]
    public static void WorkRootCleanup(SelfTestContext ctx)
    {
        string temp = NewFolder();
        try
        {
            // The user points "Folder for work copies" at their mod folder.
            string mod = Directory.CreateDirectory(Path.Combine(temp, "MyMod", "levels")).FullName;
            File.WriteAllText(Path.Combine(mod, "precious.rfl"), "months of work");
            Directory.SetLastWriteTimeUtc(mod, DateTime.UtcNow.AddDays(-30));
            string myMod = Path.Combine(temp, "MyMod");
            string root = VppWorkRoot.Resolve(myMod);
            ctx.Check(string.Equals(root, Path.Combine(myMod, VppWorkRoot.FolderName), StringComparison.OrdinalIgnoreCase), $"a chosen folder is the parent of Cairn's own folder ({root})");
            VppWorkRoot.RemoveStale(myMod);
            ctx.Check(File.Exists(Path.Combine(mod, "precious.rfl")), "the sweep never deletes anything in a folder Cairn did not create");
            string own = VppWorkRoot.CreateFolder(root, "doc1");
            string foreign = Directory.CreateDirectory(Path.Combine(root, "not-cairns")).FullName;
            File.WriteAllText(Path.Combine(foreign, "keep.txt"), "x");
            Directory.SetLastWriteTimeUtc(own, DateTime.UtcNow.AddDays(-3));
            Directory.SetLastWriteTimeUtc(foreign, DateTime.UtcNow.AddDays(-3));
            int removed = VppWorkRoot.RemoveStale(root);
            ctx.Check(removed == 1 && !Directory.Exists(own), "a stale work folder Cairn created (marked) is removed");
            ctx.Check(File.Exists(Path.Combine(foreign, "keep.txt")), "an unmarked folder inside Cairn's root is kept");
            ctx.Check(!VppWorkRoot.TryDeleteOwnFolder(mod) && Directory.Exists(mod), "deleting a folder Cairn did not create is refused");
            ctx.Check(VppWorkRoot.Validate("work") is not null, "a relative path is refused");
            ctx.Check(VppWorkRoot.Validate(Path.GetPathRoot(temp)) is not null, "a drive root is refused");
            ctx.Check(VppWorkRoot.Validate(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) is not null, "the Windows folder is refused");
            File.WriteAllText(Path.Combine(temp, "RF.exe"), "");
            ctx.Check(VppWorkRoot.Validate(temp) is not null, "the game folder is refused");
            ctx.Check(VppWorkRoot.Resolve("work") == VppWorkRoot.Default, "a refused setting falls back to the default root");
        }
        finally { VppWorkFolder.TryDeleteFolder(temp); }
    }

    [SelfTest("vpp.extract-names-and-truncation")]
    public static async Task ExtractNamesAndTruncation(SelfTestContext ctx)
    {
        var module = ctx.Shell.Modules.OfType<VppModule>().First();
        string temp = NewFolder();
        try
        {
            foreach (var device in new[] { "con.foo.tga", "nul .tga", "COM¹.wav", "conout$.txt", "lpt1.x.y" })
                ctx.Check(VppWorkFolder.SafeFileName(device).StartsWith('_'), $"'{device}' is a device name and gets a '_' prefix");
            ctx.Check(VppWorkFolder.SafeFileName("console.tga") == "console.tga", "an ordinary name is kept");

            string path = Packfile(temp, "names.vpp", ("Ü.tga", 10), ("ü.tga", 11), ("a?.tga", 12), ("a_.tga", 13), ("con.foo.tga", 14));
            var doc = (VppDocument)module.Kind.Open(path);
            try
            {
                string outDir = Path.Combine(temp, "out");
                var written = await doc.Commands.ExtractAsync(doc.Current.Items, outDir, VppOverwritePolicy.Overwrite, quiet: true);
                int files = Directory.GetFiles(outDir).Length;
                ctx.Check(written?.Count == 5 && files == 5 && written.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 5,
                    $"five entries whose names collide on Windows give five files, none overwritten ({files} files)");
                ctx.Check(File.Exists(Path.Combine(outDir, "_con.foo.tga")), "the device-named entry is written as a real file");
            }
            finally { doc.Dispose(); }

            string truncated = Packfile(temp, "cut.vpp", ("a.tbl", 5000));
            // cut BEFORE opening: a packfile cut after opening is refused earlier as "changed on disk" (finding 12)
            using (var fs = new FileStream(truncated, FileMode.Open)) fs.SetLength(4096 + 1000);
            var cut = (VppDocument)module.Kind.Open(truncated);
            try
            {
                string outDir = Path.Combine(temp, "cut");
                Exception? error = null;
                try { await cut.Commands.ExtractAsync(cut.Current.Items, outDir, VppOverwritePolicy.Overwrite, quiet: true); }
                catch (IOException ex) { error = ex; }
                ctx.Check(error is not null && error.Message.Contains("a.tbl"), $"a short entry of a truncated packfile fails with an error naming it ({error?.Message})");
                ctx.Check(!Directory.Exists(outDir) || Directory.GetFiles(outDir).Length == 0, "no short file (and no part file) is left");
            }
            finally { cut.Dispose(); }
        }
        finally { VppWorkFolder.TryDeleteFolder(temp); }
    }

    [SelfTest("vpp.recovery-partial-prompt")]
    public static void RecoveryPartialPrompt(SelfTestContext ctx)
    {
        var module = ctx.Shell.Modules.OfType<VppModule>().First();
        var service = ctx.Shell.Dialogs as DialogService;
        if (service is null) { ctx.Skip("needs the shell's dialog service"); return; }
        string temp = NewFolder();
        var previous = service.NonInteractiveChoice;
        try
        {
            string path = Packfile(temp, "r.vpp", ("big.tga", 1000), ("keep.tbl", 100));
            var pkg = VppEdit.Replace(VppPackage.Open(path), "big.tga", new MemorySource(new byte[2 << 20]));
            byte[] data = VppRecoveryManifest.Capture(pkg).ToJson(); // no side file: the large data is not restorable
            var snapshot = new RecoverySnapshot(Guid.NewGuid().ToString("N"), path, "r.vpp", DateTime.UtcNow, "vpp", data);
            string? asked = null;
            service.NonInteractiveChoice = (text, buttons) => { asked = text; return 0; };
            var doc = (VppDocument)module.Kind.Restore(snapshot);
            try
            {
                ctx.Check(asked is not null && asked.Contains("partly recovered"), $"the user is asked before anything is restored ({asked})");
                ctx.Check(doc.Notice?.Contains("big.tga") == true, $"the missing entry is named ({doc.Notice})");
                ctx.Check(doc.FilePath is null && doc.Current.Path is null, "the rest is a NEW packfile (Save As), never written over the original");
                ctx.Check(doc.Current.Count == 1 && doc.Current.Contains("keep.tbl"), "the restorable entries are kept");
            }
            finally { doc.Dispose(); }

            service.NonInteractiveChoice = (_, _) => 1;
            bool discarded = false;
            try { module.Kind.Restore(snapshot).Dispose(); } catch (OperationCanceledException) { discarded = true; }
            ctx.Check(discarded, "Discard restores nothing");
            ctx.Check(VppPackage.Open(path).Find("big.tga")?.Size == 1000, "the packfile on disk is untouched");
        }
        finally
        {
            service.NonInteractiveChoice = previous;
            VppWorkFolder.TryDeleteFolder(temp);
        }
    }
}
