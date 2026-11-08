using System.ComponentModel;
using System.Reflection;
using AgentDebugToolkit.Mcp;
using ModelContextProtocol.Server;
using Xunit;

namespace AgentDebugToolkit.UiAutomation.Tests;

public sealed class McpToolMetadataTests
{
    [Fact]
    public void UiScreenshot_DescribesPhysicalGeometryAndOptionalTransforms()
    {
        var method = typeof(UiAutomationTools).GetMethod(nameof(UiAutomationTools.Screenshot));
        Assert.NotNull(method);
        var description = method.GetCustomAttribute<DescriptionAttribute>();
        Assert.NotNull(description);
        Assert.Contains("screenshotPath", description.Description);
        Assert.Contains("windowRect (left, top, right, bottom in physical screen pixels)", description.Description);
        Assert.Contains("imageWidth", description.Description);
        Assert.Contains("imageHeight", description.Description);
        Assert.Contains("dpiScale (GetDpiForWindow / 96)", description.Description);
        Assert.Contains("region.x + imagePixelX / scale", description.Description);
        var parameters = method.GetParameters();
        Assert.Equal(new[] { "hwnd", "cancellationToken", "region", "scale" }, parameters.Select(parameter => parameter.Name));
        Assert.Equal(typeof(string), parameters[2].ParameterType);
        Assert.True(parameters[2].IsOptional);
        Assert.Equal(typeof(double?), parameters[3].ParameterType);
        Assert.True(parameters[3].IsOptional);
    }

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
