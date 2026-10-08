using System.Text.Json;
using System.Text.Json.Nodes;
using Cairn.Ui.Modules;
using Cairn.Ui.Services;
using Cairn.Workspace;

namespace Cairn.Shell;

/// <summary>Command-line switches: files to open plus the diagnostics harness options.</summary>
public sealed class CommandLine
{
    public List<string> Files { get; } = [];
    public string? Screenshot { get; private set; }
    public bool SelfTest { get; private set; }
    public string? SelfTestOnly { get; private set; }
    public string? Dialog { get; private set; }
    public (int Width, int Height)? Size { get; private set; }
    public AppTheme? Theme { get; private set; }
    public bool Wait { get; private set; }
    public bool ProbeModule { get; private set; }
    public Dictionary<string, string> ModuleOptions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IsDiagnostic => Screenshot != null || SelfTest || Dialog != null;

    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        var c = new CommandLine();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            string Next() => i + 1 < args.Count ? args[++i] : string.Empty;
            switch (a.ToLowerInvariant())
            {
                case "--screenshot": c.Screenshot = Next(); break;
                case "--selftest": c.SelfTest = true; break;
                case "--selftest-only": c.SelfTest = true; c.SelfTestOnly = Next(); break;
                case "--dialog": c.Dialog = Next(); c.ModuleOptions["dialog"] = c.Dialog; break;
                case "--wait": c.Wait = true; break;
                case "--probe-module": c.ProbeModule = true; break;
                case "--theme": c.Theme = Enum.TryParse<AppTheme>(Next(), true, out var t) ? t : null; break;
                case "--size":
                    var parts = Next().Split('x', 'X');
                    if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h)) c.Size = (w, h);
                    break;
                default:
                    if (a.StartsWith("--", StringComparison.Ordinal))
                        c.ModuleOptions[a[2..]] = i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
                    else c.Files.Add(a);
                    break;
            }
        }
        return c;
    }
}

/// <summary>The modules compiled into this build (see the CairnModules property in the project file).</summary>
public static class ModuleCatalog
{
    public static IReadOnlyList<IModule> Create(bool includeProbe)
    {
        var modules = new List<IModule>();
#if CAIRN_MODULE_ATX
        modules.Add(new Cairn.Atx.Ui.AtxModule());
#endif
#if CAIRN_MODULE_RFA
        modules.Add(new Cairn.Rfa.Ui.RfaModule());
#endif
#if CAIRN_MODULE_VFX
        modules.Add(new Cairn.Vfx.Ui.VfxModule());
#endif
#if CAIRN_MODULE_VPP
        modules.Add(new Cairn.Vpp.Ui.VppModule());
#endif
#if CAIRN_MODULE_TBL
        modules.Add(new Cairn.Tbl.Ui.TblModule());
#endif
#if CAIRN_MODULE_VF
        modules.Add(new Cairn.Vf.Ui.VfModule());
#endif
#if CAIRN_MODULE_VBM
        modules.Add(new Cairn.Vbm.Ui.VbmModule());
#endif
#if CAIRN_MODULE_SND
        modules.Add(new Cairn.Snd.Ui.SndModule());
#endif
        if (includeProbe) { modules.Add(new Probe.ProbeModule()); modules.Add(new Probe.ProbeTwoModule()); }
        return modules;
    }
}

