using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using USBGuardianAgent;

namespace USBGuardianTests;

public sealed class EnforcementRegressionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "usb-guardian-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeDevices _devices = new();
    private readonly UsbDeviceMonitor _monitor = new(NullLogger<UsbDeviceMonitor>.Instance);
    private string PathFor(string name) => Path.Combine(_directory, name);
    private IConfiguration Configuration => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["AgentPolicyCachePath"] = PathFor("policy.json"),
        ["ManagedDeviceStatePath"] = PathFor("managed.json"),
        ["AlertDirectory"] = PathFor("alerts"),
        ["SecurityEventQueuePath"] = PathFor("events.json")
    }).Build();

    private UsbPolicyManager Manager() => new(NullLogger<UsbPolicyManager>.Instance,
        new SecurityEventOutbox(Configuration, NullLogger<SecurityEventOutbox>.Instance), Configuration, _devices, _monitor);

    [Fact]
    public void UaspUsbTransportIsBlockedAndRestoredByItsExactIdentity()
    {
        _devices.InstanceId = @"USB\VID_1234&PID_5678\Uasp-Transport";
        _devices.Service = "UASPStor";
        _devices.Class = "SCSIAdapter";
        var manager = Manager();
        manager.Enforce();
        Assert.Equal(22u, _devices.Code);
        Assert.Contains(_devices.InstanceId, JsonSerializer.Deserialize<string[]>(File.ReadAllText(PathFor("managed.json")))!);
        manager.ApplyRemotePolicy(new MachinePolicies { IsUnlocked = true, Revision = 1 });
        manager.Enforce();
        Assert.Equal(0u, _devices.Code);
        Assert.Equal(new[] { "disable-device", "enable-device" }, _devices.Actions);
    }

    [Fact]
    public void LearnModeIsFailClosedOfflineAndLocalLockCancelsIt()
    {
        var manager = Manager();
        manager.ApplyRemotePolicy(new MachinePolicies { LearnMode = true, LearnModeExpiry = DateTime.UtcNow.AddHours(1), Revision = 1 });
        manager.Enforce(); // offline
        Assert.Equal(22u, _devices.Code);
        manager.SetControlConnected(true);
        manager.Enforce();
        Assert.Equal(0u, _devices.Code);
        manager.Lock();
        manager.Enforce();
        Assert.Equal(22u, _devices.Code);
    }

    [Fact]
    public async Task EnforcementContinuesWhileServerNegotiationStalls()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        Directory.CreateDirectory(_directory);
        File.WriteAllText(PathFor("config.json"), JsonSerializer.Serialize(new AgentConfig
        {
            ServerUrl = $"http://127.0.0.1:{port}", MachineId = "test-endpoint", AgentToken = "isolated-token"
        }));
        var configuration = new ConfigurationBuilder().AddConfiguration(Configuration)
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AgentConfigPath"] = PathFor("config.json") }).Build();
        var manager = Manager();
        await using var client = new AgentSignalRClient(new AgentConfigManager(configuration,
            NullLogger<AgentConfigManager>.Instance), manager, NullLogger<AgentSignalRClient>.Instance);
        using var enforcement = new Worker(NullLogger<Worker>.Instance, manager, _monitor);
        using var communication = new CommunicationWorker(NullLogger<CommunicationWorker>.Instance, manager, client);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await enforcement.StartAsync(timeout.Token);
        await communication.StartAsync(timeout.Token);
        try
        {
            using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
            var firstScan = manager.GetEnforcementStatus().CheckedUtc;
            await Task.Delay(TimeSpan.FromMilliseconds(6500), timeout.Token);
            Assert.False(communication.ExecuteTask!.IsCompleted);
            Assert.NotNull(firstScan);
            Assert.True(manager.GetEnforcementStatus().CheckedUtc > firstScan);
            Assert.Single(_devices.Actions);
        }
        finally
        {
            timeout.Cancel();
            await communication.StopAsync(CancellationToken.None);
            await enforcement.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void CorruptOwnershipJournalIsPreservedAndCannotBeOverwritten()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(PathFor("managed.json"), "{ corrupted journal");
        var manager = Manager();
        manager.Enforce();
        Assert.Empty(_devices.Actions);
        Assert.Null(manager.GetEnforcementStatus().Revision);
        Assert.False(manager.RestoreManagedDevices());
        Assert.Equal("{ corrupted journal", File.ReadAllText(PathFor("managed.json")));
    }

    [Fact]
    public void BlockingIsJournaledBeforeHardwareAndSurvivesRestart()
    {
        var manager = Manager();
        _devices.BeforeSet = (_, _) => Assert.Contains(FakeDevices.Id,
            JsonSerializer.Deserialize<string[]>(File.ReadAllText(PathFor("managed.json")))!);
        manager.Enforce();
        Assert.Equal(22u, _devices.Code);
        Assert.Single(_devices.Actions);
        manager.Enforce();
        Assert.Single(_devices.Actions); // already disabled: no reconnect loop
        _devices.BeforeSet = null;
        var restarted = Manager();
        restarted.ApplyRemotePolicy(new MachinePolicies { IsUnlocked = true, Revision = 1 });
        restarted.Enforce();
        Assert.Equal(0u, _devices.Code);
        Assert.Empty(JsonSerializer.Deserialize<string[]>(File.ReadAllText(PathFor("managed.json")))!);
    }

    [Fact]
    public void JournalFailureSkipsDisableAndDoesNotClaimVerifiedProtection()
    {
        var manager = Manager();
        File.WriteAllText(PathFor("managed.json"), "[]");
        using (var fileLock = new FileStream(PathFor("managed.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            manager.Enforce();
            Assert.Empty(_devices.Actions);
            Assert.Null(manager.GetEnforcementStatus().Revision);
        }
        manager.Enforce();
        Assert.Equal(22u, _devices.Code);
        Assert.Single(_devices.Actions);
    }

    [Fact]
    public void CacheFailureRetainsThePreviousDurablePolicy()
    {
        var manager = Manager();
        manager.ApplyRemotePolicy(new MachinePolicies { IsUnlocked = true, Revision = 1 });
        using (var fileLock = new FileStream(PathFor("policy.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var error = Record.Exception(() => manager.ApplyRemotePolicy(new MachinePolicies { Revision = 2 }));
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.True(manager.IsUnlocked);
        }
        Assert.True(Manager().IsUnlocked);
        manager.ApplyRemotePolicy(new MachinePolicies { Revision = 2 });
        Assert.False(Manager().IsUnlocked);
    }

    [Fact]
    public void FailedOwnershipCleanupRemainsUnverifiedAndRetriesWithoutRetogglingHardware()
    {
        var manager = Manager();
        manager.Enforce();
        manager.ApplyRemotePolicy(new MachinePolicies { IsUnlocked = true, Revision = 1 });
        // Hold across the actual hardware result and journal cleanup.
        using (var hold = new FileStream(PathFor("managed.json"), FileMode.Open,
            FileAccess.ReadWrite, FileShare.None))
        {
            manager.Enforce();
            Assert.Equal(0u, _devices.Code);
            Assert.Null(manager.GetEnforcementStatus().Revision);
        }
        manager.Enforce();
        Assert.Equal(1L, manager.GetEnforcementStatus().Revision);
        Assert.Equal(new[] { "disable-device", "enable-device" }, _devices.Actions);
        Assert.Empty(JsonSerializer.Deserialize<string[]>(File.ReadAllText(PathFor("managed.json")))!);
    }

    [Fact]
    public void AllowDoesNotRestoreDevicesDisabledExternally()
    {
        _devices.Code = 22;
        var manager = Manager();
        manager.ApplyRemotePolicy(new MachinePolicies { IsUnlocked = true });
        manager.Enforce();
        Assert.Empty(_devices.Actions);
        Assert.Equal(22u, _devices.Code);
    }

    [Fact]
    public void FailedHardwareBlockIsShownAsEnabledAndUnverified()
    {
        _devices.FailAction = true;
        var manager = Manager();
        manager.Enforce();
        var reported = manager.GetConnectedDevices().Single();
        Assert.False(reported.IsAllowed);
        Assert.True(reported.IsRunning);
        Assert.Contains("still enabled", reported.EnforcementError);
        Assert.Null(manager.GetEnforcementStatus().Revision);
        manager.Enforce();
        Assert.Single(_devices.Actions); // failure backoff avoids repeated popups/retries
    }

    [Theory]
    [InlineData("Keyboard", "kbdhid")]
    [InlineData("HIDClass", "HidUsb")]
    [InlineData("USB", "USBHUB3")]
    [InlineData("USB", "usbccgp")]
    public void InputAndSharedParentsAreNeverDisabledByRemovableRules(string pnpClass, string service)
    {
        _devices.Class = pnpClass;
        _devices.Service = service;
        var manager = Manager();
        manager.Enforce();
        Assert.Empty(_devices.Actions);
    }

    [Fact]
    public void PnputilUsesAHiddenAbsoluteExecutableAndExactInstanceArgument()
    {
        string id = @"USB\VID_1234&PID_5678\CaseSensitive-instance";
        var start = WindowsUsbDeviceAccess.CreateStartInfo("disable-device", id);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "pnputil.exe"), start.FileName);
        Assert.True(start.CreateNoWindow);
        Assert.False(start.UseShellExecute);
        Assert.Equal(System.Diagnostics.ProcessWindowStyle.Hidden, start.WindowStyle);
        Assert.Equal(new[] { "/disable-device", id }, start.ArgumentList);
    }

    [Fact]
    public void WpdUsbPhoneIsBlockedAndRestoredWithoutTouchingItsParent()
    {
        _devices.InstanceId = @"SWD\WPDBUSENUM\_??_USB#VID_1234&PID_5678#Phone";
        _devices.Class = "WPD";
        _devices.Service = "WUDFRd";
        _devices.BeforeSet = (_, id) => Assert.Equal(_devices.InstanceId, id);
        var manager = Manager();
        manager.Enforce();
        Assert.Equal(22u, _devices.Code);
        manager.ApplyRemotePolicy(new MachinePolicies { IsUnlocked = true, Revision = 1 });
        manager.Enforce();
        Assert.Equal(0u, _devices.Code);
        Assert.Equal(new[] { "disable-device", "enable-device" }, _devices.Actions);
    }

    [Fact]
    public void PortableQueryDoesNotClassifyThisHostsWebcamByItsName()
    {
        using var searcher = new System.Management.ManagementObjectSearcher(UsbDeviceQueries.Portable);
        using var results = searcher.Get();
        foreach (System.Management.ManagementObject device in results)
        {
            using (device)
            {
                Assert.Contains(device["PNPClass"]?.ToString(), new[] { "WPD", "PortableDevice" });
                Assert.NotEqual("usbvideo", device["Service"]?.ToString());
            }
        }
    }

    [Theory]
    [InlineData("Camera", "usbvideo")]
    [InlineData("Media", "usbaudio")]
    [InlineData("Net", "RNDIS")]
    public void RemovableRulesDoNotDisableOtherUsbFunctions(string pnpClass, string service)
    {
        _devices.InstanceId = @"USB\VID_1234&PID_5678\Another-function";
        _devices.Class = pnpClass;
        _devices.Service = service;
        var manager = Manager();
        manager.Enforce();
        Assert.Empty(_devices.Actions);
        Assert.Empty(manager.GetConnectedDevices());
    }

    [Fact]
    public void ProductionWqlQueriesAreValidOnWindows()
    {
        foreach (string wql in UsbDeviceQueries.Removable)
        {
            using var searcher = new System.Management.ManagementObjectSearcher(wql);
            using var results = searcher.Get();
            Assert.True(results.Count >= 0); // execute actual WQL, without touching devices
        }
    }

    private sealed class FakeDevices : IUsbDeviceAccess
    {
        public const string Id = @"USBSTOR\DISK&VEN_REVIEW&PROD_TEST\Unit-1";
        public string InstanceId = Id;
        public uint Code = 0;
        public string Class = "DiskDrive";
        public string Service = "USBSTOR";
        public bool FailAction;
        public Action<string, string>? BeforeSet;
        public List<string> Actions = new();
        public IReadOnlyList<DeviceSnapshot> Query(string wql) => wql ==
            (Class is "WPD" or "PortableDevice" ? UsbDeviceQueries.Portable : UsbDeviceQueries.Storage)
            ? new[] { new DeviceSnapshot(new Dictionary<string, object?>
                { ["PNPDeviceID"] = InstanceId, ["Name"] = "Review drive", ["Status"] = Code == 0 ? "OK" : "Error",
                    ["ConfigManagerErrorCode"] = Code, ["PNPClass"] = Class, ["Service"] = Service }) }
            : Array.Empty<DeviceSnapshot>();
        public bool SetState(string verb, string id)
        {
            BeforeSet?.Invoke(verb, id);
            Actions.Add(verb);
            if (FailAction) return false;
            Code = verb == "disable-device" ? 22u : 0u;
            return true;
        }
    }

    public void Dispose()
    {
        _monitor.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
