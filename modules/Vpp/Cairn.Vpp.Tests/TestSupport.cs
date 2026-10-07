using Cairn.Vpp.Editing;
using Cairn.Vpp.Model;
using Cairn.Workspace;

namespace Cairn.Vpp.Tests;

/// <summary>A temporary folder deleted when the test ends.</summary>
public sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cairn-vpp-tests", Guid.NewGuid().ToString("N"));
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

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

internal static class TestData
{
    /// <summary>Every packfile under the game folder, or empty when the game folder is not configured.</summary>
    public static IReadOnlyList<FileInfo> GamePackfiles()
    {
        string? dir = LocalPaths.GameDirectory;
        if (dir is null || !Directory.Exists(dir)) return [];
        return [.. new DirectoryInfo(dir).EnumerateFiles("*.vpp", SearchOption.AllDirectories)];
    }

    /// <summary>The packfiles directly in the game folder (the stock ones and client mods at the top level).</summary>
    public static IReadOnlyList<FileInfo> RootPackfiles()
    {
        string? dir = LocalPaths.GameDirectory;
        if (dir is null || !Directory.Exists(dir)) return [];
        return [.. new DirectoryInfo(dir).EnumerateFiles("*.vpp", SearchOption.TopDirectoryOnly)];
    }

    /// <summary>A stock packfile from the game folder's root by name, or null.</summary>
    public static string? Stock(string name)
    {
        string? dir = LocalPaths.GameDirectory;
        if (dir is null) return null;
        string path = System.IO.Path.Combine(dir, name);
        return System.IO.File.Exists(path) ? path : null;
    }

    /// <summary>Deterministic pseudo-random bytes.</summary>
    public static byte[] Bytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>A small package built in memory.</summary>
    public static VppPackage Small(params (string Name, int Length)[] entries)
    {
        var package = VppPackage.Empty;
        int seed = 1;
        foreach (var (name, length) in entries)
        {
            package = VppEdit.AddBytes(package, name, Bytes(length, seed++), VppClashPolicy.Replace).Package;
        }
        return package;
    }

    public static byte[] ReadEntry(VppItem item) => item.Source.ReadAll();
}
