using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cairn.Rfa.Retarget;

/// <summary>
/// The JSON settings rig profiles and bone maps are saved with: indented, camelCase property names,
/// enums as their names, vectors as <c>[x, y, z]</c>.
/// </summary>
public static class RetargetJson
{
    /// <summary>Shared serializer options (read-only once built).</summary>
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new Vector3JsonConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>Serialises a value with <see cref="Options"/>.</summary>
    internal static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>
    /// Deserialises a value, turning every JSON problem into a <see cref="FormatException"/> whose
    /// message says what the file was expected to be.
    /// </summary>
    internal static T Read<T>(string json, string what) where T : class
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options)
                ?? throw new FormatException($"The file is empty; it should hold a {what}.");
        }
        catch (JsonException ex)
        {
            throw new FormatException($"This is not a valid {what} file ({ex.Message}). Save it again from Cairn, or fix the JSON by hand.", ex);
        }
        catch (NotSupportedException ex)
        {
            throw new FormatException($"This is not a valid {what} file ({ex.Message}).", ex);
        }
    }
}

/// <summary>Writes a <see cref="Vector3"/> as a three-number array and reads it back.</summary>
public sealed class Vector3JsonConverter : JsonConverter<Vector3>
{
    /// <inheritdoc />
    public override Vector3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("A vector must be an array of three numbers.");
        var c = new float[3];
        for (int i = 0; i < 3; i++)
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.Number) throw new JsonException("A vector must be an array of three numbers.");
            c[i] = reader.GetSingle();
        }
        if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray) throw new JsonException("A vector must be an array of three numbers.");
        return new Vector3(c[0], c[1], c[2]);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Vector3 value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStartArray();
        writer.WriteNumberValue(value.X);
        writer.WriteNumberValue(value.Y);
        writer.WriteNumberValue(value.Z);
        writer.WriteEndArray();
    }
}
