using UsbDeviceBridge.App.Services;

namespace UsbDeviceBridge.Tests;

/// <summary>
/// Unit tests for <see cref="AttachToastMessages"/>.
/// </summary>
public sealed class AttachToastMessagesTests
{
    // ── PolicyPrevented ───────────────────────────────────────────────────────

    [Fact]
    public void PolicyPrevented_ContainsDeviceName()
    {
        var msg = AttachToastMessages.PolicyPrevented("USB Camera");
        Assert.Contains("USB Camera", msg);
    }

    [Fact]
    public void PolicyPrevented_ContainsFirewallGuidance()
    {
        var msg = AttachToastMessages.PolicyPrevented("Any Device");
        Assert.Contains("Firewall", msg, StringComparison.OrdinalIgnoreCase);
    }

    // ── FirewallFixFailed ─────────────────────────────────────────────────────

    [Fact]
    public void FirewallFixFailed_ContainsDeviceName()
    {
        var msg = AttachToastMessages.FirewallFixFailed("USB Hub");
        Assert.Contains("USB Hub", msg);
    }

    [Fact]
    public void FirewallFixFailed_MentionsFirewall()
    {
        var msg = AttachToastMessages.FirewallFixFailed("Any Device");
        Assert.Contains("firewall", msg, StringComparison.OrdinalIgnoreCase);
    }

    // ── StillFailedAfterFix ───────────────────────────────────────────────────

    [Fact]
    public void StillFailedAfterFix_ContainsDeviceName()
    {
        var msg = AttachToastMessages.StillFailedAfterFix("USB Keyboard");
        Assert.Contains("USB Keyboard", msg);
    }

    [Fact]
    public void StillFailedAfterFix_MentionsFirewallFix()
    {
        var msg = AttachToastMessages.StillFailedAfterFix("Any Device");
        Assert.Contains("fix", msg, StringComparison.OrdinalIgnoreCase);
    }

    // ── FirewallFixAppliedAndSucceeded ────────────────────────────────────────

    [Fact]
    public void FirewallFixAppliedAndSucceeded_ContainsDeviceAndDistro()
    {
        var msg = AttachToastMessages.FirewallFixAppliedAndSucceeded("USB Audio", "Ubuntu-22.04");
        Assert.Contains("USB Audio", msg);
        Assert.Contains("Ubuntu-22.04", msg);
    }

    [Fact]
    public void FirewallFixAppliedAndSucceeded_MentionsFirewallFix()
    {
        var msg = AttachToastMessages.FirewallFixAppliedAndSucceeded("Any Device", "Ubuntu");
        Assert.Contains("fix", msg, StringComparison.OrdinalIgnoreCase);
    }

    // ── Auto-attach variants ──────────────────────────────────────────────────

    [Fact]
    public void AutoAttachFirewallFixApplied_ContainsDeviceId()
    {
        var msg = AttachToastMessages.AutoAttachFirewallFixApplied("USB\\VID_1234&PID_5678");
        Assert.Contains("USB\\VID_1234&PID_5678", msg);
    }

    [Fact]
    public void PolicyPreventedAutoAttach_ContainsDeviceId()
    {
        var msg = AttachToastMessages.PolicyPreventedAutoAttach("USB\\VID_1234&PID_5678");
        Assert.Contains("USB\\VID_1234&PID_5678", msg);
    }

    [Fact]
    public void AutoAttachStillFailedAfterFix_ContainsDeviceId()
    {
        var msg = AttachToastMessages.AutoAttachStillFailedAfterFix("USB\\VID_1234&PID_5678");
        Assert.Contains("USB\\VID_1234&PID_5678", msg);
    }

    [Fact]
    public void AutoAttachFirewallFixFailed_ContainsDeviceId()
    {
        var msg = AttachToastMessages.AutoAttachFirewallFixFailed("USB\\VID_1234&PID_5678");
        Assert.Contains("USB\\VID_1234&PID_5678", msg);
    }

    // ── All messages are non-empty ────────────────────────────────────────────

    [Fact]
    public void AllMessages_AreNonEmpty()
    {
        Assert.NotEmpty(AttachToastMessages.PolicyPrevented("D"));
        Assert.NotEmpty(AttachToastMessages.FirewallFixFailed("D"));
        Assert.NotEmpty(AttachToastMessages.StillFailedAfterFix("D"));
        Assert.NotEmpty(AttachToastMessages.FirewallFixAppliedAndSucceeded("D", "distro"));
        Assert.NotEmpty(AttachToastMessages.PolicyPreventedAutoAttach("id"));
        Assert.NotEmpty(AttachToastMessages.AutoAttachFirewallFixApplied("id"));
        Assert.NotEmpty(AttachToastMessages.AutoAttachStillFailedAfterFix("id"));
        Assert.NotEmpty(AttachToastMessages.AutoAttachFirewallFixFailed("id"));
    }
}
