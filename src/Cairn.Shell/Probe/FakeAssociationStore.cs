using System.Windows;
using Cairn.Shell.Dialogs;
using Microsoft.Win32;

namespace Cairn.Shell;

/// <summary>
/// An in-memory <see cref="IAssociationStore"/> for self-tests and captures: keys are "HKCU\path" /
/// "HKLM\path" (case-insensitive) holding named values. <c>Seed*</c> sets up a scene without counting as a
/// write; every <see cref="IAssociationStore"/> write is counted and its path recorded.
/// </summary>
public sealed class FakeAssociationStore : IAssociationStore
{
    private readonly Dictionary<string, Dictionary<string, object>> _keys = new(StringComparer.OrdinalIgnoreCase);

    public string? ExecutablePath { get; set; } = @"C:\Programs\Cairn\Cairn.exe";
    /// <summary>Executable path -> the description <see cref="DescribeExecutable"/> returns.</summary>
    public Dictionary<string, string> Descriptions { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The HKCU paths written (set or deleted), in order.</summary>
    public List<string> WrittenPaths { get; } = [];
    public int Writes => WrittenPaths.Count;
    public int Notifications { get; private set; }
    /// <summary>The extensions <see cref="ShowDefaultChooser"/> was asked for.</summary>
    public List<string> ChooserCalls { get; } = [];

    private static string Full(RegistryHive hive, string path) => (hive == RegistryHive.LocalMachine ? @"HKLM\" : @"HKCU\") + path.Trim('\\');

    private Dictionary<string, object> Create(string full)
    {
        var parts = full.Split('\\');
        for (int i = 2; i <= parts.Length; i++)
        {
            var prefix = string.Join('\\', parts[..i]);
            if (!_keys.ContainsKey(prefix)) _keys[prefix] = new(StringComparer.OrdinalIgnoreCase);
        }
        return _keys[full];
    }

    /// <summary>Sets a string value without counting it as a write.</summary>
    public void Seed(RegistryHive hive, string path, string? name, string value) => Create(Full(hive, path))[name ?? string.Empty] = value;

    /// <summary>Creates a key without counting it as a write.</summary>
    public void SeedKey(RegistryHive hive, string path) => Create(Full(hive, path));

    /// <summary>Forgets the writes recorded so far (after a scene was set up through the real code).</summary>
    public void ResetCounters() { WrittenPaths.Clear(); Notifications = 0; ChooserCalls.Clear(); }

    public string? GetString(RegistryHive hive, string path, string? name = null) =>
        _keys.TryGetValue(Full(hive, path), out var values) && values.TryGetValue(name ?? string.Empty, out var value) ? value as string : null;

    public IReadOnlyList<string> GetValueNames(RegistryHive hive, string path) =>
        _keys.TryGetValue(Full(hive, path), out var values) ? [.. values.Keys] : [];

    public bool KeyExists(RegistryHive hive, string path)
    {
        var full = Full(hive, path);
        return _keys.ContainsKey(full);
    }

    public bool IsEmptyKey(string path)
    {
        var full = Full(RegistryHive.CurrentUser, path);
        return _keys.TryGetValue(full, out var values) && values.Count == 0
            && !_keys.Keys.Any(k => k.StartsWith(full + @"\", StringComparison.OrdinalIgnoreCase));
    }

    public void SetString(string path, string? name, string value)
    {
        WrittenPaths.Add(path);
        Create(Full(RegistryHive.CurrentUser, path))[name ?? string.Empty] = value;
    }

    public void SetEmptyValue(string path, string name)
    {
        WrittenPaths.Add(path);
        Create(Full(RegistryHive.CurrentUser, path))[name] = Array.Empty<byte>();
    }

    public void DeleteValue(string path, string? name)
    {
        WrittenPaths.Add(path);
        if (_keys.TryGetValue(Full(RegistryHive.CurrentUser, path), out var values)) values.Remove(name ?? string.Empty);
    }

    public void DeleteTree(string path)
    {
        WrittenPaths.Add(path);
        var full = Full(RegistryHive.CurrentUser, path);
        foreach (var key in _keys.Keys.Where(k => k.Equals(full, StringComparison.OrdinalIgnoreCase) || k.StartsWith(full + @"\", StringComparison.OrdinalIgnoreCase)).ToList())
            _keys.Remove(key);
    }

    public string? DescribeExecutable(string path) => Descriptions.TryGetValue(path, out var name) ? name : null;

    public string ResolveIndirect(string text) => text;

    public void NotifyChanged() => Notifications++;

    public bool ShowDefaultChooser(Window? owner, string extension)
    {
        ChooserCalls.Add(extension);
        return true;
    }
}
