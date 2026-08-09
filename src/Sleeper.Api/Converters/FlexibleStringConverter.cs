using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sleeper.Api.Converters;

/// <summary>
/// Reads JSON values that may be either a string or a number into a string? property.
/// The Sleeper API is inconsistent — fields like espn_id, sportradar_id, etc.
/// sometimes appear as strings and sometimes as numbers.
/// </summary>
public class FlexibleStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number when reader.TryGetInt64(out var l) => l.ToString(CultureInfo.InvariantCulture),
            JsonTokenType.Number when reader.TryGetDouble(out var d) => d.ToString(CultureInfo.InvariantCulture),
            JsonTokenType.Null => null,
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            _ => reader.GetString()
        };
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value);
    }
}
