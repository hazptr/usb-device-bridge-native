using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using UsbDeviceBridge.Service.Domain;
using UsbDeviceBridge.Service.Services;
using Usbdevicebridge.V1;

namespace UsbDeviceBridge.Tests;

public sealed class AutoAttachServiceImplTests
{
    [Fact]
    public async Task ForgetDevice_RemovesRememberedEntry_AndCancelsInflightAttempt()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"remembered-{Guid.NewGuid():N}.json");
        try
        {
            var store = new RememberedDeviceStore(tempFile);
            store.AddOrUpdate("dev-1", "Ubuntu");

            var cancellationRegistry = new AutoAttachAttemptCancellationRegistry();
            using var attemptCts = new CancellationTokenSource();
            cancellationRegistry.Register("dev-1", attemptCts);

            var service = new AutoAttachServiceImpl(
                NullLogger<AutoAttachServiceImpl>.Instance,
                store,
                cancellationRegistry
            );

            var response = await service.ForgetDevice(
                new ForgetDeviceRequest { InstanceId = "dev-1" },
                null!
            );

            Assert.True(response.Ok);
            Assert.Equal("Device forgotten.", response.Message);
            Assert.True(attemptCts.IsCancellationRequested);
            Assert.False(store.Load().ContainsKey("dev-1"));
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ForgetDevice_EmptyInstanceId_ThrowsInvalidArgument()
    {
        var store = new RememberedDeviceStore(Path.Combine(Path.GetTempPath(), $"remembered-{Guid.NewGuid():N}.json"));
        var cancellationRegistry = new AutoAttachAttemptCancellationRegistry();
        var service = new AutoAttachServiceImpl(
            NullLogger<AutoAttachServiceImpl>.Instance,
            store,
            cancellationRegistry
        );

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            service.ForgetDevice(new ForgetDeviceRequest { InstanceId = "" }, null!)
        );

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }
}
