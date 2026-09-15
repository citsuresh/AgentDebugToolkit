using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using EnvDTE;

namespace AgentDebugToolkit.Debugger.VisualStudio;

/// <summary>
/// Locates a running `devenv.exe` instance's DTE object via the Running Object Table (ROT).
/// A running Visual Studio process registers itself in the ROT under a moniker like
/// "!VisualStudio.DTE.17.0:1234" (pid suffix); there is no other supported way to attach to an
/// already-running instance from an external process.
/// </summary>
internal static class DteLocator
{
    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(uint reserved, out IRunningObjectTable prot);

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(uint reserved, out IBindCtx ppbc);

    public static (DTE? dte, string? errorCode, string? error) FindDte(string? solutionNameFilter)
    {
        var candidates = new List<(DTE dte, string moniker)>();

        var hr = GetRunningObjectTable(0, out var rot);
        if (hr != 0 || rot is null)
        {
            return (null, "rot-unavailable", "Could not access the Running Object Table.");
        }

        try
        {
            rot.EnumRunning(out var enumMoniker);
            if (enumMoniker is null)
            {
                return (null, "devenv-not-found", "No running Visual Studio instances were found.");
            }

            try
            {
                enumMoniker.Reset();
                var monikers = new IMoniker[1];
                var fetchedPtr = Marshal.AllocHGlobal(sizeof(int));
                CreateBindCtx(0, out var bindCtx);

                try
                {
                    while (enumMoniker.Next(1, monikers, fetchedPtr) == 0 && Marshal.ReadInt32(fetchedPtr) == 1)
                    {
                        var moniker = monikers[0];
                        try
                        {
                            string displayName;
                            try
                            {
                                displayName = ComRetry.Invoke(() =>
                                {
                                    moniker.GetDisplayName(bindCtx, null, out var name);
                                    return name;
                                });
                            }
                            catch (COMException)
                            {
                                continue;
                            }

                            if (!displayName.StartsWith("!VisualStudio.DTE.", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            try
                            {
                                var comObject = ComRetry.Invoke(() =>
                                {
                                    rot.GetObject(moniker, out var obj);
                                    return obj;
                                });
                                if (comObject is DTE dte)
                                {
                                    candidates.Add((dte, displayName));
                                }
                            }
                            catch (COMException)
                            {
                                // The instance may be busy/starting up; skip it rather than failing the whole scan.
                            }
                        }
                        finally
                        {
                            // Only the enumeration moniker RCW is released here -- comObject/dte
                            // above is intentionally NOT released: it is returned to (or held by)
                            // the caller via `candidates`, and callers dispose/use it independently.
                            Marshal.ReleaseComObject(moniker);
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(fetchedPtr);
                    if (bindCtx is not null)
                    {
                        Marshal.ReleaseComObject(bindCtx);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(enumMoniker);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(rot);
        }

        if (candidates.Count == 0)
        {
            return (null, "devenv-not-found", "No running Visual Studio instances were found.");
        }

        if (string.IsNullOrEmpty(solutionNameFilter))
        {
            return (candidates[0].dte, null, null);
        }

        foreach (var (dte, _) in candidates)
        {
            string? solutionFile = null;
            try
            {
                solutionFile = ComRetry.Invoke(() => dte.Solution?.FullName);
            }
            catch (COMException)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(solutionFile)
                && Path.GetFileNameWithoutExtension(solutionFile)
                    .Equals(solutionNameFilter, StringComparison.OrdinalIgnoreCase))
            {
                return (dte, null, null);
            }
        }

        return (null, "devenv-not-found",
            $"No running Visual Studio instance has a solution named '{solutionNameFilter}' open.");
    }
}
