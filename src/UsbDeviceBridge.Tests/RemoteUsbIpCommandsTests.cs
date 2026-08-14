using UsbDeviceBridge.App.Services;

namespace UsbDeviceBridge.Tests;

public sealed class RemoteUsbIpCommandsTests
{
    [Theory]
    [InlineData("1-6")]
    [InlineData("1-7")]
    [InlineData("2-14")]
    [InlineData("1-2.3")]
    [InlineData("1-2.3.4")]
    public void IsValidBusId_AcceptsRealBusIds(string busId)
    {
        Assert.True(RemoteUsbIpCommands.IsValidBusId(busId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1")]
    [InlineData("abc")]
    [InlineData("1-6; rm -rf /")]
    [InlineData("1-6 && curl evil.example")]
    [InlineData("$(id)")]
    [InlineData("`id`")]
    public void IsValidBusId_RejectsMalformedOrInjectingValues(string? busId)
    {
        Assert.False(RemoteUsbIpCommands.IsValidBusId(busId));
    }

    [Fact]
    public void BuildAttachScript_RejectsInvalidBusId()
    {
        Assert.Throws<ArgumentException>(
            () => RemoteUsbIpCommands.BuildAttachScript("1-6; reboot", "127.0.0.1", 3240));
    }

    [Fact]
    public void BuildAttachScript_LoadsVhciModuleWhenAbsent()
    {
        var script = RemoteUsbIpCommands.BuildAttachScript("1-6", "127.0.0.1", 3240);

        // The module is not loaded by default on most distros and does not survive a
        // reboot, which is what made attach fail with "open vhci_driver".
        Assert.Contains("/proc/modules", script);
        Assert.Contains("sudo -n modprobe vhci_hcd", script);
        Assert.Contains($"exit {RemoteUsbIpCommands.ExitVhciUnavailable}", script);
    }

    [Fact]
    public void BuildAttachScript_SettlesUdevAndRetries()
    {
        var script = RemoteUsbIpCommands.BuildAttachScript("1-6", "127.0.0.1", 3240);

        Assert.Contains("udevadm settle", script);
        Assert.Contains($"seq 1 {RemoteUsbIpCommands.AttachAttempts}", script);
    }

    [Fact]
    public void BuildAttachScript_VerifiesEnumerationRatherThanTrustingExitCode()
    {
        var script = RemoteUsbIpCommands.BuildAttachScript("1-6", "127.0.0.1", 3240);

        Assert.Contains("usbip port", script);
        Assert.Contains("usbip://[^/]+/${BUSID}", script);
        Assert.Contains($"exit {RemoteUsbIpCommands.ExitNotEnumerated}", script);
    }

    [Fact]
    public void BuildAttachScript_MarksAcceptedOnlyInsideTheAttachSuccessBranch()
    {
        var script = RemoteUsbIpCommands.BuildAttachScript("1-6", "127.0.0.1", 3240);

        // Setting the flag once per iteration made ExitAttachFailed unreachable: every
        // run reported ExitNotEnumerated, even when usbip never accepted the device.
        var loopStart = script.IndexOf("for attempt in", StringComparison.Ordinal);
        var attachCall = script.IndexOf("usbip attach", StringComparison.Ordinal);
        var acceptedFlag = script.IndexOf("ACCEPTED=1;", StringComparison.Ordinal);

        Assert.True(loopStart >= 0 && attachCall > loopStart);
        Assert.True(acceptedFlag > attachCall, "ACCEPTED must be set after the attach call, inside its success branch.");
        Assert.DoesNotContain("TRIED", script);
    }

    [Fact]
    public void BuildAttachScript_DistinguishesAttachFailedFromNotEnumerated()
    {
        var script = RemoteUsbIpCommands.BuildAttachScript("1-6", "127.0.0.1", 3240);

        Assert.Contains($"if [ \"$ACCEPTED\" = \"1\" ]; then exit {RemoteUsbIpCommands.ExitNotEnumerated}; fi;", script);
        Assert.Contains($"exit {RemoteUsbIpCommands.ExitAttachFailed}", script);
        Assert.NotEqual(RemoteUsbIpCommands.ExitAttachFailed, RemoteUsbIpCommands.ExitNotEnumerated);
    }

    [Fact]
    public void BuildAttachScript_ClearsAStaleImportBeforeAttaching()
    {
        var script = RemoteUsbIpCommands.BuildAttachScript("1-6", "127.0.0.1", 3240);

        // An app restart loses the in-memory bus-id -> host map, leaving the remote with a
        // dead import that still occupies the vhci port and blocks every later attach.
        Assert.Contains("STALE=$(sudo -n usbip port", script);
        Assert.Contains("sudo -n usbip detach --port \"$STALE\"", script);

        var stalePos = script.IndexOf("STALE=", StringComparison.Ordinal);
        var attachPos = script.IndexOf("usbip attach", StringComparison.Ordinal);
        Assert.True(stalePos > 0 && stalePos < attachPos, "the stale import must be cleared before attaching");
    }

    [Fact]
    public void BuildAttachScript_ClearsStaleImportOnlyAfterEnsuringVhciIsLoaded()
    {
        var script = RemoteUsbIpCommands.BuildAttachScript("1-6", "127.0.0.1", 3240);

        // usbip port cannot report anything until vhci_hcd exists.
        var modprobePos = script.IndexOf("modprobe vhci_hcd", StringComparison.Ordinal);
        var stalePos = script.IndexOf("STALE=", StringComparison.Ordinal);
        Assert.True(modprobePos > 0 && modprobePos < stalePos);
    }

    [Fact]
    public void BuildAttachScript_StaleClearIsBestEffort()
    {
        var script = RemoteUsbIpCommands.BuildAttachScript("1-6", "127.0.0.1", 3240);

        // A stale import we cannot clear must not abort the attach; the retry loop reports
        // the real failure with a far more useful exit code.
        Assert.Contains("sudo -n usbip detach --port \"$STALE\" >/dev/null 2>&1 || true;", script);
    }

    [Fact]
    public void BuildAttachAndDetachScripts_ShareOnePortLookup()
    {
        var attach = RemoteUsbIpCommands.BuildAttachScript("1-6", "127.0.0.1", 3240);
        var detach = RemoteUsbIpCommands.BuildDetachScript("1-6");

        const string lookupFragment = "$1 == \"Port\" { p = $2; sub(/:/, \"\", p); next }";
        Assert.Contains(lookupFragment, attach);
        Assert.Contains(lookupFragment, detach);
    }

    [Fact]
    public void BuildDetachScript_RejectsInvalidBusId()
    {
        Assert.Throws<ArgumentException>(() => RemoteUsbIpCommands.BuildDetachScript("1-6; reboot"));
    }

    [Fact]
    public void BuildDetachScript_LooksUpThePortRatherThanPassingTheBusId()
    {
        var script = RemoteUsbIpCommands.BuildDetachScript("1-6");

        // usbip detach takes a vhci port number, not a bus id.
        Assert.Contains("usbip port", script);
        Assert.Contains("usbip detach --port \"$PORT\"", script);
        Assert.Contains("BUSID='1-6'", script);
        Assert.Contains($"exit {RemoteUsbIpCommands.ExitDetachFailed}", script);
    }

    [Fact]
    public void BuildDetachScript_IsIdempotentWhenNothingIsImported()
    {
        var script = RemoteUsbIpCommands.BuildDetachScript("1-6");

        // No vhci_hcd, or no matching port, both mean "already detached" -> success.
        Assert.Contains("if ! grep -q '^vhci_hcd ' /proc/modules 2>/dev/null; then exit 0; fi;", script);
        Assert.Contains("if [ -z \"$PORT\" ]; then exit 0; fi;", script);
    }

    [Fact]
    public void IndicatesDeviceNotExported_DetectsTheUsbipdSideFailure()
    {
        Assert.True(RemoteUsbIpCommands.IndicatesDeviceNotExported(
            "usbip: error: Attach Request for 1-7 failed - Device not found"));
        Assert.False(RemoteUsbIpCommands.IndicatesDeviceNotExported("usbip: error: open vhci_driver"));
        Assert.False(RemoteUsbIpCommands.IndicatesDeviceNotExported(null));
    }

    [Fact]
    public void DescribeFailure_DeviceNotExported_BlamesSharingRatherThanTheTunnel()
    {
        // usbip reports this as a successful request that never enumerated, so the
        // exit code alone would produce a message blaming the reverse tunnel.
        var message = RemoteUsbIpCommands.DescribeFailure(
            RemoteUsbIpCommands.ExitNotEnumerated,
            "usbip: error: Attach Request for 1-7 failed - Device not found",
            "desktop");

        Assert.Contains("not sharing this device", message);
        Assert.Contains("forced bind", message);
        Assert.DoesNotContain("reverse tunnel may have dropped", message);
    }

    [Fact]
    public void DescribeFailure_DetachFailed_SaysDeviceMayStillBeClaimed()
    {
        var message = RemoteUsbIpCommands.DescribeFailure(
            RemoteUsbIpCommands.ExitDetachFailed,
            "usbip: error: Invalid port 3",
            "desktop",
            "detach");

        Assert.Contains("usbip detach failed", message);
        Assert.Contains("desktop", message);
        Assert.Contains("Invalid port 3", message);
    }

    [Fact]
    public void DescribeFailure_UsesTheOperationNounInTheGenericFallback()
    {
        var message = RemoteUsbIpCommands.DescribeFailure(255, string.Empty, "desktop", "detach");

        Assert.Contains("detach", message);
        Assert.DoesNotContain("attach", message);
    }

    [Fact]
    public void BuildAttachScript_UsesSuppliedHostAndPort()
    {
        var script = RemoteUsbIpCommands.BuildAttachScript("1-6", "10.0.0.5", 5555);

        Assert.Contains("RHOST='10.0.0.5'", script);
        Assert.Contains("RPORT='5555'", script);
        Assert.Contains("BUSID='1-6'", script);
    }

    [Theory]
    [InlineData("sudo: a password is required")]
    [InlineData("sudo: no tty present and no askpass program specified")]
    [InlineData("sudo: a terminal is required to read the password")]
    [InlineData("Sorry, user delabrcd may not run /usr/sbin/modprobe on fedora.")]
    public void IndicatesSudoPasswordRequired_DetectsMissingNopasswdRule(string output)
    {
        Assert.True(RemoteUsbIpCommands.IndicatesSudoPasswordRequired(output));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("usbip: error: open vhci_driver")]
    public void IndicatesSudoPasswordRequired_IgnoresUnrelatedOutput(string? output)
    {
        Assert.False(RemoteUsbIpCommands.IndicatesSudoPasswordRequired(output));
    }

    [Fact]
    public void IndicatesVhciDriverUnavailable_DetectsTheDocumentedError()
    {
        Assert.True(RemoteUsbIpCommands.IndicatesVhciDriverUnavailable("usbip: error: open vhci_driver"));
        Assert.False(RemoteUsbIpCommands.IndicatesVhciDriverUnavailable("usbip: error: already used"));
        Assert.False(RemoteUsbIpCommands.IndicatesVhciDriverUnavailable(null));
    }

    [Fact]
    public void DescribeFailure_VhciUnavailable_PointsAtSetup()
    {
        var message = RemoteUsbIpCommands.DescribeFailure(
            RemoteUsbIpCommands.ExitVhciUnavailable,
            "modprobe: FATAL: Module vhci_hcd not found",
            "desktop");

        Assert.Contains("vhci_hcd", message);
        Assert.Contains("desktop", message);
        Assert.Contains("Setup", message);
        Assert.Contains("Module vhci_hcd not found", message);
    }

    [Fact]
    public void DescribeFailure_NotEnumerated_MentionsTunnelAsLikelyCause()
    {
        var message = RemoteUsbIpCommands.DescribeFailure(
            RemoteUsbIpCommands.ExitNotEnumerated,
            string.Empty,
            "desktop");

        Assert.Contains("never appeared", message);
        Assert.Contains("tunnel", message);
    }

    [Fact]
    public void DescribeFailure_AttachFailed_ReportsAttemptCount()
    {
        var message = RemoteUsbIpCommands.DescribeFailure(
            RemoteUsbIpCommands.ExitAttachFailed,
            "usbip: error: open vhci_driver",
            "desktop");

        Assert.Contains($"{RemoteUsbIpCommands.AttachAttempts} attempts", message);
        Assert.Contains("open vhci_driver", message);
    }

    [Fact]
    public void DescribeFailure_SudoPasswordTakesPrecedenceOverExitCode()
    {
        var message = RemoteUsbIpCommands.DescribeFailure(
            RemoteUsbIpCommands.ExitVhciUnavailable,
            "sudo: a password is required",
            "desktop");

        Assert.Contains("passwordless sudo", message);
        Assert.Contains("modprobe", message);
    }

    [Fact]
    public void DescribeFailure_UnknownExitCode_FallsBackToRemoteOutput()
    {
        var message = RemoteUsbIpCommands.DescribeFailure(7, "some remote failure", "desktop");
        Assert.Equal("some remote failure", message);

        var empty = RemoteUsbIpCommands.DescribeFailure(7, "  ", "desktop");
        Assert.Contains("exit code 7", empty);
    }
}
