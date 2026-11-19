<#
.SYNOPSIS
Builds AgentDebugToolkit in place (Debug) and records what was built in a .built-version marker.
.DESCRIPTION
Used by Install-Skills.ps1 from the Visual-Studio-Copilot-Skills repo, which keeps a clone of this
repo under %USERPROFILE%\.copilot\toolkits\AgentDebugToolkit and rebuilds it when VERSION or the commit changes.
The skills run src\<project>\bin\Debug\net8.0-windows\agentdebug-*.exe, so this script builds Debug.
Steps: invalidate the old marker, check a .NET SDK is installed, build the four tool projects,
require info identity/path/hash checks for every executable, optionally smoke check ui/vs, then
atomically write .built-version with the legacy fields and exact executable paths under tools.
The marker is written only after every step succeeds, so a failed build is never reported as current.
Modifies only bin/obj under this checkout and .built-version. Does not start Visual Studio or touch any UI.
Exit code 0 on success, 1 on any failure.
.PARAMETER SourceRoot
Root of the AgentDebugToolkit checkout. Defaults to the folder two levels above this script (resolved in
the body, because $PSScriptRoot is empty in a parameter default on Windows PowerShell 5.1).
.PARAMETER SkipSmokeCheck
Skip only the legacy no-verb smoke check. Required info checks still run for all four tools.
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

