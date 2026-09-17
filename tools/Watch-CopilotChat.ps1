<#
.SYNOPSIS
    Polls a VS Copilot Chat window (via agentdebug-ui.exe) until it is blocked waiting for
    user input, goes idle with a new final message, or a timeout is reached.

.DESCRIPTION
    Wraps repeated `inspect --hwnd <h>` calls, parses the resulting UIA tree, and reports a
    single-line status so a calling agent/session doesn't need to re-implement the polling
    loop and tree-walk logic every time.

    Detection rules:
      - PENDING: a "Waiting..." text node exists (a confirmation card with Submit/Cancel and
        optionally radio-button options is on screen).
      - WORKING: a "Working on it..." node exists and no "Waiting..." node exists.
      - IDLE: neither node exists. Reports the last ChatMessageItem message text (truncated).

.PARAMETER Hwnd
    Target window handle, e.g. "0xCA18B2".

.PARAMETER ExePath
    Path to agentdebug-ui.exe. Defaults to the well-known build output path.

.PARAMETER InspectJsonPath
    Scratch file path to write each `inspect` call's JSON output to.

.PARAMETER TimeoutSeconds
    Max time to poll before giving up and reporting STATE=TIMEOUT. Default 480 (8 min).

.PARAMETER PollIntervalSeconds
    Initial delay between polls, in seconds. Default 12. The script uses adaptive backoff (see
    -PollBackoffMultiplier / -MaxPollIntervalSeconds): each invocation starts at this short
    interval (since the caller typically just sent a response and the agent may finish or hit
    its next prompt quickly) and gradually increases the wait between polls the longer it stays
    in the same non-terminal state, up to -MaxPollIntervalSeconds. Each new invocation of this
    script (e.g. after you've answered a prompt) naturally resets back to this short interval.

.PARAMETER MaxPollIntervalSeconds
    Upper bound the adaptive poll interval is allowed to grow to. Default 60, or the initial
    -PollIntervalSeconds when that is larger and this parameter is not explicitly supplied.

.PARAMETER PollBackoffMultiplier
    Growth factor applied to the poll interval after each non-terminal poll. Default 1.4.

.PARAMETER MaxDepth
    --maxDepth passed to `inspect`. Default 20.

.OUTPUTS
    Writes a PSCustomObject to the pipeline (and as JSON to stdout if -AsJson) with:
      State            : "Pending" | "Working" | "Idle" | "Timeout" | "Error"
      Question         : radio-field label text, if State=Pending
      Options          : array of radio button option labels, if State=Pending
      Recommended      : options whose label contains "Recommended", if State=Pending
      LastMessage      : last ChatMessageItem text, if State=Idle
      MessageCount     : total ChatMessageItem count seen
      ElapsedSeconds   : how long the loop ran before returning

.EXAMPLE
    .\Watch-CopilotChat.ps1 -Hwnd 0xCA18B2 -TimeoutSeconds 300

.EXAMPLE
    .\Watch-CopilotChat.ps1 -Hwnd 0xCA18B2 -AsJson | ConvertFrom-Json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Hwnd,

    [string]$ExePath = 'C:\MyFiles\Git\AgentDebugToolkit\src\AgentDebugToolkit.UiAutomation.Cli\bin\Debug\net8.0-windows\agentdebug-ui.exe',

    [string]$InspectJsonPath = (Join-Path $env:TEMP 'watch-copilot-chat-inspect.json'),

    [int]$TimeoutSeconds = 480,

    [int]$PollIntervalSeconds = 12,

    [int]$MaxPollIntervalSeconds = 60,

    [double]$PollBackoffMultiplier = 1.4,

    [int]$MaxDepth = 20,

    [switch]$AsJson,

    [switch]$StopOnFirstIdleMessage
)

# The inherited DOTNET_ROOT from VS breaks agentdebug-ui.exe; always clear it.
Remove-Item Env:DOTNET_ROOT -ErrorAction SilentlyContinue

if ($PollIntervalSeconds -le 0) {
    throw "-PollIntervalSeconds must be a positive integer."
}

