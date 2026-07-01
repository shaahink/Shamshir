using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingEngine.Web.Configuration.Json;

/// <summary>
/// iter-38 W-B8: serialize every <see cref="DateTime"/> as an explicit UTC ISO-8601 string with a
/// trailing <c>Z</c>. Domain timestamps are UTC but carry <see cref="DateTimeKind.Unspecified"/> (EF/SQLite
/// materializes them that way), so System.Text.Json emitted no offset and the browser's <c>new Date(...)</c>
/// parsed them as LOCAL — shifting every chart/label by the viewer's timezone. Treat Unspecified as UTC;
/// normalize Local to UTC. Applies to <c>DateTime?</c> automatically (STJ wraps the nullable).
/// </summary>
public sealed class UtcDateTimeConverter : JsonConverter<DateTime>
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetDateTime();
        return value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value;
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
        writer.WriteStringValue(utc.ToString(Format, CultureInfo.InvariantCulture));
    }
}
