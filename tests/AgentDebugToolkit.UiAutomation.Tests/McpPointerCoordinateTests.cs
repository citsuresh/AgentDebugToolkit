using System.Globalization;
using AgentDebugToolkit.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentDebugToolkit.UiAutomation.Tests;

public sealed class McpPointerCoordinateTests
{
    [Theory]
    [InlineData(nameof(UiAutomationTools.Click), false)]
    [InlineData(nameof(UiAutomationTools.RightClick), false)]
    [InlineData(nameof(UiAutomationTools.DoubleClick), false)]
    [InlineData(nameof(UiAutomationTools.Click), true)]
    [InlineData(nameof(UiAutomationTools.RightClick), true)]
    [InlineData(nameof(UiAutomationTools.DoubleClick), true)]
    [InlineData(nameof(UiAutomationTools.Click), null)]
    [InlineData(nameof(UiAutomationTools.RightClick), null)]
    [InlineData(nameof(UiAutomationTools.DoubleClick), null)]
    public async Task PointerTools_PreserveOutsideWindowFlagForSelectorValidation(
        string methodName, bool? allowOutsideWindow)
    {
        var cli = new CliRunner(NullLogger.Instance, AppContext.BaseDirectory, AppContext.BaseDirectory);
        var tools = new UiAutomationTools(cli);
        var invocation = methodName switch
        {
            nameof(UiAutomationTools.Click) => tools.Click(
                "not-a-window", strategy: "Name", value: "Target", allowOutsideWindow: allowOutsideWindow),
            nameof(UiAutomationTools.RightClick) => tools.RightClick(
                "not-a-window", strategy: "Name", value: "Target", allowOutsideWindow: allowOutsideWindow),
            nameof(UiAutomationTools.DoubleClick) => tools.DoubleClick(
                "not-a-window", strategy: "Name", value: "Target", allowOutsideWindow: allowOutsideWindow),
            _ => throw new ArgumentOutOfRangeException(nameof(methodName)),
        };

        var result = await invocation;

        Assert.Null(result.WrapperErrorCode);
        Assert.NotNull(result.Result);
        Assert.False(result.Result.Value.GetProperty("success").GetBoolean());
        Assert.Equal("invalid-argument", result.Result.Value.GetProperty("error").GetString());
        Assert.Contains(allowOutsideWindow.HasValue
                ? "--allowOutsideWindow is only valid with the coordinate form."
                : "Invalid hwnd",
            result.Result.Value.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData(nameof(UiAutomationTools.Click))]
    [InlineData(nameof(UiAutomationTools.RightClick))]
    [InlineData(nameof(UiAutomationTools.DoubleClick))]
    public async Task PointerTools_ForwardNegativeCoordinatesInvariantly(string methodName)
    {
        var cli = new CliRunner(NullLogger.Instance, AppContext.BaseDirectory, AppContext.BaseDirectory);
        var tools = new UiAutomationTools(cli);
        var originalCulture = CultureInfo.CurrentCulture;
        var customCulture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        customCulture.NumberFormat.NegativeSign = "~";
        CultureInfo.CurrentCulture = customCulture;

        try
        {
            var invocation = methodName switch
            {
                nameof(UiAutomationTools.Click) => tools.Click(
                    "not-a-window", x: -11, y: 20, allowOutsideWindow: true),
                nameof(UiAutomationTools.RightClick) => tools.RightClick(
                    "not-a-window", x: -11, y: 20, allowOutsideWindow: true),
                nameof(UiAutomationTools.DoubleClick) => tools.DoubleClick(
                    "not-a-window", x: -11, y: 20, allowOutsideWindow: true),
                _ => throw new ArgumentOutOfRangeException(nameof(methodName)),
            };

            var result = await invocation;

            Assert.Null(result.WrapperErrorCode);
            Assert.NotNull(result.Result);
            Assert.Equal("invalid-argument", result.Result.Value.GetProperty("error").GetString());
            Assert.Contains("Invalid hwnd", result.Result.Value.GetProperty("message").GetString());
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }
}
