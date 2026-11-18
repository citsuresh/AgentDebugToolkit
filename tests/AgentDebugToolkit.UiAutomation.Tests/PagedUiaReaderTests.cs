using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using AgentDebugToolkit.Core;
using AgentDebugToolkit.Core.Models;
using AgentDebugToolkit.Inspection;
using AgentDebugToolkit.UiAutomation.Cli;
using Xunit;

namespace AgentDebugToolkit.UiAutomation.Tests;

public sealed class PagedUiaReaderTests
{
    private sealed class Node(string name, PageNodeKind kind = PageNodeKind.Other)
    {
        internal string Name { get; } = name;
        internal PageNodeKind Kind { get; } = kind;
        internal Node? Parent { get; private set; }
        internal List<Node> Children { get; } = new();
        internal bool Enabled { get; init; } = true;
        internal bool Virtualized { get; init; }
        internal GridItemPosition? Position { get; init; }
        internal Node Add(string name, PageNodeKind kind) { var child = new Node(name, kind) { Parent = this }; Children.Add(child); return child; }
    }

    private sealed class Source(InspectionTraversal budget) : PageSource<Node>
    {
        internal bool Grid { get; init; }
        internal GridDimensions Shape { get; init; } = new(0, 0);
        internal GridDimensions? After { get; init; }
        internal Func<string, Exception?>? Fail { get; init; }
        internal Action<string>? Observe { get; init; }
        internal Func<int, int, Node>? GetCell { get; init; }
        internal List<string> Calls { get; } = new();
        private int _dimensionReads;
        private void Call(string call) { Calls.Add(call); Observe?.Invoke(call); if (Fail?.Invoke(call) is { } error) throw error; }
        internal override bool SupportsGrid(Node root) { Call("grid"); return Grid; }
        internal override GridDimensions Dimensions(Node root) { Call("dimensions"); return _dimensionReads++ == 0 ? Shape : After ?? Shape; }
        internal override Node Cell(Node root, int row, int column) { Call($"cell:{row}:{column}"); return GetCell?.Invoke(row, column) ?? new($"r{row}c{column}") { Position = new(row, column, 1, 1) }; }
        internal override PageNodeKind Kind(Node node) { Call("kind:" + node.Name); return node.Kind; }
        internal override Node? First(Node node) { Call("first:" + node.Name); return node.Children.FirstOrDefault(); }
        internal override Node? Next(Node node)
        {
            Call("next:" + node.Name);
            return node.Parent is { } parent && parent.Children.IndexOf(node) + 1 < parent.Children.Count
                ? parent.Children[parent.Children.IndexOf(node) + 1] : null;
        }
        internal override PageElement Snapshot(Node node, string stage)
        {
            Call("snapshot:" + node.Name);
            var complete = true;
            return new() { Kind = node.Kind, RuntimeId = new[] { 42, node.Name.Length },
                Name = UiaPageSource.LimitText(node.Name, budget, ref complete), ControlType = node.Kind.ToString(),
                State = new ElementStateInfo { IsEnabled = node.Enabled, ToggleSupported = false, SelectionItemSupported = true, IsSelected = false },
                VirtualizedItemSupported = node.Virtualized, ExpandCollapseSupported = true, ExpandCollapseState = "Collapsed",
                GridItemSupported = node.Position is not null, GridItem = node.Position, Complete = complete };
        }
    }

    private static PageReadOptions Options(int offset = 0, int limit = 2, int columns = 32, int depth = 8, int nodes = 5000) => new(offset, limit, columns, depth, new(nodes, 10000));
    private static (JsonElement Json, string? Error) Read(Source source, InspectionTraversal budget, Node root, PageReadOptions options, bool tree = false)
    {
        var reader = new PagedUiaReader<Node>(source, options, tree, budget);
        var page = reader.Read(root);
        using var output = new StringWriter(); var original = Console.Out;
        try { Console.SetOut(output); Assert.Equal(reader.Error is null ? 0 : 1, Verbs.WritePage(page, reader.Error)); }
        finally { Console.SetOut(original); }
        using var document = JsonDocument.Parse(output.ToString());
        return (document.RootElement.Clone(), reader.Error);
    }
    private static JsonElement Budget(JsonElement result) => result.GetProperty("traversal");
    private static string?[] Reasons(JsonElement result) => Budget(result).GetProperty("stopReasons").EnumerateArray().Select(reason => reason.GetString()).ToArray();

