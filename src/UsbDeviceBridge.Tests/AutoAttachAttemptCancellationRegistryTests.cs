using UsbDeviceBridge.Service.Domain;

namespace UsbDeviceBridge.Tests;

public class AutoAttachAttemptCancellationRegistryTests
{
    [Fact]
    public void RegisterThenCancel_CancelsAndRemovesAttempt()
    {
        var registry = new AutoAttachAttemptCancellationRegistry();
        using var cts = new CancellationTokenSource();

        var registered = registry.Register("dev-1", cts);
        var canceled = registry.Cancel("dev-1");
        var canceledAgain = registry.Cancel("dev-1");

        Assert.True(registered);
        Assert.True(canceled);
        Assert.True(cts.IsCancellationRequested);
        Assert.False(canceledAgain);
    }

    [Fact]
    public void Complete_RemovesWithoutCanceling()
    {
        var registry = new AutoAttachAttemptCancellationRegistry();
        using var cts = new CancellationTokenSource();

        registry.Register("dev-2", cts);

        var completed = registry.Complete("dev-2");
        var canceled = registry.Cancel("dev-2");

        Assert.True(completed);
        Assert.False(canceled);
        Assert.False(cts.IsCancellationRequested);
    }
}
