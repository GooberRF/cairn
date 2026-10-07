using System.Globalization;
using System.Windows;
using Cairn.Rfa.Ui.ViewModels;
using Cairn.Ui.Diagnostics;

namespace Cairn.Rfa.Ui.Diagnostics;

/// <summary>
/// RFA Workbench's old command-line view for the ported tests: the shell's <c>--name value</c>
/// options as <see cref="Extra"/>, plus <see cref="Time"/> and <see cref="Dialog"/>.
/// </summary>
internal sealed class RfaDiagnosticOptions(IReadOnlyDictionary<string, string> options)
{
    /// <summary>Every module option (the shell passes unknown <c>--name value</c> switches through).</summary>
    public Dictionary<string, string> Extra { get; } = new(options, StringComparer.OrdinalIgnoreCase);

    /// <summary><c>--time seconds</c>, or null.</summary>
    public float? Time => options.TryGetValue("time", out var s)
        && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float t) ? t : null;

    /// <summary><c>--dialog name</c> (a shell switch, so read from the process command line), or null.</summary>
    public string? Dialog
    {
        get
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.FindIndex(args, a => a.Equals("--dialog", StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}

/// <summary>What an RFA self-test gets: the old <c>SelfTestContext</c> members over the shell's context.</summary>
public sealed class RfaSelfTestContext(Cairn.Ui.Diagnostics.SelfTestContext inner)
{
    public Cairn.Ui.Diagnostics.SelfTestContext Inner { get; } = inner;

    public IShellContext Shell => Inner.Shell;

    public Window Window => Inner.MainWindow;

    /// <summary>The RFA workspace (the old <c>MainViewModel</c>).</summary>
    public RfaWorkspace Model => RfaModule.Workspace;

    internal RfaDiagnosticOptions Options { get; } = new(inner.Options);

    /// <summary>The active document as a clip document, or null.</summary>
    public ClipDocumentViewModel? Clip => Model.ActiveDocument as ClipDocumentViewModel;

    public int Failures => Inner.Failures.Count;

    public void Check(bool ok, string what) => Inner.Check(ok, what);

    public void Log(string line) => Inner.Log(line);

    public async Task<bool> SettleAsync() { await Inner.SettleAsync(); return true; }

    public Task YieldAsync() => Cairn.Ui.Diagnostics.ScreenshotContext.YieldAsync();
}

/// <summary>What an RFA screenshot step or dialog factory gets (the old <c>ScreenshotContext</c> members).</summary>
public sealed class RfaScreenshotContext(Cairn.Ui.Diagnostics.ScreenshotContext inner)
{
    private static readonly HashSet<string> Used = new(StringComparer.OrdinalIgnoreCase);

    public Cairn.Ui.Diagnostics.ScreenshotContext Inner { get; } = inner;

    public IShellContext Shell => Inner.Shell;

    public Window Window => Inner.MainWindow;

    public RfaWorkspace Model => RfaModule.Workspace;

    internal RfaDiagnosticOptions Options { get; } = new(inner.Options);

    public ClipDocumentViewModel? Clip => Model.ActiveDocument as ClipDocumentViewModel;

    /// <summary>The value of a module switch (<c>--name value</c>), or null.</summary>
    public string? Extra(string name)
    {
        if (!Inner.Options.TryGetValue(name, out string? value)) return null;
        Used.Add(name);
        return value;
    }

    /// <summary>Module switches no step or dialog asked for.</summary>
    public IEnumerable<string> Unused => Inner.Options.Keys.Where(k => !Used.Contains(k));

    public void Log(string line) => Inner.Log(line);

    public async Task<bool> SettleAsync() { await Inner.SettleAsync(); return true; }

    public Task YieldAsync() => Cairn.Ui.Diagnostics.ScreenshotContext.YieldAsync();
}