    [Theory]
    [InlineData(0, 0, 2, 0, false, true)]
    [InlineData(3, 0, 2, 2, true, false)]
    [InlineData(3, 2, 2, 1, false, false)]
    [InlineData(2, 2, 2, 0, false, false)]
    [InlineData(2, int.MaxValue, 100, 0, false, false)]
    public void GridPages_UseExactCoordinatesLimitsAndKnownTotals(int rows, int offset, int limit, int returned, bool more, bool complete)
    {
        var options = Options(offset, limit); var budget = new InspectionTraversal(options.Budget, () => 0);
        var source = new Source(budget) { Grid = true, Shape = new(rows, 2) };
        var (json, error) = Read(source, budget, new("grid", PageNodeKind.Rows), options);
        Assert.Null(error); Assert.True(json.GetProperty("success").GetBoolean());
        Assert.Equal(returned, json.GetProperty("returned").GetInt32());
        Assert.Equal(rows, json.GetProperty("logicalTotal").GetInt32());
        Assert.Equal(more, json.GetProperty("hasMore").GetBoolean());
        Assert.Equal(complete, json.GetProperty("complete").GetBoolean());
        Assert.True(json.GetProperty("pageComplete").GetBoolean());
        Assert.Equal(returned * 2, source.Calls.Count(call => call.StartsWith("cell:")));
        Assert.DoesNotContain(source.Calls, call => call.StartsWith("first:") || call.StartsWith("kind:"));
        if (more) Assert.Equal((long)offset + returned, json.GetProperty("nextOffset").GetInt64());
        else Assert.False(json.TryGetProperty("nextOffset", out _));
        if (returned > 0)
        {
            var row = json.GetProperty("rows")[0]; Assert.Equal(offset, row.GetProperty("index").GetInt32());
            Assert.Equal(2, row.GetProperty("cells").GetArrayLength());
            Assert.Equal(offset, row.GetProperty("cells")[0].GetProperty("element").GetProperty("gridItem").GetProperty("row").GetInt32());
        }
    }

    [Theory]
    [InlineData(32, false)]
    [InlineData(33, true)]
    public void GridColumnBoundary_IsExactAndNeverSilentlyDropsColumns(int columns, bool truncated)
    {
        var options = Options(); var budget = new InspectionTraversal(options.Budget, () => 0);
        var source = new Source(budget) { Grid = true, Shape = new(1, columns) };
        var (json, _) = Read(source, budget, new("grid"), options);
        Assert.Equal(32, json.GetProperty("rows")[0].GetProperty("cells").GetArrayLength());
        Assert.Equal(columns, json.GetProperty("totalColumns").GetInt32());
        Assert.Equal(!truncated, json.GetProperty("pageComplete").GetBoolean());
        Assert.Equal(truncated, Reasons(json).Contains("column-limit"));
    }

    [Fact]
    public void HugeLogicalOffset_DoesNotEnumerateSkippedRowsOrInventCheapProviderComplexity()
    {
        var options = Options(1000000, 1); var budget = new InspectionTraversal(options.Budget, () => 0);
        var source = new Source(budget) { Grid = true, Shape = new(1000010, 1) };
        var (json, _) = Read(source, budget, new("grid"), options);
        Assert.Equal(new[] { "cell:1000000:0" }, source.Calls.Where(call => call.StartsWith("cell:")));
        Assert.Equal("provider-coordinate-access", json.GetProperty("offsetCost").GetString());
    }

