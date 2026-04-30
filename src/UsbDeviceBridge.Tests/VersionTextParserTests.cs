using UsbDeviceBridge.Service.Interop;

namespace UsbDeviceBridge.Tests;

public sealed class VersionTextParserTests
{
    [Fact]
    public void ParseWslVersion_ReturnsVersionTokenFromFirstLine()
    {
        var output = "WSL version: 2.4.13.0\r\nKernel version: 5.15.167.4-1";

        var result = VersionTextParser.ParseWslVersion(output);

        Assert.Equal("2.4.13.0", result);
    }

    [Fact]
    public void ParseWslVersion_ReturnsUnknownForEmptyOutput()
    {
        var result = VersionTextParser.ParseWslVersion("   \r\n");

        Assert.Equal("Unknown", result);
    }

    [Fact]
    public void ParseUsbIpdVersion_ReturnsSemanticVersionFromBanner()
    {
        var output = "usbipd-win 5.2.0\r\nCopyright";

        var result = VersionTextParser.ParseUsbIpdVersion(output);

        Assert.Equal("5.2.0", result);
    }
}
