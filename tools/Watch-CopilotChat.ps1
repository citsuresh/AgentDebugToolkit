<#
.SYNOPSIS
    Polls a VS Copilot Chat window (via agentdebug-ui.exe) until it is blocked waiting for
    user input, goes idle with a new final message, or a timeout is reached.

.DESCRIPTION
    Wraps repeated `inspect --hwnd <h>` calls, parses the resulting UIA tree, and reports a
    single-line status so a calling agent/session doesn't need to re-implement the polling
    loop and tree-walk logic every time.

    Detection rules:
      - PENDING: a -WaitingMarkers node (default "Waiting...") exists, a question card is
        open (a RadioFieldLabel or FormField element plus a Submit button), or a tool
        permission prompt is open ("Do you allow Copilot to ...?" with Allow/Deny buttons;
        detected by the "More confirmation options" button, AutomationId ExtraActionsButton, or
        by sibling "Allow..." and "Deny" buttons). Newer Copilot Chat experiences show no
        "Waiting..." node and drop "Working" while a permission prompt is open, so without
        these checks a waiting agent looks Idle.
      - WORKING: a -WorkingMarkers node (default "Working on it..." or "Working") exists and
        no waiting node exists. The shorter label is used by newer Copilot Chat experiences.
      - IDLE: neither node exists. Idle is confirmed immediately when the last
        ChatMessageItem is a new completion footer (newer experiences add "The response used
        the <model> model..." when a reply finishes), after 2 consecutive stable polls when
        the last message is a footer, or after -IdleWithoutFooterPolls stable polls when it
        is not. An Idle result without a footer carries a Note: check the transcript before
        trusting it. Reports the last non-footer ChatMessageItem text as LastMessage.

    ChatMessageItem elements whose Name is only a view-model type name (for example
    "Microsoft.VisualStudio.Copilot.UI...HarnessMessageViewModel", see -PlaceholderPattern)
    carry no text and are ignored.

    The chat list is virtualized, so only messages currently rendered are visible to UIA;
    MessageCount is a count of rendered messages, not of the whole conversation.

.PARAMETER Hwnd
    Target window handle, e.g. "0xCA18B2".

.PARAMETER ExePath
    Path to agentdebug-ui.exe. Defaults to the well-known build output path.

.PARAMETER InspectJsonPath
    Scratch file path to write each `inspect` call's JSON output to. The default includes the
    process ID so concurrent watchers do not collide on the same file.

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

.PARAMETER WorkingMarkers
    Exact UIA Name values that mean the agent is working. Default "Working on it...", "Working".
    Override when a Visual Studio update changes the status text.

.PARAMETER WaitingMarkers
    Exact UIA Name values that mean the agent is waiting for input. Default "Waiting...".

.PARAMETER CompletionPattern
    Regex matching the completion footer ChatMessageItem. Default
    '^The response (used the .+ model|cost [\d.]+ credits?\.?$)'. Some responses show only the
    short footer "The response cost 0.1 credits." with no model name.

.PARAMETER PlaceholderPattern
    Regex for ChatMessageItem Names that are view-model placeholders, not text. Default
    '^Microsoft\.VisualStudio\.'.

.PARAMETER IdleWithoutFooterPolls
    Stable Idle polls required when the last message is not a completion footer. Default 3.

.PARAMETER RequireWorkingFirst
    Do not report Idle until Working/Pending has been seen at least once, a completion footer
    different from the one present at the first poll appears, or -StartGraceSeconds pass.
    Use this when starting the watcher right after sending a prompt or answering a card, so
    the chat is not reported Idle before the agent has started. The grace period covers
    replies that finish before the first poll. WorkingSeen in the result says which case hit.

.PARAMETER StartGraceSeconds
    With -RequireWorkingFirst, how long to wait for Working before accepting a stable Idle
    anyway. Default 30.

.PARAMETER MaxMessageLength
    Maximum characters of LastMessage to return. Default 4000.

.PARAMETER StopOnFirstIdleMessage
    Retained for compatibility. Confirmed Idle now always ends the watch.

.OUTPUTS
    Writes a PSCustomObject to the pipeline (and as JSON to stdout if -AsJson) with:
      State            : "Pending" | "Working" | "Idle" | "Timeout" | "Error"
      PromptKind       : "ChoicePrompt" | "FreeTextPrompt" | "PermissionPrompt", if State=Pending
      Question         : radio-field label or permission question text, if State=Pending
      Options          : radio option labels, or permission button names, if State=Pending
      Recommended      : options whose label contains "Recommended", if State=Pending
      MorePages        : true if a multi-question card has more pages ("Next question" enabled),
                         if State=Pending; answer each page with Select-ChoicePromptOption.ps1
      LastMessage      : last non-footer ChatMessageItem text, if State=Idle
      Completed        : true if the last ChatMessageItem is a completion footer, if State=Idle
      CompletionFooter : latest completion footer text (model and cost), if State=Idle
      WorkingSeen      : true if Working or Pending was seen during this watch, if State=Idle
      Note             : set when Idle was confirmed without a completion footer
      Banners          : chat-pane banners (any state), each with Id, Text and Buttons, e.g.
                         HarnessWorkspaceMismatchBanner (session working directory differs
                         from the open folder) or WorkspaceTrustWarningBanner
      MessageCount     : rendered ChatMessageItem count seen
      ElapsedSeconds   : how long the loop ran before returning

.EXAMPLE
    .\Watch-CopilotChat.ps1 -Hwnd 0xCA18B2 -TimeoutSeconds 300

.EXAMPLE
    .\Watch-CopilotChat.ps1 -Hwnd 0xCA18B2 -AsJson | ConvertFrom-Json

.EXAMPLE
    .\Watch-CopilotChat.ps1 -Hwnd 0xCA18B2 -RequireWorkingFirst -PollIntervalSeconds 10
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Hwnd,

    [string]$ExePath = 'C:\MyFiles\Git\AgentDebugToolkit\src\AgentDebugToolkit.UiAutomation.Cli\bin\Debug\net8.0-windows\agentdebug-ui.exe',

    [string]$InspectJsonPath = (Join-Path $env:TEMP "watch-copilot-chat-inspect-$PID.json"),

    [int]$TimeoutSeconds = 480,

    [int]$PollIntervalSeconds = 12,

    [int]$MaxPollIntervalSeconds = 60,

    [double]$PollBackoffMultiplier = 1.4,

    [int]$MaxDepth = 20,

    [string[]]$WorkingMarkers = @('Working on it...', 'Working'),

    [string[]]$WaitingMarkers = @('Waiting...'),

    [string]$CompletionPattern = '^The response (used the .+ model|cost [\d.]+ credits?\.?$)',

    [string]$PlaceholderPattern = '^Microsoft\.VisualStudio\.',

    [ValidateRange(2, 20)]
    [int]$IdleWithoutFooterPolls = 3,

    [switch]$RequireWorkingFirst,

    [int]$StartGraceSeconds = 30,

    [int]$MaxMessageLength = 4000,

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

if ($MaxMessageLength -le 0) {
    throw "-MaxMessageLength must be a positive integer."
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
            # Push in reverse so nodes come out in document order; LastMessage depends on it.
            $children = @($n.Children)
            for ($i = $children.Count - 1; $i -ge 0; $i--) {
                if ($children[$i]) { $stack.Push($children[$i]) }
            }
        }
    }
    return $all
}

