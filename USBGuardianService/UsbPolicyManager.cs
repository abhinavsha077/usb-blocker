using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Management;
using System.Linq;

namespace USBGuardianService
{
    public class UsbDeviceInfo
    {
        public string DeviceID { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public bool IsAllowed { get; set; }
        public bool IsHub { get; set; }                          // true = Root Hub row, false = plugged-in device row
        public string ConnectedDeviceName { get; set; } = string.Empty; // friendly name of what's plugged in
    }

    public class UsbPolicyManager
    {
        private readonly ILogger<UsbPolicyManager> _logger;
        private readonly ConfigManager _config;
        
        private DateTime? _unlockExpiry = null;
        private readonly object _lockObj = new object();

        public UsbPolicyManager(ILogger<UsbPolicyManager> logger, ConfigManager config)
        {
            _logger = logger;
            _config = config;
        }

        public bool IsUnlocked
        {
            get
            {
                lock (_lockObj)
                {
                    if (_unlockExpiry.HasValue)
                    {
                        if (DateTime.UtcNow < _unlockExpiry.Value)
                        {
                            return true;
                        }
                        else
                        {
                            _logger.LogInformation("Unlock timer expired. Auto relocking.");
                            _unlockExpiry = null;
                            ForceReevaluation();
                            return false;
                        }
                    }
                    return false;
                }
            }
        }

        public void Unlock(TimeSpan? duration = null)
        {
            lock (_lockObj)
            {
                if (duration.HasValue)
                {
                    _unlockExpiry = DateTime.UtcNow.Add(duration.Value);
                    _logger.LogInformation("USB system unlocked for {Minutes} minutes.", duration.Value.TotalMinutes);
                }
                else
                {
                    _unlockExpiry = DateTime.MaxValue;
                    _logger.LogInformation("USB system unlocked indefinitely.");
                }
            }
            // Enforce is handled asynchronously by the caller
        }

        public void Lock()
        {
            lock (_lockObj)
            {
                _unlockExpiry = null;
                _logger.LogInformation("USB system locked manually.");
            }
            // Enforce is handled asynchronously by the caller
        }

        private void ForceReevaluation()
        {
            Enforce();
        }

