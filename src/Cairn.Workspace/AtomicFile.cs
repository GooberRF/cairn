using System.Text;

namespace Cairn.Workspace;

/// <summary>
/// Thrown when a document is larger than this editor will open. Carries the numbers so the message
/// can name them.
/// </summary>
public sealed class FileTooLargeException : IOException
{
    /// <param name="path">The file that was refused.</param>
    /// <param name="length">Its size in bytes.</param>
    /// <param name="limit">The limit it broke.</param>
    public FileTooLargeException(string path, long length, long limit)
        : base($"'{System.IO.Path.GetFileName(path)}' is {length / (1024.0 * 1024.0):0.#} MB, "
            + $"larger than the {limit / (1024 * 1024)} MB this editor opens.")
    {
        Path = path;
        Length = length;
        Limit = limit;
    }

    /// <summary>The file that was refused.</summary>
    public string Path { get; }

    /// <summary>Its size in bytes.</summary>
    public long Length { get; }

    /// <summary>The limit in bytes.</summary>
    public long Limit { get; }
}

/// <summary>
/// Reading and atomically writing documents and settings. A write goes to a temporary file in the
/// destination's folder and then replaces the destination, so a crash mid-save can never leave a
/// half-written clip or mesh behind.
/// </summary>
public static class AtomicFile
{
    /// <summary>
    /// The largest document this editor opens. The biggest stock clip or mesh is well under a
    /// megabyte; this only keeps a mislabelled huge file from being pulled into memory.
    /// </summary>
    public const long MaxDocumentBytes = 64L * 1024 * 1024;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Reads a whole file, refusing one larger than <paramref name="maxBytes"/> before reading it.</summary>
    /// <exception cref="FileTooLargeException">The file is over the limit.</exception>
    public static byte[] ReadAllBytes(string path, long maxBytes = MaxDocumentBytes)
    {
        var info = new FileInfo(path);
        if (info.Exists && info.Length > maxBytes) throw new FileTooLargeException(path, info.Length, maxBytes);
        return File.ReadAllBytes(path);
    }

    /// <summary>Writes <paramref name="bytes"/> to <paramref name="path"/> atomically.</summary>
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> bytes)
    {
        byte[] copy = bytes.ToArray();
        Replace(path, temp => File.WriteAllBytes(temp, copy));
    }

    /// <summary>Writes <paramref name="text"/> to <paramref name="path"/> as UTF-8 without a BOM, atomically.</summary>
    public static void WriteAllText(string path, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Replace(path, temp => File.WriteAllText(temp, text, Utf8NoBom));
    }

    private static void Replace(string path, Action<string> writeTemp)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        // GetDirectoryName answers null for a path with no directory part at all (a device name such
        // as "CON:"); refuse that with an IOException every caller already handles.
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(directory))
            throw new IOException($"'{path}' is not a place a file can be written.");
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            writeTemp(temp);
            if (File.Exists(path))
            {
                // Replace keeps the destination's identity (ACLs, hard links) where it can.
                File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
