using UsbDeviceBridge.App.Services;

namespace UsbDeviceBridge.Tests;

public sealed class LocalDeviceManagerHostnameTests
{
    [Theory]
    // ssh accepts user@host; handing that straight to DNS threw and every ad-hoc
    // "user@host" client failed to attach with "could not be resolved".
    [InlineData("delabrcd@10.215.32.37", "10.215.32.37")]
    [InlineData("delabrcd@desktop.local", "desktop.local")]
    [InlineData("delabrcd@10.215.32.37:2222", "10.215.32.37")]
    [InlineData("10.215.32.37", "10.215.32.37")]
    [InlineData("desktop", "desktop")]
    [InlineData("desktop:22", "desktop")]
    [InlineData("  desktop  ", "desktop")]
    public void ExtractResolvableHostname_StripsUserAndPort(string target, string expected)
    {
        Assert.Equal(expected, LocalDeviceManager.ExtractResolvableHostname(target));
    }

    [Theory]
    // A bare IPv6 literal must survive: splitting on the last colon would corrupt it.
    [InlineData("fe80::1", "fe80::1")]
    [InlineData("[fe80::1]", "fe80::1")]
    [InlineData("[fe80::1]:2222", "fe80::1")]
    [InlineData("user@[fe80::1]:2222", "fe80::1")]
    [InlineData("::1", "::1")]
    public void ExtractResolvableHostname_PreservesIpv6Literals(string target, string expected)
    {
        Assert.Equal(expected, LocalDeviceManager.ExtractResolvableHostname(target));
    }

    [Theory]
    // A non-numeric suffix is not a port, so it must not be stripped.
    [InlineData("desktop:notaport", "desktop:notaport")]
    public void ExtractResolvableHostname_OnlyStripsNumericPorts(string target, string expected)
    {
        Assert.Equal(expected, LocalDeviceManager.ExtractResolvableHostname(target));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("delabrcd@")]
    public void ExtractResolvableHostname_ReturnsEmptyWhenThereIsNoHost(string target)
    {
        Assert.Equal(string.Empty, LocalDeviceManager.ExtractResolvableHostname(target));
    }
}
