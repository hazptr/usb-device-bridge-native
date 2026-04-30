using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using UsbDeviceBridge.Service.Domain;
using UsbDeviceBridge.Service.Interop;
using UsbDeviceBridge.Service.Services;
using Usbdevicebridge.V1;

namespace UsbDeviceBridge.Tests;

public sealed class VersionInfoProviderAndDeviceServiceTests
{
    [Fact]
    public async Task VersionInfoProvider_ParsesCommandOutput()
    {
        var runner = new FakeCommandRunner
        {
            WslResult = new ProcessResult(0, "WSL version: 2.3.26.0", ""),
            UsbIpdResult = new ProcessResult(0, "usbipd-win 4.1.0", ""),
        };

        var provider = new VersionInfoProvider(new UsbIpdClient("usbipd"), runner);

        var info = await provider.QueryAsync(CancellationToken.None);

        Assert.Equal("2.3.26.0", info.WslVersion);
        Assert.Equal("4.1.0", info.UsbIpdVersion);
        Assert.False(string.IsNullOrWhiteSpace(info.ServiceVersion));
    }

    [Fact]
    public async Task GetVersionInfo_ReturnsServiceAndToolVersions()
    {
        var runner = new FakeCommandRunner
        {
            WslResult = new ProcessResult(0, "WSL version: 2.0.14.0", ""),
            UsbIpdResult = new ProcessResult(0, "usbipd-win 4.1.0", ""),
        };

        var provider = new VersionInfoProvider(new UsbIpdClient("usbipd"), runner);
        var service = new DeviceServiceImpl(
            NullLogger<DeviceServiceImpl>.Instance,
            new UsbIpdClient("usbipd"),
            new WslInterop(),
            new RememberedDeviceStore(Path.GetTempFileName()),
            new AutoAttachActivityTracker(),
            new ServiceClientConnectionTracker(),
            provider);

        var response = await service.GetVersionInfo(new GetVersionInfoRequest(), new TestServerCallContext());

        Assert.Equal("N/A", response.FrontendVersion);
        Assert.Equal("2.0.14.0", response.WslVersion);
        Assert.Equal("4.1.0", response.UsbipdVersion);
        Assert.False(string.IsNullOrWhiteSpace(response.ServiceVersion));
    }

    private sealed class FakeCommandRunner : ICommandRunner
    {
        public ProcessResult WslResult { get; init; } = new(-1, "", "failed");

        public ProcessResult UsbIpdResult { get; init; } = new(-1, "", "failed");

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, CancellationToken cancellationToken)
        {
            if (string.Equals(fileName, "wsl", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(WslResult);

            return Task.FromResult(UsbIpdResult);
        }
    }
}