/// <summary>
/// First run: seeds Cairn's settings from the RFA and ATX Workbench settings files and copies RFA's
/// profiles. Runs only while Cairn has no settings.json; the old apps' files are only read.
/// </summary>
public static class FirstRunImport
{
    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonDocumentOptions DocumentOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Imports from <c>%APPDATA%\RFAWorkbench</c> and <c>%APPDATA%\ATXWorkbench</c> into <c>%APPDATA%\Cairn</c>.</summary>
    public static void Run()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Run(SettingsStore.DefaultDirectory, Path.Combine(appData, "RFAWorkbench"), Path.Combine(appData, "ATXWorkbench"));
    }

    /// <summary>
    /// Imports from the given settings folders. Returns true when a Cairn settings.json was written
    /// (false when one already existed or neither old app had settings).
    /// </summary>
    public static bool Run(string cairnFolder, string rfaFolder, string atxFolder)
    {
        var target = Path.Combine(cairnFolder, "settings.json");
        if (File.Exists(target)) return false;
        // Both old apps saved camelCase JSON; the lookups below ignore case so either spelling reads.
        var rfa = Read(Path.Combine(rfaFolder, "settings.json"));
        var atx = Read(Path.Combine(atxFolder, "settings.json"));
        if (rfa is null && atx is null) return false;
        var s = new AppSettings();
        foreach (var old in new[] { rfa, atx })
        {
            if (old is null) continue;
            if (old["theme"] is JsonValue th && s.Theme == AppTheme.System && Enum.TryParse<AppTheme>(th.ToString(), true, out var t)) s.Theme = t;
            if (string.IsNullOrWhiteSpace(s.GameDirectory) && old["gameDirectory"] is JsonValue gd && gd.TryGetValue<string>(out var dir) && dir.Length > 0) s.GameDirectory = dir;
            foreach (var f in Strings(old["searchFolders"])) if (!s.SearchFolders.Contains(f, StringComparer.OrdinalIgnoreCase)) s.SearchFolders.Add(f);
            foreach (var f in Strings(old["recentFiles"])) if (s.RecentFiles.Count < s.MaxRecentFiles && !s.RecentFiles.Contains(f, StringComparer.OrdinalIgnoreCase)) s.RecentFiles.Add(f);
        }
        // RFA's values (dialog sizes, library/viewport/time options...) under the rfa. prefix the RFA module reads.
        if (rfa?["values"] is JsonObject values)
            foreach (var (key, value) in values)
                if (value is not null) s.Values["rfa." + key] = JsonSerializer.Deserialize<JsonElement>(value.ToJsonString());
        ImportLayout(s, rfa, atx);
        if (!SettingsStore.Save(s, target)) return false;
        CopyProfiles(Path.Combine(rfaFolder, "profiles"), Path.Combine(cairnFolder, "profiles"));
        return true;
    }

    /// <summary>
    /// Pane layout: the old keys that mean a shell pane become the shell's keys (RFA library / ATX left pane = left,
    /// RFA bottom panel / ATX problems = bottom); every other old key (inspector width, ATX splits...) is copied as is,
    /// because the ported modules read the same names. RFA wins over ATX; widths of 40 or less are ignored, as the old apps did.
    /// </summary>
    private static void ImportLayout(AppSettings s, JsonObject? rfa, JsonObject? atx)
    {
        var shellLayout = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["libraryWidth"] = "leftWidth", ["bottomHeight"] = "bottomHeight", ["leftPane"] = "leftWidth", ["problemsHeight"] = "bottomHeight",
        };
        var shellPanels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["library"] = "left", ["bottom"] = "bottom", ["problems"] = "bottom" };
        foreach (var old in new[] { rfa, atx })
        {
            if (old?["layout"] is JsonObject layout)
                foreach (var (key, node) in layout)
                {
                    if (node is not JsonValue v || !v.TryGetValue<double>(out var number) || !double.IsFinite(number)) continue;
                    var isShell = shellLayout.TryGetValue(key, out var mapped);
                    if (isShell && number <= 40) continue;
                    s.Layout.TryAdd(isShell ? mapped! : key, number);
                }
            if (old?["panels"] is JsonObject panels)
                foreach (var (key, node) in panels)
                    if (node is JsonValue v && v.TryGetValue<bool>(out var visible))
                        s.Panels.TryAdd(shellPanels.TryGetValue(key, out var mapped) ? mapped : key, visible);
        }
    }

    private static JsonObject? Read(string path)
    {
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path), NodeOptions, DocumentOptions) as JsonObject : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static IEnumerable<string> Strings(JsonNode? node) =>
        node is JsonArray a ? a.OfType<JsonValue>().Select(v => v.ToString()).Where(v => v.Length > 0) : [];

    private static void CopyProfiles(string from, string to)
    {
        try
        {
            if (!Directory.Exists(from)) return;
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(to, Path.GetRelativePath(from, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (!File.Exists(target)) File.Copy(file, target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>The shell's dialog service; modules see it as <see cref="IDialogService"/>.</summary>
public sealed class ShellDialogs : DialogService
{
    /// <summary>The crash report with details, Copy details and Open log.</summary>
    public override void ShowCrash(string body, IReadOnlyList<string> recovered, string details)
    {
        if (Unattended($"Something went wrong. {body} ({details})")) return;
        Dialogs.CrashDialog.Show(Owner, body, recovered, details);
    }
}
