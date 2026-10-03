using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace USBGuardianAgent
{
    public class MachinePolicies
    {
        public long Revision { get; set; }
        public List<string> AllowedDevices { get; set; } = new();
        public List<string> BlockedDevices { get; set; } = new();
        public bool IsUnlocked { get; set; }
        public DateTime? UnlockExpiry { get; set; }
        public bool LearnMode { get; set; }
        public DateTime? LearnModeExpiry { get; set; }
        public bool OpticalBlocked { get; set; }
        public bool BluetoothBlocked { get; set; }
        public bool WifiBlocked { get; set; }
        public bool BadUsbBlock { get; set; }
    }

    public class AgentSignalRClient : IAsyncDisposable
    {
        private readonly AgentConfigManager _agentConfig;
        private readonly UsbPolicyManager _policyManager;
        private readonly ILogger<AgentSignalRClient> _logger;
        private readonly SemaphoreSlim _connectionGate = new(1, 1);
        private HubConnection? _connection;
        private volatile bool _registered;
        private volatile bool _useLegacyEventReporting;
        private DateTime _nextConnectionWarningUtc;

        public AgentSignalRClient(
            AgentConfigManager agentConfig,
            UsbPolicyManager policyManager,
            ILogger<AgentSignalRClient> logger)
        {
            _agentConfig = agentConfig;
            _policyManager = policyManager;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (_connection != null) return;

            string hubUrl = _agentConfig.Config.ServerUrl.TrimEnd('/') + "/agenthub";
            _connection = new HubConnectionBuilder()
                .WithUrl(hubUrl)
                .WithAutomaticReconnect(new[]
                {
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(10),
                    TimeSpan.FromSeconds(30)
                })
                .Build();

            _connection.On<MachinePolicies>("ApplyPolicy", policy =>
            {
                _logger.LogInformation(
                    "Received policy. Unlocked={Unlocked}, unlockExpiry={UnlockExpiry}, " +
                    "learn={Learn}, learnExpiry={LearnExpiry}.",
                    policy.IsUnlocked, policy.UnlockExpiry, policy.LearnMode,
                    policy.LearnModeExpiry);
                try { _policyManager.ApplyRemotePolicy(policy); }
                catch (Exception ex)
                {
                    _policyManager.RecordPolicyError("Policy cache could not be persisted; requested policy was not applied.");
                    _logger.LogError(ex, "Policy update failed; retaining the previous durable policy.");
                }
            });

            // Retained for compatibility with older servers. New servers persist
            // UnlockExpiry in the policy and no longer rely on this transient message.
            _connection.On<int>("TimedUnlock", minutes =>
            {
                if (minutes <= 0) return;
                _logger.LogInformation(
                    "Received legacy timed unlock for {Minutes} minutes.", minutes);
                _policyManager.Unlock(TimeSpan.FromMinutes(minutes));
            });

            _connection.On<string>("RemoteCommand", async command =>
            {
                _logger.LogWarning("Remote command received: {Command}", command);
                switch (command)
                {
                    case "reboot":
                        RunShutdown("/r");
                        break;
                    case "shutdown":
                        RunShutdown("/s");
                        break;
                    case "reload":
                        _policyManager.Enforce();
                        await RegisterAfterReconnectAsync();
                        break;
                    default:
                        _logger.LogWarning("Unknown remote command: {Command}", command);
                        break;
                }
            });

            _connection.Reconnecting += error =>
            {
                _registered = false;
                _policyManager.SetControlConnected(false);
                _logger.LogWarning(error, "Connection to the control server was interrupted.");
                return Task.CompletedTask;
            };

            _connection.Reconnected += _ => RegisterAfterReconnectAsync();

            _connection.Closed += error =>
            {
                _registered = false;
                _policyManager.SetControlConnected(false);
                _logger.LogWarning(error,
                    "Connection to the control server closed. The worker will retry.");
                return Task.CompletedTask;
            };

            await EnsureConnectedAsync(cancellationToken);
        }

        public async Task<bool> EnsureConnectedAsync(CancellationToken cancellationToken)
        {
            if (_connection == null) return false;
            if (_connection.State == HubConnectionState.Connected && _registered) return true;
            if (_connection.State == HubConnectionState.Reconnecting) return false;

            await _connectionGate.WaitAsync(cancellationToken);
            try
            {
                if (_connection.State == HubConnectionState.Disconnected)
                    await _connection.StartAsync(cancellationToken);

                if (_connection.State == HubConnectionState.Connected && !_registered)
                    await RegisterAsync(cancellationToken);

                return _connection.State == HubConnectionState.Connected && _registered;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _registered = false;
                _policyManager.SetControlConnected(false);
                if (DateTime.UtcNow >= _nextConnectionWarningUtc)
                {
                    _nextConnectionWarningUtc = DateTime.UtcNow.AddMinutes(1);
                    _logger.LogWarning(ex,
                        "Could not connect or register with {Url}. Retrying in the worker loop.",
                        _agentConfig.Config.ServerUrl);
                }
                return false;
            }
            finally
            {
                _connectionGate.Release();
            }
        }

        public async Task<bool> PushDeviceStatusAsync(
            List<UsbDeviceDto> devices,
            CancellationToken cancellationToken)
        {
            if (_connection == null || !_registered ||
                _connection.State != HubConnectionState.Connected)
                return false;

            try
            {
                await _connection.InvokeAsync(
                    "UpdateDeviceList",
                    _agentConfig.Config.MachineId,
                    devices,
                    cancellationToken);
                return true;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _registered = false;
                _policyManager.SetControlConnected(false);
                _logger.LogWarning(ex, "Failed to push device status to server.");
                return false;
            }
        }

        public async Task ReportEnforcementAsync(CancellationToken cancellationToken)
        {
            if (_connection == null || !_registered) return;
            var status = _policyManager.GetEnforcementStatus();
            try
            {
                await _connection.InvokeAsync("ReportEnforcement", _agentConfig.Config.MachineId,
                    status.Revision, status.CheckedUtc, status.Error, cancellationToken);
            }
            catch (HubException ex) when (ex.Message.Contains("Unknown hub method", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("Method does not exist", StringComparison.OrdinalIgnoreCase))
            {
                // Older servers have no applied-state protocol. Their UI cannot
                // claim verified enforcement; preserve device reporting compatibility.
                _logger.LogDebug("Server does not support enforcement receipts.");
            }
        }

        public async Task<bool> ReportEventAsync(
            Guid eventId,
            DateTime createdUtc,
            string type,
            string deviceId,
            string deviceName,
            string detail,
            CancellationToken cancellationToken)
        {
            if (_connection == null || !_registered ||
                _connection.State != HubConnectionState.Connected)
                return false;

            try
            {
                if (!_useLegacyEventReporting)
                {
                    try
                    {
                        await _connection.InvokeAsync(
                            "ReportEventV2",
                            _agentConfig.Config.MachineId,
                            eventId,
                            createdUtc,
                            type,
                            deviceId,
                            deviceName,
                            detail,
                            cancellationToken);
                        return true;
                    }
                    catch (HubException ex) when (
                        ex.Message.Contains("Unknown hub method", StringComparison.OrdinalIgnoreCase) ||
                        ex.Message.Contains("Method does not exist", StringComparison.OrdinalIgnoreCase))
                    {
                        _useLegacyEventReporting = true;
                        _logger.LogWarning(
                            ex,
                            "Server does not support idempotent event reporting; " +
                            "using the legacy event method until the Agent restarts.");
                    }
                }

                await _connection.InvokeAsync(
                    "ReportEvent",
                    _agentConfig.Config.MachineId,
                    type,
                    deviceId,
                    deviceName,
                    detail,
                    cancellationToken);
                return true;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _registered = false;
                _policyManager.SetControlConnected(false);
                _logger.LogWarning(ex, "Failed to report security event.");
                return false;
            }
        }

        private async Task RegisterAsync(CancellationToken cancellationToken)
        {
            if (_connection == null ||
                _connection.State != HubConnectionState.Connected)
                return;

            var config = _agentConfig.Config;
            string ip = GetLocalIp();
            await _connection.InvokeAsync(
                "RegisterAgent",
                config.MachineId,
                Environment.MachineName,
                ip,
                config.AgentToken,
                cancellationToken);

            _registered = true;
            _policyManager.SetControlConnected(true);
            _nextConnectionWarningUtc = DateTime.MinValue;
            _logger.LogInformation(
                "Registered with server as {MachineId} at {IpAddress}.",
                config.MachineId, ip);
        }

        private async Task RegisterAfterReconnectAsync()
        {
            _registered = false;
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await EnsureConnectedAsync(deadline.Token);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Reconnected transport but agent registration failed.");
            }
        }

        private void RunShutdown(string flag)
        {
            string verb = flag == "/r" ? "reboot" : "shutdown";
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = false
                };
                startInfo.ArgumentList.Add(flag);
                startInfo.ArgumentList.Add("/t");
                startInfo.ArgumentList.Add("20");
                startInfo.ArgumentList.Add("/c");
                startInfo.ArgumentList.Add(
                    $"USB Guardian: administrative {verb} in 20 seconds. Save your work.");
                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to execute {Verb}.", verb);
            }
        }

        private static string GetLocalIp()
        {
            try
            {
                return Dns.GetHostEntry(Dns.GetHostName()).AddressList
                    .FirstOrDefault(address =>
                        address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(address))
                    ?.ToString() ?? "unknown";
            }
            catch
            {
                return "unknown";
            }
        }

        public async ValueTask DisposeAsync()
        {
            _registered = false;
            _policyManager.SetControlConnected(false);
            if (_connection != null) await _connection.DisposeAsync();
            _connectionGate.Dispose();
        }
    }

    public class UsbDeviceDto
    {
        public string DeviceID { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public bool IsAllowed { get; set; }
        public bool IsHub { get; set; }
        public string ConnectedDeviceName { get; set; } = string.Empty;
        public bool? IsRunning { get; set; }
        public string EnforcementError { get; set; } = string.Empty;
    }
}
