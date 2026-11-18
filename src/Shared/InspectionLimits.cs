namespace AgentDebugToolkit.Inspection;

internal sealed record InspectionLimits(int? MaxNodes, int? MaxDurationMs)
{
    internal const int NodeCeiling = 100000;
    internal const int DurationCeilingMs = 120000;
    internal const int ProcessHeadroomMs = 10000;
    internal bool IsBounded => MaxNodes is not null || MaxDurationMs is not null;
    internal int? ProcessTimeoutMs => MaxDurationMs is int duration ? duration + ProcessHeadroomMs : null;

    internal static bool TryParse(IReadOnlyDictionary<string, string> options, out InspectionLimits limits, out string? error)
    {
        limits = new(null, null);
        error = null;
        string? validationError = null;
        if (!Read("maxNodes", NodeCeiling, out var nodes) || !Read("maxDurationMs", DurationCeilingMs, out var duration))
        {
            error = validationError;
            return false;
        }
        limits = new(nodes, duration);
        if (limits.IsBounded && options.TryGetValue("maxDepth", out var text) && (!int.TryParse(text, out var depth) || depth < 0))
        {
            error = "--maxDepth must be a non-negative integer for bounded inspection.";
            return false;
        }
        return true;

        bool Read(string name, int maximum, out int? result)
        {
            result = null;
            if (!options.TryGetValue(name, out var text)) return true;
            if (!int.TryParse(text, out var value) || value <= 0 || value > maximum)
            {
                validationError = $"--{name} must be an integer from 1 to {maximum}.";
                return false;
            }
            result = value;
            return true;
        }
    }
}