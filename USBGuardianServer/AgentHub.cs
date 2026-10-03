using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

namespace USBGuardianServer
{
    public class AgentHub : Hub
    {
        public static readonly ConcurrentDictionary<string, AgentInfo> ConnectedAgents = new();

        private readonly PolicyStore _policyStore;
        private readonly MachineInventoryStore _inventory;
        private readonly ServerConfigManager _config;
        private readonly EventStore _events;
        private readonly ILogger<AgentHub> _logger;
        private readonly EndpointCredentialStore _credentials;
        private readonly bool _allowLegacyToken;

        public AgentHub(
            PolicyStore policyStore,
            MachineInventoryStore inventory,
            ServerConfigManager config,
            EventStore events,
            ILogger<AgentHub> logger,
            EndpointCredentialStore credentials,
            IConfiguration configuration)
        {
            _policyStore = policyStore;
            _inventory = inventory;
            _config = config;
            _events = events;
            _logger = logger;
            _credentials = credentials;
            _allowLegacyToken = configuration.GetValue<bool>("Security:AllowLegacySharedAgentToken") ||
                configuration.GetValue<bool>("Security:AllowUnauthenticatedAgents");
        }

        public async Task RegisterAgent(
            string machineId,
            string machineName,
            string ipAddress,
            string? agentToken = null)
        {
            // Validate identity without lossy cleaning or truncation. A machine's
            // credential must never authenticate an alias or another identity.
            try { machineId = EndpointCredentialStore.ValidateId(machineId); }
            catch (ArgumentException) { throw new HubException("Invalid machine identity."); }
            bool authenticated = _credentials.IsEnrolled(machineId)
                ? _credentials.Validate(machineId, agentToken)
                : _allowLegacyToken && _config.ValidateAgentToken(agentToken);
            if (!authenticated)
            {
                _logger.LogWarning(
                    "Rejected agent registration from {MachineName} ({IpAddress}): invalid token.",
                    Clean(machineName, 128),
                    Clean(ipAddress, 64));
                Context.Abort();
                throw new HubException("Agent authentication failed.");
            }

            machineId = Required(machineId, "Machine ID", 128);
            machineName = Required(machineName, "Machine name", 128);
            ipAddress = ResolveRemoteIp(ipAddress);

            var policies = _policyStore.GetOrCreate(machineId);
            _inventory.Register(machineId, machineName, ipAddress);

            // One authoritative connection per machine. A stale transport may remain
            // alive briefly, but it is removed from the registry and cannot submit
            // updates because all hub methods verify the current connection ID.
            foreach (var existing in ConnectedAgents)
            {
                if (existing.Key == Context.ConnectionId) continue;
                if (!string.Equals(
                    existing.Value.MachineId,
                    machineId,
                    StringComparison.OrdinalIgnoreCase))
                    continue;

                ConnectedAgents.TryRemove(existing.Key, out _);
                _logger.LogInformation(
                    "Superseded stale connection {ConnectionId} for {MachineId}.",
                    existing.Key, machineId);
            }

            ConnectedAgents[Context.ConnectionId] = new AgentInfo
            {
                MachineId = machineId,
                MachineName = machineName,
                IpAddress = ipAddress,
                ConnectionId = Context.ConnectionId,
                CredentialBinding = EndpointCredentialStore.Fingerprint(agentToken),
                IsLocked = !policies.IsEffectivelyUnlocked(DateTime.UtcNow),
                LastSeen = DateTime.UtcNow
            };

            _logger.LogInformation(
                "Agent registered: {MachineName} ({IpAddress}) as {MachineId}.",
                machineName, ipAddress, machineId);

            await Clients.Caller.SendAsync(
                "ApplyPolicy", policies, Context.ConnectionAborted);
        }

