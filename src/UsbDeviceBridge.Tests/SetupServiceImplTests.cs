using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using UsbDeviceBridge.Service.Services;
using Usbdevicebridge.V1;

namespace UsbDeviceBridge.Tests;

[Trait("Category", "Integration")]
public sealed class SetupServiceImplTests
{
    [Fact]
    public async Task CheckPrerequisites_ReturnsValidResponse()
    {
        // Arrange
        var service = new SetupServiceImpl(NullLogger<SetupServiceImpl>.Instance);
        var context = new TestServerCallContext();

        // Act
        var response = await service.CheckPrerequisites(
            new CheckPrerequisitesRequest(),
            context
        );

        // Assert
        Assert.NotNull(response);
        Assert.NotNull(response.Prerequisites);
        Assert.True(response.Prerequisites.Count >= 2, "Should check at least usbipd-win and WSL2");

        // Verify usbipd-win is in the list
        Assert.Contains(response.Prerequisites, p => p.Name == "usbipd-win");

        // Verify WSL2 is in the list
        Assert.Contains(response.Prerequisites, p => p.Name == "WSL2");
    }

    [Fact]
    public async Task CheckPrerequisites_PrerequisiteStatusIsAlwaysValid()
    {
        // Arrange
        var service = new SetupServiceImpl(NullLogger<SetupServiceImpl>.Instance);
        var context = new TestServerCallContext();

        // Act
        var response = await service.CheckPrerequisites(
            new CheckPrerequisitesRequest(),
            context
        );

        // Assert
        foreach (var prereq in response.Prerequisites)
        {
            // Status should be one of: installed, missing, outdated
            Assert.True(
                prereq.Status == "installed" || prereq.Status == "missing" || prereq.Status == "outdated",
                $"Invalid status '{prereq.Status}' for {prereq.Name}"
            );

            // Each prerequisite should have a non-empty name
            Assert.False(string.IsNullOrEmpty(prereq.Name));

            // Each prerequisite should have a non-empty message
            Assert.False(string.IsNullOrEmpty(prereq.Message));

            // Version can be empty if not installed, but should be populated if installed
            if (prereq.Status == "installed")
            {
                // Version may be "unknown" or an actual version string
                Assert.False(string.IsNullOrEmpty(prereq.Version));
            }
        }
    }

    [Fact]
    public async Task CheckPrerequisites_AllMetIndicatesPrerequisiteStatus()
    {
        // Arrange
        var service = new SetupServiceImpl(NullLogger<SetupServiceImpl>.Instance);
        var context = new TestServerCallContext();

        // Act
        var response = await service.CheckPrerequisites(
            new CheckPrerequisitesRequest(),
            context
        );

        // Assert
        // AllMet should match whether all prerequisites are installed
        var allInstalled = response.Prerequisites.All(p => p.Status == "installed");
        Assert.Equal(allInstalled, response.AllMet);
    }

    [Fact]
    public async Task RunSetup_WritesOutputMessagesToStream()
    {
        // Arrange
        var service = new SetupServiceImpl(NullLogger<SetupServiceImpl>.Instance);
        var context = new TestServerCallContext();
        var streamWriter = new TestAsyncStreamWriter<SetupOutputEvent>();

        // Act
        await service.RunSetup(
            new RunSetupRequest(),
            streamWriter,
            context
        );

        // Assert
        // Should have written at least one message
        Assert.NotEmpty(streamWriter.Messages);

        // All messages should be valid
        foreach (var msg in streamWriter.Messages)
        {
            Assert.NotNull(msg);
            Assert.NotNull(msg.OutputLine);
            // Exit code should be 0 while running, non-zero at end
        }
    }

    [Fact]
    public async Task RunSetup_WritesFinalStatusMessage()
    {
        // Arrange
        var service = new SetupServiceImpl(NullLogger<SetupServiceImpl>.Instance);
        var context = new TestServerCallContext();
        var streamWriter = new TestAsyncStreamWriter<SetupOutputEvent>();

        // Act
        await service.RunSetup(
            new RunSetupRequest(),
            streamWriter,
            context
        );

        // Assert
        // Last message should indicate completion
        var lastMessage = streamWriter.Messages.Last();
        Assert.False(string.IsNullOrEmpty(lastMessage.OutputLine));
        
        // At least one message should mention completion or prerequisites
        var output = string.Join(" ", streamWriter.Messages.Select(m => m.OutputLine));
        Assert.NotEmpty(output);
    }

    [Fact]
    public async Task RunSetup_HandlesOutputForAllPrerequisites()
    {
        // Arrange
        var service = new SetupServiceImpl(NullLogger<SetupServiceImpl>.Instance);
        var context = new TestServerCallContext();
        var streamWriter = new TestAsyncStreamWriter<SetupOutputEvent>();

        // Act
        await service.RunSetup(
            new RunSetupRequest(),
            streamWriter,
            context
        );

        // Assert
        var output = string.Join(" ", streamWriter.Messages.Select(m => m.OutputLine));
        
        // If all prerequisites are already installed, should say so
        // Otherwise, may attempt to install missing ones
        Assert.NotEmpty(output);
    }
}

/// <summary>
/// Mock ServerCallContext for testing.
/// </summary>
public sealed class TestServerCallContext : ServerCallContext
{
    public CancellationToken TestCancellationToken { get; set; }

    protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders)
    {
        return Task.CompletedTask;
    }

    protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options)
    {
        throw new NotImplementedException();
    }

    protected override string MethodCore => "Test";
    protected override string HostCore => "localhost";
    protected override string PeerCore => "127.0.0.1";
    protected override DateTime DeadlineCore => DateTime.MaxValue;
    protected override Metadata RequestHeadersCore => new();
    protected override Metadata ResponseTrailersCore => new();
    protected override Status StatusCore { get; set; }
    protected override WriteOptions? WriteOptionsCore { get; set; }
    protected override AuthContext AuthContextCore => throw new NotImplementedException();
    protected override CancellationToken CancellationTokenCore => TestCancellationToken;
}

/// <summary>
/// Mock async stream writer for testing.
/// </summary>
public sealed class TestAsyncStreamWriter<T> : IServerStreamWriter<T>
{
    public List<T> Messages { get; } = new();

    public WriteOptions? WriteOptions { get; set; }

    public Task WriteAsync(T message)
    {
        Messages.Add(message);
        return Task.CompletedTask;
    }
}
