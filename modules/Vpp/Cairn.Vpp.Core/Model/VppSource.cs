namespace Cairn.Vpp.Model;

/// <summary>Where an entry's bytes come from. Sources are immutable descriptions; data is read on demand.</summary>
public abstract record VppSource
{
    internal VppSource() { }

    /// <summary>Length of the entry's data in bytes.</summary>
    public abstract long Size { get; }

    /// <summary>A short description for tooltips and reports ("archive", the file path, "memory").</summary>
    public abstract string Describe();

    /// <summary>Opens the data as a fresh read-only stream of exactly <see cref="Size"/> bytes (shorter if the source shrank).</summary>
    public abstract Stream Open();

    /// <summary>Reads the whole entry into memory. Meant for previews and facts of single entries, never for whole packfiles.</summary>
    public byte[] ReadAll()
    {
        if (Size > Array.MaxLength) throw new IOException($"{Describe()} is too large to read into memory ({Size:N0} bytes).");
        using var stream = Open();
        var bytes = new byte[Size];
        int read = 0;
        while (read < bytes.Length)
        {
            int n = stream.Read(bytes, read, bytes.Length - read);
            if (n <= 0) throw new IOException($"{Describe()} ended after {read:N0} of {Size:N0} bytes.");
            read += n;
        }
        return bytes;
    }
}

/// <summary>An entry stored in a packfile on disk (normally the one the package was opened from).</summary>
/// <param name="ArchivePath">Full path of the packfile.</param>
/// <param name="Offset">Byte offset of the data in the packfile.</param>
/// <param name="Length">Data length in bytes.</param>
public sealed record ArchiveSource(string ArchivePath, long Offset, int Length) : VppSource
{
    public override long Size => Length;

    public override string Describe() => $"'{Path.GetFileName(ArchivePath)}' at offset {Offset:N0}";

    public override Stream Open()
    {
        // FileShare.Delete lets a save replace the packfile while previews still read from it.
        var stream = new FileStream(ArchivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096);
        try
        {
            return new WindowStream(stream, Offset, Length, ownsInner: true);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }
}

/// <summary>A file on disk, recorded with the size and timestamp it had when it was added.</summary>
/// <param name="FilePath">Full path of the file.</param>
/// <param name="Length">Size in bytes when it was added.</param>
/// <param name="LastWriteUtc">Last write time when it was added.</param>
public sealed record FileSource(string FilePath, long Length, DateTime LastWriteUtc) : VppSource
{
    public override long Size => Length;

    public override string Describe() => FilePath;

    public override Stream Open() =>
        new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096);

    /// <summary>Captures a file's current size and timestamp.</summary>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public static FileSource FromFile(string path)
    {
        var info = new FileInfo(Path.GetFullPath(path));
        if (!info.Exists) throw new FileNotFoundException($"'{path}' does not exist.", path);
        return new FileSource(info.FullName, info.Length, info.LastWriteTimeUtc);
    }

    /// <summary>Null when the file is still as recorded; otherwise why not ("missing", "size changed", "modified").</summary>
    public string? CheckUnchanged()
    {
        var info = new FileInfo(FilePath);
        if (!info.Exists) return "the file no longer exists";
        if (info.Length != Length) return $"its size changed from {Length:N0} to {info.Length:N0} bytes";
        if (info.LastWriteTimeUtc != LastWriteUtc) return "it was modified after it was added";
        return null;
    }
}

/// <summary>Bytes held in memory (pasted, generated or recovered).</summary>
public sealed record MemorySource(byte[] Bytes) : VppSource
{
    public override long Size => Bytes.Length;

    public override string Describe() => "memory";

    public override Stream Open() => new MemoryStream(Bytes, writable: false);

    // Records compare arrays by reference; content equality is what an edit's "nothing changed" check wants.
    public bool Equals(MemorySource? other) =>
        other is not null && (ReferenceEquals(Bytes, other.Bytes) || Bytes.AsSpan().SequenceEqual(other.Bytes));

    public override int GetHashCode() => Bytes.Length;
}
