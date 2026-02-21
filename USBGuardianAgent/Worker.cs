namespace USBGuardianAgent
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly UsbPolicyManager _policyManager;
        private readonly AgentSignalRClient _signalR;

        public Worker(ILogger<Worker> logger, UsbPolicyManager policyManager, AgentSignalRClient signalR)
        {
            _logger        = logger;
            _policyManager = policyManager;
            _signalR       = signalR;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Connect to the server
            await _signalR.StartAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Enforce local USB policy
                    _policyManager.Enforce();

                    // Push live device status to server
                    var devices = _policyManager.GetConnectedDevices();
                    var dtos = devices.Select(d => new UsbDeviceDto
                    {
                        DeviceID            = d.DeviceID,
                        Name                = d.Name,
                        Status              = d.Status,
                        IsAllowed           = d.IsAllowed,
                        IsHub               = d.IsHub,
                        ConnectedDeviceName = d.ConnectedDeviceName
                    }).ToList();

                    await _signalR.PushDeviceStatusAsync(dtos);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in agent enforcement loop.");
                }

                await Task.Delay(5000, stoppingToken);
            }
        }
    }
}
