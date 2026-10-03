using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using USBGuardianServer;

namespace USBGuardianTests;

public sealed class MachineInventoryStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(
            Path.GetTempPath(),
            "usb-guardian-tests",
            Guid.NewGuid().ToString("N"));
    private readonly string _inventoryPath;

    public MachineInventoryStoreTests()
    {
        _inventoryPath = Path.Combine(_directory, "inventory.json");
    }

    [Fact]
    public void IdentityAndDeviceCountPersistAcrossInstances()
    {
        var first = CreateStore();
        first.Register("machine-1", "WORKSTATION-1", "10.0.0.10");
        first.Touch("machine-1", deviceCount: 4, forcePersist: true);

        var record = CreateStore().Snapshot()["machine-1"];

        Assert.Equal("WORKSTATION-1", record.MachineName);
        Assert.Equal("10.0.0.10", record.IpAddress);
        Assert.Equal(4, record.LastDeviceCount);
        Assert.NotEqual(default, record.LastSeenUtc);
    }

    [Fact]
    public void SnapshotCannotMutateStoredInventory()
    {
        var store = CreateStore();
        store.Register("machine-1", "WORKSTATION-1", "10.0.0.10");

        store.Snapshot()["machine-1"].MachineName = "changed";

        Assert.Equal(
            "WORKSTATION-1",
            store.Snapshot()["machine-1"].MachineName);
    }

    [Fact]
    public void MalformedInventoryIsNotOverwritten()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(_inventoryPath, "{ invalid json");

        var store = CreateStore();

        Assert.Empty(store.Snapshot());
        Assert.Equal("{ invalid json", File.ReadAllText(_inventoryPath));
    }

    private MachineInventoryStore CreateStore()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["InventoryFilePath"] = _inventoryPath
            })
            .Build();
        return new MachineInventoryStore(
            configuration,
            NullLogger<MachineInventoryStore>.Instance);
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
