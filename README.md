# AgentDebugToolkit

AgentDebugToolkit is a Windows/.NET 8 command-line toolkit for agents that need to inspect and
control desktop applications, observe Visual Studio debugging state, or drive console applications
through ConPTY. Each command writes one JSON result to stdout and uses a non-zero exit code for
failures.

## Components

| Executable | Purpose |
| --- | --- |
| `agentdebug-ui` | UI Automation process/window/element inspection and input |
| `agentdebug-vs` | Read and control a running Visual Studio debugger through EnvDTE |
| `agentdebug-console` | Launch and control a console target through a detached ConPTY broker |

The command-line tools are intentionally separate. They do not own navigation maps or screen
graphs; an agent supplies selectors and owns workflow reasoning.

## Build

```powershell
Remove-Item Env:DOTNET_ROOT -ErrorAction SilentlyContinue
& 'C:\Program Files\dotnet\dotnet.exe' build .\AgentDebugToolkit.slnx --nologo
```

Clearing `DOTNET_ROOT` avoids a Visual Studio-private runtime override that can prevent .NET 8
tools from starting in some shell environments.

## Console automation quick start

```powershell
.\src\AgentDebugToolkit.ConsoleAutomation.Cli\bin\Debug\net8.0-windows\agentdebug-console.exe `
  launch --exe C:\Windows\System32\cmd.exe --args '/d /k'

.\src\AgentDebugToolkit.ConsoleAutomation.Cli\bin\Debug\net8.0-windows\agentdebug-console.exe `
  send-text --text "echo HELLO`r"

.\src\AgentDebugToolkit.ConsoleAutomation.Cli\bin\Debug\net8.0-windows\agentdebug-console.exe `
  read-screen

.\src\AgentDebugToolkit.ConsoleAutomation.Cli\bin\Debug\net8.0-windows\agentdebug-console.exe `
  stop
```

`agentdebug-console` persists a local session for short-lived client invocations. Its detached
broker owns the ConPTY target, terminal buffer, and named-pipe transport. `read-screen` returns
the terminal buffer, not a native console-window capture.

## Implemented command surfaces

- **UI Automation (`agentdebug-ui`):** `attach`, `list-windows`, `inspect`, `click`, `right-click`,
  `double-click`, `drag`, `move-mouse`, `get-cursor-pos`, `type`, `get-text`, `wait-for-element`,
  `wait-for-window-change`, `wait-for-process-responding`, `delay`, `set-context`,
  `read-visible-text`, `screenshot`, `activate`, `send-keys`, `submit-chat-message`, `find-first`,
  and `find-all`.
- **Visual Studio debugger (`agentdebug-vs`):** `debugger-status`, `get-callstack`, `get-locals`,
  `get-exception-info`, `continue`, `step-over`/`step-into`/`step-out`, `start-debugging`,
  `stop-debugging`, `set-breakpoint`, `list-breakpoints`, `remove-breakpoint`, and
  `wait-for-break`.
- **Console automation (`agentdebug-console`):** `launch`, `read-screen`, `send-text`,
  `send-keys`, `wait-for-text`, `is-running`, and `stop`.

See [docs/CLI_CONTRACT.md](docs/CLI_CONTRACT.md) for exact options, JSON payloads, and error
codes. See [docs/IMPLEMENTATION_PLAN.md](docs/IMPLEMENTATION_PLAN.md) for current phase status
and validation boundaries. See [docs/KNOWN_OPEN_FINDINGS.md](docs/KNOWN_OPEN_FINDINGS.md) for a
user-curated log of findings surfaced during development (all currently resolved).

## Using the toolkit from an agent

Treat each invocation as a single request/response operation:

1. Run one executable command and parse its sole JSON object from stdout.
2. Continue only when `"success": true`; on failure, use the machine-readable `"error"` code and
   `"message"` to choose the next action.
3. Use condition-based waits (`wait-for-element` or `wait-for-text`) after actions that change
   state. Do not rely on fixed sleeps except as a deliberate last resort.

For UI Automation, begin with `agentdebug-ui attach --process <name>`, then use
`list-windows` and `inspect` to obtain live window handles and element data. Prefer a supported
automation pattern through `click` or `type`; those verbs use synthetic input only as their
fallback. Persisted context is a convenience, not a navigation model: the calling agent must
select the intended window and provide selectors for each action.

`list-windows` and `attach` include visible owned native dialogs, such as a WPF
`MessageBox.Show(...)` window (`#32770`). Pass the returned dialog HWND to `inspect`, `screenshot`,
`get-text`, or `click` to inspect it or invoke a named button, for example
`click --hwnd <dialog-hwnd> --strategy Name --value Yes`.

For console automation, call `launch` once, then use `read-screen`, `send-text`/`send-keys`, and
`wait-for-text` through the persisted broker session. Call `is-running` to obtain target status
and its exit code, then call `stop` when the session is finished. Treat
`session-not-found` or `broker-unreachable` as a lost console session and launch a new one if
appropriate.

For Visual Studio debugging, use `agentdebug-vs debugger-status` before break-mode operations.
Only call stack, locals, exception, continue, or stepping verbs when the returned mode is
`"break"`; use `start-debugging` and `stop-debugging` deliberately because they change the IDE's
debug session.

Keep application navigation maps, expected screen transitions, and workflow decisions outside
these executables. The toolkit executes live UI, debugger, and terminal operations; the agent
owns the plan.

## Current validation boundary

ConPTY behavior and public console commands have been smoke-tested with local `cmd.exe` targets,
including interactive input, immediate output, timeout handling, target exit code 259, malformed
pipe-client isolation, and clean shutdown. Validation against the planned
`CAMFWDownloadConsole.exe` target remains intentionally open.

## Requirements and scope

- Windows with ConPTY support for console automation.
- .NET 8 SDK.
- A running Visual Studio instance for `agentdebug-vs`.

The toolkit is designed for local, authorized automation. It does not replace application-specific
workflow validation or agent-owned navigation maps.
