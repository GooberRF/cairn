using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Cairn.Ui.Modules;

namespace Cairn.Ui.Diagnostics;

/// <summary>Marks <c>static Task X(SelfTestContext or wrapper)</c> as a self-test run by <c>--selftest</c>.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SelfTestAttribute(string name) : Attribute
{
    public string Name { get; } = name;
    /// <summary>Run order (lower first).</summary>
    public int Order { get; init; } = 100;
    /// <summary>
    /// A document the test needs: a kind id ("rfa.clip") or an extension (".rfa"). The runner activates the
    /// matching document opened from the command line before the test, or reports the test as skipped.
    /// </summary>
    public string? Requires { get; init; }
}

/// <summary>Marks <c>static Task X(ScreenshotContext or wrapper)</c> as a step before a <c>--screenshot</c> capture.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ScreenshotStepAttribute(int order) : Attribute
{
    public int Order { get; } = order;
}

/// <summary>Marks <c>static Window? X(ScreenshotContext or wrapper)</c> as the dialog <c>--dialog name</c> opens.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ScreenshotDialogAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>What a diagnostic method gets: the window, the shell, the run's options, and helpers.</summary>
public class ScreenshotContext(Window mainWindow, IShellContext shell, IReadOnlyDictionary<string, string> options, Action<string> log)
{
    public Window MainWindow { get; } = mainWindow;
    public IShellContext Shell { get; } = shell;
    /// <summary>The run's <c>--name value</c> options.</summary>
    public IReadOnlyDictionary<string, string> Options { get; } = options;
    /// <summary>Writes a line to the run's log.</summary>
    public void Log(string message) => log(message);
    /// <summary>Lets queued UI work (layout, render, bindings) run.</summary>
    public static async Task YieldAsync() =>
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    /// <summary>Waits for background work (<see cref="Services.BusyTracker"/>) and the UI to go idle.</summary>
    public virtual async Task SettleAsync(TimeSpan? timeout = null)
    {
        var limit = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        do await YieldAsync();
        while (Services.BusyTracker.Count > 0 && DateTime.UtcNow < limit && await Delay());
        if (Services.BusyTracker.Count > 0) Log("still busy after the settle timeout: " + string.Join(", ", Services.BusyTracker.Describe()));
        await YieldAsync();
        static async Task<bool> Delay() { await Task.Delay(20); return true; }
    }
}

/// <summary>A self-test's context: <see cref="ScreenshotContext"/> plus <see cref="Check"/>.</summary>
public class SelfTestContext(Window mainWindow, IShellContext shell, IReadOnlyDictionary<string, string> options, Action<string> log)
    : ScreenshotContext(mainWindow, shell, options, log)
{
    private readonly List<string> _failures = [];
    public IReadOnlyList<string> Failures => _failures;
    /// <summary>How many checks passed.</summary>
    public int Passed { get; private set; }
    /// <summary>Set by <see cref="Skip"/>: why the test did not run.</summary>
    public string? SkipReason { get; private set; }
    /// <summary>Records a failure (and logs it) when <paramref name="condition"/> is false.</summary>
    public bool Check(bool condition, string what)
    {
        Log((condition ? "  ok   " : "  FAIL ") + what);
        if (condition) Passed++;
        else _failures.Add(what);
        return condition;
    }
    /// <summary>
    /// Set by the shell: the shortest strong field path from a root (statics, the application, its windows, the UI
    /// dispatcher) to an object, one hop per line, or null when no such path exists (a stack slot or GC handle holds it).
    /// </summary>
    public Func<object, string?>? RootPathFinder { get; init; }
    /// <summary>Why <paramref name="target"/> is still alive, for a leak check's log (see <see cref="RootPathFinder"/>).</summary>
    public string DescribeRoot(object target) => RootPathFinder is null
        ? "  (no root finder in this run)"
        : RootPathFinder(target) ?? "  (no path from statics, windows or the dispatcher: a stack slot or GC handle root)";
    /// <summary>Reports the test as skipped (not failed), e.g. when the document it needs is missing; return after calling it.</summary>
    public void Skip(string reason)
    {
        SkipReason = reason;
        Log("  skip " + reason);
    }
}

/// <summary>A discovered diagnostic method, bound to build its argument from the base context.</summary>
public sealed record DiagnosticMethod(string Name, int Order, MethodInfo Method)
{
    /// <summary>The <see cref="SelfTestAttribute.Requires"/> of a self-test, else null.</summary>
    public string? Requires => Method.GetCustomAttribute<SelfTestAttribute>()?.Requires;
    /// <summary>The assembly that declares the method (which module it belongs to).</summary>
    public Assembly Assembly => Method.Module.Assembly;

    /// <summary>Calls the method with <paramref name="context"/>, or with its parameter type built from it.</summary>
    public object? Invoke(ScreenshotContext context)
    {
        var type = Method.GetParameters()[0].ParameterType;
        object argument = type.IsInstanceOfType(context) ? context : Activator.CreateInstance(type, context)!;
        return Method.Invoke(null, [argument]);
    }
}

/// <summary>Finds <see cref="SelfTestAttribute"/>, <see cref="ScreenshotStepAttribute"/> and <see cref="ScreenshotDialogAttribute"/> methods.</summary>
public sealed class DiagnosticsRegistry
{
    public DiagnosticsRegistry(IEnumerable<Assembly> assemblies)
    {
        // One entry per assembly name (a module listed twice, or loaded twice, is scanned once) and per method
        // (a source file compiled into two scanned assemblies yields its methods once).
        var methods = assemblies.GroupBy(a => a.FullName).Select(g => g.First()).SelectMany(a => a.GetTypes())
            .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .DistinctBy(m => (m.DeclaringType?.FullName, m.ToString()))
            .ToList();
        SelfTests = [.. Collect<SelfTestAttribute>(methods, typeof(SelfTestContext), a => (a.Name, a.Order)).OrderBy(m => m.Order)];
        ScreenshotSteps = [.. Collect<ScreenshotStepAttribute>(methods, typeof(ScreenshotContext), a => ("step", a.Order)).OrderBy(m => m.Order)];
        Dialogs = Collect<ScreenshotDialogAttribute>(methods, typeof(ScreenshotContext), a => (a.Name, 0))
            .ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<DiagnosticMethod> SelfTests { get; }
    public IReadOnlyList<DiagnosticMethod> ScreenshotSteps { get; }
    public IReadOnlyDictionary<string, DiagnosticMethod> Dialogs { get; }
    /// <summary>Every <c>--dialog</c> name, sorted.</summary>
    public IEnumerable<string> DialogNames => Dialogs.Keys.Order(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="parameter"/> is <paramref name="context"/> (or a base) or has a public constructor taking it.</summary>
    public static bool Accepts(Type parameter, Type context) =>
        parameter.IsAssignableFrom(context)
        || parameter.GetConstructors().Any(c => c.GetParameters() is [var p] && p.ParameterType.IsAssignableFrom(context));

    private static IEnumerable<DiagnosticMethod> Collect<TAttr>(List<MethodInfo> methods, Type context, Func<TAttr, (string, int)> describe)
        where TAttr : Attribute
    {
        foreach (var method in methods)
        {
            if (method.GetCustomAttribute<TAttr>() is not { } attribute) continue;
            if (method.GetParameters() is not [var p] || !Accepts(p.ParameterType, context))
                throw new InvalidOperationException($"{method.DeclaringType?.Name}.{method.Name} must take one {context.Name} (or a type built from it).");
            var (name, order) = describe(attribute);
            yield return new DiagnosticMethod(name == "step" ? method.Name : name, order, method);
        }
    }
}
