# Reusable Scripts

The date records the last run or change. Scripts marked as modifying require approval before each run.

| Script | Purpose | How to call | Read-only or modifies | Date |
| --- | --- | --- | --- | --- |
| `scripts\agent\Rebuild-Mcp.ps1` | Close Claude Desktop, wait for MCP exit, and rebuild the MCP and the UI automation CLI (the MCP runs `agentdebug-ui.exe` as a separate process) with DOTNET_ROOT cleared; stop on remaining processes or build failure. | `& .\scripts\agent\Rebuild-Mcp.ps1` (confirmation required); add `-WhatIf` for a read-only preview. Optional: `-RepoRoot`, `-Configuration`, `-ExitTimeoutSeconds`. | Modifies: stops Claude processes and writes build output; never stops MCP directly. | 2026-11-08 |
| `scripts\agent\Publish-AgentDebugToolkit.ps1` | Publish all four tools as self-contained Windows x64 builds, with usage docs, to a timestamp-free package folder; failed staging and replaced packages are retained. | `& .\scripts\agent\Publish-AgentDebugToolkit.ps1` (confirmation required); add `-WhatIf` to preview. Defaults to Release and `C:\Shared\AgentDebugToolkit-win-x64`. Optional: `-Destination`, `-Configuration`, `-ReplaceExisting`. A copy outside the repo requires `-RepoRoot <source-repository-path>`. Requires the .NET SDK and access to configured NuGet feeds; does not change sources. | Modifies: writes build outputs and package folders; replacement requires `-ReplaceExisting`, preserves a backup, and never stops processes. | 2026-11-06 |
