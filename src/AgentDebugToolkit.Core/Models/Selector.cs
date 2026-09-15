namespace AgentDebugToolkit.Core.Models;

/// <summary>
/// Identifies how to locate a UI element. See docs/CLI_CONTRACT.md for full semantics.
/// </summary>
public enum SelectorStrategy
{
    Name,
    AutomationId,
    NameRegex,
    ControlTypeIndex,
    Coordinates
}

public class Selector
{
    public SelectorStrategy Strategy { get; set; }
    public string Value { get; set; } = string.Empty;
}
