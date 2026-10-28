using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cairn.Formats.Rfl;
using Cairn.Lvl.Model;

namespace Cairn.Lvl.Conversion;

/// <summary>
/// Writes level model records (objects, rooms, moving data, level properties) as JSON for glTF extras, and reads them
/// back, so a level that goes to glTF and comes back keeps every field. Member names are the property names in
/// snake_case; vectors, quaternions and colours are number arrays, an <see cref="RfMatrix"/> is its nine numbers row
/// by row (right, up, forward), byte lists are base64 text, numbers that are not finite are the strings "NaN",
/// "Infinity" and "-Infinity", and a null value is a JSON null. Computed properties are not written.
/// </summary>
public static class LvlExtras
{
    private static readonly ConcurrentDictionary<Type, Member[]> Members = new();

    private sealed record Member(PropertyInfo Property, string JsonName);

    /// <summary>Every field of a record as a JSON object.</summary>
    /// <exception cref="NotSupportedException">A field has a type this serializer does not know.</exception>
    public static JsonObject ToJson(object record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var o = new JsonObject();
        foreach (var m in MembersOf(record.GetType()))
            o[m.JsonName] = Write(m.Property.GetValue(record), m.Property.PropertyType);
        return o;
    }

    /// <summary>A record of type <typeparamref name="T"/> from JSON written by <see cref="ToJson"/>; members that are absent keep their defaults.</summary>
    /// <exception cref="FormatException">A member holds a value of the wrong kind.</exception>
    public static T FromJson<T>(JsonObject json) => (T)FromJson(typeof(T), json);

