using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;

namespace Cairn.Lvl.Model;

/// <summary>
/// Structural comparison of two model values (levels, group files or any part of them). Records compare their
/// lists by reference, so "read, write, read again gives the same model" needs this: it walks every public property,
/// compares lists element by element, and reports where the two differ.
/// </summary>
public static class RfModelDiff
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Properties = new();

    /// <summary>Paths and values where <paramref name="a"/> and <paramref name="b"/> differ; empty when they are equal.</summary>
    public static IReadOnlyList<string> Compare(object? a, object? b, int maxDifferences = 20, params string[] ignoreProperties)
    {
        var diffs = new List<string>();
        var ignore = new HashSet<string>(ignoreProperties, StringComparer.Ordinal);
        Walk(a, b, "", diffs, Math.Max(1, maxDifferences), ignore);
        return diffs;
    }

    public static bool AreEqual(object? a, object? b, params string[] ignoreProperties) =>
        Compare(a, b, 1, ignoreProperties).Count == 0;

    private static void Walk(object? a, object? b, string path, List<string> diffs, int max, HashSet<string> ignore)
    {
        if (diffs.Count >= max) return;
        if (a is null || b is null)
        {
            if (a is not null || b is not null) diffs.Add($"{Root(path)}: {Show(a)} vs {Show(b)}");
            return;
        }
        var type = a.GetType();
        if (type != b.GetType())
        {
            diffs.Add($"{Root(path)}: {type.Name} vs {b.GetType().Name}");
            return;
        }
        if (a is ReadOnlyMemory<byte> ma)
        {
            if (!ma.Span.SequenceEqual(((ReadOnlyMemory<byte>)b).Span)) diffs.Add($"{Root(path)}: bytes differ");
            return;
        }
        if (IsLeaf(type))
        {
            if (!a.Equals(b)) diffs.Add($"{Root(path)}: {Show(a)} vs {Show(b)}");
            return;
        }
        if (a is IList la && b is IList lb)
        {
            if (la.Count != lb.Count)
            {
                diffs.Add($"{Root(path)}: {la.Count} items vs {lb.Count}");
                return;
            }
            for (int i = 0; i < la.Count && diffs.Count < max; i++) Walk(la[i], lb[i], $"{path}[{i}]", diffs, max, ignore);
            return;
        }
        foreach (var p in Properties.GetOrAdd(type, Readable))
        {
            if (ignore.Contains(p.Name)) continue;
            Walk(p.GetValue(a), p.GetValue(b), path.Length == 0 ? p.Name : $"{path}.{p.Name}", diffs, max, ignore);
            if (diffs.Count >= max) return;
        }
    }

    /// <summary>Values compared with Equals: primitives, strings, enums, and structs that hold no lists.</summary>
    private static bool IsLeaf(Type t) =>
        t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal)
        || (t.IsValueType && !IsImmutableArray(t) && !HasListMember(t));

    private static bool IsImmutableArray(Type t) => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ImmutableArray<>);

    private static bool HasListMember(Type t) =>
        t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Any(f => IsImmutableArray(f.FieldType));

    /// <summary>Public instance properties without indexers, leaving out computed sequences (they repeat stored lists).</summary>
    private static PropertyInfo[] Readable(Type t) =>
        [.. t.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.GetIndexParameters().Length == 0 && p.CanRead)
            .Where(p => IsImmutableArray(p.PropertyType) || p.PropertyType == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(p.PropertyType))];

    private static string Root(string path) => path.Length == 0 ? "(value)" : path;

    private static string Show(object? v) => v switch
    {
        null => "null",
        string s => $"\"{s}\"",
        _ => v.ToString() ?? "?",
    };
}
