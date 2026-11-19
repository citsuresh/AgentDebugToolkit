using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AgentDebugToolkit.UiAutomation.Tests;

public sealed class ManagedBuildMarkerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulBuild_PreservesLegacyFieldsAndPublishesVerifiedExactPaths(bool skipSmoke)
    {
        var result = await Run("success", skipSmoke);
        Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());
        Assert.True(result.GetProperty("markerExists").GetBoolean());
        var marker = result.GetProperty("marker");
        Assert.Equal("AgentDebugToolkit", marker.GetProperty("toolkit").GetString());
        Assert.Equal(42, marker.GetProperty("version").GetInt64());
        Assert.Equal(new string('a', 40), marker.GetProperty("commit").GetString());
        Assert.True(DateTime.TryParse(marker.GetProperty("builtUtc").GetString(), out _));
        Assert.Equal(1, marker.GetProperty("schemaVersion").GetInt32());
        var root = result.GetProperty("root").GetString()!;
        var expected = new[]
        {
            ("ui", "AgentDebugToolkit.UiAutomation.Cli", "agentdebug-ui"),
            ("vs", "AgentDebugToolkit.Debugger.VisualStudio", "agentdebug-vs"),
            ("console", "AgentDebugToolkit.ConsoleAutomation.Cli", "agentdebug-console"),
            ("server", "AgentDebugToolkit.Mcp", "agentdebug-mcp")
        };
        Assert.Equal(4, marker.GetProperty("tools").EnumerateObject().Count());
        var calls = result.GetProperty("calls").EnumerateArray().Select(call => call.GetString()!).ToArray();
        Assert.Equal(4, calls.Count(call => call.StartsWith("build:", StringComparison.Ordinal)));
        Assert.Equal(4, calls.Count(call => call.StartsWith("info:", StringComparison.Ordinal)));
        Assert.Equal(skipSmoke ? 0 : 2, calls.Count(call => call.StartsWith("smoke:", StringComparison.Ordinal)));
        foreach (var (tool, project, executable) in expected)
        {
            var path = marker.GetProperty("tools").GetProperty(tool).GetProperty("executablePath").GetString();
            Assert.NotNull(path);
            Assert.Equal(Path.Combine(root, "src", project, "bin", "Debug", "net8.0-windows", executable + ".exe"), path);
            Assert.True(Path.IsPathFullyQualified(path));
            Assert.Contains("checkout with spaces", path);
            Assert.Contains("info:" + executable, calls);
        }
        Assert.True(result.GetProperty("markerAbsentDuringCommands").GetBoolean());
        Assert.Equal(0, result.GetProperty("temporaryMarkers").GetInt32());
    }

    [Theory]
    [InlineData("missing-version")]
    [InlineData("invalid-version")]
    [InlineData("missing-project")]
    [InlineData("no-dotnet")]
    [InlineData("sdk-error")]
    [InlineData("no-sdk")]
    [InlineData("build-failed")]
    [InlineData("missing-executable")]
    [InlineData("missing-assembly")]
    [InlineData("info-failed")]
    [InlineData("old-unsupported-info")]
    [InlineData("malformed-info")]
    [InlineData("array-info")]
    [InlineData("string-success")]
    [InlineData("wrong-tool")]
    [InlineData("wrong-path")]
    [InlineData("wrong-assembly")]
    [InlineData("wrong-configuration")]
    [InlineData("wrong-kind")]
    [InlineData("wrong-commit")]
    [InlineData("wrong-contract")]
    [InlineData("missing-capability")]
    [InlineData("wrong-executable-hash")]
    [InlineData("wrong-assembly-hash")]
    [InlineData("smoke-failed")]
    public async Task AnyPrerequisiteBuildOrIdentityFailure_InvalidatesOldSuccessMarker(string scenario)
    {
        var result = await Run(scenario);
        Assert.False(result.GetProperty("success").GetBoolean(), result.ToString());
        Assert.False(result.GetProperty("markerExists").GetBoolean(), result.ToString());
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("failure").GetString()));
        Assert.True(result.GetProperty("markerAbsentDuringCommands").GetBoolean());
        Assert.Equal(0, result.GetProperty("temporaryMarkers").GetInt32());
    }

    [Fact]
    public async Task NativeCommandBoundary_PreservesStderrAndNonzeroExitWithoutThrowingOnStderr()
    {
        var result = await Run("native-error");
        Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());
        Assert.Equal(7, result.GetProperty("nativeExitCode").GetInt32());
        Assert.Contains("injected native stderr", result.GetProperty("nativeOutput").GetString());
    }

    [Theory]
    [InlineData("no-git")]
    [InlineData("git-failed")]
    [InlineData("empty-commit")]
    [InlineData("no-git-unknown")]
    [InlineData("git-failed-unknown")]
    [InlineData("empty-commit-unknown")]
    [InlineData("no-git-mixed")]
    [InlineData("git-failed-mixed")]
    [InlineData("empty-commit-mixed")]
    public async Task UnknownCheckoutCommit_RequiresConsistentBinaryCommits(string scenario)
    {
        var result = await Run(scenario);
        var consistent = !scenario.EndsWith("-mixed", StringComparison.Ordinal);
        Assert.Equal(consistent, result.GetProperty("success").GetBoolean());
        Assert.Equal(consistent, result.GetProperty("markerExists").GetBoolean());
        Assert.Equal(4, result.GetProperty("calls").EnumerateArray().Count(call => call.GetString()!.StartsWith("info:", StringComparison.Ordinal)));
        if (consistent) Assert.Equal("", result.GetProperty("marker").GetProperty("commit").GetString());
        else Assert.Contains("Inconsistent binary source commit", result.GetProperty("failure").GetString());
        if (scenario.StartsWith("git-failed", StringComparison.Ordinal))
            Assert.Contains("Git commit unavailable", result.GetProperty("warnings").GetString());
        Assert.True(result.GetProperty("markerAbsentDuringCommands").GetBoolean());
        Assert.Equal(0, result.GetProperty("temporaryMarkers").GetInt32());
    }

    [Fact]
    public async Task SkipSmoke_DoesNotBypassMandatoryServerIdentityCheck()
    {
        var result = await Run("info-failed", skipSmoke: true);
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.False(result.GetProperty("markerExists").GetBoolean());
        Assert.Contains("Required info check failed for agentdebug-mcp", result.GetProperty("failure").GetString());
        Assert.Contains("info:agentdebug-mcp", result.GetProperty("calls").EnumerateArray().Select(call => call.GetString()));
    }

    private static async Task<JsonElement> Run(string scenario, bool skipSmoke = false)
    {
        using var folder = new IdentityTestFolder();
        var root = Path.Combine(folder.Path, "checkout with spaces 'quoted'");
        Directory.CreateDirectory(root);
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "scripts", "agent", "Build-Toolkit.ps1")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var scriptPath = Path.Combine(directory.FullName, "scripts", "agent", "Build-Toolkit.ps1");
        static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        var script = $"$ProgressPreference = 'SilentlyContinue'; . {Literal(scriptPath)}; $root = {Literal(root)}; $scenario = {Literal(scenario)}; $skip = ${skipSmoke.ToString().ToLowerInvariant()};\n" + Harness;
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell test harness did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) process.Kill(); }
        var output = await stdout;
        var error = await stderr;
        Assert.True(process.ExitCode == 0, output + error);
        Assert.True(string.IsNullOrWhiteSpace(error), error);
        using var json = JsonDocument.Parse(output);
        return json.RootElement.Clone();
    }

    private const string Harness = """
        $markerPath = Join-Path $root '.built-version'
        [IO.File]::WriteAllText($markerPath, '{"toolkit":"AgentDebugToolkit","version":1,"commit":"old","builtUtc":"old"}')
        [IO.File]::WriteAllText((Join-Path $root 'VERSION'), '42')
        $projects = @(
            @{ Tool = 'ui'; Project = 'AgentDebugToolkit.UiAutomation.Cli'; Exe = 'agentdebug-ui' },
            @{ Tool = 'vs'; Project = 'AgentDebugToolkit.Debugger.VisualStudio'; Exe = 'agentdebug-vs' },
            @{ Tool = 'console'; Project = 'AgentDebugToolkit.ConsoleAutomation.Cli'; Exe = 'agentdebug-console' },
            @{ Tool = 'server'; Project = 'AgentDebugToolkit.Mcp'; Exe = 'agentdebug-mcp' }
        )
        foreach ($project in $projects) {
            $project.Path = Join-Path $root "src\$($project.Project)\$($project.Project).csproj"
            [IO.Directory]::CreateDirectory((Split-Path -Parent $project.Path)) | Out-Null
            [IO.File]::WriteAllText($project.Path, 'stub project: never built')
        }
        if ($scenario -eq 'missing-version') { Remove-Item -LiteralPath (Join-Path $root 'VERSION') }
        if ($scenario -eq 'invalid-version') { [IO.File]::WriteAllText((Join-Path $root 'VERSION'), 'invalid') }
        if ($scenario -eq 'missing-project') { Remove-Item -LiteralPath $projects[3].Path }
        $calls = New-Object 'System.Collections.Generic.List[string]'
        $observations = @{ MarkerAbsent = $true }
        $find = {
            param($Name)
            if (Test-Path -LiteralPath $markerPath) { $observations.MarkerAbsent = $false }
            if (($scenario -eq 'no-dotnet' -and $Name -eq 'dotnet') -or ($scenario.StartsWith('no-git') -and $Name -eq 'git')) { return $null }
            return $Name
        }
        $run = {
            param($FilePath, $Arguments)
            if (Test-Path -LiteralPath $markerPath) { $observations.MarkerAbsent = $false }
            if ($FilePath -eq 'dotnet' -and $Arguments[0] -eq '--list-sdks') {
                $calls.Add('sdk')
                if ($scenario -eq 'sdk-error') { return @{ ExitCode = 1; Output = 'SDK listing failed' } }
                if ($scenario -eq 'no-sdk') { return @{ ExitCode = 0; Output = '' } }
                return @{ ExitCode = 0; Output = '8.0.100 [stub SDK]' }
            }
            if ($FilePath -eq 'git') {
                $calls.Add('git')
                if ($scenario.StartsWith('git-failed')) { return @{ ExitCode = 1; Output = 'injected Git failure' } }
                if ($scenario.StartsWith('empty-commit')) { return @{ ExitCode = 0; Output = '' } }
                return @{ ExitCode = 0; Output = ('a' * 40) }
            }
            if ($FilePath -eq 'dotnet' -and $Arguments[0] -eq 'build') {
                $project = @($projects | Where-Object { $_.Path -eq $Arguments[1] })[0]
                $calls.Add('build:' + $project.Project)
                if ($scenario -eq 'build-failed' -and $project.Tool -eq 'server') { return @{ ExitCode = 1; Output = 'build failed' } }
                $bin = Join-Path (Split-Path -Parent $project.Path) 'bin\Debug\net8.0-windows'
                [IO.Directory]::CreateDirectory($bin) | Out-Null
                if ($scenario -ne 'missing-executable' -or $project.Tool -ne 'server') {
                    [IO.File]::WriteAllText((Join-Path $bin ($project.Exe + '.exe')), 'not an executable; command injection only')
                }
                if ($scenario -ne 'missing-assembly' -or $project.Tool -ne 'server') {
                    [IO.File]::WriteAllText((Join-Path $bin ($project.Exe + '.dll')), 'stub assembly')
                }
                return @{ ExitCode = 0; Output = '' }
            }
            $name = [IO.Path]::GetFileNameWithoutExtension($FilePath)
            if ($Arguments.Count -eq 0) {
                $calls.Add('smoke:' + $name)
                return @{ ExitCode = 1; Output = $(if ($scenario -eq 'smoke-failed') { 'unexpected' } else { '{"success":false,"error":"invalid-argument"}' }) }
            }
            if ($Arguments.Count -ne 1 -or $Arguments[0] -ne 'info') { throw 'Unexpected injected command' }
            $calls.Add('info:' + $name)
            $project = @($projects | Where-Object { $_.Exe -eq $name })[0]
            $assembly = [IO.Path]::ChangeExtension($FilePath, '.dll')
            if (-not (Test-Path -LiteralPath $assembly)) { return @{ ExitCode = 1; Output = 'missing assembly' } }
            $info = @{
                success = $true; tool = $project.Tool; contractVersion = 1; capabilities = @('toolkit.info.v1')
                executablePath = $FilePath; assemblyPath = $assembly
                executableSha256 = (Get-FileHash -LiteralPath $FilePath -Algorithm SHA256).Hash
                assemblySha256 = (Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash
                build = @{ configuration = 'Debug'; deploymentKind = 'managed-source'; sourceCommit = ('a' * 40) }
            }
            if ($scenario.EndsWith('-unknown')) { $info.build.sourceCommit = $null }
            if ($project.Tool -eq 'server') {
                if ($scenario.EndsWith('-mixed')) { $info.build.sourceCommit = ('b' * 40) }
                switch ($scenario) {
                    'info-failed' { return @{ ExitCode = 1; Output = 'info failed' } }
                    'old-unsupported-info' { return @{ ExitCode = 1; Output = '{"success":false,"error":"invalid-argument","message":"Unknown verb info"}' } }
                    'malformed-info' { return @{ ExitCode = 0; Output = 'not JSON' } }
                    'array-info' { return @{ ExitCode = 0; Output = '[]' } }
                    'string-success' { $info.success = 'true' }
                    'wrong-tool' { $info.tool = 'ui' }
                    'wrong-path' { $info.executablePath = Join-Path $root 'wrong.exe' }
                    'wrong-assembly' { $info.assemblyPath = Join-Path $root 'wrong.dll' }
                    'wrong-configuration' { $info.build.configuration = 'Release' }
                    'wrong-kind' { $info.build.deploymentKind = 'portable-package' }
                    'wrong-commit' { $info.build.sourceCommit = ('b' * 40) }
                    'wrong-contract' { $info.contractVersion = 999 }
                    'missing-capability' { $info.capabilities = @() }
                    'wrong-executable-hash' { $info.executableSha256 = ('0' * 64) }
                    'wrong-assembly-hash' { $info.assemblySha256 = ('0' * 64) }
                }
            }
            return @{ ExitCode = 0; Output = ($info | ConvertTo-Json -Depth 6 -Compress) }
        }
        $success = $false
        $failure = $null
        $native = @{ ExitCode = $null; Output = $null }
        if ($scenario -eq 'native-error') {
            $native = Invoke-ToolkitCommand -FilePath (Join-Path $PSHOME 'powershell.exe') -Arguments @('-NoProfile', '-NonInteractive', '-Command', "[Console]::Error.WriteLine('injected native stderr'); exit 7")
        }
        $warningPath = Join-Path $root 'warnings.txt'
        try { $null = Invoke-ToolkitBuild -SourceRoot $root -SkipSmokeCheck:$skip -RunCommand $run -FindApplication $find 3>$warningPath 6>$null; $success = $true }
        catch { $failure = $_.Exception.Message }
        $exists = Test-Path -LiteralPath $markerPath
        $marker = if ($exists) { Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json } else { $null }
        [ordered]@{
            success = $success; failure = $failure; root = $root; markerExists = $exists; marker = $marker
            calls = @($calls.ToArray()); markerAbsentDuringCommands = $observations.MarkerAbsent
            warnings = [IO.File]::ReadAllText($warningPath)
            nativeExitCode = $native.ExitCode; nativeOutput = $native.Output
            temporaryMarkers = @(Get-ChildItem -LiteralPath $root -Filter '.built-version.*.tmp').Count
        } | ConvertTo-Json -Depth 12 -Compress
        """;
}