# Script tests

Test cases for the helper scripts that are not covered by `dotnet test`:
`scripts\agent\Rebuild-Mcp.ps1` and `tools\Test-McpServer.ps1`. Read the cases for a script before
changing it and update them after. Results are recorded with the date; a case marked *Not run* has
not been run, whatever the script's design suggests.

Run all commands from the repo root in Windows PowerShell 5.1.

## Rebuild-Mcp.ps1

The script stops Claude Desktop, so only the cases that end before that point are safe to run from
inside a Claude Desktop session.

| ID | Case | How | Expected | Result |
|----|------|-----|----------|--------|
| R-01 | Preview | `.\scripts\agent\Rebuild-Mcp.ps1 -WhatIf` | Lists the Claude PIDs, prints a "What if" line naming the project, stops nothing, builds nothing. | 2026-11-01: pass (13 Claude PIDs listed, all still running afterwards). |
| R-02 | Invalid configuration | `... -Configuration Staging -WhatIf` | Parameter validation error naming the set `Debug,Release`. | 2026-11-01: pass. |
| R-03 | Timeout below range | `... -ExitTimeoutSeconds 0 -WhatIf` | Validation error: minimum is 1. | 2026-11-01: pass. |
| R-04 | RepoRoot does not exist | `... -RepoRoot <missing folder> -WhatIf` | `Cannot find path` error. | 2026-11-01: pass. |
| R-05 | RepoRoot without the MCP project | `... -RepoRoot <empty folder>` (no `-WhatIf`) | Throws `MCP project not found: ...` before any prompt; no process is stopped. | 2026-11-01: pass (Claude process count unchanged at 13). |
| R-06 | Real rebuild | `.\scripts\agent\Rebuild-Mcp.ps1`, confirm | Claude Desktop closes, `agentdebug-mcp` exits, build succeeds with `DOTNET_ROOT` cleared, output object shows an executable timestamp at or after `BuildStartedUtc`. `DOTNET_ROOT` is restored in the calling shell. | *Not run*: closes Claude Desktop, including the session driving the tests. Run it from a plain terminal. |
| R-07 | Processes outlive the timeout | R-06 with an MCP process that will not exit and `-ExitTimeoutSeconds 1` | Throws `Build not started; processes still running: ...`; no build. | *Not run*. |
| R-08 | Build failure | R-06 with a deliberate compile error | Throws `MCP rebuild failed (exit N)`; `DOTNET_ROOT` still restored. | *Not run*. |

## Test-McpServer.ps1

| ID | Case | How | Expected | Result |
|----|------|-----|----------|--------|
| T-01 | Server exe missing | `.\tools\Test-McpServer.ps1 -ServerExePath <missing file>` | `Cannot find path` error before any process starts. | 2026-11-01: pass. |
| T-02 | Live run | `.\tools\Test-McpServer.ps1` with Visual Studio open | Handshake returns `serverInfo`; `tools/list` contains `vs_debugger_status`, `ui_list_windows`, `vs_evaluate`; the `vs_evaluate` result outside break mode is a CLI error envelope under `result` with no `wrapperErrorCode`; server stderr is printed; the server process is gone afterwards. | *Not run*: it calls `vs_evaluate`, a state-changing tool, so it needs the user's approval first. |
| T-03 | Only one Visual Studio instance matters | T-02 with two instances and `-Solution <name>` | Both `vs_*` calls target the named solution. | *Not run*. |
| T-04 | Expected tool missing | T-02 against a server started with `--tools console` | Throws `Expected tool 'vs_debugger_status' was not found`. | *Not run*. |
