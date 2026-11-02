using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pos.Domain.Common;

/// <summary>
/// The one JSON format used everywhere: database, hub API, cloud and sync.
/// camelCase names, enums as words (e.g. "Accepted"), money as { "minorUnits": 1250, "currency": "GBP" }.
/// </summary>
public static class PosJson
{
    /// <summary>Shared serializer settings.</summary>
    public static JsonSerializerOptions Options { get; } = Create();

    /// <summary>Applies the shared settings to an existing options object (e.g. the web framework's).</summary>
    public static void Configure(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.PropertyNameCaseInsensitive = true;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.RespectNullableAnnotations = true;
        options.Converters.Add(new JsonStringEnumConverter());
    }

    /// <summary>Converts to JSON text.</summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Reads JSON text; throws if it is empty or invalid.</summary>
    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException("Empty JSON.");

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions();
        Configure(options);
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
