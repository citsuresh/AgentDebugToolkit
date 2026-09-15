using AgentDebugToolkit.ConsoleAutomation.Cli;

if (args.Length > 0 && args[0].Equals("--broker", StringComparison.OrdinalIgnoreCase))
{
    return BrokerProgram.Run(args.Skip(1).ToArray());
}

if (args.Length == 0)
{
    JsonOutput.WriteError("invalid-argument", "No verb specified.");
    return 1;
}

var options = CommandLine.ParseOptions(args.Skip(1).ToArray());
try
{
    return args[0].ToLowerInvariant() switch
    {
        "launch" => ConsoleVerbs.Launch(options),
        "read-screen" => ConsoleVerbs.ReadScreen(options),
        "send-text" => ConsoleVerbs.SendText(options),
        "send-keys" => ConsoleVerbs.SendKeys(options),
        "wait-for-text" => ConsoleVerbs.WaitForText(options),
        "is-running" => ConsoleVerbs.IsRunning(options),
        "stop" => ConsoleVerbs.Stop(options),
        _ => WriteUnknownVerb(args[0])
    };
}
catch (Exception ex)
{
    JsonOutput.WriteError("unhandled-exception", ex.Message);
    return 1;
}

static int WriteUnknownVerb(string verb)
{
    JsonOutput.WriteError("invalid-argument", $"Unknown verb '{verb}'.");
    return 1;
}
