using System.IO;
using System.Text.Json;

namespace AgentDebugToolkit.UiAutomation.Cli;

/// <summary>
/// Persists the "current" pid across CLI invocations (each invocation is a fresh process).
/// See docs/ARCHITECTURE.md "Process/session model".
/// </summary>
public static class SessionContext
{
    private static readonly string FilePath = Path.Combine(
        Path.GetTempPath(), "agentdebugtoolkit", "ui-session.json");

    public record ContextData(int Pid, string ProcessName, DateTime StartedAtUtc);

    public static void Save(int pid, string processName, DateTime startedAtUtc)
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        var data = new ContextData(pid, processName, startedAtUtc);
        var temporaryPath = Path.Combine(dir, $".ui-session-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(data));
            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static ContextData? Load()
    {
        try
        {
            using var stream = new FileStream(
                FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<ContextData>(stream);
        }
        catch
        {
            return null;
        }
    }
}
