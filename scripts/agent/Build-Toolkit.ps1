<#
.SYNOPSIS
Builds AgentDebugToolkit in place (Debug) and records what was built in a .built-version marker.
.DESCRIPTION
Used by Install-Skills.ps1 from the Visual-Studio-Copilot-Skills repo, which keeps a clone of this
repo under %USERPROFILE%\.copilot\toolkits\AgentDebugToolkit and rebuilds it when VERSION or the commit changes.
The skills run src\<project>\bin\Debug\net8.0-windows\agentdebug-*.exe, so this script builds Debug.
Steps: check a .NET SDK is installed, dotnet build of the four tool projects (ui, vs, console, mcp; the
test project is not built), smoke check (agentdebug-ui and agentdebug-vs, run with no verb, must print
their JSON invalid-argument error, which proves they start), then write .built-version.
The marker is written only after every step succeeds, so a failed build is never reported as current.
Modifies only bin/obj under this checkout and .built-version. Does not start Visual Studio or touch any UI.
Exit code 0 on success, 1 on any failure.
.PARAMETER SourceRoot
Root of the AgentDebugToolkit checkout. Defaults to the folder two levels above this script (resolved in
the body, because $PSScriptRoot is empty in a parameter default on Windows PowerShell 5.1).
.PARAMETER SkipSmokeCheck
Skip the smoke check (faster; the exes are then only checked for existence).
.EXAMPLE
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\agent\Build-Toolkit.ps1
#>
[CmdletBinding()]
param(
    [string]$SourceRoot,
    [switch]$SkipSmokeCheck
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Fail([string]$Message) {
    Write-Host "BUILD: Failed - $Message" -ForegroundColor Red
    exit 1
}

if (-not $SourceRoot) { $SourceRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot) }
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$versionFile = Join-Path $SourceRoot 'VERSION'
$marker = Join-Path $SourceRoot '.built-version'

if (-not (Test-Path -LiteralPath $versionFile)) { Fail "VERSION file not found in $SourceRoot" }
$version = (Get-Content -LiteralPath $versionFile -Raw).Trim()
if ($version -notmatch '^\d+$') { Fail "VERSION must be a whole number, found '$version'" }

$tools = @(
    @{ Project = 'AgentDebugToolkit.UiAutomation.Cli'; Exe = 'agentdebug-ui'; Smoke = $true },
    @{ Project = 'AgentDebugToolkit.Debugger.VisualStudio'; Exe = 'agentdebug-vs'; Smoke = $true },
    @{ Project = 'AgentDebugToolkit.ConsoleAutomation.Cli'; Exe = 'agentdebug-console'; Smoke = $false },
    @{ Project = 'AgentDebugToolkit.Mcp'; Exe = 'agentdebug-mcp'; Smoke = $false }
)
foreach ($tool in $tools) {
    $tool.ProjectPath = Join-Path $SourceRoot "src\$($tool.Project)\$($tool.Project).csproj"
    $tool.ExePath = Join-Path $SourceRoot "src\$($tool.Project)\bin\Debug\net8.0-windows\$($tool.Exe).exe"
    if (-not (Test-Path -LiteralPath $tool.ProjectPath)) { Fail "Project not found: $($tool.ProjectPath)" }
}

if (-not (Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue)) {
    Fail 'No .NET SDK found (dotnet is not on PATH). Install a .NET SDK 8 or later, then run again.'
}
$sdks = @(& dotnet --list-sdks 2>$null)
if ($sdks.Count -eq 0) { Fail 'dotnet is installed but no SDK is present (runtime only). Install a .NET SDK 8 or later.' }

$commit = ''
if (Get-Command git -CommandType Application -ErrorAction SilentlyContinue) {
    $commit = ((& git -C $SourceRoot rev-parse HEAD 2>$null) | Out-String).Trim()
}

# A stale marker must not survive a failed build.
if (Test-Path -LiteralPath $marker) { Remove-Item -LiteralPath $marker -Force }

Write-Host "Building AgentDebugToolkit version $version (Debug)..." -ForegroundColor Cyan
foreach ($tool in $tools) {
    & dotnet build $tool.ProjectPath -c Debug --nologo -v q -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { Fail "dotnet build of $($tool.Project) exited with code $LASTEXITCODE" }
    if (-not (Test-Path -LiteralPath $tool.ExePath)) { Fail "Build succeeded but $($tool.ExePath) is missing" }
}

if (-not $SkipSmokeCheck) {
    foreach ($tool in $tools | Where-Object { $_.Smoke }) {
        try {
            $out = & $tool.ExePath 2>&1 | Out-String
        }
        catch {
            Fail "Smoke check: $($tool.Exe) could not be started: $($_.Exception.Message)"
        }
        if ($out -notmatch 'invalid-argument') {
            Fail "Smoke check: $($tool.Exe) did not print the expected JSON error. Output: $($out.Trim())"
        }
    }
}

$json = "{`r`n  `"toolkit`": `"AgentDebugToolkit`",`r`n  `"version`": $version,`r`n  `"commit`": `"$commit`",`r`n  `"builtUtc`": `"$([DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ'))`"`r`n}`r`n"
[IO.File]::WriteAllText($marker, $json, (New-Object Text.UTF8Encoding($false)))
Write-Host "BUILD: OK (AgentDebugToolkit version $version, commit $($commit.Substring(0, [Math]::Min(7, $commit.Length))))" -ForegroundColor Green
exit 0
