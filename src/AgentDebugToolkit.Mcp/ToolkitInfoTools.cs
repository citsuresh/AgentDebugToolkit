using System.ComponentModel;
using System.Text.Json;
using AgentDebugToolkit.Identity;
using ModelContextProtocol.Server;

namespace AgentDebugToolkit.Mcp;

[McpServerToolType]
public sealed class ToolkitInfoTools(CliRunner cli, IReadOnlySet<string> enabledGroups)
{
    [McpServerTool(Name = "toolkit_info", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Reports the actual server or configured CLI executable/assembly paths and SHA-256 hashes, embedded build identity and capability IDs. Optional verifyPackage checks portable package provenance and hashes without modifying files. Does not attach to Visual Studio, access UIA, or launch a console session. Disabled tool groups stay gated; no path search or capability inference for old binaries.")]
    public async Task<ToolInvocationResult> Info(
        [Description("Component: server, ui, vs or console. Non-server components require their tool group enabled.")] string tool = "server",
        [Description("Verify the adjacent/parent package-manifest.json. Reports matched, mismatched or not-present; SHA-256 integrity is not authenticity.")] bool verifyPackage = false,
        CancellationToken cancellationToken = default)
    {
        if (tool == "server")
        {
            var result = ToolkitIdentity.CreateResult(verifyPackage);
            return new(result.ExitCode, result.Json, null, null, null);
        }
        if (tool is not ("ui" or "vs" or "console")) return Error("invalid-argument", "tool must be server, ui, vs or console.");
        if (!enabledGroups.Contains(tool)) return Error("tool-disabled", $"The {tool} group is disabled; no executable was launched.");
        var resultFromCli = await cli.RunAsync($"agentdebug-{tool}.exe", verifyPackage ? new[] { "info", "--verifyPackage", "true" } : new[] { "info" }, null, cancellationToken);
        return RelayInfo(resultFromCli);
    }

    private static ToolInvocationResult RelayInfo(CliResult resultFromCli)
    {
        if (resultFromCli.Json is { } json && json.TryGetProperty("error", out var error) && error.GetString() == "invalid-argument"
            && json.TryGetProperty("message", out var message) && message.GetString()?.Contains("Unknown verb 'info'", StringComparison.OrdinalIgnoreCase) == true)
            return Error("info-not-supported", "The configured executable does not support info; no capabilities were inferred.");
        return new(resultFromCli.ExitCode, resultFromCli.Json, resultFromCli.ErrorCode, resultFromCli.ErrorMessage, resultFromCli.RawOutput);
    }

    private static ToolInvocationResult Error(string error, string message) => new(1,
        JsonSerializer.SerializeToElement(new { success = false, error, message }), null, null, null);
}