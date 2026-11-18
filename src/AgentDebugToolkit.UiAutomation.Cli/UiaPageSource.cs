using System.Windows.Automation;
using AgentDebugToolkit.Core.Models;

namespace AgentDebugToolkit.UiAutomation.Cli;

internal sealed class UiaPageSource(InspectionTraversal budget) : PageSource<AutomationElement>
{
    internal static string? LimitText(string? text, InspectionTraversal budget, ref bool complete)
    {
        if (text is null || text.Length <= 512) return text;
        complete = false; budget.NoteLimit("text-limit"); return text[..512];
    }

    internal static int[]? LimitIdentity(int[]? identity, InspectionTraversal budget, ref bool complete, string stage = "runtimeId")
    {
        if (identity is null || identity.Length == 0)
        { complete = false; budget.NoteError(stage, "inspection-provider-error", "No UIA runtime identity was returned."); return null; }
        if (identity.Length <= 32) return identity;
        complete = false; budget.NoteLimit("identity-limit"); return identity[..32];
    }

    private readonly TreeWalker _walker = TreeWalker.RawViewWalker;
    private GridPattern? _grid;

    internal override bool SupportsGrid(AutomationElement root)
    {
        if (!root.TryGetCurrentPattern(GridPattern.Pattern, out var value)) return false;
        _grid = value as GridPattern ?? throw new InvalidOperationException("Supported GridPattern returned no usable pattern.");
        return true;
    }

    internal override GridDimensions Dimensions(AutomationElement root) => new(
        budget.Call(() => _grid!.Current.RowCount), budget.Call(() => _grid!.Current.ColumnCount));

    internal override AutomationElement Cell(AutomationElement root, int row, int column) =>
        budget.Call(() => _grid!.GetItem(row, column)) ?? throw new InvalidOperationException("GridPattern returned no cell at the requested coordinate.");

    internal override PageNodeKind Kind(AutomationElement node) => Classify(node.Current.ControlType);
    internal override AutomationElement? First(AutomationElement node) => _walker.GetFirstChild(node);
    internal override AutomationElement? Next(AutomationElement node) => _walker.GetNextSibling(node);

    private static PageNodeKind Classify(ControlType type) =>
        type == ControlType.List || type == ControlType.DataGrid || type == ControlType.Table ? PageNodeKind.Rows :
        type == ControlType.DataItem || type == ControlType.ListItem ? PageNodeKind.Row :
        type == ControlType.Tree ? PageNodeKind.Tree : type == ControlType.TreeItem ? PageNodeKind.TreeItem : PageNodeKind.Other;

    internal override PageElement Snapshot(AutomationElement node, string stage)
        => Snapshot(node, stage, new PageElement());

    internal PageElement Snapshot(AutomationElement node, string stage, PageElement element)
    {
        var complete = true;
        T Read<T>(string field, Func<T> read)
        {
            if (budget.TryRead(stage + "." + field, read, out var value)) return value;
            complete = false;
            return default!;
        }
        string? Text(string? text) => LimitText(text, budget, ref complete);
        TPattern? Pattern<TPattern>(AutomationPattern pattern, string field, Action<bool?> support) where TPattern : class
        {
            if (!budget.TryRead(stage + "." + field, () =>
                { var supported = node.TryGetCurrentPattern(pattern, out var providerValue); return (Supported: supported, Value: providerValue); }, out var observed))
            { complete = false; return null; }
            support(observed.Supported);
            if (!observed.Supported) return null;
            if (observed.Value is TPattern typed) return typed;
            complete = false;
            budget.NoteError(stage + "." + field, "inspection-provider-error", "Supported pattern returned no usable pattern object.");
            return null;
        }

        if (budget.TryRead(stage + ".runtimeId", node.GetRuntimeId, out var identity))
            element.RuntimeId = LimitIdentity(identity, budget, ref complete, stage + ".runtimeId");
        else complete = false;
        element.ProcessId = Read<int?>("processId", () => node.Current.ProcessId);
        element.AutomationId = Text(Read("automationId", () => node.Current.AutomationId));
        element.Name = Text(Read("name", () => node.Current.Name));
        element.ClassName = Text(Read("className", () => node.Current.ClassName));
        var type = Read("controlType", () => node.Current.ControlType);
        element.ControlType = type?.ProgrammaticName;
        if (type is not null) element.Kind = Classify(type);
        element.IsOffscreen = Read<bool?>("isOffscreen", () => node.Current.IsOffscreen);
        element.State = budget.Call(() => UiaHelper.ReadState(node, budget.CheckTime));
        if (element.State.Errors is { } errors)
        {
            complete = false;
            foreach (var error in errors) budget.NoteError(stage + ".State." + error.Pattern, error.Error, error.Message);
            element.State.Errors = errors.Select(error => error with { Message = Text(error.Message)! }).ToList();
        }

        var valuePattern = Pattern<ValuePattern>(ValuePattern.Pattern, "ValuePattern", supported => element.ValueSupported = supported);
        if (valuePattern is not null) element.Value = Text(Read("value", () => valuePattern.Current.Value
            ?? throw new InvalidOperationException("Supported ValuePattern returned no value.")));
        var textPattern = Pattern<TextPattern>(TextPattern.Pattern, "TextPattern", supported => element.TextSupported = supported);
        if (textPattern is not null)
            element.Text = Text(Read("text", () =>
            {
                var range = budget.Call(() => textPattern.DocumentRange) ?? throw new InvalidOperationException("Supported TextPattern returned no document range.");
                return budget.Call(() => range.GetText(513));
            }));

        var gridItem = Pattern<GridItemPattern>(GridItemPattern.Pattern, "GridItemPattern", supported => element.GridItemSupported = supported);
        if (gridItem is not null)
            element.GridItem = Read("gridItem", () =>
            {
                var row = budget.Call(() => gridItem.Current.Row);
                var column = budget.Call(() => gridItem.Current.Column);
                var rowSpan = budget.Call(() => gridItem.Current.RowSpan);
                var columnSpan = budget.Call(() => gridItem.Current.ColumnSpan);
                if (row < 0 || column < 0 || rowSpan <= 0 || columnSpan <= 0)
                    throw new InvalidOperationException("GridItemPattern reported invalid coordinates or spans.");
                return new GridItemPosition(row, column, rowSpan, columnSpan);
            });
        var expand = Pattern<ExpandCollapsePattern>(ExpandCollapsePattern.Pattern, "ExpandCollapsePattern", supported => element.ExpandCollapseSupported = supported);
        if (expand is not null)
            element.ExpandCollapseState = Read("expandCollapseState", () => expand.Current.ExpandCollapseState.ToString());
        Pattern<VirtualizedItemPattern>(VirtualizedItemPattern.Pattern, "VirtualizedItemPattern", supported => element.VirtualizedItemSupported = supported);
        element.Complete = complete;
        return element;
    }
}