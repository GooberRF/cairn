using System.Reflection;
using System.Text.Json.Nodes;
using Cairn.Formats.Rfl;
using Cairn.Lvl.Conversion;
using Cairn.Lvl.Model;

namespace Cairn.Lvl.Tests;

/// <summary>Every level record written as glTF extras reads back equal, whatever its fields hold.</summary>
public class LvlExtrasTests
{
    /// <summary>A value of <paramref name="type"/> that differs from its default (seeded so two fields differ).</summary>
    private static object? Sample(Type type, int seed, int depth = 0)
    {
        var nullable = Nullable.GetUnderlyingType(type);
        if (nullable is not null) return Sample(nullable, seed, depth);
        if (type == typeof(bool)) return true;
        if (type == typeof(string)) return $"text {seed} \"quoted\"";
        if (type == typeof(float)) return seed % 5 == 0 ? float.NaN : seed * 0.375f + 0.1f;
        if (type == typeof(double)) return seed * 0.5;
        if (type == typeof(byte)) return (byte)(seed % 200 + 1);
        if (type == typeof(sbyte)) return (sbyte)(-(seed % 100) - 1);
        if (type == typeof(short)) return (short)(seed + 1);
        if (type == typeof(ushort)) return (ushort)(seed + 1);
        if (type == typeof(int)) return seed * 7 + 1;
        if (type == typeof(uint)) return 0x80000000u + (uint)seed;
        if (type == typeof(long)) return seed * 1000L;
        if (type.IsEnum)
        {
            var values = Enum.GetValues(type);
            return values.GetValue(values.Length - 1);
        }
        if (type == typeof(Vector2)) return new Vector2(seed, -seed);
        if (type == typeof(Vector3)) return new Vector3(seed, seed + 0.5f, -seed);
        if (type == typeof(RfMatrix)) return LvlConvertTestData.Rotation(seed * 0.1f, 0.2f, 0.3f);
        if (type == typeof(RflColor)) return new RflColor((byte)seed, 2, 3, 4);
        if (type == typeof(ReadOnlyMemory<byte>)) return new ReadOnlyMemory<byte>([1, 2, (byte)seed]);
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ImmutableArray<>))
        {
            var element = type.GetGenericArguments()[0];
            var items = Array.CreateInstance(element, depth > 2 ? 0 : 2);
            for (int i = 0; i < items.Length; i++) items.SetValue(Sample(element, seed + i + 1, depth + 1), i);
            return typeof(ImmutableArray).GetMethods().First(m => m.Name == "Create" && m.GetParameters() is [{ ParameterType.IsArray: true }])
                .MakeGenericMethod(element).Invoke(null, [items]);
        }
        if (type.IsValueType && type.IsGenericType && type.FullName!.StartsWith("System.ValueTuple`", StringComparison.Ordinal))
        {
            var args = type.GetGenericArguments();
            return Activator.CreateInstance(type, args.Select((a, i) => Sample(a, seed + i, depth + 1)).ToArray());
        }
        if (type.Namespace == "Cairn.Lvl.Model")
        {
            var ctor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
            object instance = type.GetConstructors().Any(c => c.GetParameters().Length == 0) && !type.IsValueType
                ? Activator.CreateInstance(type)!
                : ctor.Invoke([.. ctor.GetParameters().Select((p, i) => Sample(p.ParameterType, seed + i, depth + 1))]);
            int k = 0;
            foreach (var p in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (p.SetMethod is null || p.GetIndexParameters().Length > 0) continue;
                p.SetValue(instance, Sample(p.PropertyType, seed + ++k, depth + 1));
            }
            return instance;
        }
        throw new NotSupportedException(type.Name);
    }

    public static IEnumerable<object[]> RecordTypes() =>
        LvlExtras.ObjectTypes.Select(t => new object[] { t.Type.Name })
            .Concat(new[] { nameof(RfRoom), nameof(RfMovingData), nameof(RfLevelProperties), nameof(RfLevelInfo), nameof(RfAlpineBrushData), nameof(RfFace), nameof(RfAlpineLevelProperties) }
                .Select(n => new object[] { n }));

    [Theory]
    [MemberData(nameof(RecordTypes))]
    public void Every_field_of_a_record_reads_back(string typeName)
    {
        var type = typeof(RfLevel).Assembly.GetType("Cairn.Lvl.Model." + typeName)!;
        var original = Sample(type, 3)!;
        var json = LvlExtras.ToJson(original);
        // Through text, as a glTF file stores it.
        var reparsed = (JsonObject)JsonNode.Parse(json.ToJsonString())!;
        var back = LvlExtras.FromJson(type, reparsed);
        Assert.True(RfModelDiff.AreEqual(original, back), string.Join("; ", RfModelDiff.Compare(original, back)));
    }

    [Fact]
    public void Computed_properties_are_not_written_and_names_are_snake_case()
    {
        var json = LvlExtras.ToJson(new RfGeoRegion { Packed = 0x40002, Uid = 3 });
        Assert.True(json.ContainsKey("packed"));
        Assert.True(json.ContainsKey("hidden_in_editor"));
        Assert.False(json.ContainsKey("is_sphere"));
        Assert.False(json.ContainsKey("hardness"));
        Assert.Equal("rf2_data", LvlExtras.SnakeCase("Rf2Data"));
        Assert.Equal("use_custom_attack_range", LvlExtras.SnakeCase("UseCustomAttackRange"));
        Assert.Equal("NaN", LvlExtras.Number(float.NaN).GetValue<string>());
        Assert.Equal(float.NegativeInfinity, LvlExtras.ReadFloat(LvlExtras.Number(float.NegativeInfinity)));
    }

    [Fact]
    public void Object_type_names_map_both_ways()
    {
        foreach (var (name, type) in LvlExtras.ObjectTypes)
        {
            Assert.Equal(type, LvlExtras.ObjectType(name));
            Assert.Equal(name, LvlExtras.TypeName(type));
        }
        Assert.Null(LvlExtras.ObjectType("brush"));
    }

    [Fact]
    public void Wrong_values_are_a_format_exception()
    {
        var json = new JsonObject { ["uid"] = "seven" };
        Assert.Throws<FormatException>(() => LvlExtras.FromJson<RfItem>(json));
        Assert.Throws<FormatException>(() => LvlExtras.ReadMatrix(new JsonArray(1, 2, 3)));
    }
}
