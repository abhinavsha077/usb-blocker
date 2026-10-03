using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using USBGuardianAgent;

namespace USBGuardianTests;

public sealed class SecurityEventOutboxTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(
            Path.GetTempPath(),
            "usb-guardian-tests",
            Guid.NewGuid().ToString("N"));
    private readonly string _queuePath;

    public SecurityEventOutboxTests()
    {
        _queuePath = Path.Combine(_directory, "events.json");
    }

    [Fact]
    public void EventRemainsQueuedAcrossInstancesUntilAcknowledged()
    {
        var first = CreateOutbox();
        first.Enqueue(new SecurityEvent(
            "BadUSBBlocked",
            @"USB\VID_1234&PID_5678\A",
            "Keyboard",
            "unapproved keyboard disabled"));

        var loaded = CreateOutbox();
        var queued = Assert.Single(loaded.Peek());

        Assert.Equal("BadUSBBlocked", queued.Type);
        Assert.True(loaded.Acknowledge(queued.EventId));
        Assert.Empty(CreateOutbox().Peek());
    }

    [Fact]
    public void PeekReturnsDetachedRecordsAndHonorsLimit()
    {
        var outbox = CreateOutbox();
        outbox.Enqueue(new SecurityEvent("First", "", "", ""));
        outbox.Enqueue(new SecurityEvent("Second", "", "", ""));

        var first = Assert.Single(outbox.Peek(1));

        Assert.Equal("First", first.Type);
        Assert.Equal(2, outbox.Peek().Count);
    }

    [Fact]
    public void InvalidAcknowledgementDoesNotChangeQueue()
    {
        var outbox = CreateOutbox();
        outbox.Enqueue(new SecurityEvent("BadUSBBlocked", "", "", ""));

        Assert.False(outbox.Acknowledge(Guid.NewGuid()));
        Assert.Single(outbox.Peek());
    }

    private SecurityEventOutbox CreateOutbox()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SecurityEventQueuePath"] = _queuePath
            })
            .Build();
        return new SecurityEventOutbox(
            configuration,
            NullLogger<SecurityEventOutbox>.Instance);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch
        {
        }
    }
}
