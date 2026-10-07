using Cairn.Workspace;

namespace Cairn.Rfa.Tests;

/// <summary>
/// Locates repository folders from the test assembly's output directory, and the optional
/// machine-specific data through <see cref="LocalPaths"/> (environment variables, or the git-ignored
/// research/local-paths.json), exactly as the App's self-test does.
/// </summary>
internal static class TestPaths
{
    /// <summary>The folder containing Cairn.sln, or null when the tests run detached.</summary>
    public static string? RepositoryRoot => LocalPaths.RepositoryRoot;

    /// <summary>Full path of a repository-relative file, or null when the repository is not around.</summary>
    public static string? Repo(params string[] parts) =>
        RepositoryRoot is null ? null : Path.Combine([RepositoryRoot, .. parts]);

    /// <summary>
    /// The stock corpus (research/rfa_workbench/rf_decomp/meshes_anims), read in place, or null when it is absent —
    /// in which case the corpus tests pass trivially. Nothing from it is ever copied into the tests.
    /// </summary>
    public static string? Corpus => LocalPaths.Corpus;

    /// <summary>The committed sample set (samples/rfa, written by tools/Cairn.Rfa.SampleGen), or null when absent.</summary>
    public static string? Samples { get; } = ExistingDirectory(Repo("samples", "rfa"));

    /// <summary>The stock tables (research/rfa_workbench/rf_decomp/tables), or null when absent.</summary>
    public static string? Tables { get; } = ExistingDirectory(Research("rfa_workbench", "rf_decomp", "tables"));

    /// <summary>The Python reference implementation's folder, or null when absent.</summary>
    public static string? ReferenceTools { get; } = ExistingDirectory(Research("rfa_workbench", "anim_retarget", "tools"));

    /// <summary>
    /// REDUX's non-stock sample meshes and clips (community exporter output), read in place, or null
    /// when not configured (<c>CAIRN_REDUX_RESEARCH</c> or <c>reduxResearch</c> in research/local-paths.json).
    /// </summary>
    public static string? ReduxResearch => LocalPaths.ReduxResearch;

    /// <summary>
    /// A Red Faction install, or null when not configured (<c>CAIRN_GAME_DIR</c> or <c>gameDirectory</c>
    /// in research/local-paths.json); the real-install tests then pass trivially.
    /// </summary>
    public static string? GameDirectory => LocalPaths.GameDirectory;

    /// <summary>
    /// The folders of Alpine Faction's golden retarget outputs (<c>af_*.rfa</c>), empty when not configured
    /// (<c>CAIRN_GOLDEN_CLIPS</c> or <c>goldenClips</c> in research/local-paths.json; several folders allowed).
    /// </summary>
    public static IReadOnlyList<string> GoldenClips => LocalPaths.GoldenClips;

    /// <summary>
    /// A path under the repository's research folder. The environment variable <c>CAIRN_RESEARCH</c>
    /// overrides the folder, so a copy of the solution built elsewhere can still read the corpus in
    /// place (read-only).
    /// </summary>
    public static string? Research(params string[] parts) =>
        LocalPaths.Research is { } root ? Path.Combine([root, .. parts]) : null;

    /// <summary>A corpus file's full path, or null when the corpus or the file is absent.</summary>
    public static string? CorpusFile(string name) => LocalPaths.CorpusFile(name);

    private static string? ExistingDirectory(string? path) => path is not null && Directory.Exists(path) ? path : null;
}

/// <summary>A scratch folder that deletes itself at the end of a test.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cairn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public string Write(string name, byte[] bytes)
    {
        string path = File(name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, bytes);
        return path;
    }

    public string Write(string name, string text)
    {
        string path = File(name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, text);
        return path;
    }

    public string SubDirectory(string name)
    {
        string path = File(name);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
