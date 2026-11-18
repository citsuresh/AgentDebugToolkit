using System.Globalization;

namespace AgentDebugToolkit.Inspection;

internal sealed record PageReadOptions(int Offset, int Limit, int MaxColumns, int MaxDepth, InspectionLimits Budget)
{
    internal static bool TryParse(IReadOnlyDictionary<string, string> options, bool tree,
        out PageReadOptions? result, out string? error)
    {
        result = null;
        error = null;
        string? validationError = null;
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "hwnd", "strategy", "value", "controlType", "scopeHwnd", "offset", "limit", "maxNodes", "maxDurationMs", "maxDepth" };
        if (!tree) allowed.Add("maxColumns");
        foreach (var key in options.Keys)
            if (!allowed.Contains(key)) { error = $"Unknown or conflicting page option --{key}."; return false; }
        if (!options.TryGetValue("hwnd", out var hwnd) || string.IsNullOrWhiteSpace(hwnd))
        { error = "--hwnd is required for paged reads."; return false; }
        if (options.ContainsKey("strategy") != options.ContainsKey("value") ||
            options.ContainsKey("controlType") && !options.ContainsKey("strategy"))
        { error = "Use --strategy and --value together; --controlType requires a selector."; return false; }
        var values = new Dictionary<string, string>(options, StringComparer.OrdinalIgnoreCase);
        values.TryAdd("maxNodes", "5000");
        values.TryAdd("maxDurationMs", "10000");
        if (!InspectionLimits.TryParse(values, out var budget, out error)) return false;
        if (!Integer("offset", null, 0, int.MaxValue, out var offset) ||
            !Integer("limit", null, 1, 100, out var limit) ||
            !Integer("maxColumns", 32, 1, 64, out var columns) ||
            !Integer("maxDepth", 8, 0, 64, out var depth))
        { error = validationError; return false; }
        result = new(offset, limit, columns, depth, budget);
        return true;

        bool Integer(string name, int? fallback, int minimum, int maximum, out int value)
        {
            value = fallback ?? 0;
            if (!options.TryGetValue(name, out var text))
            {
                if (fallback is not null) return true;
                validationError = $"--{name} is required.";
                return false;
            }
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= minimum && value <= maximum) return true;
            validationError = $"--{name} must be an integer from {minimum} to {maximum}.";
            return false;
        }
    }
}