using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Windows.Automation;
using AgentDebugToolkit.Inspection;

namespace AgentDebugToolkit.UiAutomation.Cli;

internal sealed record TraversalReadError(
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("message")] string Message);

internal sealed class InspectionTraversal
{
    private sealed class BudgetReachedException : Exception { }
    private sealed class Frame<TNode, TValue>(TNode node, TValue value, int depth)
        where TNode : class where TValue : class
    {
        internal TNode Node { get; } = node;
        internal TValue Value { get; } = value;
        internal int Depth { get; } = depth;
        internal TNode? LastChild { get; set; }
        internal bool Started { get; set; }
        internal int ChildCount { get; set; }
    }

    private readonly InspectionLimits _limits;
    private readonly Func<long> _elapsed;
    private readonly List<string> _reasons = new();
    private readonly List<TraversalReadError> _errors = new();
    private int _errorCount;
    private bool _errorDetailsTruncated;
    private long _finishedElapsed;
    private int? _legacyNodeLimit;
    internal int NodesVisited { get; private set; }
    internal int NodesCaptured { get; private set; }
    internal bool Truncated => _reasons.Any(reason => reason is not ("provider-error" or "unsupported-provider" or "snapshot-changed" or "exposure-only"));
    internal bool Complete => _reasons.Count == 0;
    internal bool HasErrors => _errorCount != 0;
    internal IReadOnlyList<string> Reasons => _reasons;

    internal InspectionTraversal(InspectionLimits limits, Func<long>? elapsed = null)
    {
        _limits = limits;
        var stopwatch = Stopwatch.StartNew();
        _elapsed = elapsed ?? (() => stopwatch.ElapsedMilliseconds);
    }

    internal void NoteLimit(string reason)
    {
        if (!_reasons.Contains(reason)) _reasons.Add(reason);
    }

    internal void NoteError(string stage, string error, string message)
    {
        NoteLimit("provider-error");
        _errorCount++;
        if (_errors.Count < 20)
        {
            _errors.Add(new(stage, error, message.Length > 1024 ? message[..1024] : message));
            _errorDetailsTruncated |= message.Length > 1024;
        }
        else _errorDetailsTruncated = true;
    }

    internal void CheckTime()
    {
        if (_limits.MaxDurationMs is int duration && _elapsed() >= duration)
        {
            NoteLimit("time-budget");
            throw new BudgetReachedException();
        }
    }

    internal T Call<T>(Func<T> read)
    {
        CheckTime();
        var result = read();
        CheckTime();
        return result;
    }

    internal bool TryRead<T>(string stage, Func<T> read, out T result)
    {
        try { result = Call(read); return true; }
        catch (Exception ex) when (ex is ElementNotAvailableException || UiaHelper.IsStateProviderError(ex))
        {
            NoteError(stage, ex is ElementNotAvailableException ? "stale-context" : "inspection-provider-error", ex.Message);
            CheckTime();
            result = default!;
            return false;
        }
    }

    internal bool TryReadNode<T>(string stage, Func<T> read, out T result)
    {
        CheckTime();
        if (_limits.MaxNodes is int nodes && NodesVisited >= nodes)
        { NoteLimit("node-budget"); throw new BudgetReachedException(); }
        NodesVisited++;
        if (!TryRead(stage, read, out result)) return false;
        NodesCaptured++;
        return true;
    }

    internal void RunBounded(Action read)
    {
        try { read(); }
        catch (BudgetReachedException) { }
        finally { _finishedElapsed = Math.Max(0, _elapsed()); }
    }

    internal TValue? Walk<TNode, TValue>(TNode root, int maxDepth, DepthLimitStats depthStats,
        Func<TNode, TValue> readNode, Action<TValue, TValue?> append,
        Func<TNode, TNode?> firstChild, Func<TNode, TNode?> nextSibling, int? maxChildren = null, int? legacyNodeLimit = null)
        where TNode : class where TValue : class
    {
        TValue? rootValue = null;
        _legacyNodeLimit = legacyNodeLimit;
        var stack = new Stack<Frame<TNode, TValue>>();
        bool Visit(TNode node, TValue? parent, int depth)
        {
            CheckTime();
            if (_limits.MaxNodes is int nodes && NodesVisited >= nodes)
            {
                NoteLimit("node-budget");
                throw new BudgetReachedException();
            }
            if (legacyNodeLimit is int legacy && NodesVisited >= legacy)
            {
                NoteLimit("legacy-node-limit");
                throw new BudgetReachedException();
            }
            NodesVisited++;
            if (!TryRead("node", () => readNode(node), out var value)) return false;
            NodesCaptured++;
            append(value, parent);
            rootValue ??= value;
            stack.Push(new(node, value, depth));
            return true;
        }

        try
        {
            if (!Visit(root, null, 0)) return null;
            while (stack.TryPeek(out var frame))
            {
                if (frame.Depth >= maxDepth)
                {
                    if (TryRead("depth-probe", () => depthStats.Probe(() => Call(() => firstChild(frame.Node)) is not null), out var hasChildren))
                    {
                        if (hasChildren == true) NoteLimit("max-depth");
                        if (depthStats.Capped) NoteLimit("depth-probe-limit");
                    }
                    stack.Pop();
                    continue;
                }
                if (!TryRead("children", () => frame.Started ? nextSibling(frame.LastChild!) : firstChild(frame.Node), out var child))
                {
                    stack.Pop();
                    continue;
                }
                frame.Started = true;
                if (child is null) { stack.Pop(); continue; }
                if (maxChildren is int maximum && frame.ChildCount >= maximum)
                {
                    NoteLimit("child-limit");
                    stack.Pop();
                    continue;
                }
                frame.LastChild = child;
                frame.ChildCount++;
                Visit(child, frame.Value, frame.Depth + 1);
            }
        }
        catch (BudgetReachedException) { }
        finally { _finishedElapsed = Math.Max(0, _elapsed()); }
        return rootValue;
    }

    internal List<string> ReadTextValues(params (string Stage, Func<string?> Read)[] sources)
    {
        var values = new List<string>();
        foreach (var (stage, read) in sources)
        {
            if (!TryRead(stage, read, out var text) || string.IsNullOrWhiteSpace(text)) continue;
            if (text.Length > 2000) { NoteLimit("text-limit"); text = text[..2000]; }
            values.Add(text);
        }
        return values;
    }

    internal object Report => ReportFor("exposed-uia-tree");

    internal object ReportFor(string scope) => new
    {
        scope, complete = Complete, truncated = Truncated,
        nodesVisited = NodesVisited, nodesCaptured = NodesCaptured, elapsedMs = _finishedElapsed,
        maxNodes = _limits.MaxNodes, maxDurationMs = _limits.MaxDurationMs,
        legacyNodeLimit = _legacyNodeLimit,
        stopReasons = _reasons, errorCount = _errorCount, errors = _errors, errorsTruncated = _errorDetailsTruncated,
        hint = Complete
            ? "All exposed nodes were read; this does not establish that virtualized or inactive content was exposed."
            : "Partial observation: retry from the root with a smaller root HWND or deliberately larger budgets/depth. Filters prune output after traversal; counts cover visited nodes only, and zero matches do not prove absence. No resume token is provided. Provider calls cannot be interrupted by this budget."
    };
}