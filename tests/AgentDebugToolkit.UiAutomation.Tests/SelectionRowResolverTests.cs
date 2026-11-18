using System.Runtime.InteropServices;
using AgentDebugToolkit.Core.Models;
using AgentDebugToolkit.Inspection;
using AgentDebugToolkit.UiAutomation.Cli;
using Xunit;

namespace AgentDebugToolkit.UiAutomation.Tests;

public sealed class SelectionRowResolverTests
{
    private sealed class Node(int id, PageNodeKind kind, string type)
    {
        internal int Id = id;
        internal PageNodeKind Kind = kind;
        internal string Type = type;
        internal Node? Parent;
        internal List<Node> Children = new();
        internal GridItemPosition? Position;
        internal bool Complete = true, Enabled = true, Virtualized;
        internal Node Add(Node node) { node.Parent = this; Children.Add(node); return node; }
    }
    private sealed class Source : PageSource<Node>
    {
        internal bool Grid = true;
        internal GridDimensions Shape = new(2, 1);
        internal Node Coordinate = null!;
        internal readonly List<string> Calls = new();
        internal Func<string, bool>? Fail;
        private void Call(string call) { Calls.Add(call); if (Fail?.Invoke(call) == true) throw new COMException("row provider evidence"); }
        internal override bool SupportsGrid(Node node) { Call("grid"); return Grid; }
        internal override GridDimensions Dimensions(Node node) { Call("dimensions"); return Shape; }
        internal override Node Cell(Node node, int row, int column) { Call($"cell:{row}:{column}"); return Coordinate; }
        internal override PageNodeKind Kind(Node node) { Call("kind:" + node.Id); return node.Kind; }
        internal override Node? First(Node node) { Call("first:" + node.Id); return node.Children.FirstOrDefault(); }
        internal override Node? Next(Node node)
        {
            Call("next:" + node.Id);
            var siblings = node.Parent?.Children;
            return siblings is not null && siblings.IndexOf(node) + 1 < siblings.Count ? siblings[siblings.IndexOf(node) + 1] : null;
        }
        internal override PageElement Snapshot(Node node, string stage)
        {
            Call("snapshot:" + node.Id);
            return new() { ProcessId = 42, RuntimeId = new[] { 42, node.Id }, Kind = node.Kind, ControlType = node.Type,
                GridItemSupported = node.Position is not null, GridItem = node.Position, Complete = node.Complete,
                VirtualizedItemSupported = node.Virtualized, State = new ElementStateInfo { IsEnabled = node.Enabled, SelectionItemSupported = true } };
        }
    }
    private static SelectionIdentity Identity(Node node) => new(42, new[] { 42, node.Id });
    private static (Node Root, Node Row, Node Cell, Source Source) Fixture()
    {
        var root = new Node(10, PageNodeKind.Rows, "ControlType.DataGrid");
        var row = root.Add(new(11, PageNodeKind.Row, "ControlType.DataItem"));
        var cell = row.Add(new(12, PageNodeKind.Row, "ControlType.DataItem") { Position = new(1, 0, 1, 1) });
        return (root, row, cell, new() { Coordinate = cell });
    }
    private static (SelectionResolution<Node>? Result, InspectionTraversal Budget) Resolve(Node root, Source source, string kind = "logical",
        int ordinal = 1, int nodes = 5000, int depth = 8, Node? owner = null)
    {
        var bounds = new PageReadOptions(ordinal, 1, 32, depth, new(nodes, 10000));
        var budget = new InspectionTraversal(bounds.Budget, () => 0);
        SelectionResolution<Node>? result = null;
        budget.RunBounded(() => result = SelectionRowResolver.Resolve(root, new(true, kind, bounds), source, budget,
            node => node.Parent, _ => Identity(owner ?? root), Identity));
        return (result, budget);
    }

    [Fact]
    public void LogicalCoordinatesProveAProperRowAncestorNeverTheCoordinateCell()
    {
        var fixture = Fixture(); var (result, _) = Resolve(fixture.Root, fixture.Source);
        Assert.Null(result!.Error); Assert.Same(fixture.Row, result.Node); Assert.NotSame(fixture.Cell, result.Node);
        Assert.True(SelectionIdentity.Same(Identity(fixture.Cell), result.Anchor));
        Assert.True(SelectionIdentity.Same(Identity(fixture.Root), result.ExpectedContainer));
        Assert.Equal(new(2, 1), result.Dimensions);
        Assert.Equal(new[] { "cell:1:0" }, fixture.Source.Calls.Where(call => call.StartsWith("cell:")));
        Assert.DoesNotContain(fixture.Source.Calls, call => call.StartsWith("first:") || call.StartsWith("next:"));
    }

