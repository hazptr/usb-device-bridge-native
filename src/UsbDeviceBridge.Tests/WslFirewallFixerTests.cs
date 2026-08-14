using UsbDeviceBridge.Service.Interop;

namespace UsbDeviceBridge.Tests;

/// <summary>
/// Unit tests for <see cref="WslFirewallFixer"/> outcome parsing. The fix itself shells out to
/// PowerShell and needs elevation, but the classification of its result is what decides whether
/// the app reports success — so that parsing is covered here.
/// </summary>
public sealed class WslFirewallFixerTests
{
    [Theory]
    [InlineData("ok", WslFirewallFixer.FixStatus.Ok)]
    [InlineData("blocked-by-policy", WslFirewallFixer.FixStatus.BlockedByPolicy)]
    [InlineData("not-effective", WslFirewallFixer.FixStatus.NotEffective)]
    [InlineData("no-adapter", WslFirewallFixer.FixStatus.NoAdapter)]
    [InlineData("failed", WslFirewallFixer.FixStatus.Failed)]
    public void ParseOutcome_MapsEachStatus(string token, WslFirewallFixer.FixStatus expected)
    {
        var (status, _) = WslFirewallFixer.ParseOutcome($"STATUS={token}\nDETAIL=some detail\n");
        Assert.Equal(expected, status);
    }

    [Fact]
    public void ParseOutcome_ReturnsDetail()
    {
        var (_, detail) = WslFirewallFixer.ParseOutcome(
            "STATUS=blocked-by-policy\nDETAIL=Group policy manages the Public firewall profile.\n");

        Assert.Equal("Group policy manages the Public firewall profile.", detail);
    }

    [Fact]
    public void ParseOutcome_UnknownStatus_IsFailed()
    {
        var (status, _) = WslFirewallFixer.ParseOutcome("STATUS=banana\nDETAIL=x\n");
        Assert.Equal(WslFirewallFixer.FixStatus.Failed, status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Set-NetFirewallProfile : Access is denied.")]
    public void ParseOutcome_NoStatusLine_ReturnsNull(string? stdout)
    {
        // A missing STATUS line must never be read as success: the caller treats null as failure.
        var (status, _) = WslFirewallFixer.ParseOutcome(stdout);
        Assert.Null(status);
    }

    [Fact]
    public void ParseOutcome_ToleratesCrLfAndSurroundingNoise()
    {
        var (status, detail) = WslFirewallFixer.ParseOutcome(
            "some banner\r\nSTATUS=ok\r\nDETAIL=Excluded from the Public profile: vEthernet (WSL)\r\n");

        Assert.Equal(WslFirewallFixer.FixStatus.Ok, status);
        Assert.Equal("Excluded from the Public profile: vEthernet (WSL)", detail);
    }
}
