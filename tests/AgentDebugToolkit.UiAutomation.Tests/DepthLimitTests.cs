using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AgentDebugToolkit.UiAutomation.Tests.Infrastructure;
using Xunit;

namespace AgentDebugToolkit.UiAutomation.Tests;

/// <summary>
/// A deterministic fixture nested well past a small --maxDepth: six GroupBoxes (each a UIA Group)
/// around a Button, a TextBox (the only Edit in the window) and a TextBlock. No input is sent; only read-only inspect/read-visible-text run.
/// </summary>
public sealed class DepthLimitTests
{
    private const string LeafButton = "DeepLeafButton";
    private const string LeafEdit = "DeepLeafEdit";
    private const string LeafText = "DeepLeafText";

    private static UIElement BuildNested()
    {
        UIElement content = new StackPanel
        {
            Children = { new Button { Content = LeafButton }, new TextBox { Text = LeafEdit }, new TextBlock { Text = LeafText } },
        };
        for (var level = 6; level >= 1; level--)
        {
            content = new GroupBox { Header = $"level{level}", Content = content };
        }

        return content;
    }

    private static void AssertNotDepthLimited(JsonElement json)
    {
        Assert.False(json.TryGetProperty("depthLimited", out _));
        Assert.False(json.TryGetProperty("maxDepth", out _));
        Assert.False(json.TryGetProperty("nodesBeyondDepth", out _));
        Assert.False(json.TryGetProperty("hint", out _));
    }

    private static void AssertDepthLimited(JsonElement json, int maxDepth)
    {
        Assert.True(json.GetProperty("depthLimited").GetBoolean());
        Assert.Equal(maxDepth, json.GetProperty("maxDepth").GetInt32());
        Assert.True(json.GetProperty("nodesBeyondDepth").GetInt32() > 0);
        var hint = json.GetProperty("hint").GetString()!;
        Assert.Contains($"--maxDepth {maxDepth}", hint);
        Assert.Contains("Electron/Chromium", hint);
    }

    [Fact]
    public void Inspect_ShallowLimit_ReportsDepthLimited_AndDeepLimit_DoesNot()
    {
        using var window = FixtureWindow.Create(BuildNested);

        var shallow = UiCli.Run("inspect", "--hwnd", window.HwndArgument, "--maxDepth", "3");
        Assert.True(shallow.Success, shallow.RawOutput);
        AssertDepthLimited(shallow.Json, 3);
        Assert.False(shallow.Json.TryGetProperty("matchCount", out _));

        var deep = UiCli.Run("inspect", "--hwnd", window.HwndArgument, "--maxDepth", "40");
        Assert.True(deep.Success, deep.RawOutput);
        AssertNotDepthLimited(deep.Json);
        Assert.Contains(LeafButton, deep.RawOutput);
    }

    [Fact]
    public void Inspect_ExistingFieldsAreUnchanged()
    {
        using var window = FixtureWindow.Create(BuildNested);

        var result = UiCli.Run("inspect", "--hwnd", window.HwndArgument, "--maxDepth", "3");

        var root = result.Json.GetProperty("root");
        foreach (var field in new[] { "controlType", "className", "isEnabled", "isOffscreen", "boundingRect", "supportedPatterns", "children" })
        {
            Assert.Contains(root.EnumerateObject(), property => string.Equals(property.Name, field, StringComparison.OrdinalIgnoreCase));
        }
        Assert.True(result.Json.GetProperty("success").GetBoolean());
        Assert.False(result.Json.TryGetProperty("screenshotPath", out _));
    }

    [Fact]
    public void FilteredInspect_NoMatchOnlyBecauseOfDepth_StillReportsHint()
    {
        using var window = FixtureWindow.Create(BuildNested);

        var byName = UiCli.Run("inspect", "--hwnd", window.HwndArgument, "--maxDepth", "3", "--nameContains", LeafButton);
        Assert.True(byName.Success, byName.RawOutput);
        Assert.Equal(0, byName.Json.GetProperty("matchCount").GetInt32());
        AssertDepthLimited(byName.Json, 3);

        var byType = UiCli.Run("inspect", "--hwnd", window.HwndArgument, "--maxDepth", "3", "--controlType", "Edit");
        Assert.True(byType.Success, byType.RawOutput);
        Assert.Equal(0, byType.Json.GetProperty("matchCount").GetInt32());
        AssertDepthLimited(byType.Json, 3);

        var found = UiCli.Run("inspect", "--hwnd", window.HwndArgument, "--maxDepth", "40", "--nameContains", LeafButton);
        Assert.True(found.Success, found.RawOutput);
        Assert.True(found.Json.GetProperty("matchCount").GetInt32() >= 1);
        AssertNotDepthLimited(found.Json);
    }

    [Fact]
    public void ReadVisibleText_ShallowLimit_ReportsDepthLimited_SeparateFromTruncated()
    {
        using var window = FixtureWindow.Create(BuildNested);

        var shallow = UiCli.Run("read-visible-text", "--hwnd", window.HwndArgument, "--maxDepth", "3");
        Assert.True(shallow.Success, shallow.RawOutput);
        AssertDepthLimited(shallow.Json, 3);
        Assert.False(shallow.Json.GetProperty("truncated").GetBoolean());
        Assert.DoesNotContain(LeafText, shallow.RawOutput);

        var deep = UiCli.Run("read-visible-text", "--hwnd", window.HwndArgument, "--maxDepth", "40");
        Assert.True(deep.Success, deep.RawOutput);
        AssertNotDepthLimited(deep.Json);
        Assert.False(deep.Json.GetProperty("truncated").GetBoolean());
        Assert.Contains(LeafText, deep.Json.GetProperty("lines").EnumerateArray().Select(line => line.GetString()));
    }
}
