using System.Text.Json;
using System.Windows;
using Cairn.Shell.Dialogs;
using Cairn.Ui.Diagnostics;
using Cairn.Workspace;

namespace Cairn.Shell;

/// <summary>Settings self-tests and the settings dialog capture. Everything runs in temp folders; the real settings file is never touched.</summary>
public static class SettingsSelfTests
{
    /// <summary><c>--dialog settings</c>: the dialog, shown modelessly over the main window and never applied. <c>--settings-page title</c> picks the page.</summary>
    [ScreenshotDialog("settings")]
    public static async Task<Window?> ShowSettings(ScreenshotContext ctx)
    {
        var shell = (ShellViewModel)ctx.Shell;
        ctx.Options.TryGetValue("settings-page", out var page);
        var dialog = SettingsDialog.CreateForCapture(shell, page);
        dialog.Owner = ctx.MainWindow;
        dialog.Show();
        // The game folder check runs off the UI thread; give it a moment so the status line is final.
        await Task.Delay(400);
        await ctx.SettleAsync();
        return dialog;
    }

    private static string NewTempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Cairn-selftest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void DeleteQuietly(string folder)
    {
        try { Directory.Delete(folder, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [SelfTest("shell.settings-roundtrip")]
    public static void Roundtrip(SelfTestContext ctx)
    {
        var folder = NewTempFolder();
        try
        {
            var path = Path.Combine(folder, "settings.json");
            var s = new AppSettings
            {
                Theme = AppTheme.Dark,
                GameDirectory = Path.Combine(folder, "game"),
                SearchFolders = [Path.Combine(folder, "a"), Path.Combine(folder, "b")],
                RecentFiles = [Path.Combine(folder, "x.rfa"), Path.Combine(folder, "y.v3c")],
            };
            s.Set("probe.number", 42);
            s.Set("probe.list", new[] { "one", "two" });
            ctx.Check(SettingsStore.Save(s, path), "settings saved to the temp path");
            var back = SettingsStore.Load(path);
            ctx.Check(back.Theme == AppTheme.Dark, "theme survives");
            ctx.Check(back.GameDirectory == s.GameDirectory, "game directory survives");
            ctx.Check(back.SearchFolders.SequenceEqual(s.SearchFolders), "search folders survive in order");
            ctx.Check(back.RecentFiles.SequenceEqual(s.RecentFiles), "recent files survive in order");
            ctx.Check(back.Get<int>("probe.number") == 42, "module value (number) survives");
            ctx.Check(back.Get<string[]>("probe.list") is ["one", "two"], "module value (list) survives");

            // The dialog's model edits a copy: cancelling leaves the shell's settings as they were.
            var shell = (ShellViewModel)ctx.Shell;
            var before = JsonSerializer.Serialize(shell.Settings);
            var model = new SettingsViewModel(shell);
            model.GameDirectory = Path.Combine(folder, "elsewhere");
            model.SearchFolders.Add(Path.Combine(folder, "c"));
            model.Revert();
            ctx.Check(JsonSerializer.Serialize(shell.Settings) == before, "cancelled settings dialog changes nothing");
        }
        finally
        {
            DeleteQuietly(folder);
        }
        ctx.Check(!Directory.Exists(folder), "temp folder removed");
    }

    [SelfTest("shell.first-run-import")]
    public static void FirstRunImportTest(SelfTestContext ctx)
    {
        var root = NewTempFolder();
        try
        {
            var cairn = Path.Combine(root, "Cairn");
            var rfa = Path.Combine(root, "RFAWorkbench");
            var atx = Path.Combine(root, "ATXWorkbench");
            Directory.CreateDirectory(Path.Combine(rfa, "profiles", "sub"));
            Directory.CreateDirectory(atx);
            // The shapes RFA and ATX Workbench write: camelCase, string enums. Paths live under the temp root.
            string P(string name) => Path.Combine(root, name);
            var rfaJson = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["theme"] = "Dark",
                ["gameDirectory"] = P("rf"),
                ["searchFolders"] = new[] { P("one"), P("two") },
                ["recentFiles"] = new[] { P("walk.rfa") },
                ["maxRecentFiles"] = 12,
                ["values"] = new Dictionary<string, object> { ["libraryDoubleClick"] = "newTab", ["viewport"] = new { grid = true }, ["retargetDialogWidth"] = 1300 },
                ["layout"] = new Dictionary<string, object> { ["libraryWidth"] = 310, ["inspectorWidth"] = 330, ["bottomHeight"] = 30 },
                ["panels"] = new Dictionary<string, object> { ["library"] = false, ["inspector"] = true },
            });
            var atxJson = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["theme"] = "Light",
                ["gameDirectory"] = P("other-rf"),
                ["searchFolders"] = new[] { P("TWO"), P("three") },
                ["recentFiles"] = new[] { P("wall.tga"), P("walk.rfa") },
                ["values"] = new Dictionary<string, object> { ["atxOnly"] = 1 },
                ["layout"] = new Dictionary<string, object> { ["leftPane"] = 250, ["problemsHeight"] = 180, ["settingsShare"] = 0.4 },
                ["panels"] = new Dictionary<string, object> { ["problems"] = false, ["textureSettings"] = true },
            });
            var rfaSettings = Path.Combine(rfa, "settings.json");
            var atxSettings = Path.Combine(atx, "settings.json");
            var profile = Path.Combine(rfa, "profiles", "sub", "human.json");
            File.WriteAllText(rfaSettings, rfaJson);
            File.WriteAllText(atxSettings, atxJson);
            File.WriteAllText(profile, "{ \"profile\": 1 }");
            var sources = new[] { rfaSettings, atxSettings, profile }.ToDictionary(p => p, File.ReadAllBytes);

            ctx.Check(FirstRunImport.Run(cairn, rfa, atx), "import ran into the empty Cairn folder");
            var s = SettingsStore.Load(Path.Combine(cairn, "settings.json"));
            ctx.Check(s.Theme == AppTheme.Dark, "theme imported (RFA's first)");
            ctx.Check(s.GameDirectory == P("rf"), "game directory imported (RFA's first)");
            ctx.Check(s.SearchFolders.SequenceEqual([P("one"), P("two"), P("three")]), "search folders merged without duplicates");
            ctx.Check(s.RecentFiles.SequenceEqual([P("walk.rfa"), P("wall.tga")]), "recent files merged without duplicates");
            ctx.Check(s.Get<string>("rfa.libraryDoubleClick") == "newTab", "RFA value carried over with the rfa. prefix");
            ctx.Check(s.Values.TryGetValue("rfa.viewport", out var vp) && vp.GetProperty("grid").GetBoolean(), "nested RFA value carried over");
            ctx.Check(!s.Values.Keys.Any(k => k.Contains("atxOnly", StringComparison.Ordinal)), "ATX values are not imported");
            ctx.Check(s.Get<int>("rfa.retargetDialogWidth") == 1300, "RFA dialog size value carried over with the rfa. prefix");
            ctx.Check(s.Layout.GetValueOrDefault("leftWidth") == 310, "RFA library width -> shell left pane width (RFA first)");
            ctx.Check(s.Layout.GetValueOrDefault("bottomHeight") == 180, "too small RFA bottom height ignored; ATX problems height used");
            ctx.Check(s.Layout.GetValueOrDefault("inspectorWidth") == 330 && s.Layout.GetValueOrDefault("settingsShare") == 0.4, "module layout keys copied as is");
            ctx.Check(!s.Layout.ContainsKey("libraryWidth") && !s.Layout.ContainsKey("leftPane") && !s.Layout.ContainsKey("problemsHeight"), "shell pane keys renamed, not duplicated");
            ctx.Check(s.Panels.GetValueOrDefault("left", true) == false && s.Panels.GetValueOrDefault("bottom", true) == false, "pane visibility mapped (library -> left, problems -> bottom)");
            ctx.Check(s.Panels.GetValueOrDefault("inspector") && s.Panels.GetValueOrDefault("textureSettings"), "module panel flags copied as is");
            var copied = Path.Combine(cairn, "profiles", "sub", "human.json");
            ctx.Check(File.Exists(copied) && File.ReadAllBytes(copied).SequenceEqual(sources[profile]), "RFA profile copied");
            ctx.Check(sources.All(kv => File.ReadAllBytes(kv.Key).SequenceEqual(kv.Value)), "old apps' files unchanged");

            // A second run must not overwrite settings Cairn already has.
            File.WriteAllText(Path.Combine(cairn, "settings.json"), "{ \"theme\": \"Light\" }");
            ctx.Check(!FirstRunImport.Run(cairn, rfa, atx), "import skipped once Cairn has settings");
            ctx.Check(SettingsStore.Load(Path.Combine(cairn, "settings.json")).Theme == AppTheme.Light, "existing Cairn settings kept");
        }
        finally
        {
            DeleteQuietly(root);
        }
        ctx.Check(!Directory.Exists(root), "temp folder removed");
    }
}
