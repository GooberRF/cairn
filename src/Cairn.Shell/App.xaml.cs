using System.Windows;
using System.Windows.Threading;
using Cairn.Ui.Modules;
using Cairn.Ui.Services;
using Cairn.Workspace;

namespace Cairn.Shell;

/// <summary>Startup: command line, settings, theme, modules, single instance, crash handling.</summary>
public partial class App : Application
{
    private SingleInstance? _instance;
    private ShellViewModel? _shell;
    private AppSettings _settings = new();
    private CommandLine _options = new();
    private IReadOnlyList<IModule> _modules = [];

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _options = CommandLine.Parse(e.Args);
        // Diagnostic runs log next to their throwaway recovery folders, never in the user's profile.
        CrashLogPath = _options.IsDiagnostic
            ? Path.Combine(Path.GetTempPath(), "Cairn-diagnostics", "crash.log")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cairn", "crash.log");
        Dialogs.CrashDialog.LogPath = CrashLogPath;
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
        TaskScheduler.UnobservedTaskException += OnTaskException;
        var diagnostic = _options.IsDiagnostic;

        if (!diagnostic)
        {
            _instance = SingleInstance.Acquire("Cairn");
            if (!_instance.IsFirstInstance && _instance.TryForward(_options.Files))
            {
                Shutdown();
                return;
            }
            FirstRunImport.Run();
        }

        // "--user-settings": a diagnostic run that reads (never writes) the user's settings, e.g. for a screenshot of
        // their own game folder and Recent list.
        _settings = diagnostic && !_options.ModuleOptions.ContainsKey("user-settings") ? new AppSettings() : SettingsStore.Load();
        // Diagnostic runs: the research game directory when settings have none. The shell's AssetHost is the
        // one source of game data for every module; modules do not fall back on their own.
        if (diagnostic && string.IsNullOrEmpty(_settings.GameDirectory) && Cairn.Workspace.LocalPaths.GameDirectory is { } game)
            _settings.GameDirectory = game;
        if (_options.Theme is { } forced) _settings.Theme = forced;
        var theme = new ThemeService(this);
        theme.Apply(_settings.Theme);

        _modules = ModuleCatalog.Create(_options.ProbeModule);
        foreach (var dictionary in _modules.SelectMany(m => m.Resources)) Resources.MergedDictionaries.Add(dictionary);

        var window = new MainWindow();
        var dialogs = new ShellDialogs { Owner = window, CollectErrors = diagnostic ? [] : null };
        var recovery = diagnostic ? new RecoveryStore(Path.Combine(Path.GetTempPath(), "Cairn-diagnostics", Guid.NewGuid().ToString("N"))) : new RecoveryStore();
        _shell = new ShellViewModel(window, _settings, theme, _modules, diagnostic, dialogs, recovery);
        // Before the modules initialise (diagnostic runs included): every module reads this one host.
        _shell.Assets.Reconfigure(_settings);
        foreach (var module in _modules) module.Initialize(_shell);
        _shell.SaveRecent(); // the modules know their work areas now: old work-copy paths leave the Recent list
        // Diagnostic options reach the modules from DiagnosticsRunner, after the command-line files are open.
        window.Attach(_shell);

        MainWindow = window;
        if (diagnostic)
        {
            _ = DiagnosticsRunner.RunAsync(window, _shell, _options);
            return;
        }

