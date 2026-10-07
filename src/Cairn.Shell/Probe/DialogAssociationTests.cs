using System.Windows;
using System.Windows.Controls;
using Cairn.Shell.Dialogs;
using Cairn.Shell.Probe;
using Cairn.Ui.Diagnostics;
using Cairn.Ui.Modules;
using Microsoft.Win32;
using Kind = Cairn.Ui.Services.FileAssociation.Kind;

namespace Cairn.Shell;

/// <summary>
/// File associations over a <see cref="FakeAssociationStore"/>: nothing here reads or writes the real
/// registry except the final checks that <c>HKCU\Software\Classes</c> was left alone.
/// </summary>
public static class DialogAssociationTests
{
    private const RegistryHive User = RegistryHive.CurrentUser;
    private const RegistryHive Machine = RegistryHive.LocalMachine;

    private static string C(string path) => $@"{AssociationRegistry.ClassesPath}\{path}";

    /// <summary>Fakes another program: ProgID <paramref name="progId"/> named <paramref name="name"/>, as <paramref name="extension"/>'s per-user default.</summary>
    private static void FakeOther(FakeAssociationStore store, string extension, string progId, string name, bool makeDefault = true)
    {
        store.Seed(User, C(progId), null, name);
        if (makeDefault) store.Seed(User, C(extension), null, progId);
        store.Seed(User, C($@"{extension}\OpenWithProgids"), progId, string.Empty);
    }

    [SelfTest("shell.associations")]
    public static void Associations(SelfTestContext ctx)
    {
        var single = Kind.For("probe", ".cairnprobe", "Probe document");
        var pairA = Kind.For("pair", ".cairnpa", "Pair document");
        var pairB = Kind.For("pair", ".cairnpb", "Pair document");
        var store = new FakeAssociationStore();
        var registry = new AssociationRegistry(store, [single, pairA, pairB], store.ExecutablePath!);
        AssociationState State(Kind k) => registry.Query(k).State;
        string? Default(string extension) => store.GetString(User, C(extension));
        bool Exists(string path) => store.KeyExists(User, C(path));
        bool Lists(string extension, string progId) => store.GetValueNames(User, C(extension + @"\OpenWithProgids")).Contains(progId);

        FakeOther(store, ".cairnprobe", "Other.App", "Other document");
        var other = registry.Query(single);
        ctx.Check(other is { State: AssociationState.OtherApp, OtherProgId: "Other.App", OtherName: "Other document" }, "another program's association is reported with its name");
        ctx.Check(State(pairA) == AssociationState.NotRegistered, "an untouched extension is not registered");

        ctx.Check(registry.Register([single]) is null && State(single) == AssociationState.Cairn, "register one extension");
        ctx.Check(Lists(".cairnprobe", "Cairn.probe") && Exists(@"Cairn.probe\shell\open\command") && Exists(@"Cairn.probe\DefaultIcon"),
            "registration writes the ProgID, command, icon and OpenWithProgids");
        ctx.Check(store.GetString(User, C(@"Cairn.probe\shell\open\command")) == $"\"{store.ExecutablePath}\" \"%1\"", "the command passes the file as \"%1\"");
        ctx.Check(store.Notifications == 1, "a change notifies the shell");

        ctx.Check(registry.Remove([single]) is null && Default(".cairnprobe") == "Other.App", "remove puts the previous program back");
        ctx.Check(!Exists("Cairn.probe") && !Lists(".cairnprobe", "Cairn.probe") && Lists(".cairnprobe", "Other.App"),
            "remove deletes our ProgID and only our OpenWithProgids entry");

        ctx.Check(registry.Register(registry.Kinds) is null && registry.Kinds.All(k => State(k) == AssociationState.Cairn), "register all");
        ctx.Check(registry.Remove([pairA]) is null && State(pairA) == AssociationState.NotRegistered && !Exists(".cairnpa"),
            "removing one extension of a kind clears it and drops its emptied key");
        ctx.Check(Exists("Cairn.pair") && State(pairB) == AssociationState.Cairn, "a ProgID another extension still uses is kept");
        ctx.Check(registry.Remove([pairB]) is null && !Exists("Cairn.pair"), "the ProgID goes with its last extension");

        store.Seed(User, C(@"Cairn.probe\shell\open\command"), null, "\"elsewhere.exe\" \"%1\"");
        ctx.Check(State(single) == AssociationState.OtherCairn, "a Cairn ProgID pointing at another executable is another copy");
        ctx.Check(registry.Register([single]) is null && State(single) == AssociationState.Cairn, "registering again takes it back");

        store.Seed(User, C(".cairnprobe"), null, "Later.App");
        ctx.Check(registry.Remove([single]) is null && Default(".cairnprobe") == "Later.App", "remove leaves an extension that now opens elsewhere alone");
        ctx.Check(registry.Register([single]) is null, "register again before the old-app check");

        FakeOther(store, ".rfa", "RFAWorkbench.rfa", "Red Faction animation clip");
        FakeOther(store, ".v3c", "Someone.v3c", "Someone's mesh");
        FakeOther(store, ".v3c", "RFAWorkbench.v3c", "Red Faction character mesh", makeDefault: false);
        store.Seed(User, C("ATXWorkbench.atx"), null, "Animated texture");
        ctx.Check(registry.HasOldApps(), "old RFA/ATX Workbench associations are found");
        ctx.Check(registry.RemoveOldApps() is null && !registry.HasOldApps(), "remove old RFA/ATX Workbench associations");
        ctx.Check(!Exists(".rfa") && !Exists("RFAWorkbench.rfa") && !Exists("RFAWorkbench.v3c") && !Exists("ATXWorkbench.atx"),
            "the old ProgIDs and the defaults pointing at them are gone");
        ctx.Check(Default(".v3c") == "Someone.v3c" && Lists(".v3c", "Someone.v3c") && !Lists(".v3c", "RFAWorkbench.v3c"),
            "an extension owned by another program keeps its default");
        ctx.Check(State(single) == AssociationState.Cairn, "removing the old apps leaves Cairn's own associations");

        ctx.Check(AssociationRegistry.ExecutableOf("\"C:\\A B\\x.exe\" \"%1\"") == @"C:\A B\x.exe" && AssociationRegistry.ExecutableOf(@"C:\Tools\y.exe %1") == @"C:\Tools\y.exe",
            "the program is read from quoted and unquoted command lines");

        using var real = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.cairnprobe");
        ctx.Check(real is null, "the real HKCU\\Software\\Classes was not written");
    }

