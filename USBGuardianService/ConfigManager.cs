using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace USBGuardianService
{
    public class ConfigData
    {
        public string PasswordHash { get; set; } = string.Empty;
        public List<string> AllowedDevices { get; set; } = new List<string>();
        public List<string> BlockedDevices { get; set; } = new List<string>();
    }

    public class ConfigManager
    {
        private readonly ILogger<ConfigManager> _logger;
        private readonly string _configDirectory = @"C:\ProgramData\USBGuardian";
        private readonly string _configFilePath = @"C:\ProgramData\USBGuardian\config.json";
        
        private ConfigData _config;

        public ConfigManager(ILogger<ConfigManager> logger)
        {
            _logger = logger;
            _config = new ConfigData();
            LoadConfig();
        }

        private void LoadConfig()
        {
            try
            {
                if (!Directory.Exists(_configDirectory))
                {
                    Directory.CreateDirectory(_configDirectory);
                }

                if (!File.Exists(_configFilePath))
                {
                    // Default password is 'admin'
                    string defaultHash = BCrypt.Net.BCrypt.HashPassword("admin");
                    _config.PasswordHash = defaultHash;
                    SaveConfig();
                    _logger.LogInformation("Created default configuration file.");
                }
                else
                {
                    string json = File.ReadAllText(_configFilePath);
                    var loaded = JsonSerializer.Deserialize<ConfigData>(json);
                    if (loaded != null)
                    {
                        _config = loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load or construct configuration.");
            }
        }

        public void SaveConfig()
        {
            try
            {
                File.WriteAllText(_configFilePath, JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save configuration.");
            }
        }

        public bool VerifyPassword(string inputPassword)
        {
            if (string.IsNullOrEmpty(_config.PasswordHash) || string.IsNullOrEmpty(inputPassword))
                return false;

            try
            {
                return BCrypt.Net.BCrypt.Verify(inputPassword, _config.PasswordHash);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Password verification failed due to internal error.");
                return false;
            }
        }

        public List<string> GetAllowedDevices() => new List<string>(_config.AllowedDevices);
        public List<string> GetBlockedDevices()  => new List<string>(_config.BlockedDevices);


        public void AddAllowedDevice(string deviceId)
        {
            if (!_config.AllowedDevices.Contains(deviceId))
            {
                _config.AllowedDevices.Add(deviceId);
            }
            if (_config.BlockedDevices.Contains(deviceId))
            {
                _config.BlockedDevices.Remove(deviceId);
            }
            SaveConfig();
        }

        public void AddBlockedDevice(string deviceId)
        {
            if (!_config.BlockedDevices.Contains(deviceId))
            {
                _config.BlockedDevices.Add(deviceId);
            }
            if (_config.AllowedDevices.Contains(deviceId))
            {
                _config.AllowedDevices.Remove(deviceId);
            }
            SaveConfig();
        }

        public void ClearAllowedDevices()
        {
            _config.AllowedDevices.Clear();
            SaveConfig();
        }

        public void ClearBlockedDevices()
        {
            _config.BlockedDevices.Clear();
            SaveConfig();
        }

        public bool IsExplicitlyAllowed(string id) => _config.AllowedDevices.Contains(id);
        public bool IsExplicitlyBlocked(string id) => _config.BlockedDevices.Contains(id);

        public void ChangePassword(string newPassword)
        {
            _config.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
            SaveConfig();
            _logger.LogInformation("Admin password changed successfully.");
        }
    }
}
