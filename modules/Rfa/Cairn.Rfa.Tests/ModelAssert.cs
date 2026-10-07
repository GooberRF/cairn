using System.Collections;
using System.Numerics;
using System.Reflection;
using Cairn.Formats;

namespace Cairn.Rfa.Tests;

/// <summary>
/// Structural equality for the immutable format models. Records holding ImmutableArrays compare by
/// reference, so a "read, write, read gives an equal model" test needs a deep walk: this compares
/// public properties recursively, sequences element by element and floats by their bits.
/// </summary>
internal static class ModelAssert
{
    public static void Equal(object? expected, object? actual)
    {
        string? diff = Diff(expected, actual, "model");
        if (diff is not null) Assert.Fail(diff);
    }

    public static string? Diff(object? a, object? b, string path)
    {
        if (a is null || b is null) return a is null && b is null ? null : $"{path}: one side is null";
        var type = a.GetType();
        if (type != b.GetType()) return $"{path}: type {type.Name} vs {b.GetType().Name}";

        switch (a)
        {
            case float fa:
                return BitConverter.SingleToInt32Bits(fa) == BitConverter.SingleToInt32Bits((float)b)
                    ? null : $"{path}: {fa} vs {b}";
            case Vector2 va:
                return Diff(va.X, ((Vector2)b).X, path + ".X") ?? Diff(va.Y, ((Vector2)b).Y, path + ".Y");
            case Vector3 v3:
            {
                var o = (Vector3)b;
                return Diff(v3.X, o.X, path + ".X") ?? Diff(v3.Y, o.Y, path + ".Y") ?? Diff(v3.Z, o.Z, path + ".Z");
            }
            case Quaternion q:
            {
                var o = (Quaternion)b;
                return Diff(q.X, o.X, path + ".X") ?? Diff(q.Y, o.Y, path + ".Y")
                    ?? Diff(q.Z, o.Z, path + ".Z") ?? Diff(q.W, o.W, path + ".W");
            }
            case FixedString fs:
                return fs.Equals((FixedString)b) ? null : $"{path}: '{fs}' vs '{b}'";
            case string or decimal or Enum:
                return Equals(a, b) ? null : $"{path}: {a} vs {b}";
        }
        if (type.IsPrimitive) return Equals(a, b) ? null : $"{path}: {a} vs {b}";

        if (a is IEnumerable ea)
        {
            var la = ea.Cast<object?>().ToList();
            var lb = ((IEnumerable)b).Cast<object?>().ToList();
            if (la.Count != lb.Count) return $"{path}: {la.Count} items vs {lb.Count}";
            for (int i = 0; i < la.Count; i++)
            {
                if (Diff(la[i], lb[i], $"{path}[{i}]") is { } d) return d;
            }
            return null;
        }

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length > 0 || !prop.CanRead) continue;
            if (Diff(prop.GetValue(a), prop.GetValue(b), $"{path}.{prop.Name}") is { } d) return d;
        }
        return null;
    }
}
