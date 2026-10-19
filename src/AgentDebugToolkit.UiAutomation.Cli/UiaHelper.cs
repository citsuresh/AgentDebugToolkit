using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Automation;
using AgentDebugToolkit.Core.Models;

namespace AgentDebugToolkit.UiAutomation.Cli;

/// <summary>
/// Thrown when the clipboard cannot be written to after retrying (e.g. locked by another
/// process during a clipboard-paste type/submit-chat-message operation).
/// </summary>
internal sealed class ClipboardUnavailableException : Exception
{
    public ClipboardUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Wraps System.Windows.Automation calls used by the CLI verbs. Kept deliberately simple for
/// Phase 1: only the "Name" selector strategy, no map awareness (see docs/ARCHITECTURE.md).
/// </summary>
internal static class UiaHelper
{
    internal sealed record WindowQueryOptions(
        int? Pid,
        bool AllProcesses,
        string? ProcessName,
        string? TitleContains,
        string? ClassName,
        bool IncludeUntitled)
    {
        public bool IsCrossProcess => AllProcesses || ProcessName is not null;
    }

    public enum KeyFocusMode
    {
        SetFocus,
        Click,
        None,
    }

    internal sealed class GridCellResolution
    {
        public GridCellResolution(AutomationElement row, AutomationElement cell)
        {
            Row = row;
            Cell = cell;
        }

        public AutomationElement Row { get; }
        public AutomationElement Cell { get; }
    }

    public static List<WindowInfo> ListTopLevelWindows(int pid)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        var processNameCache = new Dictionary<int, string?>();
        return NativeMethods.EnumerateVisibleTopLevelWindows(pid)
            .Select(hwnd => ToWindowInfo(hwnd, pid, foreground, processNameCache))
            .ToList();
    }