        public List<UsbDeviceInfo> GetConnectedDevices()
        {
            var devices = new List<UsbDeviceInfo>();
            bool isMasterUnlocked = IsUnlocked;
            try
            {
                // --- Layer 1: Root Hubs (with Allow/Block policy) ---
                string hubQuery = "SELECT * FROM Win32_PnPEntity WHERE Name LIKE '%Hub%' AND PNPDeviceID LIKE 'USB%'";
                using (var searcher = new ManagementObjectSearcher(hubQuery))
                {
                    foreach (ManagementObject device in searcher.Get())
                    {
                        string id = device["PNPDeviceID"]?.ToString() ?? "Unknown";

                        bool isExplicitlyAllowed = _config.IsExplicitlyAllowed(id);
                        bool isExplicitlyBlocked = _config.IsExplicitlyBlocked(id);

                        bool isEffectiveAllowed;
                        if (isExplicitlyBlocked) isEffectiveAllowed = false;
                        else if (isExplicitlyAllowed) isEffectiveAllowed = true;
                        else isEffectiveAllowed = isMasterUnlocked;

                        devices.Add(new UsbDeviceInfo
                        {
                            DeviceID  = id,
                            Name      = device["Name"]?.ToString() ?? "Unknown USB Hub",
                            Status    = device["Status"]?.ToString() ?? "Unknown",
                            IsAllowed = isEffectiveAllowed,
                            IsHub     = true
                        });
                    }
                }

                // --- Layer 2: Actually plugged-in devices (storage + phones/MTP) ---
                var deviceQueries = new[]
                {
                    // No Status filter — show both active (OK) and blocked/ejected (Error) devices
                    "SELECT * FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USBSTOR%'",
                    "SELECT * FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB\\VID%' " +
                    "AND (Name LIKE '%Phone%' OR Name LIKE '%MTP%' OR Name LIKE '%Portable%' " +
                    "OR Name LIKE '%Camera%' OR Name LIKE '%Android%' OR Name LIKE '%iPhone%')"
                };

                foreach (string q in deviceQueries)
                {
                    using var s = new ManagementObjectSearcher(q);
                    foreach (ManagementObject d in s.Get())
                    {
                        string id   = d["PNPDeviceID"]?.ToString() ?? "Unknown";
                        string name = d["Name"]?.ToString() ?? "Unknown USB Device";

                        // Skip duplicates (e.g. disk & partition entries for same device)
                        if (devices.Any(x => !x.IsHub && x.DeviceID == id)) continue;

                        devices.Add(new UsbDeviceInfo
                        {
                            DeviceID            = id,
                            Name                = name,
                            Status              = d["Status"]?.ToString() ?? "Unknown",
                            IsAllowed           = isMasterUnlocked,   // mirrors global state
                            IsHub               = false,
                            ConnectedDeviceName = name
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to query connected USB devices.");
            }
            return devices;
        }

        public void Enforce()
        {
            lock (_lockObj)
            {
                bool systemUnlocked = IsUnlocked;
                var allowed = _config.GetAllowedDevices();

                try
                {
                    string query = "SELECT * FROM Win32_PnPEntity WHERE Name LIKE '%Hub%' AND PNPDeviceID LIKE 'USB%'";
                    using (var searcher = new ManagementObjectSearcher(query))
                    {
                        foreach (ManagementObject device in searcher.Get())
                        {
                            string deviceId = device["PNPDeviceID"]?.ToString();
                            string status = device["Status"]?.ToString();
                            
                            if (string.IsNullOrEmpty(deviceId)) continue;

                            bool isExplicitlyAllowed = _config.IsExplicitlyAllowed(deviceId);
                            bool isExplicitlyBlocked = _config.IsExplicitlyBlocked(deviceId);

                            bool shouldBeEnabled;
                            if (isExplicitlyBlocked) shouldBeEnabled = false;
                            else if (isExplicitlyAllowed) shouldBeEnabled = true;
                            else shouldBeEnabled = systemUnlocked;
                            
                            // OK usually means enabled for PnP devices
                            bool isEnabled = (status == "OK");

                            if (shouldBeEnabled && !isEnabled)
                            {
                                _logger.LogInformation("Enabling USB Device: {DeviceId}", deviceId);
                                ExecuteNativeCommand($"pnputil /enable-device \"{deviceId}\"");
                            }
                            else if (!shouldBeEnabled && isEnabled)
                            {
                                _logger.LogInformation("Disabling USB Device: {DeviceId}", deviceId);
                                ExecuteNativeCommand($"pnputil /disable-device \"{deviceId}\"");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to enforce granular USB policy. Ensure the service has SYSTEM privileges.");
                }

                // Second layer: sync already-connected USB storage/MTP device states
                if (!systemUnlocked)
                    ForceEjectActiveUsbDevices();   // Kill already-mounted devices
                else
                    ForceEnableUsbDevices();         // Re-enable any that were force-ejected
            }
        }

        /// <summary>Queries USB storage and MTP devices with a given status filter and runs pnputil on each.</summary>
        private void ApplyToUsbChildDevices(string statusFilter, string pnputilVerb, string logVerb)
        {
            var queries = new[]
            {
                "SELECT * FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USBSTOR%'",
                "SELECT * FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB\\VID%' " +
                "AND (Name LIKE '%Phone%' OR Name LIKE '%MTP%' OR Name LIKE '%Portable%' " +
                "OR Name LIKE '%Camera%' OR Name LIKE '%Android%' OR Name LIKE '%iPhone%')"
            };

            foreach (string wmiQuery in queries)
            {
                try
                {
                    using var searcher = new ManagementObjectSearcher(wmiQuery);
                    foreach (ManagementObject device in searcher.Get())
                    {
                        string deviceId = device["PNPDeviceID"]?.ToString();
                        string status   = device["Status"]?.ToString();
                        if (string.IsNullOrEmpty(deviceId)) continue;

                        if (status == statusFilter)
                        {
                            _logger.LogInformation("{Verb} USB child device: {DeviceId}", logVerb, deviceId);
                            ExecuteNativeCommand($"pnputil /{pnputilVerb} \"{deviceId}\"");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed during {Verb} pass on USB child devices.", logVerb);
                }
            }
        }

        private void ForceEjectActiveUsbDevices() =>
            ApplyToUsbChildDevices(statusFilter: "OK",  pnputilVerb: "disable-device", logVerb: "Force-ejecting");

        private void ForceEnableUsbDevices() =>
            ApplyToUsbChildDevices(statusFilter: "Error", pnputilVerb: "enable-device",  logVerb: "Re-enabling");



        private void ExecuteNativeCommand(string commandArgs)
        {
            try
            {
                var processInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c {commandArgs}",
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using (var process = System.Diagnostics.Process.Start(processInfo))
                {
                    process?.WaitForExit(3000); // Wait briefly but don't hang
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to execute native command.");
            }
        }
        
        public string GetStatusMessage()
        {
            lock (_lockObj)
            {
                if (_unlockExpiry.HasValue)
                {
                    if (_unlockExpiry.Value == DateTime.MaxValue)
                        return "SYSTEM UNLOCKED";
                    else if (_unlockExpiry.Value > DateTime.UtcNow)
                        return $"SYSTEM UNLOCKED:UNTIL:{_unlockExpiry.Value:u}";
                    else
                        return "SYSTEM LOCKED"; 
                }
                return "SYSTEM LOCKED";
            }
        }
    }
}
