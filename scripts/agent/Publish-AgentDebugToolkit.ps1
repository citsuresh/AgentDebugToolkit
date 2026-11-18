<#
.SYNOPSIS
Builds a self-contained Windows x64 toolkit package in a timestamp-free folder.
.DESCRIPTION
Modifies build outputs and the destination folder. Publishes all four tools to a
fresh staging folder before installing the package. Existing packages require
-ReplaceExisting and are preserved in a uniquely named backup folder. Failed
staging folders are retained for diagnosis. Embeds portable-package identity,
writes package-manifest.json with per-tool provenance and SHA-256 payload hashes,
and verifies the package through every tool before installing it.
Does not stop processes or change
NuGet sources. Requires approval before each run; -WhatIf makes no changes.
.PARAMETER RepoRoot
Repository root; defaults to two directories above this script. Specify this
explicitly when running a copy of the script outside the repository.
.PARAMETER Destination
Final package folder; defaults to C:\Shared\AgentDebugToolkit-win-x64.
.PARAMETER Configuration
Build configuration, Release by default.
.PARAMETER ReplaceExisting
Allows replacement of an existing package, retaining the previous folder.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$RepoRoot = (Join-Path $PSScriptRoot '..\..'),
    [string]$Destination = 'C:\Shared\AgentDebugToolkit-win-x64',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$ReplaceExisting
)

