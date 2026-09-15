using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentDebugToolkit.Core;

/// <summary>
/// Base envelope every CLI verb writes to stdout. Concrete verb results derive from this
/// via anonymous/dynamic objects assembled in the CLI project; this class documents+enforces
/// the shared "success"/"error"/"message" contract described in docs/CLI_CONTRACT.md.
/// </summary>
public static class JsonOutput
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void WriteSuccess(object payload, bool preserveNullFields = false)
    {
        var dict = new Dictionary<string, object?>(ToDictionary(payload, preserveNullFields))
        {
            ["success"] = true
        };
        Console.WriteLine(JsonSerializer.Serialize(dict, Options));
    }

    public static void WriteError(string error, string message, object? extra = null)
    {
        var dict = new Dictionary<string, object?>
        {
            ["success"] = false,
            ["error"] = error,
            ["message"] = message
        };
        if (extra is not null)
        {
            foreach (var kvp in ToDictionary(extra, preserveNullFields: false))
            {
                dict[kvp.Key] = kvp.Value;
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(dict, Options));
    }

    private static readonly JsonSerializerOptions PreserveNullOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() }
    };

    private static Dictionary<string, object?> ToDictionary(object payload, bool preserveNullFields)
    {
        // Round-trip through JSON to flatten an anonymous/POCO object into a dictionary
        // so we can merge in "success"/"error" fields without reflection gymnastics.
        var options = preserveNullFields ? PreserveNullOptions : Options;
        var json = JsonSerializer.Serialize(payload, payload.GetType(), options);
        return JsonSerializer.Deserialize<Dictionary<string, object?>>(json, options)!;
    }
}
