using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Threading;
using Cairn.Ui.Diagnostics;
using Cairn.Ui.Documents;
using Cairn.Ui.Modules;

namespace Cairn.Shell;

/// <summary>Checks that only make sense with several modules loaded together: shortcut routing, glTF importer choice, mass open/close.</summary>
public static class IntegrationSelfTests
{
    private static string OwnerName(ShellViewModel shell, ShortcutInfo s) =>
        shell.ShellShortcuts.Contains(s) ? "shell" : shell.Modules.FirstOrDefault(m => m.Shortcuts.Contains(s))?.DisplayName ?? "?";

    /// <summary>Module shortcuts that deliberately apply whatever document is active (they create documents).</summary>
    private static readonly HashSet<string> DeliberateGlobal = new(StringComparer.Ordinal);

    private static IModule? OwnerOf(ShellViewModel shell, IDocument? d) =>
        d is null ? null : shell.Modules.FirstOrDefault(m => m.DocumentKinds.Any(k => k.Id == d.Kind.Id));

    /// <summary>
    /// Every gesture used by more than one owner, and for each open document (and none) which shortcut applies first in the
    /// router's order. Ambiguous = the first applying shortcut belongs to a module that does not own the active document
    /// while another such module's shortcut for the same gesture also applies.
    /// </summary>
    [SelfTest("integration.shortcuts")]
    public static void Shortcuts(SelfTestContext ctx)
    {
        var shell = (ShellViewModel)ctx.Shell;
        var all = shell.AllShortcuts;
        var shared = all.GroupBy(s => (s.Key, s.Modifiers)).Where(g => g.Select(s => OwnerName(shell, s)).Distinct().Count() > 1).ToList();
        ctx.Log($"shortcuts: {all.Count} rows, {shared.Count} gestures used by more than one owner");
        var before = shell.ActiveDocument;
        foreach (var p in CommandLineDocuments(shell)) shell.OpenFile(p);
        var docs = shell.Documents.GroupBy(d => d.Kind.Id).Select(g => g.First()).ToList();
        foreach (var g in shared)
        {
            var gesture = (g.Key.Modifiers == ModifierKeys.None ? "" : g.Key.Modifiers.ToString().Replace(", ", "+") + "+") + g.Key.Key;
            ctx.Log($"  {gesture}: {string.Join(", ", g.Select(s => $"{OwnerName(shell, s)} '{s.Description}'"))}");
            foreach (var d in docs.Cast<IDocument?>().Append(null))
            {
                var owner = OwnerOf(shell, d);
                var order = g.OrderBy(s => owner is not null && owner.Shortcuts.Contains(s) ? 0 : shell.ShellShortcuts.Contains(s) ? 1 : 2).ToList();
                var applying = order.Where(s => s.AppliesTo?.Invoke(d) ?? true).ToList();
                var winner = applying.FirstOrDefault();
                var foreign = applying.Where(s => OwnerName(shell, s) != "shell" && !(owner?.Shortcuts.Contains(s) ?? false)).Select(s => OwnerName(shell, s)).Distinct().ToList();
                if (winner is not null) ctx.Log($"    {d?.Kind.Id ?? "(none)"} -> {OwnerName(shell, winner)}{(foreign.Count > 0 ? " (foreign: " + string.Join(", ", foreign) + ")" : "")}");
                var winnerForeign = winner is not null && foreign.Contains(OwnerName(shell, winner));
                if (winnerForeign && foreign.Count > 1) ctx.Check(false, $"{gesture} on {d?.Kind.Id ?? "(none)"} is ambiguous between {string.Join(" and ", foreign)}");
                // another module's command must not run on this module's document unless it is a deliberate module-global one
                // ("?" = a module whose Shortcuts list is rebuilt per call; the probe modules are test fixtures)
                if (d is not null && winnerForeign && OwnerName(shell, winner!) is var on && on != "?" && !on.StartsWith("Probe", StringComparison.Ordinal)
                    && !DeliberateGlobal.Contains(winner!.Description))
                    ctx.Check(false, $"{gesture} on {d.Kind.Id} runs {OwnerName(shell, winner)} '{winner.Description}' (scope it with AppliesTo)");
            }
        }
        foreach (var m in shell.Modules)
            foreach (var s in m.Shortcuts.Where(s => s.AppliesTo is null))
                ctx.Log($"  module-global: {m.DisplayName} '{s.Description}'{(DeliberateGlobal.Contains(s.Description) ? "" : " (not in the deliberate list)")}");
        ctx.Check(true, $"checked {shared.Count} shared gestures on {docs.Count} document kinds");
        if (before is not null) shell.Activate(before);
    }

