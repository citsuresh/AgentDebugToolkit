namespace AgentDebugToolkit.ConsoleAutomation.Cli;

internal static class CommandLine
{
    public static Dictionary<string, string> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = args[index][2..];
            var acceptsDashPrefixedValue = key.Equals("args", StringComparison.OrdinalIgnoreCase);
            var value = index + 1 < args.Length
                && (acceptsDashPrefixedValue
                    || !args[index + 1].StartsWith("--", StringComparison.Ordinal))
                ? args[++index]
                : "true";
            options[key] = value;
        }

        return options;
    }

    public static bool TryGetPositiveInteger(
        IReadOnlyDictionary<string, string> options,
        string name,
        int defaultValue,
        out int value)
    {
        if (!options.TryGetValue(name, out var supplied))
        {
            value = defaultValue;
            return true;
        }

        return int.TryParse(supplied, out value) && value > 0;
    }
}
