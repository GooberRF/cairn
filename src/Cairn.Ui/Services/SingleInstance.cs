using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Cairn.Ui.Services;

/// <summary>
/// Keeps one window per user: a second launch hands its file arguments to the first and exits, so
/// double-clicking three .atx files opens three tabs rather than three windows.
///
/// The rule this has to obey is that it may never make the app worse. A first instance that is busy,
/// hung, or half-dead must not stop a new one from starting, so every step is on a short timeout and
/// every failure means "just start normally". Both the mutex and the pipe are per-user, so two people
/// on the same machine never collide.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    /// <summary>How long a second instance waits for the first before giving up on it.</summary>
    private const int ForwardTimeoutMs = 1200;

    /// <summary>
    /// The most a forwarded message may be. Command lines cannot get anywhere near this; the cap is
    /// there so that whatever connects to the pipe cannot stream into the window's memory forever.
    /// </summary>
    private const int MaxMessageBytes = 64 * 1024;

    /// <summary>The most paths one launch may forward.</summary>
    private const int MaxForwardedPaths = 100;

    /// <summary>
    /// How many times the listener may fail to take the pipe before it gives up. Something else
    /// holding the name is not going to stop holding it, and a loop that retries forever is just a
    /// busy background thread for the rest of the session.
    /// </summary>
    private const int MaxListenFailures = 5;

    private readonly string _pipeName;
    private readonly Mutex? _mutex;
    private readonly CancellationTokenSource _shutdown = new();
    private bool _disposed;

    private SingleInstance(string pipeName, Mutex? mutex, bool isFirst)
    {
        _pipeName = pipeName;
        _mutex = mutex;
        IsFirstInstance = isFirst;
    }

    /// <summary>True when this process owns the single-instance slot and should show a window.</summary>
    public bool IsFirstInstance { get; }

    /// <summary>
    /// Claims the slot for this process. Never throws: if the mutex cannot be created at all — a
    /// locked-down machine, a sandbox — the process simply behaves as the first instance.
    /// </summary>
    /// <param name="identity">A name unique to the application.</param>
    public static SingleInstance Acquire(string identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        string suffix = UserSuffix();
        string mutexName = $"Local\\{identity}.{suffix}.instance";
        string pipeName = $"{identity}.{suffix}.pipe";

        try
        {
            var mutex = new Mutex(initiallyOwned: false, mutexName, out bool created);
            bool owned = false;
            try
            {
                // A previous instance that crashed leaves the mutex abandoned, which is ours to take.
                owned = mutex.WaitOne(0, false);
            }
            catch (AbandonedMutexException) { owned = true; }

            if (owned) return new SingleInstance(pipeName, mutex, isFirst: true);
            mutex.Dispose();
            return new SingleInstance(pipeName, null, isFirst: created);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
            or WaitHandleCannotBeOpenedException or NotSupportedException)
        {
            return new SingleInstance(pipeName, null, isFirst: true);
        }
    }

    /// <summary>
    /// Sends file paths to the running instance. Returns false — promptly — when there is nobody
    /// listening or the listener is not answering, which tells the caller to start normally.
    /// </summary>
    /// <param name="files">Paths to open; an empty list still asks the first window to come forward.</param>
    public bool TryForward(IReadOnlyList<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        try
        {
            // CurrentUserOnly on both ends: the client refuses to talk to a pipe another account
            // created, and the server refuses connections from one. Without it anyone signed in to
            // the machine could hand this window a list of files to open.
            using var client = new NamedPipeClientStream(
                ".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            client.Connect(ForwardTimeoutMs);

            byte[] payload = Encoding.UTF8.GetBytes(
                string.Join("\n", files.Take(MaxForwardedPaths)));
            if (payload.Length > MaxMessageBytes) return false;
            var write = client.WriteAsync(payload, 0, payload.Length);
            if (!write.Wait(ForwardTimeoutMs)) return false;
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException
            or UnauthorizedAccessException or AggregateException or ObjectDisposedException
            or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Starts listening for later launches. <paramref name="onFiles"/> is raised on a background
    /// thread, so the caller marshals to the UI itself.
    /// </summary>
    /// <param name="onFiles">Receives the forwarded paths, possibly an empty list.</param>
    public void StartServer(Action<IReadOnlyList<string>> onFiles)
    {
        ArgumentNullException.ThrowIfNull(onFiles);
        if (!IsFirstInstance) return;
        _ = Task.Run(() => ListenAsync(onFiles, _shutdown.Token));
    }

    private async Task ListenAsync(Action<IReadOnlyList<string>> onFiles, CancellationToken token)
    {
        int failures = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                // CurrentUserOnly puts an access-control list on the pipe granting only this
                // account, so nothing another user runs can connect to it.
                using var server = new NamedPipeServerStream(
                    _pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                string text = await ReadMessageAsync(server, token).ConfigureAwait(false);
                failures = 0;
                var files = text
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Take(MaxForwardedPaths)
                    .ToArray();
                onFiles(files);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or ObjectDisposedException or OperationCanceledException)
            {
                // A malformed or abandoned connection costs one iteration, not the listener. But a
                // name we can never take — something else is squatting on it — is permanent, and
                // retrying it for the rest of the session would only burn a thread.
                if (token.IsCancellationRequested) return;
                if (++failures >= MaxListenFailures) return;
                await Task.Delay(200, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Reads at most <see cref="MaxMessageBytes"/>, and gives up if the sender stops sending. A
    /// read-to-end would let whatever connected hold the thread, and the memory, indefinitely.
    /// </summary>
    private static async Task<string> ReadMessageAsync(Stream server, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(ForwardTimeoutMs * 5);

        var buffer = new byte[8192];
        using var message = new MemoryStream();
        while (message.Length < MaxMessageBytes)
        {
            int wanted = (int)Math.Min(buffer.Length, MaxMessageBytes - message.Length);
            int read = await server.ReadAsync(buffer.AsMemory(0, wanted), timeout.Token)
                .ConfigureAwait(false);
            if (read <= 0) break;
            message.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
    }

    /// <summary>A stable, per-user suffix so two accounts never share an instance.</summary>
    private static string UserSuffix()
    {
        try
        {
            string? sid = WindowsIdentity.GetCurrent().User?.Value;
            if (!string.IsNullOrEmpty(sid)) return sid!;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException) { }
        return Environment.UserName;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        _shutdown.Dispose();
        try { _mutex?.ReleaseMutex(); }
        catch (ApplicationException) { /* never owned it */ }
        _mutex?.Dispose();
    }
}
