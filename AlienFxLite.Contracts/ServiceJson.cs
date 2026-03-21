using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlienFxLite.Contracts;

public static class ServiceJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    public static JsonElement ToElement<T>(T payload)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Options);
        using JsonDocument document = JsonDocument.Parse(bytes);
        return document.RootElement.Clone();
    }

    public static T Deserialize<T>(JsonElement element)
    {
        T? result = element.Deserialize<T>(Options);
        if (result is null)
        {
            throw new InvalidOperationException($"Unable to deserialize payload to {typeof(T).Name}.");
        }

        return result;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
