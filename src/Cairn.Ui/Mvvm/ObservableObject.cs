using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Cairn.Ui.Mvvm;

/// <summary>
/// The whole MVVM base class: change notification and a <see cref="Set{T}"/> helper. The design
/// calls for no MVVM framework, and nothing in this app needs more than this. Event args are cached
/// per property name, so a property raised every frame during playback does not allocate.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    private static readonly ConcurrentDictionary<string, PropertyChangedEventArgs> Args = new(StringComparer.Ordinal);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raises <see cref="PropertyChanged"/> for <paramref name="name"/>.</summary>
    protected void Raise([CallerMemberName] string? name = null)
    {
        var handler = PropertyChanged;
        if (handler is null) return;
        handler(this, name is null ? new PropertyChangedEventArgs(null) : Args.GetOrAdd(name, n => new PropertyChangedEventArgs(n)));
    }

    /// <summary>Raises <see cref="PropertyChanged"/> for several properties at once.</summary>
    protected void RaiseAll(params string[] names)
    {
        foreach (string name in names) Raise(name);
    }

    /// <summary>
    /// Assigns <paramref name="field"/> and raises a change notification when the value actually
    /// changed. Returns true when it changed, so callers can chain extra work.
    /// </summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}
