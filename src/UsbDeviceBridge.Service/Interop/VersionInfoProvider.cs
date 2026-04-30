using System.Reflection;

namespace UsbDeviceBridge.Service.Interop;

public sealed record VersionInfoSnapshot(string ServiceVersion, string WslVersion, string UsbIpdVersion);

public sealed class VersionInfoProvider(UsbIpdClient usbIpdClient, ICommandRunner commandRunner)
{
    private const string UnknownVersion = "Unknown";

    public async Task<VersionInfoSnapshot> QueryAsync(CancellationToken cancellationToken)
    {
        var serviceVersion = ResolveServiceVersion();

        var wslResult = await commandRunner.RunAsync("wsl", ["--version"], cancellationToken);
        var wslVersion = wslResult.ExitCode == 0
            ? VersionTextParser.ParseWslVersion(wslResult.StdOut)
            : UnknownVersion;

        var usbIpdResult = await commandRunner.RunAsync(usbIpdClient.UsbIpdPath, ["--version"], cancellationToken);
        var usbIpdVersion = usbIpdResult.ExitCode == 0
            ? VersionTextParser.ParseUsbIpdVersion(usbIpdResult.StdOut)
            : UnknownVersion;

        return new VersionInfoSnapshot(serviceVersion, wslVersion, usbIpdVersion);
    }

    private static string ResolveServiceVersion()
    {
        var assembly = typeof(VersionInfoProvider).Assembly;
        return assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? UnknownVersion;
    }
}