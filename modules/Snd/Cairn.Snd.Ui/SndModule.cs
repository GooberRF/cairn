using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Cairn.Formats.Audio;
using Cairn.Snd.Ui.Dialogs;
using Cairn.Snd.Ui.Documents;
using Cairn.Ui.Documents;
using Cairn.Ui.Modules;
using Cairn.Ui.Mvvm;
using Cairn.Workspace;

namespace Cairn.Snd.Ui;

/// <summary>
/// The sounds module: opens PlayStation 2 sounds (.vse effects, .vmu music) and PC sounds (.wav, .ogg, .aif) in read-only
/// tabs with a waveform, playback (loops included) and the format's details, and converts them to WAV or Ogg Vorbis, one at a time
/// from a tab or in a batch from a packfile ("Convert sounds..." in the packfile's menus).
/// </summary>
public sealed class SndModule : ModuleBase, IArchiveBatchConverter
{
    /// <summary>The bottom Problems tab's id.</summary>
    public const string ProblemsPanelId = "snd.problems";

    private readonly List<SndDocument> _documents = [];
    private readonly List<MenuContribution> _menus = [];
    private readonly List<ShortcutInfo> _shortcuts = [];
    private ModuleSettings? _store;
    private SndKind? _kind;
    private IReadOnlyDictionary<string, string>? _pendingOptions;

    public SndModule()
    {
        Settings = new SndSettings(() => _store);
        SettingsPages = [new SndSettingsPage(Settings, () => Shell?.Dialogs)];
    }

    public override string Id => "snd";
    public override string DisplayName => "Sounds";

    /// <summary>The sound kind.</summary>
    public SndKind Kind => _kind ??= new SndKind(this);

    /// <summary>The module's settings ("snd." keys).</summary>
    public SndSettings Settings { get; }

    /// <summary>The shell (for documents and self-tests).</summary>
    public new IShellContext Shell => base.Shell;
    internal IShellContext ShellContext => base.Shell;

    /// <summary>True to play without a sound device (diagnostic runs: positions and loops still run).</summary>
    public bool SilentPlayback => base.Shell?.IsDiagnosticRun ?? true;

    /// <summary>The open sound documents.</summary>
    public IReadOnlyList<SndDocument> OpenDocuments => _documents;

    public override IReadOnlyList<IDocumentKind> DocumentKinds => [Kind];
    public override IReadOnlyList<MenuContribution> Menus => _menus;
    public override IReadOnlyList<ShortcutInfo> Shortcuts => _shortcuts;
    public override IReadOnlyList<ISettingsPage> SettingsPages { get; }

    public override IReadOnlyList<PanelContribution> Panels { get; } =
        [new PanelContribution(ProblemsPanelId, "Problems", PanelSide.Bottom, 10, d => d is SndDocument s ? SndProblemsPanel.For(s) : null)];

    public override IReadOnlyList<HelpTopic> HelpTopics { get; } = [new HelpTopic("snd.sounds", "Sounds (.vse, .vmu, .wav, .ogg)", BuildHelp)];

    private SndDocument? Active => base.Shell.ActiveDocument as SndDocument;
    private static bool IsSound(IDocument? d) => d is SndDocument;

    public override void Initialize(IShellContext shell)
    {
        base.Initialize(shell);
        _store = new ModuleSettings(shell.Settings, Id);
        RelayCommand Cmd(Action<SndDocument> action) => new(() => { if (Active is { } d) action(d); }, () => Active is not null);
        var play = Cmd(d => d.TogglePlay());
        var stop = Cmd(d => d.Stop());
        var loop = Cmd(d => d.Looping = !d.Looping);
        var start = Cmd(d => d.Seek(0));
        var convert = Cmd(ConvertDocument);
        var menu = new MenuItem { Header = "_Sound", Name = "SoundMenu" };
        void Add(string header, string tip, ICommand command, string gesture)
            => menu.Items.Add(new MenuItem { Header = header, ToolTip = tip, Command = command, InputGestureText = gesture });
        Add("_Play / Pause", "Play the sound, or pause it", play, "Space");
        Add("_Stop", "Stop and go back to the start", stop, "");
        Add("Go to _start", "Move the play position to the start", start, "Home");
        Add("_Loop", "Repeat the loop (the whole sound when it has no loop points)", loop, "L");
        menu.Items.Add(new Separator());
        Add("_Convert...", "Convert the sound to WAV or Ogg Vorbis: into its packfile, next to it or into a folder", convert, "Ctrl+Shift+E");
        _menus.Add(new MenuContribution(MenuSlot.TopLevel, 60, menu, IsSound));
        _shortcuts.Add(new("Sound", "Play or pause", Key.Space, ModifierKeys.None, play, IsSound));
        _shortcuts.Add(new("Sound", "Repeat the loop on or off", Key.L, ModifierKeys.None, loop, IsSound));
        _shortcuts.Add(new("Sound", "Go to the start", Key.Home, ModifierKeys.None, start, IsSound));
        _shortcuts.Add(new("Sound", "Convert the sound to WAV or Ogg Vorbis", Key.E, ModifierKeys.Control | ModifierKeys.Shift, convert, IsSound));
    }