# Tool permission prompt ("Do you allow Copilot to ...?"): its buttons are siblings of the
# question's document viewer. Returns Question and button Names, or $null.
function Find-PermissionPrompt {
    param($Root)
    $navigation = @('More confirmation options', 'Previous confirmation', 'Next confirmation')
    $stack = [System.Collections.Generic.Stack[object]]::new()
    $stack.Push($Root)
    while ($stack.Count -gt 0) {
        $n = $stack.Pop()
        $children = @($n.Children | Where-Object { $_ })
        $buttons = @($children | Where-Object { $_.ControlType -eq 'ControlType.Button' })
        $isPrompt = [bool]($buttons | Where-Object { $_.AutomationId -eq 'ExtraActionsButton' }) -or
            ([bool]($buttons | Where-Object { $_.Name -eq 'Deny' }) -and [bool]($buttons | Where-Object { $_.Name -like 'Allow*' }))
        if ($isPrompt) {
            # The card's controls can be flat siblings of the whole chat pane (header buttons,
            # input box), so use only the run of adjacent buttons around the anchor button.
            $anchor = -1
            for ($i = 0; $i -lt $children.Count; $i++) {
                $c = $children[$i]
                if ($c.ControlType -eq 'ControlType.Button' -and ($c.AutomationId -eq 'ExtraActionsButton' -or $c.Name -eq 'Deny')) { $anchor = $i; break }
            }
            $first = $anchor
            while ($first -gt 0 -and $children[$first - 1].ControlType -eq 'ControlType.Button') { $first-- }
            $last = $anchor
            while ($last -lt $children.Count - 1 -and $children[$last + 1].ControlType -eq 'ControlType.Button') { $last++ }
            $options = @($children[$first..$last] | Where-Object { $_.IsEnabled -and $_.Name -and $_.Name -notin $navigation } | ForEach-Object { $_.Name })

            $question = $null
            for ($i = $first - 1; $i -ge 0 -and -not $question; $i--) {
                if ($children[$i].ControlType -eq 'ControlType.Button') { break }
                $question = @(Get-AllNodes -Root $children[$i] | Where-Object {
                        $_.ControlType -in @('ControlType.Document', 'ControlType.Text') -and $_.Name -and $_.Name.Length -gt 15
                    } | ForEach-Object { $_.Name }) | Select-Object -First 1
            }
            return [pscustomobject]@{ Question = $question; Options = $options }
        }
        for ($i = $children.Count - 1; $i -ge 0; $i--) { $stack.Push($children[$i]) }
    }
    return $null
}

