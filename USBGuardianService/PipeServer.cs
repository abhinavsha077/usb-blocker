using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace USBGuardianService
{
    public class PipeServer : BackgroundService
    {
        private readonly ILogger<PipeServer> _logger;
        private readonly UsbPolicyManager _policyManager;
        private readonly ConfigManager _configManager;
        private const string PipeName = "USBGuardianControlPipe";

        public PipeServer(ILogger<PipeServer> logger, UsbPolicyManager policyManager, ConfigManager configManager)
        {
            _logger = logger;
            _policyManager = policyManager;
            _configManager = configManager;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Pipe Server started, listening on {PipeName}", PipeName);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var security = new PipeSecurity();
                    var everyone = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
                    security.AddAccessRule(new PipeAccessRule(everyone, PipeAccessRights.ReadWrite, AccessControlType.Allow));

                    using (var pipeServer = NamedPipeServerStreamAcl.Create(
                        PipeName,
                        PipeDirection.InOut,
                        1,
                        PipeTransmissionMode.Message,
                        PipeOptions.Asynchronous,
                        0,
                        0,
                        security))
                    {
                        await pipeServer.WaitForConnectionAsync(stoppingToken);

                        using (var reader = new StreamReader(pipeServer))
                        using (var writer = new StreamWriter(pipeServer) { AutoFlush = true })
                        {
                            string request = await reader.ReadLineAsync();
                            if (!string.IsNullOrEmpty(request))
                            {
                                string response = ProcessRequest(request);
                                await writer.WriteLineAsync(response);
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error handling named pipe connection.");
                }
            }
        }

        private string ProcessRequest(string request)
        {
            // Format: Password:Command:Args (Args is optional)
            var parts = request.Split(':', 3);
            if (parts.Length < 2) return "ERROR:Invalid format";

            string password = parts[0];
            string command = parts[1].ToUpper();
            string args = parts.Length > 2 ? parts[2] : string.Empty;

            if (!_configManager.VerifyPassword(password))
            {
                _logger.LogWarning("Failed login attempt via Named Pipe.");
                return "ERROR:Invalid password";
            }

            switch (command)
            {
                case "UNLOCK":
                    if (!string.IsNullOrEmpty(args) && int.TryParse(args, out int minutes))
                    {
                        _configManager.ClearBlockedDevices();
                        _policyManager.Unlock(TimeSpan.FromMinutes(minutes));
                        Task.Run(() => _policyManager.Enforce());
                        return "SUCCESS:SYSTEM UNLOCKED";
                    }
                    else
                    {
                        _configManager.ClearBlockedDevices();
                        _policyManager.Unlock();
                        Task.Run(() => _policyManager.Enforce());
                        return "SUCCESS:SYSTEM UNLOCKED";
                    }
                case "LOCK":
                    _configManager.ClearAllowedDevices();
                    _policyManager.Lock();
                    Task.Run(() => _policyManager.Enforce());
                    return "SUCCESS:SYSTEM LOCKED";
                case "STATUS":
                    return "SUCCESS:" + _policyManager.GetStatusMessage();
                case "LIST":
                    var devices = _policyManager.GetConnectedDevices();
                    return "SUCCESS:" + System.Text.Json.JsonSerializer.Serialize(devices);
                case "ALLOW":
                    if (!string.IsNullOrEmpty(args))
                    {
                        _configManager.AddAllowedDevice(args);
                        Task.Run(() => _policyManager.Enforce());
                        return "SUCCESS:Device allowed";
                    }
                    return "ERROR:Missing device ID";
                case "BLOCK":
                    if (!string.IsNullOrEmpty(args))
                    {
                        _configManager.AddBlockedDevice(args);
                        Task.Run(() => _policyManager.Enforce());
                        return "SUCCESS:Device blocked";
                    }
                    return "ERROR:Missing device ID";
                default:
                    return "ERROR:Unknown command";
            }
        }
    }
}