    internal static List<WindowInfo> QueryTopLevelWindows(
        WindowQueryOptions options,
        Dictionary<int, string?> processNameCache)
    {
        var windows = new List<WindowInfo>();
        var foreground = NativeMethods.GetForegroundWindow();
        var candidates = options.Pid is int pid
            ? NativeMethods.EnumerateVisibleTopLevelWindows(pid).Select(hwnd => (Hwnd: hwnd, Pid: pid))
            : NativeMethods.EnumerateVisibleTopLevelWindows();

        foreach (var (hwnd, windowPid) in candidates)
        {
            if (options.IsCrossProcess && NativeMethods.IsWindowCloaked(hwnd))
            {
                continue;
            }

            var title = NativeMethods.GetWindowTitle(hwnd);
            if (options.IsCrossProcess && !options.IncludeUntitled && title.Length == 0)
            {
                continue;
            }

            if (options.TitleContains is not null
                && !title.Contains(options.TitleContains, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var className = NativeMethods.GetWindowClassName(hwnd);
            if (options.ClassName is not null
                && !className.Contains(options.ClassName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (options.IsCrossProcess && NativeMethods.GetWindowRect(hwnd, out var bounds)
                && (bounds.Right - bounds.Left <= 0 || bounds.Bottom - bounds.Top <= 0))
            {
                continue;
            }

            if (options.ProcessName is not null
                && !string.Equals(GetProcessName(windowPid, processNameCache), options.ProcessName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            windows.Add(ToWindowInfo(hwnd, windowPid, foreground, processNameCache, title, className));
        }

        return windows;
    }

    private static WindowInfo ToWindowInfo(
        IntPtr hwnd,
        int pid,
        IntPtr foreground,
        Dictionary<int, string?> processNameCache,
        string? title = null,
        string? className = null)
    {
        var ownerHwnd = NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER);
        var hasBounds = NativeMethods.GetWindowRect(hwnd, out var bounds);
        return new WindowInfo
        {
            Hwnd = $"0x{hwnd.ToInt64():X}",
            Title = title ?? NativeMethods.GetWindowTitle(hwnd),
            ClassName = className ?? NativeMethods.GetWindowClassName(hwnd),
            Pid = pid,
            ProcessName = GetProcessName(pid, processNameCache),
            OwnerHwnd = ownerHwnd != IntPtr.Zero ? $"0x{ownerHwnd.ToInt64():X}" : null,
            IsModal = ownerHwnd != IntPtr.Zero,
            IsForeground = hwnd == foreground,
            BoundingRect = hasBounds
                ? new Rect
                {
                    X = bounds.Left,
                    Y = bounds.Top,
                    Width = bounds.Right - bounds.Left,
                    Height = bounds.Bottom - bounds.Top
                }
                : null
        };
    }

    private static string? GetProcessName(int pid, Dictionary<int, string?> cache)
    {
        if (cache.TryGetValue(pid, out var processName))
        {
            return processName;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            processName = process.ProcessName;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (Win32Exception)
        {
            return null;
        }

        cache[pid] = processName;
        return processName;
    }

    /// <summary>
    /// Converts a UIA BoundingRectangle to a Rect, returning null instead of throwing/serializing
    /// invalid data when the rectangle contains non-finite values (NaN/Infinity) — which UIA can
    /// legitimately report for offscreen, virtualized, or not-yet-realized elements, and which
    /// System.Text.Json cannot serialize as a JSON number.
    /// </summary>
    internal static Rect? SafeRect(System.Windows.Rect r)
    {
        if (!IsFinite(r.X) || !IsFinite(r.Y) || !IsFinite(r.Width) || !IsFinite(r.Height))
        {
            return null;
        }

        return new Rect { X = r.X, Y = r.Y, Width = r.Width, Height = r.Height };
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

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

    // Phase 16: SelectorStrategy already declares NameRegex/ControlTypeIndex/Coordinates
    // (reserved for Phase 3), so a bare Enum.TryParse<SelectorStrategy> accepts them as valid
    // --strategy/--scopeStrategy values even though ResolveSelector/ResolveSelectorAll below
    // throw NotSupportedException for all three -- previously that propagated uncaught to
    // Program.cs's top-level handler as "unhandled-exception" instead of a clean
    // "invalid-argument". This is the single, central place every selector-consuming verb
    // parses --strategy/--scopeStrategy through, so the "currently implemented" set only needs
    // to be maintained here.
    private static readonly SelectorStrategy[] ImplementedSelectorStrategies =
    {
        SelectorStrategy.Name,
        SelectorStrategy.AutomationId,
    };

    /// <summary>
    /// Parses a --strategy/--scopeStrategy value, accepting only selector strategies that
    /// <see cref="ResolveSelector"/>/<see cref="ResolveSelectorAll"/> actually implement today
    /// (Name, AutomationId). Rejects NameRegex/ControlTypeIndex/Coordinates -- reserved for
    /// Phase 3 -- as a clean, callable-time validation failure instead of letting them reach
    /// ResolveSelector's NotSupportedException. See docs/KNOWN_OPEN_FINDINGS.md (Phase 16).
    /// </summary>
    public static bool TryParseImplementedSelectorStrategy(
        string? text, out SelectorStrategy strategy, out string error)
    {
        if (text is not null
            && Enum.TryParse(text, ignoreCase: true, out strategy)
            && ImplementedSelectorStrategies.Contains(strategy))
        {
            error = string.Empty;
            return true;
        }

        strategy = default;
        error = "must be one of the implemented values: Name, AutomationId.";
        return false;
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

            // NameRegex / ControlTypeIndex / Coordinates are not implemented yet — see
            // docs/KNOWN_OPEN_FINDINGS.md.
            _ => throw new NotSupportedException(
                $"Selector strategy '{selector.Strategy}' is not implemented.")
        };
    }

    /// <summary>
    /// Same strategy support as <see cref="ResolveSelector"/>, but returns every matching
    /// descendant (<c>FindAll</c>) instead of stopping at the first match.
    /// </summary>
    public static AutomationElementCollection ResolveSelectorAll(AutomationElement scope, Selector selector)
    {
        return selector.Strategy switch
        {
            SelectorStrategy.Name => scope.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, selector.Value)),

            SelectorStrategy.AutomationId => scope.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, selector.Value)),

            // NameRegex / ControlTypeIndex / Coordinates are not implemented yet — see
            // docs/KNOWN_OPEN_FINDINGS.md.
            _ => throw new NotSupportedException(
                $"Selector strategy '{selector.Strategy}' is not implemented.")
        };
    }

    /// <summary>
    /// Finds a DataGrid row containing a matching descendant, then selects one of its cells by the
    /// stable UIA GridItemPattern column index. The search starts from the top when the grid can
    /// scroll and re-queries after every scroll because virtualized rows can be re-realized.
    /// </summary>
    public static GridCellResolution? ResolveGridCell(
        AutomationElement grid, Selector rowSelector, int columnIndex)
    {
        if (!grid.TryGetCurrentPattern(GridPattern.Pattern, out _))
        {
            return null;
        }

        var scroll = grid.TryGetCurrentPattern(ScrollPattern.Pattern, out var scrollObject)
            ? scrollObject as ScrollPattern
            : null;

        if (scroll is null || !scroll.Current.VerticallyScrollable)
        {
            return FindGridCellInRealizedRows(grid, rowSelector, columnIndex);
        }

        return ScrollSearch(
            scroll,
            ScrollAmount.SmallIncrement,
            () => FindGridCellInRealizedRows(grid, rowSelector, columnIndex),
            result => ScrollCellIntoView(result.Cell)).Result;
    }

    internal enum ScrollSearchStop
    {
        Found,
        ReachedEnd,
        MaxScrolls,
        Timeout,
    }

    internal sealed class ScrollSearchResult<T> where T : class
    {
        public ScrollSearchResult(ScrollSearchStop stop, T? result, int steps, T? partialResult = null)
        {
            Stop = stop;
            Result = result;
            Steps = steps;
            PartialResult = partialResult;
        }

        public ScrollSearchStop Stop { get; }

        /// <summary>The re-queried result after scrollIntoView; may be null even when Stop is Found.</summary>
        public T? Result { get; }

        /// <summary>The most recent find result rejected by isSatisfied at termination; null without a predicate.</summary>
        public T? PartialResult { get; }

        public int Steps { get; }
    }

    /// <summary>
    /// Shared vertical scroll-search loop (set-grid-cell and scroll-into-view): scrolls to the top,
    /// then alternates find / scroll-by-<paramref name="step"/>, re-querying after every step
    /// because virtualized items are re-realized. On a hit it calls
    /// <paramref name="scrollIntoView"/> and returns a fresh <paramref name="find"/> result,
    /// unless <paramref name="isSatisfied"/> is supplied, in which case it keeps searching until
    /// the fresh result is non-null and satisfies that predicate. Stops
    /// when the vertical percent reaches 100 or stops advancing (ReachedEnd), and, only when
    /// supplied, at <paramref name="maxScrolls"/> steps or when <paramref name="isTimedOut"/>
    /// returns true. With <paramref name="isSatisfied"/>, three consecutive non-advancing scroll
    /// steps are required before stopping; advancing resets the counter. On end, limit, or timeout,
    /// PartialResult carries the most recent non-null find result if the predicate rejected it.
    /// Without a predicate, one non-advancing step stops the search and PartialResult stays null.
    /// </summary>
    public static ScrollSearchResult<T> ScrollSearch<T>(
        ScrollPattern scroll,
        ScrollAmount step,
        Func<T?> find,
        Action<T> scrollIntoView,
        int? maxScrolls = null,
        Func<bool>? isTimedOut = null,
        Func<T, bool>? isSatisfied = null) where T : class
    {
        const int MaxConsecutiveNonAdvancingSteps = 3;
        scroll.SetScrollPercent(ScrollPattern.NoScroll, 0);
        var steps = 0;
        var nonAdvancingSteps = 0;
        T? partialResult = null;
        while (true)
        {
            var result = find();
            if (result is not null)
            {
                scrollIntoView(result);
                var refreshed = find();
                if (isSatisfied is null || (refreshed is not null && isSatisfied(refreshed)))
                {
                    return new ScrollSearchResult<T>(ScrollSearchStop.Found, refreshed, steps);
                }

                if (refreshed is not null)
                {
                    partialResult = refreshed;
                }
            }

            var verticalPercentBeforeScroll = scroll.Current.VerticalScrollPercent;
            if (verticalPercentBeforeScroll >= 100)
            {
                return new ScrollSearchResult<T>(ScrollSearchStop.ReachedEnd, null, steps, partialResult);
            }

            if (maxScrolls is int max && steps >= max)
            {
                return new ScrollSearchResult<T>(ScrollSearchStop.MaxScrolls, null, steps, partialResult);
            }

            if (isTimedOut is not null && isTimedOut())
            {
                return new ScrollSearchResult<T>(ScrollSearchStop.Timeout, null, steps, partialResult);
            }

            scroll.Scroll(ScrollAmount.NoAmount, step);
            steps++;
            if (scroll.Current.VerticalScrollPercent <= verticalPercentBeforeScroll)
            {
                nonAdvancingSteps++;
                if (isSatisfied is null || nonAdvancingSteps >= MaxConsecutiveNonAdvancingSteps)
                {
                    return new ScrollSearchResult<T>(ScrollSearchStop.ReachedEnd, null, steps, partialResult);
                }
            }
            else
            {
                nonAdvancingSteps = 0;
            }
        }
    }

    public static ScrollPattern? GetScrollPattern(AutomationElement? element)
    {
        return element is not null
            && element.TryGetCurrentPattern(ScrollPattern.Pattern, out var scrollObject)
            ? scrollObject as ScrollPattern
            : null;
    }

    /// <summary>Vertical scroll percent, or -1 when the element has no vertically scrollable ScrollPattern.</summary>
    public static double GetVerticalScrollPercent(AutomationElement? container)
    {
        var scroll = GetScrollPattern(container);
        if (scroll is null || !scroll.Current.VerticallyScrollable)
        {
            return -1;
        }

        return Math.Round(scroll.Current.VerticalScrollPercent, 2);
    }

    /// <summary>
    /// Nearest control-view ancestor with a vertically scrollable ScrollPattern, searching up to
    /// and including <paramref name="boundary"/> (the scope element or window).
    /// </summary>
    public static AutomationElement? FindScrollableAncestor(AutomationElement element, AutomationElement boundary)
    {
        var walker = TreeWalker.ControlViewWalker;
        var current = walker.GetParent(element);
        while (current is not null)
        {
            var scroll = GetScrollPattern(current);
            if (scroll is not null && scroll.Current.VerticallyScrollable)
            {
                return current;
            }

            if (current.Equals(boundary))
            {
                return null;
            }

            current = walker.GetParent(current);
        }

        return null;
    }

    /// <summary>
    /// True when the element is on-screen and its bounding rectangle lies inside the container's
    /// bounding rectangle (or, with no container, when it is simply not offscreen).
    /// </summary>
    public static bool IsFullyVisible(AutomationElement element, AutomationElement? container)
    {
        var bounds = element.Current.BoundingRectangle;
        if (bounds.IsEmpty || element.Current.IsOffscreen)
        {
            return false;
        }

        return container is null || container.Current.BoundingRectangle.Contains(bounds);
    }

    public static bool TryScrollItemIntoView(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var scrollItemObject)
            && scrollItemObject is ScrollItemPattern scrollItem)
        {
            scrollItem.ScrollIntoView();
            return true;
        }

        return false;
    }

