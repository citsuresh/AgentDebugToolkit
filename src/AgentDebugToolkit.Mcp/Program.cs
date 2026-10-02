using AgentDebugToolkit.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
    return new CliRunner(logger, vsBinDirectory, uiBinDirectory);
});

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<VsDebuggerTools>()
    .WithTools<UiAutomationTools>();

await builder.Build().RunAsync();
