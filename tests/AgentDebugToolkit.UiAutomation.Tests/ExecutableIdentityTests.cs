using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using AgentDebugToolkit.Identity;
using AgentDebugToolkit.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using Xunit;
using Xunit.Abstractions;

namespace AgentDebugToolkit.UiAutomation.Tests;

public sealed class ExecutableIdentityTests(ITestOutputHelper testOutput)
{
    [Theory]
    [InlineData("ui")]
    [InlineData("vs")]
    [InlineData("console")]
    [InlineData("mcp")]
    public async Task EveryExecutable_ReportsActualPathsHashesAndEmbeddedBuildWithoutLiveTargets(string executable)
    {
        var result = await Run(executable, "info");
        Assert.Equal(0, result.ExitCode);
        var info = result.Json;
        Assert.True(info.GetProperty("success").GetBoolean());
        Assert.Equal(executable == "mcp" ? "server" : executable, info.GetProperty("tool").GetString());
        Assert.Equal(1, info.GetProperty("contractVersion").GetInt32());
        var path = Path.Combine(AppContext.BaseDirectory, $"agentdebug-{executable}.exe");
        Assert.Equal(path, info.GetProperty("executablePath").GetString(), ignoreCase: true);
        Assert.Equal(ToolkitIdentity.Hash(path), info.GetProperty("executableSha256").GetString());
        var assembly = Path.ChangeExtension(path, ".dll");
        Assert.Equal(assembly, info.GetProperty("assemblyPath").GetString(), ignoreCase: true);
        Assert.Equal(ToolkitIdentity.Hash(assembly), info.GetProperty("assemblySha256").GetString());
        Assert.True(Guid.TryParse(info.GetProperty("moduleVersionId").GetString(), out _));
        Assert.Contains(info.GetProperty("architecture").GetString(), new[] { "x86", "x64", "arm64", "arm" });
        var build = info.GetProperty("build");
        var metadata = Assembly.LoadFrom(assembly).GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(item => item.Key, item => item.Value);
        var embeddedCommit = metadata["ToolkitSourceCommit"];
        Assert.Equal(string.IsNullOrEmpty(embeddedCommit) ? null : embeddedCommit, build.GetProperty("sourceCommit").GetString());
        if (!string.IsNullOrEmpty(embeddedCommit)) Assert.Matches("^[0-9a-f]{40,64}$", embeddedCommit);
        bool? embeddedDirty = bool.TryParse(metadata["ToolkitSourceDirty"], out var dirty) ? dirty : null;
        Assert.Equal(embeddedDirty, build.GetProperty("sourceDirty").Deserialize<bool?>());
        Assert.Equal(metadata["ToolkitConfiguration"], build.GetProperty("configuration").GetString());
        Assert.Equal("managed-source", build.GetProperty("deploymentKind").GetString());
        Assert.Contains("toolkit.info.v1", info.GetProperty("capabilities").EnumerateArray().Select(item => item.GetString()));
        Assert.False(info.TryGetProperty("packageVerification", out _));
    }

    [Theory]
    [InlineData("ui")]
    [InlineData("vs")]
    [InlineData("console")]
    [InlineData("mcp")]
    public async Task Info_WithoutManifestExplicitlyReportsNotPresent(string executable)
    {
        var result = await Run(executable, "info", "--verifyPackage");
        Assert.Equal(0, result.ExitCode);
        var verification = result.Json.GetProperty("packageVerification");
        Assert.Equal("not-present", verification.GetProperty("status").GetString());
        Assert.Equal(0, verification.GetProperty("checkedFiles").GetInt32());
        Assert.Equal(JsonValueKind.Null, verification.GetProperty("manifestPath").ValueKind);
    }