    /// <summary>Kinds and an importer with extensions nothing else uses, for the page tests.</summary>
    private sealed class AssocTestModule : ModuleBase
    {
        public override string Id => "assoctest";
        public override string DisplayName => "Association test";
        public override IReadOnlyList<IDocumentKind> DocumentKinds { get; } =
        [
            new ProbeKind("ta", "Type A", ".cairnta"), new ProbeKind("tb", "Type B", ".cairntb"),
            new ProbeKind("tc", "Type C", ".cairntc"), new ProbeKind("td", "Type D", ".cairntd"),
        ];
        public override IReadOnlyList<IFileImporter> Importers { get; } = [new TestImporter()];
    }

    private sealed class TestImporter : IFileImporter
    {
        public string DisplayName => "Test bitmap as type A";
        public IReadOnlyList<string> Extensions { get; } = [".cairnti", ".gltf", ".cairnta"];
        public string FileFilter => "Test bitmaps (*.cairnti)|*.cairnti";
        public int Probe(string path) => 0;
        public void Import(string path) { }
    }

    [SelfTest("shell.associations-page")]
    public static void AssociationsPage(SelfTestContext ctx)
    {
        var shell = (ShellViewModel)ctx.Shell;

        // Rows for every kind the shell has (the probe kinds included), grouped by module, each extension once.
        var shellRows = AssociationsModel.RowsFor(shell.Modules);
        var extensions = shell.Kinds.SelectMany(k => k.Extensions).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        ctx.Check(extensions.All(e => shellRows.Count(r => string.Equals(r.Extension, e, StringComparison.OrdinalIgnoreCase)) == 1),
            $"one row per extension of every registered kind ({extensions.Count})");
        ctx.Check(shellRows.FirstOrDefault(r => r.Extension == ".cairnprobe") is { Group: "Probe", Kind.ProgId: "Cairn.probe" }
            && shellRows.Any(r => r.Extension == ".cairnprobe2"), "the probe kinds are listed under their module, ProgID Cairn.<kindId>");
        ctx.Check(!shellRows.Any(r => r.Extension is ".gltf" or ".glb"), "general formats from importers are not offered");

        var store = new FakeAssociationStore();
        var exe = store.ExecutablePath!;
        var module = new AssocTestModule();
        // .cairnta: nothing. .cairntb: another program per user. .cairntc: the user's own choice in Windows.
        // .cairntd: a machine-wide program, and Cairn registered for the user.
        store.Seed(User, C(".cairntb"), null, "Other.Editor");
        store.Seed(User, C(@"Other.Editor\shell\open\command"), null, @"""C:\Tools\other.exe"" ""%1""");
        store.Descriptions[@"C:\Tools\other.exe"] = "Other Editor";
        store.Seed(User, $@"{AssociationRegistry.FileExtsPath}\.cairntc\UserChoice", "ProgId", @"Applications\viewer.exe");
        store.Seed(Machine, C(@"Applications\viewer.exe\shell\open\command"), null, @"C:\Viewer\viewer.exe %1");
        store.Descriptions[@"C:\Viewer\viewer.exe"] = "Picture Viewer";
        store.Seed(Machine, C(".cairntd"), null, "Machine.Thing");
        store.Seed(Machine, C("Machine.Thing"), null, "Machine thing file");
        var setup = new AssociationRegistry(store, [Kind.For("td", ".cairntd", "Cairn type d")], exe);
        setup.Register([Kind.For("td", ".cairntd", "Cairn type d")]);
        store.ResetCounters();

        var model = new AssociationsModel(store, [module], shell.Dialogs);
        model.Refresh(keepPending: false);
        AssociationRow Row(string extension) => model.Rows.First(r => r.Extension == extension);
        ctx.Check(model.Rows.Select(r => r.Extension).SequenceEqual([".cairnta", ".cairntb", ".cairntc", ".cairntd", ".cairnti"]),
            "kinds first, then an importer's own extension; general formats and kind extensions are not repeated");
        ctx.Check(Row(".cairnti") is { Group: "Association test", Kind.ProgId: "Cairn.import.cairnti", Kind.FriendlyName: "Test bitmaps" }, "the importer row");
        ctx.Check(Row(".cairnta") is { OwnerText: "None", Owner.Source: OwnerSource.None, OpenWithCairn: false }, "unowned extension: None");
        ctx.Check(Row(".cairntb") is { OwnerText: "Other Editor", Owner.Source: OwnerSource.UserClasses, IsProtected: false }, "owned by another program via HKCU Classes: its friendly name");
        ctx.Check(Row(".cairntc") is { OwnerText: "Picture Viewer", Owner.Source: OwnerSource.UserChoice, IsProtected: true, HasNote: false },
            "owned via UserChoice: the chosen program's name, protected");
        ctx.Check(Row(".cairntd") is { OwnerText: "Cairn", Owner.IsCairn: true, OpenWithCairn: true, IsRegistered: true }, "registered extension: Cairn, ticked");
        ctx.Check(store.Writes == 0, "loading the page writes nothing");

        // Cancel: the settings dialog's model with the fake store; tick everything, cancel.
        var settings = new SettingsViewModel(shell, store);
        ctx.Check(settings.Pages.FirstOrDefault() is AssociationsPage { Title: AssociationsModel.PageTitle }, "the File associations page comes first, right after General");
        settings.Associations.Load();
        settings.Associations.Model.SelectAllCommand.Execute(null);
        ctx.Check(settings.Associations.Model.Rows.All(r => r.OpenWithCairn), "Select all ticks every row");
        settings.Associations.Model.SelectNoneCommand.Execute(null);
        ctx.Check(settings.Associations.Model.Rows.All(r => !r.OpenWithCairn), "Select none unticks every row");
        settings.Revert();
        ctx.Check(store.Writes == 0 && store.Notifications == 0, "Cancel writes nothing");

        // Apply only what changed: tick A and C, untick D, leave B alone.
        Row(".cairnta").OpenWithCairn = true;
        Row(".cairntc").OpenWithCairn = true;
        Row(".cairntd").OpenWithCairn = false;
        ctx.Check(Row(".cairntc") is { CanChooseDefault: true, HasNote: true }, "ticking a protected row explains it and offers Choose default");
        ctx.Check(model.Apply() is null, "apply succeeds");
        ctx.Check(store.WrittenPaths.All(p => p.Contains("cairnta", StringComparison.OrdinalIgnoreCase) || p.Contains("Cairn.ta", StringComparison.Ordinal)
                || p.Contains("cairntc", StringComparison.OrdinalIgnoreCase) || p.Contains("Cairn.tc", StringComparison.Ordinal)
                || p.Contains("cairntd", StringComparison.OrdinalIgnoreCase) || p.Contains("Cairn.td", StringComparison.Ordinal)),
            "only the changed rows' keys are written");
        ctx.Check(store.GetString(User, C(".cairnta")) == "Cairn.ta" && Row(".cairnta") is { OwnerText: "Cairn", IsRegistered: true, IsChanged: false }, "a ticked row is registered");
        ctx.Check(store.GetString(User, C(".cairntb")) == "Other.Editor" && !store.KeyExists(User, C("Cairn.tb")), "an unchanged row is untouched");
        ctx.Check(!store.KeyExists(User, C("Cairn.td")) && !store.KeyExists(User, C(".cairntd")) && Row(".cairntd") is { OwnerText: "Machine thing file", IsRegistered: false },
            "an unticked row is unregistered and the machine default shows again");
        ctx.Check(store.GetString(User, $@"{AssociationRegistry.FileExtsPath}\.cairntc\UserChoice", "ProgId") == @"Applications\viewer.exe"
                && store.GetString(User, C(".cairntc")) is null
                && store.GetValueNames(User, C(@".cairntc\OpenWithProgids")).Contains("Cairn.tc")
                && store.KeyExists(User, C(@"Cairn.tc\shell\open\command")),
            "a protected row: UserChoice and the default untouched, Cairn added to Open with");
        ctx.Check(Row(".cairntc") is { IsRegistered: true, OwnerText: "Picture Viewer", CanChooseDefault: true, HasNote: true }, "the protected row still offers Choose default");
        ctx.Check(store.Notifications > 0, "the shell is notified of the change");
        ctx.Check(!store.WrittenPaths.Any(p => p.Contains("FileExts", StringComparison.OrdinalIgnoreCase)), "UserChoice is never written");

        model.ChooseDefaultCommand.Execute(Row(".cairntc"));
        ctx.Check(store.ChooserCalls.SequenceEqual([".cairntc"]), "Choose default opens Windows' chooser for that extension");

        int before = store.Writes;
        ctx.Check(model.Apply() is null && store.Writes == before, "applying again with nothing changed writes nothing");

        // Tools > File Associations opens the settings dialog at this page.
        var dialog = SettingsDialog.CreateForCapture(shell, AssociationsModel.PageTitle, store);
        ctx.Check(dialog.Tabs.SelectedItem is TabItem { Header: AssociationsModel.PageTitle } && dialog.Tabs.SelectedIndex == 1, "the dialog opens at File associations, the second tab");
        dialog.Close();
        ctx.Check(store.Writes == before, "closing the dialog without OK writes nothing");

        using var real = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.cairnta");
        ctx.Check(real is null, "the real HKCU\\Software\\Classes was not written");
    }

