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

    private static UIElement BuildChains(int chains)
    {
        // Each chain is six nested GroupBoxes around a TextBlock, which has no UIA children, so the
        // only nodes with children at any limit are the chain GroupBoxes (one per chain).
        var panel = new StackPanel();
        for (var chain = 0; chain < chains; chain++)
        {
            UIElement content = new TextBlock { Text = $"chain{chain}" };
            for (var level = 6; level >= 1; level--)
            {
                content = new GroupBox { Header = $"c{chain}l{level}", Content = content };
            }

            panel.Children.Add(content);
        }

        return panel;
    }

    private static List<int> NodesBeyondDepthByLimit(Func<UIElement> content, int maxLimit)
    {
        using var window = FixtureWindow.Create(content);
        var counts = new List<int>();
        for (var depth = 1; depth <= maxLimit; depth++)
        {
            var result = UiCli.Run("inspect", "--hwnd", window.HwndArgument, "--maxDepth", depth.ToString());
            Assert.True(result.Success, result.RawOutput);
            counts.Add(result.Json.TryGetProperty("nodesBeyondDepth", out var count) ? count.GetInt32() : 0);
        }

        return counts;
    }

    [Fact]
    public void NodesBeyondDepth_CountsBoundaryNodesOnly_NotDescendantsOrDoubleCounts()
    {
        // The window frame has its own nodes with children (title bar and so on), so measure the
        // difference against the same window with empty content.
        var baseline = NodesBeyondDepthByLimit(() => BuildChains(0), 14);
        var twoChains = NodesBeyondDepthByLimit(() => BuildChains(2), 14);

        var extra = twoChains.Zip(baseline, (chains, empty) => chains - empty).ToList();
        // The two chains add at most two boundary parents at any limit (one per chain), reach two
        // where the limit cuts both of them, and nothing once the limit is deep enough.
        Assert.All(extra, difference => Assert.InRange(difference, 0, 2));
        Assert.Equal(2, extra.Max());
        Assert.Equal(0, extra[^1]);
    }

    private static int NodeCount(CliResult result) =>
        System.Text.RegularExpressions.Regex.Matches(result.RawOutput, System.Text.RegularExpressions.Regex.Escape("ControlType.")).Count;

    [Fact]
    public void Boundary_EachLevelAddsNodes_UntilTheDepthThatShowsEverything()
    {
        using var window = FixtureWindow.Create(BuildNested);
        var runs = new List<CliResult>();
        int? firstUnlimited = null;
        for (var depth = 1; depth <= 20 && firstUnlimited is null; depth++)
        {
            var result = UiCli.Run("inspect", "--hwnd", window.HwndArgument, "--maxDepth", depth.ToString());
            runs.Add(result);
            if (!result.Json.TryGetProperty("depthLimited", out _))
            {
                firstUnlimited = depth;
            }
        }

        Assert.NotNull(firstUnlimited);
        var unlimited = runs[^1];
        Assert.Contains(LeafText, unlimited.RawOutput);

        // Every limited run reports exactly the limit it used.
        for (var i = 0; i < runs.Count - 1; i++)
        {
            Assert.True(runs[i].Json.GetProperty("depthLimited").GetBoolean());
            Assert.Equal(i + 1, runs[i].Json.GetProperty("maxDepth").GetInt32());
        }

        // One level short shows strictly fewer nodes than the unlimited run (so the flag is not
        // raised for a result that is already complete, nor missing for one that is cut), and a
        // deeper limit adds nothing (so the "unlimited" run really showed everything).
        var oneLess = runs[^2];
        Assert.True(NodeCount(oneLess) < NodeCount(unlimited), "The limited run showed as many nodes as the complete one.");
        var deeper = UiCli.Run("inspect", "--hwnd", window.HwndArgument, "--maxDepth", (firstUnlimited!.Value + 2).ToString());
        Assert.Equal(NodeCount(unlimited), NodeCount(deeper));
        Assert.False(deeper.Json.TryGetProperty("depthLimited", out _));

        var textOneLess = UiCli.Run("read-visible-text", "--hwnd", window.HwndArgument, "--maxDepth", (firstUnlimited.Value - 1).ToString());
        Assert.True(textOneLess.Json.GetProperty("depthLimited").GetBoolean());
        Assert.Equal(firstUnlimited.Value - 1, textOneLess.Json.GetProperty("maxDepth").GetInt32());
        var textUnlimited = UiCli.Run("read-visible-text", "--hwnd", window.HwndArgument, "--maxDepth", firstUnlimited.Value.ToString());
        Assert.False(textUnlimited.Json.TryGetProperty("depthLimited", out _));
    }

    [Fact]
    public void Hint_SuggestsADepthDeeperThanTheOneUsed_AndMarksCappedCounts()
    {
        var stats = new AgentDebugToolkit.UiAutomation.Cli.DepthLimitStats { NodesBeyondDepth = 3 };
        Assert.Contains("(for example 30)", stats.Hint(3));
        Assert.Contains("(for example 60)", stats.Hint(30));
        Assert.Contains("(for example 80)", stats.Hint(40));
        Assert.DoesNotContain("at least", stats.Hint(3));

        stats.Capped = true;
        Assert.Contains("at least 3 node(s)", stats.Hint(3));
    }
}
