using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace USBGuardianService
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly UsbPolicyManager _policyManager;

        public Worker(ILogger<Worker> logger, UsbPolicyManager policyManager)
        {
            _logger = logger;
            _policyManager = policyManager;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("USB Guardian Service started at: {time}", DateTimeOffset.Now);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _policyManager.Enforce();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error enforcing USB policy");
                }

                await Task.Delay(5000, stoppingToken);
            }
        }
    }
}