    /// <summary>
    /// <c>--dialog associations</c>: the settings dialog at its File associations page, over a fake registry
    /// holding every state (Cairn, another program, a protected choice with Cairn in "Open with", machine-wide, none).
    /// </summary>
    [ScreenshotDialog("associations")]
    public static async Task<Window?> ShowAssociations(ScreenshotContext ctx)
    {
        var shell = (ShellViewModel)ctx.Shell;
        var store = new FakeAssociationStore();
        var rows = AssociationsModel.RowsFor(shell.Modules);
        var registry = new AssociationRegistry(store, [.. rows.Select(r => r.Kind)], store.ExecutablePath!);
        if (rows.Count > 0) registry.Register([rows[0].Kind]);
        if (rows.Count > 1)
        {
            store.Seed(User, C(rows[1].Extension), null, "Notepad.Plus");
            store.Seed(User, C(@"Notepad.Plus\shell\open\command"), null, @"""C:\Tools\notepad++.exe"" ""%1""");
            store.Descriptions[@"C:\Tools\notepad++.exe"] = "Notepad++ : a free (GNU) source code editor";
        }
        if (rows.Count > 2)
        {
            store.Seed(User, $@"{AssociationRegistry.FileExtsPath}\{rows[2].Extension}\UserChoice", "ProgId", @"Applications\viewer.exe");
            store.Seed(Machine, C(@"Applications\viewer.exe\shell\open\command"), null, @"C:\Viewer\viewer.exe %1");
            store.Descriptions[@"C:\Viewer\viewer.exe"] = "Picture Viewer";
            registry.Register([rows[2].Kind], setDefault: false);
        }
        if (rows.Count > 3)
        {
            store.Seed(Machine, C(rows[3].Extension), null, "Studio.File");
            store.Seed(Machine, C("Studio.File"), null, "Studio file");
        }
        store.Seed(User, C("RFAWorkbench.rfa"), null, "Red Faction animation clip");

        var dialog = SettingsDialog.CreateForCapture(shell, AssociationsModel.PageTitle, store);
        dialog.Owner = ctx.MainWindow;
        dialog.Show();
        await Task.Delay(200);
        await ctx.SettleAsync();
        // --scroll end: the bottom of the page (the last rows and the old-apps section).
        if (ctx.Options.TryGetValue("scroll", out var scroll) && scroll == "end" && dialog.Tabs.SelectedContent is ScrollViewer scroller)
        {
            scroller.ScrollToEnd();
            await ctx.SettleAsync();
        }
        return dialog;
    }
}
