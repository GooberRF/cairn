using System.Diagnostics;
using Cairn.Workspace;

namespace Cairn.Vfx.Tests;

/// <summary>
/// Locates and runs a REDUX command-line build for cross-tool tests. <see cref="Exe"/> is null (tests return
/// early) unless <c>CAIRN_REDUX_EXE</c> names an existing file or a build sits next to REDUX's research folder.
/// </summary>
internal static class ReduxCli
{
    public const string ExeVariable = "CAIRN_REDUX_EXE";

    public static string? Exe { get; } = Find();

    private static string? Find()
    {
        if (Environment.GetEnvironmentVariable(ExeVariable) is { Length: > 0 } configured)
            return File.Exists(configured) ? Path.GetFullPath(configured) : null;
        if (LocalPaths.ReduxResearch is not { } research || Path.GetDirectoryName(Path.GetFullPath(research).TrimEnd('\\', '/')) is not { } root)
            return null;
        foreach (string candidate in new[]
        {
            Path.Combine(root, "bin", "Release", "net8.0", "redux.exe"),
            Path.Combine(root, "dist", "redux.exe"),
        })
        {
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Copies <paramref name="input"/> (plus <paramref name="companions"/>, e.g. a glTF's .bin) into
    /// <paramref name="workDir"/>, converts it there and returns the output path (REDUX writes
    /// <c>&lt;stem&gt;.&lt;outFormat&gt;</c> into its working directory).
    /// </summary>
    public static string Convert(string input, string outFormat, string workDir, params string[] companions)
    {
        if (Exe is null) throw new InvalidOperationException("REDUX is not configured.");
        Directory.CreateDirectory(workDir);
        foreach (string file in companions.Prepend(input))
        {
            string target = Path.Combine(workDir, Path.GetFileName(file));
            if (!string.Equals(Path.GetFullPath(file), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                File.Copy(file, target, overwrite: true);
        }
        string name = Path.GetFileName(input);
        string output = Path.Combine(workDir, Path.ChangeExtension(name, outFormat));
        if (File.Exists(output) && !string.Equals(Path.GetFullPath(output), Path.GetFullPath(Path.Combine(workDir, name)), StringComparison.OrdinalIgnoreCase))
            File.Delete(output);
        var start = new ProcessStartInfo(Exe)
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-input");
        start.ArgumentList.Add(name);
        start.ArgumentList.Add("-outformat");
        start.ArgumentList.Add(outFormat);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("REDUX did not start.");
        var stderr = process.StandardError.ReadToEndAsync();
        string stdout = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(300_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"REDUX timed out converting {name}.");
        }
        if (!File.Exists(output))
            throw new InvalidOperationException($"REDUX wrote no {outFormat} for {name} (exit {process.ExitCode}):\n{stdout}\n{stderr.Result}");
        return output;
    }
}
