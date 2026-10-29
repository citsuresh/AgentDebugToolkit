using System.IO;
using AgentDebugToolkit.UiAutomation.Cli;
using Xunit;

namespace AgentDebugToolkit.UiAutomation.Tests;

public sealed class CursorSessionTests : IDisposable
{
    private readonly string _flagPath = Path.Combine(Path.GetTempPath(), $"cursor-session-test-{Guid.NewGuid():N}.flag");
    private readonly FakeCursors _cursors = new();

    [Fact]
    public void Start_WritesScriptCompatibleFlagAndMarksRequestedKind()
    {
        var result = Session().Start(CursorSessionKind.UiAutomation);

        Assert.Equal("UiAutomation", result.Kind);
        Assert.Equal("UiAutomation", result.Owner);
        Assert.True(result.Changed);
        Assert.Null(result.Warning);
        Assert.Equal([CursorSessionKind.UiAutomation], _cursors.Marks);
        var flag = File.ReadAllText(_flagPath);
        Assert.StartsWith($"kind=UiAutomation pid={Environment.ProcessId} started=", flag);
        Assert.True(DateTimeOffset.TryParse(flag.Split("started=")[1].Trim(), out _), flag);
    }

    [Fact]
    public void NestedStart_OfOtherKindReturnsOwnerWithoutChangingFlagOrCursor()
    {
        var session = Session();
        session.Start(CursorSessionKind.Orchestrator);
        var flag = File.ReadAllText(_flagPath);

        var result = session.Start(CursorSessionKind.UiAutomation);

        Assert.Equal("UiAutomation", result.Kind);
        Assert.Equal("Orchestrator", result.Owner);
        Assert.False(result.Changed);
        Assert.Equal(flag, File.ReadAllText(_flagPath));
        Assert.Equal([CursorSessionKind.Orchestrator], _cursors.Marks);
        Assert.Equal(0, _cursors.Restores);
    }

    [Fact]
    public void Stop_WrongKindLeavesOwnerSessionAlone()
    {
        var session = Session();
        session.Start(CursorSessionKind.UiAutomation);

        var result = session.Stop(CursorSessionKind.Orchestrator);

        Assert.Equal("UiAutomation", result.Owner);
        Assert.False(result.Changed);
        Assert.True(File.Exists(_flagPath));
        Assert.Equal(0, _cursors.Restores);
    }

    [Fact]
    public void Stop_WithoutKindClearsSessionAndRestoresCursor()
    {
        var session = Session();
        session.Start(CursorSessionKind.Orchestrator);

        var result = session.Stop(null);

        Assert.Null(result.Kind);
        Assert.Null(result.Owner);
        Assert.True(result.Changed);
        Assert.False(File.Exists(_flagPath));
        Assert.Equal(1, _cursors.Restores);
    }

    [Fact]
    public void Start_IgnoresAbandonedFlagOlderThanTwelveHours()
    {
        File.WriteAllText(_flagPath, "kind=Orchestrator pid=1 started=old");
        File.SetLastWriteTimeUtc(_flagPath, DateTime.UtcNow.AddHours(-13));

        var result = Session().Start(CursorSessionKind.UiAutomation);

        Assert.Equal("UiAutomation", result.Owner);
        Assert.True(result.Changed);
        Assert.StartsWith("kind=UiAutomation ", File.ReadAllText(_flagPath));
        Assert.Equal([CursorSessionKind.UiAutomation], _cursors.Marks);
    }

    [Fact]
    public void LegacyFlagWithoutKindCountsAsOrchestrator()
    {
        File.WriteAllText(_flagPath, "pid=1 started=legacy");

        var result = Session().Start(CursorSessionKind.UiAutomation);

        Assert.Equal("Orchestrator", result.Owner);
        Assert.False(result.Changed);
        Assert.Empty(_cursors.Marks);
        Assert.Equal("pid=1 started=legacy", File.ReadAllText(_flagPath));
    }

    [Theory]
    [InlineData("--kind", "UiAutomation")]
    [InlineData("--kind=UiAutomation", null)]
    public void CursorSessionArguments_AcceptBothKindForms(string option, string? value)
    {
        var arguments = value is null ? new[] { "start", option } : new[] { "start", option, value };

        var accepted = Verbs.TryParseCursorSessionArguments(arguments, out var start, out var kind, out var error);

        Assert.True(accepted, error);
        Assert.True(start);
        Assert.Equal(CursorSessionKind.UiAutomation, kind);
        Assert.Null(error);
    }

    [Fact]
    public void CursorSessionArguments_RejectStrayPositionalsAndOptions()
    {
        Assert.False(Verbs.TryParseCursorSessionArguments(["start", "extra"], out _, out _, out _));
        Assert.False(Verbs.TryParseCursorSessionArguments(["stop", "--unknown", "x"], out _, out _, out _));
        Assert.False(Verbs.TryParseCursorSessionArguments(["start", "--kind", "UiAutomation", "extra"], out _, out _, out _));
    }

    [Theory]
    [InlineData("--kind", "1")]
    [InlineData("--kind=1", null)]
    public void CursorSessionArguments_RejectNumericKind(string option, string? value)
    {
        var arguments = value is null ? new[] { "start", option } : new[] { "start", option, value };

        var accepted = Verbs.TryParseCursorSessionArguments(arguments, out _, out var kind, out var error);

        Assert.False(accepted);
        Assert.Null(kind);
        Assert.Equal("--kind must be UiAutomation or Orchestrator.", error);
    }

    [Fact]
    public void FlagPath_PrefersScriptTempWhenTempAndFallbackTmpDiffer()
    {
        var temp = Path.Combine(Path.GetTempPath(), "script-temp");
        var fallbackTmp = Path.Combine(Path.GetTempPath(), "dotnet-tmp");

        Assert.Equal(Path.Combine(temp, "agent-cursor-session.flag"), CursorSession.FlagPath(temp, fallbackTmp));
        Assert.Equal(Path.Combine(fallbackTmp, "agent-cursor-session.flag"), CursorSession.FlagPath(null, fallbackTmp));
    }

    [Fact]
    public void FailedCursorOperation_ReturnsSuccessShapedResultWithWarning()
    {
        _cursors.MarkSucceeds = false;
        var session = Session();

        var started = session.Start(CursorSessionKind.UiAutomation);
        _cursors.RestoreSucceeds = false;
        var stopped = session.Stop(null);

        Assert.True(started.Changed);
        Assert.Contains("Could not set", started.Warning);
        Assert.True(stopped.Changed);
        Assert.Contains("Could not restore", stopped.Warning);
    }

    private CursorSession Session() => new(_cursors, _flagPath);

    public void Dispose()
    {
        if (File.Exists(_flagPath)) File.Delete(_flagPath);
    }

    private sealed class FakeCursors : ICursorOperations
    {
        public List<CursorSessionKind> Marks { get; } = [];
        public int Restores { get; private set; }
        public bool MarkSucceeds { get; set; } = true;
        public bool RestoreSucceeds { get; set; } = true;

        public bool Mark(CursorSessionKind kind)
        {
            Marks.Add(kind);
            return MarkSucceeds;
        }

        public bool Restore()
        {
            Restores++;
            return RestoreSucceeds;
        }
    }
}