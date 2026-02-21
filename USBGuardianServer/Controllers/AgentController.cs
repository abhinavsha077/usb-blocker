using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace USBGuardianServer.Controllers
{
    [ApiController]
    [Route("api")]
    public class AgentController : ControllerBase
    {
        private readonly IHubContext<AgentHub> _hub;
        private readonly PolicyStore _policyStore;
        private readonly ServerConfigManager _config;

        public AgentController(IHubContext<AgentHub> hub, PolicyStore policyStore, ServerConfigManager config)
        {
            _hub = hub;
            _policyStore = policyStore;
            _config = config;
        }

        // ── Auth helper ──────────────────────────────────────────────────────
        private bool IsAuthorized() =>
            Request.Headers.TryGetValue("X-Admin-Password", out var pw)
            && _config.VerifyPassword(pw!);

        // ── Agent list ───────────────────────────────────────────────────────
        [HttpGet("agents")]
        public IActionResult GetAgents()
        {
            if (!IsAuthorized()) return Unauthorized();
            return Ok(AgentHub.ConnectedAgents.Values.Select(a => new
            {
                a.MachineId, a.MachineName, a.IpAddress, a.IsLocked,
                a.LastSeen, DeviceCount = a.Devices.Count,
                Online = (DateTime.UtcNow - a.LastSeen).TotalSeconds < 30
            }));
        }

        [HttpGet("agents/{machineId}/devices")]
        public IActionResult GetDevices(string machineId)
        {
            if (!IsAuthorized()) return Unauthorized();
            var agent = AgentHub.ConnectedAgents.Values.FirstOrDefault(a => a.MachineId == machineId);
            if (agent == null) return NotFound("Agent not connected.");
            return Ok(agent.Devices);
        }

        // ── Lock / Unlock ────────────────────────────────────────────────────
        [HttpPost("agents/{machineId}/lock")]
        public async Task<IActionResult> Lock(string machineId)
        {
            if (!IsAuthorized()) return Unauthorized();
            var agent = GetAgent(machineId);
            if (agent == null) return NotFound();

            var policies = _policyStore.GetOrCreate(machineId);
            policies.IsUnlocked = false;
            _policyStore.Save(machineId, policies);

            await _hub.Clients.Client(agent.ConnectionId).SendAsync("ApplyPolicy", policies);
            return Ok("Locked.");
        }

        [HttpPost("agents/{machineId}/unlock")]
        public async Task<IActionResult> Unlock(string machineId, [FromQuery] int? minutes = null)
        {
            if (!IsAuthorized()) return Unauthorized();
            var agent = GetAgent(machineId);
            if (agent == null) return NotFound();

            var policies = _policyStore.GetOrCreate(machineId);
            policies.IsUnlocked = true;
            policies.AllowedDevices.Clear();
            policies.BlockedDevices.Clear();
            _policyStore.Save(machineId, policies);

            await _hub.Clients.Client(agent.ConnectionId).SendAsync("ApplyPolicy", policies);
            if (minutes.HasValue)
                await _hub.Clients.Client(agent.ConnectionId).SendAsync("TimedUnlock", minutes.Value);
            return Ok("Unlocked.");
        }

        // ── Granular device control ──────────────────────────────────────────
        [HttpPost("agents/{machineId}/allow/{deviceId}")]
        public async Task<IActionResult> Allow(string machineId, string deviceId)
        {
            if (!IsAuthorized()) return Unauthorized();
            var agent = GetAgent(machineId);
            if (agent == null) return NotFound();

            var policies = _policyStore.GetOrCreate(machineId);
            var id = Uri.UnescapeDataString(deviceId);
            policies.BlockedDevices.Remove(id);
            if (!policies.AllowedDevices.Contains(id)) policies.AllowedDevices.Add(id);
            _policyStore.Save(machineId, policies);

            await _hub.Clients.Client(agent.ConnectionId).SendAsync("ApplyPolicy", policies);
            return Ok("Allowed.");
        }

        [HttpPost("agents/{machineId}/block/{deviceId}")]
        public async Task<IActionResult> Block(string machineId, string deviceId)
        {
            if (!IsAuthorized()) return Unauthorized();
            var agent = GetAgent(machineId);
            if (agent == null) return NotFound();

            var policies = _policyStore.GetOrCreate(machineId);
            var id = Uri.UnescapeDataString(deviceId);
            policies.AllowedDevices.Remove(id);
            if (!policies.BlockedDevices.Contains(id)) policies.BlockedDevices.Add(id);
            _policyStore.Save(machineId, policies);

            await _hub.Clients.Client(agent.ConnectionId).SendAsync("ApplyPolicy", policies);
            return Ok("Blocked.");
        }

        // ── Global actions ───────────────────────────────────────────────────
        [HttpPost("agents/lockall")]
        public async Task<IActionResult> LockAll()
        {
            if (!IsAuthorized()) return Unauthorized();
            foreach (var agent in AgentHub.ConnectedAgents.Values)
            {
                var policies = _policyStore.GetOrCreate(agent.MachineId);
                policies.IsUnlocked = false;
                _policyStore.Save(agent.MachineId, policies);
                await _hub.Clients.Client(agent.ConnectionId).SendAsync("ApplyPolicy", policies);
            }
            return Ok("All agents locked.");
        }

        [HttpPost("agents/unlockall")]
        public async Task<IActionResult> UnlockAll()
        {
            if (!IsAuthorized()) return Unauthorized();
            foreach (var agent in AgentHub.ConnectedAgents.Values)
            {
                var policies = _policyStore.GetOrCreate(agent.MachineId);
                policies.IsUnlocked = true;
                policies.AllowedDevices.Clear();
                policies.BlockedDevices.Clear();
                _policyStore.Save(agent.MachineId, policies);
                await _hub.Clients.Client(agent.ConnectionId).SendAsync("ApplyPolicy", policies);
            }
            return Ok("All agents unlocked.");
        }

        // ── Password ────────────────────────────────────────────────────────
        [HttpPost("auth/changepassword")]
        public IActionResult ChangePassword([FromBody] ChangePasswordRequest req)
        {
            if (!_config.VerifyPassword(req.CurrentPassword)) return Unauthorized("Wrong current password.");
            _config.ChangePassword(req.NewPassword);
            return Ok("Password changed.");
        }

        private AgentInfo? GetAgent(string machineId) =>
            AgentHub.ConnectedAgents.Values.FirstOrDefault(a => a.MachineId == machineId);
    }

    public class ChangePasswordRequest
    {
        public string CurrentPassword { get; set; } = string.Empty;
        public string NewPassword     { get; set; } = string.Empty;
    }
}
