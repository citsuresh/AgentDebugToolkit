using System.Windows.Controls;
using AgentDebugToolkit.UiAutomation.Tests.Infrastructure;
using Xunit;

namespace AgentDebugToolkit.UiAutomation.Tests;

public sealed class FixtureSmokeTests
{
    [Fact]
    public void FindAll_FindsElementInFixtureWindow()
    {
        using var window = FixtureWindow.Create(() => new StackPanel
        {
            Children = { new TextBlock { Text = "Smoke label" } },
        });

        var result = UiCli.Run("find-all", "--hwnd", window.HwndArgument, "--strategy", "Name", "--value", "Smoke label");

        Assert.True(result.Success, result.RawOutput);
        Assert.Equal(1, result.Json.GetProperty("count").GetInt32());
        Assert.Equal("ControlType.Text",
            result.Json.GetProperty("elements")[0].GetProperty("controlType").GetString());
    }
}
