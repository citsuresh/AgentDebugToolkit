# Key Flows

Program.Verbs.Attach -> SessionContext.Save -> UiaHelper.ListTopLevelWindows -> JsonOutput.WriteSuccess

Program.Verbs.Inspect -> UiaHelper.FindWindowByHwnd -> UiaHelper.ToElementInfo -> ScreenshotHelper.Capture -> JsonOutput.WriteSuccess

Program.Verbs.Click -> Program.Verbs.ResolveElement -> UiaHelper.FindWindowByHwnd -> UiaHelper.ResolveSelector -> UiaHelper.Click (InvokePattern/TogglePattern, falls back to NativeMethods.Click)

Program.Verbs.Type -> Program.Verbs.ResolveElement -> UiaHelper.ResolveSelector -> UiaHelper.Type (ValuePattern, falls back to NativeMethods.Click + NativeMethods.SendText)

ConsoleVerbs.Launch -> StartBroker -> BrokerProgram.Run -> ConPtySession -> named-pipe broker session

ConsoleVerbs.SendText/WaitForText/ReadScreen/Stop -> BrokerClient.SendRequest -> BrokerProgram.ServeConnectionAsync -> TerminalBuffer/ConPtySession