        _instance?.StartServer(files => Dispatcher.BeginInvoke(() =>
        {
            _shell.OpenFiles(files);
            window.BringToFront();
        }));
        window.Show();
        _shell.OfferRecovery();
        foreach (var f in _options.Files) _shell.OpenFile(f);
    }

    /// <summary>Saves settings unless this is a diagnostic run; called by the main window as it closes.</summary>
    public void SaveSettings()
    {
        if (_options.IsDiagnostic) return;
        SettingsStore.Save(_settings);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        foreach (var module in _modules)
        {
            try { module.OnShutdown(); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException) { }
        }
        _instance?.Dispose();
        base.OnExit(e);
    }

    // ---- crash handling ----

    private static readonly TimeSpan SnapshotTimeout = TimeSpan.FromSeconds(5);
    private int _handlingCrash;

    /// <summary>Where unhandled exceptions are logged: <c>%LOCALAPPDATA%\Cairn\crash.log</c>, or a temp file in diagnostic runs.</summary>
    public static string CrashLogPath { get; private set; } = string.Empty;

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e) =>
        e.Handled = Report(e.Exception, fatal: false);

    private void OnDomainException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception) Report(exception, fatal: e.IsTerminating);
    }

    private void OnTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        // A background task that failed and was never awaited: log it, never interrupt the user.
        WriteCrashLog("unobserved task exception", e.Exception.ToString());
    }

    /// <summary>
    /// Snapshots every dirty document, logs what happened, and tells the user when there is a UI
    /// thread to tell them on. Returns true when the app can keep running.
    /// </summary>
    private bool Report(Exception exception, bool fatal)
    {
        if (Interlocked.Exchange(ref _handlingCrash, 1) == 1)
        {
            // A second fault while the crash dialog is up (its modal loop keeps timers running):
            // log it and keep going; the first report already saved the documents.
            WriteCrashLog(fatal ? "unhandled exception during crash report (fatal)" : "unhandled exception during crash report", exception.ToString());
            return !fatal;
        }
        try
        {
            var dirty = DirtyCount();
            var rescued = SnapshotSafely();
            var body = rescued.Count >= dirty && rescued.Count > 0
                ? "Cairn hit a problem it did not expect. Your unsaved documents have been copied to the recovery folder, so nothing is lost."
                : rescued.Count > 0
                    ? $"Cairn hit a problem it did not expect. {rescued.Count} of {dirty} unsaved documents were copied to the recovery folder; save the others now if you can."
                    : dirty > 0
                        ? "Cairn hit a problem it did not expect, and the unsaved documents could not be copied to the recovery folder. Save them now if you can."
                        : "Cairn hit a problem it did not expect. No unsaved work was open, so nothing was lost.";
            if (fatal) body += " The app has to close.";
            var details = exception + Environment.NewLine + Environment.NewLine + "Recovery folder: " + (_shell?.Recovery.Directory ?? string.Empty);
            WriteCrashLog(fatal ? "unhandled exception (fatal)" : "unhandled exception",
                details + Environment.NewLine + "Rescued: " + (rescued.Count > 0 ? string.Join(", ", rescued) : "nothing was dirty"));
            if (_options.IsDiagnostic)
            {
                // Self-tests and captures must fail loudly rather than wait on a dialog.
                Console.Error.WriteLine(details);
                if (Dispatcher.CheckAccess()) Shutdown(3);
                return true;
            }
            ShowCrashDialog(body, rescued, details);
            return !fatal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _handlingCrash, 0);
        }
    }

    private int DirtyCount()
    {
        try
        {
            return _shell is null ? 0 : Dispatcher.CheckAccess() ? _shell.OpenDocuments.Count(d => d.IsDirty)
                : Dispatcher.Invoke(() => _shell.OpenDocuments.Count(d => d.IsDirty), DispatcherPriority.Send, CancellationToken.None, SnapshotTimeout);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            return 0;
        }
    }

    private IReadOnlyList<string> SnapshotSafely()
    {
        var shell = _shell;
        if (shell is null) return [];
        try
        {
            if (Dispatcher.CheckAccess()) return shell.SnapshotRecovery();
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return [];
            return Dispatcher.Invoke(shell.SnapshotRecovery, DispatcherPriority.Send, CancellationToken.None, SnapshotTimeout);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void ShowCrashDialog(string body, IReadOnlyList<string> rescued, string details)
    {
        var dialogs = _shell?.Dialogs;
        void Show()
        {
            if (dialogs is not null) dialogs.ShowCrash(body, rescued, details);
            else Dialogs.CrashDialog.Show(null, body, rescued, details);
        }
        try
        {
            if (Dispatcher.CheckAccess()) { Show(); return; }
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Dispatcher.Invoke(Show, DispatcherPriority.Send, CancellationToken.None, SnapshotTimeout);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or OperationCanceledException)
        {
            WriteCrashLog("crash dialog", ex.ToString());
        }
    }

    private static void WriteCrashLog(string what, string details)
    {
        if (CrashLogPath.Length == 0) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            File.AppendAllText(CrashLogPath, $"[{DateTime.Now:u}] {what}{Environment.NewLine}{details}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}