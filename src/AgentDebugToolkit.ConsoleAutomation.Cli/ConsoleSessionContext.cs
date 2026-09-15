using System.Text.Json;

namespace AgentDebugToolkit.ConsoleAutomation.Cli;

internal static class ConsoleSessionContext
{
    private const string ContextMutexName = @"Local\AgentDebugToolkit.ConsoleAutomation.SessionContext";
    private static readonly string FilePath = Path.Combine(
        Path.GetTempPath(), "agentdebugtoolkit", "console-session.json");

    internal sealed record Data(
        string SessionId,
        int BrokerPid,
        DateTime BrokerStartedAtUtc,
        string PipeName,
        int TargetPid);

    public static void Save(Data data)
    {
        using var contextLock = new ContextLock();
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".console-session-{Guid.NewGuid():N}.json");

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

    public static Data? Load()
    {
        try
        {
            using var stream = new FileStream(
                FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<Data>(stream);
        }
        catch
        {
            return null;
        }
    }

    public static void ClearIfCurrent(Data expected)
    {
        using var contextLock = new ContextLock();
        try
        {
            var current = Load();
            if (current?.SessionId == expected.SessionId)
            {
                File.Delete(FilePath);
            }
        }
        catch (FileNotFoundException)
        {
        }
    }

    private sealed class ContextLock : IDisposable
    {
        private readonly Mutex mutex = new(false, ContextMutexName);
        private bool held;

        public ContextLock()
        {
            try
            {
                held = mutex.WaitOne();
            }
            catch (AbandonedMutexException)
            {
                held = true;
            }
        }

        public void Dispose()
        {
            if (held)
            {
                mutex.ReleaseMutex();
            }

            mutex.Dispose();
        }
    }
}
