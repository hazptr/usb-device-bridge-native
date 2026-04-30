using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UsbDeviceBridge.Service.Interop;

/// <summary>P/Invoke declarations for wslapi.dll and relevant kernel32 APIs.</summary>
internal static class WslApiNativeMethods
{
    [Flags]
    internal enum WslDistributionFlags : uint
    {
        None = 0,
        EnableInterop = 1,
        AppendNtPath = 2,
        EnableDriveMounting = 4,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SecurityAttributes
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
    }

    internal const uint HandleFlagInherit = 0x00000001;

    [DllImport("wslapi.dll", CharSet = CharSet.Unicode)]
    internal static extern int WslConfigureDistribution(
        string distributionName,
        uint defaultUID,
        WslDistributionFlags wslDistributionFlags
    );

    [DllImport("wslapi.dll", CharSet = CharSet.Unicode)]
    internal static extern int WslGetDistributionConfiguration(
        string distributionName,
        out uint distributionVersion,
        out uint defaultUid,
        out WslDistributionFlags wslDistributionFlags,
        out IntPtr defaultEnvironmentVariables,
        out uint defaultEnvironmentVariableCount
    );

    [DllImport("wslapi.dll", CharSet = CharSet.Unicode)]
    internal static extern int WslLaunch(
        string distributionName,
        string command,
        [MarshalAs(UnmanagedType.Bool)] bool useCurrentWorkingDirectory,
        SafeFileHandle stdIn,
        SafeFileHandle stdOut,
        SafeFileHandle stdErr,
        out IntPtr process
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CreatePipe(
        out SafeFileHandle hReadPipe,
        out SafeFileHandle hWritePipe,
        ref SecurityAttributes lpPipeAttributes,
        int nSize
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool SetHandleInformation(
        SafeFileHandle hObject,
        uint dwMask,
        uint dwFlags
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr hObject);

    [DllImport("wslapi.dll")]
    internal static extern void WslFreeMemory(IntPtr memoryPointer);
}
