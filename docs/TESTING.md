# Testing

`ControlTypeSelectorTests.Click_ControlTypeDisambiguatesDifferentTypesButNotRepeatedSameTypeMatches`
performs a real UI click and requires an interactive Windows desktop. Run the rest of the UI
Automation test suite without that test with:

```powershell
dotnet test tests\AgentDebugToolkit.UiAutomation.Tests\AgentDebugToolkit.UiAutomation.Tests.csproj --filter "Category!=InteractiveDesktop"
```

Run the full suite without the filter only in a session with an interactive desktop available.

Test cases for `scripts\agent\Rebuild-Mcp.ps1` and `tools\Test-McpServer.ps1` are in
`docs\SCRIPT_TESTS.md`.
