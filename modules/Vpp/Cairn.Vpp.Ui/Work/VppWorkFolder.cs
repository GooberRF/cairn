using System.Runtime.InteropServices;
using System.Windows.Threading;
using Cairn.Ui.Services;
using Cairn.Vpp.Model;

namespace Cairn.Vpp.Ui.Work;

/// <summary>A work copy that changed on disk since it was extracted (or last taken into the packfile).</summary>
public sealed record VppWorkChange(string EntryName, string FilePath);

/// <summary>
/// One packfile's folder of work copies (entries opened in another program or in a Cairn tab), watched so the
/// document can offer to take edited copies back. Deleted when the packfile closes.
/// </summary>
public sealed class VppWorkFolder : IDisposable
{
    private sealed class Copy(string entryName, string path, VppSource source)
    {
        public string EntryName { get; set; } = entryName;
        public string Path { get; } = path;
        public (long Length, DateTime Write) Baseline { get; set; }
        /// <summary>
        /// The entry's identity: every data source this copy's entry has had (extracted from, then each update taken in).
        /// Rename, undo and redo keep an entry's source, so the copy finds its entry again in any snapshot.
        /// </summary>
        public List<VppSource> Sources { get; } = [source];
    }

    private readonly Dictionary<string, Copy> _copies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dispatcher _dispatcher;
    private FileChangeWatcher? _watcher;
    private bool _disposed;

    public VppWorkFolder(string root, Dispatcher dispatcher)
    {
        Root = root;
        _dispatcher = dispatcher;
    }

    /// <summary>The folder (created on first use).</summary>
    public string Root { get; }
    /// <summary>The copies that changed and were neither taken in nor ignored.</summary>
    public IReadOnlyList<VppWorkChange> Changes { get; private set; } = [];
    /// <summary>Raised on the UI thread when <see cref="Changes"/> changes.</summary>
    public event EventHandler? ChangesChanged;

    /// <summary>The work copy of <paramref name="entryName"/>, or null when none was made.</summary>
    public string? PathOf(string entryName) => _copies.Values.FirstOrDefault(c => string.Equals(c.EntryName, entryName, StringComparison.OrdinalIgnoreCase))?.Path;

    /// <summary>The entry (its current name) whose work copy is <paramref name="path"/>, or null when it is not one of this folder's copies.</summary>
    public string? EntryOf(string path)
    {
        string full;
        try { full = System.IO.Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        return _copies.Values.FirstOrDefault(c => string.Equals(System.IO.Path.GetFullPath(c.Path), full, StringComparison.OrdinalIgnoreCase))?.EntryName;
    }

    /// <summary>
    /// Extracts <paramref name="item"/> (off the UI thread) unless a copy already exists: an existing copy may hold
    /// the user's unsaved-into-the-packfile edits, so it is reused rather than overwritten.
    /// </summary>
    public async Task<string> PrepareAsync(VppItem item)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var tracked = _copies.Values.FirstOrDefault(c => c.Sources.Contains(item.Source))
            ?? _copies.Values.FirstOrDefault(c => string.Equals(c.EntryName, item.Name, StringComparison.OrdinalIgnoreCase));
        if (tracked is not null && File.Exists(tracked.Path)) return tracked.Path;
        VppWorkRoot.CreateFolder(System.IO.Path.GetDirectoryName(Root)!, System.IO.Path.GetFileName(Root));
        // never extract over a path another copy uses (it may hold that entry's edits): "name (2).ext" instead
        string safe = SafeFileName(item.Name), path = System.IO.Path.Combine(Root, safe);
        for (int n = 2; _copies.ContainsKey(path) && _copies[path] != tracked; n++)
            path = System.IO.Path.Combine(Root, $"{System.IO.Path.GetFileNameWithoutExtension(safe)} ({n}){System.IO.Path.GetExtension(safe)}");
        var source = item.Source;
        await Task.Run(() =>
        {
            try
            {
                using var input = source.Open();
                using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                CopyExactly(input, output, item.Size, item.Name);
            }
            catch (IOException)
            {
                try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                throw;
            }
        });
        var copy = new Copy(item.Name, path, source) { Baseline = Stamp(path) };
        _copies[path] = copy;
        EnsureWatching();
        return path;
    }

