using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

namespace USBGuardianServer
{
    /// <summary>
    /// SignalR Hub that manages persistent WebSocket connections from all USB Guardian Agents.
    /// </summary>
    public class AgentHub : Hub
    {
        public static readonly ConcurrentDictionary<string, AgentInfo> ConnectedAgents = new();
        private readonly PolicyStore _policyStore;
        private readonly ILogger<AgentHub> _logger;

        public AgentHub(PolicyStore policyStore, ILogger<AgentHub> logger)
        {
            _policyStore = policyStore;
            _logger = logger;
        }

        /// <summary>Called by agent on connect to register itself.</summary>
        public async Task RegisterAgent(string machineId, string machineName, string ipAddress)
        {
            var policies = _policyStore.GetOrCreate(machineId);
            ConnectedAgents[Context.ConnectionId] = new AgentInfo
            {
                MachineId    = machineId,
                MachineName  = machineName,
                IpAddress    = ipAddress,
                ConnectionId = Context.ConnectionId,
                IsLocked     = !policies.IsUnlocked,
                LastSeen     = DateTime.UtcNow
            };

            _logger.LogInformation("Agent registered: {MachineName} ({IpAddress})", machineName, ipAddress);

            // Send current stored policy back to the newly connected agent
            await Clients.Caller.SendAsync("ApplyPolicy", policies);
        }

        /// <summary>Called by agent every 5s to push live device status.</summary>
        public Task UpdateDeviceList(string machineId, List<UsbDeviceDto> devices)
        {
            if (ConnectedAgents.TryGetValue(Context.ConnectionId, out var agent))
            {
                agent.Devices  = devices;
                agent.LastSeen = DateTime.UtcNow;
                // Reflect the locked state from the device policy
                var policies = _policyStore.GetOrCreate(machineId);
                agent.IsLocked = !policies.IsUnlocked;
            }
            return Task.CompletedTask;
        }

        public override Task OnDisconnectedAsync(Exception? exception)
        {
            ConnectedAgents.TryRemove(Context.ConnectionId, out var agent);
            if (agent != null)
                _logger.LogInformation("Agent disconnected: {MachineName}", agent.MachineName);
            return base.OnDisconnectedAsync(exception);
        }
    }
}
