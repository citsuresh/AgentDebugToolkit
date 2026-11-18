using System.Text.Json.Serialization;
using AgentDebugToolkit.Core.Models;
using AgentDebugToolkit.Inspection;

namespace AgentDebugToolkit.UiAutomation.Cli;

internal enum PageNodeKind { Other, Rows, Row, Tree, TreeItem }
internal sealed record GridDimensions(int Rows, int Columns);
internal sealed record GridItemPosition(
    [property: JsonPropertyName("row")] int Row, [property: JsonPropertyName("column")] int Column,
    [property: JsonPropertyName("rowSpan")] int RowSpan, [property: JsonPropertyName("columnSpan")] int ColumnSpan);

internal sealed class PageElement
{
    [JsonIgnore] public PageNodeKind Kind { get; set; }
    [JsonPropertyName("runtimeId")] public int[]? RuntimeId { get; set; }
    [JsonPropertyName("processId")] public int? ProcessId { get; set; }
    [JsonPropertyName("automationId")] public string? AutomationId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("controlType")] public string? ControlType { get; set; }
    [JsonPropertyName("className")] public string? ClassName { get; set; }
    [JsonPropertyName("isOffscreen")] public bool? IsOffscreen { get; set; }
    [JsonPropertyName("state")] public ElementStateInfo? State { get; set; }
    [JsonPropertyName("valueSupported")] public bool? ValueSupported { get; set; }
    [JsonPropertyName("value")] public string? Value { get; set; }
    [JsonPropertyName("textSupported")] public bool? TextSupported { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("gridItemSupported")] public bool? GridItemSupported { get; set; }
    [JsonPropertyName("gridItem")] public GridItemPosition? GridItem { get; set; }
    [JsonPropertyName("expandCollapseSupported")] public bool? ExpandCollapseSupported { get; set; }
    [JsonPropertyName("expandCollapseState")] public string? ExpandCollapseState { get; set; }
    [JsonPropertyName("virtualizedItemSupported")] public bool? VirtualizedItemSupported { get; set; }
    [JsonPropertyName("complete")] public bool Complete { get; set; }
}

internal sealed class PageCell(int index)
{
    [JsonPropertyName("index")] public int Index { get; } = index;
    [JsonPropertyName("status")] public string Status { get; set; } = "incomplete";
    [JsonPropertyName("element")] public PageElement? Element { get; set; }
}

internal sealed class PageRecord(long index)
{
    [JsonPropertyName("index")] public long Index { get; } = index;
    [JsonPropertyName("path")] public int[]? Path { get; set; }
    [JsonPropertyName("depth")] public int? Depth { get; set; }
    [JsonPropertyName("parentIndex")] public long? ParentIndex { get; set; }
    [JsonPropertyName("element")] public PageElement? Element { get; set; }
    [JsonPropertyName("cells")] public List<PageCell>? Cells { get; set; }
    [JsonPropertyName("cellsComplete")] public bool? CellsComplete { get; set; }
    [JsonPropertyName("exposedCellCount")] public int? ExposedCellCount { get; set; }
}

internal abstract class PageSource<TNode> where TNode : class
{
    internal abstract bool SupportsGrid(TNode root);
    internal abstract GridDimensions Dimensions(TNode root);
    internal abstract TNode Cell(TNode root, int row, int column);
    internal abstract PageNodeKind Kind(TNode node);
    internal abstract TNode? First(TNode node);
    internal abstract TNode? Next(TNode node);
    internal abstract PageElement Snapshot(TNode node, string stage);
}

