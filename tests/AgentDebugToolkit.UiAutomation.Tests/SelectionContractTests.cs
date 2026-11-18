using System.ComponentModel;
using System.Reflection;
using AgentDebugToolkit.Inspection;
using AgentDebugToolkit.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using Xunit;

namespace AgentDebugToolkit.UiAutomation.Tests;

public sealed class SelectionContractTests
{
    private static Dictionary<string, string> Arguments(bool row) => row
        ? new(StringComparer.OrdinalIgnoreCase) { ["hwnd"] = "0x123", ["mode"] = "replace", ["row"] = "0", ["rowKind"] = "exposed" }
        : new(StringComparer.OrdinalIgnoreCase) { ["hwnd"] = "0x123", ["mode"] = "replace", ["strategy"] = "Name", ["value"] = "Item" };

    [Theory]
    [InlineData(false, "mode", "add")]
    [InlineData(false, "mode", "")]
    [InlineData(false, "maxNodes", "0")]
    [InlineData(false, "maxDurationMs", "120001")]
    [InlineData(false, "maxDepth", "1")]
    [InlineData(false, "row", "0")]
    [InlineData(false, "offset", "0")]
    [InlineData(true, "row", "-1")]
    [InlineData(true, "row", "2147483648")]
    [InlineData(true, "row", "1.5")]
    [InlineData(true, "rowKind", "auto")]
    [InlineData(true, "maxDepth", "65")]
    [InlineData(true, "maxColumns", "1")]
    [InlineData(true, "limit", "1")]
    [InlineData(true, "restore", "true")]
    [InlineData(true, "strategy", "Name")]
    public void SharedValidationRejectsInvalidOrConflictingFlags(bool row, string key, string value)
    {
        var args = Arguments(row); args[key] = value;
        Assert.False(SelectionOptions.TryParse(args, row, out var options, out var error));
        Assert.Null(options); Assert.False(string.IsNullOrEmpty(error));
    }

    [Theory]
    [InlineData(false, "hwnd")]
    [InlineData(false, "mode")]
    [InlineData(false, "strategy")]
    [InlineData(false, "value")]
    [InlineData(true, "mode")]
    [InlineData(true, "row")]
    [InlineData(true, "rowKind")]
    public void ExplicitRequiredArgumentsCannotBeOmitted(bool row, string missing)
    {
        var args = Arguments(row); args.Remove(missing);
        Assert.False(SelectionOptions.TryParse(args, row, out _, out var error)); Assert.Contains(missing, error);
    }

    [Fact]
    public void DefaultsAndMaximumsReuseThePagingBudgetWithoutAddingPagingFlags()
    {
        var args = Arguments(true);
        Assert.True(SelectionOptions.TryParse(args, true, out var options, out _));
        Assert.Equal(1, options!.Bounds.Limit); Assert.Equal(8, options.Bounds.MaxDepth);
        Assert.Equal(5000, options.Bounds.Budget.MaxNodes); Assert.Equal(20000, options.Bounds.Budget.ProcessTimeoutMs);
        args["row"] = int.MaxValue.ToString(); args["maxNodes"] = "100000"; args["maxDurationMs"] = "120000"; args["maxDepth"] = "64";
        Assert.True(SelectionOptions.TryParse(args, true, out options, out _));
        Assert.Equal(int.MaxValue, options!.Bounds.Offset); Assert.Equal(130000, options.Bounds.Budget.ProcessTimeoutMs);
    }

