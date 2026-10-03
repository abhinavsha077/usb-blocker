namespace USBGuardianAgent;

public class CommunicationWorker(ILogger<CommunicationWorker> logger,
    UsbPolicyManager policyManager, AgentSignalRClient signalR) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(8));
                await signalR.StartAsync(deadline.Token);
                bool connected = await signalR.EnsureConnectedAsync(deadline.Token);
                if (connected)
                {
                    var devices = policyManager.GetConnectedDevices().Select(d => new UsbDeviceDto
                    {
                        DeviceID = d.DeviceID, Name = d.Name, Status = d.Status,
                        IsAllowed = d.IsAllowed, IsHub = d.IsHub,
                        ConnectedDeviceName = d.ConnectedDeviceName,
                        IsRunning = d.IsRunning, EnforcementError = d.EnforcementError
                    }).ToList();
                    if (!await signalR.PushDeviceStatusAsync(devices, deadline.Token))
                        throw new IOException("Endpoint status was not accepted by the server.");
                    policyManager.SetControlConnected(true);
                    await signalR.ReportEnforcementAsync(deadline.Token);
                    foreach (var ev in policyManager.PeekSecurityEvents())
                    {
                        if (!await signalR.ReportEventAsync(ev.EventId, ev.CreatedUtc, ev.Type,
                            ev.DeviceId, ev.DeviceName, ev.Detail, deadline.Token)) break;
                        policyManager.AcknowledgeSecurityEvent(ev.EventId);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (OperationCanceledException)
            {
                policyManager.SetControlConnected(false);
                logger.LogWarning("Server communication exceeded its deadline; local enforcement continues.");
            }
            catch (Exception ex)
            {
                policyManager.SetControlConnected(false);
                logger.LogError(ex, "Agent communication failed; local enforcement continues.");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
