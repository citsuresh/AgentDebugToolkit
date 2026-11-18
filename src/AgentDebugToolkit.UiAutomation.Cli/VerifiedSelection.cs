using System.Text.Json.Serialization;
using System.Windows.Automation;

namespace AgentDebugToolkit.UiAutomation.Cli;

internal sealed record SelectionIdentity(
    [property: JsonPropertyName("processId")] int ProcessId,
    [property: JsonPropertyName("runtimeId")] int[] RuntimeId)
{
    internal static SelectionIdentity? From(PageElement? element) => element is { ProcessId: > 0, RuntimeId.Length: > 0 and <= 32 }
        ? new(element.ProcessId.Value, element.RuntimeId) : null;
    internal static bool Same(SelectionIdentity? left, SelectionIdentity? right) => left is not null && right is not null &&
        left.ProcessId == right.ProcessId && left.RuntimeId.SequenceEqual(right.RuntimeId);
}

internal sealed record SelectionResolution<TNode>(TNode? Node, string? Error = null, string? Message = null, object? Details = null,
    SelectionIdentity? Identity = null, SelectionIdentity? ExpectedContainer = null, SelectionIdentity? Anchor = null, GridDimensions? Dimensions = null)
    where TNode : class;

internal sealed class SelectionObservation
{
    [JsonPropertyName("target")] public PageElement? Target { get; set; }
    [JsonPropertyName("container")] public SelectionIdentity? Container { get; set; }
    [JsonPropertyName("selectionSupported")] public bool? SelectionSupported { get; set; }
    [JsonPropertyName("canSelectMultiple")] public bool? CanSelectMultiple { get; set; }
    [JsonPropertyName("isSelectionRequired")] public bool? IsSelectionRequired { get; set; }
    [JsonPropertyName("isSelected")] public bool? IsSelected { get; set; }
    [JsonPropertyName("reportedSelectionCount")] public int? ReportedSelectionCount { get; set; }
    [JsonPropertyName("selectedItems")] public List<SelectionIdentity>? SelectedItems { get; set; }
    [JsonPropertyName("selectionSetComplete")] public bool SelectionSetComplete { get; set; }
    [JsonPropertyName("complete")] public bool Complete { get; set; }
    [JsonIgnore] internal Action? Replace { get; set; }
    [JsonIgnore] internal string? Error { get; set; }
}

internal abstract class SelectionSource<TNode> where TNode : class
{
    internal abstract SelectionResolution<TNode> Resolve();
    internal abstract void Observe(TNode target, SelectionObservation observation, string stage);
}

internal sealed class VerifiedSelection<TNode>(SelectionSource<TNode> source, InspectionTraversal budget) where TNode : class
{
    private SelectionObservation? _before;
    private SelectionObservation? _preAction;
    private SelectionObservation? _after;
    private bool _attempted;
    private bool _verified;
    private bool? _postcondition;
    private string? _error;
    private object? _details;
    private string _stage = "before";
    private string? _actionError;
    private object? _beforeResolution;
    private object? _preActionResolution;
    private object? _afterResolution;

    internal string? Error => _error ?? (_verified ? null : budget.HasErrors ? "selection-provider-error" : "selection-incomplete");

    internal object Run(object? request = null)
    {
        budget.RunBounded(() =>
        {
            var first = Resolve();
            if (first?.Node is null) return;
            _before = new();
            if (!Observe(first, _before)) return;
            _stage = "preAction";
            var fresh = Resolve();
            if (fresh?.Node is null || !SameResolution(first, fresh))
            { _error ??= "selection-changed"; return; }
            _preAction = new();
            if (!Observe(fresh, _preAction)) return;
            if (!SameSelection(_before, _preAction)) { _error = "selection-changed"; return; }
            if (!Desired(_preAction))
            {
                budget.CheckTime();
                _stage = "action";
                _attempted = true;
                try { _preAction.Replace!(); }
                catch (Exception ex) when (ex is ElementNotAvailableException || UiaHelper.IsStateProviderError(ex))
                {
                    _actionError = ex is ElementNotAvailableException ? "stale-context" : "selection-action-failed";
                    budget.NoteError("action", _actionError, ex.Message);
                }
            }
            _stage = "after";
            var afterResolution = Resolve();
            if (afterResolution?.Node is null || !SameResolution(first, afterResolution))
            { _error ??= "selection-changed"; return; }
            _after = new();
            if (!Observe(afterResolution, _after)) return;
            if (!SelectionIdentity.Same(_preAction.Container, _after.Container))
            { _error = "selection-container-changed"; return; }
            _postcondition = Desired(_after);
            if (_postcondition != true) { _error = "selection-verification-failed"; return; }
            if (_actionError is not null) { _error = _actionError; return; }
            _verified = true;
        });
        bool? changed = _before?.Complete == true && _after?.Complete == true &&
            _before.SelectionSetComplete && _after.SelectionSetComplete &&
            SelectionIdentity.Same(_before.Container, _after.Container) ? !SameSelectedSet(_before, _after) : null;
        return new
        {
            request, mode = "replace", atomic = false, verified = _verified, stage = _stage,
            actionAttempted = _attempted, noOp = _verified && !_attempted,
            postconditionSatisfied = _postcondition, selectionChanged = changed,
            mutationOutcome = !_attempted ? "not-attempted" : changed is true ? "selection-change-observed" : changed is false ? "no-net-selection-change-observed" : "unknown",
            partialMutationPossible = _attempted && !_verified,
            before = _before, preAction = _preAction, after = _after, actionError = _actionError, details = _details,
            beforeResolution = _beforeResolution, preActionResolution = _preActionResolution, afterResolution = _afterResolution,
            traversal = budget.ReportFor("selection-observations"),
            hint = "Sequential UIA replacement observations, not an atomic transaction. Success requires target IsSelected and the same container's full reported selection set containing only that target. Provider focus preservation and persistent cross-call runtime identities are not guaranteed. On actionAttempted failure, inspect state before retrying; no rollback or automatic retry is performed."
        };
    }