    internal SndDocument Track(SndDocument document)
    {
        _documents.Add(document);
        return document;
    }

    internal void Forget(SndDocument document) => _documents.Remove(document);

    internal void RefreshCommands() => base.Shell.RefreshCommands();

    /// <summary>Brings the document's Problems tab forward.</summary>
    internal void ShowProblems(SndDocument document)
    {
        var panel = SndProblemsPanel.For(document);
        if (base.Shell.ShowPanel(ProblemsPanelId)) base.Shell.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, panel.FocusList);
    }

    // ── converting one sound (a tab) ──────────────────────────────────────────────────────────────────────────

    private IArchiveEntryTarget? ArchiveTarget => base.Shell.Modules.OfType<IArchiveEntryTarget>().FirstOrDefault();

    /// <summary>The packfile a document's work copy came from (its name), or null.</summary>
    public string? PackfileOf(SndDocument doc) => doc.FilePath is { } path ? ArchiveTarget?.ArchiveOf(path) : null;

    /// <summary>
    /// The folder "next to the source" means: the file's own, or for a packfile entry the packfile's; null when none, or
    /// when that is the game directory (a loose sound there changes what the game loads; the user picks a folder).
    /// </summary>
    public string? NextToFolder(SndDocument doc)
    {
        string? folder = SourceFolder(doc);
        return IsGameFolder(folder) ? null : folder;
    }

    private string? SourceFolder(SndDocument doc)
    {
        if (doc.FilePath is not { } path) return null;
        foreach (var provider in base.Shell.Modules.OfType<IWorkCopyProvider>())
        {
            if (!provider.IsWorkCopy(path)) continue;
            return provider.ArchiveEntryOf(path) is { } entry ? Path.GetDirectoryName(entry.ArchivePath) : null;
        }
        return Path.GetDirectoryName(path);
    }

    /// <summary>True when <paramref name="folder"/> is the game directory (or inside it): never a default output folder.</summary>
    internal bool IsGameFolder(string? folder) => Cairn.Assets.GameDirectoryLocator.IsInGameDirectory(folder, base.Shell.Settings.GameDirectory);

    /// <summary>Why "next to the source" is off for a source in <paramref name="sourceFolder"/>.</summary>
    private string NoNextToReason(string? sourceFolder) => IsGameFolder(sourceFolder) ? "that is the game directory" : "it has no folder";

    /// <summary>The folder a batch writes into when nothing else says: the remembered one, else Documents (never the game directory).</summary>
    private string DefaultFolder() =>
        Settings.Folder is { Length: > 0 } f && !IsGameFolder(f) ? f : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    /// <summary>"Convert..." on a tab: the window, then the conversion.</summary>
    internal void ConvertDocument(SndDocument doc) => _ = ConvertDocumentAsync(doc);

    /// <summary>
    /// Converts <paramref name="doc"/>'s sound with <paramref name="choice"/>, or with what the Convert window settles on
    /// when null (self-tests pass a choice). Returns the names or paths written, or null when cancelled or failed. The
    /// output never takes the source's place: a sound already in the chosen format is not converted, an output name
    /// never equals the source's, and a file or entry of that name is only replaced after asking.
    /// </summary>
    public Task<IReadOnlyList<string>?> ConvertDocumentAsync(SndDocument doc, SndConvertChoice? choice = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        string? packfile = PackfileOf(doc), nextTo = NextToFolder(doc);
        if (choice is null)
        {
            if (base.Shell.IsDiagnosticRun) return Task.FromResult<IReadOnlyList<string>?>(null);
            var window = new SndConvertWindow(base.Shell.Dialogs, [doc.DisplayName], packfile, nextTo, Settings, o => SoundConversion.Notes(doc.Sound, o),
                f => SoundConversion.SameFormatReason(doc.Sound, f), NoNextToReason(SourceFolder(doc))) { Owner = base.Shell.MainWindow };
            if (window.ShowDialog() != true || window.Result is not { } chosen) return Task.FromResult<IReadOnlyList<string>?>(null);
            Remember(chosen);
            choice = chosen;
        }
        var result = SoundConversion.Convert(doc.Sound, choice.Options);
        if (result.Bytes is null)
        {
            base.Shell.Dialogs.ShowError($"Could not convert {doc.DisplayName}", Capitalise(result.Error ?? "the sound could not be converted."));
            return Task.FromResult<IReadOnlyList<string>?>(null);
        }
        IReadOnlyList<string>? written;
        string format = SoundConversion.ExtensionOf(choice.Options.Format).TrimStart('.').ToUpperInvariant();
        if (choice.Target == SoundTarget.IntoPackfile && doc.FilePath is { } path && ArchiveTarget is { } target)
        {
            bool replace = false;
            if (choice.Replace && target.EntryNamesOf(path) is { } names && names.Contains(result.OutputName, StringComparer.OrdinalIgnoreCase))
            {
                if (AskReplace(result.OutputName, packfile ?? "the packfile") is not { } answer) return Task.FromResult<IReadOnlyList<string>?>(null);
                replace = answer;
            }
            written = target.AddFiles(path, $"Convert {doc.DisplayName} to {format}", [(result.OutputName, result.Bytes)], replace);
            if (written is { Count: > 0 }) doc.ShowStatus($"Added {written[0]} to {packfile} (undo in the packfile's tab; save the packfile to keep it)");
        }
        else
        {
            if ((choice.Folder ?? nextTo) is not { Length: > 0 } folder)
            {
                base.Shell.Dialogs.ShowError($"Could not convert {doc.DisplayName}",
                    $"Next to the source is not available ({NoNextToReason(SourceFolder(doc))}). Convert again and choose a folder.");
                return Task.FromResult<IReadOnlyList<string>?>(null);
            }
            bool replace = false;
            if (choice.Replace && File.Exists(Path.Combine(folder, result.OutputName)))
            {
                if (AskReplace(result.OutputName, folder) is not { } answer) return Task.FromResult<IReadOnlyList<string>?>(null);
                replace = answer;
            }
            written = WriteFiles(folder, [result], replace, out var errors, doc.FilePath);
            if (errors.Count > 0) base.Shell.Dialogs.ShowError($"Could not write {result.OutputName}", string.Join(Environment.NewLine, errors));
            else doc.ShowStatus($"Wrote {Path.GetFileName(written[0])} to {Path.GetDirectoryName(written[0])}");
        }
        return Task.FromResult(written);
    }

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>"x.wav already exists": true to replace it, false to keep both (a free name), null to cancel.</summary>
    private bool? AskReplace(string name, string where) =>
        base.Shell.Dialogs.Choose($"{name} already exists", $"{where} already has {name}. Replace it with the converted sound, or keep both (the new file gets a free name)?",
            ["Replace", "Keep both", "Cancel"], 2) switch { 0 => true, 1 => false, _ => null };

    /// <summary>Remembers the choice for next time, except Replace (asked for each conversion, never carried over).</summary>
    private void Remember(SndConvertChoice choice)
    {
        if (base.Shell.IsDiagnosticRun) return;
        Settings.Format = choice.Options.Format;
        if (choice.Options.Format == SoundOutputFormat.Ogg) Settings.Quality = choice.Options.Quality;
        Settings.WriteLoop = choice.Options.WriteLoop;
        Settings.Target = choice.Target;
        if (choice.Target == SoundTarget.Folder && choice.Folder is { } folder) Settings.Folder = folder;
    }

    /// <summary>
    /// Writes the converted files into <paramref name="folder"/> (a free name for each taken one unless replacing). A path
    /// in <paramref name="protectedPath"/> (the source) is never written, even when replacing: the file gets a free name.
    /// </summary>
    internal static IReadOnlyList<string> WriteFiles(string folder, IReadOnlyList<SoundConvertResult> results, bool replace, out List<string> errors, string? protectedPath = null)
    {
        errors = [];
        var written = new List<string>();
        try { Directory.CreateDirectory(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            errors.Add($"{folder}: {ex.Message}");
            return written;
        }
        string? source = null;
        try { if (protectedPath is not null) source = Path.GetFullPath(protectedPath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        bool IsSource(string p) => source is not null && string.Equals(Path.GetFullPath(p), source, StringComparison.OrdinalIgnoreCase);
        foreach (var r in results.Where(r => r.Bytes is not null))
        {
            string path = Path.Combine(folder, r.OutputName);
            if (!replace || IsSource(path))
            {
                string stem = Path.GetFileNameWithoutExtension(r.OutputName), ext = Path.GetExtension(r.OutputName);
                for (int n = 2; File.Exists(path) || IsSource(path) || written.Contains(path, StringComparer.OrdinalIgnoreCase); n++) path = Path.Combine(folder, $"{stem} ({n}){ext}");
            }
            try
            {
                AtomicFile.WriteAllBytes(path, r.Bytes!);
                written.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{r.OutputName}: {ex.Message}");
            }
        }
        return written;
    }

    // ── converting a batch from a packfile (IArchiveBatchConverter) ───────────────────────────────────────────

    public string CommandText => "Convert _sounds...";
    public string CommandToolTip => "Convert the selected sounds (PS2 .vse/.vmu, .wav, .ogg, .aif) to WAV or Ogg Vorbis: into the packfile as new entries (one undo step) or into a folder";

    public bool CanConvert(string entryName) => SoundDecoder.CanDecode(entryName);

    public async Task<string?> ConvertAsync(ArchiveBatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var entries = request.Entries.Where(e => CanConvert(e.Name)).ToList();
        if (entries.Count == 0) return null;
        string? packfile = request.AddFiles is null ? null : request.ArchiveName;
        // never the game directory by default: a loose sound there changes what the game loads
        string? nextTo = IsGameFolder(request.ArchiveFolder) ? null : request.ArchiveFolder;
        SndConvertChoice? choice = request.Options as SndConvertChoice;
        if (choice is null && request.Interactive && !base.Shell.IsDiagnosticRun)
        {
            var window = new SndConvertWindow(base.Shell.Dialogs, [.. entries.Select(e => e.Name)], packfile, nextTo, Settings, null,
                noNextToReason: NoNextToReason(request.ArchiveFolder)) { Owner = base.Shell.MainWindow };
            if (window.ShowDialog() != true || window.Result is not { } chosen) return null;
            Remember(chosen);
            choice = chosen;
        }
        choice ??= new SndConvertChoice(packfile is not null ? SoundTarget.IntoPackfile : SoundTarget.Folder,
            nextTo ?? DefaultFolder(),
            new SoundConvertOptions { Format = Settings.Format == SoundOutputFormat.Ogg && OggVorbisWriter.IsAvailable ? SoundOutputFormat.Ogg : SoundOutputFormat.Wav, WriteLoop = Settings.WriteLoop, Quality = Settings.Quality },
            Replace: false);

        var results = new List<SoundConvertResult>();
        Task Work(IProgress<(double Fraction, string Item)> progress, CancellationToken ct) => Task.Run(() =>
        {
            for (int i = 0; i < entries.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress.Report(((double)i / entries.Count, entries[i].Name));
                byte[] bytes;
                try { bytes = entries[i].Read(); }
                catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
                {
                    results.Add(new SoundConvertResult(entries[i].Name, SoundConversion.OutputName(entries[i].Name, choice.Options.Format), null, [], ex.Message));
                    continue;
                }
                // one bad file is listed, never the end of the batch (Convert reports any decoder failure as an error)
                results.Add(SoundConversion.Convert(entries[i].Name, bytes, choice.Options));
            }
            progress.Report((1, "done"));
        }, ct);
        bool finished;
        if (request.RunAsync is { } run) finished = await run("Converting sounds", Work);
        else { await Work(new Progress<(double, string)>(), CancellationToken.None); finished = true; }
        if (!finished) return null;

        bool intoPackfile = choice.Target == SoundTarget.IntoPackfile && request.AddFiles is not null;
        string folder = choice.Folder ?? nextTo ?? DefaultFolder();
        string place = intoPackfile ? request.ArchiveName : folder;
        // Names: two selected sounds that give one name (x.wav and x.aif to x.ogg) keep one, the game's .wav first; a name
        // already in the packfile or folder is left alone unless Replace is ticked and confirmed.
        LeaveOutNameClashes(results);
        var existing = intoPackfile ? new HashSet<string>(request.AllEntries.Select(e => e.Name), StringComparer.OrdinalIgnoreCase) : null;
        bool Taken(SoundConvertResult r) => existing?.Contains(r.OutputName) ?? SafeExists(Path.Combine(folder, r.OutputName));
        var taken = results.Where(r => r.Succeeded && Taken(r)).ToList();
        bool replace = false;
        if (taken.Count > 0 && choice.Replace)
        {
            if (!request.Interactive || base.Shell.IsDiagnosticRun) replace = true;
            else
            {
                string list = string.Join("\n", taken.Take(8).Select(r => "• " + r.OutputName)) + (taken.Count > 8 ? $"\n... and {taken.Count - 8:N0} more" : "");
                int answer = base.Shell.Dialogs.Choose($"{taken.Count:N0} converted name{(taken.Count == 1 ? " is" : "s are")} taken",
                    $"{place} already has:\n{list}\n\nReplace them with the converted sounds, or leave them as they are and skip those sounds?",
                    ["Replace them", "Skip them", "Cancel"], 2);
                if (answer is not (0 or 1)) return null;
                replace = answer == 0;
            }
        }
        if (!replace)
            for (int i = 0; i < results.Count; i++)
                if (results[i].Succeeded && Taken(results[i]))
                    results[i] = results[i] with { Bytes = null, Skipped = true, Error = $"{place} already has {results[i].OutputName}; it was left as it is (tick Replace to replace it)" };

        var ok = results.Where(r => r.Succeeded).ToList();
        string format = SoundConversion.ExtensionOf(choice.Options.Format).TrimStart('.').ToUpperInvariant();
        string where;
        var errors = new List<string>();
        if (intoPackfile && request.AddFiles is { } add)
        {
            string label = ok.Count == 1 ? $"Convert {ok[0].SourceName} to {format}" : $"Convert {ok.Count:N0} sounds to {format}";
            var names = ok.Count == 0 ? [] : add(label, [.. ok.Select(r => (r.OutputName, r.Bytes!))], replace);
            where = $" ({names.Count:N0} added to {request.ArchiveName}; Undo removes them)";
        }
        else
        {
            var written = WriteFiles(folder, ok, replace, out errors);
            where = $" ({written.Count:N0} written to {folder})";
        }
        string summary = SoundConversion.Summary(results, choice.Options.Format).TrimEnd('.') + where + ".";
        LastBatch = results;
        if (request.Interactive && !base.Shell.IsDiagnosticRun) ShowReport(summary, results, errors);
        return summary;
    }

    /// <summary>The last batch's results (self-tests).</summary>
    internal IReadOnlyList<SoundConvertResult>? LastBatch { get; private set; }

    private static bool SafeExists(string path)
    {
        try { return File.Exists(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary>
    /// Leaves out all but one of the converted sounds that share an output name (x.wav and x.aif both give x.ogg): the
    /// one from a .wav (what the game plays), else the first; the others are reported, as the mesh batch does.
    /// </summary>
    internal static void LeaveOutNameClashes(List<SoundConvertResult> results)
    {
        static int Rank(string source) => Path.GetExtension(source).ToLowerInvariant() switch { ".wav" => 0, ".ogg" => 1, _ => 2 };
        foreach (var group in results.Select((r, i) => (Result: r, Index: i)).Where(p => p.Result.Succeeded).GroupBy(p => p.Result.OutputName, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() < 2) continue;
            var keep = group.OrderBy(p => Rank(p.Result.SourceName)).ThenBy(p => p.Index).First();
            foreach (var (r, i) in group)
                if (i != keep.Index)
                    results[i] = r with { Bytes = null, Skipped = true, Error = $"{keep.Result.SourceName} also makes {r.OutputName} and is converted instead; this one was left out" };
        }
    }

    /// <summary>The batch report: failures first, then what was left out, then each kind of approximation once with how many sounds it concerns.</summary>
    private void ShowReport(string summary, IReadOnlyList<SoundConvertResult> results, IReadOnlyList<string> errors)
    {
        static string Block(string heading, List<string> lines, int max) =>
            lines.Count == 0 ? "" : $"\n\n{heading}\n" + string.Join("\n", lines.Take(max)) + (lines.Count > max ? $"\n... and {lines.Count - max:N0} more" : "");
        var failed = results.Where(r => !r.Succeeded && !r.Skipped).Select(r => $"• {r.SourceName}: {r.Error}").Concat(errors.Select(e => "• " + e)).ToList();
        var leftOut = results.Where(r => r.Skipped).Select(r => $"• {r.SourceName}: {r.Error}").ToList();
        var notes = results.Where(r => r.Succeeded).SelectMany(r => r.Notes.Select(n => (Key: GenericNote(n), r.SourceName)))
            .GroupBy(n => n.Key).Select(g => g.Count() == 1 ? $"• {g.First().SourceName}: {g.Key}" : $"• {g.Key} ({g.Count():N0} sounds)").ToList();
        string text = summary + Block("Not converted:", failed, 10) + Block("Left out:", leftOut, 10) + Block("What the conversion changes:", notes, 12);
        base.Shell.Dialogs.Choose("Sounds converted", text, ["OK"], 0);
    }

    /// <summary>A note with its numbers taken out, so the same remark about many sounds is listed once.</summary>
    private static string GenericNote(string note)
    {
        if (note.StartsWith("Written at ", StringComparison.Ordinal)) return "Written at the standard rate; the console plays PS2 sounds slightly slower (its SPU pitch).";
        if (note.StartsWith("The loop (", StringComparison.Ordinal))
            return note.Contains("'smpl'", StringComparison.Ordinal) ? "Loops are written in a 'smpl' chunk."
                : note.Contains("LOOPSTART", StringComparison.Ordinal) ? "Loops are written as LOOPSTART/LOOPLENGTH comments." : "Loops are not kept.";
        return note;
    }

    // ── diagnostic options ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Diagnostic runs (applied to the first sound that becomes active): <c>--snd-zoom n</c> (samples per pixel),
    /// <c>--snd-scroll seconds</c> (left edge), <c>--snd-position seconds</c> (play head), <c>--snd-loop on|off</c>.
    /// </summary>
    public override void ApplyDiagnosticOptions(IReadOnlyDictionary<string, string> options)
    {
        if (!options.Keys.Any(k => k.StartsWith("snd-", StringComparison.OrdinalIgnoreCase))) return;
        if (base.Shell.ActiveDocument is SndDocument doc) { ApplyOptions(doc, options); return; }
        _pendingOptions = options;
        base.Shell.ActiveDocumentChanged += OnFirstActive;
    }

    private void OnFirstActive(object? sender, EventArgs e)
    {
        if (base.Shell.ActiveDocument is not SndDocument doc || _pendingOptions is not { } options) return;
        base.Shell.ActiveDocumentChanged -= OnFirstActive;
        _pendingOptions = null;
        base.Shell.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => ApplyOptions(doc, options));
    }

    internal static void ApplyOptions(SndDocument doc, IReadOnlyDictionary<string, string> options)
    {
        static double? Number(IReadOnlyDictionary<string, string> o, string key) =>
            o.TryGetValue(key, out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
        if (options.TryGetValue("snd-loop", out var loop)) doc.Looping = !loop.Equals("off", StringComparison.OrdinalIgnoreCase);
        if (Number(options, "snd-position") is { } seconds) doc.Seek((long)(seconds * doc.Sound.SampleRate));
        if (doc.View is SndDocumentView view)
        {
            view.UpdateLayout();
            double zoom = Number(options, "snd-zoom") is { } z && z > 0 ? z : view.Wave.FramesPerPixel;
            if (options.ContainsKey("snd-zoom") || options.ContainsKey("snd-scroll"))
                view.SetView(zoom, (Number(options, "snd-scroll") ?? 0) * doc.Sound.SampleRate);
        }
    }

    // ── help ──────────────────────────────────────────────────────────────────────────────────────────────────

    private static FlowDocument BuildHelp()
    {
        var doc = new FlowDocument { PagePadding = new Thickness(16), FontFamily = new System.Windows.Media.FontFamily("Segoe UI"), FontSize = 13 };
        doc.SetResourceReference(FlowDocument.ForegroundProperty, "App.Text");
        doc.SetResourceReference(FlowDocument.BackgroundProperty, "App.PaneBackground");
        void Heading(string text) => doc.Blocks.Add(new Paragraph(new Run(text)) { FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) });
        void Para(string text) => doc.Blocks.Add(new Paragraph(new Run(text)) { Margin = new Thickness(0, 0, 0, 6) });
        doc.Blocks.Add(new Paragraph(new Run("Sounds")) { FontSize = 20, FontWeight = FontWeights.SemiBold });
        Para("The Sounds module opens sound files in read-only tabs: the PlayStation 2 version's sound effects (.vse) and music (.vmu), " +
             "and the PC's .wav, .ogg and .aif files. A tab shows the waveform of each channel, plays the sound (Space), repeats its loop " +
             "(L), and lists the format, codec, sample rate, channels, bit depth, duration, loop points, size, the header's fields and " +
             "what is special about the format. Damaged data is decoded as well as it can be and listed in the Problems tab.");
        Heading("PlayStation 2 sounds");
        Para(".vse files are mono sound effects at 11,025, 22,050 or 44,100 Hz; .vmu files are stereo music at 44,100 Hz. Both use the " +
             "console's 4-bit ADPCM (\"PS ADPCM\"), which is lossy: Cairn decodes it exactly as the console's sound chip does. The rate is " +
             "stored as a pitch value, so the console plays a 22,050 Hz sound at 22,043 Hz; converted files use the standard rate. Looping " +
             "sounds carry their loop in the data, in steps of 28 samples. Cairn never writes .vse or .vmu files.");
        Heading("Converting");
        Para("Sound > Convert... (Ctrl+Shift+E) writes a file with the same name: into the packfile the sound came from as a new " +
             "entry (one undo step in the packfile's tab), next to the source, or into a folder. The format is a 16-bit WAV (the decoded " +
             "sound exactly; a loop is kept in a 'smpl' chunk) or Ogg Vorbis (much smaller and lossy; a loop is kept as LOOPSTART/LOOPLENGTH " +
             "comments). Ogg Vorbis files are made with the Xiph.Org reference encoder; the quality runs from q-1 (smallest) to q10 (best), " +
             "and the default q5 is about 160 kbit/s for 44.1 kHz stereo and far less for mono effects. Some players and tools read the " +
             "loop points; the game loops a sound because a table or level asks it to. The window lists what the conversion approximates. " +
             "In a packfile, select sounds and use Packfile > Convert sounds... (also on the list's right-click menu) to convert them all " +
             "at once. Save As on a sound tab writes a WAV. Settings > Sounds holds the defaults. A conversion never takes its source's " +
             "place: a format the sound is in already is not offered, an output is never named like its source, a taken name is only " +
             "replaced when Replace is ticked and Cairn has asked, and next to the source is off when that is the game directory.");
        Heading("Limits");
        Para("Sounds are not edited, and Save As writes WAV only (use Convert for Ogg Vorbis). The stock game loads .wav; Alpine Faction " +
             "also loads .ogg. MP3 files are previewed in packfiles but not opened in tabs.");
        return doc;
    }
}

/// <summary>The sound document kind (read-only tabs; Save As writes a WAV).</summary>
public sealed class SndKind(SndModule module) : IDocumentKind
{
    public string Id => "snd";
    public string DisplayName => "Sound";
    public IReadOnlyList<string> Extensions { get; } = [".wav", ".ogg", ".aif", ".aiff", ".aifc", ".vse", ".vmu"];
    public string FileFilter => "Sounds (*.wav;*.ogg;*.aif;*.aiff;*.vse;*.vmu)|*.wav;*.ogg;*.aif;*.aiff;*.aifc;*.vse;*.vmu";
    public bool CanCreateNew => false;
    public string AssociationDescription => "Sound";

    public IDocument? CreateNew() => null;

    public IDocument Open(string path) => Create(AtomicFile.ReadAllBytes(path), Path.GetFileName(path), path, null);

    public IDocument OpenBytes(byte[] bytes, string displayName, string originText) => Create(bytes, displayName, null, originText);

    /// <summary>Sounds keep no recovery data (they are never edited); a snapshot is opened as it was.</summary>
    public IDocument Restore(RecoverySnapshot snapshot) => Create(snapshot.Data, snapshot.DisplayName, null, "Recovered");

    private SndDocument Create(byte[] bytes, string name, string? path, string? origin)
    {
        // a work copy of a packfile entry is named after the entry
        string decodeName = name;
        var sound = SoundDecoder.Decode(bytes, decodeName);
        return module.Track(new SndDocument(module, this, bytes, sound, name, path, origin));
    }
}
