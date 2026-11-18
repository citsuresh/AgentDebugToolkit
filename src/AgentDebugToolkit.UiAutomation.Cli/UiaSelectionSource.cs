using System.Windows.Automation;
using AgentDebugToolkit.Inspection;

namespace AgentDebugToolkit.UiAutomation.Cli;

internal sealed class UiaSelectionSource(Func<SelectionResolution<AutomationElement>> resolveRequested,
    SelectionOptions options, InspectionTraversal budget) : SelectionSource<AutomationElement>
{
    internal const int MaximumSelectedItems = 128;
    private readonly UiaPageSource _pages = new(budget);

    internal override SelectionResolution<AutomationElement> Resolve()
    {
        var requested = resolveRequested();
        if (requested.Node is not { } node || requested.Error is not null) return requested;
        if (!options.Row) return requested with { Identity = ReadIdentity(node) };
        return SelectionRowResolver.Resolve(node, options, _pages, budget,
            element => TreeWalker.RawViewWalker.GetParent(element),
            element =>
            {
                var pattern = Pattern<GridItemPattern>(element, GridItemPattern.Pattern);
                return pattern is null ? null : ReadIdentity(budget.Call(() => pattern.Current.ContainingGrid));
            }, ReadIdentity);
    }

    private SelectionIdentity? ReadIdentity(AutomationElement? node)
    {
        if (node is null) { budget.NoteError("identity", "selection-provider-error", "Selection element identity is unavailable."); return null; }
        var complete = true;
        var runtime = UiaPageSource.LimitIdentity(budget.Call(node.GetRuntimeId), budget, ref complete);
        var pid = budget.Call(() => node.Current.ProcessId);
        if (pid <= 0) { budget.NoteError("identity", "selection-provider-error", "Provider process identity is unavailable."); return null; }
        return complete && runtime is not null ? new(pid, runtime) : null;
    }

    private TPattern? Pattern<TPattern>(AutomationElement node, AutomationPattern pattern) where TPattern : class
    {
        var result = budget.Call(() => { var supported = node.TryGetCurrentPattern(pattern, out var value); return (supported, value); });
        if (!result.supported) return null;
        return result.value as TPattern ?? throw new InvalidOperationException("Supported selection pattern returned no usable object.");
    }

    internal override void Observe(AutomationElement target, SelectionObservation observation, string stage)
    {
        observation.Target = new();
        _pages.Snapshot(target, stage + ".target", observation.Target);
        if (!observation.Target.Complete) return;
        if (observation.Target.State?.SelectionItemSupported != true)
        { observation.Error = "unsupported-selection"; return; }
        var pattern = Pattern<SelectionItemPattern>(target, SelectionItemPattern.Pattern);
        if (pattern is null) { observation.Error = "selection-changed"; return; }
        observation.IsSelected = budget.Call(() => pattern.Current.IsSelected);
        if (observation.IsSelected != observation.Target.State.IsSelected)
        { observation.Error = "selection-changed"; return; }
        observation.Replace = pattern.Select;
        var container = budget.Call(() => pattern.Current.SelectionContainer);
        if (container is null) { observation.Error = "unsupported-selection"; return; }
        observation.Container = ReadIdentity(container);
        if (observation.Container is null) return;
        var selection = Pattern<SelectionPattern>(container, SelectionPattern.Pattern);
        observation.SelectionSupported = selection is not null;
        if (selection is null) { observation.Error = "unsupported-selection"; return; }
        observation.CanSelectMultiple = budget.Call(() => selection.Current.CanSelectMultiple);
        observation.IsSelectionRequired = budget.Call(() => selection.Current.IsSelectionRequired);
        var selected = budget.Call(() => selection.Current.GetSelection()) ?? throw new InvalidOperationException("SelectionPattern returned no selection array.");
        observation.ReportedSelectionCount = selected.Length;
        observation.SelectedItems = new();
        if (selected.Length > MaximumSelectedItems) { budget.NoteLimit("selection-limit"); return; }
        foreach (var element in selected)
        {
            SelectionIdentity? id = null;
            if (!budget.TryReadNode(stage + ".selectedItem", () =>
            {
                if (element is null) throw new InvalidOperationException("SelectionPattern returned an unavailable selected item.");
                id = ReadIdentity(element);
                var item = Pattern<SelectionItemPattern>(element, SelectionItemPattern.Pattern);
                if (id is null || item is null || !budget.Call(() => item.Current.IsSelected) ||
                    !SelectionIdentity.Same(ReadIdentity(budget.Call(() => item.Current.SelectionContainer)), observation.Container)) return false;
                return true;
            }, out var valid) || !valid)
            { observation.Error = "selection-inconsistent"; return; }
            if (observation.SelectedItems.Any(existing => SelectionIdentity.Same(existing, id)))
            { observation.Error = "selection-inconsistent"; return; }
            observation.SelectedItems.Add(id!);
        }
        if (observation.CanSelectMultiple == false && selected.Length > 1)
        { observation.Error = "selection-inconsistent"; return; }
        observation.SelectionSetComplete = true;
        observation.Complete = true;
    }
}