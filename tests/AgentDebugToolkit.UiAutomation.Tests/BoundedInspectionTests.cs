using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;
using AgentDebugToolkit.Core;
using AgentDebugToolkit.Core.Models;
using AgentDebugToolkit.Inspection;
using AgentDebugToolkit.UiAutomation.Cli;
using Xunit;

namespace AgentDebugToolkit.UiAutomation.Tests;

public sealed class BoundedInspectionTests
{
    private sealed class Node(string name)
    {
        internal string Name { get; } = name;
        internal List<Node> Children { get; } = new();
        internal Node? Parent { get; private set; }
        internal Node Add(string name) { var child = new Node(name) { Parent = this }; Children.Add(child); return child; }
    }

    private static Node? First(Node node) => node.Children.FirstOrDefault();
    private static Node? Next(Node node) => node.Parent is { } parent && parent.Children.IndexOf(node) + 1 < parent.Children.Count
        ? parent.Children[parent.Children.IndexOf(node) + 1] : null;
    private static JsonElement Report(InspectionTraversal traversal) => JsonSerializer.SerializeToElement(traversal.Report, JsonOutput.Options);
    private static ElementInfo? Tree(Node root, InspectionTraversal traversal, int depth = 8, Func<Node, ElementInfo>? read = null,
        Func<Node, Node?>? first = null, DepthLimitStats? stats = null) => traversal.Walk(root, depth, stats ?? new(),
            read ?? (node => new ElementInfo { Name = node.Name }), (value, parent) => parent?.Children.Add(value), first ?? First, Next);

    [Theory]
    [InlineData(1, 0, true)]
    [InlineData(2, 1, true)]
    [InlineData(3, 2, false)]
    [InlineData(4, 2, false)]
    public void NodeBudget_CountsRootAndStopsExactlyWithoutInventingAnOmission(int budget, int children, bool truncated)
    {
        var root = new Node("root"); root.Add("one"); root.Add("two");
        var snapshots = 0;
        var traversal = new InspectionTraversal(new(budget, null), () => 0);
        var tree = Tree(root, traversal, read: node => { snapshots++; return new() { Name = node.Name }; });
        Assert.NotNull(tree);
        Assert.Equal(children, tree.Children.Count);
        Assert.Equal(children + 1, snapshots);
        Assert.Equal(snapshots, traversal.NodesVisited);
        Assert.Equal(snapshots, traversal.NodesCaptured);
        Assert.Equal(truncated, traversal.Truncated);
        Assert.Equal(!truncated, traversal.Complete);
        Assert.Equal(truncated ? new[] { "node-budget" } : Array.Empty<string>(), Report(traversal).GetProperty("stopReasons").EnumerateArray().Select(reason => reason.GetString()));
    }

    [Fact]
    public void TimeOverrun_DiscardsPartialSnapshotAndNeverStartsTheNextProviderRead()
    {
        long clock = 0;
        var calls = 0;
        var traversal = new InspectionTraversal(new(null, 5), () => clock);
        var tree = Tree(new("root"), traversal, read: node =>
        {
            traversal.Call(() => { calls++; clock = 50; return node.Name; });
            throw new Exception("A second read after the deadline is forbidden");
        });
        Assert.Null(tree);
        Assert.Equal(1, calls);
        Assert.Equal(1, traversal.NodesVisited);
        Assert.Equal(0, traversal.NodesCaptured);
        var report = Report(traversal);
        Assert.Equal(50, report.GetProperty("elapsedMs").GetInt64());
        Assert.False(report.GetProperty("complete").GetBoolean());
        Assert.Equal("time-budget", Assert.Single(report.GetProperty("stopReasons").EnumerateArray()).GetString());

        using var output = new StringWriter();
        var original = Console.Out;
        try { Console.SetOut(output); Assert.Equal(1, Verbs.WriteIncompleteInspection(traversal.Report)); }
        finally { Console.SetOut(original); }
        using var json = JsonDocument.Parse(output.ToString());
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("inspection-incomplete", json.RootElement.GetProperty("error").GetString());
        Assert.Equal(0, json.RootElement.GetProperty("traversal").GetProperty("nodesCaptured").GetInt32());
    }

