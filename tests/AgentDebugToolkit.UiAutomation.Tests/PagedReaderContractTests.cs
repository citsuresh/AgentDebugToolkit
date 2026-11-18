using System.ComponentModel;
using System.Reflection;
using AgentDebugToolkit.Inspection;
using AgentDebugToolkit.Mcp;
using ModelContextProtocol.Server;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentDebugToolkit.UiAutomation.Tests;

public sealed class PagedReaderContractTests
{
    private static Dictionary<string, string> Arguments() => new(StringComparer.OrdinalIgnoreCase)
        { ["hwnd"] = "0x123", ["offset"] = "0", ["limit"] = "10" };

    [Theory]
    [InlineData("offset", "-1")]
    [InlineData("offset", "2147483648")]
    [InlineData("offset", "bad")]
    [InlineData("limit", "0")]
    [InlineData("limit", "101")]
    [InlineData("limit", "")]
    [InlineData("limit", "1.5")]
    [InlineData("maxColumns", "0")]
    [InlineData("maxColumns", "65")]
    [InlineData("maxDepth", "-1")]
    [InlineData("maxDepth", "65")]
    [InlineData("maxNodes", "0")]
    [InlineData("maxNodes", "100001")]
    [InlineData("maxDurationMs", "0")]
    [InlineData("maxDurationMs", "120001")]
    [InlineData("native", "true")]
    [InlineData("strategy", "Name")]
    [InlineData("value", "row")]
    [InlineData("controlType", "List")]
    public void SharedValidation_RejectsMalformedConflictingAndUnboundedRequests(string key, string value)
    {
        var args = Arguments(); args[key] = value;
        Assert.False(PageReadOptions.TryParse(args, false, out var options, out var error));
        Assert.Null(options); Assert.False(string.IsNullOrEmpty(error));
    }

    [Theory]
    [InlineData("offset")]
    [InlineData("limit")]
    [InlineData("hwnd")]
    public void ExplicitTargetOffsetAndLimitAreRequired(string missing)
    {
        var args = Arguments(); args.Remove(missing);
        Assert.False(PageReadOptions.TryParse(args, false, out _, out var error)); Assert.Contains(missing, error);
    }

    [Fact]
    public void DefaultsAndMaximums_AreExplicitAndShared()
    {
        var args = Arguments(); Assert.True(PageReadOptions.TryParse(args, false, out var defaults, out _));
        Assert.Equal(32, defaults!.MaxColumns); Assert.Equal(8, defaults.MaxDepth);
        Assert.Equal(5000, defaults.Budget.MaxNodes); Assert.Equal(10000, defaults.Budget.MaxDurationMs);
        Assert.Equal(20000, defaults.Budget.ProcessTimeoutMs);
        args["offset"] = int.MaxValue.ToString(); args["limit"] = "100"; args["maxColumns"] = "64";
        args["maxDepth"] = "64"; args["maxNodes"] = "100000"; args["maxDurationMs"] = "120000";
        Assert.True(PageReadOptions.TryParse(args, false, out var maximum, out _));
        Assert.Equal(130000, maximum!.Budget.ProcessTimeoutMs);
        Assert.False(PageReadOptions.TryParse(args, true, out _, out var conflict)); Assert.Contains("maxColumns", conflict);
    }

