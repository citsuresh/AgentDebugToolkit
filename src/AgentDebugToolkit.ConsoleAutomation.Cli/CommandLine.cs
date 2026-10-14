namespace AgentDebugToolkit.ConsoleAutomation.Cli;

internal static class CommandLine
{
    public static bool TryParseOptions(string[] args, out Dictionary<string, string> options, out string? error)
    {
        options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        error = null;
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var equalsIndex = argument.IndexOf('=');
            string key;
            string value;
            if (equalsIndex >= 0)
            {
                key = argument[2..equalsIndex];
                value = argument[(equalsIndex + 1)..];
            }
            else
            {
                key = argument[2..];
                if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    value = args[++index];
                }
                else if (key.Equals("regex", StringComparison.OrdinalIgnoreCase))
                {
                    value = "true";
                }
                else
                {
                    error = $"--{key} requires a value. Some shells (Windows PowerShell 5.1) drop empty-string arguments; use --{key}=.";
                    return false;
                }
            }

            options[key] = value;
        }

        return true;
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
