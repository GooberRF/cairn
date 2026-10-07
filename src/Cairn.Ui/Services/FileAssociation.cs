using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Cairn.Ui.Services;

/// <summary>
/// Registers the extensions the caller lists (<see cref="Kind"/>) with this copy of Cairn, under
/// <c>HKCU\Software\Classes</c> only.
///
/// Per-user on purpose: it needs no administrator rights, it cannot break another user's
/// associations, and removing it puts the machine back exactly as it was (the previous handler is
/// parked on our ProgID and restored). Windows is told with <c>SHChangeNotify</c>.
/// </summary>
public static class FileAssociation
{
    /// <summary>One extension to register: <paramref name="Extension"/> (".rfa") opens through <paramref name="ProgId"/>.</summary>
    /// <param name="Extension">Lower case, with the dot.</param>
    /// <param name="ProgId">The ProgID written under <c>HKCU\Software\Classes</c> (<c>Cairn.&lt;kindId&gt;</c>).</param>
    /// <param name="FriendlyName">The type name Explorer shows ("Red Faction animation clip").</param>
    public sealed record Kind(string Extension, string ProgId, string FriendlyName)
    {
        /// <summary>A registration for a document kind: ProgID <c>Cairn.&lt;kindId&gt;</c>.</summary>
        public static Kind For(string kindId, string extension, string friendlyName) =>
            new(extension.ToLowerInvariant(), "Cairn." + kindId, friendlyName);
    }

    private const string ClassesPath = @"Software\Classes";
    private const string PreviousProgIdValue = "PreviousProgId";
    private const int ShcneAssocchanged = 0x08000000;
    private const int ShcnfIdlist = 0x0000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);

    /// <summary>The extensions this app registers, for menus and messages.</summary>
    public static string ExtensionList(IReadOnlyList<Kind> kinds) => string.Join(", ", kinds.Select(k => k.Extension));

    /// <summary>The full path of the running executable, which is what gets registered.</summary>
    public static string? ExecutablePath
    {
        get
        {
            try
            {
                string? path = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
                return Process.GetCurrentProcess().MainModule?.FileName;
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>True when every registered extension currently opens with this executable.</summary>
    public static bool IsAssociated(IReadOnlyList<Kind> kinds)
    {
        try
        {
            string? exe = ExecutablePath;
            if (exe is null) return false;
            using var classes = Registry.CurrentUser.OpenSubKey(ClassesPath);
            foreach (var kind in kinds)
            {
                using var extension = classes?.OpenSubKey(kind.Extension);
                if (extension?.GetValue(null) as string != kind.ProgId) return false;
                using var command = classes?.OpenSubKey($@"{kind.ProgId}\shell\open\command");
                if (command?.GetValue(null) is not string line || !line.Contains(exe, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>Points the extensions at this executable. Returns null on success, or a sentence explaining what went wrong.</summary>
    public static string? Associate(IReadOnlyList<Kind> kinds)
    {
        string? exe = ExecutablePath;
        if (exe is null) return "Cairn could not work out where it is running from.";
        try
        {
            using var classes = Registry.CurrentUser.CreateSubKey(ClassesPath, writable: true);
            if (classes is null) return "The per-user registry could not be opened for writing.";
            foreach (var kind in kinds)
            {
                string? previous;
                using (var existing = classes.OpenSubKey(kind.Extension))
                {
                    previous = existing?.GetValue(null) as string;
                }
                if (string.Equals(previous, kind.ProgId, StringComparison.OrdinalIgnoreCase)) previous = null;

                using (var progId = classes.CreateSubKey(kind.ProgId, writable: true))
                {
                    progId.SetValue(null, kind.FriendlyName);
                    progId.SetValue("FriendlyTypeName", kind.FriendlyName);
                    if (previous is { Length: > 0 }) progId.SetValue(PreviousProgIdValue, previous);
                    using (var icon = progId.CreateSubKey("DefaultIcon", writable: true))
                    {
                        icon.SetValue(null, $"\"{exe}\",0");
                    }
                    using var command = progId.CreateSubKey(@"shell\open\command", writable: true);
                    command.SetValue(null, $"\"{exe}\" \"%1\"");
                }

                using var extension = classes.CreateSubKey(kind.Extension, writable: true);
                extension.SetValue(null, kind.ProgId);
                using var openWith = extension.CreateSubKey("OpenWithProgids", writable: true);
                openWith.SetValue(kind.ProgId, Array.Empty<byte>(), RegistryValueKind.None);
            }
            Notify();
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Undoes <see cref="Associate"/>. Leaves an extension alone if it now points somewhere else,
    /// because taking away another program's association would be worse than doing nothing.
    /// </summary>
    public static string? Remove(IReadOnlyList<Kind> kinds)
    {
        try
        {
            using var classes = Registry.CurrentUser.CreateSubKey(ClassesPath, writable: true);
            if (classes is null) return null;
            foreach (var kind in kinds)
            {
                string? previous;
                using (var progId = classes.OpenSubKey(kind.ProgId))
                {
                    previous = progId?.GetValue(PreviousProgIdValue) as string;
                }
                using (var extension = classes.OpenSubKey(kind.Extension, writable: true))
                {
                    if (extension is not null)
                    {
                        if (extension.GetValue(null) as string == kind.ProgId) extension.SetValue(null, previous ?? string.Empty);
                        using var openWith = extension.OpenSubKey("OpenWithProgids", writable: true);
                        if (openWith is not null)
                        {
                            openWith.DeleteValue(kind.ProgId, throwOnMissingValue: false);
                            if (openWith.ValueCount == 0 && openWith.SubKeyCount == 0)
                                extension.DeleteSubKeyTree("OpenWithProgids", throwOnMissingSubKey: false);
                        }
                    }
                }
                classes.DeleteSubKeyTree(kind.ProgId, throwOnMissingSubKey: false);
            }
            Notify();
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return ex.Message;
        }
    }

    private static void Notify()
    {
        try { SHChangeNotify(ShcneAssocchanged, ShcnfIdlist, IntPtr.Zero, IntPtr.Zero); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }
}
