# Key Flows

Executable Program -> ToolkitIdentity.TryHandle -> ToolkitIdentity.Read -> PackageVerifier.Verify (optional) -> minified identity envelope, before live subsystem initialization

ToolkitInfoTools.Info -> enabled-group gate -> CliRunner.RunAsync(info) / ToolkitIdentity.CreateResult(server) -> ToolInvocationResult

Program.Verbs.Attach -> SessionContext.Save -> UiaHelper.ListTopLevelWindows -> JsonOutput.WriteSuccess

Program.Verbs.ListWindows/Attach -> UiaHelper.ListTopLevelWindows -> NativeMethods.EnumerateVisibleTopLevelWindows -> JsonOutput.WriteSuccess

Program.Verbs.Inspect -> UiaHelper.FindWindowByHwnd -> UiaHelper.ToElementInfo -> ScreenshotHelper.Capture -> JsonOutput.WriteSuccess

Program.Verbs.Click -> Program.Verbs.ResolveElement -> UiaHelper.FindWindowByHwnd -> UiaHelper.ResolveSelectorAll -> UiaHelper.Click (InvokePattern/TogglePattern, falls back to NativeMethods.Click)

Program.Verbs.Type -> Program.Verbs.ResolveElement -> UiaHelper.ResolveSelectorAll -> UiaHelper.Type (ValuePattern, falls back to NativeMethods.Click + NativeMethods.SendText)

Program.Verbs.ResolveElement -> UiaHelper.ResolveSelectorAll -> Program.Verbs.ToElementSummary -> JsonOutput.WriteError (ambiguous-match)

Program.Verbs.Inspect -> UiaHelper.ToElementInfo -> UiaHelper.PruneElementInfo -> JsonOutput.WriteSuccess (filtered tree and matchCount)

Program.Verbs.SetGridCell -> UiaHelper.ResolveGridCell -> UiaHelper.ResolveGridEditor -> UiaHelper.Type -> UiaHelper.Click (optional Apply)

ConsoleVerbs.Launch -> StartBroker -> BrokerProgram.Run -> ConPtySession -> named-pipe broker session

CommandLine.TryParseOptions -> ConsoleVerbs.Launch -> ConsoleVerbs.StartBroker (ArgumentList flag/value pairs) -> BrokerProgram.Run -> BrokerProgram.ParseOptions

UiAutomationTools/VsDebuggerTools/ConsoleTools.BuildArgs -> ArgumentFormatting.AppendOption -> CliRunner.RunAsync

ConsoleVerbs.SendText/WaitForText/ReadScreen/Stop -> BrokerClient.SendRequest -> BrokerProgram.ServeConnectionAsync -> TerminalBuffer/ConPtySession

Program.Verbs.Activate -> Program.Verbs.ValidateResolvedWindowHwnd -> NativeMethods.SetForegroundWindow -> JsonOutput.WriteSuccess/WriteError

Program.Verbs.SendKeys -> ResolveWindowHwnd -> Program.Verbs.ResolveElement -> UiaHelper.SendKeys (NativeMethods.Click + NativeMethods.SendKeysRaw)

Program.Verbs.SubmitChatMessage -> ResolveWindowHwnd -> UiaHelper.FindWindowByHwnd -> UiaHelper.ResolveSelector (input) -> Program.Verbs.TryEnsureChatInputForeground -> NativeMethods.Click -> UiaHelper.Type -> UiaHelper.GetText (conditional verify) -> UiaHelper.ResolveSelector (Send) -> UiaHelper.Click

Program.Verbs.ScrollContainer -> ResolveWindowHwnd -> UiaHelper.ResolveSelectorAll -> UiaHelper.GetScrollPattern/SetScrollPercent -> bounded position polling -> JsonOutput.WriteSuccess/WriteError

Program.Verbs.ScrollIntoView -> ResolveWindowHwnd -> UiaHelper.ResolveSelectorAll -> UiaHelper.TryFindAndRealizeContainerItem/ScrollSearch -> UIA scroll patterns -> JsonOutput.WriteSuccess/WriteError

UiAutomationTools.CursorSessionStart/Stop -> CliRunner.RunAsync -> Program.Verbs.CursorSessionVerb -> CursorSession.Start/Stop -> SystemCursorOperations.Mark/Restore -> JsonOutput.WriteSuccess

Program.Verbs.ResolveWindowHwnd/ResolveElement -> Program.Verbs.ValidateResolvedWindowHwnd -> NativeMethods.EnsureWindowExists/IsResponding -> window-gone/window-not-responding

Program.Verbs.Screenshot -> Program.Verbs.ResolveWindowHwnd -> ScreenshotHelper.CaptureWindow -> NativeMethods.GetWindowRect/GetDpiForWindow -> ScreenshotHelper.TryPrintWindow/CaptureScreenRegion -> ScreenshotHelper.CropAndScale (optional) -> ScreenshotHelper.SaveBitmap -> JsonOutput.WriteSuccess

Program.Verbs.Click/RightClick/DoubleClick (coordinates) -> TryValidateCoordinateTarget (preflight unless minimized) -> EnsureTargetWindowForeground (restore/wait if minimized) -> TryValidateCoordinateTarget (fresh bounds) -> NativeMethods.Click/RightClick/DoubleClick

VS Program -> DebuggerDiscovery.TryHandleHelp -> canonical verb/alias descriptors -> JsonOutput.WriteSuccess (no DTE lookup)

VsDebuggerTools.ListInstances -> CliRunner.RunAsync -> DteLocator.ListInstances -> EnumerateDteInstances/ParseInstancePid -> nullable per-field observations/errors -> JsonOutput.WriteSuccess

VsDebuggerTools targeted operation -> RunAsync (instancePid validation/forwarding) -> VS Program target validation -> DteLocator.FindDte/FindProcessByPid -> DebuggerDiscovery.SelectInstance (PID then solution, unique explicit match) -> existing debugger verb

Verbs.DebuggerStatus (context=true) -> DteLocator.FindDte (selected moniker) -> GetStatusSnapshot -> BreakContextReader.Read -> ReadExceptionLocal/observed thread/frame/last-hit/end-mode -> nullable non-atomic context

VsDebuggerTools.WaitForBreak -> DebuggerWaitOptions.TryParse/ProcessTimeoutMs -> CliRunner.RunAsync -> Verbs.WaitForBreak -> DebuggerWaitOptions.TryParse/Poll -> GetStatusSnapshot -> existing success or timeout facts
