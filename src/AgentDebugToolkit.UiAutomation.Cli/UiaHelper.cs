using System.Windows.Automation;
using AgentDebugToolkit.Core.Models;

namespace AgentDebugToolkit.UiAutomation.Cli;

/// <summary>
/// Wraps System.Windows.Automation calls used by the CLI verbs. Kept deliberately simple for
/// Phase 1: only the "Name" selector strategy, no map awareness (see docs/ARCHITECTURE.md).
/// </summary>
internal static class UiaHelper
{
    public static List<WindowInfo> ListTopLevelWindows(int pid)
    {
        var result = new List<WindowInfo>();
        var cond = new PropertyCondition(AutomationElement.ProcessIdProperty, pid);
        var root = AutomationElement.RootElement;
        var foreground = NativeMethods.GetForegroundWindow();

        AutomationElementCollection windows;
        try
        {
            windows = root.FindAll(TreeScope.Children, cond);
        }
        catch
        {
            return result;
        }

        foreach (AutomationElement win in windows)
        {
            result.Add(ToWindowInfo(win, foreground));
        }

        return result;
    }

    private static WindowInfo ToWindowInfo(AutomationElement el, IntPtr foreground)
    {
        var hwnd = (IntPtr)el.Current.NativeWindowHandle;
        var ownerHwnd = NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER);
        var r = el.Current.BoundingRectangle;
        return new WindowInfo
        {
            Hwnd = $"0x{hwnd.ToInt64():X}",
            Title = el.Current.Name,
            ClassName = el.Current.ClassName,
            Pid = el.Current.ProcessId,
            OwnerHwnd = ownerHwnd != IntPtr.Zero ? $"0x{ownerHwnd.ToInt64():X}" : null,
            IsModal = ownerHwnd != IntPtr.Zero,
            IsForeground = hwnd == foreground,
            BoundingRect = new Rect { X = r.X, Y = r.Y, Width = r.Width, Height = r.Height }
        };
    }

    public static AutomationElement? FindWindowByHwnd(string hwndText)
    {
        var hwnd = ParseHwnd(hwndText);
        try
        {
            return AutomationElement.FromHandle(hwnd);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Cheap liveness check for a previously-resolved window handle (used by polling loops that
    /// cache a scope across ticks, e.g. wait-for-element's "disappeared"/"appeared" checks),
    /// without re-walking the desktop window tree.
    /// </summary>
    public static bool IsWindowAlive(IntPtr hwnd)
    {
        try
        {
            var element = AutomationElement.FromHandle(hwnd);
            _ = element.Current.ProcessId;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static IntPtr ParseHwnd(string hwndText)
    {
        var text = hwndText.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }
        return new IntPtr(Convert.ToInt64(text, 16));
    }

    public static AutomationElement? ResolveSelector(AutomationElement scope, Selector selector)
    {
        return selector.Strategy switch
        {
            SelectorStrategy.Name => scope.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, selector.Value)),

            SelectorStrategy.AutomationId => scope.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, selector.Value)),

            // NameRegex / ControlTypeIndex / Coordinates arrive in Phase 3; not implemented yet.
            _ => throw new NotSupportedException(
                $"Selector strategy '{selector.Strategy}' is not implemented in Phase 1.")
        };
    }

    public static ElementInfo ToElementInfo(AutomationElement el, bool includeChildren, int maxDepth, int depth = 0)
    {
        var r = el.Current.BoundingRectangle;
        var info = new ElementInfo
        {
            Name = el.Current.Name ?? string.Empty,
            ControlType = el.Current.ControlType?.ProgrammaticName ?? string.Empty,
            ClassName = el.Current.ClassName ?? string.Empty,
            AutomationId = el.Current.AutomationId ?? string.Empty,
            IsEnabled = el.Current.IsEnabled,
            IsOffscreen = el.Current.IsOffscreen,
            BoundingRect = new Rect { X = r.X, Y = r.Y, Width = r.Width, Height = r.Height },
            SupportedPatterns = el.GetSupportedPatterns()
                .Select(p => p.ProgrammaticName)
                .ToList()
        };

        if (includeChildren && depth < maxDepth)
        {
            AutomationElementCollection children;
            try
            {
                children = el.FindAll(TreeScope.Children, Condition.TrueCondition);
            }
            catch
            {
                return info;
            }

            foreach (AutomationElement child in children)
            {
                info.Children.Add(ToElementInfo(child, includeChildren, maxDepth, depth + 1));
            }
        }

        return info;
    }

    /// <summary>
    /// Attempts InvokePattern/TogglePattern first; returns the method actually used.
    /// Falls back to a synthetic click at the element's bounding-rect center (see
    /// docs/VALIDATION_FINDINGS.md - most elements in the validated target app support no
    /// patterns at all).
    /// </summary>
    public static string Click(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invokeObj)
            && invokeObj is InvokePattern invoke)
        {
            invoke.Invoke();
            return "pattern";
        }

        if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var toggleObj)
            && toggleObj is TogglePattern toggle)
        {
            toggle.Toggle();
            return "pattern";
        }

        var r = element.Current.BoundingRectangle;
        var cx = (int)(r.X + r.Width / 2);
        var cy = (int)(r.Y + r.Height / 2);
        NativeMethods.Click(cx, cy);
        return "synthetic-click";
    }

    /// <summary>
    /// Attempts ValuePattern first; falls back to click-to-focus + synthetic keyboard input.
    /// </summary>
    public static string Type(AutomationElement element, string text)
    {
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObj)
            && valueObj is ValuePattern value
            && !(bool)element.GetCurrentPropertyValue(ValuePattern.IsReadOnlyProperty))
        {
            value.SetValue(text);
            return "pattern";
        }

        var r = element.Current.BoundingRectangle;
        var cx = (int)(r.X + r.Width / 2);
        var cy = (int)(r.Y + r.Height / 2);
        NativeMethods.Click(cx, cy);
        Thread.Sleep(100);
        NativeMethods.SendText(text);
        return "synthetic-keyboard";
    }

    public static string? GetText(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObj)
            && valueObj is ValuePattern value)
        {
            return value.Current.Value;
        }

        return element.Current.Name;
    }
}