    [Fact]
    public void AlreadyExpiredBudget_DoesNotStartAnyProviderCall()
    {
        var traversal = new InspectionTraversal(new(10, 1), () => 1);
        Assert.Null(Tree(new("root"), traversal, read: _ => throw new Exception("No read allowed"), first: _ => throw new Exception("No navigation allowed")));
        Assert.Equal(0, traversal.NodesVisited);
        Assert.False(traversal.Complete);
    }

    [Fact]
    public void FailingProviderThatOverrunsTime_ReportsBothFactsAndStopsReading()
    {
        long clock = 0;
        var traversal = new InspectionTraversal(new(10, 5), () => clock);
        Assert.Null(Tree(new("root"), traversal, read: _ => { clock = 50; throw new COMException("provider failed after deadline"); },
            first: _ => throw new Exception("No navigation allowed")));
        var report = Report(traversal);
        Assert.Equal(new[] { "provider-error", "time-budget" }, report.GetProperty("stopReasons").EnumerateArray().Select(reason => reason.GetString()));
        Assert.Equal(1, report.GetProperty("errorCount").GetInt32());
        Assert.Equal(50, report.GetProperty("elapsedMs").GetInt64());
    }

    [Fact]
    public void TraversalElapsedTime_FreezesBeforePostProcessing()
    {
        long clock = 0;
        var traversal = new InspectionTraversal(new(10, 100), () => clock);
        Tree(new("root"), traversal, read: node => { clock = 20; return new() { Name = node.Name }; });
        clock = 1000;
        Assert.Equal(20, Report(traversal).GetProperty("elapsedMs").GetInt64());
        Assert.True(traversal.Complete);
    }

