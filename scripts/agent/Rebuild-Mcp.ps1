<#
.SYNOPSIS
Closes Claude Desktop and rebuilds the MCP project and the UI automation CLI.
.DESCRIPTION
Modifies running process state and build output. Builds both agentdebug-mcp.exe and
agentdebug-ui.exe, because the MCP server launches the CLI as a separate process. Stops Claude processes by PID,
waits for agentdebug-mcp to exit, clears DOTNET_ROOT for the build, and reports
output timestamps. Does not stop MCP processes, stage, commit, or push files.
Requires approval before each run. Use -WhatIf for a read-only preview.
.PARAMETER RepoRoot
Repository root; defaults to two directories above this script.
.PARAMETER Configuration
Build configuration, Debug or Release.
.PARAMETER ExitTimeoutSeconds
Maximum time to wait for Claude and MCP processes to exit.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$RepoRoot = (Join-Path $PSScriptRoot '..\..'),
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [ValidateRange(1, 300)]
    [int]$ExitTimeoutSeconds = 15
)

$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
$project = Join-Path $RepoRoot 'src\AgentDebugToolkit.Mcp\AgentDebugToolkit.Mcp.csproj'
$exe = Join-Path $RepoRoot "src\AgentDebugToolkit.Mcp\bin\$Configuration\net8.0-windows\agentdebug-mcp.exe"
# The MCP server runs the UI automation CLI as a separate process and does not build it, so a
# rebuilt MCP next to a stale agentdebug-ui.exe reports new tools whose verbs the CLI does not know.
$cliProject = Join-Path $RepoRoot 'src\AgentDebugToolkit.UiAutomation.Cli\AgentDebugToolkit.UiAutomation.Cli.csproj'
$cliExe = Join-Path $RepoRoot "src\AgentDebugToolkit.UiAutomation.Cli\bin\$Configuration\net8.0-windows\agentdebug-ui.exe"
if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
    throw "MCP project not found: $project"
}
if (-not (Test-Path -LiteralPath $cliProject -PathType Leaf)) {
    throw "UI automation CLI project not found: $cliProject"
}
$dotnet = (Get-Command dotnet -CommandType Application).Source
$claude = @(Get-Process | Where-Object { $_.ProcessName -eq 'Claude' })
Write-Output ('Claude PIDs: {0}' -f (($claude | Select-Object -ExpandProperty Id) -join ', '))
if (-not $PSCmdlet.ShouldProcess($project, 'Close Claude Desktop processes and rebuild the MCP and the UI automation CLI')) {
    return
}

foreach ($process in $claude) {
    $process.Refresh()
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id
    }
}

$deadline = [DateTime]::UtcNow.AddSeconds($ExitTimeoutSeconds)
do {
    $remaining = @(Get-Process | Where-Object {
        $_.ProcessName -eq 'Claude' -or $_.ProcessName -eq 'agentdebug-mcp'
    })
    if ($remaining.Count -eq 0) { break }
    if ([DateTime]::UtcNow -ge $deadline) {
        $details = ($remaining | ForEach-Object { '{0} (PID {1})' -f $_.ProcessName, $_.Id }) -join ', '
        throw "Build not started; processes still running: $details"
    }
    Start-Sleep -Milliseconds 250
} while ($true)

$hadDotnetRoot = Test-Path Env:DOTNET_ROOT
$previousDotnetRoot = $env:DOTNET_ROOT
$buildStartedUtc = [DateTime]::UtcNow
try {
    Remove-Item Env:DOTNET_ROOT -ErrorAction SilentlyContinue
    & $dotnet build $project --configuration $Configuration --no-restore --no-incremental --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "MCP rebuild failed (exit $LASTEXITCODE). No retry performed; inspect build output for any file-lock owner."
    }

    & $dotnet build $cliProject --configuration $Configuration --no-restore --no-incremental --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "UI automation CLI rebuild failed (exit $LASTEXITCODE). The MCP was rebuilt; do not restart Claude until the CLI also builds."
    }
}
finally {
    if ($hadDotnetRoot) {
        $env:DOTNET_ROOT = $previousDotnetRoot
    }
    else {
        Remove-Item Env:DOTNET_ROOT -ErrorAction SilentlyContinue
    }
}

$output = Get-Item -LiteralPath $exe
if ($output.LastWriteTimeUtc -lt $buildStartedUtc) {
    throw "Build succeeded but executable timestamp predates this run: $exe"
}
$cliOutput = Get-Item -LiteralPath $cliExe
if ($cliOutput.LastWriteTimeUtc -lt $buildStartedUtc) {
    throw "Build succeeded but executable timestamp predates this run: $cliExe"
}
[pscustomobject]@{
    Executable = $output.FullName
    LastWriteTimeUtc = $output.LastWriteTimeUtc
    CliExecutable = $cliOutput.FullName
    CliLastWriteTimeUtc = $cliOutput.LastWriteTimeUtc
    BuildStartedUtc = $buildStartedUtc
}
