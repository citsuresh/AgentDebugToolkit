extern alias vsdebugger;

using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using DebuggerWaitOptions = vsdebugger::AgentDebugToolkit.Debugger.Contracts.DebuggerWaitOptions;
using DebuggerDiscovery = vsdebugger::AgentDebugToolkit.Debugger.VisualStudio.DebuggerDiscovery;
using InstanceTarget = vsdebugger::AgentDebugToolkit.Debugger.VisualStudio.InstanceTarget;
using DteLocator = vsdebugger::AgentDebugToolkit.Debugger.VisualStudio.DteLocator;
using BreakContextReader = vsdebugger::AgentDebugToolkit.Debugger.VisualStudio.BreakContextReader;
using AgentDebugToolkit.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using Xunit;

namespace AgentDebugToolkit.UiAutomation.Tests;

public sealed class DebuggerHelpTests
{
    [Theory]
    [InlineData("help")]
    [InlineData("--help")]
    public async Task Help_IsStaticCompleteJsonWithoutVisualStudio(string form)
    {
        var result = await ExecutableIdentityTests.Run("vs", form);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Json.GetProperty("success").GetBoolean());
        var names = result.Json.GetProperty("verbs").EnumerateArray().Select(verb => verb.GetProperty("name").GetString()).ToArray();
        Assert.Equal(DebuggerDiscovery.Verbs, names);
        Assert.Equal(names.Length, names.Distinct().Count());
        Assert.Contains("get-callstack", names);
        Assert.Contains("get-exception-info", names);
        Assert.DoesNotContain("get-call-stack", names);
        Assert.Equal(590000, result.Json.GetProperty("wait").GetProperty("maxMcpTimeoutMs").GetInt32());
    }

    [Theory]
    [InlineData("get-callstack")]
    [InlineData("get-call-stack")]
    public async Task AliasHelp_ResolvesToOneCanonicalImplementation(string name)
    {
        var result = await ExecutableIdentityTests.Run("vs", "help", "--verb", name);
        Assert.Equal(0, result.ExitCode);
        var verbs = result.Json.GetProperty("verbs");
        Assert.Equal(1, verbs.GetArrayLength());
        Assert.Equal("get-callstack", verbs[0].GetProperty("name").GetString());
        Assert.Equal("get-call-stack", verbs[0].GetProperty("aliases")[0].GetString());
        var inline = await ExecutableIdentityTests.Run("vs", name, "--help");
        Assert.Equal(result.Json.GetRawText(), inline.Json.GetRawText());
    }

    [Fact]
    public async Task AliasDispatch_RetainsCanonicalValidationWithoutContactingVs()
    {
        var alias = await ExecutableIdentityTests.Run("vs", "get-call-stack", "--instancePid", "0");
        var canonical = await ExecutableIdentityTests.Run("vs", "get-callstack", "--instancePid", "0");
        Assert.Equal(1, alias.ExitCode);
        Assert.Equal(canonical.Json.GetRawText(), alias.Json.GetRawText());
        Assert.Equal("invalid-argument", alias.Json.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("missing")]
    public async Task UnknownHelp_IsArgumentFailure(string verb)
    {
        var result = await ExecutableIdentityTests.Run("vs", "help", "--verb=" + verb);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("invalid-argument", result.Json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Mcp_HelpUsesConfiguredExecutableAndReadOnlyMetadata()
    {
        var tools = new VsDebuggerTools(new CliRunner(NullLogger.Instance, AppContext.BaseDirectory, AppContext.BaseDirectory));
        var result = await tools.Help("get-call-stack");
        Assert.Null(result.WrapperErrorCode);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("get-callstack", result.Result!.Value.GetProperty("verbs")[0].GetProperty("name").GetString());
        foreach (var method in new[] { nameof(VsDebuggerTools.Help), nameof(VsDebuggerTools.ListInstances) })
        {
            var metadata = typeof(VsDebuggerTools).GetMethod(method)!.GetCustomAttribute<McpServerToolAttribute>()!;
            Assert.True(metadata.ReadOnly);
            Assert.False(metadata.Destructive);
        }
    }

    [Theory]
    [InlineData("info")]
    [InlineData("get-exception-info")]
    [InlineData("wait-for-break")]
    public async Task KnownVerbsSupportInlineStaticHelp(string verb)
    {
        var result = await ExecutableIdentityTests.Run("vs", verb, "--help");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(verb, result.Json.GetProperty("verbs")[0].GetProperty("name").GetString());
    }
}

public sealed class DebuggerWaitTests
{
    [Theory]
    [InlineData(0, 10000)]
    [InlineData(120000, 130000)]
    [InlineData(170001, 180001)]
    [InlineData(590000, 600000)]
    public void Milliseconds_HaveExactHeadroomWithoutSilentClamping(int milliseconds, int process)
    {
        Assert.True(DebuggerWaitOptions.TryParse(new Dictionary<string, string> { ["timeoutMs"] = milliseconds.ToString() }, 590000, out var wait, out var error), error);
        Assert.Equal(milliseconds, wait!.TimeoutMs);
        Assert.Equal(250, wait.PollMs);
        Assert.Equal(process, wait.ProcessTimeoutMs);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(590)]
    public void Seconds_AreExplicitAndConvertedExactly(int seconds)
    {
        Assert.True(DebuggerWaitOptions.TryParse(new Dictionary<string, string> { ["timeoutSeconds"] = seconds.ToString() }, 590000, out var wait, out _));
        Assert.Equal(seconds * 1000, wait!.TimeoutMs);
    }

    [Theory]
    [InlineData("timeoutMs", "-1")]
    [InlineData("timeoutMs", "590001")]
    [InlineData("timeoutMs", "2147483648")]
    [InlineData("timeoutSeconds", "591")]
    [InlineData("timeoutSeconds", "1.5")]
    [InlineData("timeoutSeconds", "-1")]
    public void OutOfRangeAndMalformedWaits_AreRejected(string unit, string value)
    {
        Assert.False(DebuggerWaitOptions.TryParse(new Dictionary<string, string> { [unit] = value }, 590000, out var wait, out var error));
        Assert.Null(wait);
        Assert.NotEmpty(error!);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidPoll_IsRejected(int poll)
    {
        Assert.False(DebuggerWaitOptions.TryParse(new Dictionary<string, string> { ["timeoutMs"] = "0", ["pollMs"] = poll.ToString() }, 590000, out _, out _));
    }

    [Fact]
    public void MissingOrConflictingUnits_AreRejectedAndLegacyCliMillisecondsRemainSupported()
    {
        Assert.False(DebuggerWaitOptions.TryParse(new Dictionary<string, string>(), 590000, out _, out _));
        Assert.False(DebuggerWaitOptions.TryParse(new Dictionary<string, string> { ["timeoutMs"] = "0", ["timeoutSeconds"] = "0" }, 590000, out _, out _));
        Assert.True(DebuggerWaitOptions.TryParse(new Dictionary<string, string> { ["timeoutMs"] = int.MaxValue.ToString() }, int.MaxValue, out var legacy, out _));
        Assert.Equal(int.MaxValue, legacy!.TimeoutMs);
    }

    [Fact]
    public void FakeClock_PollSleepsOnlyRemainingBudgetAndZeroMeansOneObservation()
    {
        long elapsed = 0;
        var reads = 0;
        var sleeps = new List<int>();
        Assert.False(DebuggerWaitOptions.Poll(() => { reads++; return false; }, () => elapsed,
            delay => { sleeps.Add(delay); elapsed += delay; }, new(600, 250)));
        Assert.Equal(new[] { 250, 250, 100 }, sleeps);
        Assert.Equal(4, reads);
        Assert.Equal(600, elapsed);
        reads = 0;
        Assert.False(DebuggerWaitOptions.Poll(() => { reads++; return false; }, () => 0, _ => throw new Exception("Unexpected sleep"), new(0, 250)));
        Assert.Equal(1, reads);
        Assert.True(DebuggerWaitOptions.Poll(() => true, () => 0, _ => throw new Exception("Unexpected sleep"), new(0, 250)));
    }

    [Theory]
    [InlineData(-1, null, null)]
    [InlineData(590001, null, null)]
    [InlineData(null, 591, null)]
    [InlineData(0, 0, null)]
    [InlineData(0, null, 0)]
    [InlineData(null, null, null)]
    public async Task Mcp_InvalidWaitsNeverLaunchAProcess(int? milliseconds, int? seconds, int? poll)
    {
        var missing = Path.Combine(Path.GetTempPath(), "agentdebug-no-cli-" + Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(missing));
        var tools = new VsDebuggerTools(new CliRunner(NullLogger.Instance, missing, missing));
        var result = await tools.WaitForBreak(milliseconds, poll, timeoutSeconds: seconds);
        Assert.Null(result.WrapperErrorCode);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("invalid-argument", result.Result!.Value.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("--timeoutSeconds=591")]
    [InlineData("--timeoutMs=-1")]
    [InlineData("--timeoutSeconds=1.5")]
    public async Task Cli_InvalidUnitsFailBeforeVsLookup(string option)
    {
        var result = await ExecutableIdentityTests.Run("vs", "wait-for-break", option);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("invalid-argument", result.Json.GetProperty("error").GetString());
    }
}

public sealed class DebuggerTargetingContextTests
{
    [Fact]
    public void ExplicitPid_IsExactAndNeverReadsAnotherInstancesSolution()
    {
        var candidates = new[] { new InstanceTarget(10, () => throw new Exception("Wrong instance read")), new InstanceTarget(20, () => "Target") };
        Assert.Equal(1, DebuggerDiscovery.SelectInstance(candidates, "Target", 20).Index);
        Assert.Equal("devenv-not-found", DebuggerDiscovery.SelectInstance(candidates, "Target", 99).Error);
        Assert.Equal("devenv-not-found", DebuggerDiscovery.SelectInstance(candidates, "Other", 20).Error);
    }

    [Fact]
    public void ExplicitDuplicateNamesOrPids_AreAmbiguousWhileOmittedTargetKeepsLegacyDefault()
    {
        var candidates = new[] { new InstanceTarget(10, () => "Same"), new InstanceTarget(20, () => "Same") };
        Assert.Equal("ambiguous-instance", DebuggerDiscovery.SelectInstance(candidates, "Same", null).Error);
        Assert.Equal(0, DebuggerDiscovery.SelectInstance(candidates, null, null).Index);
        Assert.Equal("ambiguous-instance", DebuggerDiscovery.SelectInstance(new[] { candidates[0], candidates[0] }, null, 10).Error);
    }

    [Fact]
    public void UnreadableExplicitSolution_CannotFallThroughToAnotherCandidate()
    {
        var candidates = new[] { new InstanceTarget(10, () => throw new COMException("unavailable")), new InstanceTarget(20, () => "Same") };
        Assert.Throws<COMException>(() => DebuggerDiscovery.SelectInstance(candidates, "Same", null));
    }

    [Theory]
    [InlineData("!VisualStudio.DTE.17.0:1234", 1234)]
    [InlineData("bad", null)]
    [InlineData("!VisualStudio.DTE.17.0:0", null)]
    public void InstancePid_ComesFromObservedMonikerNotDebuggeePid(string moniker, int? pid) => Assert.Equal(pid, DteLocator.ParseInstancePid(moniker));

    [Theory]
    [InlineData("--instancePid=0")]
    [InlineData("--instancePid=-1")]
    [InlineData("--instancePid=not-a-pid")]
    [InlineData("--solution=")]
    [InlineData("--solution= ")]
    [InlineData("--context=maybe")]
    public async Task InvalidTargetsAndContext_AreRejectedBeforeAnyVsLookup(string option)
    {
        var result = await ExecutableIdentityTests.Run("vs", "debugger-status", option);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("invalid-argument", result.Json.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("--instancePid=1")]
    [InlineData("--solution=Example")]
    [InlineData("--unexpected=true")]
    public async Task DiscoveryRejectsOptionsBeforeReadingTheRot(string option)
    {
        var result = await ExecutableIdentityTests.Run("vs", "list-instances", option);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("invalid-argument", result.Json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Mcp_ForwardsValidPinAndPreservesExplicitEmptySolutionWithoutLaunching()
    {
        var missing = Path.Combine(Path.GetTempPath(), "agentdebug-no-cli-" + Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(missing));
        var tools = new VsDebuggerTools(new CliRunner(NullLogger.Instance, missing, missing));
        var build = typeof(VsDebuggerTools).GetMethod("BuildArgs", BindingFlags.NonPublic | BindingFlags.Static)!;
        var args = Assert.IsType<List<string>>(build.Invoke(null, new object[] { "debugger-status", "", Array.Empty<(string, string?)>() }));
        Assert.Contains("--solution=", args);
        var run = typeof(VsDebuggerTools).GetMethod("RunAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var pending = Assert.IsAssignableFrom<Task<ToolInvocationResult>>(run.Invoke(tools, new object?[] { args, null, CancellationToken.None, 1234 }));
        var result = await pending;
        Assert.Equal("exe-not-found", result.WrapperErrorCode);
        Assert.Equal("--instancePid", args[^2]);
        Assert.Equal("1234", args[^1]);
    }

    [Fact]
    public async Task EveryMcpTargetedOperation_ValidatesAndForwardsPinWithoutLaunching()
    {
        var missing = Path.Combine(Path.GetTempPath(), "agentdebug-no-cli-" + Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(missing));
        var tools = new VsDebuggerTools(new CliRunner(NullLogger.Instance, missing, missing));
        var methods = typeof(VsDebuggerTools).GetMethods().Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null
            && method.Name is not (nameof(VsDebuggerTools.Help) or nameof(VsDebuggerTools.ListInstances))).ToArray();
        Assert.Equal(22, methods.Length);
        foreach (var method in methods)
        {
            var parameters = method.GetParameters();
            var pin = parameters.Single(parameter => parameter.Name == "instancePid");
            Assert.Contains("not the debuggee PID", pin.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()!.Description);
            Assert.True(pin.IsOptional);
            Assert.True(Array.IndexOf(parameters, pin) > Array.FindIndex(parameters, parameter => parameter.ParameterType == typeof(CancellationToken)));
            var arguments = parameters.Select(parameter => parameter.Name == "instancePid" ? (object)0
                : parameter.Name == "timeoutMs" ? 0
                : parameter.HasDefaultValue ? parameter.DefaultValue
                : parameter.ParameterType == typeof(string) ? "not-executed" : (object)1).ToArray();
            var pending = Assert.IsAssignableFrom<Task<ToolInvocationResult>>(method.Invoke(tools, arguments));
            var result = await pending;
            Assert.Null(result.WrapperErrorCode);
            Assert.Equal("invalid-argument", result.Result!.Value.GetProperty("error").GetString());
        }
    }

    [Theory]
    [InlineData("design")]
    [InlineData("run")]
    public void NonBreakContext_DoesNotReadFrameThreadOrException(string mode)
    {
        object? Forbidden() => throw new Exception("No break-only observation allowed");
        var context = JsonSerializer.SerializeToElement(BreakContextReader.Read(mode, () => new { instancePid = 1 },
            () => throw new Exception("No thread read"), () => throw new Exception("No frame read"), Forbidden, Forbidden, () => mode));
        Assert.Equal("unknown", context.GetProperty("breakReason").GetString());
        Assert.Equal(JsonValueKind.Null, context.GetProperty("selectedThreadId").ValueKind);
        Assert.Equal(JsonValueKind.Null, context.GetProperty("exceptionLocal").ValueKind);
        Assert.Empty(context.GetProperty("errors").EnumerateArray());
    }

    [Fact]
    public void BreakContext_ReportsFactsAndProviderErrorsWithoutInventingCauseOrDefaults()
    {
        var context = JsonSerializer.SerializeToElement(BreakContextReader.Read("break", () => new { instancePid = 7 },
            () => throw new COMException("thread unavailable"), () => "Observed.Function", () => new { type = "Example", value = "observed" },
            () => null, () => "run"));
        Assert.Equal("unknown", context.GetProperty("breakReason").GetString());
        Assert.Equal(JsonValueKind.Null, context.GetProperty("selectedThreadId").ValueKind);
        Assert.Equal("Observed.Function", context.GetProperty("selectedFrameFunction").GetString());
        Assert.Equal("Example", context.GetProperty("exceptionLocal").GetProperty("type").GetString());
        Assert.Equal("run", context.GetProperty("modeAtEnd").GetString());
        Assert.False(context.GetProperty("observationsAreAtomic").GetBoolean());
        var error = Assert.Single(context.GetProperty("errors").EnumerateArray());
        Assert.Equal("selectedThreadId", error.GetProperty("field").GetString());
        Assert.Equal("context-read-failed", error.GetProperty("error").GetString());
    }

    [Fact]
    public void MissingExceptionLocalAndBreakpoint_DoNotEstablishTheBreakCause()
    {
        var context = JsonSerializer.SerializeToElement(BreakContextReader.Read("break", () => new { instancePid = 7 },
            () => 99, () => "Selected", () => null, () => null, () => "break"));
        Assert.Equal(99, context.GetProperty("selectedThreadId").GetInt32());
        Assert.Equal(JsonValueKind.Null, context.GetProperty("exceptionLocal").ValueKind);
        Assert.Equal(JsonValueKind.Null, context.GetProperty("lastBreakpointHit").ValueKind);
        Assert.Equal("unknown", context.GetProperty("breakReason").GetString());
    }
}