    internal enum AncestorScrollStop
    {
        Visible,
        ReachedEnd,
        MaxScrolls,
        Timeout,
    }

    /// <summary>
    /// Raised when a BoundingRectangle needed for a visibility decision is empty or has zero area;
    /// scroll-into-view reports it as scroll-provider-error at stage "visibility-check".
    /// </summary>
    internal sealed class EmptyBoundsException : InvalidOperationException
    {
        public EmptyBoundsException(string message)
            : base(message)
        {
        }
    }

    private static bool HasNoArea(System.Windows.Rect rect)
    {
        return rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0;
    }

    /// <summary>Throws <see cref="EmptyBoundsException"/> when the element's or container's BoundingRectangle has no area.</summary>
    public static void EnsureBoundsHaveArea(AutomationElement element, AutomationElement? container)
    {
        if (HasNoArea(element.Current.BoundingRectangle))
        {
            throw new EmptyBoundsException(
                "The element's BoundingRectangle is empty or has zero area, so its visibility cannot be determined.");
        }

        if (container is not null && HasNoArea(container.Current.BoundingRectangle))
        {
            throw new EmptyBoundsException(
                "The scroll container's BoundingRectangle is empty or has zero area, so the element's visibility cannot be determined.");
        }
    }

    /// <summary>
    /// <see cref="IsFullyVisible"/> for scroll-into-view: throws <see cref="EmptyBoundsException"/>
    /// instead of returning false when either BoundingRectangle has no area.
    /// </summary>
    public static bool IsFullyVisibleWithArea(AutomationElement element, AutomationElement? container)
    {
        EnsureBoundsHaveArea(element, container);
        return IsFullyVisible(element, container);
    }