        public async Task UpdateDeviceList(string machineId, List<UsbDeviceDto>? devices)
        {
            var agent = GetRegisteredAgent(machineId);
            machineId = agent.MachineId;
            var incoming = NormalizeDevices(devices);
            var previousIds = agent.Devices
                .Where(device => !string.IsNullOrWhiteSpace(device.DeviceID))
                .Select(device => device.DeviceID)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var device in incoming)
            {
                if (device.IsHub ||
                    !device.IsAllowed ||
                    previousIds.Contains(device.DeviceID))
                    continue;
                _events.Append(new UsbEvent
                {
                    MachineId = machineId,
                    EventType = "Connected",
                    DeviceId = device.DeviceID,
                    DeviceName = device.Name,
                    Detail = device.Status
                });
            }

            agent.Devices = incoming;
            agent.LastSeen = DateTime.UtcNow;
            _inventory.Touch(machineId, incoming.Count);

            var policies = _policyStore.GetOrCreate(machineId, out bool expired);
            bool policyChanged = expired;

            if (expired)
            {
                _events.Append(new UsbEvent
                {
                    MachineId = machineId,
                    EventType = "PolicyExpired",
                    Detail = "timed unlock or learn mode expired"
                });
            }

            if (policies.LearnMode)
            {
                var candidates = incoming
                    .Where(device =>
                        !device.IsHub &&
                        !string.IsNullOrWhiteSpace(device.DeviceID) &&
                        !policies.AllowedDevices.Contains(
                            device.DeviceID, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                var captured = new List<UsbDeviceDto>();

                if (candidates.Count > 0)
                {
                    policies = _policyStore.Update(machineId, current =>
                    {
                        if (!current.LearnMode) return;
                        foreach (var device in candidates)
                        {
                            if (current.AllowedDevices.Contains(
                                device.DeviceID, StringComparer.OrdinalIgnoreCase))
                                continue;
                            current.AllowedDevices.Add(device.DeviceID);
                            captured.Add(device);
                        }
                    });
                }

                foreach (var device in captured)
                {
                    _logger.LogInformation(
                        "Learn mode captured on {MachineId}: {DeviceId}.",
                        machineId, device.DeviceID);
                    _events.Append(new UsbEvent
                    {
                        MachineId = machineId,
                        EventType = "LearnCaptured",
                        DeviceId = device.DeviceID,
                        DeviceName = device.Name
                    });
                }

                policyChanged |= captured.Count > 0;
            }

            if (policyChanged)
            {
                await Clients.Caller.SendAsync(
                    "ApplyPolicy", policies, Context.ConnectionAborted);
            }

            agent.IsLocked = !policies.IsEffectivelyUnlocked(DateTime.UtcNow);
        }

        public Task ReportEvent(
            string machineId,
            string type,
            string deviceId,
            string deviceName,
            string detail) =>
            ReportEventCore(
                machineId,
                Guid.Empty,
                DateTime.UtcNow,
                type,
                deviceId,
                deviceName,
                detail);

        public async Task ReportEnforcement(string machineId, long? revision, DateTime? checkedUtc, string? error)
        {
            var agent = GetRegisteredAgent(machineId);
            var desired = _policyStore.GetOrCreate(agent.MachineId);
            agent.AppliedRevision = revision.HasValue && revision.Value == desired.Revision ? revision : null;
            agent.EnforcementUtc = checkedUtc.HasValue ? NormalizeAgentTimestamp(checkedUtc.Value) : null;
            agent.EnforcementError = Clean(error, 1024);
            if (revision != desired.Revision)
                await Clients.Caller.SendAsync("ApplyPolicy", desired, Context.ConnectionAborted);
        }

        public Task ReportEventV2(
            string machineId,
            Guid eventId,
            DateTime createdUtc,
            string type,
            string deviceId,
            string deviceName,
            string detail) =>
            ReportEventCore(
                machineId,
                eventId,
                createdUtc,
                type,
                deviceId,
                deviceName,
                detail);

        private Task ReportEventCore(
            string machineId,
            Guid eventId,
            DateTime createdUtc,
            string type,
            string deviceId,
            string deviceName,
            string detail)
        {
            var agent = GetRegisteredAgent(machineId);
            agent.LastSeen = DateTime.UtcNow;
            _inventory.Touch(agent.MachineId);

            type = Clean(type, 64);
            bool appended = _events.Append(new UsbEvent
            {
                EventId = eventId,
                Timestamp = NormalizeAgentTimestamp(createdUtc),
                MachineId = agent.MachineId,
                EventType = string.IsNullOrWhiteSpace(type) ? "AgentEvent" : type,
                DeviceId = Clean(deviceId, 512),
                DeviceName = Clean(deviceName, 256),
                Detail = Clean(detail, 1024)
            });

            if (appended)
            {
                _logger.LogWarning(
                    "Security event from {MachineId}: {Type} {Device}.",
                    agent.MachineId, type, deviceName);
            }
            return Task.CompletedTask;
        }

        private static DateTime NormalizeAgentTimestamp(DateTime timestamp)
        {
            DateTime utc = timestamp.Kind switch
            {
                DateTimeKind.Utc => timestamp,
                DateTimeKind.Local => timestamp.ToUniversalTime(),
                _ => DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)
            };
            return utc == default || utc > DateTime.UtcNow.AddMinutes(5)
                ? DateTime.UtcNow
                : utc;
        }