    [Theory]
    [InlineData("--verifyPackage=maybe")]
    [InlineData("--verifyPackage=")]
    [InlineData("--unknown")]
    public async Task Info_RejectsInvalidOptions(string option)
    {
        var result = await Run("ui", "info", option);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("invalid-argument", result.Json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Mcp_InfoForwardsConfiguredCliAndPackageFlag()
    {
        var tools = Tools(new HashSet<string> { "ui", "vs" });
        var result = await tools.Info("ui", true);
        Assert.Null(result.WrapperErrorCode);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ui", result.Result!.Value.GetProperty("tool").GetString());
        Assert.Equal("not-present", result.Result.Value.GetProperty("packageVerification").GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("console", "tool-disabled")]
    [InlineData("vs", "tool-disabled")]
    [InlineData("bad", "invalid-argument")]
    public async Task Mcp_InfoCannotBypassDisabledGroupsOrAcceptUnknownTools(string tool, string error)
    {
        var tools = new ToolkitInfoTools(new CliRunner(NullLogger.Instance, "missing", "missing"), new HashSet<string> { "ui" });
        var result = await tools.Info(tool);
        Assert.Null(result.WrapperErrorCode);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(error, result.Result!.Value.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Mcp_ServerIdentityUsesEmbeddedServerAssemblyAndReadOnlyAnnotations()
    {
        var result = await Tools(new HashSet<string>()).Info();
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("server", result.Result!.Value.GetProperty("tool").GetString());
        Assert.EndsWith("agentdebug-mcp.dll", result.Result.Value.GetProperty("assemblyPath").GetString());
        var annotation = typeof(ToolkitInfoTools).GetMethod(nameof(ToolkitInfoTools.Info))!.GetCustomAttribute<McpServerToolAttribute>()!;
        Assert.Equal("toolkit_info", annotation.Name);
        Assert.True(annotation.ReadOnly);
        Assert.False(annotation.Destructive);
        Assert.True(annotation.Idempotent);
        Assert.False(annotation.OpenWorld);
    }

    [Fact]
    public async Task CopiedManagedExecutable_ReportsCopiedLocationButUnchangedEmbeddedBuild()
    {
        using var folder = new IdentityTestFolder();
        foreach (var name in new[] { "agentdebug-console.exe", "agentdebug-console.dll", "agentdebug-console.runtimeconfig.json", "agentdebug-console.deps.json" })
            File.Copy(Path.Combine(AppContext.BaseDirectory, name), Path.Combine(folder.Path, name));
        var original = await Run("console", "info");
        var copied = await RunPath(Path.Combine(folder.Path, "agentdebug-console.exe"), folder.Path, "info");
        Assert.Equal(0, copied.ExitCode);
        Assert.Equal(Path.Combine(folder.Path, "agentdebug-console.exe"), copied.Json.GetProperty("executablePath").GetString(), ignoreCase: true);
        Assert.Equal(original.Json.GetProperty("build").GetRawText(), copied.Json.GetProperty("build").GetRawText());
        Assert.Equal(original.Json.GetProperty("assemblySha256").GetString(), copied.Json.GetProperty("assemblySha256").GetString());
    }

    private static ToolkitInfoTools Tools(IReadOnlySet<string> groups) => new(
        new CliRunner(NullLogger.Instance, AppContext.BaseDirectory, AppContext.BaseDirectory, AppContext.BaseDirectory), groups);

    [Fact]
    public async Task McpStdio_RegistersInfoAndPreservesConsoleGateWithoutLiveTargets()
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "agentdebug-mcp.exe"))
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        start.ArgumentList.Add("--tools");
        start.ArgumentList.Add("ui");
        start.Environment.Remove("DOTNET_ROOT");
        start.Environment.Remove("DOTNET_ROOT_X64");
        start.Environment.Remove("DOTNET_ROOT_X86");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("MCP did not start.");
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            async Task<JsonElement> Request(int id, string method, object parameters)
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
                await process.StandardInput.FlushAsync();
                while (true)
                {
                    var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                    Assert.NotNull(line);
                    using var document = JsonDocument.Parse(line);
                    if (document.RootElement.TryGetProperty("id", out var responseId) && responseId.GetInt32() == id)
                    {
                        Assert.False(document.RootElement.TryGetProperty("error", out _), line);
                        return document.RootElement.GetProperty("result").Clone();
                    }
                }
            }
            await Request(1, "initialize", new { protocolVersion = "2024-11-05", capabilities = new { }, clientInfo = new { name = "identity-tests", version = "1" } });
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            await process.StandardInput.FlushAsync();
            var listed = await Request(2, "tools/list", new { });
            var names = listed.GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()).ToArray();
            Assert.Contains("toolkit_info", names);
            Assert.DoesNotContain(names, name => name!.StartsWith("console_", StringComparison.Ordinal));
            var call = await Request(3, "tools/call", new { name = "toolkit_info", arguments = new { tool = "server" } });
            Assert.False(call.TryGetProperty("isError", out var callError) && callError.GetBoolean(), call.GetRawText());
            using var response = JsonDocument.Parse(call.GetProperty("content")[0].GetProperty("text").GetString()!);
            var cli = response.RootElement.EnumerateObject().Single(property => property.Name.Equals("result", StringComparison.OrdinalIgnoreCase)).Value;
            Assert.True(cli.GetProperty("success").GetBoolean());
            Assert.Equal("server", cli.GetProperty("tool").GetString());
            Assert.Equal(Path.Combine(AppContext.BaseDirectory, "agentdebug-mcp.exe"), cli.GetProperty("executablePath").GetString(), ignoreCase: true);
            var gated = await Request(4, "tools/call", new { name = "toolkit_info", arguments = new { tool = "console" } });
            Assert.False(gated.TryGetProperty("isError", out var gateError) && gateError.GetBoolean(), gated.GetRawText());
            using var gateResponse = JsonDocument.Parse(gated.GetProperty("content")[0].GetProperty("text").GetString()!);
            var gateCli = gateResponse.RootElement.EnumerateObject().Single(property => property.Name.Equals("result", StringComparison.OrdinalIgnoreCase)).Value;
            Assert.Equal("tool-disabled", gateCli.GetProperty("error").GetString());
        }
        finally
        {
            process.StandardInput.Close();
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(shutdown.Token);
            Assert.Equal(0, process.ExitCode);
            testOutput.WriteLine(await stderr);
        }
    }

    [Fact]
    public void OldBinaryUnknownInfo_IsUnsupportedRatherThanInferredCapabilities()
    {
        var old = new AgentDebugToolkit.Mcp.CliResult { IsError = true, ExitCode = 1,
            Json = JsonSerializer.SerializeToElement(new { success = false, error = "invalid-argument", message = "Unknown verb 'info'." }) };
        var method = typeof(ToolkitInfoTools).GetMethod("RelayInfo", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = Assert.IsType<ToolInvocationResult>(method.Invoke(null, new object[] { old }));
        Assert.Equal(1, result.ExitCode);
        Assert.Null(result.WrapperErrorCode);
        Assert.Equal("info-not-supported", result.Result!.Value.GetProperty("error").GetString());
        Assert.False(result.Result.Value.TryGetProperty("capabilities", out _));
    }

    internal static Task<(int ExitCode, JsonElement Json)> Run(string tool, params string[] args) => RunPath(
        Path.Combine(AppContext.BaseDirectory, $"agentdebug-{tool}.exe"), AppContext.BaseDirectory, args);

    private static async Task<(int ExitCode, JsonElement Json)> RunPath(string path, string workingDirectory, params string[] args)
    {
        var start = new ProcessStartInfo(path) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = workingDirectory };
        start.Environment.Remove("DOTNET_ROOT");
        start.Environment.Remove("DOTNET_ROOT_X64");
        start.Environment.Remove("DOTNET_ROOT_X86");
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Identity process failed to start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        var stdout = await output;
        Assert.True(string.IsNullOrWhiteSpace(await error), await error);
        using var json = JsonDocument.Parse(stdout);
        return (process.ExitCode, json.RootElement.Clone());
    }
}

internal sealed class IdentityTestFolder : IDisposable
{
    internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agentdebug-identity-" + Guid.NewGuid().ToString("N"));
    internal IdentityTestFolder() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, recursive: true);
}