    [Theory]
    [InlineData("wrong-row")]
    [InlineData("wrong-column")]
    [InlineData("merged-rows")]
    [InlineData("wrong-owner")]
    [InlineData("cell-only")]
    [InlineData("row-itself-has-griditem")]
    [InlineData("depth-zero")]
    public void CellEvidenceOrAmbiguousCoordinatesNeverBecomeRowSelection(string failure)
    {
        var fixture = Fixture(); Node? owner = null;
        if (failure == "wrong-row") fixture.Cell.Position = new(0, 0, 1, 1);
        if (failure == "wrong-column") fixture.Cell.Position = new(1, 1, 1, 1);
        if (failure == "merged-rows") fixture.Cell.Position = new(1, 0, 2, 1);
        if (failure == "wrong-owner") owner = new(99, PageNodeKind.Rows, "ControlType.DataGrid");
        if (failure == "cell-only") fixture.Cell.Parent = fixture.Root;
        if (failure == "row-itself-has-griditem") fixture.Row.Position = new(1, 0, 1, 1);
        var (result, _) = Resolve(fixture.Root, fixture.Source, depth: failure == "depth-zero" ? 0 : 8, owner: owner);
        Assert.Equal("row-identity-unavailable", result!.Error); Assert.Null(result.Node);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2147483647, false)]
    public void LogicalRowRangeUsesActualProviderDimensions(int ordinal, bool available)
    {
        var fixture = Fixture(); fixture.Cell.Position = new(ordinal, 0, 1, 1);
        var (result, _) = Resolve(fixture.Root, fixture.Source, ordinal: ordinal);
        Assert.Equal(available ? null : "row-out-of-range", result!.Error);
        Assert.Equal(available ? 1 : 0, fixture.Source.Calls.Count(call => call.StartsWith("cell:")));
    }

    [Fact]
    public void MissingGridOrZeroColumnsFailWithoutInventingAnExposedFallback()
    {
        var fixture = Fixture(); fixture.Source.Grid = false;
        Assert.Equal("unsupported-row-selection", Resolve(fixture.Root, fixture.Source).Result!.Error);
        Assert.DoesNotContain(fixture.Source.Calls, call => call.StartsWith("first:"));
        fixture.Source.Grid = true; fixture.Source.Shape = new(2, 0);
        Assert.Equal("unsupported-row-selection", Resolve(fixture.Root, fixture.Source).Result!.Error);
        Assert.DoesNotContain(fixture.Source.Calls, call => call.StartsWith("cell:"));
    }

    [Fact]
    public void ExposedOrdinalsAreThePagerOrdinalsAndDoNotSkipCellLikeItems()
    {
        var root = new Node(10, PageNodeKind.Rows, "ControlType.List");
        root.Add(new(11, PageNodeKind.Other, "ControlType.Header"));
        var first = root.Add(new(12, PageNodeKind.Row, "ControlType.ListItem"));
        var second = root.Add(new(13, PageNodeKind.Row, "ControlType.ListItem"));
        var source = new Source(); var (result, _) = Resolve(root, source, "exposed", 1);
        Assert.Same(second, result!.Node); Assert.Null(result.Error);
        Assert.DoesNotContain(source.Calls, call => call is "grid" or "dimensions" || call.StartsWith("cell:"));
        first.Type = "ControlType.DataItem"; first.Position = new(0, 0, 1, 1);
        Assert.Equal("unsupported-row-selection", Resolve(root, source, "exposed", 0).Result!.Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2147483647)]
    public void EmptyAndOutOfRangeExposureIsExplicitFailureNotACompleteEmptySelection(int ordinal)
    {
        var root = new Node(10, PageNodeKind.Rows, "ControlType.List");
        Assert.Equal("row-out-of-range", Resolve(root, new(), "exposed", ordinal).Result!.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnreadableAndVirtualizedRowPlaceholdersAreNotSelectable(bool virtualized)
    {
        var fixture = Fixture(); fixture.Row.Complete = false; fixture.Row.Virtualized = virtualized;
        Assert.Equal("selection-incomplete", Resolve(fixture.Root, fixture.Source).Result!.Error);
    }

    [Fact]
    public void DisabledRowIdentityIsRetainedForTheSelectionStatePrecondition()
    {
        var fixture = Fixture(); fixture.Row.Enabled = false;
        var result = Resolve(fixture.Root, fixture.Source).Result!;
        Assert.Null(result.Error); Assert.Same(fixture.Row, result.Node); Assert.False(result.Node!.Enabled);
    }

    [Theory]
    [InlineData("grid")]
    [InlineData("dimensions")]
    [InlineData("cell:1:0")]
    [InlineData("snapshot:11")]
    public void ProviderFailuresDoNotMasqueradeAsEmptyOrUnsupportedRows(string operation)
    {
        var fixture = Fixture(); fixture.Source.Fail = call => call == operation;
        var options = new SelectionOptions(true, "logical", new(1, 1, 32, 8, new(5000, 10000)));
        var budget = new InspectionTraversal(options.Bounds.Budget, () => 0);
        Assert.False(budget.TryRead("resolve", () => SelectionRowResolver.Resolve(fixture.Root, options, fixture.Source, budget,
            node => node.Parent, _ => Identity(fixture.Root), Identity), out _));
        Assert.True(budget.HasErrors); Assert.Contains("provider-error", budget.Reasons);
    }

    [Fact]
    public void ProviderFailureInSkippedExposurePreventsTrustingALaterOrdinal()
    {
        var root = new Node(10, PageNodeKind.Rows, "ControlType.List");
        root.Add(new(11, PageNodeKind.Row, "ControlType.ListItem")); root.Add(new(12, PageNodeKind.Row, "ControlType.ListItem"));
        var source = new Source { Fail = call => call == "kind:11" };
        var (result, budget) = Resolve(root, source, "exposed", 0);
        Assert.True(budget.HasErrors); Assert.Equal("selection-incomplete", result!.Error); Assert.Null(result.Node);
    }

    [Fact]
    public void ExposedWorkBudgetStopsScanningSkippedItemsAndNeverMaterializesTheTree()
    {
        var root = new Node(10, PageNodeKind.Rows, "ControlType.List");
        for (var index = 0; index < 100; index++) root.Add(new(100 + index, PageNodeKind.Row, "ControlType.ListItem"));
        var source = new Source(); var (result, budget) = Resolve(root, source, "exposed", 99, nodes: 2);
        Assert.Null(result); Assert.Contains("node-budget", budget.Reasons); Assert.Equal(2, budget.NodesVisited);
        Assert.True(source.Calls.Count(call => call.StartsWith("kind:")) <= 2);
        Assert.DoesNotContain(source.Calls, call => call == "kind:199");
    }
}