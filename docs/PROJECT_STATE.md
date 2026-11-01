# Project State

> This file is OVERWRITTEN (not appended) at the end of each working session.

## Current Focus

2026-11-01: Completed the real interactive-desktop test and filtered UI Automation suite in an
isolated copy after the in-place build was blocked by running MCP processes.

## Verification and Diagnostic Evidence

- GraphTools full build succeeded in 36.76 seconds: 807 nodes and 3,801 edges.
- The dependency manifest `commitSha` matches `git rev-parse HEAD`.
- `docs/graph-viewer.html` matches the current skill source.
- In-place attempt: `dotnet test tests\AgentDebugToolkit.UiAutomation.Tests\AgentDebugToolkit.UiAutomation.Tests.csproj --filter "FullyQualifiedName=AgentDebugToolkit.UiAutomation.Tests.ControlTypeSelectorTests.Click_ControlTypeDisambiguatesDifferentTypesButNotRepeatedSameTypeMatches" --logger "console;verbosity=normal"` failed before test execution (`0` executed; pass/fail/skip counts not reported). `MSB3027`/`MSB3021` could not copy `src\AgentDebugToolkit.Mcp\obj\Debug\net8.0-windows\apphost.exe` to `src\AgentDebugToolkit.Mcp\bin\Debug\net8.0-windows\agentdebug-mcp.exe`; the destination was locked by `agentdebug-mcp` PIDs 2448, 19920, 22136, and 24912. Restore reported all projects up-to-date.
- Isolated copy: created at `%TEMP%\AgentDebugToolkit-live-test-20261101` using `robocopy "C:\MyFiles\Git\AgentDebugToolkit" "%TEMP%\AgentDebugToolkit-live-test-20261101" /E /XD .git .vs bin obj session-state /R:1 /W:1 /NFL /NDL /NP`; Robocopy exit code `1` (78 files copied, 0 failed). All test commands below ran from that copy.
- Real-click test: `dotnet test tests\AgentDebugToolkit.UiAutomation.Tests\AgentDebugToolkit.UiAutomation.Tests.csproj --filter "FullyQualifiedName=AgentDebugToolkit.UiAutomation.Tests.ControlTypeSelectorTests.Click_ControlTypeDisambiguatesDifferentTypesButNotRepeatedSameTypeMatches" --logger "console;verbosity=normal"` — **1 passed, 0 failed, 0 skipped**. Exact test: `AgentDebugToolkit.UiAutomation.Tests.ControlTypeSelectorTests.Click_ControlTypeDisambiguatesDifferentTypesButNotRepeatedSameTypeMatches`.
- Documented filtered suite: `dotnet test tests\AgentDebugToolkit.UiAutomation.Tests\AgentDebugToolkit.UiAutomation.Tests.csproj --filter "Category!=InteractiveDesktop" --logger "console;verbosity=normal"` — **36 passed, 0 failed, 0 skipped**.
- Complementary category run: `dotnet test tests\AgentDebugToolkit.UiAutomation.Tests\AgentDebugToolkit.UiAutomation.Tests.csproj --filter "Category=InteractiveDesktop" --logger "console;verbosity=normal"` — **1 passed, 0 failed, 0 skipped**; the sole result was the exact real-click test named above.
- Discovery: `dotnet test tests\AgentDebugToolkit.UiAutomation.Tests\AgentDebugToolkit.UiAutomation.Tests.csproj --list-tests` exited `0`, listed **37** test cases, and listed the exact real-click test once. The category run selected exactly that one test; the negated-category run executed the other 36. This is the observed evidence that the filter excludes exactly the named test.
- The isolated restore/build completed. NuGet vulnerability-data requests emitted `NU1900` warnings for configured Azure DevOps feeds; the test runs succeeded.

## Open Tasks / Known Issues

- `set-grid-cell` parity is not consistently demonstrated: intermittent row-resolution failures
  occurred on both builds. Diagnose internal termination and provider/layout timing without
  inferring root cause from snapshots alone.
- Cursor-session concurrent-start ownership race matches the external script and remains a
  documented limitation by user decision; cross-process concurrency and real cursor drawing
  remain unverified.
- Prior unresolved items, not revalidated: installed vs-debug skill update, fresh-chat MCP schema
  checks, cross-process discovery MCP validation, strict-parser live VS/broker checks, and owned
  modeless windows misclassified by `isModal`. `tools\Test-McpServer.ps1` was not run or modified.
- Graph queries previously returned symbol-not-found for ScrollSearch/ResolveGridCell; prior
  caller queries reported duplicate key `Program`. Do not assume those query limitations are resolved.

## Recently Changed Files

- `docs\PROJECT_STATE.md` — recorded the in-place build blocker and isolated live test results.