    [Theory]
    [InlineData("read-rows", "--offset=0", "--OFFSET=1")]
    [InlineData("read-tree-items", "--limit=1", "--limit=1")]
    [InlineData("read-rows", "--maxNodes=1", "--maxNodes=2")]
    [InlineData("read-rows", "--strategy=Name", "--strategy=AutomationId")]
    [InlineData("read-tree-items", "--maxColumns=2", null)]
    [InlineData("read-rows", "--native=true", null)]
    [InlineData("read-rows", "--limit=0", null)]
    [InlineData("read-tree-items", "--offset=-1", null)]
    [InlineData("read-rows", "--maxDurationMs=0", null)]
    [InlineData("read-rows", "--maxDepth=65", null)]
    [InlineData("read-tree-items", "unexpected", null)]
    [InlineData("read-rows", "--limit", null)]
    public async Task CliRejectsInvalidPagesBeforeAnyWindowLookup(string verb, string first, string? second)
    {
        var args = new List<string> { verb, "--hwnd=not-a-window", "--offset=0", "--limit=1", first };
        if (second is not null) args.Add(second);
        var result = await ExecutableIdentityTests.Run("ui", args.ToArray());
        Assert.Equal(1, result.ExitCode); Assert.Equal("invalid-argument", result.Json.GetProperty("error").GetString());
        Assert.DoesNotContain("Invalid hwnd", result.Json.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("read-rows")]
    [InlineData("read-tree-items")]
    public async Task CliValidBudgetsReachExistingHwndValidationWithoutDesktopAccess(string verb)
    {
        var result = await ExecutableIdentityTests.Run("ui", verb, "--hwnd=not-a-window", "--offset=0", "--limit=1");
        Assert.Equal(1, result.ExitCode); Assert.Contains("Invalid hwnd", result.Json.GetProperty("message").GetString());
    }

    private static (List<string>? Args, int? TimeoutMs, string? Error) Plan(string verb, int offset = 0, int limit = 10,
        string? strategy = null, string? value = null, string? type = null, string? scope = null,
        int? columns = null, int? depth = null, int? nodes = null, int? duration = null) =>
        ((List<string>?, int?, string?))typeof(UiAutomationTools).GetMethod("PageArgs", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object?[] { verb, "0x123", offset, limit, strategy, value, type, scope, columns, depth, nodes, duration })!;

    [Theory]
    [InlineData("read-rows")]
    [InlineData("read-tree-items")]
    public void McpPlanning_ForwardsRequiredPageAndPreservesFiniteDefaults(string verb)
    {
        var plan = Plan(verb); Assert.Null(plan.Error); Assert.Equal(20000, plan.TimeoutMs);
        Assert.Equal(new[] { verb, "--hwnd", "0x123", "--offset", "0", "--limit", "10" }, plan.Args);
        var bounded = Plan(verb, int.MaxValue, 100, "Name", "--Rows", "List", "0x456", depth: 64, nodes: 100000, duration: 120000);
        Assert.Null(bounded.Error); Assert.Equal(130000, bounded.TimeoutMs);
        Assert.Contains("--value=--Rows", bounded.Args!); Assert.Contains("--scopeHwnd", bounded.Args!);
        Assert.Contains("2147483647", bounded.Args!); Assert.Contains("100000", bounded.Args!);
        Assert.Contains("120000", bounded.Args!); Assert.Contains("64", bounded.Args!);
    }

    [Fact]
    public void McpPlanning_DoesNotMixTreeAndRowSpecificOptions()
    {
        Assert.Null(Plan("read-rows", columns: 64).Error);
        Assert.Contains("maxColumns", Plan("read-tree-items", columns: 64).Error);
        Assert.Contains("strategy", Plan("read-rows", strategy: "Name").Error);
    }

    [Theory]
    [InlineData(-1, 1, null, null)]
    [InlineData(0, 0, null, null)]
    [InlineData(0, 101, null, null)]
    [InlineData(0, 1, 0, null)]
    [InlineData(0, 1, null, 120001)]
    public async Task McpInvalidRequestsFailBeforeCliLaunch(int offset, int limit, int? nodes, int? duration)
    {
        var tools = new UiAutomationTools(new CliRunner(NullLogger.Instance, "missing", "missing"));
        foreach (var result in new[] { await tools.ReadRows("0x123", offset, limit, maxNodes: nodes, maxDurationMs: duration),
            await tools.ReadTreeItems("0x123", offset, limit, maxNodes: nodes, maxDurationMs: duration) })
        {
            Assert.Equal(1, result.ExitCode); Assert.Null(result.WrapperErrorCode);
            Assert.Equal("invalid-argument", result.Result!.Value.GetProperty("error").GetString());
        }
    }

    [Theory]
    [InlineData(nameof(UiAutomationTools.ReadRows), "ui_read_rows")]
    [InlineData(nameof(UiAutomationTools.ReadTreeItems), "ui_read_tree_items")]
    public void McpMetadata_IsReadOnlyAndRequiresExplicitPageArguments(string methodName, string toolName)
    {
        var method = typeof(UiAutomationTools).GetMethod(methodName)!;
        var annotation = method.GetCustomAttribute<McpServerToolAttribute>()!;
        Assert.Equal(toolName, annotation.Name); Assert.True(annotation.ReadOnly); Assert.False(annotation.Destructive);
        Assert.True(annotation.Idempotent); Assert.False(annotation.OpenWorld);
        Assert.Equal(new[] { "hwnd", "offset", "limit" }, method.GetParameters().Where(parameter => !parameter.IsOptional).Select(parameter => parameter.Name));
        Assert.Contains("never scroll, expand, realize, select", method.GetCustomAttribute<DescriptionAttribute>()!.Description);
    }

    [Fact]
    public async Task InfoAdvertisesUiaOnlyPagedCapabilities()
    {
        var result = await ExecutableIdentityTests.Run("ui", "info");
        var capabilities = result.Json.GetProperty("capabilities").EnumerateArray().Select(capability => capability.GetString()).ToArray();
        Assert.Contains("ui.read-rows.uia-paged.v1", capabilities); Assert.Contains("ui.read-tree-items.uia-paged.v1", capabilities);
        Assert.DoesNotContain(capabilities, capability => capability!.Contains("native-memory"));
    }

    [Fact]
    public async Task McpPublicMethods_ForwardToRealCliArgumentValidationWithoutLiveTargets()
    {
        var tools = new UiAutomationTools(new CliRunner(NullLogger.Instance, AppContext.BaseDirectory, AppContext.BaseDirectory));
        foreach (var result in new[]
        {
            await tools.ReadRows("not-a-window", 1, 2, "Name", "--Rows", "List", maxColumns: 4, maxNodes: 10, maxDurationMs: 10000),
            await tools.ReadTreeItems("not-a-window", 1, 2, "Name", "--Tree", "Tree", maxDepth: 4, maxNodes: 10, maxDurationMs: 10000)
        })
        {
            Assert.Null(result.WrapperErrorCode); Assert.Equal(1, result.ExitCode);
            Assert.Equal("invalid-argument", result.Result!.Value.GetProperty("error").GetString());
            Assert.Contains("Invalid hwnd", result.Result.Value.GetProperty("message").GetString());
        }
    }
}