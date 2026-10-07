using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using Cairn.Ui.Services;
using Microsoft.Win32;

namespace Cairn.Shell.Dialogs;

/// <summary>
/// Everything the file-association code reads from and writes to Windows: registry values (reads from
/// <see cref="RegistryHive.CurrentUser"/> or <see cref="RegistryHive.LocalMachine"/>, writes to the current
/// user only), plus the few shell calls around them. Self-tests substitute an in-memory fake so they never
/// touch the real registry. Paths are relative to the hive root (<c>Software\Classes\.vfx</c>); a null or
/// empty value name means the key's default value.
/// </summary>
public interface IAssociationStore
{
    /// <summary>The executable registrations point at, or null when it cannot be worked out.</summary>
    string? ExecutablePath { get; }
    /// <summary>A string value, or null when the key or value is missing or not a string.</summary>
    string? GetString(RegistryHive hive, string path, string? name = null);
    /// <summary>The value names of a key (empty when the key is missing).</summary>
    IReadOnlyList<string> GetValueNames(RegistryHive hive, string path);
    /// <summary>True when the key exists.</summary>
    bool KeyExists(RegistryHive hive, string path);
    /// <summary>True when the current user's key exists and holds no values and no subkeys.</summary>
    bool IsEmptyKey(string path);
    /// <summary>Writes a string value under the current user, creating the key.</summary>
    void SetString(string path, string? name, string value);
    /// <summary>Writes an empty <c>REG_NONE</c> value (an <c>OpenWithProgids</c> entry) under the current user.</summary>
    void SetEmptyValue(string path, string name);
    /// <summary>Deletes a current-user value; missing is fine.</summary>
    void DeleteValue(string path, string? name);
    /// <summary>Deletes a current-user key and everything under it; missing is fine.</summary>
    void DeleteTree(string path);
    /// <summary>A program's friendly name from its file (version resource description), or null.</summary>
    string? DescribeExecutable(string path);
    /// <summary>Resolves an indirect string ("@{Package?ms-resource://...}", "@file.dll,-123"); returns it unchanged otherwise.</summary>
    string ResolveIndirect(string text);
    /// <summary>Tells Explorer that associations changed (<c>SHChangeNotify(SHCNE_ASSOCCHANGED)</c>).</summary>
    void NotifyChanged();
    /// <summary>Opens Windows' own chooser for <paramref name="extension"/> (modal over <paramref name="owner"/>). False when it could not be shown.</summary>
    bool ShowDefaultChooser(Window? owner, string extension);
}

/// <summary>The real <see cref="IAssociationStore"/>: <c>HKCU</c>/<c>HKLM</c> through <see cref="Registry"/> and shell32.</summary>
public sealed class WindowsAssociationStore : IAssociationStore
{
    public string? ExecutablePath => FileAssociation.ExecutablePath;

    private static RegistryKey Root(RegistryHive hive) => hive == RegistryHive.LocalMachine ? Registry.LocalMachine : Registry.CurrentUser;

    private static T Read<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return fallback; }
    }

    public string? GetString(RegistryHive hive, string path, string? name = null) => Read(() =>
    {
        using var key = Root(hive).OpenSubKey(path);
        return key?.GetValue(name ?? string.Empty) as string;
    }, null);

    public IReadOnlyList<string> GetValueNames(RegistryHive hive, string path) => Read<IReadOnlyList<string>>(() =>
    {
        using var key = Root(hive).OpenSubKey(path);
        return key?.GetValueNames() ?? [];
    }, []);

    public bool KeyExists(RegistryHive hive, string path) => Read(() =>
    {
        using var key = Root(hive).OpenSubKey(path);
        return key is not null;
    }, false);

    public bool IsEmptyKey(string path)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path);
        return key is { ValueCount: 0, SubKeyCount: 0 };
    }

    public void SetString(string path, string? name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(path, writable: true);
        key.SetValue(name ?? string.Empty, value);
    }

    public void SetEmptyValue(string path, string name)
    {
        using var key = Registry.CurrentUser.CreateSubKey(path, writable: true);
        key.SetValue(name, Array.Empty<byte>(), RegistryValueKind.None);
    }

    public void DeleteValue(string path, string? name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path, writable: true);
        key?.DeleteValue(name ?? string.Empty, throwOnMissingValue: false);
    }

    public void DeleteTree(string path) => Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);

    public string? DescribeExecutable(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var info = FileVersionInfo.GetVersionInfo(path);
            return info.FileDescription is { Length: > 0 } description ? description.Trim()
                : info.ProductName is { Length: > 0 } product ? product.Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string source, StringBuilder output, int capacity, IntPtr reserved);

    public string ResolveIndirect(string text)
    {
        if (!text.StartsWith('@')) return text;
        try
        {
            var buffer = new StringBuilder(1024);
            return SHLoadIndirectString(text, buffer, buffer.Capacity, IntPtr.Zero) == 0 && buffer.Length > 0 ? buffer.ToString() : text;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return text; }
    }

    private const int ShcneAssocchanged = 0x08000000;
    private const int ShcnfIdlist = 0x0000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);

    public void NotifyChanged()
    {
        try { SHChangeNotify(ShcneAssocchanged, ShcnfIdlist, IntPtr.Zero, IntPtr.Zero); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenAsInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string File;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Class;
        public int Flags;
    }

    // OAIF_ALLOW_REGISTRATION | OAIF_REGISTER_EXT | OAIF_FORCE_REGISTRATION. OAIF_EXEC is left out on
    // purpose: there is no real file to open afterwards, only the extension whose default is being chosen.
    private const int OpenAsFlags = 0x0001 | 0x0002 | 0x0008;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHOpenWithDialog(IntPtr parent, ref OpenAsInfo info);

    public bool ShowDefaultChooser(Window? owner, string extension)
    {
        var hwnd = owner is null ? IntPtr.Zero : new WindowInteropHelper(owner).Handle;
        try
        {
            var info = new OpenAsInfo { File = "Example" + extension, Class = null, Flags = OpenAsFlags };
            int hr = SHOpenWithDialog(hwnd, ref info);
            // S_OK, or the user closed the chooser (HRESULT_FROM_WIN32(ERROR_CANCELLED)).
            if (hr == 0 || hr == unchecked((int)0x800704C7)) return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        // Fallback: the Default apps page of Windows Settings ("Choose default apps by file type").
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }
}
