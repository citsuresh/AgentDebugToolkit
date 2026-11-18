using System.ComponentModel;
using System.Reflection;
using AgentDebugToolkit.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using Xunit;

namespace AgentDebugToolkit.UiAutomation.Tests;

public sealed class BoundedInspectionMcpTests
{
    private static (List<string>? Args, int? TimeoutMs, string? Error) Plan(string verb, int? depth = null,
        int? nodes = null, int? duration = null, string? name = null, string? type = null) =>
        ((List<string>?, int?, string?))typeof(UiAutomationTools).GetMethod("InspectionArgs", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object?[] { verb, "0x123", depth, nodes, duration, name, type })!;

    [Theory]
    [InlineData("inspect")]
    [InlineData("read-visible-text")]
    public void Forwarding_PreservesDefaultsAndCombinesExplicitBoundsWithProcessHeadroom(string verb)
    {
        var legacy = Plan(verb);
        Assert.Null(legacy.Error);
        Assert.Null(legacy.TimeoutMs);
        Assert.Equal(new[] { verb, "--hwnd", "0x123" }, legacy.Args);
        var bounded = Plan(verb, 0, 100000, 120000);
        Assert.Null(bounded.Error);
        Assert.Equal(130000, bounded.TimeoutMs);
        Assert.Equal(new[] { verb, "--hwnd", "0x123", "--maxDepth", "0", "--maxNodes", "100000", "--maxDurationMs", "120000" }, bounded.Args);
        Assert.Null(Plan(verb, nodes: 1).TimeoutMs);
        Assert.Equal(10001, Plan(verb, duration: 1).TimeoutMs);
    }

    [Fact]
    public void Forwarding_KeepsExistingFilterArgumentsAndAllowsLegacyInspectDepth()
    {
        Assert.Equal(new[] { "inspect", "--hwnd", "0x123", "--maxDepth", "8", "--nameContains", "needle", "--controlType", "Button", "--maxNodes", "100" },
            Plan("inspect", 8, 100, name: "needle", type: "Button").Args);
        Assert.Null(Plan("inspect", depth: -1).Error);
        Assert.Contains("maxDepth", Plan("inspect", depth: -1, nodes: 1).Error);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    [InlineData(100001, null)]
    [InlineData(null, 0)]
    [InlineData(null, -1)]
    [InlineData(null, 120001)]
    public async Task InvalidBounds_ReturnArgumentErrorsBeforeAttemptingToLaunchAnyCli(int? nodes, int? duration)
    {
        var tools = new UiAutomationTools(new CliRunner(NullLogger.Instance, "missing", "missing"));
        foreach (var result in new[]
        {
            await tools.Inspect("not-a-window", maxNodes: nodes, maxDurationMs: duration),
            await tools.ReadVisibleText("not-a-window", maxNodes: nodes, maxDurationMs: duration)
        })
        {
            Assert.Equal(1, result.ExitCode);
            Assert.Null(result.WrapperErrorCode);
            Assert.Equal("invalid-argument", result.Result!.Value.GetProperty("error").GetString());
            Assert.False(result.Result.Value.GetProperty("success").GetBoolean());
        }
    }

    [Theory]
    [InlineData(nameof(UiAutomationTools.Inspect), "ui_inspect", 4)]
    [InlineData(nameof(UiAutomationTools.ReadVisibleText), "ui_read_visible_text", 2)]
    public void Metadata_RemainsReadOnlyAndPreservesCancellationTokenSourcePosition(string methodName, string toolName, int tokenIndex)
    {
        var method = typeof(UiAutomationTools).GetMethod(methodName)!;
        var tool = method.GetCustomAttribute<McpServerToolAttribute>()!;
        Assert.Equal(toolName, tool.Name);
        Assert.True(tool.ReadOnly);
        Assert.False(tool.Destructive);
        Assert.False(tool.OpenWorld);
        var parameters = method.GetParameters();
        Assert.Equal(typeof(CancellationToken), parameters[tokenIndex].ParameterType);
        Assert.Equal("maxNodes", parameters[tokenIndex + 1].Name);
        Assert.Equal("maxDurationMs", parameters[tokenIndex + 2].Name);
        Assert.Null(parameters[tokenIndex + 1].DefaultValue);
        Assert.Null(parameters[tokenIndex + 2].DefaultValue);
        Assert.Contains("cannot be interrupted", method.GetCustomAttribute<DescriptionAttribute>()!.Description);
    }
}