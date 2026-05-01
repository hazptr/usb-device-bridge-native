namespace UsbDeviceBridge.Service.Interop;

public readonly record struct ProcessResult(int ExitCode, string StdOut, string StdErr);