    /// <summary>
    /// Maps every copy to its entry in <paramref name="package"/> (called on every snapshot change, so also after undo
    /// and redo): by the entry's data source first, else by the name it had (a save gives every entry a new source).
    /// </summary>
    public void Follow(VppPackage package)
    {
        if (_copies.Count == 0) return;
        var bySource = new Dictionary<VppSource, VppItem>();
        foreach (var item in package.Items) bySource.TryAdd(item.Source, item);
        bool renamed = false;
        foreach (var c in _copies.Values)
        {
            VppItem? found = null;
            for (int i = c.Sources.Count - 1; i >= 0 && found is null; i--) bySource.TryGetValue(c.Sources[i], out found);
            if (found is null && package.Find(c.EntryName) is { } byName)
            {
                found = byName;
                c.Sources.Add(byName.Source);
            }
            if (found is null || string.Equals(found.Name, c.EntryName, StringComparison.Ordinal)) continue;
            c.EntryName = found.Name;
            renamed = true;
        }
        if (renamed) { Changes = []; Rescan(); }
    }

    /// <summary>Records that the copy at <paramref name="copyPath"/> was taken into the packfile as <paramref name="source"/>.</summary>
    public void NoteTaken(string copyPath, VppSource source)
    {
        if (_copies.TryGetValue(copyPath, out var c) && !c.Sources.Contains(source)) c.Sources.Add(source);
    }

    /// <summary>Follows a rename inside the packfile so a later update goes to the right entry.</summary>
    public void NoteRenamed(string oldName, string newName)
    {
        foreach (var c in _copies.Values.Where(c => string.Equals(c.EntryName, oldName, StringComparison.OrdinalIgnoreCase))) c.EntryName = newName;
    }

