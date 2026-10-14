namespace AgentDebugToolkit.Mcp;

internal static class ArgumentFormatting
{
    internal static void AppendOption(List<string> args, string flag, string value)
    {
        if (value.Length == 0 || value.StartsWith("--", StringComparison.Ordinal))
        {
            args.Add($"{flag}={value}");
        }
        else
        {
            args.Add(flag);
            args.Add(value);
        }
    }
}