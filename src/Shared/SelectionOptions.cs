namespace AgentDebugToolkit.Inspection;

internal sealed record SelectionOptions(bool Row, string? RowKind, PageReadOptions Bounds)
{
    internal static bool TryParse(IReadOnlyDictionary<string, string> options, bool row,
        out SelectionOptions? result, out string? error)
    {
        result = null;
        error = null;
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "hwnd", "strategy", "value", "controlType", "scopeHwnd", "mode", "maxNodes", "maxDurationMs" };
        if (row) allowed.UnionWith(new[] { "row", "rowKind", "maxDepth" });
        foreach (var key in options.Keys)
            if (!allowed.Contains(key)) { error = $"Unknown or conflicting selection option --{key}."; return false; }
        if (!options.TryGetValue("mode", out var mode) || !mode.Equals("replace", StringComparison.OrdinalIgnoreCase))
        { error = "--mode replace is required; additive/removal selection is not supported."; return false; }
        if (!row && (!options.ContainsKey("strategy") || !options.ContainsKey("value")))
        { error = "select-item requires --strategy and --value."; return false; }
        var ordinal = "0";
        string? kind = null;
        if (row)
        {
            if (!options.TryGetValue("row", out ordinal)) { error = "--row is required."; return false; }
            if (!int.TryParse(ordinal, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
            { error = "--row must be a zero-based integer from 0 through 2147483647."; return false; }
            if (!options.TryGetValue("rowKind", out kind) || kind.ToLowerInvariant() is not ("logical" or "exposed"))
            { error = "--rowKind must explicitly be logical or exposed."; return false; }
            kind = kind.ToLowerInvariant();
        }
        var page = new Dictionary<string, string>(options, StringComparer.OrdinalIgnoreCase);
        page.Remove("mode"); page.Remove("row"); page.Remove("rowKind");
        page["offset"] = ordinal;
        page["limit"] = "1";
        if (!PageReadOptions.TryParse(page, false, out var bounds, out error)) return false;
        result = new(row, kind, bounds!);
        return true;
    }
}