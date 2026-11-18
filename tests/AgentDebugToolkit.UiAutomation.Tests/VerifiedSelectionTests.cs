using System.Runtime.InteropServices;
using System.Text.Json;
using AgentDebugToolkit.Core.Models;
using AgentDebugToolkit.Inspection;
using AgentDebugToolkit.UiAutomation.Cli;
using Xunit;

namespace AgentDebugToolkit.UiAutomation.Tests;

public sealed class VerifiedSelectionTests
{
    private static SelectionIdentity Id(int id) => new(42, new[] { 42, id });
    private sealed class Source : SelectionSource<string>
    {
        internal List<SelectionIdentity> Selected = new() { Id(2) };
        internal SelectionIdentity Container = Id(10);
        internal int Resolves, Selects;
        internal bool? Enabled = true, Supported = true;
        internal bool Complete = true, Virtualized;
        internal Action<int>? OnResolve;
        internal Action<string, SelectionObservation>? OnObserve;
        internal Action? OnSelect;
        internal Func<int, SelectionResolution<string>>? Resolution;
        internal override SelectionResolution<string> Resolve()
        {
            Resolves++;
            OnResolve?.Invoke(Resolves);
            return Resolution?.Invoke(Resolves) ?? new("target", Identity: Id(1));
        }
        internal override void Observe(string target, SelectionObservation observation, string stage)
        {
            var selected = Selected.Any(id => SelectionIdentity.Same(id, Id(1)));
            observation.Target = new() { ProcessId = 42, RuntimeId = Id(1).RuntimeId, Complete = Complete,
                ControlType = "ControlType.ListItem", VirtualizedItemSupported = Virtualized,
                State = new ElementStateInfo { IsEnabled = Enabled, SelectionItemSupported = Supported, IsSelected = selected } };
            observation.Container = Container; observation.SelectionSupported = true;
            observation.CanSelectMultiple = true; observation.IsSelectionRequired = false;
            observation.IsSelected = selected; observation.ReportedSelectionCount = Selected.Count;
            observation.SelectedItems = Selected.ToList(); observation.SelectionSetComplete = Complete; observation.Complete = Complete;
            observation.Replace = () => { Selects++; if (OnSelect is null) Selected = new() { Id(1) }; else OnSelect(); };
            OnObserve?.Invoke(stage, observation);
        }
    }

