# Architecture

## Components

```
AgentDebugToolkit/
  src/
	AgentDebugToolkit.Core/                 Shared models, no UIA/EnvDTE dependency
	AgentDebugToolkit.UiAutomation.Cli/     Thin UIA executor (Phases 1-4)
	AgentDebugToolkit.Debugger.VisualStudio/  EnvDTE-based debugger bridge (Phase 6, independent)
	AgentDebugToolkit.ConsoleAutomation.Cli/ Detached ConPTY broker client (Phase 7, independent)
  docs/
	ARCHITECTURE.md                (this file)
	IMPLEMENTATION_PLAN.md          Phased delivery plan
	CLI_CONTRACT.md                 Exact verb/JSON specs for UiAutomation.Cli
	NAVIGATION_MAP_SCHEMA.md        Agent-owned screen-graph file format
	VALIDATION_FINDINGS.md          Ground-truth findings from real-app probing
```

## Core design decisions (see VALIDATION_FINDINGS.md and prior discussion for rationale)

1. **CLI is a thin, stateless-per-call executor.** It knows nothing about "screens," navigation
   graphs, or map files. It only understands: processes, windows, elements (via selector),
   actions, and waits. All map-graph reasoning happens in the calling agent (Claude), which reads
   and writes the navigation map files directly using its own file tools.

2. **Selector resolution is pattern-first, coordinate-fallback.** Given a resolved
   `AutomationElement`, the adapter tries `InvokePattern`/`TogglePattern` (for click) or
   `ValuePattern` (for type) first; if unsupported (the common case in this app, per
   `VALIDATION_FINDINGS.md`), it falls back to a synthetic mouse click at the element's
   `BoundingRectangle` center, or synthetic keyboard input after click-to-focus.

3. **`Name` is the primary selector strategy for this app family**, not `AutomationId`
   (contradicts typical UIA tooling defaults — see `VALIDATION_FINDINGS.md`). The selector model
   still supports `AutomationId` for other target applications where it may be reliable (e.g.,
   genuine WPF apps with explicit automation properties).

4. **hwnd is never a caller-supplied identity.** Callers target windows by `--pid`
   (session-context default) plus optional scoping; hwnd only appears in CLI *output* for
   diagnostic purposes. This avoids brittleness from hwnd values changing across window
   recreation.

5. **All coordinates are client-area-relative**, recomputed live from `BoundingRectangle` at the
   moment of action — never cached/absolute, since window position/size varies across sessions.

6. **Waits are condition-based, not fixed sleeps.** `wait-for-window-change` (settle+diff),
   `wait-for-element`, and `wait-for-process-responding` are the primary primitives; `delay` is an
   explicit, rarely-used escape hatch.

7. **IDE-specific components (Debugger.VisualStudio) are fully independent** of the UI automation
   CLI — separate project, separate process, separate JSON contract, only sharing
   `AgentDebugToolkit.Core` where genuinely common (e.g., a shared `JsonResult<T>` envelope type).

8. **Console automation uses a detached broker.** `agentdebug-console` public invocations are
   short-lived named-pipe clients. The broker owns the nonserializable ConPTY handle, target
   process, synchronous host pipes, output reader, and terminal buffer for the session lifetime.
   The component remains independent of Core, UI Automation, and EnvDTE.

## Process/session model

- `attach --process <name>` resolves a running process by name (must match exactly one process;
  the CLI reports an error listing candidates if ambiguous) and persists `{pid, processName}` to a
  local session-context file (e.g., `%TEMP%\agentdebugtoolkit\ui-session.json`).
- Every other command reads this context by default; `--pid` is available as an explicit
  per-call override without disturbing the stored default.
- If the stored pid is no longer a running process, commands fail fast with a distinct
  `"error": "stale-context"` rather than silently misbehaving.

## Output contract

Every CLI invocation prints exactly one JSON object to stdout and sets exit code 0 on success,
non-zero on failure. Failures always include an `"error"` field with a short machine-readable code
(e.g., `"element-not-found"`, `"stale-context"`, `"ambiguous-process"`, `"timeout"`) plus a
human-readable `"message"`. See `CLI_CONTRACT.md` for exact shapes per verb.

## Console broker session model

- `launch` starts the target under ConPTY and persists `{sessionId, brokerPid, brokerStartedAtUtc,
  pipeName, targetPid}` in `%TEMP%\agentdebugtoolkit\console-session.json`.
- Subsequent console verbs validate the broker PID/start time before using the named pipe; stale
  context is conditionally cleared without deleting a newer session.
- The broker drains VT output into `TerminalBuffer` continuously. After target exit, it closes the
  pseudoconsole and waits for output-pipe EOF before exposing final screen/status data.
- Each pipe connection carries exactly one newline-terminated request. Malformed, disconnected,
  and timed-out clients are isolated; target input is serialized through a bounded queue so a
  blocked target does not consume all request handlers.