    [Theory]
    [InlineData(false, "node-budget")]
    [InlineData(true, "time-budget")]
    public void CombinedBudgets_StopAtTheFirstExhaustedBound(bool advanceClock, string reason)
    {
        long clock = 0;
        var root = new Node("root"); root.Add("child");
        var traversal = new InspectionTraversal(new(1, 5), () => clock);
        Tree(root, traversal, first: node => { if (advanceClock) clock = 5; return First(node); });
        Assert.Equal(1, traversal.NodesCaptured);
        Assert.Equal(reason, Assert.Single(Report(traversal).GetProperty("stopReasons").EnumerateArray()).GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DepthZero_DistinguishesLeavesFromHiddenChildren(bool hasChild)
    {
        var root = new Node("root"); if (hasChild) root.Add("hidden");
        var stats = new DepthLimitStats();
        var traversal = new InspectionTraversal(new(10, null), () => 0);
        Assert.Empty(Tree(root, traversal, depth: 0, stats: stats)!.Children);
        Assert.Equal(hasChild ? 1 : 0, stats.NodesBeyondDepth);
        Assert.Equal(!hasChild, traversal.Complete);
        Assert.Equal(hasChild, traversal.Truncated);
    }

    [Fact]
    public void ProbeCeiling_LeavesCompletenessUnknownRatherThanClaimingAnOpaqueTree()
    {
        var root = new Node("root");
        for (var i = 0; i < 301; i++) root.Add(i.ToString());
        var stats = new DepthLimitStats();
        var probes = 0;
        var traversal = new InspectionTraversal(new(1000, null), () => 0);
        Tree(root, traversal, 1, first: node => { if (node != root) probes++; return First(node); }, stats: stats);
        Assert.Equal(300, probes);
        Assert.True(stats.Capped);
        Assert.False(stats.IsLimited);
        Assert.False(traversal.Complete);
        Assert.Contains("depth-probe-limit", Report(traversal).GetProperty("stopReasons").EnumerateArray().Select(reason => reason.GetString()));
    }

    [Theory]
    [InlineData("node")]
    [InlineData("children")]
    [InlineData("depth-probe")]
    public void ProviderFailures_AreExplicitAndNeverReportedComplete(string stage)
    {
        var root = new Node("root"); root.Add("bad"); root.Add("good");
        var traversal = new InspectionTraversal(new(10, null), () => 0);
        var tree = Tree(root, traversal, stage == "depth-probe" ? 0 : 8,
            read: node => node.Name == "bad" && stage == "node" ? throw new COMException("provider failed") : new() { Name = node.Name },
            first: node => stage is "children" or "depth-probe" && node == root ? throw new COMException("provider failed") : First(node));
        Assert.NotNull(tree);
        if (stage == "node") Assert.Equal(new[] { "good" }, tree.Children.Select(child => child.Name));
        var report = Report(traversal);
        Assert.False(report.GetProperty("complete").GetBoolean());
        Assert.False(report.GetProperty("truncated").GetBoolean());
        Assert.Equal(stage, Assert.Single(report.GetProperty("errors").EnumerateArray()).GetProperty("stage").GetString());
        Assert.Equal(1, report.GetProperty("errorCount").GetInt32());
    }

    [Fact]
    public void GoneRoot_IsNotAnEmptySuccessfulTreeAndUnexpectedErrorsStillEscape()
    {
        var traversal = new InspectionTraversal(new(10, null), () => 0);
        Assert.Null(Tree(new("root"), traversal, read: _ => throw new ElementNotAvailableException("gone")));
        Assert.Equal("stale-context", Assert.Single(Report(traversal).GetProperty("errors").EnumerateArray()).GetProperty("error").GetString());
        var unexpected = new ApplicationException("not a provider failure");
        Assert.Same(unexpected, Assert.Throws<ApplicationException>(() => Tree(new("root"), new(new(1, null)), read: _ => throw unexpected)));
    }

    [Fact]
    public void FilteringIsPostTraversalAndDoesNotMakeAnIncompleteZeroCountExhaustive()
    {
        var root = new Node("root"); root.Add("other"); root.Add("needle");
        var traversal = new InspectionTraversal(new(2, null), () => 0);
        var tree = Tree(root, traversal)!;
        Assert.Equal(0, UiaHelper.PruneElementInfo(tree, "needle", null));
        Assert.Empty(tree.Children);
        Assert.Equal(2, traversal.NodesVisited);
        Assert.False(traversal.Complete);
        Assert.Contains("zero matches do not prove absence", Report(traversal).GetProperty("hint").GetString());
    }

    [Theory]
    [InlineData(200, false)]
    [InlineData(201, true)]
    public void TextTraversal_RetainsExactLegacyBreadthCapAndDeduplicates(int children, bool limited)
    {
        var root = new Node("root"); for (var i = 0; i < children; i++) root.Add("same");
        var traversal = new InspectionTraversal(new(1000, null), () => 0);
        var lines = UiaHelper.CollectBoundedText(root, 8, traversal, new(), node => new() { node.Name }, First, Next);
        Assert.Equal(new[] { "root", "same" }, lines);
        Assert.Equal(201, traversal.NodesCaptured);
        Assert.Equal(!limited, traversal.Complete);
        Assert.Equal(limited, traversal.Truncated);
    }

    [Theory]
    [InlineData(5000, false)]
    [InlineData(5001, true)]
    public void TextTraversal_EnforcesActual5000NodeSafetyCapWithoutRecursion(int count, bool limited)
    {
        var root = new Node("0"); var node = root;
        for (var i = 1; i < count; i++) node = node.Add(i.ToString());
        var traversal = new InspectionTraversal(new(100000, null), () => 0);
        var lines = UiaHelper.CollectBoundedText(root, 6000, traversal, new(), current => new() { current.Name }, First, Next);
        Assert.Equal(5000, lines.Count);
        Assert.Equal(5000, traversal.NodesVisited);
        Assert.Equal(!limited, traversal.Complete);
        Assert.Equal(5000, Report(traversal).GetProperty("legacyNodeLimit").GetInt32());
        if (limited) Assert.Contains("legacy-node-limit", Report(traversal).GetProperty("stopReasons").EnumerateArray().Select(reason => reason.GetString()));
    }

    [Theory]
    [InlineData(2000, false)]
    [InlineData(2001, true)]
    public void TextLength_UsesOneExtraCharacterToDistinguishTheActualBoundary(int length, bool limited)
    {
        var traversal = new InspectionTraversal(new(1, null), () => 0);
        var value = Assert.Single(traversal.ReadTextValues(("TextPattern", () => new string('x', length))));
        Assert.Equal(2000, value.Length);
        Assert.Equal(limited, traversal.Truncated);
    }

    [Fact]
    public void TextProviderFailure_PreservesAvailableFallbackAndReportsBoundedErrors()
    {
        var traversal = new InspectionTraversal(new(1, null), () => 0);
        Assert.Equal(new[] { "name", "value" }, traversal.ReadTextValues(("Name", () => "name"),
            ("TextPattern", () => throw new COMException("failed")), ("ValuePattern", () => "value")));
        for (var i = 0; i < 25; i++) traversal.NoteError("state", "inspection-provider-error", new string('x', 2000));
        var report = Report(traversal);
        Assert.Equal(26, report.GetProperty("errorCount").GetInt32());
        Assert.Equal(20, report.GetProperty("errors").GetArrayLength());
        Assert.True(report.GetProperty("errorsTruncated").GetBoolean());
        Assert.All(report.GetProperty("errors").EnumerateArray(), error => Assert.InRange(error.GetProperty("message").GetString()!.Length, 1, 1024));
        Assert.False(report.GetProperty("complete").GetBoolean());
    }

    [Fact]
    public void UnsupportedTextPatterns_AreNeitherErrorsNorInventedText()
    {
        var traversal = new InspectionTraversal(new(1, null), () => 0);
        Assert.Empty(traversal.ReadTextValues(("Name", () => ""), ("TextPattern", () => null), ("ValuePattern", () => null)));
        Assert.True(traversal.Complete);
        Assert.Equal(0, Report(traversal).GetProperty("errorCount").GetInt32());
    }

    [Fact]
    public void Report_HasExactAdditiveShapeAndRetainsElementFieldCasing()
    {
        var traversal = new InspectionTraversal(new(2, 100), () => 0);
        var tree = Tree(new("root"), traversal)!;
        var json = JsonSerializer.SerializeToElement(new { root = tree, traversal = traversal.Report, truncated = traversal.Truncated }, JsonOutput.Options);
        Assert.True(json.GetProperty("root").TryGetProperty("Name", out _));
        Assert.True(json.GetProperty("root").TryGetProperty("Children", out _));
        var report = json.GetProperty("traversal");
        Assert.Equal(new[] { "scope", "complete", "truncated", "nodesVisited", "nodesCaptured", "elapsedMs", "maxNodes", "maxDurationMs", "stopReasons", "errorCount", "errors", "errorsTruncated", "hint" }, report.EnumerateObject().Select(property => property.Name));
        Assert.Equal("exposed-uia-tree", report.GetProperty("scope").GetString());
        Assert.True(report.GetProperty("complete").GetBoolean());
        Assert.Equal(2, report.GetProperty("maxNodes").GetInt32());
        Assert.Equal(100, report.GetProperty("maxDurationMs").GetInt32());
    }

    [Theory]
    [InlineData("maxNodes", "0")]
    [InlineData("maxNodes", "-1")]
    [InlineData("maxNodes", "100001")]
    [InlineData("maxNodes", "2147483648")]
    [InlineData("maxNodes", "")]
    [InlineData("maxDurationMs", "0")]
    [InlineData("maxDurationMs", "-1")]
    [InlineData("maxDurationMs", "120001")]
    [InlineData("maxDurationMs", "1.5")]
    [InlineData("maxDurationMs", "no")]
    public void Validation_RejectsInvalidBudgetsBeforeAnyTargetLookup(string name, string value)
    {
        var options = new Dictionary<string, string> { [name] = value };
        Assert.False(InspectionLimits.TryParse(options, out _, out var error));
        Assert.Contains(name, error);
        foreach (var verb in new Func<Dictionary<string, string>, int>[] { Verbs.Inspect, Verbs.ReadVisibleText })
        {
            using var output = new StringWriter(); var original = Console.Out;
            try { Console.SetOut(output); Assert.Equal(1, verb(options)); }
            finally { Console.SetOut(original); }
            using var json = JsonDocument.Parse(output.ToString());
            Assert.Equal("invalid-argument", json.RootElement.GetProperty("error").GetString());
            Assert.Contains(name, json.RootElement.GetProperty("message").GetString());
        }
    }

    [Fact]
    public void OmittedLimitsPreserveLegacyModeAndBothPositiveLimitsCombine()
    {
        Assert.True(InspectionLimits.TryParse(new Dictionary<string, string>(), out var legacy, out _));
        Assert.False(legacy.IsBounded);
        Assert.Null(legacy.ProcessTimeoutMs);
        Assert.True(InspectionLimits.TryParse(new Dictionary<string, string> { ["maxNodes"] = "100000", ["maxDurationMs"] = "120000", ["maxDepth"] = "0" }, out var both, out _));
        Assert.Equal(100000, both.MaxNodes);
        Assert.Equal(130000, both.ProcessTimeoutMs);
    }

    [Theory]
    [InlineData("inspect", "--maxNodes=1", "--maxNodes=2")]
    [InlineData("read-visible-text", "--maxDurationMs=1", "--MAXDURATIONMS=1")]
    [InlineData("inspect", "--maxNodes", null)]
    [InlineData("read-visible-text", "--maxDurationMs", null)]
    [InlineData("inspect", "--maxNodes=1", "--maxDepth=-1")]
    [InlineData("inspect", "--maxNodes=1", "--maxDepth=bad")]
    [InlineData("get-text", "--maxNodes=1", null)]
    public async Task Cli_RejectsMissingConflictingAndMisappliedOptionsWithoutDesktopAccess(string verb, string first, string? second)
    {
        var args = new List<string> { verb, "--hwnd=not-a-window", first }; if (second is not null) args.Add(second);
        var result = await ExecutableIdentityTests.Run("ui", args.ToArray());
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("invalid-argument", result.Json.GetProperty("error").GetString());
        Assert.DoesNotContain("hwnd", result.Json.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecutableAdvertisesBothBoundedReadCapabilities()
    {
        var result = await ExecutableIdentityTests.Run("ui", "info");
        Assert.Equal(0, result.ExitCode);
        var capabilities = result.Json.GetProperty("capabilities").EnumerateArray().Select(value => value.GetString());
        Assert.Contains("ui.inspect.bounded.v1", capabilities);
        Assert.Contains("ui.read-visible-text.bounded.v1", capabilities);
    }

    [Theory]
    [InlineData("inspect")]
    [InlineData("read-visible-text")]
    public async Task ValidBudgetOptionsReachExistingHwndValidationWithoutDesktopAccess(string verb)
    {
        var result = await ExecutableIdentityTests.Run("ui", verb, "--hwnd=not-a-window", "--maxNodes=1", "--maxDurationMs=1");
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("invalid-argument", result.Json.GetProperty("error").GetString());
        Assert.Contains("hwnd", result.Json.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }
}