    /// <summary>
    /// <c>--gltf-vfx p --gltf-rfa p --gltf-plain p</c>: the importer the shell would pick for each (highest probe; a tie asks).
    /// </summary>
    [SelfTest("integration.gltf-routing")]
    public static void GltfRouting(SelfTestContext ctx)
    {
        var shell = (ShellViewModel)ctx.Shell;
        var expect = new[] { ("gltf-vfx", "Effects"), ("gltf-rfa", "Animations and meshes"), ("gltf-plain", "") };
        foreach (var m in shell.Modules)
            ctx.Check(m.Importers.All(i => shell.Importers.Contains(i)), $"{m.DisplayName}: every importer is registered with the shell ({m.Importers.Count})");
        if (!expect.Any(e => ctx.Options.ContainsKey(e.Item1))) { ctx.Skip("needs --gltf-vfx/--gltf-rfa/--gltf-plain paths"); return; }
        foreach (var (option, module) in expect)
        {
            if (!ctx.Options.TryGetValue(option, out var path) || !File.Exists(path)) continue;
            var scored = shell.Importers.Select(i => (Importer: i, Score: SafeProbe(i, path),
                Module: shell.Modules.FirstOrDefault(m => m.Importers.Contains(i))?.DisplayName ?? "?")).Where(s => s.Score > 0).OrderByDescending(s => s.Score).ToList();
            ctx.Log($"{option} {Path.GetFileName(path)}: {string.Join(", ", scored.Select(s => $"{s.Module} '{s.Importer.DisplayName}'={s.Score}"))}");
            var tie = scored.Count > 1 && scored[0].Score == scored[1].Score;
            if (module.Length == 0)
                ctx.Check(tie && scored.Select(s => s.Module).Distinct().Count() > 1, $"{option}: a plain mesh is a tie between modules (the shell asks)");
            else
                ctx.Check(scored.Count > 0 && !tie && scored[0].Module == module, $"{option}: goes to {module}");
        }
    }

    /// <summary>The shell's AssetHost is configured from the settings in every run (diagnostic runs included).</summary>
    [SelfTest("integration.asset-host")]
    public static async Task AssetHostConfigured(SelfTestContext ctx)
    {
        var shell = (ShellViewModel)ctx.Shell;
        if (string.IsNullOrEmpty(shell.Settings.GameDirectory)) { ctx.Skip("no game directory in settings"); return; }
        ctx.Check(shell.Assets.HasSources, "settings have a game directory, so the shared AssetHost has sources");
        var sw = Stopwatch.StartNew();
        await shell.Assets.ArchivesIndexed;
        ctx.Log($"asset host: archives indexed after {sw.ElapsedMilliseconds:N0} ms more ({shell.Assets.Resolver.ArchiveDirectoryReads.Reads:N0} archive directories read so far)");
        sw.Restart();
        var vpps = shell.Assets.Resolver.EnumerateAll([".tbl"]).Count();
        ctx.Log($"asset host: tables enumerated in {sw.ElapsedMilliseconds:N0} ms");
        ctx.Check(vpps > 0, $"the shared resolver finds game tables ({vpps})");
        sw.Restart();
        for (int i = 0; i < 20; i++) _ = shell.Assets.Resolver.Resolve($"cairn_not_there_{i}.tga");
        ctx.Log($"asset host: a missing texture costs {sw.Elapsed.TotalMilliseconds / 20:F1} ms");
        ctx.Check(sw.Elapsed.TotalMilliseconds / 20 < 100, "a name in no packfile is looked up in well under 100 ms");
    }

