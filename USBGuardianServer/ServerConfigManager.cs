using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using USBGuardian.Shared;

namespace USBGuardianServer
{
    public class ServerConfigManager
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
        private readonly string _filePath;
        private readonly ILogger<ServerConfigManager> _logger;
        private readonly bool _allowUnauthenticatedAgents;
        private readonly object _lock = new();
        private ServerConfig _config;

        public ServerConfigManager(
            IConfiguration configuration,
            ILogger<ServerConfigManager> logger)
        {
            _logger = logger;
            _allowUnauthenticatedAgents =
                configuration.GetValue<bool>(
                    "Security:AllowUnauthenticatedAgents");
            string configuredSecret =
                ServerDeploymentSettings.ResolveAgentSecret(configuration, logger);
            _filePath = configuration["ConfigFilePath"]
                ?? @"C:\ProgramData\USBGuardian\server_config.json";
            var parent = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
            WindowsFileSecurity.RestrictExistingFileToSystemAndAdministrators(_filePath);

            if (!File.Exists(_filePath))
            {
                _config = NewDefault(configuredSecret);
                Save();
            }
            else
            {
                try
                {
                    _config = JsonSerializer.Deserialize<ServerConfig>(
                        File.ReadAllText(_filePath)) ?? NewDefault(configuredSecret);
                }
                catch (Exception ex)
                {
                    _logger.LogCritical(ex,
                        "Server configuration {File} is unreadable. Using fail-safe defaults; " +
                        "the existing file was not overwritten.", _filePath);
                    _config = NewDefault(configuredSecret);
                }

                // A deployment can inject AgentSecret through appsettings or an
                // environment variable or protected MSI registry setting.
                if (!string.IsNullOrWhiteSpace(configuredSecret) &&
                    !string.Equals(
                        _config.AgentSecret,
                        configuredSecret,
                        StringComparison.Ordinal))
                {
                    _config.AgentSecret = configuredSecret;
                    Save();
                }
            }

            if (!AgentAuthEnabled)
            {
                if (_allowUnauthenticatedAgents)
                {
                    _logger.LogWarning(
                        "Agent authentication is explicitly disabled. Any network client " +
                        "can register as an endpoint.");
                }
                else
                {
                    _logger.LogInformation(
                        "No legacy AgentSecret is configured. Endpoints require their individually enrolled credentials.");
                }
            }
            if (IsDefaultPassword)
                _logger.LogWarning(
                    "Admin password is still the default. Remote admin API access is blocked.");
        }

        public bool VerifyPassword(string? plainPassword)
        {
            if (string.IsNullOrEmpty(plainPassword)) return false;
            lock (_lock)
            {
                try
                {
                    return VerifyPasswordCore(plainPassword);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Stored admin password hash is invalid.");
                    return false;
                }
            }
        }

        public bool TryChangePassword(
            string? currentPassword,
            string newPassword)
        {
            lock (_lock)
            {
                try
                {
                    if (string.IsNullOrEmpty(currentPassword) ||
                        !VerifyPasswordCore(currentPassword))
                        return false;

                    string previousHash = _config.PasswordHash;
                    bool previousDefault = _config.PasswordIsDefault;
                    bool previousEnhanced = _config.PasswordHashUsesEnhancedEntropy;
                    try
                    {
                        _config.PasswordHash =
                            BCrypt.Net.BCrypt.EnhancedHashPassword(newPassword);
                        _config.PasswordHashUsesEnhancedEntropy = true;
                        _config.PasswordIsDefault = false;
                        SaveUnsafe();
                        return true;
                    }
                    catch
                    {
                        _config.PasswordHash = previousHash;
                        _config.PasswordIsDefault = previousDefault;
                        _config.PasswordHashUsesEnhancedEntropy = previousEnhanced;
                        throw;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Could not verify or persist the admin password change.");
                    return false;
                }
            }
        }

        public bool IsDefaultPassword
        {
            get { lock (_lock) return _config.PasswordIsDefault; }
        }

        private bool VerifyPasswordCore(string password) =>
            _config.PasswordHashUsesEnhancedEntropy
                ? BCrypt.Net.BCrypt.EnhancedVerify(password, _config.PasswordHash)
                : BCrypt.Net.BCrypt.Verify(password, _config.PasswordHash);

        public bool AgentAuthEnabled
        {
            get { lock (_lock) return !string.IsNullOrWhiteSpace(_config.AgentSecret); }
        }

        public bool ValidateAgentToken(string? token)
        {
            lock (_lock)
            {
                if (string.IsNullOrWhiteSpace(_config.AgentSecret))
                    return _allowUnauthenticatedAgents;
                if (token == null) return false;

                byte[] supplied = Encoding.UTF8.GetBytes(token);
                byte[] expected = Encoding.UTF8.GetBytes(_config.AgentSecret);
                return supplied.Length == expected.Length &&
                    CryptographicOperations.FixedTimeEquals(supplied, expected);
            }
        }

        private static ServerConfig NewDefault(string? configuredSecret) => new()
        {
            PasswordHash = BCrypt.Net.BCrypt.EnhancedHashPassword("admin"),
            PasswordHashUsesEnhancedEntropy = true,
            PasswordIsDefault = true,
            AgentSecret = configuredSecret?.Trim() ?? string.Empty
        };

        private void Save()
        {
            lock (_lock) SaveUnsafe();
        }

        private void SaveUnsafe()
        {
            WindowsFileSecurity.WriteAllTextAtomically(
                _filePath,
                JsonSerializer.Serialize(_config, JsonOptions),
                restrictToSystemAndAdministrators: true);
        }

        private class ServerConfig
        {
            public string PasswordHash { get; set; } = string.Empty;
            // Missing in pre-migration files: those continue using classic BCrypt
            // until the administrator rotates the password.
            public bool PasswordHashUsesEnhancedEntropy { get; set; }
            public bool PasswordIsDefault { get; set; } = true;
            public string AgentSecret { get; set; } = string.Empty;
        }
    }
}
