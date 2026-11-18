using AgentDebugToolkit.Inspection;

namespace AgentDebugToolkit.UiAutomation.Cli;

internal static class SelectionRowResolver
{
    internal static bool IsRowRole(string? containerType, string? candidateType, bool? gridItemSupported) =>
        candidateType == "ControlType.ListItem" && containerType == "ControlType.List" ||
        candidateType == "ControlType.DataItem" && gridItemSupported == false;

    internal static SelectionResolution<TNode> Resolve<TNode>(TNode root, SelectionOptions options, PageSource<TNode> pages,
        InspectionTraversal budget, Func<TNode, TNode?> parent, Func<TNode, SelectionIdentity?> cellOwner,
        Func<TNode, SelectionIdentity?> identity) where TNode : class
    {
        SelectionResolution<TNode> Fail(string code, string message) => new(null, code, message);
        var rootSnapshot = budget.Call(() => pages.Snapshot(root, "row.container"));
        var rootId = SelectionIdentity.From(rootSnapshot);
        if (!rootSnapshot.Complete || rootId is null) return Fail("selection-incomplete", "Row container identity is unreadable or incomplete.");
        if (rootSnapshot.Kind != PageNodeKind.Rows) return Fail("unsupported-row-selection", "A List/DataGrid/Table container is required.");
        var row = options.Bounds.Offset;
        if (options.RowKind == "exposed")
        {
            var enumerator = new PagedUiaReader<TNode>(pages, options.Bounds, false, budget);
            foreach (var candidate in enumerator.Enumerate(root))
            {
                if (candidate.Index != row) continue;
                if (budget.Reasons.Any() || budget.HasErrors)
                    return Fail("selection-incomplete", "A truncated scan cannot establish the requested exposed ordinal.");
                var observed = budget.Call(() => pages.Snapshot(candidate.Node, "row.exposed"));
                var id = SelectionIdentity.From(observed);
                if (!observed.Complete || id is null) return Fail("selection-incomplete", "Exposed row identity is unreadable.");
                if (!IsRowRole(rootSnapshot.ControlType, observed.ControlType, observed.GridItemSupported))
                    return Fail("unsupported-row-selection", "An exposed cell is not evidence of a selectable row.");
                return new(candidate.Node, Identity: id, ExpectedContainer: rootId);
            }
            return Fail(budget.Reasons.Any() ? "selection-incomplete" : "row-out-of-range", "Requested exposed row was not established within the bounded exposure.");
        }

        if (!budget.Call(() => pages.SupportsGrid(root))) return Fail("unsupported-row-selection", "Logical row coordinates require GridPattern.");
        var dimensions = budget.Call(() => pages.Dimensions(root));
        if (dimensions.Rows < 0 || dimensions.Columns < 0) return Fail("selection-provider-error", "GridPattern reported invalid dimensions.");
        if (row >= dimensions.Rows) return Fail("row-out-of-range", "Logical row is outside the provider-reported row count.");
        if (dimensions.Columns == 0) return Fail("unsupported-row-selection", "No cell coordinate is available to establish row ancestry.");
        var cell = budget.Call(() => pages.Cell(root, row, 0));
        var cellSnapshot = budget.Call(() => pages.Snapshot(cell, "row.coordinateCell"));
        var cellId = SelectionIdentity.From(cellSnapshot);
        if (!cellSnapshot.Complete || cellId is null || cellSnapshot.GridItem is not { } position ||
            position.Row != row || position.Column != 0 || position.RowSpan != 1 || position.ColumnSpan < 1 ||
            !SelectionIdentity.Same(budget.Call(() => cellOwner(cell)), rootId))
            return Fail("row-identity-unavailable", "Cell coordinates/owner cannot establish the exact requested logical row.");

        TNode? current = cell;
        for (var depth = 0; depth < options.Bounds.MaxDepth; depth++)
        {
            var previous = current;
            if (previous is null) break;
            current = budget.Call(() => parent(previous));
            if (current is null) break;
            var node = current;
            if (!budget.TryReadNode("row.ancestor", () => identity(node), out var id) || id is null)
                return Fail("selection-incomplete", "Row ancestry identity is unavailable.");
            if (SelectionIdentity.Same(id, rootId)) break;
            if (budget.Call(() => pages.Kind(node)) != PageNodeKind.Row) continue;
            var observed = budget.Call(() => pages.Snapshot(node, "row.ancestor"));
            if (!observed.Complete) return Fail("selection-incomplete", "Candidate row state is incomplete.");
            if (IsRowRole(rootSnapshot.ControlType, observed.ControlType, observed.GridItemSupported))
                return new(node, Identity: id, ExpectedContainer: rootId, Anchor: cellId, Dimensions: dimensions);
        }
        return Fail("row-identity-unavailable", "No provable row ancestor was found; selecting a cell is not row selection. Increase maxDepth only if appropriate.");
    }
}