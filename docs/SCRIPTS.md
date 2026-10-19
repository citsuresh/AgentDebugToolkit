# Reusable Scripts

The date records the last run or change. Scripts marked as modifying require approval before each run.

| Script | Purpose | How to call | Read-only or modifies | Date |
| --- | --- | --- | --- | --- |
| `scripts\agent\Rebuild-Mcp.ps1` | Close Claude Desktop, wait for MCP exit, and rebuild MCP with DOTNET_ROOT cleared; stop on remaining processes or build failure. | `& .\scripts\agent\Rebuild-Mcp.ps1` (confirmation required); add `-WhatIf` for a read-only preview. Optional: `-RepoRoot`, `-Configuration`, `-ExitTimeoutSeconds`. | Modifies: stops Claude processes and writes build output; never stops MCP directly. | 2026-10-19 |