function Invoke-ToolkitCommand {
    param([string]$FilePath, [string[]]$Arguments)
    $ErrorActionPreference = 'Continue'
    $output = @(& $FilePath @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    foreach ($item in $output) {
        if ($item -is [Management.Automation.ErrorRecord] -and
            $item.FullyQualifiedErrorId -notin @('NativeCommandError', 'NativeCommandErrorMessage')) {
            throw $item
        }
    }
    return @{ ExitCode = $exitCode; Output = ($output | Out-String) }
}

function Invoke-ToolkitBuild {
    param(
        [string]$SourceRoot,
        [switch]$SkipSmokeCheck,
        [scriptblock]$RunCommand = { param($FilePath, $Arguments) Invoke-ToolkitCommand -FilePath $FilePath -Arguments $Arguments },
        [scriptblock]$FindApplication = { param($Name) Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source }
    )

    if (-not $SourceRoot) { $SourceRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot) }
    $SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
    $versionFile = Join-Path $SourceRoot 'VERSION'
    $marker = Join-Path $SourceRoot '.built-version'
    if (Test-Path -LiteralPath $marker) { Remove-Item -LiteralPath $marker -Force }

    if (-not (Test-Path -LiteralPath $versionFile -PathType Leaf)) { throw "VERSION file not found in $SourceRoot" }
    $version = (Get-Content -LiteralPath $versionFile -Raw).Trim()
    if ($version -notmatch '^\d+$') { throw "VERSION must be a whole number, found '$version'" }

    $tools = @(
        @{ Tool = 'ui'; Project = 'AgentDebugToolkit.UiAutomation.Cli'; Exe = 'agentdebug-ui'; Smoke = $true },
        @{ Tool = 'vs'; Project = 'AgentDebugToolkit.Debugger.VisualStudio'; Exe = 'agentdebug-vs'; Smoke = $true },
        @{ Tool = 'console'; Project = 'AgentDebugToolkit.ConsoleAutomation.Cli'; Exe = 'agentdebug-console'; Smoke = $false },
        @{ Tool = 'server'; Project = 'AgentDebugToolkit.Mcp'; Exe = 'agentdebug-mcp'; Smoke = $false }
    )
    foreach ($tool in $tools) {
        $tool.ProjectPath = Join-Path $SourceRoot "src\$($tool.Project)\$($tool.Project).csproj"
        $tool.ExePath = Join-Path $SourceRoot "src\$($tool.Project)\bin\Debug\net8.0-windows\$($tool.Exe).exe"
        if (-not (Test-Path -LiteralPath $tool.ProjectPath -PathType Leaf)) { throw "Project not found: $($tool.ProjectPath)" }
    }

    $dotnet = & $FindApplication -Name 'dotnet'
    if (-not $dotnet) { throw 'No .NET SDK found (dotnet is not on PATH). Install a .NET SDK 8 or later, then run again.' }
    $sdks = & $RunCommand -FilePath $dotnet -Arguments @('--list-sdks')
    if ($sdks.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($sdks.Output)) {
        throw 'dotnet could not list an installed SDK. Install a .NET SDK 8 or later.'
    }

    $commit = ''
    $git = & $FindApplication -Name 'git'
    if ($git) {
        $gitResult = & $RunCommand -FilePath $git -Arguments @('-C', $SourceRoot, 'rev-parse', 'HEAD')
        if ($gitResult.ExitCode -eq 0) { $commit = $gitResult.Output.Trim() }
        else { Write-Warning "Git commit unavailable: $($gitResult.Output.Trim())" }
    }

    Write-Host "Building AgentDebugToolkit version $version (Debug)..." -ForegroundColor Cyan
    foreach ($tool in $tools) {
        $build = & $RunCommand -FilePath $dotnet -Arguments @('build', $tool.ProjectPath, '-c', 'Debug', '--nologo', '-v', 'q', '-p:NuGetAudit=false')
        if ($build.Output) { Write-Host $build.Output.TrimEnd() }
        if ($build.ExitCode -ne 0) { throw "dotnet build of $($tool.Project) exited with code $($build.ExitCode)" }
        if (-not (Test-Path -LiteralPath $tool.ExePath -PathType Leaf)) { throw "Build succeeded but $($tool.ExePath) is missing" }
    }

    $paths = [ordered]@{}
    $identityCommitSet = $false
    $identityCommit = $null
    foreach ($tool in $tools) {
        $result = & $RunCommand -FilePath $tool.ExePath -Arguments @('info')
        if ($result.ExitCode -ne 0) { throw "Required info check failed for $($tool.Exe) (exit $($result.ExitCode)): $($result.Output)" }
        $info = $result.Output | ConvertFrom-Json -ErrorAction Stop
        $assemblyPath = [IO.Path]::ChangeExtension($tool.ExePath, '.dll')
        if ($info.success -isnot [bool] -or $info.success -ne $true -or $info.tool -cne $tool.Tool -or
            $info.contractVersion -ne 1 -or @($info.capabilities) -cnotcontains 'toolkit.info.v1' -or
            -not [string]::Equals($info.executablePath, $tool.ExePath, [StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals($info.assemblyPath, $assemblyPath, [StringComparison]::OrdinalIgnoreCase) -or
            $info.build.configuration -cne 'Debug' -or $info.build.deploymentKind -cne 'managed-source' -or
            ($commit -and $info.build.sourceCommit -cne $commit)) {
            throw "Unexpected required info identity for $($tool.Exe); no success marker was written."
        }
        if (-not $identityCommitSet) {
            $identityCommit = $info.build.sourceCommit
            $identityCommitSet = $true
        }
        elseif ($info.build.sourceCommit -cne $identityCommit) {
            throw "Inconsistent binary source commit for $($tool.Exe); no success marker was written."
        }
        if ($info.executableSha256 -ine (Get-FileHash -LiteralPath $tool.ExePath -Algorithm SHA256).Hash -or
            $info.assemblySha256 -ine (Get-FileHash -LiteralPath $assemblyPath -Algorithm SHA256).Hash) {
            throw "Required info hash mismatch for $($tool.Exe); no success marker was written."
        }
        $paths[$tool.Tool] = [ordered]@{ executablePath = $tool.ExePath }
    }

    if (-not $SkipSmokeCheck) {
        foreach ($tool in $tools | Where-Object { $_.Smoke }) {
            $result = & $RunCommand -FilePath $tool.ExePath -Arguments @()
            if ($result.Output -notmatch 'invalid-argument') {
                throw "Smoke check: $($tool.Exe) did not print the expected JSON error. Output: $($result.Output.Trim())"
            }
        }
    }

    $metadata = [ordered]@{
        toolkit = 'AgentDebugToolkit'
        version = [long]$version
        commit = $commit
        builtUtc = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
        schemaVersion = 1
        tools = $paths
    }
    $temporaryMarker = $marker + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllText($temporaryMarker, ($metadata | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))
        [IO.File]::Move($temporaryMarker, $marker)
    }
    finally {
        if (Test-Path -LiteralPath $temporaryMarker) { Remove-Item -LiteralPath $temporaryMarker -Force }
    }
    Write-Host "BUILD: OK (AgentDebugToolkit version $version, commit $($commit.Substring(0, [Math]::Min(7, $commit.Length))))" -ForegroundColor Green
    return $metadata
}

if ($MyInvocation.InvocationName -ne '.') {
    try {
        $null = Invoke-ToolkitBuild -SourceRoot $SourceRoot -SkipSmokeCheck:$SkipSmokeCheck
        exit 0
    }
    catch { Fail $_.Exception.Message }
}