    /// <summary>
    /// Steps the container's ScrollPattern vertically toward the element (direction from the
    /// relative bounding rectangles) until the element is fully visible.
    /// </summary>
    public static (AncestorScrollStop stop, int steps) StepAncestorUntilVisible(
        AutomationElement element,
        AutomationElement container,
        ScrollPattern scroll,
        ScrollAmount step,
        int maxScrolls,
        Func<bool> isTimedOut)
    {
        var steps = 0;
        var decrement = step == ScrollAmount.SmallIncrement
            ? ScrollAmount.SmallDecrement
            : ScrollAmount.LargeDecrement;
        while (true)
        {
            if (IsFullyVisible(element, container))
            {
                return (AncestorScrollStop.Visible, steps);
            }

            EnsureBoundsHaveArea(element, container);
            var elementBounds = element.Current.BoundingRectangle;
            var containerBounds = container.Current.BoundingRectangle;

            if (!scroll.Current.VerticallyScrollable)
            {
                return (AncestorScrollStop.ReachedEnd, steps);
            }

            if (steps >= maxScrolls)
            {
                return (AncestorScrollStop.MaxScrolls, steps);
            }

            if (isTimedOut())
            {
                return (AncestorScrollStop.Timeout, steps);
            }

            var before = scroll.Current.VerticalScrollPercent;
            var up = elementBounds.Top < containerBounds.Top;
            scroll.Scroll(ScrollAmount.NoAmount, up ? decrement : step);
            steps++;
            if (scroll.Current.VerticalScrollPercent == before)
            {
                EnsureBoundsHaveArea(element, container);
                return (IsFullyVisible(element, container) ? AncestorScrollStop.Visible : AncestorScrollStop.ReachedEnd, steps);
            }
        }
    }

