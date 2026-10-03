using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace USBGuardianServer.Controllers
{
    [ApiController]
    [Route("api")]
    public class AgentController : ControllerBase
    {
        private const int MaxUnlockMinutes = 7 * 24 * 60;
        private const int MaxLearnMinutes = 48 * 60;

        private readonly IHubContext<AgentHub> _hub;
        private readonly PolicyStore _policyStore;
        private readonly MachineInventoryStore _inventory;
        private readonly ServerConfigManager _config;
        private readonly EventStore _events;
        private readonly ILogger<AgentController> _logger;
        private readonly EndpointCredentialStore _credentials;
        private readonly IConfiguration _configuration;

        public AgentController(
            IHubContext<AgentHub> hub,
            PolicyStore policyStore,
            MachineInventoryStore inventory,
            ServerConfigManager config,
            EventStore events,
            ILogger<AgentController> logger,
            EndpointCredentialStore credentials,
            IConfiguration configuration)
        {
            _hub = hub;
            _policyStore = policyStore;
            _inventory = inventory;
            _config = config;
            _events = events;
            _logger = logger;
            _credentials = credentials;
            _configuration = configuration;
        }

        private bool IsAuthorized()
        {
            if (!Request.Headers.TryGetValue("X-Admin-Password", out var password) ||
                !_config.VerifyPassword(password!))
                return false;
            if (_config.IsDefaultPassword && !IsLoopback())
                return false;
            return true;
        }

        private bool IsLoopback()
        {
            var ip = HttpContext.Connection.RemoteIpAddress;
            return ip != null && System.Net.IPAddress.IsLoopback(ip);
        }

        [HttpGet("status")]
        public IActionResult Status()
        {
            if (!IsAuthorized()) return Unauthorized();
            var knownMachineIds = _policyStore.Snapshot().Keys
                .Concat(_inventory.Snapshot().Keys)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return Ok(new
            {
                passwordIsDefault = _config.IsDefaultPassword,
                agentAuthEnabled = _config.AgentAuthEnabled ||
                    !_configuration.GetValue<bool>("Security:AllowUnauthenticatedAgents"),
                agentCount = AgentHub.ConnectedAgents.Values
                    .Where(IsOnline)
                    .Select(agent => agent.MachineId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count(),
                knownMachineCount = knownMachineIds.Count
            });
        }

        [HttpGet("agents")]
        public IActionResult GetAgents()
        {
            if (!IsAuthorized()) return Unauthorized();

            var policies = _policyStore.Snapshot();
            var inventory = _inventory.Snapshot();
            var onlineAgents = AgentHub.ConnectedAgents.Values
                .Where(IsOnline)
                .GroupBy(agent => agent.MachineId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderByDescending(agent => agent.LastSeen).First(),
                    StringComparer.OrdinalIgnoreCase);
            var machineIds = policies.Keys
                .Concat(inventory.Keys)
                .Concat(onlineAgents.Keys)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var result = machineIds.Select(machineId =>
                {
                    policies.TryGetValue(machineId, out var policy);
                    policy ??= new MachinePolicies();
                    inventory.TryGetValue(machineId, out var known);
                    onlineAgents.TryGetValue(machineId, out var online);

                    bool isLocked =
                        !policy.IsEffectivelyUnlocked(DateTime.UtcNow);
                    if (online != null) online.IsLocked = isLocked;

                    return new AgentSummaryDto
                    {
                        MachineId = machineId,
                        MachineName = online?.MachineName
                            ?? known?.MachineName
                            ?? machineId,
                        IpAddress = online?.IpAddress
                            ?? known?.IpAddress
                            ?? string.Empty,
                        IsLocked = isLocked,
                        LastSeen = online?.LastSeen
                            ?? (known?.LastSeenUtc == default
                                ? null
                                : known?.LastSeenUtc),
                        DeviceCount = online?.Devices.Count
                            ?? known?.LastDeviceCount
                            ?? 0,
                        LearnMode = policy.LearnMode,
                        LearnModeExpiry = policy.LearnModeExpiry,
                        UnlockExpiry = policy.UnlockExpiry,
                        Group = policy.Group,
                        OpticalBlocked = policy.OpticalBlocked,
                        BluetoothBlocked = policy.BluetoothBlocked,
                        WifiBlocked = policy.WifiBlocked,
                        BadUsbBlock = policy.BadUsbBlock,
                        Online = online != null,
                        PolicyRevision = policy.Revision,
                        AppliedRevision = online?.AppliedRevision,
                        EnforcementUtc = online?.EnforcementUtc,
                        EnforcementError = online?.EnforcementError ?? string.Empty
                    };
                })
                .OrderBy(agent => agent.MachineName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return Ok(result);
        }

        [HttpPost("agents/{machineId}/credential")]
        public IActionResult IssueCredential(string machineId)
        {
            if (!IsAuthorized()) return Unauthorized();
            try { EndpointCredentialStore.ValidateId(machineId); }
            catch (ArgumentException ex) { return BadRequest(ex.Message); }
            _policyStore.GetOrCreate(machineId);
            // Audit availability is a precondition: once a token rotates, its
            // only plaintext copy must be returned rather than lost to a later
            // fallible append. This event records intent, not successful issue.
            AppendAdminEvent(machineId, "EndpointCredentialIssuanceRequested");
            string token = _credentials.Issue(machineId);
            // Reset revokes active sessions as well as future registrations.
            foreach (var pair in AgentHub.ConnectedAgents)
                if (string.Equals(pair.Value.MachineId, machineId, StringComparison.OrdinalIgnoreCase))
                    AgentHub.ConnectedAgents.TryRemove(pair.Key, out _);
            Response.Headers.CacheControl = "no-store";
            return Ok(new { machineId, agentToken = token });
        }

        [HttpGet("agents/{machineId}/devices")]
        public IActionResult GetDevices(string machineId)
        {
            if (!IsAuthorized()) return Unauthorized();
            var agent = GetAgent(machineId);
            return agent == null ? NotFound("Agent not connected.") : Ok(agent.Devices);
        }

        [HttpGet("agents/{machineId}/history")]
        public IActionResult GetHistory(string machineId, [FromQuery] int limit = 200)
        {
            if (!IsAuthorized()) return Unauthorized();
            return Ok(_events.GetRecent(machineId, Math.Clamp(limit, 1, 2000)));
        }

        [HttpPost("agents/{machineId}/lock")]
        public async Task<IActionResult> Lock(string machineId)
        {
            if (!IsAuthorized()) return Unauthorized();

            var policy = _policyStore.Update(machineId, current =>
            {
                current.IsUnlocked = false;
                current.UnlockExpiry = null;
                current.LearnMode = false;
                current.LearnModeExpiry = null;
            });
            await PushPolicyAsync(machineId, policy);
            AppendAdminEvent(machineId, "Locked");
            return Ok("Locked.");
        }

        [HttpPost("agents/{machineId}/unlock")]
        public async Task<IActionResult> Unlock(
            string machineId,
            [FromQuery] int? minutes = null)
        {
            if (!IsAuthorized()) return Unauthorized();
            if (minutes is <= 0 or > MaxUnlockMinutes)
                return BadRequest($"Minutes must be between 1 and {MaxUnlockMinutes}.");

            DateTime? expiry = minutes.HasValue
                ? DateTime.UtcNow.AddMinutes(minutes.Value)
                : null;
            var policy = _policyStore.Update(machineId, current =>
            {
                current.IsUnlocked = true;
                current.UnlockExpiry = expiry;
            });
            await PushPolicyAsync(machineId, policy);
            _events.Append(new UsbEvent
            {
                MachineId = machineId,
                EventType = "Unlocked",
                Detail = minutes.HasValue ? $"{minutes.Value} min" : "indefinite"
            });
            return Ok(minutes.HasValue
                ? $"Unlocked for {minutes.Value} minute(s)."
                : "Unlocked.");
        }

        [HttpPost("agents/{machineId}/allow/{deviceId}")]
        public async Task<IActionResult> Allow(string machineId, string deviceId)
        {
            if (!IsAuthorized()) return Unauthorized();
            if (!TryDecodeDeviceId(deviceId, out var id, out var error)) return error;

            var policy = _policyStore.Update(machineId, current =>
            {
                Remove(current.BlockedDevices, id);
                if (!current.AllowedDevices.Contains(id, StringComparer.OrdinalIgnoreCase))
                    current.AllowedDevices.Add(id);
            });
            await PushPolicyAsync(machineId, policy);
            _events.Append(new UsbEvent
            {
                MachineId = machineId,
                EventType = "Authorized",
                DeviceId = id,
                Detail = "by admin"
            });
            return Ok("Allowed.");
        }

        [HttpPost("agents/{machineId}/block/{deviceId}")]
        public async Task<IActionResult> Block(string machineId, string deviceId)
        {
            if (!IsAuthorized()) return Unauthorized();
            if (!TryDecodeDeviceId(deviceId, out var id, out var error)) return error;

            var policy = _policyStore.Update(machineId, current =>
            {
                Remove(current.AllowedDevices, id);
                if (!current.BlockedDevices.Contains(id, StringComparer.OrdinalIgnoreCase))
                    current.BlockedDevices.Add(id);
            });
            await PushPolicyAsync(machineId, policy);
            _events.Append(new UsbEvent
            {
                MachineId = machineId,
                EventType = "BlockedByAdmin",
                DeviceId = id,
                Detail = "by admin"
            });
            return Ok("Blocked.");
        }

        [HttpPost("agents/{machineId}/learnmode")]
        public async Task<IActionResult> LearnMode(
            string machineId,
            [FromQuery] bool on,
            [FromQuery] int minutes = 480)
        {
            if (!IsAuthorized()) return Unauthorized();
            if (on && (minutes <= 0 || minutes > MaxLearnMinutes))
                return BadRequest($"Minutes must be between 1 and {MaxLearnMinutes}.");

            var policy = _policyStore.Update(machineId, current =>
            {
                current.LearnMode = on;
                current.LearnModeExpiry = on
                    ? DateTime.UtcNow.AddMinutes(minutes)
                    : null;
            });
            await PushPolicyAsync(machineId, policy);
            _events.Append(new UsbEvent
            {
                MachineId = machineId,
                EventType = on ? "LearnOn" : "LearnOff",
                Detail = on ? $"{minutes} min" : "by admin"
            });
            return Ok(on
                ? $"Learn mode enabled for {minutes} minute(s)."
                : "Learn mode disabled.");
        }

        [HttpPost("agents/{machineId}/sector")]
        public async Task<IActionResult> SetSector(
            string machineId,
            [FromQuery] string name,
            [FromQuery] bool block)
        {
            if (!IsAuthorized()) return Unauthorized();

            string sector = (name ?? string.Empty).Trim().ToLowerInvariant();
            if (sector is not ("optical" or "bluetooth" or "wifi"))
                return BadRequest("Unknown sector. Use optical, bluetooth, or wifi.");

            var policy = _policyStore.Update(machineId, current =>
            {
                switch (sector)
                {
                    case "optical":
                        current.OpticalBlocked = block;
                        break;
                    case "bluetooth":
                        current.BluetoothBlocked = block;
                        break;
                    case "wifi":
                        current.WifiBlocked = block;
                        break;
                }
            });
            await PushPolicyAsync(machineId, policy);
            _events.Append(new UsbEvent
            {
                MachineId = machineId,
                EventType = block ? "SectorBlocked" : "SectorAllowed",
                Detail = sector
            });
            return Ok($"Sector '{sector}' {(block ? "blocked" : "allowed")}.");
        }

        [HttpPost("agents/{machineId}/badusb")]
        public async Task<IActionResult> SetBadUsb(
            string machineId,
            [FromQuery] bool block)
        {
            if (!IsAuthorized()) return Unauthorized();

            var policy = _policyStore.Update(machineId,
                current => current.BadUsbBlock = block);
            await PushPolicyAsync(machineId, policy);
            _events.Append(new UsbEvent
            {
                MachineId = machineId,
                EventType = "BadUsbProtect",
                Detail = block ? "auto-block on" : "detect-only"
            });
            return Ok($"BadUSB auto-block {(block ? "enabled" : "disabled")}.");
        }

        [HttpPost("agents/{machineId}/reboot")]
        public Task<IActionResult> Reboot(string machineId) =>
            SendRemoteAsync(machineId, "reboot");

        [HttpPost("agents/{machineId}/shutdown")]
        public Task<IActionResult> Shutdown(string machineId) =>
            SendRemoteAsync(machineId, "shutdown");

        [HttpPost("agents/{machineId}/reload")]
        public Task<IActionResult> Reload(string machineId) =>
            SendRemoteAsync(machineId, "reload");

        [HttpPost("agents/{machineId}/group")]
        public IActionResult SetGroup(string machineId, [FromQuery] string name)
        {
            if (!IsAuthorized()) return Unauthorized();
            name = (name ?? string.Empty).Trim();
            if (name.Length is 0 or > 100)
                return BadRequest("Group name must contain 1 to 100 characters.");

            var policy = _policyStore.Update(machineId, current => current.Group = name);
            _events.Append(new UsbEvent
            {
                MachineId = machineId,
                EventType = "GroupSet",
                Detail = policy.Group
            });
            return Ok($"Assigned {machineId} to group '{policy.Group}'.");
        }

        [HttpGet("groups")]
        public IActionResult GetGroups()
        {
            if (!IsAuthorized()) return Unauthorized();

            var connectedMachines = AgentHub.ConnectedAgents.Values
                .Where(IsOnline)
                .Select(agent => agent.MachineId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var groups = _policyStore.Snapshot()
                .GroupBy(pair => pair.Value.Group, StringComparer.OrdinalIgnoreCase)
                .Select(group => new
                {
                    Name = group.Key,
                    Total = group.Count(),
                    Online = group.Count(pair => connectedMachines.Contains(pair.Key)),
                    Locked = group.Count(pair =>
                        !pair.Value.IsEffectivelyUnlocked(DateTime.UtcNow))
                })
                .OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase);
            return Ok(groups);
        }

        [HttpPost("groups/{name}/lock")]
        public Task<IActionResult> GroupLock(string name) =>
            ApplyToGroupAsync(name, "lock");

        [HttpPost("groups/{name}/unlock")]
        public Task<IActionResult> GroupUnlock(string name) =>
            ApplyToGroupAsync(name, "unlock");

        [HttpPost("groups/{name}/learnmode")]
        public Task<IActionResult> GroupLearn(
            string name,
            [FromQuery] bool on,
            [FromQuery] int minutes = 480) =>
            ApplyToGroupAsync(name, on ? "learnon" : "learnoff", minutes);

        [HttpPost("agents/lockall")]
        public Task<IActionResult> LockAll() => ApplyToAllAsync(unlock: false);

        [HttpPost("agents/unlockall")]
        public Task<IActionResult> UnlockAll() => ApplyToAllAsync(unlock: true);

        [HttpPost("auth/changepassword")]
        public IActionResult ChangePassword([FromBody] ChangePasswordRequest request)
        {
            if (request == null)
                return BadRequest("Password request is required.");
            if (_config.IsDefaultPassword && !IsLoopback())
                return Unauthorized(
                    "The initial password must be changed from the server itself.");
            if (string.IsNullOrWhiteSpace(request.NewPassword) ||
                request.NewPassword.Length is < 8 or > 128)
                return BadRequest("New password must contain 8 to 128 characters.");
            if (string.Equals(request.NewPassword, "admin", StringComparison.OrdinalIgnoreCase))
                return BadRequest("Choose a password other than the shipped default.");

            if (!_config.TryChangePassword(
                    request.CurrentPassword,
                    request.NewPassword))
                return Unauthorized("Wrong current password or password update failed.");
            return Ok("Password changed.");
        }

        private async Task<IActionResult> SendRemoteAsync(string machineId, string command)
        {
            if (!IsAuthorized()) return Unauthorized();
            var agent = GetAgent(machineId);
            if (agent == null) return NotFound("Agent not connected.");

            try
            {
                await _hub.Clients.Client(agent.ConnectionId)
                    .SendAsync(
                        "RemoteCommand",
                        command,
                        HttpContext.RequestAborted);
            }
            catch (Exception ex)
            {
                AgentHub.ConnectedAgents.TryRemove(
                    agent.ConnectionId,
                    out _);
                _logger.LogWarning(
                    ex,
                    "Remote command {Command} could not reach {MachineId}.",
                    command,
                    machineId);
                return StatusCode(
                    StatusCodes.Status503ServiceUnavailable,
                    "Agent connection was lost before the command could be sent.");
            }
            _events.Append(new UsbEvent
            {
                MachineId = agent.MachineId,
                EventType = $"Remote:{command}",
                Detail = "by admin"
            });
            return Ok($"'{command}' sent to {agent.MachineId}.");
        }

        private async Task<IActionResult> ApplyToGroupAsync(
            string name,
            string action,
            int minutes = 480)
        {
            if (!IsAuthorized()) return Unauthorized();
            name = (name ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name)) return BadRequest("Group name is required.");
            if (action == "learnon" && (minutes <= 0 || minutes > MaxLearnMinutes))
                return BadRequest($"Minutes must be between 1 and {MaxLearnMinutes}.");

            var members = _policyStore.Snapshot()
                .Where(pair => string.Equals(
                    pair.Value.Group, name, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key)
                .ToList();
            if (members.Count == 0) return NotFound($"Group '{name}' has no machines.");

            foreach (var machineId in members)
            {
                var policy = _policyStore.Update(machineId, current =>
                {
                    switch (action)
                    {
                        case "lock":
                            current.IsUnlocked = false;
                            current.UnlockExpiry = null;
                            current.LearnMode = false;
                            current.LearnModeExpiry = null;
                            break;
                        case "unlock":
                            current.IsUnlocked = true;
                            current.UnlockExpiry = null;
                            break;
                        case "learnon":
                            current.LearnMode = true;
                            current.LearnModeExpiry = DateTime.UtcNow.AddMinutes(minutes);
                            break;
                        case "learnoff":
                            current.LearnMode = false;
                            current.LearnModeExpiry = null;
                            break;
                    }
                });
                await PushPolicyAsync(machineId, policy);
                _events.Append(new UsbEvent
                {
                    MachineId = machineId,
                    EventType = $"Group:{action}",
                    Detail = $"group '{name}'"
                });
            }

            return Ok($"Applied '{action}' to {members.Count} machine(s) in group '{name}'.");
        }

        private async Task<IActionResult> ApplyToAllAsync(bool unlock)
        {
            if (!IsAuthorized()) return Unauthorized();
            var machineIds = _policyStore.Snapshot().Keys
                .Concat(_inventory.Snapshot().Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var machineId in machineIds)
            {
                var policy = _policyStore.Update(machineId, current =>
                {
                    current.IsUnlocked = unlock;
                    current.UnlockExpiry = null;
                    if (!unlock)
                    {
                        current.LearnMode = false;
                        current.LearnModeExpiry = null;
                    }
                });
                await PushPolicyAsync(machineId, policy);
                _events.Append(new UsbEvent
                {
                    MachineId = machineId,
                    EventType = unlock ? "Unlocked" : "Locked",
                    Detail = unlock ? "unlock-all" : "lock-all"
                });
            }

            return Ok($"{(unlock ? "Unlocked" : "Locked")} {machineIds.Count} known machine(s).");
        }

        private async Task<bool> PushPolicyAsync(
            string machineId,
            MachinePolicies policy)
        {
            var agent = GetAgent(machineId);
            if (agent == null) return false;

            agent.IsLocked = !policy.IsEffectivelyUnlocked(DateTime.UtcNow);
            try
            {
                await _hub.Clients.Client(agent.ConnectionId)
                    .SendAsync(
                        "ApplyPolicy",
                        policy,
                        HttpContext.RequestAborted);
                return true;
            }
            catch (Exception ex)
            {
                AgentHub.ConnectedAgents.TryRemove(
                    agent.ConnectionId,
                    out _);
                _logger.LogWarning(
                    ex,
                    "Policy for {MachineId} was persisted but live delivery failed. " +
                    "The Agent will receive it after reconnecting.",
                    machineId);
                return false;
            }
        }

        private void AppendAdminEvent(string machineId, string eventType) =>
            _events.Append(new UsbEvent
            {
                MachineId = machineId,
                EventType = eventType,
                Detail = "by admin"
            });

        private AgentInfo? GetAgent(string machineId) =>
            AgentHub.ConnectedAgents.Values.FirstOrDefault(agent =>
                IsOnline(agent) &&
                (!_credentials.IsEnrolled(agent.MachineId) ||
                 _credentials.ValidateBinding(agent.MachineId, agent.CredentialBinding)) &&
                string.Equals(
                    agent.MachineId,
                    machineId,
                    StringComparison.OrdinalIgnoreCase));

        private static bool IsOnline(AgentInfo agent) =>
            DateTime.UtcNow - agent.LastSeen < TimeSpan.FromSeconds(30);

        private static void Remove(List<string> entries, string value) =>
            entries.RemoveAll(entry =>
                string.Equals(entry, value, StringComparison.OrdinalIgnoreCase));

        private bool TryDecodeDeviceId(
            string encoded,
            out string deviceId,
            out IActionResult error)
        {
            try
            {
                deviceId = Uri.UnescapeDataString(encoded ?? string.Empty).Trim();
            }
            catch (UriFormatException)
            {
                deviceId = string.Empty;
            }

            if (deviceId.Length is 0 or > 512)
            {
                error = BadRequest("Device ID must contain 1 to 512 characters.");
                return false;
            }

            error = null!;
            return true;
        }
    }

    public class ChangePasswordRequest
    {
        public string CurrentPassword { get; set; } = string.Empty;
        public string NewPassword { get; set; } = string.Empty;
    }

    public sealed class AgentSummaryDto
    {
        public string MachineId { get; set; } = string.Empty;
        public string MachineName { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public bool IsLocked { get; set; }
        public DateTime? LastSeen { get; set; }
        public int DeviceCount { get; set; }
        public bool LearnMode { get; set; }
        public DateTime? LearnModeExpiry { get; set; }
        public DateTime? UnlockExpiry { get; set; }
        public string Group { get; set; } = "Default";
        public bool OpticalBlocked { get; set; }
        public bool BluetoothBlocked { get; set; }
        public bool WifiBlocked { get; set; }
        public bool BadUsbBlock { get; set; }
        public bool Online { get; set; }
        public long PolicyRevision { get; set; }
        public long? AppliedRevision { get; set; }
        public DateTime? EnforcementUtc { get; set; }
        public string EnforcementError { get; set; } = string.Empty;
    }
}