if ($MaxPollIntervalSeconds -le 0) {
    throw "-MaxPollIntervalSeconds must be a positive integer."
}

if ($MaxPollIntervalSeconds -lt $PollIntervalSeconds) {
    if ($PSBoundParameters.ContainsKey('MaxPollIntervalSeconds')) {
        throw "-MaxPollIntervalSeconds must be greater than or equal to -PollIntervalSeconds."
    }

    $MaxPollIntervalSeconds = $PollIntervalSeconds
}

if ([double]::IsNaN($PollBackoffMultiplier) -or [double]::IsInfinity($PollBackoffMultiplier) -or $PollBackoffMultiplier -lt 1) {
    throw "-PollBackoffMultiplier must be a finite number greater than or equal to 1."
}

if (-not (Test-Path $ExePath)) {
    throw "agentdebug-ui.exe not found at '$ExePath'. Pass -ExePath explicitly."
}

function Get-AllNodes {
    param($Root)
    $all = [System.Collections.Generic.List[object]]::new()
    $stack = [System.Collections.Generic.Stack[object]]::new()
    $stack.Push($Root)
    while ($stack.Count -gt 0) {
        $n = $stack.Pop()
        $all.Add($n)
        if ($n.Children) {
            foreach ($c in $n.Children) { $stack.Push($c) }
        }
    }
    return $all
}

function Get-SleepMilliseconds {
    param(
        [int]$RequestedMilliseconds,
        [datetime]$Deadline
    )

    $remainingMilliseconds = [Math]::Max(0, [int](($Deadline - (Get-Date)).TotalMilliseconds))
    return [Math]::Min($RequestedMilliseconds, $remainingMilliseconds)
}

$start = Get-Date
$deadline = $start.AddSeconds($TimeoutSeconds)
$lastMessageCount = -1
$lastMessageText = $null
$stableIdlePolls = 0
$pollNum = 0
# Adaptive backoff: start short (a response was likely just sent, so the agent may finish or
# hit its next prompt quickly) and grow towards MaxPollIntervalSeconds the longer we stay in a
# non-terminal state. Resets to PollIntervalSeconds on each fresh invocation of this script.
$currentInterval = $PollIntervalSeconds