    [Theory]
    [InlineData("select-item", "--mode=add")]
    [InlineData("select-item", "--MODE=replace")]
    [InlineData("select-item", "--row=0")]
    [InlineData("select-item", "--maxNodes=0")]
    [InlineData("select-row", "--row=-1")]
    [InlineData("select-row", "--rowKind=auto")]
    [InlineData("select-row", "--maxDepth=65")]
    [InlineData("select-row", "--limit=1")]
    [InlineData("select-row", "unexpected")]
    [InlineData("select-row", "--maxDurationMs")]
    public async Task CliRejectsArgumentsBeforeHwndLookupOrMutation(string verb, string invalid)
    {
        var args = new List<string> { verb, "--hwnd=not-a-window", "--mode=replace", invalid };
        if (verb == "select-item") args.AddRange(new[] { "--strategy=Name", "--value=Item" });
        if (verb == "select-row" && !invalid.StartsWith("--row=")) args.Add("--row=0");
        if (verb == "select-row" && !invalid.StartsWith("--rowKind=")) args.Add("--rowKind=exposed");
        var result = await ExecutableIdentityTests.Run("ui", args.ToArray());
        Assert.Equal(1, result.ExitCode); Assert.Equal("invalid-argument", result.Json.GetProperty("error").GetString());
        Assert.DoesNotContain("Invalid hwnd", result.Json.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("select-item")]
    [InlineData("select-row")]
    public async Task CliValidSelectionOptionsReachExistingHwndValidationWithoutDesktopAccess(string verb)
    {
        var args = new List<string> { verb, "--hwnd=not-a-window", "--mode=replace" };
        args.AddRange(verb == "select-row" ? new[] { "--row=0", "--rowKind=logical" } : new[] { "--strategy=Name", "--value=Item" });
        var result = await ExecutableIdentityTests.Run("ui", args.ToArray());
        Assert.Equal(1, result.ExitCode); Assert.Contains("Invalid hwnd", result.Json.GetProperty("message").GetString());
        Assert.False(result.Json.GetProperty("actionAttempted").GetBoolean());
    }

    private static (List<string>? Args, int? TimeoutMs, string? Error) Plan(string verb, string mode = "replace", string? strategy = null, string? value = null,
        string? type = null, string? scope = null, int? row = null, string? rowKind = null, int? depth = null, int? nodes = null, int? duration = null) =>
        ((List<string>?, int?, string?))typeof(UiAutomationTools).GetMethod("SelectionArgs", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object?[] { verb, "0x123", mode, strategy, value, type, scope, row, rowKind, depth, nodes, duration })!;

    [Fact]
    public void McpPlanningForwardsExactSelectionSemanticsAndProcessHeadroom()
    {
        var item = Plan("select-item", strategy: "Name", value: "--Item", type: "ListItem", scope: "0x456", nodes: 100000, duration: 120000);
        Assert.Null(item.Error); Assert.Equal(130000, item.TimeoutMs);
        Assert.Equal(new[] { "select-item", "--hwnd", "0x123", "--mode", "replace", "--strategy", "Name", "--value=--Item", "--controlType", "ListItem",
            "--scopeHwnd", "0x456", "--maxNodes", "100000", "--maxDurationMs", "120000" }, item.Args);
        var row = Plan("select-row", row: int.MaxValue, rowKind: "logical", depth: 64);
        Assert.Null(row.Error); Assert.Equal(20000, row.TimeoutMs);
        Assert.Equal(new[] { "select-row", "--hwnd", "0x123", "--mode", "replace", "--row", "2147483647", "--rowKind", "logical", "--maxDepth", "64" }, row.Args);
    }

    [Theory]
    [InlineData(nameof(UiAutomationTools.SelectItem), "ui_select_item", "hwnd,strategy,value,mode")]
    [InlineData(nameof(UiAutomationTools.SelectRow), "ui_select_row", "hwnd,row,rowKind,mode")]
    public void McpMetadataAccuratelyRequiresMutationPermissionAndExplicitSemantics(string methodName, string name, string required)
    {
        var method = typeof(UiAutomationTools).GetMethod(methodName)!; var tool = method.GetCustomAttribute<McpServerToolAttribute>()!;
        Assert.Equal(name, tool.Name); Assert.False(tool.ReadOnly); Assert.True(tool.Destructive); Assert.True(tool.Idempotent); Assert.False(tool.OpenWorld);
        Assert.Equal(required.Split(','), method.GetParameters().Where(parameter => !parameter.IsOptional).Select(parameter => parameter.Name));
        var description = method.GetCustomAttribute<DescriptionAttribute>()!.Description;
        Assert.Contains("at most one", description); Assert.Contains("may deselect", description); Assert.Contains("partialMutationPossible", description);
    }

    [Fact]
    public async Task McpInvalidRequestsFailBeforeLaunchingAMissingCli()
    {
        var tools = new UiAutomationTools(new CliRunner(NullLogger.Instance, "missing", "missing"));
        foreach (var result in new[] { await tools.SelectItem("0x123", "Name", "Item", "add"), await tools.SelectRow("0x123", -1, "logical", "replace"),
            await tools.SelectRow("0x123", 0, "auto", "replace"), await tools.SelectItem("0x123", "Name", "Item", "replace", maxNodes: 0) })
        {
            Assert.Equal(1, result.ExitCode); Assert.Null(result.WrapperErrorCode);
            Assert.Equal("invalid-argument", result.Result!.Value.GetProperty("error").GetString());
            Assert.False(result.Result.Value.GetProperty("actionAttempted").GetBoolean());
        }
    }

    [Fact]
    public async Task McpPublicMethodsForwardToRealCliValidationWithoutLiveTargets()
    {
        var tools = new UiAutomationTools(new CliRunner(NullLogger.Instance, AppContext.BaseDirectory, AppContext.BaseDirectory));
        foreach (var result in new[] { await tools.SelectItem("not-a-window", "Name", "--Item", "replace", maxNodes: 20),
            await tools.SelectRow("not-a-window", 1, "logical", "replace", maxDepth: 4, maxDurationMs: 10000) })
        {
            Assert.Equal(1, result.ExitCode); Assert.Null(result.WrapperErrorCode);
            Assert.Contains("Invalid hwnd", result.Result!.Value.GetProperty("message").GetString());
            Assert.False(result.Result.Value.GetProperty("actionAttempted").GetBoolean());
        }
    }

    [Fact]
    public async Task CapabilityInfoAdvertisesVerifiedSelectionWithoutClaimingNativeOrFocusSupport()
    {
        var result = await ExecutableIdentityTests.Run("ui", "info");
        var capabilities = result.Json.GetProperty("capabilities").EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Contains("ui.select-item.verified.v1", capabilities); Assert.Contains("ui.select-row.verified.v1", capabilities);
        Assert.DoesNotContain(capabilities, value => value!.Contains("native-memory") || value.Contains("focus-preservation"));
    }
}