    /// <summary>
    /// Freezes the changed copies (each one copied into its own snapshot file, so editing the work copy again does not
    /// alter what was taken in) and returns (entry name, snapshot path) pairs; the bar clears.
    /// </summary>
    public IReadOnlyList<(string EntryName, string SnapshotPath, string CopyPath)> TakeChanges(IReadOnlyList<VppWorkChange>? only = null)
    {
        var result = new List<(string, string, string)>();
        foreach (var change in only ?? Changes)
        {
            if (!_copies.TryGetValue(change.FilePath, out var copy) || !File.Exists(copy.Path)) continue;
            string folder = System.IO.Path.Combine(Root, ".taken", Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(folder);
            string snapshot = System.IO.Path.Combine(folder, System.IO.Path.GetFileName(copy.Path));
            File.Copy(copy.Path, snapshot, overwrite: true);
            copy.Baseline = Stamp(copy.Path);
            result.Add((copy.EntryName, snapshot, copy.Path));
        }
        Rescan();
        return result;
    }

    /// <summary>Accepts the current state of every changed copy as the new baseline (the bar clears).</summary>
    public void IgnoreChanges()
    {
        foreach (var change in Changes) if (_copies.TryGetValue(change.FilePath, out var c)) c.Baseline = Stamp(c.Path);
        Rescan();
    }

    /// <summary>Re-checks every copy against its baseline (also run by the watcher).</summary>
    public void Rescan()
    {
        if (_disposed) return;
        var changed = _copies.Values
            .Where(c => File.Exists(c.Path) && Stamp(c.Path) != c.Baseline)
            .Select(c => new VppWorkChange(c.EntryName, c.Path))
            .OrderBy(c => c.EntryName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (changed.SequenceEqual(Changes)) return;
        Changes = changed;
        ChangesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EnsureWatching()
    {
        if (_watcher is not null) return;
        _watcher = new FileChangeWatcher(_dispatcher, 400);
        _watcher.Changed += (_, _) => Rescan();
        _watcher.Watch(Root);
    }

    private static (long, DateTime) Stamp(string path)
    {
        try { var info = new FileInfo(path); return info.Exists ? (info.Length, info.LastWriteTimeUtc) : (-1, DateTime.MinValue); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (-1, DateTime.MinValue); }
    }

    /// <summary>An entry name made safe for a Windows file name (packfile names may hold characters Windows refuses).</summary>
    public static string SafeFileName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        string safe = new string(chars).TrimEnd('.', ' ');
        if (safe.Length == 0) safe = "entry";
        if (IsDeviceName(safe)) safe = "_" + safe;
        return safe;
    }

    /// <summary>
    /// True when Windows treats <paramref name="fileName"/> as a DOS device: the part before the FIRST dot, without
    /// trailing spaces, is CON, PRN, AUX, NUL, COM0-9 / LPT0-9 (superscript digits too), CONIN$ or CONOUT$
    /// ("con.foo.tga" and "nul .tga" are devices).
    /// </summary>
    public static bool IsDeviceName(string fileName)
    {
        int dot = fileName.IndexOf('.');
        string stem = (dot < 0 ? fileName : fileName[..dot]).TrimEnd(' ').ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$") return true;
        return stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
            && (char.IsAsciiDigit(stem[3]) || stem[3] is '¹' or '²' or '³');
    }

    /// <summary>Copies exactly <paramref name="size"/> bytes; throws when the source ends early (a truncated packfile) or holds more.</summary>
    public static void CopyExactly(Stream input, Stream output, long size, string entryName, CancellationToken token = default)
    {
        var buffer = new byte[(int)Math.Clamp(size, 1, 1 << 20)];
        long remaining = size;
        while (remaining > 0)
        {
            token.ThrowIfCancellationRequested();
            int n = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (n <= 0) throw new IOException($"'{entryName}' ends after {size - remaining:N0} of {size:N0} bytes (the packfile is truncated or changed); nothing was written for it.");
            output.Write(buffer, 0, n);
            remaining -= n;
        }
    }

    /// <summary>Stops watching and deletes the folder. False when something in it is still locked (retry later).</summary>
    public bool TryDelete()
    {
        _disposed = true;
        _watcher?.Dispose();
        _watcher = null;
        return !Directory.Exists(Root) || VppWorkRoot.TryDeleteOwnFolder(Root); // only a folder Cairn created and marked
    }

    public void Dispose() => TryDelete();

    /// <summary>Deletes <paramref name="folder"/> recursively; false (and nothing thrown) when it could not be.</summary>
    public static bool TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}

/// <summary>Opens files the way Explorer does: the associated program, or Windows' "Open with" chooser.</summary>
public static class VppShellLaunch
{
    private const int ErrorNoAssociation = 1155;

    /// <summary>Opens <paramref name="path"/> in its associated program; with none, shows Windows' chooser. Returns an error message or null.</summary>
    public static string? Open(string path, IntPtr owner)
    {
        try
        {
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            return null;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == ErrorNoAssociation)
        {
            return OpenWith(path, owner);
        }
        catch (System.ComponentModel.Win32Exception ex) { return ex.Message; }
    }

    /// <summary>Shows Windows' "Open with" chooser for <paramref name="path"/>. Returns an error message or null.</summary>
    public static string? OpenWith(string path, IntPtr owner)
    {
        var info = new OpenAsInfo { File = path, Class = null, Flags = OaifAllowRegistration | OaifRegisterExt | OaifExec };
        int hr = SHOpenWithDialog(owner, ref info);
        // Cancelling the chooser returns ERROR_CANCELLED as an HRESULT: not an error for the user.
        return hr >= 0 || hr == unchecked((int)0x800704C7) ? null : Marshal.GetExceptionForHR(hr)?.Message ?? "Windows could not show the program chooser.";
    }

    private const int OaifAllowRegistration = 0x1, OaifRegisterExt = 0x2, OaifExec = 0x4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenAsInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string File;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Class;
        public int Flags;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHOpenWithDialog(IntPtr hwndParent, ref OpenAsInfo info);
}
