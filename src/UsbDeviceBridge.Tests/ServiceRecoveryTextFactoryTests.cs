using UsbDeviceBridge.App.Services;

namespace UsbDeviceBridge.Tests;

public sealed class ServiceRecoveryTextFactoryTests
{
    [Fact]
    public void ServiceNotRunning_UsesRequiredUserFacingMessage()
    {
        var text = ServiceRecoveryTextFactory.ServiceNotRunning();

        Assert.Equal("It looks like the service is not running.", text.Message);
        Assert.Contains("Restart Service", text.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void Reconnecting_ExplainsThatActionsAreDisabled()
    {
        var text = ServiceRecoveryTextFactory.Reconnecting();

        Assert.Contains("Device actions", text.Details, StringComparison.Ordinal);
        Assert.Contains("disabled", text.Details, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RestartCancelledOrBlocked_PreservesRetryGuidance()
    {
        var text = ServiceRecoveryTextFactory.RestartCancelledOrBlocked();

        Assert.Equal("It looks like the service is not running.", text.Message);
        Assert.Contains("try again", text.Details, StringComparison.OrdinalIgnoreCase);
    }
}