    [Fact]
    public void InaccessibleGridCell_RemainsAnIndexedPlaceholderAndMakesTheEnvelopeFail()
    {
        var options = Options(); var budget = new InspectionTraversal(options.Budget, () => 0);
        var source = new Source(budget) { Grid = true, Shape = new(2, 2), Fail = call => call == "cell:0:1" ? new COMException("unavailable") : null };
        var (json, error) = Read(source, budget, new("grid"), options);
        Assert.Equal("page-provider-error", error); Assert.False(json.GetProperty("success").GetBoolean());
        Assert.Equal(2, json.GetProperty("rows").GetArrayLength());
        var failed = json.GetProperty("rows")[0].GetProperty("cells")[1];
        Assert.Equal(1, failed.GetProperty("index").GetInt32());
        Assert.Equal("unavailable", failed.GetProperty("status").GetString());
        Assert.False(failed.TryGetProperty("element", out _));
        Assert.False(json.GetProperty("pageComplete").GetBoolean());
        Assert.Equal(0, json.GetProperty("retryOffset").GetInt32());
        Assert.False(json.TryGetProperty("nextOffset", out _));
        Assert.Equal("rows[0].cells[1]", Budget(json).GetProperty("errors")[0].GetProperty("stage").GetString());
    }

    [Theory]
    [InlineData("grid")]
    [InlineData("dimensions")]
    public void FailedProviderSupportOrTotals_AreNotUnsupportedOrSuccessfulEmptyPages(string operation)
    {
        var options = Options(); var budget = new InspectionTraversal(options.Budget, () => 0);
        var source = new Source(budget) { Grid = true, Fail = call => call == operation ? new COMException("provider failure") : null };
        var (json, error) = Read(source, budget, new("grid", PageNodeKind.Rows), options);
        Assert.Equal("page-provider-error", error);
        Assert.False(json.TryGetProperty("total", out _)); Assert.False(json.GetProperty("complete").GetBoolean());
        Assert.DoesNotContain(source.Calls, call => call.StartsWith("first:"));
    }