internal sealed class PagedUiaReader<TNode>(PageSource<TNode> source, PageReadOptions options, bool tree,
    InspectionTraversal budget) where TNode : class
{
    private sealed class Frame(TNode node, int depth, int[] path, long? parentIndex)
    {
        internal TNode Node { get; } = node;
        internal int Depth { get; } = depth;
        internal int[] Path { get; } = path;
        internal long? ParentIndex { get; } = parentIndex;
        internal TNode? Last { get; set; }
        internal int ChildIndex { get; set; }
    }
    internal sealed record Candidate(TNode Node, long Index, int Depth, int[] Path, long? ParentIndex);
    private readonly List<PageRecord> _records = new();
    private PageElement? _root;
    private string _backend = "unavailable";
    private string? _error;
    private long? _total;
    private int? _columns;
    private GridDimensions? _after;
    private bool? _hasMore;
    private bool _ended;

    internal string? Error => _error ?? (budget.HasErrors ? "page-provider-error" : _root is null ? "page-incomplete" : null);
    private bool PageComplete => Error is null && _root?.Complete == true && (_ended || _records.Count == options.Limit) &&
        budget.Reasons.All(reason => reason is "offset" or "page-limit" or "exposure-only") &&
        _records.All(record => record.Element?.Complete != false && record.CellsComplete != false);

    internal object Read(TNode root)
    {
        budget.RunBounded(() =>
        {
            if (options.Offset > 0) budget.NoteLimit("offset");
            if (!budget.TryReadNode("root", () => source.Snapshot(root, "root"), out _root)) return;
            if (_root is null) { budget.NoteError("root", "inspection-provider-error", "No root snapshot was returned."); return; }
            if (!tree)
            {
                if (!budget.TryReadNode("GridPattern", () => source.SupportsGrid(root), out var grid)) return;
                if (grid) { Grid(root); return; }
            }
            if (tree ? _root.Kind is not (PageNodeKind.Tree or PageNodeKind.TreeItem) : _root.Kind != PageNodeKind.Rows)
            {
                if (!budget.HasErrors) { _error = "unsupported-provider"; budget.NoteLimit("unsupported-provider"); }
                return;
            }
            _backend = tree ? "uia-exposed-tree" : "uia-exposed-rows";
            budget.NoteLimit("exposure-only");
            Exposed(root);
        });
        var pageComplete = PageComplete;
        return new
        {
            backend = _backend, scope = tree ? "exposed-tree-items" : _backend == "uia-grid-pattern" ? "logical-grid-rows" : "exposed-rows",
            atomic = false, offsetCost = _backend == "uia-grid-pattern" ? "provider-coordinate-access" : "scan-from-root",
            root = _root, offset = options.Offset, limit = options.Limit,
            maxDepth = options.MaxDepth, maxColumns = tree ? null : (int?)options.MaxColumns,
            rows = tree ? null : _records, items = tree ? _records : null, returned = _records.Count,
            total = _total, totalKind = _total is null ? null : _backend == "uia-grid-pattern" ? "provider-logical-rows" : "observed-exposed-items",
            logicalTotal = _backend == "uia-grid-pattern" ? _total : null, totalColumns = _columns,
            logicalTotalAfter = _after?.Rows, totalColumnsAfter = _after?.Columns,
            pageComplete, complete = pageComplete && budget.Complete,
            hasMore = _hasMore,
            nextOffset = pageComplete && _hasMore != false ? (long?)options.Offset + _records.Count : null,
            retryOffset = pageComplete ? null : (int?)options.Offset,
            traversal = budget.ReportFor(tree ? "exposed-tree-items" : _backend == "uia-grid-pattern" ? "logical-grid-rows" : "exposed-rows"),
            hint = "Non-atomic observations; identities and offsets can change between calls. Use nextOffset with identical options when present; retryOffset requires fixing errors or increasing budgets/caps. Exposed enumeration rescans skipped nodes from the root and never proves logical totals or hidden/virtualized completeness. No expansion, realization, scrolling, selection or focus is requested. Provider calls cannot be interrupted."
        };
    }

    private void Grid(TNode root)
    {
        _backend = "uia-grid-pattern";
        if (!budget.TryReadNode("GridPattern.dimensions", () => source.Dimensions(root), out var shape)) return;
        if (shape.Rows < 0 || shape.Columns < 0) { InvalidShape(); return; }
        _total = shape.Rows;
        _columns = shape.Columns;
        for (long rowIndex = options.Offset; rowIndex < Math.Min((long)shape.Rows, (long)options.Offset + options.Limit); rowIndex++)
        {
            var row = new PageRecord(rowIndex) { Cells = new(), CellsComplete = false };
            _records.Add(row);
            var readable = true;
            for (var column = 0; column < Math.Min(shape.Columns, options.MaxColumns); column++)
            {
                var cell = new PageCell(column);
                row.Cells.Add(cell);
                var stage = $"rows[{rowIndex}].cells[{column}]";
                if (budget.TryReadNode(stage, () => source.Snapshot(source.Cell(root, (int)rowIndex, column), stage), out var element))
                {
                    cell.Element = element;
                    if (element.GridItem is { } position &&
                        (rowIndex < position.Row || rowIndex >= (long)position.Row + position.RowSpan ||
                         column < position.Column || column >= (long)position.Column + position.ColumnSpan))
                    {
                        element.Complete = false;
                        budget.NoteError(stage, "grid-item-mismatch", "Reported GridItem position/span does not contain the requested coordinate.");
                    }
                    cell.Status = element.Complete ? "read" : "partial";
                    readable &= element.Complete;
                }
                else { cell.Status = "unavailable"; readable = false; }
            }
            if (shape.Columns > options.MaxColumns) { budget.NoteLimit("column-limit"); readable = false; }
            row.CellsComplete = readable;
        }
        var more = (long)options.Offset + _records.Count < shape.Rows;
        if (more) budget.NoteLimit("page-limit");
        if (!budget.TryReadNode("GridPattern.dimensionsAfter", () => source.Dimensions(root), out var after)) return;
        if (after.Rows < 0 || after.Columns < 0) { InvalidShape("GridPattern.dimensionsAfter"); return; }
        _after = after;
        if (after != shape) { _error = "snapshot-changed"; budget.NoteLimit("snapshot-changed"); return; }
        _hasMore = more;
        _ended = !more;
    }

    private void InvalidShape(string stage = "GridPattern.dimensions") => budget.NoteError(stage, "inspection-provider-error", "GridPattern reported negative dimensions.");

    private bool CanConcludeEnd => budget.Reasons.All(reason => reason is "offset" or "exposure-only") && !budget.HasErrors;

    private void Exposed(TNode root)
    {
        using var nodes = Enumerate(root).GetEnumerator();
        long encountered = 0;
        while (nodes.MoveNext())
        {
            var candidate = nodes.Current;
            encountered++;
            if (candidate.Index < options.Offset) continue;
            if (_records.Count == options.Limit)
            { _hasMore = true; budget.NoteLimit("page-limit"); return; }
            var record = new PageRecord(candidate.Index) { Path = candidate.Path, Depth = candidate.Depth, ParentIndex = candidate.ParentIndex };
            _records.Add(record);
            var stage = tree ? $"items[{candidate.Index}]" : $"rows[{candidate.Index}]";
            if (budget.TryReadNode(stage, () => source.Snapshot(candidate.Node, stage), out var element)) record.Element = element;
            if (!tree) ReadChildren(candidate.Node, record, stage);
        }
        if (CanConcludeEnd) { _hasMore = false; _ended = true; _total = encountered; }
    }

    private void ReadChildren(TNode node, PageRecord row, string stage)
    {
        row.Cells = new();
        row.CellsComplete = false;
        if (!budget.TryRead(stage + ".children", () => source.First(node), out var child)) return;
        var readable = true;
        while (child is not null)
        {
            if (row.Cells.Count == options.MaxColumns) { budget.NoteLimit("column-limit"); return; }
            var current = child;
            var cell = new PageCell(row.Cells.Count);
            row.Cells.Add(cell);
            var cellStage = stage + $".cells[{cell.Index}]";
            if (budget.TryReadNode(cellStage, () => source.Snapshot(current, cellStage), out var element))
            { cell.Element = element; cell.Status = element.Complete ? "read" : "partial"; readable &= element.Complete; }
            else { cell.Status = "unavailable"; readable = false; }
            if (!budget.TryRead(cellStage + ".next", () => source.Next(current), out child)) return;
        }
        row.CellsComplete = readable;
        row.ExposedCellCount = row.Cells.Count;
    }

    internal IEnumerable<Candidate> Enumerate(TNode root)
    {
        var frames = new Stack<Frame>();
        frames.Push(new(root, 0, Array.Empty<int>(), null));
        long index = 0;
        while (frames.TryPeek(out var frame))
        {
            if (frame.Depth >= options.MaxDepth)
            {
                if (!budget.TryRead("depth-probe", () => source.First(frame.Node), out var beyond)) yield break;
                if (beyond is not null) budget.NoteLimit("max-depth");
                frames.Pop(); continue;
            }
            var previous = frame.Last;
            if (!budget.TryRead("enumeration", () => previous is null ? source.First(frame.Node) : source.Next(previous), out var child)) yield break;
            if (child is null) { frames.Pop(); continue; }
            frame.Last = child;
            var path = frame.Path.Append(frame.ChildIndex++).ToArray();
            if (!budget.TryReadNode("classification", () => source.Kind(child), out var kind)) yield break;
            var matches = tree ? kind == PageNodeKind.TreeItem : kind == PageNodeKind.Row;
            long? currentIndex = matches ? index++ : null;
            if (matches) yield return new(child, currentIndex!.Value, frame.Depth + 1, path, frame.ParentIndex);
            if (tree || !matches) frames.Push(new(child, frame.Depth + 1, path, currentIndex ?? frame.ParentIndex));
        }
    }
}