    private static (JsonElement Json, string? Error) Run(Source source, int nodes = 5000, Func<long>? elapsed = null)
    {
        var engine = new VerifiedSelection<string>(source, new(new(nodes, 10000), elapsed ?? (() => 0)));
        var payload = JsonSerializer.SerializeToElement(engine.Run());
        return (payload, engine.Error);
    }

    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(false, true, 1)]
    [InlineData(true, false, 0)]
    [InlineData(true, true, 1)]
    public void Replacement_RequiresTheFullSingletonSetAndExactlyZeroOrOneSelect(bool targetSelected, bool others, int calls)
    {
        var source = new Source { Selected = targetSelected ? new() { Id(1) } : new() };
        if (others) source.Selected.Add(Id(2));
        var (json, error) = Run(source);
        Assert.Null(error); Assert.True(json.GetProperty("verified").GetBoolean());
        Assert.Equal(calls, source.Selects); Assert.Equal(3, source.Resolves);
        Assert.Equal(calls == 0, json.GetProperty("noOp").GetBoolean());
        Assert.Equal(calls != 0, json.GetProperty("actionAttempted").GetBoolean());
        Assert.True(json.GetProperty("postconditionSatisfied").GetBoolean());
        Assert.Single(source.Selected); Assert.True(SelectionIdentity.Same(Id(1), source.Selected[0]));
        Assert.False(json.GetProperty("atomic").GetBoolean());
        Assert.Single(json.GetProperty("after").GetProperty("selectedItems").EnumerateArray());
    }

    [Theory]
    [InlineData("disabled", "element-disabled")]
    [InlineData("unknown-enabled", "selection-incomplete")]
    [InlineData("unsupported-item", "unsupported-selection")]
    [InlineData("unsupported-container", "unsupported-selection")]
    [InlineData("unknown-support", "selection-incomplete")]
    [InlineData("no-action", "unsupported-selection")]
    [InlineData("incomplete-set", "selection-incomplete")]
    [InlineData("missing-set", "selection-inconsistent")]
    [InlineData("missing-container", "selection-incomplete")]
    [InlineData("unknown-multiple", "selection-incomplete")]
    [InlineData("inconsistent-flag", "selection-inconsistent")]
    [InlineData("inconsistent-count", "selection-inconsistent")]
    [InlineData("wrong-container", "selection-container-mismatch")]
    [InlineData("wrong-identity", "selection-changed")]
    public void RejectedPreconditionsNeverSelect(string failure, string code)
    {
        var source = new Source();
        if (failure == "disabled") source.Enabled = false;
        if (failure == "unknown-enabled") source.Enabled = null;
        if (failure == "unsupported-item") source.Supported = false;
        if (failure == "unknown-support") source.Supported = null;
        if (failure == "wrong-container") source.Resolution = _ => new("target", Identity: Id(1), ExpectedContainer: Id(99));
        if (failure == "wrong-identity") source.Resolution = _ => new("target", Identity: Id(99));
        source.OnObserve = (_, observation) =>
        {
            switch (failure)
            {
                case "unsupported-container": observation.SelectionSupported = false; break;
                case "no-action": observation.Replace = null; break;
                case "incomplete-set": observation.SelectionSetComplete = false; break;
                case "missing-set": observation.SelectedItems = null; break;
                case "missing-container": observation.Container = null; break;
                case "unknown-multiple": observation.CanSelectMultiple = null; break;
                case "inconsistent-flag": observation.IsSelected = true; break;
                case "inconsistent-count": observation.ReportedSelectionCount = 0; break;
            }
        };
        var (json, error) = Run(source);
        Assert.Equal(code, error); Assert.Equal(0, source.Selects); Assert.Equal(1, source.Resolves);
        Assert.False(json.GetProperty("actionAttempted").GetBoolean()); Assert.False(json.GetProperty("verified").GetBoolean());
        Assert.False(json.GetProperty("partialMutationPossible").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IncompleteOrVirtualizedPlaceholdersNeverAct(bool virtualized)
    {
        var source = new Source { Complete = false, Virtualized = virtualized };
        var (json, error) = Run(source);
        Assert.Equal("selection-incomplete", error); Assert.Equal(0, source.Selects);
        Assert.Equal(virtualized, json.GetProperty("before").GetProperty("target").GetProperty("virtualizedItemSupported").GetBoolean());
    }

    [Theory]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public void FullSelectionSetCapIsExact(int count, bool supported)
    {
        var source = new Source { Selected = Enumerable.Range(1, count).Select(Id).ToList() };
        var (_, error) = Run(source);
        Assert.Equal(supported ? null : "selection-inconsistent", error); Assert.Equal(supported ? 1 : 0, source.Selects);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DuplicateOrContradictorySingleSelectSetsNeverAct(bool duplicate)
    {
        var source = new Source { Selected = new() { Id(1), Id(duplicate ? 1 : 2) } };
        source.OnObserve = (_, observation) => observation.CanSelectMultiple = duplicate;
        Assert.Equal("selection-inconsistent", Run(source).Error); Assert.Equal(0, source.Selects);
    }

    [Theory]
    [InlineData("target")]
    [InlineData("root")]
    [InlineData("anchor")]
    [InlineData("dimensions")]
    public void FreshResolutionRacesFailBeforeActing(string changed)
    {
        var source = new Source { Resolution = read => new("target", Identity: Id(read > 1 && changed == "target" ? 9 : 1),
            ExpectedContainer: Id(read > 1 && changed == "root" ? 99 : 10),
            Anchor: Id(read > 1 && changed == "anchor" ? 99 : 50), Dimensions: new(read > 1 && changed == "dimensions" ? 2 : 1, 1)) };
        Assert.Equal("selection-changed", Run(source).Error); Assert.Equal(0, source.Selects); Assert.Equal(2, source.Resolves);
    }

    [Theory]
    [InlineData("set")]
    [InlineData("container")]
    [InlineData("capability")]
    public void PreActionStateChangesAreNotSilentlyAccepted(string change)
    {
        var source = new Source();
        source.OnObserve = (stage, observation) =>
        {
            if (stage != "preAction") return;
            if (change == "set") { observation.SelectedItems = new(); observation.ReportedSelectionCount = 0; }
            if (change == "container") observation.Container = Id(99);
            if (change == "capability") observation.IsSelectionRequired = true;
        };
        Assert.Equal("selection-changed", Run(source).Error); Assert.Equal(0, source.Selects);
    }

    [Theory]
    [InlineData(1, 0, "ambiguous-match")]
    [InlineData(2, 0, "ambiguous-match")]
    [InlineData(3, 1, "ambiguous-match")]
    [InlineData(1, 0, "window-gone")]
    [InlineData(2, 0, "window-gone")]
    [InlineData(3, 1, "window-gone")]
    [InlineData(1, 0, "window-not-responding")]
    [InlineData(2, 0, "window-not-responding")]
    [InlineData(3, 1, "window-not-responding")]
    public void FreshResolutionErrorsRetainTheirCodesAndAttemptStatus(int failAt, int calls, string code)
    {
        var source = new Source { Resolution = read => read == failAt ? new(null, code, "Resolution evidence") : new("target", Identity: Id(1)) };
        var (json, error) = Run(source);
        Assert.Equal(code, error); Assert.Equal(calls, source.Selects); Assert.Equal(failAt, source.Resolves);
        Assert.Equal(calls == 1, json.GetProperty("partialMutationPossible").GetBoolean());
    }

    [Theory]
    [InlineData("before", 0)]
    [InlineData("preAction", 0)]
    [InlineData("after", 1)]
    public void ProviderFailuresRetainKnownFieldsAndAttemptStatus(string stage, int calls)
    {
        var source = new Source { OnObserve = (current, _) => { if (current == stage) throw new COMException("provider evidence"); } };
        var (json, error) = Run(source);
        Assert.Equal("selection-provider-error", error); Assert.Equal(calls, source.Selects);
        Assert.Equal(calls == 1, json.GetProperty("actionAttempted").GetBoolean());
        Assert.False(json.GetProperty("verified").GetBoolean());
        Assert.Equal("provider evidence", json.GetProperty("traversal").GetProperty("errors")[0].GetProperty("message").GetString());
        Assert.Equal(42, json.GetProperty(stage).GetProperty("target").GetProperty("processId").GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThrowingSelectIsNeverRetriedOrReportedAsSuccessEvenIfItChangedState(bool changesFirst)
    {
        var source = new Source();
        source.OnSelect = () => { if (changesFirst) source.Selected = new() { Id(1) }; throw new COMException("action evidence"); };
        var (json, error) = Run(source);
        Assert.Equal(changesFirst ? "selection-action-failed" : "selection-verification-failed", error);
        Assert.Equal(1, source.Selects); Assert.Equal(3, source.Resolves);
        Assert.Equal(changesFirst, json.GetProperty("postconditionSatisfied").GetBoolean());
        Assert.Equal(changesFirst, json.GetProperty("selectionChanged").GetBoolean());
        Assert.True(json.GetProperty("partialMutationPossible").GetBoolean()); Assert.False(json.GetProperty("verified").GetBoolean());
        Assert.Equal("selection-action-failed", json.GetProperty("actionError").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WrongPostStateIncludingTargetPlusOtherItemsFails(bool targetAlsoSelected)
    {
        var source = new Source(); source.OnSelect = () => { if (targetAlsoSelected) source.Selected.Add(Id(1)); };
        var (json, error) = Run(source);
        Assert.Equal("selection-verification-failed", error); Assert.Equal(1, source.Selects);
        Assert.False(json.GetProperty("postconditionSatisfied").GetBoolean()); Assert.True(json.GetProperty("partialMutationPossible").GetBoolean());
    }

    [Fact]
    public void ChangedContainerAfterMutationCannotVerifyAnItemInADifferentSelectionScope()
    {
        var source = new Source(); source.OnSelect = () => { source.Selected = new() { Id(1) }; source.Container = Id(99); };
        var (json, error) = Run(source);
        Assert.Equal("selection-container-changed", error); Assert.Equal(1, source.Selects);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("postconditionSatisfied").ValueKind);
        Assert.Equal(99, json.GetProperty("after").GetProperty("container").GetProperty("runtimeId")[1].GetInt32());
    }

    [Fact]
    public void NoOpStillRequiresFreshPostVerification()
    {
        var source = new Source { Selected = new() { Id(1) }, OnResolve = _ => { } };
        source.OnResolve = read => { if (read == 3) source.Selected = new() { Id(2) }; };
        var (json, error) = Run(source);
        Assert.Equal("selection-verification-failed", error); Assert.Equal(0, source.Selects); Assert.Equal(3, source.Resolves);
        Assert.False(json.GetProperty("noOp").GetBoolean()); Assert.False(json.GetProperty("partialMutationPossible").GetBoolean());
    }

    [Theory]
    [InlineData(3, 0, false)]
    [InlineData(4, 1, false)]
    [InlineData(5, 1, false)]
    [InlineData(6, 1, true)]
    public void ExactNodeBudgetIncludesFreshResolutionAndEveryObservation(int nodes, int actions, bool verified)
    {
        var source = new Source(); var (json, error) = Run(source, nodes);
        Assert.Equal(actions, source.Selects); Assert.Equal(verified, json.GetProperty("verified").GetBoolean());
        Assert.Equal(verified, error is null); Assert.Equal(nodes, json.GetProperty("traversal").GetProperty("nodesVisited").GetInt32());
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void TimeBudgetNeverTurnsUnreadablePostStateIntoSuccess(bool expiresDuringAction, int calls)
    {
        long clock = expiresDuringAction ? 0 : 10000;
        var source = new Source(); source.OnSelect = () => { source.Selected = new() { Id(1) }; clock = 10000; };
        var (json, error) = Run(source, elapsed: () => clock);
        Assert.Equal("selection-incomplete", error); Assert.Equal(calls, source.Selects);
        Assert.False(json.GetProperty("verified").GetBoolean()); Assert.Equal(calls == 1, json.GetProperty("partialMutationPossible").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("postconditionSatisfied").ValueKind);
        Assert.Contains(json.GetProperty("traversal").GetProperty("stopReasons").EnumerateArray(), reason => reason.GetString() == "time-budget");
    }

    [Fact]
    public void MissingRuntimeIdentityIsUnknownNotAProvedIdentityChange()
    {
        var source = new Source { Resolution = _ => new("target") };
        Assert.Equal("selection-incomplete", Run(source).Error); Assert.Equal(0, source.Selects);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealCliEnvelopeRetainsProofAndAttemptedFailureFields(bool failure)
    {
        var source = new Source(); if (failure) source.OnSelect = () => { };
        var engine = new VerifiedSelection<string>(source, new(new(5000, 10000), () => 0));
        var result = engine.Run(new { operation = "select-item" });
        using var output = new System.IO.StringWriter(); var original = Console.Out;
        try { Console.SetOut(output); Assert.Equal(failure ? 1 : 0, Verbs.WriteSelection(result, engine.Error)); }
        finally { Console.SetOut(original); }
        using var document = JsonDocument.Parse(output.ToString()); var json = document.RootElement;
        Assert.Equal(!failure, json.GetProperty("success").GetBoolean());
        Assert.Equal(!failure, json.GetProperty("verified").GetBoolean());
        Assert.True(json.GetProperty("actionAttempted").GetBoolean()); Assert.Equal(failure, json.GetProperty("partialMutationPossible").GetBoolean());
        Assert.Equal(1, source.Selects); Assert.Equal(42, json.GetProperty("beforeResolution").GetProperty("target").GetProperty("processId").GetInt32());
        Assert.Equal("select-item", json.GetProperty("request").GetProperty("operation").GetString());
        if (failure) Assert.Equal("selection-verification-failed", json.GetProperty("error").GetString());
    }
}