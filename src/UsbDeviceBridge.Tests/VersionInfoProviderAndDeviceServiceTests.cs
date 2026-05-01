using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
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
        var hostLifetime = new TestHostApplicationLifetime();
        var service = new DeviceServiceImpl(
            NullLogger<DeviceServiceImpl>.Instance,
            provider,
            hostLifetime);

        var response = await service.GetVersionInfo(new GetVersionInfoRequest(), new TestServerCallContext());

        Assert.Equal("N/A", response.FrontendVersion);
        Assert.Equal("2.0.14.0", response.WslVersion);
        Assert.Equal("4.1.0", response.UsbipdVersion);
        Assert.False(string.IsNullOrWhiteSpace(response.ServiceVersion));
    }

    [Fact]
    public async Task WatchHeartbeat_StopsWhenApplicationStopping()
    {
        var runner = new FakeCommandRunner
        {
            WslResult = new ProcessResult(0, "WSL version: 2.0.14.0", ""),
            UsbIpdResult = new ProcessResult(0, "usbipd-win 4.1.0", ""),
        };

        var provider = new VersionInfoProvider(new UsbIpdClient("usbipd"), runner);
        var hostLifetime = new TestHostApplicationLifetime();
        var service = new DeviceServiceImpl(
            NullLogger<DeviceServiceImpl>.Instance,
            provider,
            hostLifetime);

        var streamWriter = new TestAsyncStreamWriter<HeartbeatEvent>();
        var streamTask = service.WatchHeartbeat(
            new HeartbeatRequest { IntervalMs = 500 },
            streamWriter,
            new TestServerCallContext());

        await Task.Delay(100);
        hostLifetime.StopApplication();

        await streamTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotEmpty(streamWriter.Messages);
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

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _applicationStarted = new();
        private readonly CancellationTokenSource _applicationStopping = new();
        private readonly CancellationTokenSource _applicationStopped = new();

        public CancellationToken ApplicationStarted => _applicationStarted.Token;

        public CancellationToken ApplicationStopping => _applicationStopping.Token;

        public CancellationToken ApplicationStopped => _applicationStopped.Token;

        public void StopApplication()
        {
            if (!_applicationStopping.IsCancellationRequested)
                _applicationStopping.Cancel();

            if (!_applicationStopped.IsCancellationRequested)
                _applicationStopped.Cancel();
        }
    }
}