    [Fact]
    public void ChangedGridDimensions_ReportBothObservationsWithoutRetry()
    {
        var options = Options(); var budget = new InspectionTraversal(options.Budget, () => 0);
        var source = new Source(budget) { Grid = true, Shape = new(1, 1), After = new(2, 1) };
        var (json, error) = Read(source, budget, new("grid"), options);
        Assert.Equal("snapshot-changed", error);
        Assert.Equal(1, json.GetProperty("logicalTotal").GetInt32()); Assert.Equal(2, json.GetProperty("logicalTotalAfter").GetInt32());
        Assert.False(json.GetProperty("atomic").GetBoolean()); Assert.False(json.GetProperty("pageComplete").GetBoolean());
        Assert.Equal(2, source.Calls.Count(call => call == "dimensions"));
        Assert.False(json.TryGetProperty("hasMore", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GridItemCoordinates_AllowMergedCellsButRejectUnrelatedProviderItems(bool mismatched)
    {
        var options = Options(1, 1); var budget = new InspectionTraversal(options.Budget, () => 0);
        var source = new Source(budget) { Grid = true, Shape = new(2, 2),
            GetCell = (_, _) => new("merged") { Position = new(mismatched ? 9 : 0, 0, 2, 2) } };
        var (json, error) = Read(source, budget, new("grid"), options);
        Assert.Equal(mismatched ? "page-provider-error" : null, error);
        Assert.Equal(!mismatched, json.GetProperty("pageComplete").GetBoolean());
    }

    [Fact]
    public void WorkBudget_PreservesAnIncompleteCellInsteadOfClaimingTheRowIsComplete()
    {
        var options = Options(nodes: 4); var budget = new InspectionTraversal(options.Budget, () => 0);
        var source = new Source(budget) { Grid = true, Shape = new(1, 3) };
        var (json, _) = Read(source, budget, new("grid"), options);
        Assert.Equal(4, budget.NodesVisited);
        var cells = json.GetProperty("rows")[0].GetProperty("cells");
        Assert.Equal(2, cells.GetArrayLength()); Assert.Equal("read", cells[0].GetProperty("status").GetString());
        Assert.Equal("incomplete", cells[1].GetProperty("status").GetString());
        Assert.False(json.GetProperty("pageComplete").GetBoolean()); Assert.Contains("node-budget", Reasons(json));
        Assert.Equal(1, source.Calls.Count(call => call.StartsWith("cell:")));
    }

    [Fact]
    public void TimeOverrun_DoesNotStartFurtherReadsOrInventAnEmptyCompleteResult()
    {
        long clock = 0;
        var options = Options(); var budget = new InspectionTraversal(options.Budget, () => clock);
        var source = new Source(budget) { Observe = _ => clock = 20000 };
        var (json, error) = Read(source, budget, new("root"), options);
        Assert.Equal("page-incomplete", error); Assert.Single(source.Calls);
        Assert.False(json.GetProperty("complete").GetBoolean()); Assert.Contains("time-budget", Reasons(json));
    }

    [Fact]
    public void ExposedListPaging_ScansSkippedItemsButDoesNotSnapshotTheirCellsOrScanTheWholeList()
    {
        var root = new Node("list", PageNodeKind.Rows);
        root.Add("header", PageNodeKind.Other);
        root.Add("skipped", PageNodeKind.Row).Add("skippedCell", PageNodeKind.Other);
        root.Add("returned", PageNodeKind.Row).Add("cell", PageNodeKind.Other);
        root.Add("lookahead", PageNodeKind.Row); root.Add("unvisited", PageNodeKind.Row);
        var options = Options(1, 1); var budget = new InspectionTraversal(options.Budget, () => 0); var source = new Source(budget);
        var (json, error) = Read(source, budget, root, options);
        Assert.Null(error); Assert.True(json.GetProperty("pageComplete").GetBoolean());
        Assert.True(json.GetProperty("hasMore").GetBoolean()); Assert.Equal(2, json.GetProperty("nextOffset").GetInt32());
        Assert.False(json.TryGetProperty("total", out _)); Assert.False(json.TryGetProperty("logicalTotal", out _));
        Assert.Contains("kind:skipped", source.Calls); Assert.DoesNotContain("snapshot:skipped", source.Calls);
        Assert.DoesNotContain("snapshot:lookahead", source.Calls); Assert.DoesNotContain("kind:unvisited", source.Calls);
        var row = json.GetProperty("rows")[0]; Assert.Equal("returned", row.GetProperty("element").GetProperty("name").GetString());
        Assert.Equal(1, row.GetProperty("exposedCellCount").GetInt32());
        Assert.False(row.GetProperty("cells")[0].TryGetProperty("column", out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    public void EmptyAndOutOfRangeExposure_DoesNotInventLogicalTotalsOrFullCompleteness(int offset)
    {
        var options = Options(offset); var budget = new InspectionTraversal(options.Budget, () => 0);
        var (json, error) = Read(new(budget), budget, new("list", PageNodeKind.Rows), options);
        Assert.Null(error); Assert.Equal(0, json.GetProperty("total").GetInt32());
        Assert.Equal("observed-exposed-items", json.GetProperty("totalKind").GetString());
        Assert.False(json.TryGetProperty("logicalTotal", out _)); Assert.True(json.GetProperty("pageComplete").GetBoolean());
        Assert.False(json.GetProperty("complete").GetBoolean()); Assert.Contains("exposure-only", Reasons(json));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void ExposedPageBoundary_ProbesForOneMoreMatchWithoutCapturingIt(int count, bool more)
    {
        var root = new Node("list", PageNodeKind.Rows); for (var i = 0; i < count; i++) root.Add(i.ToString(), PageNodeKind.Row);
        var options = Options(); var budget = new InspectionTraversal(options.Budget, () => 0); var source = new Source(budget);
        var (json, _) = Read(source, budget, root, options);
        Assert.Equal(Math.Min(count, 2), json.GetProperty("returned").GetInt32());
        Assert.Equal(more, json.GetProperty("hasMore").GetBoolean()); Assert.True(json.GetProperty("pageComplete").GetBoolean());
        Assert.Equal(!more, json.TryGetProperty("total", out _));
        Assert.Equal(1 + Math.Min(count, 2), source.Calls.Count(call => call.StartsWith("snapshot:")));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public void ExposedCellCap_DistinguishesExactCapFromAnOmittedSibling(int cap, bool limited)
    {
        var root = new Node("list", PageNodeKind.Rows); var row = root.Add("row", PageNodeKind.Row);
        row.Add("one", PageNodeKind.Other); row.Add("two", PageNodeKind.Other);
        var options = Options(columns: cap); var budget = new InspectionTraversal(options.Budget, () => 0);
        var (json, _) = Read(new(budget), budget, root, options);
        var observed = json.GetProperty("rows")[0];
        Assert.Equal(cap, observed.GetProperty("cells").GetArrayLength());
        Assert.Equal(!limited, observed.GetProperty("cellsComplete").GetBoolean());
        Assert.Equal(!limited, observed.TryGetProperty("exposedCellCount", out _));
        Assert.Equal(limited, Reasons(json).Contains("column-limit"));
    }

    [Fact]
    public void NegativeProviderTotals_AreNotNormalizedToZero()
    {
        var options = Options(); var budget = new InspectionTraversal(options.Budget, () => 0);
        var (json, error) = Read(new(budget) { Grid = true, Shape = new(-1, 2) }, budget, new("grid"), options);
        Assert.Equal("page-provider-error", error); Assert.False(json.TryGetProperty("total", out _));
        Assert.False(json.GetProperty("pageComplete").GetBoolean());
    }

    [Fact]
    public void UnexpectedFailures_AreNotConvertedToEmptyPages()
    {
        var options = Options(); var budget = new InspectionTraversal(options.Budget, () => 0);
        var exception = new ApplicationException("unexpected");
        var source = new Source(budget) { Fail = _ => exception };
        Assert.Same(exception, Assert.Throws<ApplicationException>(() => new PagedUiaReader<Node>(source, options, false, budget).Read(new("root"))));
    }

    [Fact]
    public void UnsupportedRoot_IsAnExplicitFailureNotAnEmptyList()
    {
        var options = Options(); var budget = new InspectionTraversal(options.Budget, () => 0);
        var (json, error) = Read(new(budget), budget, new("pane"), options);
        Assert.Equal("unsupported-provider", error); Assert.Contains("unsupported-provider", Reasons(json));
        Assert.False(json.GetProperty("pageComplete").GetBoolean()); Assert.False(Budget(json).GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void TreePage_PreservesPathsParentIndicesAndDisabledVirtualizedCollapsedObservations()
    {
        var root = new Node("tree", PageNodeKind.Tree);
        var parent = root.Add("wrapper", PageNodeKind.Other).Add("parent", PageNodeKind.TreeItem);
        parent.Add("child", PageNodeKind.TreeItem); root.Add("sibling", PageNodeKind.TreeItem);
        var options = Options(1, 1); var budget = new InspectionTraversal(options.Budget, () => 0); var source = new Source(budget);
        var (json, _) = Read(source, budget, root, options, tree: true);
        var item = json.GetProperty("items")[0]; Assert.Equal(1, item.GetProperty("index").GetInt32());
        Assert.Equal(0, item.GetProperty("parentIndex").GetInt32()); Assert.Equal(3, item.GetProperty("depth").GetInt32());
        Assert.Equal(new[] { 0, 0, 0 }, item.GetProperty("path").EnumerateArray().Select(part => part.GetInt32()));
        Assert.False(json.TryGetProperty("rows", out _)); Assert.DoesNotContain("snapshot:parent", source.Calls);

        var disabled = new Node("tree", PageNodeKind.Tree) { Enabled = false, Virtualized = true };
        var otherBudget = new InspectionTraversal(options.Budget, () => 0); var otherSource = new Source(otherBudget);
        var (observed, _) = Read(otherSource, otherBudget, disabled, options, tree: true);
        var state = observed.GetProperty("root"); Assert.False(state.GetProperty("state").GetProperty("isEnabled").GetBoolean());
        Assert.True(state.GetProperty("virtualizedItemSupported").GetBoolean());
        Assert.Equal("Collapsed", state.GetProperty("expandCollapseState").GetString());
        Assert.DoesNotContain(otherSource.Calls, call => call.Contains("focus") || call.Contains("expand") || call.Contains("select") || call.Contains("realize"));
    }

    [Theory]
    [InlineData("kind:row")]
    [InlineData("first:list")]
    [InlineData("snapshot:cell")]
    [InlineData("next:cell")]
    public void ExposedFailures_AreNotSilentlyDroppedOrTurnedIntoKnownEmptyTotals(string failingCall)
    {
        var root = new Node("list", PageNodeKind.Rows); root.Add("row", PageNodeKind.Row).Add("cell", PageNodeKind.Other);
        var options = Options(); var budget = new InspectionTraversal(options.Budget, () => 0);
        var source = new Source(budget) { Fail = call => call == failingCall ? new COMException("failed") : null };
        var (json, error) = Read(source, budget, root, options);
        Assert.Equal("page-provider-error", error); Assert.False(json.GetProperty("pageComplete").GetBoolean());
        Assert.False(json.TryGetProperty("total", out _));
        if (failingCall == "snapshot:cell") Assert.Equal("unavailable", json.GetProperty("rows")[0].GetProperty("cells")[0].GetProperty("status").GetString());
    }

    [Fact]
    public void DepthLimit_MarksUnknownDescendantsRatherThanACompleteEmptyTree()
    {
        var root = new Node("tree", PageNodeKind.Tree); root.Add("hidden", PageNodeKind.TreeItem);
        var options = Options(depth: 0); var budget = new InspectionTraversal(options.Budget, () => 0);
        var (json, _) = Read(new(budget), budget, root, options, tree: true);
        Assert.Contains("max-depth", Reasons(json)); Assert.False(json.GetProperty("pageComplete").GetBoolean());
        Assert.False(json.TryGetProperty("hasMore", out _)); Assert.False(json.TryGetProperty("total", out _));
    }

    [Theory]
    [InlineData(512, true)]
    [InlineData(513, false)]
    public void TextOutputBoundary_UsesTheProductionLimiter(int length, bool complete)
    {
        var budget = new InspectionTraversal(new(1, 10000), () => 0); var captured = true;
        Assert.Equal(512, UiaPageSource.LimitText(new string('x', length), budget, ref captured)!.Length);
        Assert.Equal(complete, captured); Assert.Equal(!complete, budget.Truncated);
    }

    [Theory]
    [InlineData(32, true)]
    [InlineData(33, false)]
    public void IdentityOutputBoundary_IsExplicitInsteadOfUnbounded(int length, bool complete)
    {
        var budget = new InspectionTraversal(new(1, 10000), () => 0); var captured = true;
        Assert.Equal(32, UiaPageSource.LimitIdentity(new int[length], budget, ref captured)!.Length);
        Assert.Equal(complete, captured);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingIdentity_IsAnErrorNotAnEmptyOrInventedIdentifier(bool nullIdentity)
    {
        var budget = new InspectionTraversal(new(1, 10000), () => 0); var complete = true;
        Assert.Null(UiaPageSource.LimitIdentity(nullIdentity ? null : Array.Empty<int>(), budget, ref complete));
        Assert.False(complete); Assert.True(budget.HasErrors);
    }
}