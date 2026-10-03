using System.Text.Json;
using USBGuardian.Shared;

namespace USBGuardianAgent
{
    public class AgentConfig
    {
        public string ServerUrl { get; set; } = "http://localhost:5050";
        public string MachineId { get; set; } = Environment.MachineName;
        public string AgentToken { get; set; } = string.Empty;
        public bool RequireHttps { get; set; }
    }

    public class AgentConfigManager
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
        public AgentConfig Config { get; }

        public AgentConfigManager(
            IConfiguration configuration,
            ILogger<AgentConfigManager> logger)
        {
            string path = configuration["AgentConfigPath"]
                ?? @"C:\ProgramData\USBGuardian\agent_config.json";
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
            WindowsFileSecurity.RestrictExistingFileToSystemAndAdministrators(path);

            if (!File.Exists(path))
            {
                var installedConfig = AgentDeploymentSettings.TryRead(logger);
                Config = Normalize(installedConfig ?? new AgentConfig());
                if (installedConfig != null)
                {
                    logger.LogInformation(
                        "Loaded agent configuration from the protected machine installer settings.");
                    return;
                }

                WriteAtomically(path, Config);
                logger.LogWarning(
                    "Created default agent configuration at {Path}. Configure the server URL and token.",
                    path);
                return;
            }

            try
            {
                Config = Normalize(
                    JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path))
                    ?? throw new JsonException("Configuration was empty."));
            }
            catch (Exception ex)
            {
                // Keep the endpoint alive so its cached fail-closed policy can still
                // be enforced. Do not overwrite the malformed file automatically.
                Config = Normalize(
                    AgentDeploymentSettings.TryRead(logger) ?? new AgentConfig());
                logger.LogCritical(ex,
                    "Agent configuration {Path} is unreadable. Using safe defaults; " +
                    "installer settings are used when available. Repair the file " +
                    "and restart the service.", path);
            }
        }

        private static AgentConfig Normalize(AgentConfig config)
        {
            string serverUrl = config.ServerUrl?.Trim() ?? string.Empty;
            if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                serverUrl = "http://localhost:5050";
            if (config.RequireHttps && !serverUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return new AgentConfig { ServerUrl = "https://localhost:5443", RequireHttps = true };

            string machineId = string.IsNullOrWhiteSpace(config.MachineId)
                ? Environment.MachineName
                : config.MachineId.Trim();
            if (machineId.Length > 128 || machineId.Any(char.IsControl) || machineId.Contains('/') || machineId.Contains('\\'))
                return new AgentConfig { ServerUrl = serverUrl, RequireHttps = config.RequireHttps };

            return new AgentConfig
            {
                ServerUrl = serverUrl.TrimEnd('/'),
                MachineId = machineId,
                AgentToken = config.AgentToken?.Trim() ?? string.Empty,
                RequireHttps = config.RequireHttps
            };
        }

        private static void WriteAtomically(string path, AgentConfig config)
        {
            WindowsFileSecurity.WriteAllTextAtomically(
                path,
                JsonSerializer.Serialize(config, JsonOptions),
                restrictToSystemAndAdministrators: true);
        }
    }
}
