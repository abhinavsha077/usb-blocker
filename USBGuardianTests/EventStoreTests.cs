using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using USBGuardianServer;

namespace USBGuardianTests;

public sealed class EventStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(
            Path.GetTempPath(),
            "usb-guardian-tests",
            Guid.NewGuid().ToString("N"));

    [Fact]
    public void AgentEventIdIsIdempotentAcrossStoreInstances()
    {
        Guid eventId = Guid.NewGuid();
        var first = CreateStore();
        var securityEvent = new UsbEvent
        {
            EventId = eventId,
            MachineId = "machine-1",
            EventType = "BadUSBBlocked",
            DeviceId = @"USB\VID_1234&PID_5678\A"
        };

        Assert.True(first.Append(securityEvent));
        Assert.False(first.Append(securityEvent));
        Assert.False(CreateStore().Append(securityEvent));

        var persisted = CreateStore().GetRecent("machine-1", 20);
        Assert.Single(persisted);
        Assert.Equal(eventId, persisted[0].EventId);
    }

    [Fact]
    public void AdministrativeEventsWithoutIdAreNotDeduplicated()
    {
        var store = CreateStore();
        var administrativeEvent = new UsbEvent
        {
            MachineId = "machine-1",
            EventType = "Locked",
            Detail = "by admin"
        };

        Assert.True(store.Append(administrativeEvent));
        Assert.True(store.Append(administrativeEvent));
        Assert.Equal(2, store.GetRecent("machine-1", 20).Count);
    }

    [Fact]
    public void AgentEventPreservesItsOriginalUtcOccurrenceTime()
    {
        DateTime occurredUtc = DateTime.UtcNow.AddHours(-2);
        var store = CreateStore();

        Assert.True(store.Append(new UsbEvent
        {
            EventId = Guid.NewGuid(),
            Timestamp = occurredUtc,
            MachineId = "machine-1",
            EventType = "Blocked"
        }));

        UsbEvent persisted = Assert.Single(store.GetRecent("machine-1", 20));
        Assert.Equal(DateTimeKind.Utc, persisted.Timestamp.Kind);
        Assert.Equal(occurredUtc, persisted.Timestamp);
    }

    private EventStore CreateStore()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EventDir"] = _directory,
                ["Cef:Enabled"] = "false",
                ["Smtp:Enabled"] = "false"
            })
            .Build();
        return new EventStore(
            configuration,
            NullLogger<EventStore>.Instance);
    }

    [Fact]
    public void FailedAppendCanBeRetriedWithoutLosingItsEventId()
    {
        var store = CreateStore();
        store.Append(new UsbEvent { MachineId = "machine-1", EventType = "Locked" });
        var ev = new UsbEvent { MachineId = "machine-1", EventId = Guid.NewGuid(), EventType = "Blocked" };
        using (var fileLock = new FileStream(Directory.GetFiles(_directory, "*.jsonl", SearchOption.AllDirectories).Single(), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<IOException>(() => store.Append(ev));
        Assert.True(store.Append(ev));
        Assert.False(store.Append(ev));
        Assert.Single(store.GetRecent("machine-1", 20), e => e.EventId == ev.EventId);
    }

    [Fact]
    public void IdentitiesWithTheSameLegacyFilenameHaveIndependentEventReceipts()
    {
        var store = CreateStore();
        Guid eventId = Guid.NewGuid();
        Assert.True(store.Append(new UsbEvent { MachineId = "machine 1", EventId = eventId, EventType = "Blocked" }));
        Assert.True(store.Append(new UsbEvent { MachineId = "machine_1", EventId = eventId, EventType = "Allowed" }));
        Assert.Equal("Blocked", Assert.Single(CreateStore().GetRecent("machine 1", 20)).EventType);
        Assert.Equal("Allowed", Assert.Single(CreateStore().GetRecent("machine_1", 20)).EventType);
        Assert.Equal(2, Directory.GetFiles(_directory, "*.jsonl", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void LegacyHistoryIsFilteredByExactIdentityAndPreservesReceiptsAcrossUpgrade()
    {
        var store = CreateStore();
        Guid own = Guid.NewGuid();
        var rows = new[] {
            new UsbEvent { MachineId = "machine 1", EventId = own, EventType = "Blocked" },
            new UsbEvent { MachineId = "machine_1", EventId = Guid.NewGuid(), EventType = "Allowed" }
        };
        File.WriteAllLines(Path.Combine(_directory, "machine_1.jsonl"), rows.Select(row => System.Text.Json.JsonSerializer.Serialize(row)));
        Assert.False(store.Append(rows[0]));
        Assert.Equal(own, Assert.Single(store.GetRecent("machine 1", 20)).EventId);
        Assert.Equal("Allowed", Assert.Single(store.GetRecent("machine_1", 20)).EventType);
        Assert.True(store.Append(new UsbEvent { MachineId = "machine 1", EventId = Guid.NewGuid(), EventType = "Locked" }));
        Assert.Equal(2, CreateStore().GetRecent("machine 1", 20).Count);
    }

    [Fact]
    public void RetentionKeepsTheAcknowledgedEventAndBoundedRecentHistory()
    {
        var store = CreateStore();
        store.Append(new UsbEvent { MachineId = "machine-1", EventType = "Locked" });
        string file = Directory.GetFiles(_directory, "*.jsonl", SearchOption.AllDirectories).Single();
        string old = System.Text.Json.JsonSerializer.Serialize(new UsbEvent
            { MachineId = "machine-1", Timestamp = DateTime.UtcNow.AddDays(-1), Detail = new string('x', 1000) });
        File.WriteAllLines(file, Enumerable.Repeat(old, 6000));
        Guid latest = Guid.NewGuid();
        Assert.True(store.Append(new UsbEvent { MachineId = "machine-1", EventId = latest, EventType = "Blocked" }));
        Assert.InRange(File.ReadLines(file).Count(), 1, 5000);
        Assert.True(new FileInfo(file).Length < 2_000_000);
        Assert.Equal(latest, store.GetRecent("machine-1", 1).Single().EventId);
        Assert.False(CreateStore().Append(new UsbEvent { MachineId = "machine-1", EventId = latest, EventType = "Blocked" }));
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
