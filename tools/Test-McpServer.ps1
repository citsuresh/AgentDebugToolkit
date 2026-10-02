<#
.SYNOPSIS
    Live verification for AgentDebugToolkit.Mcp (agentdebug-mcp.exe): starts the server over
    stdio, performs the MCP initialize handshake, lists tools, and calls read-only tools against
    a running Visual Studio instance (vs_debugger_status, ui_list_windows, vs_evaluate), also
    confirming that a CLI error envelope passes through intact.

.DESCRIPTION
    This is a thin JSON-RPC-over-stdio client, not a general MCP client library — just enough
    to prove the server speaks MCP correctly end-to-end. Prints the raw JSON-RPC responses it
    receives so the real behavior can be inspected, rather than asserting silently.

.PARAMETER ServerExePath
    Path to agentdebug-mcp.exe. Defaults to the local Debug build output next to this script's
    repo checkout.

.PARAMETER VsBinPath
    Folder containing agentdebug-vs.exe. Defaults to the Debug build output of
    AgentDebugToolkit.Debugger.VisualStudio in this repo. Passed to the server via
    AGENTDEBUG_VS_BIN.

.PARAMETER UiBinPath
    Folder containing agentdebug-ui.exe. Defaults to the Debug build output of
    AgentDebugToolkit.UiAutomation.Cli in this repo. Passed to the server via AGENTDEBUG_UI_BIN.

.PARAMETER Solution
    Optional --solution value forwarded to vs_debugger_status/vs_evaluate, to disambiguate which
    running devenv.exe instance to query if more than one is open.
#>
param(
    [string]$ServerExePath = (Join-Path $PSScriptRoot '..\src\AgentDebugToolkit.Mcp\bin\Debug\net8.0-windows\agentdebug-mcp.exe'),
    [string]$VsBinPath = (Join-Path $PSScriptRoot '..\src\AgentDebugToolkit.Debugger.VisualStudio\bin\Debug\net8.0-windows'),
    [string]$UiBinPath = (Join-Path $PSScriptRoot '..\src\AgentDebugToolkit.UiAutomation.Cli\bin\Debug\net8.0-windows'),
    [string]$Solution = $null
)

$ErrorActionPreference = 'Stop'

$ServerExePath = (Resolve-Path $ServerExePath).Path
$VsBinPath = (Resolve-Path $VsBinPath).Path
$UiBinPath = (Resolve-Path $UiBinPath).Path

Write-Host "Server exe:     $ServerExePath"
Write-Host "VS bin:         $VsBinPath"
Write-Host "UI bin:         $UiBinPath"
Write-Host ""

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $ServerExePath
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.CreateNoWindow = $true
$psi.EnvironmentVariables['AGENTDEBUG_VS_BIN'] = $VsBinPath
$psi.EnvironmentVariables['AGENTDEBUG_UI_BIN'] = $UiBinPath
$psi.EnvironmentVariables.Remove('DOTNET_ROOT') | Out-Null
$psi.EnvironmentVariables.Remove('DOTNET_ROOT_X64') | Out-Null

$proc = New-Object System.Diagnostics.Process
$proc.StartInfo = $psi

# Drain stderr on a background job so server log output doesn't block the pipe and is visible
# for diagnostics without being confused with the stdout JSON-RPC stream.
$stderrLines = New-Object System.Collections.Generic.List[string]
$stderrHandler = {
    param($sender, $e)
    if ($null -ne $e.Data) {
        $script:stderrLines.Add($e.Data)
    }
}
Register-ObjectEvent -InputObject $proc -EventName ErrorDataReceived -Action $stderrHandler | Out-Null

if (-not $proc.Start()) {
    throw "Failed to start $ServerExePath"
}
$proc.BeginErrorReadLine()

$requestId = 0
function Send-Request([string]$Method, $Params) {
    $script:requestId++
    $msg = [ordered]@{
        jsonrpc = '2.0'
        id      = $script:requestId
        method  = $Method
        params  = $Params
    }
    $json = $msg | ConvertTo-Json -Depth 20 -Compress
    Write-Host ">> $json"
    $proc.StandardInput.WriteLine($json)
    $proc.StandardInput.Flush()
    return $script:requestId
}

function Send-Notification([string]$Method, $Params) {
    $msg = [ordered]@{
        jsonrpc = '2.0'
        method  = $Method
        params  = $Params
    }
    $json = $msg | ConvertTo-Json -Depth 20 -Compress
    Write-Host ">> $json"
    $proc.StandardInput.WriteLine($json)
    $proc.StandardInput.Flush()
}

function Read-Response([int]$TimeoutMs = 15000) {
    $task = $proc.StandardOutput.ReadLineAsync()
    if (-not $task.Wait($TimeoutMs)) {
        throw "Timed out waiting for a response from the server after ${TimeoutMs}ms."
    }
    $line = $task.Result
    Write-Host "<< $line"
    return $line | ConvertFrom-Json
}

