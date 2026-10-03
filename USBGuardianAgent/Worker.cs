namespace USBGuardianAgent;

// Enforcement must never wait for server connections or event delivery.
public class Worker(ILogger<Worker> logger, UsbPolicyManager policyManager,
    UsbDeviceMonitor deviceMonitor) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try { policyManager.Enforce(); }
            catch (Exception ex) { logger.LogError(ex, "Error in agent enforcement loop."); }
            try { await deviceMonitor.WaitForNextScanAsync(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}