    private SelectionResolution<TNode>? Resolve()
    {
        if (!budget.TryReadNode(_stage + ".resolve", source.Resolve, out var resolution)) return null;
        var proof = new { target = resolution.Identity, expectedContainer = resolution.ExpectedContainer,
            coordinateCell = resolution.Anchor, rows = resolution.Dimensions?.Rows, columns = resolution.Dimensions?.Columns };
        if (_stage == "before") _beforeResolution = proof;
        else if (_stage == "preAction") _preActionResolution = proof;
        else _afterResolution = proof;
        if (resolution.Error is not null || resolution.Node is null)
        { _error = resolution.Error ?? "element-not-found"; _details = new { resolution.Message, resolution.Details }; return null; }
        return resolution;
    }

    private bool Observe(SelectionResolution<TNode> resolution, SelectionObservation observation)
    {
        if (!budget.TryReadNode(_stage + ".state", () => { source.Observe(resolution.Node!, observation, _stage); return true; }, out _)) return false;
        if (observation.Error is not null) { _error = observation.Error; return false; }
        if (observation.Target?.State?.IsEnabled == false)
        { _error = "element-disabled"; return false; }
        if (observation.Target?.State?.SelectionItemSupported == false || observation.SelectionSupported == false)
        { _error = "unsupported-selection"; return false; }
        if (!observation.Complete || observation.Target?.Complete != true || !observation.SelectionSetComplete || observation.IsSelected is null ||
            observation.Container is null || observation.CanSelectMultiple is null || observation.IsSelectionRequired is null ||
            observation.Target.State?.IsEnabled != true || observation.Target.State.SelectionItemSupported != true || observation.SelectionSupported != true)
        { _error = budget.HasErrors ? "selection-provider-error" : "selection-incomplete"; return false; }
        if (observation.Replace is null) { _error = "unsupported-selection"; return false; }
        if (resolution.Identity is null || SelectionIdentity.From(observation.Target) is null)
        { _error = budget.HasErrors ? "selection-provider-error" : "selection-incomplete"; return false; }
        if (!SelectionIdentity.Same(resolution.Identity, SelectionIdentity.From(observation.Target)))
        { _error = "selection-changed"; return false; }
        if (resolution.ExpectedContainer is not null && !SelectionIdentity.Same(resolution.ExpectedContainer, observation.Container))
        { _error = "selection-container-mismatch"; return false; }
        var selected = observation.SelectedItems!;
        if (selected is null || selected.Count > UiaSelectionSource.MaximumSelectedItems ||
            selected.Where((identity, index) => selected.Take(index).Any(other => SelectionIdentity.Same(identity, other))).Any() ||
            observation.CanSelectMultiple == false && selected.Count > 1)
        { _error = "selection-inconsistent"; return false; }
        var includes = selected.Any(identity => SelectionIdentity.Same(identity, resolution.Identity));
        if (includes != observation.IsSelected || observation.ReportedSelectionCount != selected.Count)
        { _error = "selection-inconsistent"; return false; }
        return true;
    }

    private static bool SameResolution(SelectionResolution<TNode> first, SelectionResolution<TNode> next) =>
        SelectionIdentity.Same(first.Identity, next.Identity) &&
        (first.ExpectedContainer is null && next.ExpectedContainer is null || SelectionIdentity.Same(first.ExpectedContainer, next.ExpectedContainer)) &&
        (first.Anchor is null && next.Anchor is null || SelectionIdentity.Same(first.Anchor, next.Anchor)) && first.Dimensions == next.Dimensions;

    private static bool SameSelection(SelectionObservation first, SelectionObservation second) =>
        SelectionIdentity.Same(first.Container, second.Container) && first.IsSelected == second.IsSelected &&
        first.CanSelectMultiple == second.CanSelectMultiple && first.IsSelectionRequired == second.IsSelectionRequired &&
        SameSelectedSet(first, second);

    private static bool SameSelectedSet(SelectionObservation first, SelectionObservation second) =>
        first.SelectedItems?.Count == second.SelectedItems?.Count && first.SelectedItems is not null && second.SelectedItems is not null &&
        first.SelectedItems.All(identity => second.SelectedItems.Any(other => SelectionIdentity.Same(identity, other)));

    private static bool Desired(SelectionObservation observation) => observation.IsSelected == true && observation.SelectedItems is { Count: 1 } &&
        SelectionIdentity.Same(observation.SelectedItems[0], SelectionIdentity.From(observation.Target));
}