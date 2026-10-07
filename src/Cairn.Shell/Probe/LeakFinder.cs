using System.Reflection;
using System.Windows;
using System.Windows.Threading;

namespace Cairn.Shell;

/// <summary>
/// Diagnostic runs only: finds why an object that should be garbage is still alive, by a breadth-first walk over instance
/// fields from every readable static field of the loaded assemblies, the application, its windows and the UI dispatcher.
/// Weak references are not followed (their handles are not fields), so a reported path is a strong one.
/// </summary>
internal static class LeakFinder
{
    private static readonly Dictionary<Type, FieldInfo[]> FieldCache = [];

    /// <summary>The shortest field path from a root to <paramref name="target"/>, one hop per line, or null if none was found.</summary>
    public static string? FindPath(object target, int maxObjects = 4_000_000)
    {
        var parent = new Dictionary<object, (object? From, string Edge)>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<object>();
        void Seed(string name, object? o)
        {
            if (o is null || Skip(o.GetType()) || parent.ContainsKey(o)) return;
            parent[o] = (null, name);
            queue.Enqueue(o);
        }
        if (Application.Current is { } app)
        {
            Seed("Application.Current", app);
            foreach (Window w in app.Windows) Seed($"Window '{w.Title}'", w);
        }
        Seed("UI Dispatcher", Dispatcher.CurrentDispatcher);
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = [.. ex.Types.OfType<Type>()]; }
            foreach (var t in types)
            {
                if (t.ContainsGenericParameters) continue;
                foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (f.IsLiteral || f.FieldType.IsPointer || f.FieldType.IsPrimitive || f.FieldType == typeof(string)) continue;
                    // a [ThreadStatic] field reads the UI thread's value here (DataBindEngine, focus and input state live there)
                    object? v;
                    try { v = f.GetValue(null); } catch { continue; }
                    Seed($"static {t.FullName}.{f.Name}", v);
                }
            }
        }
        var found = false;
        while (queue.Count > 0 && parent.Count < maxObjects)
        {
            var o = queue.Dequeue();
            if (ReferenceEquals(o, target)) { found = true; break; }
            foreach (var (edge, child) in Children(o, 0))
            {
                if (parent.ContainsKey(child)) continue;
                parent[child] = (o, edge);
                queue.Enqueue(child);
            }
        }
        if (!found) return null;
        var hops = new List<string>();
        for (object? o = target; o is not null; o = parent[o].From)
        {
            var (from, edge) = parent[o];
            hops.Add(from is null ? $"{edge} : {o.GetType().FullName}" : $"  .{edge} -> {o.GetType().FullName}");
        }
        hops.Reverse();
        return string.Join(Environment.NewLine, hops);
    }

    private static bool Skip(Type t) => t.IsPrimitive || t.IsPointer || t == typeof(string) || t.IsEnum || typeof(MemberInfo).IsAssignableFrom(t)
        || typeof(Assembly).IsAssignableFrom(t) || typeof(Module).IsAssignableFrom(t);

    private static IEnumerable<(string Edge, object Child)> Children(object o, int depth)
    {
        var t = o.GetType();
        if (Skip(t)) yield break;
        if (o is Array a)
        {
            if (t.GetElementType() is { } et && (et.IsPrimitive || et.IsPointer || et == typeof(string) || et.IsEnum)) yield break;
            var i = 0;
            foreach (var item in a)
            {
                if (item is not null)
                {
                    if (item.GetType().IsValueType) foreach (var c in Children(item, depth + 1)) yield return ($"[{i}].{c.Edge}", c.Child);
                    else yield return ($"[{i}]", item);
                }
                i++;
            }
            yield break;
        }
        foreach (var f in Fields(t))
        {
            object? v;
            try { v = f.GetValue(o); } catch { continue; }
            if (v is null || Skip(v.GetType())) continue;
            if (v.GetType().IsValueType)
            {
                if (depth < 4) foreach (var c in Children(v, depth + 1)) yield return ($"{f.Name}.{c.Edge}", c.Child);
            }
            else yield return (f.Name, v);
        }
    }

    private static FieldInfo[] Fields(Type t)
    {
        if (FieldCache.TryGetValue(t, out var cached)) return cached;
        var list = new List<FieldInfo>();
        for (var x = t; x is not null; x = x.BaseType)
            list.AddRange(x.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(f => !f.FieldType.IsPointer && !f.FieldType.IsPrimitive && f.FieldType != typeof(string) && !f.FieldType.IsEnum
                    && f.FieldType != typeof(IntPtr) && !f.FieldType.IsByRefLike));
        // a WeakReference's handle is an IntPtr, so it is skipped above; ConditionalWeakTable entries hold through DependentHandle (also an IntPtr)
        return FieldCache[t] = [.. list.Where(f => f.FieldType != typeof(RuntimeFieldHandle))];
    }
}
