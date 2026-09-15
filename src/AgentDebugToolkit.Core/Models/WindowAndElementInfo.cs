namespace AgentDebugToolkit.Core.Models;

public class Rect
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

public class WindowInfo
{
    public string Hwnd { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string ClassName { get; set; } = string.Empty;
    public int Pid { get; set; }
    public string? OwnerHwnd { get; set; }
    public bool IsModal { get; set; }
    public bool IsForeground { get; set; }
    public Rect? BoundingRect { get; set; }
}

public class ElementInfo
{
    public string Name { get; set; } = string.Empty;
    public string ControlType { get; set; } = string.Empty;
    public string ClassName { get; set; } = string.Empty;
    public string AutomationId { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public bool IsOffscreen { get; set; }
    public Rect? BoundingRect { get; set; }
    public List<string> SupportedPatterns { get; set; } = new();
    public List<ElementInfo> Children { get; set; } = new();
}
