using System.ComponentModel;
using System.IO;
using System.Text.Json;
using AgentDebugToolkit.UiAutomation.Cli;
using Xunit;

namespace AgentDebugToolkit.UiAutomation.Tests;

public sealed class ForegroundDiagnosticsTests
{
    [Theory]
    [InlineData(0, 0, false, "active")]
    [InlineData(2, 0, true, "active")]
    [InlineData(2, 1, true, "connected")]
    [InlineData(2, 4, true, "disconnected")]
    public void Capture_ReportsThisProcessSessionWithoutInferringClientWindowState(
        int protocol, int state, bool remote, string connectionState)
    {
        var source = new Source { Protocol = () => protocol, State = () => state };
        var json = ForegroundDiagnostics.Capture(new IntPtr(0x123), source);
        Assert.Equal("0x123", json.GetProperty("targetRootHwnd").GetString());
        Assert.True(json.GetProperty("isMinimized").GetBoolean());
        Assert.Equal("0x456", json.GetProperty("foregroundHwnd").GetString());
        Assert.Equal(17, json.GetProperty("processSessionId").GetInt32());
        Assert.Equal(protocol, json.GetProperty("sessionProtocolType").GetInt32());
        Assert.Equal(remote, json.GetProperty("isRemoteSession").GetBoolean());
        Assert.Equal(connectionState, json.GetProperty("sessionConnectionState").GetString());
        Assert.Equal(state, json.GetProperty("sessionConnectionStateValue").GetInt32());
        Assert.Equal("unknown", json.GetProperty("remoteClientWindowState").GetString());
        Assert.Empty(json.GetProperty("errors").EnumerateArray());
        Assert.Equal(new[] { "root:291", "minimized:291", "foreground", "session", "protocol:17", "state:17" }, source.Reads);
        Assert.Contains("not a diagnosis", json.GetProperty("hint").GetString());
        Assert.Contains("A local RDP client does not mean this process runs remotely", json.GetProperty("hint").GetString());
        Assert.Contains("workaround, not a proven fix", json.GetProperty("hint").GetString());
    }

