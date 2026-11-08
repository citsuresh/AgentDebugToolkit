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
    public void UiSendKeys_SelectorIsOptionalForWindowLevelInput()
    {
        var method = typeof(UiAutomationTools).GetMethod(nameof(UiAutomationTools.SendKeys));
        Assert.NotNull(method);

        var parameters = method.GetParameters();
        Assert.False(parameters.Single(parameter => parameter.Name == "hwnd").IsOptional);
        Assert.False(parameters.Single(parameter => parameter.Name == "keys").IsOptional);
        Assert.True(parameters.Single(parameter => parameter.Name == "strategy").IsOptional);
        Assert.True(parameters.Single(parameter => parameter.Name == "value").IsOptional);

        var toolMetadata = method.GetCustomAttribute<McpServerToolAttribute>();
        Assert.NotNull(toolMetadata);
        Assert.False(toolMetadata.ReadOnly);
        var description = parameters.Single(parameter => parameter.Name == "strategy")
            .GetCustomAttribute<DescriptionAttribute>();
        Assert.NotNull(description);
        Assert.Contains("current focus", description.Description);
    }

    [Fact]
    public void UiScroll_IsDestructiveWithRequiredHwndAndDirection()
    {
        var method = typeof(UiAutomationTools).GetMethod(nameof(UiAutomationTools.Scroll));
        Assert.NotNull(method);

        var toolMetadata = method.GetCustomAttribute<McpServerToolAttribute>();
        Assert.NotNull(toolMetadata);
        Assert.Equal("ui_scroll", toolMetadata.Name);
        Assert.False(toolMetadata.ReadOnly);
        Assert.True(toolMetadata.Destructive);

        var parameters = method.GetParameters();
        Assert.False(parameters.Single(parameter => parameter.Name == "hwnd").IsOptional);
        Assert.False(parameters.Single(parameter => parameter.Name == "direction").IsOptional);
        Assert.Equal(typeof(int?), parameters.Single(parameter => parameter.Name == "clicks").ParameterType);
        Assert.Equal(typeof(bool?), parameters.Single(parameter => parameter.Name == "allowOutsideWindow").ParameterType);
        Assert.Contains("moves the real cursor", method.GetCustomAttribute<DescriptionAttribute>()!.Description);
    }

    [Fact]
    public void UiPasteText_IsDestructiveAndRequiresHwndAndText()
    {
        var method = typeof(UiAutomationTools).GetMethod(nameof(UiAutomationTools.PasteText));
        Assert.NotNull(method);

        var toolMetadata = method.GetCustomAttribute<McpServerToolAttribute>();
        Assert.NotNull(toolMetadata);
        Assert.Equal("ui_paste_text", toolMetadata.Name);
        Assert.False(toolMetadata.ReadOnly);
        Assert.True(toolMetadata.Destructive);

        var parameters = method.GetParameters();
        Assert.False(parameters.Single(parameter => parameter.Name == "hwnd").IsOptional);
        Assert.False(parameters.Single(parameter => parameter.Name == "text").IsOptional);
        var restoreDelay = parameters.Single(parameter => parameter.Name == "restoreDelayMs");
        Assert.Equal(typeof(int?), restoreDelay.ParameterType);
        Assert.True(restoreDelay.IsOptional);
        Assert.Contains("clipboardSnapshotComplete",
            restoreDelay.GetCustomAttribute<DescriptionAttribute>()!.Description);
        Assert.Contains("multi-line", method.GetCustomAttribute<DescriptionAttribute>()!.Description);
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
