using USBGuardianAgent;

Host.CreateDefaultBuilder(args)
    .UseWindowsService()
    .ConfigureServices(services =>
    {
        services.AddSingleton<AgentConfigManager>();
        services.AddSingleton<UsbPolicyManager>();
        services.AddSingleton<AgentSignalRClient>();
        services.AddHostedService<Worker>();
    })
    .Build()
    .Run();