    /// <summary>A record of <paramref name="type"/> from JSON written by <see cref="ToJson"/>.</summary>
    /// <exception cref="FormatException">A member holds a value of the wrong kind.</exception>
    public static object FromJson(Type type, JsonObject json)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(json);
        return ReadObject(type, json);
    }

    /// <summary>The <c>rf_type</c> name of a level object ("light", "item", "nav_point" ...).</summary>
    public static string TypeName(RfObject obj) => TypeName(obj.GetType());

    /// <summary>The <c>rf_type</c> name of a level object type.</summary>
    public static string TypeName(Type type) => ObjectTypes.FirstOrDefault(t => t.Type == type).Name ?? SnakeCase(type.Name.StartsWith("Rf", StringComparison.Ordinal) ? type.Name[2..] : type.Name);

    /// <summary>The object type an <c>rf_type</c> name stands for, or null.</summary>
    public static Type? ObjectType(string typeName) => ObjectTypes.FirstOrDefault(t => string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase)).Type;

    /// <summary>Every level object type with its <c>rf_type</c> name.</summary>
    public static IReadOnlyList<(string Name, Type Type)> ObjectTypes { get; } =
    [
        ("light", typeof(RfLight)),
        ("item", typeof(RfItem)),
        ("clutter", typeof(RfClutter)),
        ("entity", typeof(RfEntity)),
        ("trigger", typeof(RfTrigger)),
        ("event", typeof(RfEvent)),
        ("respawn_point", typeof(RfRespawnPoint)),
        ("nav_point", typeof(RfNavPoint)),
        ("waypoint", typeof(RfLegacyWaypoint)),
        ("geo_region", typeof(RfGeoRegion)),
        ("gas_region", typeof(RfGasRegion)),
        ("push_region", typeof(RfPushRegion)),
        ("climbing_region", typeof(RfClimbingRegion)),
        ("ambient_sound", typeof(RfAmbientSound)),
        ("eax_effect", typeof(RfEaxEffect)),
        ("particle_emitter", typeof(RfParticleEmitter)),
        ("bolt_emitter", typeof(RfBoltEmitter)),
        ("decal", typeof(RfDecal)),
        ("room_effect", typeof(RfRoomEffect)),
        ("cutscene_camera", typeof(RfCutsceneCamera)),
        ("cutscene_path_node", typeof(RfCutscenePathNode)),
        ("target", typeof(RfTarget)),
        ("alpine_mesh", typeof(RfAlpineMesh)),
        ("corona", typeof(RfCorona)),
        ("keyframe", typeof(RfKeyframe)),
    ];

    /// <summary>"ClassName" -> "class_name", "Rf2Data" -> "rf2_data".</summary>
    public static string SnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]))) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>A float as JSON: a number, or a string for NaN and infinities (JSON has no such numbers).</summary>
    public static JsonNode Number(float value) => float.IsFinite(value) ? JsonValue.Create(value)
        : JsonValue.Create(float.IsNaN(value) ? "NaN" : value > 0 ? "Infinity" : "-Infinity");

    /// <summary>A float back from <see cref="Number(float)"/>.</summary>
    /// <exception cref="FormatException">The node is not a number or one of the three strings.</exception>
    public static float ReadFloat(JsonNode? node)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue(out float f)) return f;
            if (v.TryGetValue(out double d)) return (float)d;
            if (v.TryGetValue(out string? s) && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out f)) return f;
            if (v.TryGetValue(out JsonElement e))
            {
                if (e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out d)) return (float)d;
                if (e.ValueKind == JsonValueKind.String && float.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out f)) return f;
            }
        }
        throw new FormatException($"'{node?.ToJsonString() ?? "null"}' is not a number.");
    }

    /// <summary>Floats as a JSON array (see <see cref="Number(float)"/>).</summary>
    public static JsonArray Numbers(params float[] values)
    {
        var a = new JsonArray();
        foreach (float f in values) a.Add(Number(f));
        return a;
    }

    /// <summary>An orientation as nine numbers: right, up, forward.</summary>
    public static JsonArray Matrix(RfMatrix m) => Numbers(m.Right.X, m.Right.Y, m.Right.Z, m.Up.X, m.Up.Y, m.Up.Z, m.Forward.X, m.Forward.Y, m.Forward.Z);

    /// <summary>An orientation back from <see cref="Matrix(RfMatrix)"/>.</summary>
    /// <exception cref="FormatException">The node is not nine numbers.</exception>
    public static RfMatrix ReadMatrix(JsonNode? node)
    {
        var f = ReadFloats(node, 9);
        return new RfMatrix(new Vector3(f[0], f[1], f[2]), new Vector3(f[3], f[4], f[5]), new Vector3(f[6], f[7], f[8]));
    }

    /// <summary>A vector back from a three-number array.</summary>
    /// <exception cref="FormatException">The node is not three numbers.</exception>
    public static Vector3 ReadVector3(JsonNode? node)
    {
        var f = ReadFloats(node, 3);
        return new Vector3(f[0], f[1], f[2]);
    }

    private static float[] ReadFloats(JsonNode? node, int count)
    {
        if (node is not JsonArray a || a.Count != count) throw new FormatException($"Expected {count} numbers, found '{node?.ToJsonString() ?? "null"}'.");
        var result = new float[count];
        for (int i = 0; i < count; i++) result[i] = ReadFloat(a[i]);
        return result;
    }

    // ── Writing ────────────────────────────────────────────────────────────

    private static Member[] MembersOf(Type type) => Members.GetOrAdd(type, t =>
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<Member>();
        // Base-type members first, so every object starts with uid, position and the head fields.
        var chain = new List<Type>();
        for (var c = t; c is not null && c != typeof(object) && c != typeof(ValueType); c = c.BaseType) chain.Insert(0, c);
        foreach (var level in chain)
        {
            foreach (var p in level.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                if (p.GetIndexParameters().Length != 0 || p.GetMethod is null || p.SetMethod is null) continue;
                if (!seen.Add(p.Name)) continue;
                list.Add(new Member(p, SnakeCase(p.Name)));
            }
        }
        return [.. list];
    });

    private static JsonNode? Write(object? value, Type type)
    {
        if (value is null) return null;
        var t = Nullable.GetUnderlyingType(type) ?? type;
        switch (value)
        {
            case bool b: return JsonValue.Create(b);
            case string s: return JsonValue.Create(s);
            case float f: return Number(f);
            case double d: return double.IsFinite(d) ? JsonValue.Create(d) : Number((float)d);
            case byte or sbyte or short or ushort or int: return JsonValue.Create(Convert.ToInt32(value, CultureInfo.InvariantCulture));
            case uint u: return JsonValue.Create(u);
            case long l: return JsonValue.Create(l);
            case ulong ul: return JsonValue.Create(ul);
            case Vector2 v2: return Numbers(v2.X, v2.Y);
            case Vector3 v3: return Numbers(v3.X, v3.Y, v3.Z);
            case Vector4 v4: return Numbers(v4.X, v4.Y, v4.Z, v4.W);
            case Quaternion q: return Numbers(q.X, q.Y, q.Z, q.W);
            case RfMatrix m: return Matrix(m);
            case RflColor c: return new JsonArray(c.R, c.G, c.B, c.A);
            case ImmutableArray<byte> bytes: return JsonValue.Create(Convert.ToBase64String(bytes.IsDefault ? [] : bytes.AsSpan()));
            case ReadOnlyMemory<byte> memory: return JsonValue.Create(Convert.ToBase64String(memory.Span));
        }
        if (t.IsEnum) return JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture));
        if (IsImmutableArray(t, out var element))
        {
            var a = new JsonArray();
            if ((bool)t.GetProperty(nameof(ImmutableArray<int>.IsDefault))!.GetValue(value)!) return a;
            foreach (object? item in (System.Collections.IEnumerable)value) a.Add(Write(item, element));
            return a;
        }
        if (IsValueTuple(t))
        {
            var a = new JsonArray();
            var args = t.GetGenericArguments();
            for (int i = 0; i < args.Length; i++) a.Add(Write(t.GetField("Item" + (i + 1))!.GetValue(value), args[i]));
            return a;
        }
        if (IsRecordLike(t)) return ToJson(value);
        throw new NotSupportedException($"Level extras cannot hold a {t.Name}.");
    }

    // ── Reading ────────────────────────────────────────────────────────────

    private static object ReadObject(Type type, JsonObject json)
    {
        var members = MembersOf(type);
        var ctor = PrimaryConstructor(type);
        object instance;
        var consumed = new HashSet<string>(StringComparer.Ordinal);
        if (ctor is not null)
        {
            var parameters = ctor.GetParameters();
            var args = new object?[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i];
                string jsonName = SnakeCase(p.Name!);
                args[i] = json.TryGetPropertyValue(jsonName, out var node) ? Read(node, p.ParameterType, jsonName) : Default(p.ParameterType);
                consumed.Add(jsonName);
            }
            instance = ctor.Invoke(args);
        }
        else
        {
            instance = Activator.CreateInstance(type) ?? throw new FormatException($"A {type.Name} cannot be created.");
        }
        foreach (var m in members)
        {
            if (consumed.Contains(m.JsonName) || !json.TryGetPropertyValue(m.JsonName, out var node)) continue;
            m.Property.SetValue(instance, Read(node, m.Property.PropertyType, m.JsonName));
        }
        return instance;
    }

    /// <summary>A positional record's constructor (the public one whose parameters all name properties), or null when a parameterless one exists.</summary>
    private static ConstructorInfo? PrimaryConstructor(Type type)
    {
        var ctors = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public);
        // Value types always have a parameterless constructor, so a positional one is preferred when it exists.
        if (!type.IsValueType && ctors.Any(c => c.GetParameters().Length == 0)) return null;
        return ctors
            .Where(c => c.GetParameters().Length > 0 && c.GetParameters().All(p => type.GetProperty(p.Name!, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase) is not null))
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();
    }

    private static object? Default(Type t)
    {
        if (IsImmutableArray(t, out var element)) return EmptyArray(element);
        return t.IsValueType ? Activator.CreateInstance(t) : null;
    }

    private static object EmptyArray(Type element) =>
        typeof(ImmutableArray<>).MakeGenericType(element).GetField("Empty", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

    private static object? Read(JsonNode? node, Type type, string name)
    {
        try
        {
            return ReadValue(node, type);
        }
        catch (Exception e) when (e is InvalidOperationException or OverflowException or ArgumentException)
        {
            throw new FormatException($"'{name}' holds '{node?.ToJsonString() ?? "null"}', which is not a {type.Name}.", e);
        }
    }

    private static object? ReadValue(JsonNode? node, Type type)
    {
        var nullable = Nullable.GetUnderlyingType(type);
        if (node is null)
        {
            if (nullable is not null || !type.IsValueType) return IsImmutableArray(type, out var e) ? EmptyArray(e) : null;
            throw new FormatException($"null is not a {type.Name}.");
        }
        var t = nullable ?? type;
        if (t == typeof(bool)) return node.GetValue<bool>();
        if (t == typeof(string)) return node.GetValue<string>();
        if (t == typeof(float)) return ReadFloat(node);
        if (t == typeof(double)) return (double)ReadFloat(node);
        if (t == typeof(byte)) return checked((byte)Int(node));
        if (t == typeof(sbyte)) return checked((sbyte)Int(node));
        if (t == typeof(short)) return checked((short)Int(node));
        if (t == typeof(ushort)) return checked((ushort)Int(node));
        if (t == typeof(int)) return checked((int)Int(node));
        if (t == typeof(uint)) return checked((uint)Int(node));
        if (t == typeof(long)) return Int(node);
        if (t == typeof(ulong)) return node.GetValue<ulong>();
        if (t.IsEnum) return Enum.ToObject(t, Int(node));
        if (t == typeof(Vector2)) { var f = ReadFloats(node, 2); return new Vector2(f[0], f[1]); }
        if (t == typeof(Vector3)) return ReadVector3(node);
        if (t == typeof(Vector4)) { var f = ReadFloats(node, 4); return new Vector4(f[0], f[1], f[2], f[3]); }
        if (t == typeof(Quaternion)) { var f = ReadFloats(node, 4); return new Quaternion(f[0], f[1], f[2], f[3]); }
        if (t == typeof(RfMatrix)) return ReadMatrix(node);
        if (t == typeof(RflColor))
        {
            if (node is not JsonArray c || c.Count != 4) throw new FormatException("A colour is four numbers.");
            return new RflColor(checked((byte)Int(c[0])), checked((byte)Int(c[1])), checked((byte)Int(c[2])), checked((byte)Int(c[3])));
        }
        if (t == typeof(ImmutableArray<byte>)) return ImmutableArray.Create(Convert.FromBase64String(node.GetValue<string>()));
        if (t == typeof(ReadOnlyMemory<byte>)) return new ReadOnlyMemory<byte>(Convert.FromBase64String(node.GetValue<string>()));
        if (IsImmutableArray(t, out var element))
        {
            if (node is not JsonArray a) throw new FormatException("A list is a JSON array.");
            var items = Array.CreateInstance(element, a.Count);
            for (int i = 0; i < a.Count; i++) items.SetValue(ReadValue(a[i], element), i);
            var create = typeof(ImmutableArray).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == nameof(ImmutableArray.Create) && m.GetParameters() is [{ ParameterType.IsArray: true }])
                .MakeGenericMethod(element);
            return create.Invoke(null, [items]);
        }
        if (IsValueTuple(t))
        {
            var args = t.GetGenericArguments();
            if (node is not JsonArray a || a.Count != args.Length) throw new FormatException($"A pair is {args.Length} values.");
            var values = new object?[args.Length];
            for (int i = 0; i < args.Length; i++) values[i] = ReadValue(a[i], args[i]);
            return Activator.CreateInstance(t, values);
        }
        if (IsRecordLike(t))
        {
            if (node is not JsonObject o) throw new FormatException($"A {t.Name} is a JSON object.");
            return ReadObject(t, o);
        }
        throw new NotSupportedException($"Level extras cannot hold a {t.Name}.");
    }

    private static long Int(JsonNode? node)
    {
        if (node is JsonValue v)
        {
            if (v.TryGetValue(out long l)) return l;
            if (v.TryGetValue(out int i)) return i;
            if (v.TryGetValue(out uint u)) return u;
            if (v.TryGetValue(out double d) && d == Math.Floor(d)) return checked((long)d);
            if (v.TryGetValue(out JsonElement e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out l)) return l;
        }
        throw new FormatException($"'{node?.ToJsonString() ?? "null"}' is not a whole number.");
    }

    private static bool IsImmutableArray(Type t, out Type element)
    {
        element = typeof(object);
        if (!t.IsGenericType || t.GetGenericTypeDefinition() != typeof(ImmutableArray<>)) return false;
        element = t.GetGenericArguments()[0];
        return true;
    }

    private static bool IsValueTuple(Type t) => t.IsValueType && t.IsGenericType && t.FullName?.StartsWith("System.ValueTuple`", StringComparison.Ordinal) == true;

    /// <summary>Model records and record structs (anything in the model's namespaces with settable properties).</summary>
    private static bool IsRecordLike(Type t) => t.Namespace is "Cairn.Lvl.Model" && !t.IsEnum;
}
