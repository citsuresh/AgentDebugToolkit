using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentDebugToolkit.ConsoleAutomation.Cli;

internal static class NativeConPty
{
    internal const int ProcThreadAttributePseudoConsole = 0x00020016;
    internal const uint ExtendedStartupInfoPresent = 0x00080000;
    internal const uint CreateUnicodeEnvironment = 0x00000400;
    internal const uint StartfUseStdHandles = 0x00000100;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Coord { public short X; public short Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct StartupInfo
    {
        public uint cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern int CreatePseudoConsole(Coord size, SafeFileHandle inputRead, SafeFileHandle outputWrite,
        uint flags, out IntPtr pseudoConsole);

    [DllImport("kernel32.dll")]
    internal static extern void ClosePseudoConsole(IntPtr pseudoConsole);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe,
        IntPtr securityAttributes, int size);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true, EntryPoint = "CreateProcessW",
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcessW(string? applicationName, [In, Out] StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment, string? currentDirectory, ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InitializeProcThreadAttributeList(IntPtr attributeList, uint attributeCount,
        uint flags, ref nuint size);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UpdateProcThreadAttribute(IntPtr attributeList, uint flags,
        nuint attribute, IntPtr value, nuint size, IntPtr previousValue, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    internal static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    [DllImport("kernel32.dll")]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    internal const uint WaitObject0 = 0;
    internal const uint WaitTimeout = 258;
    internal const uint WaitFailed = 0xFFFFFFFF;
    internal const uint Infinite = 0xFFFFFFFF;
    internal const int ErrorAccessDenied = 5;
}
