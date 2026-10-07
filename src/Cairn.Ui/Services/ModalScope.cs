using System;

namespace Cairn.Ui.Services;

/// <summary>
/// How many modal dialogs are open. <c>ShowDialog</c> pumps the dispatcher, so anything queued with
/// <c>BeginInvoke</c> — a second instance's forwarded files, a file-watcher reload — runs *inside*
/// the modal, on top of a document the user cannot see and with a plan the dialog has already read.
/// Work that must not land under a dialog asks this first and defers to <see cref="Closed"/>.
///
/// Single-threaded by design: every modal in this app is shown from the UI thread.
/// </summary>
public static class ModalScope
{
    private static int _depth;

    /// <summary>Raised on the UI thread when the last modal closes.</summary>
    public static event EventHandler? Closed;

    /// <summary>True while at least one modal dialog is on screen.</summary>
    public static bool IsOpen => _depth > 0;

    /// <summary>Counts one modal in for as long as the returned token lives.</summary>
    public static IDisposable Enter() => new Scope();

    private sealed class Scope : IDisposable
    {
        private bool _left;

        internal Scope() => _depth++;

        public void Dispose()
        {
            if (_left) return;
            _left = true;
            if (--_depth > 0) return;
            if (_depth < 0) _depth = 0;
            Closed?.Invoke(null, EventArgs.Empty);
        }
    }
}
