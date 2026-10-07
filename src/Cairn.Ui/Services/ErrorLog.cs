using System;
using System.IO;
using System.Text;

namespace Cairn.Ui.Services;

/// <summary>
/// A last-resort record of something that went wrong, appended to
/// <c>%LOCALAPPDATA%\Cairn\crash.log</c>.
///
/// It exists because the two places an editor most needs to say something — a crash handler on a
/// background thread, and a fire-and-forget task — are exactly the two places where showing a
/// dialog is not allowed. Every method swallows its own failures: logging must never be the thing
/// that brings the app down.
/// </summary>
public static class ErrorLog
{
    private static readonly object Gate = new();

    /// <summary>Where the log is written.</summary>
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Cairn", "crash.log");

    /// <summary>Appends one entry describing an exception.</summary>
    /// <param name="context">What the app was doing, e.g. "asset lint".</param>
    /// <param name="exception">The failure.</param>
    public static void Write(string context, Exception exception)
    {
        if (exception is null) return;
        var text = new StringBuilder()
            .Append(exception.GetType().Name).Append(": ").AppendLine(exception.Message)
            .AppendLine(exception.StackTrace);
        Write(context, text.ToString());
    }

    /// <summary>Appends one entry with details already formatted.</summary>
    /// <param name="context">What the app was doing.</param>
    /// <param name="details">The body of the entry.</param>
    public static void Write(string context, string details)
    {
        try
        {
            string directory = System.IO.Path.GetDirectoryName(Path)!;
            var entry = new StringBuilder()
                .AppendLine()
                .Append("──── ").Append(DateTime.Now.ToString("u", System.Globalization.CultureInfo.InvariantCulture))
                .Append(" · ").Append(context).AppendLine(" ────")
                .AppendLine(details);

            lock (Gate)
            {
                Directory.CreateDirectory(directory);
                Trim();
                File.AppendAllText(Path, entry.ToString(), Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            // Nothing useful is left to do; the app is already in trouble.
        }
    }

    /// <summary>Starts the log over once it passes a quarter of a megabyte.</summary>
    private static void Trim()
    {
        try
        {
            var info = new FileInfo(Path);
            if (info.Exists && info.Length > 256 * 1024) info.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
