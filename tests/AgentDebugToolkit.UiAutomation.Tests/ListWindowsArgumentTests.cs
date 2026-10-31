using AgentDebugToolkit.UiAutomation.Tests.Infrastructure;
using Xunit;

namespace AgentDebugToolkit.UiAutomation.Tests;

public sealed class ListWindowsArgumentTests
{
    [Fact]
    public void ListWindows_UnknownFlagNamesFlagAndAcceptedOptions()
    {
        var result = UiCli.Run("list-windows", "--proccessName", "ExampleApp");

        Assert.False(result.Success, result.RawOutput);
        Assert.Equal("invalid-argument", result.ErrorCode);
        AssertListsFlagError(result.RawOutput, "--proccessName");
    }

    [Fact]
    public void ListWindows_MissingValueNamesFlagAndAcceptedOptions()
    {
        var result = UiCli.Run("list-windows", "--titleContains", "--all");

        Assert.False(result.Success, result.RawOutput);
        Assert.Equal("invalid-argument", result.ErrorCode);
        AssertListsFlagError(result.RawOutput, "--titleContains");
    }

    private static void AssertListsFlagError(string output, string flag)
    {
        Assert.Contains(flag, output);
        Assert.Contains("--pid", output);
        Assert.Contains("--all", output);
        Assert.Contains("--processName", output);
        Assert.Contains("--titleContains", output);
        Assert.Contains("--className", output);
        Assert.Contains("--limit", output);
        Assert.Contains("--includeUntitled", output);
    }
}