    public static List<AutomationElement> FindVerticallyScrollableDescendants(AutomationElement root)
    {
        var result = new List<AutomationElement>();
        var candidates = root.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty, true));
        foreach (AutomationElement candidate in candidates)
        {
            var scroll = GetScrollPattern(candidate);
            if (scroll is not null && scroll.Current.VerticallyScrollable)
            {
                result.Add(candidate);
            }
        }

        return result;
    }

    /// <summary>
    /// ItemContainerPattern.FindItemByProperty on Name or AutomationId, then
    /// VirtualizedItemPattern.Realize when available. Returns null when the pattern is missing,
    /// the property is unsupported by the provider, or no item matches.
    /// </summary>
    public static AutomationElement? TryFindAndRealizeContainerItem(AutomationElement container, Selector selector)
    {
        if (!container.TryGetCurrentPattern(ItemContainerPattern.Pattern, out var itemContainerObject)
            || itemContainerObject is not ItemContainerPattern itemContainer)
        {
            return null;
        }

        var property = selector.Strategy == SelectorStrategy.AutomationId
            ? AutomationElement.AutomationIdProperty
            : AutomationElement.NameProperty;
        AutomationElement? item;
        try
        {
            item = itemContainer.FindItemByProperty(null, property, selector.Value);
        }
        catch (ElementNotAvailableException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or ArgumentException
            or System.Runtime.InteropServices.COMException)
        {
            return null;
        }

        if (item is not null
            && item.TryGetCurrentPattern(VirtualizedItemPattern.Pattern, out var virtualizedObject)
            && virtualizedObject is VirtualizedItemPattern virtualized)
        {
            try
            {
                virtualized.Realize();
            }
            catch (ElementNotAvailableException)
            {
                throw;
            }
            catch (Exception ex) when (ex is InvalidOperationException
                or ArgumentException
                or System.Runtime.InteropServices.COMException)
            {
                // Realize failed; the caller falls through to scroll-search.
                return null;
            }
        }

        return item;
    }

    public static AutomationElement? ResolveGridEditor(AutomationElement cell, ControlType editorControlType)
    {
        ScrollCellIntoView(cell);
        var editor = cell.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, editorControlType));
        if (editor is not null)
        {
            return editor;
        }

        if (cell.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selectionItemObject)
            && selectionItemObject is SelectionItemPattern selectionItem)
        {
            selectionItem.Select();
        }

        cell.SetFocus();
        var bounds = cell.Current.BoundingRectangle;
        NativeMethods.DoubleClick(
            (int)(bounds.X + bounds.Width / 2),
            (int)(bounds.Y + bounds.Height / 2));
        Thread.Sleep(50);

        return cell.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, editorControlType));
    }

    public static bool TrySetChecked(AutomationElement element, bool target)
    {
        if (!element.TryGetCurrentPattern(TogglePattern.Pattern, out var toggleObject)
            || toggleObject is not TogglePattern toggle)
        {
            return false;
        }

        var desired = target ? ToggleState.On : ToggleState.Off;
        if (toggle.Current.ToggleState != desired)
        {
            toggle.Toggle();
        }

        return toggle.Current.ToggleState == desired;
    }

    public static bool TryParseGridEditorControlType(
        string? text, out ControlType editorControlType, out string error)
    {
        switch (text?.Trim())
        {
            case "Edit":
                editorControlType = ControlType.Edit;
                error = string.Empty;
                return true;
            case "ComboBox":
                editorControlType = ControlType.ComboBox;
                error = string.Empty;
                return true;
            case "CheckBox":
                editorControlType = ControlType.CheckBox;
                error = string.Empty;
                return true;
            default:
                editorControlType = null!;
                error = "must be one of: Edit, ComboBox, CheckBox.";
                return false;
        }
    }

    private static GridCellResolution? FindGridCellInRealizedRows(
        AutomationElement grid, Selector rowSelector, int columnIndex)
    {
        foreach (AutomationElement match in ResolveSelectorAll(grid, rowSelector))
        {
            var row = FindContainingDataGridRow(match, grid);
            if (row is null)
            {
                continue;
            }

            var cell = FindGridCellByColumn(row, columnIndex);
            if (cell is not null)
            {
                return new GridCellResolution(row, cell);
            }
        }

        return null;
    }

    private static AutomationElement? FindContainingDataGridRow(AutomationElement element, AutomationElement grid)
    {
        var current = element;
        while (current is not null && !current.Equals(grid))
        {
            if (current.Current.ControlType == ControlType.DataItem
                && current.Current.ClassName == "DataGridRow")
            {
                return current;
            }

            current = TreeWalker.RawViewWalker.GetParent(current);
        }

        return null;
    }

    private static AutomationElement? FindGridCellByColumn(AutomationElement row, int columnIndex)
    {
        var cells = row.FindAll(TreeScope.Children, Condition.TrueCondition);
        foreach (AutomationElement cell in cells)
        {
            if (cell.TryGetCurrentPattern(GridItemPattern.Pattern, out var gridItemObject)
                && gridItemObject is GridItemPattern gridItem
                && gridItem.Current.Column == columnIndex)
            {
                return cell;
            }
        }

        return null;
    }

    private static void ScrollCellIntoView(AutomationElement cell)
    {
        if (cell.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var scrollItemObject)
            && scrollItemObject is ScrollItemPattern scrollItem)
        {
            scrollItem.ScrollIntoView();
        }
    }

    public static bool TryParseControlType(string text, out ControlType? controlType)
    {
        var field = typeof(ControlType).GetFields(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(f => f.FieldType == typeof(ControlType)
                && string.Equals(f.Name, text, StringComparison.OrdinalIgnoreCase));
        controlType = field?.GetValue(null) as ControlType;
        return controlType is not null;
    }

    public static ElementInfo ToElementInfo(AutomationElement el, bool includeChildren, int maxDepth,
        string? nameContains, ControlType? controlType, out int matchCount)
    {
        var info = ToElementInfo(el, includeChildren, maxDepth);
        matchCount = nameContains is null && controlType is null
            ? 0
            : PruneElementInfo(info, nameContains, controlType);
        return info;
    }

    private static int PruneElementInfo(ElementInfo info, string? nameContains, ControlType? controlType,
        bool retainSubtree = false)
    {
        var matches = (nameContains is null || info.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
            && (controlType is null || info.ControlType == controlType.ProgrammaticName);
        var matchCount = matches ? 1 : 0;
        var children = new List<ElementInfo>();
        foreach (var child in info.Children)
        {
            var childMatches = PruneElementInfo(child, nameContains, controlType, retainSubtree || matches);
            matchCount += childMatches;
            if (retainSubtree || matches || childMatches > 0)
            {
                children.Add(child);
            }
        }
        info.Children = children;
        return matchCount;
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
            BoundingRect = SafeRect(r),
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
    /// Synthetic right-click at the element's bounding-rect center. Unlike <see cref="Click"/>,
    /// no UIA pattern is attempted first — right-click's usual purpose (opening a context menu)
    /// isn't expressed via InvokePattern/TogglePattern, so this goes straight to the synthetic
    /// mechanism.
    /// </summary>
    public static string RightClick(AutomationElement element)
    {
        var r = element.Current.BoundingRectangle;
        var cx = (int)(r.X + r.Width / 2);
        var cy = (int)(r.Y + r.Height / 2);
        NativeMethods.RightClick(cx, cy);
        return "synthetic-right-click";
    }

    /// <summary>
    /// Attempts InvokePattern first (same as <see cref="Click"/>); falls back to two rapid
    /// synthetic left-clicks at the element's bounding-rect center, within the OS double-click
    /// time.
    /// </summary>
    public static string DoubleClick(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invokeObj)
            && invokeObj is InvokePattern invoke)
        {
            invoke.Invoke();
            return "pattern";
        }

        var r = element.Current.BoundingRectangle;
        var cx = (int)(r.X + r.Width / 2);
        var cy = (int)(r.Y + r.Height / 2);
        NativeMethods.DoubleClick(cx, cy);
        return "synthetic-double-click";
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

    public static string Clear(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObj)
            && valueObj is ValuePattern value
            && !(bool)element.GetCurrentPropertyValue(ValuePattern.IsReadOnlyProperty))
        {
            value.SetValue(string.Empty);
            return "pattern-clear";
        }

        var r = element.Current.BoundingRectangle;
        var cx = (int)(r.X + r.Width / 2);
        var cy = (int)(r.Y + r.Height / 2);
        NativeMethods.Click(cx, cy);
        Thread.Sleep(100);
        NativeMethods.SendKeysRaw("^a{DELETE}");
        return "synthetic-clear";
    }

    /// <summary>
    /// Click-to-focus the element, then paste <paramref name="text"/> via the clipboard
    /// (Clipboard.SetDataObject + Ctrl+V) instead of per-character synthetic keystrokes. Faster
    /// and non-interruptible for long text compared to <see cref="Type"/>'s synthetic-keyboard
    /// fallback, and unlike raw SendKeys, paste does not choke on embedded newlines since the
    /// text is never typed character-by-character through SendKeys.SendWait.
    ///
    /// The text this method writes to the clipboard is marked to opt out of Windows Clipboard
    /// History and Cloud Clipboard sync (per the documented opt-out mechanism: the
    /// "ExcludeClipboardContentFromMonitorProcessing" marker format, plus the
    /// "CanIncludeInClipboardHistory"/"CanUploadToCloudClipboard" DWORD formats set to 0), since
    /// this is transient automation input, not something the user intentionally copied. The
    /// restored original clipboard content (see below) is NOT marked this way — it is the user's
    /// own prior data being put back, not new content from this operation.
    ///
    /// The original clipboard contents are snapshotted and restored via the full IDataObject
    /// (Clipboard.GetDataObject()/SetDataObject(IDataObject, ...)) rather than plain text only:
    /// an earlier version snapshotted/restored via GetText()/SetText() alone, which — per a
    /// Regression Audit finding — would silently discard any other formats (e.g. HTML/RTF/image/
    /// file-drop data) that coexisted with a text representation on the clipboard (a common case,
    /// e.g. copying a spreadsheet cell), permanently losing the user's actual prior clipboard
    /// content while still reporting a successful restore. Full-fidelity snapshot/restore avoids
    /// this.
    /// </summary>
    /// <returns>
    /// (method, clipboardRestored): method is always "clipboard-paste" on success.
    /// clipboardRestored is null if there was nothing on the clipboard to restore (a no-op, not a
    /// failure); true if a prior clipboard snapshot was successfully restored; false if a prior
    /// snapshot existed but restoring it failed (best-effort only — a restore failure does not
    /// fail the paste itself, which already succeeded by this point).
    /// </returns>
    /// <exception cref="ClipboardUnavailableException">
    /// Thrown if the clipboard could not be written to after retrying (e.g. locked by another
    /// process). Callers must translate this to a clean clipboard-unavailable response.
    /// </exception>
    public static (string method, bool? clipboardRestored) TypeViaPaste(AutomationElement element, string text)
    {
        System.Windows.Forms.IDataObject? originalData = null;
        var hadOriginalContent = false;
        try
        {
            var snapshot = System.Windows.Forms.Clipboard.GetDataObject();
            if (snapshot is not null)
            {
                var formats = snapshot.GetFormats();
                if (formats.Length > 0)
                {
                    // Clipboard.GetDataObject() returns a live COM wrapper tied to the current
                    // clipboard owner, not a deep copy — once we overwrite the clipboard below,
                    // that wrapper can go stale and silently yield no data on restore. Eagerly
                    // pull each format's actual data into a DataObject we own, so the restore
                    // later writes real captured bytes, not a now-invalid reference.
                    var owned = new System.Windows.Forms.DataObject();
                    foreach (var format in formats)
                    {
                        try
                        {
                            var value = snapshot.GetData(format);
                            if (value is not null)
                            {
                                owned.SetData(format, value);
                            }
                        }
                        catch (System.Runtime.InteropServices.ExternalException)
                        {
                            // Skip formats that fail to materialize; best-effort snapshot.
                        }
                    }

                    if (owned.GetFormats().Length > 0)
                    {
                        originalData = owned;
                        hadOriginalContent = true;
                    }
                }
            }
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Clipboard busy reading current contents — proceed without a snapshot; restore
            // will simply be skipped (clipboardRestored: null) rather than failing the paste.
        }

        SetClipboardTextWithRetry(text, excludeFromHistoryAndSync: true);

        bool? restored = null;
        try
        {
            var r = element.Current.BoundingRectangle;
            var cx = (int)(r.X + r.Width / 2);
            var cy = (int)(r.Y + r.Height / 2);
            NativeMethods.Click(cx, cy);
            Thread.Sleep(100);
            NativeMethods.SendKeysRaw("^v");
            Thread.Sleep(100);
        }
        finally
        {
            if (hadOriginalContent)
            {
                try
                {
                    SetClipboardDataWithRetry(originalData!);
                    restored = true;
                }
                catch (ClipboardUnavailableException)
                {
                    // Best-effort restore only — the paste itself already succeeded above or
                    // input was safely aborted before it could be delivered.
                    restored = false;
                }
            }
        }

        return ("clipboard-paste", restored);
    }

    // Windows Clipboard History / Cloud Clipboard opt-out marker formats (documented at
    // https://learn.microsoft.com/windows/win32/dataxchg/clipboard-history — "Exclude data from
    // the clipboard history and cloud clipboard"). Presence of the first format (zero-byte data
    // is sufficient) excludes from both; the latter two are DWORD formats set to 0 for
    // finer-grained control. All three are registered together for the widest opt-out coverage.
    private const string ExcludeFromMonitorProcessingFormat = "ExcludeClipboardContentFromMonitorProcessing";
    private const string CanIncludeInClipboardHistoryFormat = "CanIncludeInClipboardHistory";
    private const string CanUploadToCloudClipboardFormat = "CanUploadToCloudClipboard";

    private static void SetClipboardTextWithRetry(string text, bool excludeFromHistoryAndSync)
    {
        var data = new System.Windows.Forms.DataObject();
        data.SetData(System.Windows.Forms.DataFormats.UnicodeText, text);

        if (excludeFromHistoryAndSync)
        {
            // Zero-byte marker: presence alone is the opt-out signal, per the documented
            // mechanism — no meaningful payload is expected or read by the OS for this.
            data.SetData(ExcludeFromMonitorProcessingFormat, new MemoryStream());

            // DWORD 0 = excluded, per the documented format contract for these two.
            data.SetData(CanIncludeInClipboardHistoryFormat, new MemoryStream(BitConverter.GetBytes(0)));
            data.SetData(CanUploadToCloudClipboardFormat, new MemoryStream(BitConverter.GetBytes(0)));
        }

        SetClipboardDataWithRetry(data);
    }

    private static void SetClipboardDataWithRetry(System.Windows.Forms.IDataObject data)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                System.Windows.Forms.Clipboard.SetDataObject(data, copy: true);
                return;
            }
            catch (System.Runtime.InteropServices.ExternalException) when (attempt < maxAttempts)
            {
                Thread.Sleep(50);
            }
            catch (System.Runtime.InteropServices.ExternalException ex)
            {
                throw new ClipboardUnavailableException(
                    "The clipboard could not be written to after retrying; it may be locked by another process.",
                    ex);
            }
        }
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

    /// <summary>
    /// Read-back helper used only for verifying clipboard-paste results (method ==
    /// "clipboard-paste"). Unlike <see cref="GetText"/> (used by the get-text verb and kept
    /// unchanged for compatibility), this also tries TextPattern before falling back to Name:
    /// paste targets elements without ValuePattern (that's the whole reason --paste exists), so
    /// without a TextPattern attempt, verification would almost always compare against the
    /// element's static Name label and produce a false-positive mismatch — the same reason
    /// verification is skipped entirely for synthetic-keyboard typing. TextPattern lets real
    /// paste content be verified instead of skipping verification for --paste too.
    /// </summary>
    public static string? GetTextForPasteVerification(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObj)
            && valueObj is ValuePattern value)
        {
            return value.Current.Value;
        }

        if (element.TryGetCurrentPattern(TextPattern.Pattern, out var textObj)
            && textObj is TextPattern text)
        {
            return text.DocumentRange.GetText(-1);
        }

        return element.Current.Name;
    }

    /// <summary>
    /// Focuses the element according to <paramref name="focusMode"/>, then sends raw/unescaped
    /// SendKeys syntax (e.g. "^a" for Ctrl+A, "{DELETE}", "{ENTER}"). UIA SetFocus is the default
    /// because a synthetic mouse click can trigger custom controls' mouse-selection behavior before
    /// the requested key is sent. Always uses synthetic keyboard input — there is no UIA pattern
    /// equivalent for key-combination input like there is for literal text (ValuePattern).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown (by the underlying SendKeys.SendWait) if <paramref name="keys"/> is not valid
    /// SendKeys syntax (e.g. an unbalanced brace or an unrecognized key name). Callers must
    /// translate this to a clean invalid-argument response rather than letting it propagate as an
    /// unhandled exception.
    /// </exception>
    public static string SendKeys(AutomationElement element, string keys, KeyFocusMode focusMode)
    {
        switch (focusMode)
        {
            case KeyFocusMode.SetFocus:
                element.SetFocus();
                Thread.Sleep(100);
                break;
            case KeyFocusMode.Click:
                var r = element.Current.BoundingRectangle;
                var cx = (int)(r.X + r.Width / 2);
                var cy = (int)(r.Y + r.Height / 2);
                NativeMethods.Click(cx, cy);
                Thread.Sleep(100);
                break;
            case KeyFocusMode.None:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(focusMode));
        }

        NativeMethods.SendKeysRaw(keys);
        return focusMode switch
        {
            KeyFocusMode.SetFocus => "set-focus",
            KeyFocusMode.Click => "click",
            KeyFocusMode.None => "none",
            _ => throw new ArgumentOutOfRangeException(nameof(focusMode))
        };
    }

    // Bounds for read-visible-text traversal: mirrors inspect's existing default maxDepth (8) and
    // adds a breadth cap and a total-node cap so a large/complex accessibility tree (e.g. an IDE
    // window) cannot produce unbounded JSON output or run away traversing thousands of elements.
    private const int DefaultTextMaxDepth = 8;
    private const int MaxChildrenPerElement = 200;
    private const int MaxVisitedNodes = 5000;
    private const int MaxTextValueLength = 2000;

    /// <summary>
    /// Read-only traversal that collects de-duplicated, ordered visible text from an element's
    /// subtree, opportunistically pulling from TextPattern, ValuePattern, and Name. Never invokes
    /// a pattern that could change state (no Invoke/Toggle/SetValue/Focus) and never clicks or
    /// types — this is strictly an observation helper for read-visible-text. Returns whether any
    /// traversal cap (depth/breadth/node count) or per-value length cap was hit, so callers can
    /// tell incomplete output from a genuinely exhaustive result.
    /// </summary>
    public static (List<string> Lines, bool Truncated) CollectVisibleText(AutomationElement root, int? maxDepth = null)
    {
        var depthLimit = maxDepth ?? DefaultTextMaxDepth;
        var seen = new HashSet<string>();
        var ordered = new List<string>();
        var visited = 0;
        var truncated = false;

        void Visit(AutomationElement el, int depth)
        {
            if (visited >= MaxVisitedNodes)
            {
                truncated = true;
                return;
            }
            visited++;

            foreach (var text in ExtractText(el))
            {
                var trimmed = text;
                if (trimmed.Length > MaxTextValueLength)
                {
                    trimmed = trimmed[..MaxTextValueLength];
                    truncated = true;
                }
                if (trimmed.Length > 0 && seen.Add(trimmed))
                {
                    ordered.Add(trimmed);
                }
            }

            if (depth >= depthLimit)
            {
                return;
            }
            if (visited >= MaxVisitedNodes)
            {
                truncated = true;
                return;
            }

            AutomationElementCollection children;
            try
            {
                children = el.FindAll(TreeScope.Children, Condition.TrueCondition);
            }
            catch
            {
                return;
            }

            var count = 0;
            foreach (AutomationElement child in children)
            {
                if (count >= MaxChildrenPerElement)
                {
                    truncated = true;
                    break;
                }
                if (visited >= MaxVisitedNodes)
                {
                    truncated = true;
                    break;
                }
                count++;
                Visit(child, depth + 1);
            }
        }

        Visit(root, 0);
        return (ordered, truncated);
    }

    private static IEnumerable<string> ExtractText(AutomationElement el)
    {
        // IsOffscreen elements are still read here (best-effort transcript capture); this method
        // never interacts with the element, only reads already-published UIA properties/patterns.
        string? name;
        try
        {
            name = el.Current.Name;
        }
        catch
        {
            name = null;
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            yield return name;
        }

        if (el.TryGetCurrentPattern(TextPattern.Pattern, out var textObj) && textObj is TextPattern textPattern)
        {
            string? bulk = null;
            try
            {
                bulk = textPattern.DocumentRange?.GetText(MaxTextValueLength);
            }
            catch
            {
                // Some TextPattern implementations throw for unsupported ranges; ignore and fall
                // back to ValuePattern/Name below.
            }

            if (!string.IsNullOrWhiteSpace(bulk))
            {
                yield return bulk;
            }
        }

        if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObj) && valueObj is ValuePattern valuePattern)
        {
            string? value;
            try
            {
                value = valuePattern.Current.Value;
            }
            catch
            {
                value = null;
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return value;
            }
        }
    }
}
