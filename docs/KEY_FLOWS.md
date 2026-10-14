# Key Flows

Program.Verbs.Attach -> SessionContext.Save -> UiaHelper.ListTopLevelWindows -> JsonOutput.WriteSuccess

Program.Verbs.ListWindows/Attach -> UiaHelper.ListTopLevelWindows -> NativeMethods.EnumerateVisibleTopLevelWindows -> JsonOutput.WriteSuccess

Program.Verbs.Inspect -> UiaHelper.FindWindowByHwnd -> UiaHelper.ToElementInfo -> ScreenshotHelper.Capture -> JsonOutput.WriteSuccess

Program.Verbs.Click -> Program.Verbs.ResolveElement -> UiaHelper.FindWindowByHwnd -> UiaHelper.ResolveSelectorAll -> UiaHelper.Click (InvokePattern/TogglePattern, falls back to NativeMethods.Click)

Program.Verbs.Type -> Program.Verbs.ResolveElement -> UiaHelper.ResolveSelectorAll -> UiaHelper.Type (ValuePattern, falls back to NativeMethods.Click + NativeMethods.SendText)

Program.Verbs.ResolveElement -> UiaHelper.ResolveSelectorAll -> Program.Verbs.ToElementSummary -> JsonOutput.WriteError (ambiguous-match)

Program.Verbs.Inspect -> UiaHelper.ToElementInfo -> UiaHelper.PruneElementInfo -> JsonOutput.WriteSuccess (filtered tree and matchCount)

Program.Verbs.SetGridCell -> UiaHelper.ResolveGridCell -> UiaHelper.ResolveGridEditor -> UiaHelper.Type -> UiaHelper.Click (optional Apply)

ConsoleVerbs.Launch -> StartBroker -> BrokerProgram.Run -> ConPtySession -> named-pipe broker session

ConsoleVerbs.SendText/WaitForText/ReadScreen/Stop -> BrokerClient.SendRequest -> BrokerProgram.ServeConnectionAsync -> TerminalBuffer/ConPtySession

Program.Verbs.Activate -> ResolveWindowHwnd -> NativeMethods.SetForegroundWindow -> JsonOutput.WriteSuccess/WriteError(stale-context)

Program.Verbs.SendKeys -> ResolveWindowHwnd -> Program.Verbs.ResolveElement -> UiaHelper.SendKeys (NativeMethods.Click + NativeMethods.SendKeysRaw)

Program.Verbs.SubmitChatMessage -> ResolveWindowHwnd -> UiaHelper.FindWindowByHwnd -> UiaHelper.ResolveSelector (input) -> NativeMethods.Click -> UiaHelper.Type -> UiaHelper.GetText (conditional verify) -> UiaHelper.ResolveSelector (Send) -> UiaHelper.Click
