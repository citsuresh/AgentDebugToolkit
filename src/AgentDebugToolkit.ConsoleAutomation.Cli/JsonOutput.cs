using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentDebugToolkit.ConsoleAutomation.Cli;

/// <summary>
/// Local JSON envelope implementation. This CLI remains independently buildable and deployable,
/// following the shared contract style without taking a dependency on another CLI assembly.
/// </summary>
internal static class JsonOutput
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static void WriteSuccess(object payload)
    {
        var json = JsonSerializer.SerializeToElement(payload, Options);
        var result = new Dictionary<string, object?> { ["success"] = true };
        foreach (var property in json.EnumerateObject())
        {
            result[property.Name] = property.Value;
        }

        Console.WriteLine(JsonSerializer.Serialize(result, Options));
    }

    public static void WriteError(string error, string message) =>
        Console.WriteLine(JsonSerializer.Serialize(new { success = false, error, message }, Options));
}
