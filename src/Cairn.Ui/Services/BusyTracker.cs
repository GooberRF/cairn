namespace Cairn.Ui.Services;

/// <summary>
/// Counts background work in flight (library build, table load, mesh and texture loads, async lint).
/// Every piece of work the UI is waiting on takes a token from <see cref="Begin"/> and disposes it when
/// done. The <c>--screenshot</c> switch waits until the count has stayed at zero for a moment, which
/// is what makes its pictures show a settled window rather than a half-loaded one. Thread-safe.
/// </summary>
public static class BusyTracker
{
    private static int _count;
    private static long _lastChangeTicks = Environment.TickCount64;
    private static readonly object Gate = new();
    private static readonly Dictionary<long, string> Active = [];
    private static long _nextId;

    /// <summary>Work items currently running.</summary>
    public static int Count => Volatile.Read(ref _count);

    /// <summary>Milliseconds since the count last changed.</summary>
    public static long QuietMilliseconds => Environment.TickCount64 - Interlocked.Read(ref _lastChangeTicks);

    /// <summary>What is running now, for a diagnostic that timed out.</summary>
    public static IReadOnlyList<string> Describe()
    {
        lock (Gate) return [.. Active.Values];
    }

    /// <summary>Counts one piece of work in until the returned token is disposed.</summary>
    /// <param name="what">A short description, for diagnostics.</param>
    public static IDisposable Begin(string what)
    {
        long id;
        lock (Gate)
        {
            id = ++_nextId;
            Active[id] = what;
        }
        Interlocked.Increment(ref _count);
        Interlocked.Exchange(ref _lastChangeTicks, Environment.TickCount64);
        return new Token(id);
    }

    private sealed class Token(long id) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 1) return;
            lock (Gate) Active.Remove(id);
            Interlocked.Decrement(ref _count);
            Interlocked.Exchange(ref _lastChangeTicks, Environment.TickCount64);
        }
    }
}
