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
}