try {
    Write-Host "=== 1. initialize handshake ==="
    Send-Request -Method 'initialize' -Params @{
        protocolVersion = '2025-06-18'
        capabilities    = @{}
        clientInfo      = @{ name = 'AgentDebugToolkit-Test-McpServer'; version = '1.0.0' }
    } | Out-Null
    $initResponse = Read-Response
    if ($null -eq $initResponse.result) {
        throw "initialize did not return a result. Full response: $($initResponse | ConvertTo-Json -Depth 10)"
    }
    Write-Host "Server info: $($initResponse.result.serverInfo | ConvertTo-Json -Compress)"
    Write-Host ""

    Send-Notification -Method 'notifications/initialized' -Params @{}
    Write-Host ""

    Write-Host "=== 2. tools/list ==="
    Send-Request -Method 'tools/list' -Params @{} | Out-Null
    $toolsResponse = Read-Response
    if ($null -eq $toolsResponse.result -or $null -eq $toolsResponse.result.tools) {
        throw "tools/list did not return a tools array. Full response: $($toolsResponse | ConvertTo-Json -Depth 10)"
    }
    $toolNames = $toolsResponse.result.tools | ForEach-Object { $_.name }
    Write-Host "Tool count: $($toolNames.Count)"
    Write-Host ($toolNames -join ', ')
    Write-Host ""

    if ($toolNames -notcontains 'vs_debugger_status') {
        throw "Expected tool 'vs_debugger_status' was not found in tools/list output."
    }
    if ($toolNames -notcontains 'ui_list_windows') {
        throw "Expected tool 'ui_list_windows' was not found in tools/list output."
    }
    if ($toolNames -notcontains 'vs_evaluate') {
        throw "Expected tool 'vs_evaluate' was not found in tools/list output."
    }

    function Invoke-McpTool([string]$ToolName, [hashtable]$Arguments, [int]$TimeoutMs = 30000) {
        Send-Request -Method 'tools/call' -Params @{ name = $ToolName; arguments = $Arguments } | Out-Null
        $response = Read-Response -TimeoutMs $TimeoutMs
        # The MCP SDK wraps a plain-object tool return as a single text ContentBlock; parse its
        # JSON text back out so we can inspect ToolInvocationResult's fields (Result/WrapperErrorCode/etc.)
        $text = $response.result.content[0].text
        $parsed = $text | ConvertFrom-Json
        return [PSCustomObject]@{
            Envelope = $response
            Parsed   = $parsed
        }
    }

    Write-Host "=== 3. tools/call vs_debugger_status (live, read-only) ==="
    $vsArgs = @{}
    if ($Solution) { $vsArgs = @{ solution = $Solution } }
    $debuggerStatus = Invoke-McpTool -ToolName 'vs_debugger_status' -Arguments $vsArgs
    Write-Host ""
    Write-Host "=== Full vs_debugger_status response ==="
    $debuggerStatus.Envelope | ConvertTo-Json -Depth 20
    Write-Host ""

    Write-Host "=== 4. tools/call ui_list_windows (live, read-only) ==="
    $uiListWindows = Invoke-McpTool -ToolName 'ui_list_windows' -Arguments @{}
    Write-Host ""
    Write-Host "=== Full ui_list_windows response ==="
    $uiListWindows.Envelope | ConvertTo-Json -Depth 20
    Write-Host ""

    Write-Host "=== 5. tools/call vs_evaluate (live; expected to surface a CLI error envelope if not in break mode) ==="
    $evalArgs = @{ expression = '1 + 1' }
    if ($Solution) { $evalArgs.solution = $Solution }
    $evaluate = Invoke-McpTool -ToolName 'vs_evaluate' -Arguments $evalArgs
    Write-Host ""
    Write-Host "=== Full vs_evaluate response ==="
    $evaluate.Envelope | ConvertTo-Json -Depth 20
    Write-Host ""

    Write-Host "=== 6. Checking vs_evaluate's CLI error envelope passthrough ==="
    $evalResult = $evaluate.Parsed.result
    $evalWrapperErrorCode = $evaluate.Parsed.wrapperErrorCode
    if ($null -ne $evalResult -and $evalResult.success -eq $false) {
        Write-Host "CLI reported its own error envelope under 'result': errorCode='$($evalResult.error)', message='$($evalResult.message)'"
        if ($null -ne $evalWrapperErrorCode) {
            throw "Expected wrapperErrorCode to be null/absent when the CLI returns its own error envelope, but got '$evalWrapperErrorCode'."
        }
        Write-Host "Confirmed: wrapperErrorCode is null/absent -- the CLI's own error code came through under 'result' intact, with no wrapper error set."
    }
    elseif ($null -ne $evalResult -and $evalResult.success -eq $true) {
        Write-Host "Debugger was already in break mode with a selected frame -- vs_evaluate succeeded normally: $($evalResult | ConvertTo-Json -Compress)"
        Write-Host "(Could not exercise the CLI-error-envelope-passthrough check this run since there was no error to observe; re-run while NOT in break mode to see it.)"
    }
    else {
        throw "Unexpected vs_evaluate shape; neither a success nor a CLI error envelope was found under 'result'. Full parsed tool result: $($evaluate.Parsed | ConvertTo-Json -Depth 10)"
    }
}
finally {
    Write-Host ""
    Write-Host "=== Server stderr (diagnostics) ==="
    Start-Sleep -Milliseconds 200
    $stderrLines | ForEach-Object { Write-Host "  $_" }

    if (-not $proc.HasExited) {
        try { $proc.StandardInput.Close() } catch {}
        Start-Sleep -Milliseconds 300
        if (-not $proc.HasExited) {
            $proc.Kill($true)
        }
    }
    $proc.Dispose()
}
