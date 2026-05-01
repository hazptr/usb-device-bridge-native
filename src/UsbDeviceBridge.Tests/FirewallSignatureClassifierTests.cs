using UsbDeviceBridge.Service.Interop;

namespace UsbDeviceBridge.Tests;

/// <summary>
/// Unit tests for <see cref="FirewallSignatureClassifier"/>.
/// </summary>
public sealed class FirewallSignatureClassifierTests
{
    [Theory]
    [InlineData("timed out waiting for usbipd")]
    [InlineData("TIMED OUT waiting")]
    [InlineData("operation timed out")]
    public void IsFirewallBlock_TimedOut_ReturnsTrue(string output)
    {
        Assert.True(FirewallSignatureClassifier.IsFirewallBlock(output));
    }

    [Theory]
    [InlineData("firewall blocked the connection")]
    [InlineData("Windows Firewall rule")]
    [InlineData("FIREWALL")]
    public void IsFirewallBlock_FirewallKeyword_ReturnsTrue(string output)
    {
        Assert.True(FirewallSignatureClassifier.IsFirewallBlock(output));
    }

    [Theory]
    [InlineData("connection to port 3240 refused")]
    [InlineData("TCP 3240")]
    public void IsFirewallBlock_Port3240_ReturnsTrue(string output)
    {
        Assert.True(FirewallSignatureClassifier.IsFirewallBlock(output));
    }

    [Fact]
    public void IsFirewallBlock_GroupPolicy_ReturnsTrue()
    {
        Assert.True(FirewallSignatureClassifier.IsFirewallBlock("blocked by group policy settings"));
    }

    [Fact]
    public void IsFirewallBlock_PublicNetworkProfile_ReturnsTrue()
    {
        Assert.True(FirewallSignatureClassifier.IsFirewallBlock("Public network profile is applied"));
    }

    [Fact]
    public void IsFirewallBlock_BlockingTheConnection_ReturnsTrue()
    {
        Assert.True(FirewallSignatureClassifier.IsFirewallBlock("rule is blocking the connection"));
    }

    [Theory]
    [InlineData("Device not found")]
    [InlineData("BusId is required")]
    [InlineData("Device is already attached")]
    [InlineData("")]
    [InlineData(null)]
    public void IsFirewallBlock_NonFirewallMessages_ReturnsFalse(string? output)
    {
        Assert.False(FirewallSignatureClassifier.IsFirewallBlock(output));
    }

    [Fact]
    public void IsFirewallBlock_CombinedRealWorldOutput_ReturnsTrue()
    {
        const string output =
            "usbipd: error: Failed to attach device with busid '3-1'.\r\n"
            + "Failed to open device. This may be caused by Windows Firewall blocking "
            + "the connection on the Public network profile.";

        Assert.True(FirewallSignatureClassifier.IsFirewallBlock(output));
    }
}
