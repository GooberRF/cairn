using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace Cairn.Ui.Services;

/// <summary>
/// Text on the Windows clipboard, tolerant of another program holding it open. the earlier ATX tool catches
/// the <see cref="COMException"/> and gives up; here a write or read that fails with
/// <c>CLIPBRD_E_CANT_OPEN</c> (0x800401D0: OpenClipboard failed because someone else has it open) is
/// tried again a few times after short pauses, on top of the ~1 s WPF already retries inside each call.
/// Any other failure is not retried. After a failure, the next calls within
/// <see cref="CoolDown"/> try once only, so a clipboard that stays wedged (seen on the phase 5/6
/// machine: every process got ERROR_ACCESS_DENIED from OpenClipboard) costs one WPF retry run per
/// copy or paste, not several seconds each time. Must be called on an STA thread (the UI thread).
/// </summary>
public static class SystemClipboard
{
    /// <summary>The HRESULT WPF throws when OpenClipboard fails.</summary>
    public const int ClipboardCantOpen = unchecked((int)0x800401D0);

    /// <summary>Pauses between our own attempts (ms); the attempt count is one more than this.</summary>
    private static readonly int[] Delays = [60, 200];

    /// <summary>How long after a failure further calls try only once.</summary>
    public static readonly TimeSpan CoolDown = TimeSpan.FromSeconds(10);

    private static long _lastFailure;
    private static bool _loggedSinceSuccess;

    /// <summary>The last failure (null after a success): what a caller can report.</summary>
    public static Exception? LastError { get; private set; }

    /// <summary>Puts <paramref name="text"/> on the clipboard as Unicode text (kept after the app exits).</summary>
    /// <returns>True when it landed on the system clipboard.</returns>
    public static bool TrySetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Run("clipboard write", () =>
        {
            Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), true);
            return true;
        }, out bool _);
    }

    /// <summary>Reads Unicode text from the clipboard.</summary>
    /// <param name="text">The text, or null when the clipboard holds none.</param>
    /// <returns>False when the clipboard could not be read at all (<paramref name="text"/> is then null).</returns>
    public static bool TryGetText(out string? text)
    {
        bool ok = Run("clipboard read", () =>
        {
            var data = Clipboard.GetDataObject();
            return data is not null && data.GetDataPresent(DataFormats.UnicodeText, true)
                ? data.GetData(DataFormats.UnicodeText, true) as string
                : null;
        }, out string? result);
        text = ok ? result : null;
        return ok;
    }

    /// <summary>Puts <paramref name="data"/> on the clipboard (several formats at once; kept after the app exits).</summary>
    /// <returns>True when it landed on the system clipboard.</returns>
    public static bool TrySetData(IDataObject data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Run("clipboard write", () =>
        {
            Clipboard.SetDataObject(data, true);
            return true;
        }, out bool _);
    }

    /// <summary>
    /// Reads the clipboard through <paramref name="read"/>, which gets the clipboard's data object (null when it is empty)
    /// and runs inside the same retries, so a format read that fails because the clipboard is busy is tried again.
    /// </summary>
    /// <returns>False when the clipboard could not be read at all (<paramref name="value"/> is then the default).</returns>
    public static bool TryRead<T>(Func<IDataObject?, T?> read, out T? value)
    {
        ArgumentNullException.ThrowIfNull(read);
        bool ok = Run("clipboard read", () => read(Clipboard.GetDataObject()), out T? result);
        value = ok ? result : default;
        return ok;
    }

    /// <summary>True when <paramref name="ex"/> is the "someone else has the clipboard open" failure.</summary>
    public static bool IsCantOpen(Exception ex) => ex is COMException { HResult: ClipboardCantOpen } || ex.HResult == ClipboardCantOpen;

    private static bool Run<T>(string what, Func<T> action, out T? result)
    {
        bool coolingDown = _lastFailure != 0 && Stopwatch.GetElapsedTime(_lastFailure) < CoolDown;
        int attempts = coolingDown ? 1 : Delays.Length + 1;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                result = action();
                _lastFailure = 0;
                _loggedSinceSuccess = false;
                LastError = null;
                return true;
            }
            catch (ExternalException ex) // COMException derives from it
            {
                if (IsCantOpen(ex) && attempt + 1 < attempts)
                {
                    Thread.Sleep(Delays[attempt]);
                    continue;
                }
                LastError = ex;
                _lastFailure = Stopwatch.GetTimestamp();
                // One crash-log entry per unavailable spell, not one per keystroke.
                if (!_loggedSinceSuccess) ErrorLog.Write(what, ex);
                _loggedSinceSuccess = true;
                result = default;
                return false;
            }
        }
    }
}