# Informational banners above the chat input. They don't block the chat, but a new session
# started under a working-directory mismatch runs in the wrong folder.
function Find-ChatBanners {
    param($All)
    @($All | Where-Object { $_.AutomationId -match 'Banner$' -and $_.AutomationId -ne 'CloseBanner' } | ForEach-Object {
            $nodes = @(Get-AllNodes -Root $_)
            $buttonNames = @($nodes | Where-Object { $_.ControlType -eq 'ControlType.Button' -and $_.Name } | ForEach-Object { $_.Name })
            [pscustomobject]@{
                Id      = $_.AutomationId
                Text    = (@($nodes | Where-Object { $_.ControlType -eq 'ControlType.Text' -and $_.Name -and $_.Name -notin $buttonNames } | ForEach-Object { $_.Name } | Select-Object -Unique) -join ' ')
                Buttons = $buttonNames
            }
        })
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
$seenActivity = $false
$baselineCaptured = $false
$baselineFooter = $null
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

    # Newer experiences keep showing "Working" while a question card is open and have no
    # "Waiting..." node, so an open card (question label/field plus Submit) also means Pending.
    # Multi-question cards show an enabled "Next question" instead of Submit until the last page.
    $hasCardField = [bool]($all | Where-Object { $_.AutomationId -in @('RadioFieldLabel', 'FormField') })
    $hasSubmit = [bool]($all | Where-Object { $_.ControlType -eq 'ControlType.Button' -and $_.Name -eq 'Submit' })
    $hasNextQuestion = [bool]($all | Where-Object { $_.ControlType -eq 'ControlType.Button' -and $_.Name -eq 'Next question' -and $_.IsEnabled })
    $permission = Find-PermissionPrompt -Root $data.root
    $banners = Find-ChatBanners -All $all
    foreach ($b in $banners) { Write-Host "[poll #$pollNum] banner $($b.Id): $($b.Text)" -ForegroundColor Yellow }
    $isWaiting = [bool]($all | Where-Object { $_.Name -in $WaitingMarkers }) -or ($hasCardField -and ($hasSubmit -or $hasNextQuestion)) -or [bool]$permission
    $isWorking = [bool]($all | Where-Object { $_.Name -in $WorkingMarkers })
    $messages = @($all | Where-Object { $_.ClassName -eq 'ChatMessageItem' -and $_.Name -and $_.Name -notmatch $PlaceholderPattern })
    $footers = @($messages | Where-Object { $_.Name -match $CompletionPattern })
    $contentMessages = @($messages | Where-Object { $_.Name -notmatch $CompletionPattern })
    $currentFooter = if ($footers.Count -gt 0) { $footers[-1].Name } else { $null }
    $endsWithFooter = $messages.Count -gt 0 -and $messages[-1].Name -match $CompletionPattern

    if (-not $baselineCaptured) {
        $baselineFooter = $currentFooter
        $baselineCaptured = $true
    }
    if ($isWaiting -or $isWorking) { $seenActivity = $true }
    $footerChanged = [bool]$currentFooter -and $currentFooter -ne $baselineFooter

    $statusLabel = if ($isWaiting) { 'PENDING (waiting for input)' } elseif ($isWorking) { 'WORKING' } else { 'IDLE' }
    Write-Host "[poll #$pollNum] state=$statusLabel messages=$($messages.Count)" -ForegroundColor Gray

    if ($isWaiting) {
        $labels = @($all | Where-Object { $_.AutomationId -eq 'RadioFieldLabel' } | ForEach-Object { $_.Name })
        # Completion footers carry Like/Dislike radio buttons; they are not prompt options.
        $options = @($all | Where-Object { $_.ControlType -eq 'ControlType.RadioButton' -and $_.Name -and $_.Name -ne 'Other' -and $_.AutomationId -notin @('PART_LikeButton', 'PART_DislikeButton') } | ForEach-Object { $_.Name })
        $recommended = @($options | Where-Object { $_ -match 'Recommended' })
        $promptKind = 'ChoicePrompt'

        if ($permission -and -not $labels -and -not $options) {
            $labels = @($permission.Question)
            $options = $permission.Options
            $recommended = @()
            $promptKind = 'PermissionPrompt'
        }

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
            MorePages      = $hasNextQuestion
            Banners        = $banners
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

        $graceOver = ((Get-Date) - $start).TotalSeconds -ge $StartGraceSeconds
        $gateOpen = (-not $RequireWorkingFirst) -or $seenActivity -or $footerChanged -or $graceOver
        $fastConfirm = $endsWithFooter -and ($seenActivity -or $footerChanged)
        # Without a footer, "Idle" may be an unrecognized prompt or a status-text gap; wait longer.
        $neededPolls = if ($endsWithFooter) { 2 } else { $IdleWithoutFooterPolls }
        $confirmed = $messages.Count -gt 0 -and $gateOpen -and ($stableIdlePolls -ge $neededPolls -or $fastConfirm)

        if (-not $gateOpen) {
            Write-Host "[poll #$pollNum] idle, but Working has not been seen yet (-RequireWorkingFirst); waiting up to ${StartGraceSeconds}s" -ForegroundColor DarkGray
        } else {
            Write-Host "[poll #$pollNum] idle-stability=$stableIdlePolls completion-footer=$endsWithFooter (need $neededPolls stable polls, or a new completion footer, to confirm Idle)" -ForegroundColor DarkGray
        }

        if ($confirmed) {
            $lastMsg = if ($contentMessages.Count -gt 0) { $contentMessages[-1].Name } else { '' }
            $result = [PSCustomObject]@{
                State            = 'Idle'
                Question         = $null
                Options          = @()
                Recommended      = @()
                LastMessage      = $lastMsg.Substring(0, [Math]::Min($MaxMessageLength, $lastMsg.Length))
                Completed        = $endsWithFooter
                CompletionFooter = $currentFooter
                WorkingSeen      = $seenActivity
                Note             = $(if (-not $endsWithFooter) { 'No completion footer: read the transcript and inspect for a prompt before trusting Idle.' } else { $null })
                Banners          = $banners
                MessageCount     = $messages.Count
                ElapsedSeconds   = [int]((Get-Date) - $start).TotalSeconds
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
