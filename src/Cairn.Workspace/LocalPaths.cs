using System.Text.Json;

namespace Cairn.Workspace;

/// <summary>
/// Where the optional, machine-specific test data lives: the research corpus, a Red Faction install,
/// Alpine Faction's golden retarget clips and REDUX's sample files. Shared by the App's
/// <c>--selftest</c> and the test project (linked there), so both find the same folders the same way.
/// Nothing is hard-coded and nothing is guessed from neighbouring folders. Each location comes from an
/// environment variable or, failing that, from <c>research/local-paths.json</c> in the repository
/// (git-ignored like the rest of <c>research/</c>), found by walking up from the running assembly to
/// the folder holding <c>Cairn.sln</c>:
/// <code>
/// { "gameDirectory": "&lt;game folder&gt;", "goldenClips": "&lt;folder of af_*.rfa&gt;", "reduxResearch": "&lt;REDUX research folder&gt;" }
/// </code>
/// Relative values are taken from the file's own folder. A location that is not set, or names a
/// folder that does not exist, is null: the tests that need it pass trivially and the self-test logs
/// the checks as skipped. The golden clips may name several folders (each generation of the files
/// in its own): a JSON array of strings, or <c>;</c>-separated in <c>CAIRN_GOLDEN_CLIPS</c>; every
/// listed folder that exists is checked.
/// </summary>
public static class LocalPaths
{
    /// <summary>Overrides the research folder (the corpus and <see cref="FileName"/> are read from it).</summary>
    public const string ResearchVariable = "CAIRN_RESEARCH";

    /// <summary>A Red Faction install (the folder holding the game's <c>.vpp</c> archives).</summary>
    public const string GameDirectoryVariable = "CAIRN_GAME_DIR";

    /// <summary>The folders holding Alpine Faction's golden retarget outputs (<c>af_*.rfa</c>), <c>;</c>-separated.</summary>
    public const string GoldenClipsVariable = "CAIRN_GOLDEN_CLIPS";

    /// <summary>REDUX's research folder (its <c>anim</c>, <c>dev</c> and <c>br</c> samples).</summary>
    public const string ReduxResearchVariable = "CAIRN_REDUX_RESEARCH";

    /// <summary>The optional local file in the research folder.</summary>
    public const string FileName = "local-paths.json";

    /// <summary>The folder containing Cairn.sln above the running assembly, or null when run detached.</summary>
    public static string? RepositoryRoot { get; } = FindRoot();

    /// <summary>
    /// The research folder: <c>CAIRN_RESEARCH</c>, else the repository's <c>research</c> folder; null when
    /// neither is known. It need not exist.
    /// </summary>
    public static string? Research { get; } =
        Environment.GetEnvironmentVariable(ResearchVariable) is { Length: > 0 } env ? Path.GetFullPath(env)
        : RepositoryRoot is null ? null : Path.Combine(RepositoryRoot, "research");

    /// <summary>The stock corpus (<c>research/rfa_workbench/rf_decomp/meshes_anims</c>), or null when absent.</summary>
    public static string? Corpus { get; } = ExistingDirectory(Research is null ? null : Path.Combine(Research, "rfa_workbench", "rf_decomp", "meshes_anims"));

    private static readonly Dictionary<string, string[]> FileValues = ReadFile();

    /// <summary>A Red Faction install (<c>CAIRN_GAME_DIR</c> / <c>gameDirectory</c>), or null.</summary>
    public static string? GameDirectory { get; } = Resolve(GameDirectoryVariable, "gameDirectory");

    /// <summary>
    /// The folders of Alpine Faction's golden retarget outputs (<c>CAIRN_GOLDEN_CLIPS</c>, <c>;</c>-separated /
    /// <c>goldenClips</c>, a string or an array of strings) that exist, in the order given; empty when none.
    /// </summary>
    public static IReadOnlyList<string> GoldenClips { get; } = ResolveAll(GoldenClipsVariable, "goldenClips");

    /// <summary>REDUX's research folder (<c>CAIRN_REDUX_RESEARCH</c> / <c>reduxResearch</c>), or null.</summary>
    public static string? ReduxResearch { get; } = Resolve(ReduxResearchVariable, "reduxResearch");

    /// <summary>A corpus file's full path, or null when the corpus or the file is absent.</summary>
    public static string? CorpusFile(string name)
    {
        if (Corpus is null) return null;
        string path = Path.Combine(Corpus, name);
        return File.Exists(path) ? path : null;
    }

    /// <summary>How to configure a location, for a "skipped" message: "set CAIRN_GAME_DIR or gameDirectory in research/local-paths.json".</summary>
    public static string HowToSet(string variable, string key) => $"set {variable} or \"{key}\" in research/{FileName}";

    private static string? Resolve(string variable, string key)
    {
        try
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } env) return ExistingDirectory(Path.GetFullPath(env));
            if (Research is null || !FileValues.TryGetValue(key, out string[]? fromFile)) return null;
            return ExistingDirectory(Path.GetFullPath(Path.Combine(Research, fromFile[0])));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>Every existing folder a multi-folder location names (duplicates dropped).</summary>
    private static IReadOnlyList<string> ResolveAll(string variable, string key)
    {
        IEnumerable<string?> entries;
        if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } env)
            entries = env.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(e => Full(e, null));
        else if (Research is not null && FileValues.TryGetValue(key, out string[]? fromFile))
            entries = fromFile.Select(e => Full(e, Research));
        else
            return [];
        return entries.Select(ExistingDirectory).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        static string? Full(string entry, string? relativeTo)
        {
            try { return Path.GetFullPath(relativeTo is null ? entry : Path.Combine(relativeTo, entry)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        }
    }

    private static Dictionary<string, string[]> ReadFile()
    {
        var values = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (Research is null) return values;
        string path = Path.Combine(Research, FileName);
        try
        {
            if (!File.Exists(path)) return values;
            using var doc = JsonDocument.Parse(File.ReadAllText(path),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return values;
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                string[] texts = property.Value.ValueKind switch
                {
                    JsonValueKind.String => [property.Value.GetString()!],
                    JsonValueKind.Array => property.Value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToArray(),
                    _ => [],
                };
                texts = texts.Where(t => t.Length > 0).ToArray();
                if (texts.Length > 0) values[property.Name] = texts;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return values;
    }

    private static string? ExistingDirectory(string? path) => path is not null && Directory.Exists(path) ? path : null;

    private static string? FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Cairn.sln"))) dir = dir.Parent;
        return dir?.FullName;
    }
}
