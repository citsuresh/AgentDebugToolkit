using AgentDebugToolkit.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// --tools <comma-separated list from {vs, ui, console}>, default "vs,ui". Controls which tool
// groups this server process registers — console is opt-in only and never registered unless
// explicitly listed, since it is the highest-risk group (see ConsoleTools.cs/docs/MCP_SERVER.md).
var toolGroups = ParseToolGroups(args);
if (toolGroups is null)
{
    // ParseToolGroups already wrote the error to stderr.
    return 1;
}

var builder = Host.CreateApplicationBuilder(args);

// stdout is reserved exclusively for the MCP protocol stream; all logging/diagnostics must go
// to stderr only, or they would corrupt the stdio transport.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("CliRunner");
    var vsBinDirectory = CliRunner.ResolveBinDirectory(logger, "AGENTDEBUG_VS_BIN");
    var uiBinDirectory = CliRunner.ResolveBinDirectory(logger, "AGENTDEBUG_UI_BIN");
    var consoleBinDirectory = toolGroups.Contains("console")
        ? CliRunner.ResolveBinDirectory(logger, "AGENTDEBUG_CONSOLE_BIN")
        : null;
    return new CliRunner(logger, vsBinDirectory, uiBinDirectory, consoleBinDirectory);
});

var mcpBuilder = builder.Services
    .AddMcpServer()
    .WithStdioServerTransport();

if (toolGroups.Contains("vs"))
{
    mcpBuilder.WithTools<VsDebuggerTools>();
}

if (toolGroups.Contains("ui"))
{
    mcpBuilder.WithTools<UiAutomationTools>();
}

if (toolGroups.Contains("console"))
{
    mcpBuilder.WithTools<ConsoleTools>();
}

await builder.Build().RunAsync();
return 0;

/// <summary>
/// Parses --tools from the raw command-line args. Returns the default {"vs", "ui"} set if
/// --tools is absent, the parsed set if valid, or null (after writing a clear message to
/// stderr) if an unknown group name was given — this fails startup rather than silently
/// ignoring a typo'd group.
/// </summary>
static HashSet<string>? ParseToolGroups(string[] args)
{
    var validGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "vs", "ui", "console" };
    var defaultGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "vs", "ui" };

    string? rawValue = null;
    for (var i = 0; i < args.Length; i++)
    {
        if (string.Equals(args[i], "--tools", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            rawValue = args[i + 1];
            break;
        }
        if (args[i].StartsWith("--tools=", StringComparison.OrdinalIgnoreCase))
        {
            rawValue = args[i]["--tools=".Length..];
            break;
        }
    }

    if (rawValue is null)
    {
        return defaultGroups;
    }

    var requested = rawValue
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToList();

    if (requested.Count == 0)
    {
        Console.Error.WriteLine("--tools was given but contained no group names. Expected a comma-separated list from: vs, ui, console.");
        return null;
    }

    var unknown = requested.Where(g => !validGroups.Contains(g)).ToList();
    if (unknown.Count > 0)
    {
        Console.Error.WriteLine(
            $"--tools contains unknown group name(s): {string.Join(", ", unknown)}. Valid groups are: vs, ui, console.");
        return null;
    }

    return new HashSet<string>(requested, StringComparer.OrdinalIgnoreCase);
}
