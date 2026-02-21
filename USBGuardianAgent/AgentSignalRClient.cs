using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace USBGuardianAgent
{
    public class MachinePolicies
    {
        public List<string> AllowedDevices { get; set; } = new();
        public List<string> BlockedDevices { get; set; } = new();
        public bool IsUnlocked { get; set; } = false;
    }

    public class AgentSignalRClient : IAsyncDisposable
    {
        private readonly AgentConfigManager _agentConfig;
        private readonly UsbPolicyManager   _policyManager;
        private readonly ILogger<AgentSignalRClient> _logger;
        private HubConnection? _connection;

        public AgentSignalRClient(AgentConfigManager agentConfig, UsbPolicyManager policyManager,
                                  ILogger<AgentSignalRClient> logger)
        {
            _agentConfig   = agentConfig;
            _policyManager = policyManager;
            _logger        = logger;
        }

        public async Task StartAsync(CancellationToken ct)
        {
            string hubUrl = _agentConfig.Config.ServerUrl.TrimEnd('/') + "/agenthub";
            _connection = new HubConnectionBuilder()
                .WithUrl(hubUrl)
                .WithAutomaticReconnect(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5),
                                                TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30) })
                .Build();

            // Server pushes updated policy → apply locally
            _connection.On<MachinePolicies>("ApplyPolicy", policies =>
            {
                _logger.LogInformation("Received policy update from server. Unlocked={U}", policies.IsUnlocked);
                _policyManager.ApplyRemotePolicy(policies.IsUnlocked, policies.AllowedDevices, policies.BlockedDevices);
            });

            // Server requests a timed unlock
            _connection.On<int>("TimedUnlock", minutes =>
            {
                _logger.LogInformation("Timed unlock for {M} minutes requested by server.", minutes);
                _policyManager.Unlock(TimeSpan.FromMinutes(minutes));
            });

            _connection.Reconnected += async _ =>
            {
                _logger.LogInformation("Reconnected to server. Re-registering...");
                await RegisterAsync();
            };

            try
            {
                await _connection.StartAsync(ct);
                await RegisterAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect to server at {Url}. Will keep retrying.", hubUrl);
            }
        }

        private async Task RegisterAsync()
        {
            if (_connection is null || _connection.State != HubConnectionState.Connected) return;
            var cfg = _agentConfig.Config;
            var ip  = GetLocalIp();
            await _connection.InvokeAsync("RegisterAgent", cfg.MachineId, Environment.MachineName, ip);
            _logger.LogInformation("Registered with server as {MachineId} @ {Ip}", cfg.MachineId, ip);
        }

        public async Task PushDeviceStatusAsync(List<UsbDeviceDto> devices)
        {
            if (_connection is null || _connection.State != HubConnectionState.Connected) return;
            try
            {
                await _connection.InvokeAsync("UpdateDeviceList", _agentConfig.Config.MachineId, devices);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to push device status to server.");
            }
        }

        private static string GetLocalIp()
        {
            try
            {
                var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
                return host.AddressList
                    .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    ?.ToString() ?? "unknown";
            }
            catch { return "unknown"; }
        }

        public async ValueTask DisposeAsync()
        {
            if (_connection != null)
                await _connection.DisposeAsync();
        }
    }

    // DTO mirrored from server
    public class UsbDeviceDto
    {
        public string DeviceID            { get; set; } = string.Empty;
        public string Name                { get; set; } = string.Empty;
        public string Status              { get; set; } = string.Empty;
        public bool   IsAllowed           { get; set; }
        public bool   IsHub               { get; set; }
        public string ConnectedDeviceName { get; set; } = string.Empty;
    }
}
