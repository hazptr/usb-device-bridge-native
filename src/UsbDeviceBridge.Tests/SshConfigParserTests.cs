using UsbDeviceBridge.App.Services;

namespace UsbDeviceBridge.Tests;

public sealed class SshConfigParserTests : IDisposable
{
    private readonly string _tempDir;

    public SshConfigParserTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "UsbDeviceBridgeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void ParseConfig_MultipleHostAliasesPerLine_AndDedupes()
    {
        var root = Path.Combine(_tempDir, "config");
        File.WriteAllText(root, "Host alpha beta alpha\n");

        var parser = new SshConfigParser(root);
        var hosts = parser.GetHostAliases();

        Assert.Equal(new[] { "alpha", "beta" }, hosts);
    }

    [Fact]
    public void ParseConfig_NestedIncludeDirectives_LoadsHosts()
    {
        var root = Path.Combine(_tempDir, "config");
        var includedDir = Path.Combine(_tempDir, "includes");
        Directory.CreateDirectory(includedDir);

        var nested = Path.Combine(includedDir, "nested.conf");
        File.WriteAllText(nested, "Host nested-one\n");

        var first = Path.Combine(includedDir, "first.conf");
        File.WriteAllText(first, $"Host first-one\nInclude {nested}\n");

        File.WriteAllText(root, $"Include {first}\nHost root-one\n");

        var parser = new SshConfigParser(root);
        var hosts = parser.GetHostAliases();

        Assert.Equal(new[] { "first-one", "nested-one", "root-one" }, hosts);
    }

    [Fact]
    public void ParseConfig_HandlesCommentsAndWhitespace()
    {
        var root = Path.Combine(_tempDir, "config");
        File.WriteAllText(root, "  # comment\nHost   alpha   # inline\n\nHost beta\n");

        var parser = new SshConfigParser(root);
        var hosts = parser.GetHostAliases();

        Assert.Equal(new[] { "alpha", "beta" }, hosts);
    }

    [Fact]
    public void ParseConfig_ExcludesWildcardOnlyHosts()
    {
        var root = Path.Combine(_tempDir, "config");
        File.WriteAllText(root, "Host *\nHost ?\nHost stable-host\n");

        var parser = new SshConfigParser(root);
        var hosts = parser.GetHostAliases();

        Assert.Equal(new[] { "stable-host" }, hosts);
    }

    [Theory]
    [InlineData("my-host", true)]
    [InlineData("my_host", true)]
    [InlineData("my.host:2222", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("bad host", false)]
    [InlineData("-bad", false)]
    [InlineData("host$", false)]
    public void IsValidAdHocHost_ValidatesExpectedShapes(string input, bool expected)
    {
        Assert.Equal(expected, SshConfigParser.IsValidAdHocHost(input));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
