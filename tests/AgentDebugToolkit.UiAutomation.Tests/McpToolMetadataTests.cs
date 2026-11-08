using System.Reflection;
using AgentDebugToolkit.Mcp;
using ModelContextProtocol.Server;
using Xunit;

namespace AgentDebugToolkit.UiAutomation.Tests;

public sealed class McpToolMetadataTests
{
    [Fact]
    public void UiInspect_IsReadOnlyAndDoesNotExposeScreenshot()
    {
        var inspectMethod = typeof(UiAutomationTools).GetMethod(nameof(UiAutomationTools.Inspect));
        Assert.NotNull(inspectMethod);

        var toolMetadata = inspectMethod.GetCustomAttribute<McpServerToolAttribute>();
        Assert.NotNull(toolMetadata);
        Assert.True(toolMetadata.ReadOnly);
        Assert.DoesNotContain(inspectMethod.GetParameters(), parameter => parameter.Name == "screenshot");
    }

    [Theory]
    [InlineData(nameof(UiAutomationTools.Click))]
    [InlineData(nameof(UiAutomationTools.RightClick))]
    [InlineData(nameof(UiAutomationTools.DoubleClick))]
    public void PointerTools_ExposeOptionalSelectorOrCoordinateTargets(string methodName)
    {
        var method = typeof(UiAutomationTools).GetMethod(methodName);
        Assert.NotNull(method);

        var parameters = method.GetParameters();
        Assert.True(parameters.Single(parameter => parameter.Name == "strategy").IsOptional);
        Assert.True(parameters.Single(parameter => parameter.Name == "value").IsOptional);

        var x = parameters.Single(parameter => parameter.Name == "x");
        Assert.Equal(typeof(int?), x.ParameterType);
        Assert.True(x.IsOptional);

        var y = parameters.Single(parameter => parameter.Name == "y");
        Assert.Equal(typeof(int?), y.ParameterType);
        Assert.True(y.IsOptional);

        var allowOutsideWindow = parameters.Single(parameter => parameter.Name == "allowOutsideWindow");
        Assert.Equal(typeof(bool?), allowOutsideWindow.ParameterType);
        Assert.True(allowOutsideWindow.IsOptional);
    }
}