$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
if (-not (Test-Path -LiteralPath (Join-Path $RepoRoot 'AgentDebugToolkit.slnx') -PathType Leaf)) {
    throw "Toolkit repository not found at '$RepoRoot'. Supply -RepoRoot with the source repository path."
}
$Destination = [IO.Path]::GetFullPath($Destination).TrimEnd('\')
$parent = Split-Path -Parent $Destination
if ([string]::IsNullOrWhiteSpace($parent) -or $Destination -eq $RepoRoot -or
    $RepoRoot.StartsWith($Destination + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $Destination.StartsWith($RepoRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Destination must not be a drive root, inside the repository, or an ancestor of the repository.'
}
if (Test-Path -LiteralPath $Destination) {
    if (-not (Test-Path -LiteralPath $Destination -PathType Container)) {
        throw "Destination is not a directory: $Destination"
    }
    if (-not $ReplaceExisting) {
        throw "Destination already exists: $Destination. Use -ReplaceExisting to retain it as a backup and install the new package."
    }
}

$tools = @(
    @{ Folder = 'ui'; Project = 'AgentDebugToolkit.UiAutomation.Cli'; Exe = 'agentdebug-ui' },
    @{ Folder = 'vs'; Project = 'AgentDebugToolkit.Debugger.VisualStudio'; Exe = 'agentdebug-vs' },
    @{ Folder = 'console'; Project = 'AgentDebugToolkit.ConsoleAutomation.Cli'; Exe = 'agentdebug-console' },
    @{ Folder = 'mcp'; Project = 'AgentDebugToolkit.Mcp'; Exe = 'agentdebug-mcp' }
)
foreach ($tool in $tools) {
    $tool.ProjectPath = Join-Path $RepoRoot "src\$($tool.Project)\$($tool.Project).csproj"
    if (-not (Test-Path -LiteralPath $tool.ProjectPath -PathType Leaf)) {
        throw "Project not found: $($tool.ProjectPath)"
    }
}
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
if (-not $PSCmdlet.ShouldProcess($Destination, 'Publish all four tools as self-contained win-x64 builds and install the package')) {
    return
}

$staging = Join-Path $parent ((Split-Path -Leaf $Destination) + '.staging-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging -ErrorAction Stop | Out-Null
Write-Output "Staging: $staging (retained if publishing fails)"
$hadDotnetRoot = Test-Path Env:DOTNET_ROOT
$previousDotnetRoot = $env:DOTNET_ROOT
$identities = @()
try {
    Remove-Item Env:DOTNET_ROOT -ErrorAction SilentlyContinue
    foreach ($tool in $tools) {
        $output = Join-Path $staging $tool.Folder
        & $dotnet publish $tool.ProjectPath --configuration $Configuration --runtime win-x64 --self-contained true `
            -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -p:ToolkitDeploymentKind=portable-package `
            --output $output --nologo
        if ($LASTEXITCODE -ne 0) {
            throw "Publishing $($tool.Exe) failed (exit $LASTEXITCODE). Destination unchanged; staging retained at $staging. Inspect the build output; no retry or NuGet source changes performed."
        }
        foreach ($file in @("$($tool.Exe).exe", "$($tool.Exe).dll", "$($tool.Exe).deps.json", "$($tool.Exe).runtimeconfig.json", 'coreclr.dll')) {
            if (-not (Test-Path -LiteralPath (Join-Path $output $file) -PathType Leaf)) {
                throw "Published package is missing $($tool.Folder)\$file. Destination unchanged; staging retained at $staging."
            }
        }
        $identityJson = & (Join-Path $output "$($tool.Exe).exe") info | Out-String
        if ($LASTEXITCODE -ne 0) { throw "Identity query failed for $($tool.Exe). Staging retained at $staging." }
        $identity = $identityJson | ConvertFrom-Json -ErrorAction Stop
        $expectedTool = $tool.Folder
        if ($expectedTool -eq 'mcp') { $expectedTool = 'server' }
        if ($identity.success -ne $true -or $identity.tool -ne $expectedTool -or
            $identity.build.deploymentKind -ne 'portable-package' -or $identity.build.selfContained -ne $true -or
            $identity.build.configuration -ne $Configuration -or $identity.build.runtimeIdentifier -ne 'win-x64') {
            throw "Unexpected embedded identity for $($tool.Exe). Destination unchanged; staging retained at $staging."
        }
        $identities += $identity
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

$docs = Join-Path $staging 'docs'
New-Item -ItemType Directory -Path $docs -ErrorAction Stop | Out-Null
Copy-Item -LiteralPath (Join-Path $RepoRoot 'README.md') -Destination $staging -ErrorAction Stop
foreach ($name in @('CLI_CONTRACT.md', 'MCP_SERVER.md')) {
    Copy-Item -LiteralPath (Join-Path $RepoRoot "docs\$name") -Destination $docs -ErrorAction Stop
}
@'
AgentDebugToolkit - Windows x64 portable package

Copy this entire folder to the destination machine. Keep each tool's directory
and dependencies intact. .NET is included; no .NET SDK/runtime install is needed.
The VS debugger tool requires a running Visual Studio instance. Console automation
requires Windows with ConPTY support. UI automation requires an interactive desktop.

Executables:
  ui\agentdebug-ui.exe
  vs\agentdebug-vs.exe
  console\agentdebug-console.exe
  mcp\agentdebug-mcp.exe

MCP setup (edit these paths if you copy the package elsewhere):
{
  "mcpServers": {
    "agentdebug-toolkit": {
      "command": "C:\\Shared\\AgentDebugToolkit-win-x64\\mcp\\agentdebug-mcp.exe",
      "args": ["--tools", "vs,ui"],
      "env": {
        "AGENTDEBUG_UI_BIN": "C:\\Shared\\AgentDebugToolkit-win-x64\\ui",
        "AGENTDEBUG_VS_BIN": "C:\\Shared\\AgentDebugToolkit-win-x64\\vs",
        "AGENTDEBUG_CONSOLE_BIN": "C:\\Shared\\AgentDebugToolkit-win-x64\\console"
      }
    }
  }
}

Console MCP tools are opt-in. See docs\MCP_SERVER.md for enablement and the
executable allowlist restrictions; never allowlist shells or interpreters.
See docs\CLI_CONTRACT.md for CLI usage. Source build instructions in README.md
apply to the source repository, not this portable package.

Each executable supports info --verifyPackage. Require packageVerification.status
matched to confirm the package still matches package-manifest.json, including
copied packages. not-present is not verification. Hashes detect accidental drift,
not an attacker who can replace both the files and the unsigned manifest.
'@ | Set-Content -LiteralPath (Join-Path $staging 'START-HERE.txt') -Encoding UTF8

$source = $identities[0].build
foreach ($identity in $identities) {
    if ($identity.build.sourceCommit -ne $source.sourceCommit -or $identity.build.sourceDirty -ne $source.sourceDirty) {
        throw "Source identity changed during publishing. Destination unchanged; staging retained at $staging."
    }
}
$toolIdentities = @()
for ($i = 0; $i -lt $tools.Count; $i++) {
    $identity = $identities[$i]
    $tool = $tools[$i]
    $toolIdentities += [ordered]@{
        tool = $identity.tool
        executablePath = "$($tool.Folder)\$($tool.Exe).exe"
        assemblyPath = "$($tool.Folder)\$($tool.Exe).dll"
        version = $identity.version
        moduleVersionId = $identity.moduleVersionId
        contractVersion = $identity.contractVersion
        capabilities = @($identity.capabilities)
        build = $identity.build
    }
}
$files = @(Get-ChildItem -LiteralPath $staging -Recurse -Force -ErrorAction Stop | ForEach-Object {
    if (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Package reparse points are not supported: $($_.FullName)" }
    if (-not $_.PSIsContainer) {
        [ordered]@{ path = $_.FullName.Substring($staging.Length + 1); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256 -ErrorAction Stop).Hash.ToLowerInvariant() }
    }
})
$manifest = [ordered]@{
    schemaVersion = 1
    createdUtc = [DateTime]::UtcNow.ToString('o')
    sourceCommit = $source.sourceCommit
    sourceDirty = $source.sourceDirty
    configuration = $Configuration
    runtimeIdentifier = 'win-x64'
    tools = $toolIdentities
    files = $files
}
$manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $staging 'package-manifest.json') -Encoding UTF8
foreach ($tool in $tools) {
    $verificationJson = & (Join-Path $staging "$($tool.Folder)\$($tool.Exe).exe") info --verifyPackage | Out-String
    if ($LASTEXITCODE -ne 0) { throw "Package verification failed for $($tool.Exe). Destination unchanged; staging retained at $staging. $verificationJson" }
    $verification = $verificationJson | ConvertFrom-Json -ErrorAction Stop
    if ($verification.success -ne $true -or $verification.packageVerification.status -ne 'matched') {
        throw "Package verification was not matched for $($tool.Exe). Destination unchanged; staging retained at $staging."
    }
}

$backup = $null
if (Test-Path -LiteralPath $Destination) {
    if (-not $ReplaceExisting) {
        throw "Destination appeared during publishing: $Destination. Staging retained at $staging."
    }
    $backup = $Destination + '.previous-' + [Guid]::NewGuid().ToString('N')
    [IO.Directory]::Move($Destination, $backup)
    Write-Output "Previous package retained at: $backup"
}
try {
    [IO.Directory]::Move($staging, $Destination)
}
catch {
    if ($backup -and -not (Test-Path -LiteralPath $Destination)) {
        [IO.Directory]::Move($backup, $Destination)
    }
    throw
}
Write-Output "Package ready: $Destination"
