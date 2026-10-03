using USBGuardianAgent;

var host = Host.CreateDefaultBuilder(args)
    .UseWindowsService()
    .ConfigureServices(services =>
    {
        services.AddSingleton<AgentConfigManager>();
        services.AddSingleton<SecurityEventOutbox>();
        services.AddSingleton<UsbPolicyManager>();
        services.AddSingleton<UsbDeviceMonitor>();
        services.AddSingleton<IUsbDeviceAccess, WindowsUsbDeviceAccess>();
        services.AddSingleton<AgentSignalRClient>();
        services.AddHostedService<Worker>();
        services.AddHostedService<CommunicationWorker>();
    })
    .Build();

if (args.Any(argument => string.Equals(
        argument,
        "--restore-managed-devices",
        StringComparison.OrdinalIgnoreCase)))
{
    bool restored = host.Services
        .GetRequiredService<UsbPolicyManager>()
        .RestoreManagedDevices();
    Environment.ExitCode = restored ? 0 : 2;
    return;
}

host.Run();
