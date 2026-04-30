using UsbDeviceBridge.Service.Interop;

namespace UsbDeviceBridge.Tests;

public sealed class WslDistroParserTests
{
    [Fact]
    public void ParseVerbose_ParsesNameAndState_AndSkipsHeader()
    {
        var stdout = "NAME                   STATE           VERSION\n* Ubuntu-24.04         Running         2\n  Debian               Stopped         2\n";

        var entries = WslDistroParser.ParseVerbose(stdout);

        Assert.Equal(2, entries.Count);
        Assert.Equal("Ubuntu-24.04", entries[0].Name);
        Assert.Equal(WslDistroRuntimeState.Running, entries[0].RuntimeState);
        Assert.Equal("Debian", entries[1].Name);
        Assert.Equal(WslDistroRuntimeState.Stopped, entries[1].RuntimeState);
    }

    [Fact]
    public void ParseVerbose_PreservesDistroNamesWithSpaces()
    {
        var stdout = "NAME                   STATE           VERSION\n  Ubuntu Dev           Running         2\n";

        var entries = WslDistroParser.ParseVerbose(stdout);

        var entry = Assert.Single(entries);
        Assert.Equal("Ubuntu Dev", entry.Name);
        Assert.Equal(WslDistroRuntimeState.Running, entry.RuntimeState);
    }

    [Fact]
    public void ParseQuiet_ParsesNamesAndRemovesDefaultMarker()
    {
        var stdout = "* Ubuntu-24.04\nDebian\n\n";

        var names = WslDistroParser.ParseQuiet(stdout);

        Assert.Equal(2, names.Count);
        Assert.Equal("Ubuntu-24.04", names[0]);
        Assert.Equal("Debian", names[1]);
    }

    [Fact]
    public void ParseQuiet_StripsBomFromFirstName()
    {
        // wsl --list --quiet outputs UTF-16 LE with a BOM. When Process reads it
        // with Encoding.Unicode (detectEncodingFromByteOrderMarks=false), the BOM
        // character U+FEFF ends up prepended to the first distro name.
        var stdout = "\uFEFFUbuntu\nDebian\n";

        var names = WslDistroParser.ParseQuiet(stdout);

        Assert.Equal(2, names.Count);
        Assert.Equal("Ubuntu", names[0]);
        Assert.Equal("Debian", names[1]);
    }

    [Fact]
    public void ParseVerbose_StripsBomFromFirstName()
    {
        var stdout = "\uFEFFNAME                   STATE           VERSION\n  Ubuntu               Running         2\n";

        var entries = WslDistroParser.ParseVerbose(stdout);

        var entry = Assert.Single(entries);
        Assert.Equal("Ubuntu", entry.Name);
    }

    [Fact]
    public void ParseVerbose_UnknownState_MapsToUnknown()
    {
        var stdout = "NAME                   STATE           VERSION\n  Alpine               Paused          2\n";

        var entries = WslDistroParser.ParseVerbose(stdout);

        var entry = Assert.Single(entries);
        Assert.Equal("Alpine", entry.Name);
        Assert.Equal(WslDistroRuntimeState.Unknown, entry.RuntimeState);
    }

    [Fact]
    public void BuildSelectableDistros_PrioritizesRunningAndKeepsInstalled()
    {
        var validInstalled = new[] { "Ubuntu", "Debian", "Arch" };
        var running = new[] { "Debian" };

        var distros = WslDistroParser.BuildSelectableDistros(validInstalled, running);

        Assert.Equal(3, distros.Count);
        Assert.Equal("Debian", distros[0]);
        Assert.Contains("Ubuntu", distros);
        Assert.Contains("Arch", distros);
    }

    [Fact]
    public void BuildSelectableDistros_WhenNoRunning_ReturnsInstalled()
    {
        var validInstalled = new[] { "Ubuntu", "Debian" };

        var distros = WslDistroParser.BuildSelectableDistros(validInstalled, Array.Empty<string>());

        Assert.Equal(2, distros.Count);
        Assert.Equal("Ubuntu", distros[0]);
        Assert.Equal("Debian", distros[1]);
    }
}