        public override Task OnDisconnectedAsync(Exception? exception)
        {
            ConnectedAgents.TryRemove(Context.ConnectionId, out var agent);
            if (agent != null)
            {
                _inventory.Touch(
                    agent.MachineId,
                    agent.Devices.Count,
                    forcePersist: true);
                _logger.LogInformation(
                    "Agent disconnected: {MachineName} ({MachineId}).",
                    agent.MachineName, agent.MachineId);
            }
            return base.OnDisconnectedAsync(exception);
        }

        private AgentInfo GetRegisteredAgent(string claimedMachineId)
        {
            if (!ConnectedAgents.TryGetValue(Context.ConnectionId, out var agent))
                throw new HubException("Agent is not registered.");
            if (_credentials.IsEnrolled(agent.MachineId) &&
                !_credentials.ValidateBinding(agent.MachineId, agent.CredentialBinding))
            {
                ConnectedAgents.TryRemove(Context.ConnectionId, out _);
                Context.Abort();
                throw new HubException("Endpoint credential was revoked.");
            }

            if (!string.Equals(
                agent.MachineId,
                claimedMachineId?.Trim(),
                StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Connection {ConnectionId} registered as {RegisteredId} claimed {ClaimedId}.",
                    Context.ConnectionId, agent.MachineId, claimedMachineId);
                throw new HubException("Machine identity mismatch.");
            }

            return agent;
        }

        private static List<UsbDeviceDto> NormalizeDevices(IEnumerable<UsbDeviceDto>? devices) =>
            (devices ?? Array.Empty<UsbDeviceDto>())
                .Where(device => device != null && !string.IsNullOrWhiteSpace(device.DeviceID))
                .Take(512)
                .Select(device => new UsbDeviceDto
                {
                    DeviceID = Clean(device.DeviceID, 512),
                    Name = Clean(device.Name, 256),
                    Status = Clean(device.Status, 64),
                    IsAllowed = device.IsAllowed,
                    IsHub = device.IsHub,
                    ConnectedDeviceName = Clean(device.ConnectedDeviceName, 256)
                    ,IsRunning = device.IsRunning,
                    EnforcementError = Clean(device.EnforcementError, 1024)
                })
                .ToList();

        private static string Required(string value, string label, int maxLength)
        {
            value = Clean(value, maxLength);
            if (string.IsNullOrWhiteSpace(value))
                throw new HubException($"{label} is required.");
            return value;
        }

        private string ResolveRemoteIp(string? claimedIp)
        {
            var remoteIp = Context.GetHttpContext()?.Connection.RemoteIpAddress;
            if (remoteIp?.IsIPv4MappedToIPv6 == true)
                remoteIp = remoteIp.MapToIPv4();
            return remoteIp?.ToString() ?? Clean(claimedIp, 64);
        }

        private static string Clean(string? value, int maxLength)
        {
            value = (value ?? string.Empty)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();
            return value.Length <= maxLength ? value : value[..maxLength];
        }
    }
}
