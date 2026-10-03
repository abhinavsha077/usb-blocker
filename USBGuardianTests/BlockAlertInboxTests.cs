using System.Text.Json;
using USBGuardianNotifier;

namespace USBGuardianTests;

public sealed class BlockAlertInboxTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "usb-alert-tests", Guid.NewGuid().ToString("N"));
    private readonly DateTime _now = DateTime.UtcNow;

    public BlockAlertInboxTests() => Directory.CreateDirectory(_directory);

    private string Write(string name, DateTime? timestamp = null)
    {
        string path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { DeviceName = name, Timestamp = timestamp ?? _now }));
        return path;
    }

    [Fact]
    public void TwoSessionsReceiveTheSameAlertWithoutDeletingEachOthersInbox()
    {
        string path = Write("Blocked drive");
        var first = new BlockAlertInbox(_directory);
        var second = new BlockAlertInbox(_directory);
        Assert.Equal(new[] { "Blocked drive" }, first.ReadPending(_now));
        Assert.Equal(new[] { "Blocked drive" }, second.ReadPending(_now));
        Assert.True(File.Exists(path));
        Assert.Empty(first.ReadPending(_now));
        Write("Another drive");
        Assert.Equal(new[] { "Another drive" }, first.ReadPending(_now));
    }

    [Fact]
    public void ATemporarilyLockedAlertIsRetriedRatherThanDeleted()
    {
        string path = Write("Busy drive");
        var inbox = new BlockAlertInbox(_directory);
        using (var fileLock = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Empty(inbox.ReadPending(_now));
        Assert.Equal(new[] { "Busy drive" }, inbox.ReadPending(_now));
    }

    [Fact]
    public void OldFutureMalformedAndOversizedAlertsAreIgnored()
    {
        Write("Old", _now.AddMinutes(-3));
        Write("Future", _now.AddMinutes(2));
        File.WriteAllText(Path.Combine(_directory, "malformed.json"), "{ bad json");
        File.WriteAllText(Path.Combine(_directory, "oversized.json"), new string('x', 65537));
        var inbox = new BlockAlertInbox(_directory);
        Assert.Empty(inbox.ReadPending(_now));
        Write(new string('a', 512));
        Assert.Equal(new string('a', 256), Assert.Single(inbox.ReadPending(_now)));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
