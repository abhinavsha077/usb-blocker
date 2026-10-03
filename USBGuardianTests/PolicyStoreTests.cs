using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using USBGuardianServer;

namespace USBGuardianTests;

public class PolicyStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "usb-guardian-tests", Guid.NewGuid().ToString("N"));
    private readonly string _policyPath;

    public PolicyStoreTests()
    {
        _policyPath = Path.Combine(_directory, "policies.json");
    }

    [Fact]
    public void ReturnedPoliciesCannotMutateStoredState()
    {
        var store = CreateStore();
        store.Update("machine-1",
            policy => policy.AllowedDevices.Add(@"USB\VID_1234&PID_5678\A"));

        var detached = store.GetOrCreate("machine-1");
        detached.AllowedDevices.Clear();

        Assert.Single(store.GetOrCreate("machine-1").AllowedDevices);
    }

    [Fact]
    public void ExpiredTimedUnlockIsNormalizedToLocked()
    {
        var store = CreateStore();

        var policy = store.Update("machine-1", current =>
        {
            current.IsUnlocked = true;
            current.UnlockExpiry = DateTime.UtcNow.AddMinutes(-1);
        });

        Assert.False(policy.IsUnlocked);
        Assert.Null(policy.UnlockExpiry);
    }

    [Fact]
    public void LearnModeWithoutExpiryFailsClosed()
    {
        var store = CreateStore();

        var policy = store.Update("machine-1", current =>
        {
            current.LearnMode = true;
            current.LearnModeExpiry = null;
        });

        Assert.False(policy.LearnMode);
        Assert.Null(policy.LearnModeExpiry);
    }

    [Fact]
    public void PolicyPersistsAcrossStoreInstances()
    {
        var first = CreateStore();
        first.Update("machine-1", policy =>
        {
            policy.Group = "Engineering";
            policy.BadUsbBlock = true;
            policy.AllowedDevices.Add(@"USB\VID_1234&PID_5678\A");
        });

        var loaded = CreateStore().GetOrCreate("machine-1");

        Assert.Equal("Engineering", loaded.Group);
        Assert.True(loaded.BadUsbBlock);
        Assert.Single(loaded.AllowedDevices);
    }

    private PolicyStore CreateStore()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PolicyFilePath"] = _policyPath
            })
            .Build();
        return new PolicyStore(configuration, NullLogger<PolicyStore>.Instance);
    }

    [Fact]
    public void FailedPolicyWriteDoesNotPublishTheCandidate()
    {
        var store = CreateStore();
        var original = store.GetOrCreate("machine-1");
        using (var fileLock = new FileStream(_policyPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var error = Record.Exception(() => store.Update("machine-1", p => p.IsUnlocked = true));
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.False(store.Snapshot()["machine-1"].IsUnlocked);
            Assert.Equal(original.Revision, store.Snapshot()["machine-1"].Revision);
        }
        Assert.False(CreateStore().GetOrCreate("machine-1").IsUnlocked);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch { }
    }
}