    private static int SafeProbe(IFileImporter i, string path)
    {
        try { return i.Probe(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return 0; }
    }

    /// <summary>
    /// Three rounds of 10 opens/closes over the command-line documents, each kind closed idle and (where it has playback)
    /// while playing. After every round and full GCs, every closed document and view must be collected; a survivor is
    /// traced to its GC root by <see cref="LeakFinder"/>.
    /// </summary>
    [SelfTest("integration.open-close")]
    public static async Task OpenClose(SelfTestContext ctx)
    {
        var shell = (ShellViewModel)ctx.Shell;
        var paths = CommandLineDocuments(shell);
        if (paths.Count == 0) { ctx.Skip("needs documents on the command line"); return; }
        foreach (var d in shell.Documents.ToList()) shell.CloseDiscarding(d);
        await CollectAsync();
        var startMb = GC.GetTotalMemory(true) / (1024.0 * 1024);
        var variants = paths.SelectMany(p => new[] { (Path: p, Play: false), (Path: p, Play: true) }).ToList();
        var noPlayback = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var times = new Dictionary<string, List<double>>();
        var failures = 0;
        var traced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int round = 0, next = 0; round < 3; round++)
        {
            var weak = new List<(string Label, WeakReference Ref)>();
            for (var n = 0; n < 10; n++)
            {
                var (path, play) = variants[next++ % variants.Count];
                if (play && noPlayback.Contains(path)) play = false;
                var sw = Stopwatch.StartNew();
                if (!shell.OpenFile(path) || shell.ActiveDocument is null) { failures++; continue; }
                await ctx.SettleAsync();
                sw.Stop();
                var ext = Path.GetExtension(path).ToLowerInvariant();
                (times.TryGetValue(ext, out var l) ? l : times[ext] = []).Add(sw.Elapsed.TotalMilliseconds);
                var played = play && StartPlayback(shell);
                if (play && !played) noPlayback.Add(path);
                if (played) await Task.Delay(150);
                weak.AddRange(TrackAndClose(ctx, shell, $"{Path.GetFileName(path)}{(played ? " (playing)" : "")}"));
                await SelfTestContext.YieldAsync();
            }
            await ctx.SettleAsync(); // in-flight work (lints, texture loads) may still hold the last closed documents
            await CollectAsync();
            var alive = weak.Where(w => w.Ref.IsAlive).ToList();
            if (alive.Count > 0)
            {
                // In some combined runs the round's LAST closed document and view stay referenced, with no path from
                // statics/windows/dispatcher (a stack or GC handle root). Second pass to tell in-flight holds from that:
                // open and close one untracked document, settle, collect again (hang-fix: the survivor persists; open).
                ctx.Log($"round {round + 1}: {alive.Count} alive after the first collection ({string.Join(", ", alive.Select(a => a.Label).Distinct())}); "
                    + "opening and closing one untracked document, then collecting again");
                if (OpenAndCloseUntracked(shell, variants[0].Path)) await ctx.SettleAsync();
                await ctx.SettleAsync();
                await CollectAsync();
                alive = weak.Where(w => w.Ref.IsAlive).ToList();
            }
            if (alive.Count > 0)
            {
                // vpp-last: with no managed path, the remaining suspect is the input / text-services state of the last
                // focused element (a COM-held hold that only moves when another element takes keyboard focus).
                ctx.Log($"round {round + 1}: {alive.Count} still alive; keyboard focus on {System.Windows.Input.Keyboard.FocusedElement?.GetType().Name ?? "nothing"}; "
                    + "moving keyboard focus to a scratch text box, then collecting again");
                var scratch = new System.Windows.Controls.TextBox();
                var focusWindow = new System.Windows.Window { Width = 200, Height = 60, Left = -20000, Top = -20000, ShowInTaskbar = false, WindowStyle = System.Windows.WindowStyle.None, Title = "open-close focus", Content = scratch };
                focusWindow.Show();
                focusWindow.Activate();
                System.Windows.Input.Keyboard.Focus(scratch);
                await SelfTestContext.YieldAsync();
                focusWindow.Close();
                ctx.MainWindow.Activate();
                await ctx.SettleAsync();
                await CollectAsync();
                alive = weak.Where(w => w.Ref.IsAlive).ToList();
                ctx.Log(alive.Count == 0
                    ? $"round {round + 1}: collected once keyboard focus moved: the hold was the input/text-services state of the last focused element (bounded to one element), not a reference from Cairn"
                    : $"round {round + 1}: still {alive.Count} alive after moving keyboard focus");
            }
            ctx.Check(alive.Count == 0, $"round {round + 1}: {weak.Count - alive.Count}/{weak.Count} closed documents and views collected"
                + (alive.Count > 0 ? $" (alive: {string.Join(", ", alive.Select(a => a.Label).Distinct())})" : ""));
            // one trace per file name (its first survivor) per run: the walk is slow
            foreach (var (label, r) in alive.GroupBy(a => a.Label.Split(' ')[0]).Select(g => g.First()).Where(a => traced.Add(a.Label.Split(' ')[0])).ToList())
            {
                ctx.Log($"GC root of {label}:{Environment.NewLine}{Trace(r)}");
            }
        }
        ctx.Check(failures == 0, $"30 opens/closes without failure ({failures} failed)");
        if (noPlayback.Count > 0) ctx.Log($"no playback shortcut applied for: {string.Join(", ", noPlayback.Select(Path.GetFileName))}");
        foreach (var (ext, l) in times) ctx.Log($"open {ext}: {l.Count}x, mean {l.Average():0} ms, max {l.Max():0} ms (incl. settle)");
        ctx.Log($"managed memory after GC: {startMb:0.0} MB at start, {GC.GetTotalMemory(true) / (1024.0 * 1024):0.0} MB after 30 opens/closes");
        foreach (var p in paths) shell.OpenFile(p);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static bool OpenAndCloseUntracked(ShellViewModel shell, string path)
    {
        if (!shell.OpenFile(path) || shell.ActiveDocument is not { } doc) return false;
        shell.CloseDiscarding(doc);
        return true;
    }

    // The first "play" shortcut of the active document's module that applies and can run.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static bool StartPlayback(ShellViewModel shell)
    {
        var doc = shell.ActiveDocument;
        var play = shell.AllShortcuts.FirstOrDefault(s => s.Description.Contains("play", StringComparison.OrdinalIgnoreCase)
            && !s.Description.Contains("display", StringComparison.OrdinalIgnoreCase)
            && (s.AppliesTo?.Invoke(doc) ?? false) && s.Command.CanExecute(null));
        play?.Command.Execute(null);
        return play is not null;
    }

    // Separate, never-inlined methods so no hoisted local or stack slot of the async caller keeps a document or view alive.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static List<(string, WeakReference)> TrackAndClose(SelfTestContext ctx, ShellViewModel shell, string label)
    {
        var doc = shell.ActiveDocument!;
        if (doc.IsDirty) ctx.Log($"{doc.DisplayName} is dirty right after opening");
        var refs = new List<(string, WeakReference)> { ($"{label} document", new WeakReference(doc)), ($"{label} view", new WeakReference(doc.View)) };
        shell.CloseDiscarding(doc);
        return refs;
    }

    private static string Trace(WeakReference r) =>
        r.Target is { } o ? LeakFinder.FindPath(o) ?? "  (no path from statics, windows or the dispatcher: a stack or GC handle root)" : "  (collected meanwhile)";

    private static async Task CollectAsync()
    {
        for (var i = 0; i < 3; i++)
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    // Every document file on the command line (earlier tests may have closed some of them), module kinds first.
    private static List<string> CommandLineDocuments(ShellViewModel shell) =>
        [.. Environment.GetCommandLineArgs().Skip(1).Where(File.Exists).Select(Path.GetFullPath)
            .Where(p => shell.Kinds.Any(k => k.Extensions.Contains(Path.GetExtension(p).ToLowerInvariant())))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p.EndsWith("probe", StringComparison.OrdinalIgnoreCase) || p.EndsWith("probe2", StringComparison.OrdinalIgnoreCase))];
}
