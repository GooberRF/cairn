namespace Cairn.Atx.Tests;

/// <summary>Locates repository folders from the test assembly's output directory.</summary>
internal static class TestPaths
{
    /// <summary>The folder containing Cairn.sln, or null when the tests run detached.</summary>
    public static string? RepositoryRoot => LocalPaths.RepositoryRoot;

    /// <summary>Full path of a repository-relative file, or null when the repository is not around.</summary>
    public static string? Repo(params string[] parts) =>
        RepositoryRoot is null ? null : Path.Combine([RepositoryRoot, .. parts]);

    /// <summary>The committed ATX sample set.</summary>
    public static string? Samples(params string[] parts) => Repo(["samples", "atx", .. parts]);

    /// <summary>A file under the ATX research data, or null when the research folder is absent.</summary>
    public static string? Research(params string[] parts) =>
        LocalPaths.Research is null ? null : Path.Combine([LocalPaths.Research, "atx_workbench", .. parts]);
}

/// <summary>A scratch folder that deletes itself at the end of a test.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cairn-atx-" + Guid.NewGuid().ToString("N"));
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
