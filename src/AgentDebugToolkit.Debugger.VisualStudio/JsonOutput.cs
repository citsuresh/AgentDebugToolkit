using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentDebugToolkit.Debugger.VisualStudio;

/// <summary>
/// Minimal, self-contained JSON envelope for this project. Deliberately duplicated from
/// AgentDebugToolkit.Core.JsonOutput rather than referencing Core: per docs/ARCHITECTURE.md,
/// AgentDebugToolkit.Core is meant to be shared "where genuinely common (e.g. a shared
/// JsonResult&lt;T&gt; envelope type)" across CLIs. This project deliberately does not take that
/// dependency so it stays independently buildable/deployable with zero coupling to the UI
/// automation CLI's assembly; it only follows the same JSON envelope *style* documented in
/// docs/CLI_CONTRACT.md, re-implemented locally.
/// </summary>
internal static class JsonOutput
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions PreserveNullOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void WriteSuccess(object payload, bool preserveNullFields = false)
    {
        var options = preserveNullFields ? PreserveNullOptions : Options;
        var dict = new Dictionary<string, object?>(ToDictionary(payload, options))
        {
            ["success"] = true
        };
        Console.WriteLine(JsonSerializer.Serialize(dict, options));
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
            foreach (var kvp in ToDictionary(extra, Options))
            {
                dict[kvp.Key] = kvp.Value;
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(dict, Options));
    }

    private static Dictionary<string, object?> ToDictionary(object payload, JsonSerializerOptions options)
    {
        var json = JsonSerializer.Serialize(payload, payload.GetType(), options);
        return JsonSerializer.Deserialize<Dictionary<string, object?>>(json, options)!;
    }
}
