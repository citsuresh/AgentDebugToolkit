using System.IO;
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
            BoundingRect = SafeRect(r)
        };
    }

    /// <summary>
    /// Converts a UIA BoundingRectangle to a Rect, returning null instead of throwing/serializing
    /// invalid data when the rectangle contains non-finite values (NaN/Infinity) — which UIA can
    /// legitimately report for offscreen, virtualized, or not-yet-realized elements, and which
    /// System.Text.Json cannot serialize as a JSON number.
    /// </summary>
    private static Rect? SafeRect(System.Windows.Rect r)
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

        var r = element.Current.BoundingRectangle;
        var cx = (int)(r.X + r.Width / 2);
        var cy = (int)(r.Y + r.Height / 2);
        NativeMethods.Click(cx, cy);
        Thread.Sleep(100);
        NativeMethods.SendKeysRaw("^v");
        Thread.Sleep(100);

        bool? restored = null;
        if (hadOriginalContent)
        {
            try
            {
                SetClipboardDataWithRetry(originalData!);
                restored = true;
            }
            catch (ClipboardUnavailableException)
            {
                // Best-effort restore only — the paste itself already succeeded above.
                restored = false;
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
    /// Click-to-focus the element, then send raw/unescaped SendKeys syntax (e.g. "^a" for Ctrl+A,
    /// "{DELETE}", "{ENTER}"). Always uses synthetic keyboard input — there is no UIA pattern
    /// equivalent for key-combination input like there is for literal text (ValuePattern), so
    /// unlike <see cref="Type"/> this has no pattern-based fast path.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown (by the underlying SendKeys.SendWait) if <paramref name="keys"/> is not valid
    /// SendKeys syntax (e.g. an unbalanced brace or an unrecognized key name). Callers must
    /// translate this to a clean invalid-argument response rather than letting it propagate as an
    /// unhandled exception.
    /// </exception>
    public static void SendKeys(AutomationElement element, string keys)
    {
        var r = element.Current.BoundingRectangle;
        var cx = (int)(r.X + r.Width / 2);
        var cy = (int)(r.Y + r.Height / 2);
        NativeMethods.Click(cx, cy);
        Thread.Sleep(100);
        NativeMethods.SendKeysRaw(keys);
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