    [Fact]
    public void UnsupportedProtocolAndState_RemainUnknownRatherThanLocalOrConnected()
    {
        var source = new Source { Protocol = () => 99, State = () => 42 };
        var json = ForegroundDiagnostics.Capture(new IntPtr(0x123), source);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("isRemoteSession").ValueKind);
        Assert.Equal("unknown", json.GetProperty("sessionConnectionState").GetString());
        Assert.Equal(99, json.GetProperty("sessionProtocolType").GetInt32());
        Assert.Equal(42, json.GetProperty("sessionConnectionStateValue").GetInt32());
    }

    [Fact]
    public void DeniedSessionQueries_PreserveWindowFactsAndExplicitlyReportUnknownFields()
    {
        var source = new Source
        {
            Protocol = () => throw new Win32Exception(5),
            State = () => throw new UnauthorizedAccessException()
        };
        var json = ForegroundDiagnostics.Capture(new IntPtr(0x123), source);
        Assert.True(json.GetProperty("isMinimized").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("sessionProtocolType").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("isRemoteSession").ValueKind);
        Assert.Equal("unknown", json.GetProperty("sessionConnectionState").GetString());
        var errors = json.GetProperty("errors").EnumerateArray().ToArray();
        Assert.Equal(2, errors.Length);
        Assert.Equal("sessionProtocolType", errors[0].GetProperty("field").GetString());
        Assert.Equal(5, errors[0].GetProperty("nativeErrorCode").GetInt32());
        Assert.Equal("sessionConnectionState", errors[1].GetProperty("field").GetString());
        Assert.Equal("UnauthorizedAccessException", errors[1].GetProperty("error").GetString());
    }

    [Fact]
    public void UnavailableProcessSession_DoesNotQueryOrInventAnotherSession()
    {
        var source = new Source { Session = () => throw new Win32Exception(5) };
        var json = ForegroundDiagnostics.Capture(new IntPtr(0x123), source);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("processSessionId").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("sessionProtocolType").ValueKind);
        Assert.Equal("unknown", json.GetProperty("sessionConnectionState").GetString());
        Assert.Equal(new[] { "root:291", "minimized:291", "foreground", "session" }, source.Reads);
        Assert.Single(json.GetProperty("errors").EnumerateArray());
    }

    [Fact]
    public void VanishedWindow_DoesNotInventFalseMinimizedState()
    {
        var source = new Source { Minimized = () => null };
        var json = ForegroundDiagnostics.Capture(new IntPtr(0x123), source);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("isMinimized").ValueKind);
        var error = Assert.Single(json.GetProperty("errors").EnumerateArray());
        Assert.Equal("isMinimized", error.GetProperty("field").GetString());
        Assert.Equal("window-gone", error.GetProperty("error").GetString());
    }

    [Fact]
    public void MissingRoot_SkipsMinimizedReadButPreservesSessionObservations()
    {
        var source = new Source { Root = _ => IntPtr.Zero };
        var json = ForegroundDiagnostics.Capture(new IntPtr(0x123), source);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("targetRootHwnd").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("isMinimized").ValueKind);
        Assert.Equal("root-unavailable", Assert.Single(json.GetProperty("errors").EnumerateArray()).GetProperty("error").GetString());
        Assert.Equal(new[] { "root:291", "foreground", "session", "protocol:17", "state:17" }, source.Reads);
    }

    [Fact]
    public void UnexpectedProgrammingError_IsNotSilentlyTurnedIntoUnknown()
    {
        var failure = new InvalidOperationException("unexpected source failure");
        var source = new Source { Protocol = () => throw failure };
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => ForegroundDiagnostics.Capture(new IntPtr(0x123), source)));
    }

    [Fact]
    public void ForegroundFailure_RetainsEnvelopeMessageAndExtraContext()
    {
        var source = new Source();
        using var output = new StringWriter();
        var original = Console.Out;
        Console.SetOut(output);
        try
        {
            Verbs.WriteForegroundFailure(new IntPtr(0x123), "original message", new { step = "resolve-input", restoreAttempted = true }, source);
        }
        finally { Console.SetOut(original); }
        using var response = JsonDocument.Parse(output.ToString());
        var json = response.RootElement;
        Assert.False(json.GetProperty("success").GetBoolean());
        Assert.Equal("foreground-activation-failed", json.GetProperty("error").GetString());
        Assert.Equal("original message", json.GetProperty("message").GetString());
        Assert.Equal("resolve-input", json.GetProperty("step").GetString());
        Assert.True(json.GetProperty("restoreAttempted").GetBoolean());
        Assert.Equal("0x123", json.GetProperty("foregroundDiagnostics").GetProperty("targetRootHwnd").GetString());
        Assert.Equal(6, source.Reads.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChatForeground_ObservesOnlyFailureAndRetainsStep(bool success)
    {
        var source = new Source();
        using var output = new StringWriter();
        var original = Console.Out;
        var activations = 0;
        Console.SetOut(output);
        try
        {
            Assert.Equal(success, Verbs.TryEnsureChatInputForeground(() => { activations++; return success; }, new IntPtr(0x123), source));
        }
        finally { Console.SetOut(original); }
        Assert.Equal(1, activations);
        Assert.Equal(success ? 0 : 6, source.Reads.Count);
        if (success) Assert.Equal("", output.ToString());
        else
        {
            using var response = JsonDocument.Parse(output.ToString());
            Assert.Equal("resolve-input", response.RootElement.GetProperty("step").GetString());
            Assert.Equal("foreground-activation-failed", response.RootElement.GetProperty("error").GetString());
            Assert.Equal("0x123", response.RootElement.GetProperty("foregroundDiagnostics").GetProperty("targetRootHwnd").GetString());
        }
    }

    [Fact]
    public void ChatForeground_DivergentTargetUsesEffectiveHandleAfterTheSingleActivation()
    {
        var originalTarget = new IntPtr(0x123);
        var effectiveTarget = new IntPtr(0x789);
        var observedTarget = originalTarget;
        var attempts = 0;
        var source = new Source { Root = hwnd => { Assert.Equal(effectiveTarget, hwnd); return hwnd; } };
        using var output = new StringWriter();
        var original = Console.Out;
        Console.SetOut(output);
        try
        {
            Assert.False(Verbs.TryEnsureChatInputForeground(() =>
            {
                attempts++;
                observedTarget = effectiveTarget;
                return false;
            }, originalTarget, source, () => observedTarget));
        }
        finally { Console.SetOut(original); }
        Assert.Equal(1, attempts);
        using var response = JsonDocument.Parse(output.ToString());
        Assert.Equal("resolve-input", response.RootElement.GetProperty("step").GetString());
        Assert.Equal("0x789", response.RootElement.GetProperty("foregroundDiagnostics").GetProperty("targetRootHwnd").GetString());
    }

    [Fact]
    public void MidInputException_PreservesTargetAndOriginalMessage()
    {
        var failure = new ForegroundActivationException(targetHwnd: new IntPtr(0x123));
        Assert.Equal(new IntPtr(0x123), failure.TargetHwnd);
        Assert.Equal("The target window is no longer foreground.", failure.Message);
    }

    internal sealed class Source : IForegroundDiagnosticSource
    {
        internal List<string> Reads { get; } = new();
        internal Func<IntPtr, IntPtr> Root { get; init; } = hwnd => hwnd;
        internal Func<bool?> Minimized { get; init; } = () => true;
        internal Func<int> Session { get; init; } = () => 17;
        internal Func<int> Protocol { get; init; } = () => 0;
        internal Func<int> State { get; init; } = () => 0;
        public IntPtr RootWindow(IntPtr hwnd) { Reads.Add($"root:{hwnd.ToInt64()}"); return Root(hwnd); }
        public bool? IsMinimized(IntPtr hwnd) { Reads.Add($"minimized:{hwnd.ToInt64()}"); return Minimized(); }
        public IntPtr ForegroundWindow() { Reads.Add("foreground"); return new IntPtr(0x456); }
        public int CurrentSessionId() { Reads.Add("session"); return Session(); }
        public int SessionProtocol(int sessionId) { Reads.Add($"protocol:{sessionId}"); return Protocol(); }
        public int SessionConnectionState(int sessionId) { Reads.Add($"state:{sessionId}"); return State(); }
    }
}