while ((Get-Date) -lt $deadline) {
    $pollNum++
    $elapsedNow = [int]((Get-Date) - $start).TotalSeconds
    Write-Host "[poll #$pollNum @ ${elapsedNow}s] inspecting hwnd $Hwnd ..." -ForegroundColor Cyan

    & $ExePath inspect --hwnd $Hwnd --maxDepth $MaxDepth > $InspectJsonPath 2>&1

    $raw = Get-Content -Raw -Encoding UTF8 -Path $InspectJsonPath -ErrorAction SilentlyContinue
    $data = $null
    if ($raw) {
        try { $data = $raw | ConvertFrom-Json } catch { $data = $null }
    }

    if (-not $data -or -not $data.root) {
        $sleepMilliseconds = Get-SleepMilliseconds -RequestedMilliseconds ($currentInterval * 1000) -Deadline $deadline
        Write-Host "[poll #$pollNum] inspect returned no parseable data; sleeping $([Math]::Round($sleepMilliseconds / 1000, 3))s..." -ForegroundColor Yellow
        if ($sleepMilliseconds -gt 0) {
            Start-Sleep -Milliseconds $sleepMilliseconds
        }
        $currentInterval = [Math]::Min($MaxPollIntervalSeconds, [Math]::Ceiling($currentInterval * $PollBackoffMultiplier))
        continue
    }

    $all = Get-AllNodes -Root $data.root

    $isWaiting = [bool]($all | Where-Object { $_.Name -eq 'Waiting...' })
    $isWorking = [bool]($all | Where-Object { $_.Name -eq 'Working on it...' })
    $messages = @($all | Where-Object { $_.ClassName -eq 'ChatMessageItem' -and $_.Name })

    $statusLabel = if ($isWaiting) { 'PENDING (waiting for input)' } elseif ($isWorking) { 'WORKING' } else { 'IDLE' }
    Write-Host "[poll #$pollNum] state=$statusLabel messages=$($messages.Count)" -ForegroundColor Gray

    if ($isWaiting) {
        $labels = @($all | Where-Object { $_.AutomationId -eq 'RadioFieldLabel' } | ForEach-Object { $_.Name })
        $options = @($all | Where-Object { $_.ControlType -eq 'ControlType.RadioButton' -and $_.Name -and $_.Name -ne 'Other' } | ForEach-Object { $_.Name })
        $recommended = @($options | Where-Object { $_ -match 'Recommended' })
        $promptKind = 'ChoicePrompt'

        # Some confirmation cards are free-text only (a FormField Edit + bare Submit/Cancel,
        # no RadioFieldLabel/RadioButton at all). In that case the question text lives on the
        # FormField Edit element's Name instead.
        if (-not $labels -and -not $options) {
            $formField = $all | Where-Object { $_.AutomationId -eq 'FormField' -and $_.Name } | Select-Object -First 1
            if ($formField) {
                $labels = @($formField.Name)
                $promptKind = 'FreeTextPrompt'
            }
        }

        $result = [PSCustomObject]@{
            State          = 'Pending'
            PromptKind     = $promptKind
            Question       = ($labels -join ' | ')
            Options        = $options
            Recommended    = $recommended
            LastMessage    = $null
            MessageCount   = $messages.Count
            ElapsedSeconds = [int]((Get-Date) - $start).TotalSeconds
        }
        if ($AsJson) { $result | ConvertTo-Json -Depth 5 } else { $result }
        return
    }

    if (-not $isWorking) {
        $currentLastText = if ($messages.Count -gt 0) { $messages[-1].Name } else { $null }
        if ($messages.Count -eq $lastMessageCount -and $currentLastText -eq $lastMessageText) {
            $stableIdlePolls++
        } else {
            $stableIdlePolls = 1
        }
        $lastMessageCount = $messages.Count
        $lastMessageText = $currentLastText

        Write-Host "[poll #$pollNum] idle-stability=$stableIdlePolls (need 2 consecutive stable polls to confirm Idle)" -ForegroundColor DarkGray

        if ($stableIdlePolls -ge 2 -and $messages.Count -gt 0 -and $StopOnFirstIdleMessage) {
            $lastMsg = $lastMessageText
            $result = [PSCustomObject]@{
                State          = 'Idle'
                Question       = $null
                Options        = @()
                Recommended    = @()
                LastMessage    = $lastMsg.Substring(0, [Math]::Min(1000, $lastMsg.Length))
                MessageCount   = $messages.Count
                ElapsedSeconds = [int]((Get-Date) - $start).TotalSeconds
            }
            if ($AsJson) { $result | ConvertTo-Json -Depth 5 } else { $result }
            return
        }
    } else {
        $stableIdlePolls = 0
    }

    $sleepMilliseconds = Get-SleepMilliseconds -RequestedMilliseconds ($currentInterval * 1000) -Deadline $deadline
    Write-Host "[poll #$pollNum] no new terminal state; sleeping $([Math]::Round($sleepMilliseconds / 1000, 3))s (next: $([Math]::Min($MaxPollIntervalSeconds, [Math]::Ceiling($currentInterval * $PollBackoffMultiplier)))s)..." -ForegroundColor DarkGray
    if ($sleepMilliseconds -gt 0) {
        Start-Sleep -Milliseconds $sleepMilliseconds
    }
    $currentInterval = [Math]::Min($MaxPollIntervalSeconds, [Math]::Ceiling($currentInterval * $PollBackoffMultiplier))
}

$result = [PSCustomObject]@{
    State          = 'Timeout'
    Question       = $null
    Options        = @()
    Recommended    = @()
    LastMessage    = $null
    MessageCount   = $lastMessageCount
    ElapsedSeconds = [int]((Get-Date) - $start).TotalSeconds
}
if ($AsJson) { $result | ConvertTo-Json -Depth 5 } else { $result }
