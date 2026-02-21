using Microsoft.Extensions.Logging;
using System.Management;

namespace USBGuardianAgent
{
    public class UsbDeviceInfo
    {
        public string DeviceID            { get; set; } = string.Empty;
        public string Name                { get; set; } = string.Empty;
        public string Status              { get; set; } = string.Empty;
        public bool   IsAllowed           { get; set; }
        public bool   IsHub               { get; set; }
        public string ConnectedDeviceName { get; set; } = string.Empty;
    }

    public class UsbPolicyManager
    {
        private readonly ILogger<UsbPolicyManager> _logger;

        private readonly object _lock = new();
        private DateTime? _unlockExpiry = null;
        private List<string> _allowedDevices = new();
        private List<string> _blockedDevices = new();

        // Track previous lock state to only trigger force-eject/enable on TRANSITIONS, not every cycle
        private bool? _prevAnyBlocked = null;

        public UsbPolicyManager(ILogger<UsbPolicyManager> logger) => _logger = logger;

        public bool IsUnlocked
        {
            get
            {
                lock (_lock)
                {
                    if (!_unlockExpiry.HasValue) return false;
                    if (DateTime.UtcNow < _unlockExpiry.Value) return true;
                    _logger.LogInformation("Timed unlock expired. Auto-relocking.");
                    _unlockExpiry = null;
                    return false;
                }
            }
        }

        public void Unlock(TimeSpan? duration = null)
        {
            lock (_lock)
            {
                _unlockExpiry = duration.HasValue
                    ? DateTime.UtcNow.Add(duration.Value)
                    : DateTime.MaxValue;
                _logger.LogInformation("Unlocked locally for {D}.", duration?.ToString() ?? "indefinitely");
            }
        }

        public void Lock()
        {
            lock (_lock) { _unlockExpiry = null; }
            _logger.LogInformation("Locked locally.");
        }

        /// <summary>Called when server pushes a policy update.</summary>
        public void ApplyRemotePolicy(bool isUnlocked, List<string> allowed, List<string> blocked)
        {
            lock (_lock)
            {
                _allowedDevices = new List<string>(allowed);
                _blockedDevices = new List<string>(blocked);
                _unlockExpiry   = isUnlocked ? DateTime.MaxValue : (DateTime?)null;
            }
            Enforce();
        }

        public bool IsExplicitlyAllowed(string id) { lock (_lock) return _allowedDevices.Contains(id); }
        public bool IsExplicitlyBlocked(string id) { lock (_lock) return _blockedDevices.Contains(id); }
        public int  BlockedCount               ()  { lock (_lock) return _blockedDevices.Count; }

        // ── Enforcement ──────────────────────────────────────────────────────
        public void Enforce()
        {
            lock (_lock)
            {
                bool unlocked = IsUnlocked;
                try
                {
                    using var searcher = new ManagementObjectSearcher(
                        "SELECT * FROM Win32_PnPEntity WHERE Name LIKE '%Hub%' AND PNPDeviceID LIKE 'USB%'");
                    foreach (ManagementObject dev in searcher.Get())
                    {
                        string id  = dev["PNPDeviceID"]?.ToString() ?? string.Empty;
                        if (string.IsNullOrEmpty(id)) continue;

                        bool shouldEnable = IsExplicitlyBlocked(id) ? false
                                          : IsExplicitlyAllowed(id) ? true
                                          : unlocked;
                        bool isEnabled = dev["Status"]?.ToString() == "OK";

                        if (shouldEnable && !isEnabled)
                        {
                            _logger.LogInformation("Enabling hub: {Id}", id);
                            Run($"pnputil /enable-device \"{id}\"");
                        }
                        else if (!shouldEnable && isEnabled)
                        {
                            _logger.LogInformation("Disabling hub: {Id}", id);
                            Run($"pnputil /disable-device \"{id}\"");
                        }
                    }
                }
                catch (Exception ex) { _logger.LogError(ex, "Hub enforcement failed."); }

                bool anyBlocked = BlockedCount() > 0 || !unlocked;

                // Only force-eject or re-enable on STATE CHANGE — not every 5-second cycle.
                // Running pnputil /enable-device on an already-mounted drive causes Windows
                // to fire a device-reconnect event (USB sound + AutoPlay popup) every cycle.
                if (_prevAnyBlocked == null || anyBlocked != _prevAnyBlocked.Value)
                {
                    _logger.LogInformation("Lock state changed → anyBlocked={B}. Applying child device policy.", anyBlocked);
                    if (anyBlocked) ForceEject();
                    else            ForceEnable();
                    _prevAnyBlocked = anyBlocked;
                }
            }
        }

        public List<UsbDeviceInfo> GetConnectedDevices()
        {
            var list = new List<UsbDeviceInfo>();
            bool unlocked = IsUnlocked;
            try
            {
                // Hubs
                using (var s = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_PnPEntity WHERE Name LIKE '%Hub%' AND PNPDeviceID LIKE 'USB%'"))
                {
                    foreach (ManagementObject d in s.Get())
                    {
                        string id = d["PNPDeviceID"]?.ToString() ?? "Unknown";
                        bool eff = IsExplicitlyBlocked(id) ? false
                                 : IsExplicitlyAllowed(id) ? true
                                 : unlocked;
                        list.Add(new UsbDeviceInfo
                        {
                            DeviceID  = id,
                            Name      = d["Name"]?.ToString() ?? "Unknown Hub",
                            Status    = d["Status"]?.ToString() ?? "Unknown",
                            IsAllowed = eff,
                            IsHub     = true
                        });
                    }
                }
                // Connected devices (storage + MTP)
                foreach (var q in new[]
                {
                    "SELECT * FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USBSTOR%'",
                    "SELECT * FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB\\VID%' AND " +
                    "(Name LIKE '%Phone%' OR Name LIKE '%MTP%' OR Name LIKE '%Portable%' " +
                    "OR Name LIKE '%Camera%' OR Name LIKE '%Android%' OR Name LIKE '%iPhone%')"
                })
                {
                    using var s = new ManagementObjectSearcher(q);
                    foreach (ManagementObject d in s.Get())
                    {
                        string id   = d["PNPDeviceID"]?.ToString() ?? "Unknown";
                        string name = d["Name"]?.ToString() ?? "Unknown Device";
                        if (list.Any(x => !x.IsHub && x.DeviceID == id)) continue;
                        list.Add(new UsbDeviceInfo
                        {
                            DeviceID            = id,
                            Name                = name,
                            Status              = d["Status"]?.ToString() ?? "Unknown",
                            IsAllowed           = unlocked,
                            IsHub               = false,
                            ConnectedDeviceName = name
                        });
                    }
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "GetConnectedDevices failed."); }
            return list;
        }

        // ── Helpers ──────────────────────────────────────────────────────────
        // ForceEject: disable devices that are currently OK (mounted)
        // ForceEnable: enable ALL listed devices regardless of status — pnputil /enable-device is
        //              a no-op on already-enabled devices, so this is safe.
        private void ForceEject()  => ApplyToChildDevices("OK",  "disable-device", "Ejecting");
        private void ForceEnable() => ApplyToChildDevices(null,  "enable-device",  "Re-enabling");

        private void ApplyToChildDevices(string statusFilter, string verb, string logVerb)
        {
            foreach (var q in new[]
            {
                "SELECT * FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USBSTOR%'",
                "SELECT * FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB\\VID%' AND " +
                "(Name LIKE '%Phone%' OR Name LIKE '%MTP%' OR Name LIKE '%Portable%' " +
                "OR Name LIKE '%Camera%' OR Name LIKE '%Android%' OR Name LIKE '%iPhone%')"
            })
            {
                try
                {
                    using var s = new ManagementObjectSearcher(q);
                    foreach (ManagementObject d in s.Get())
                    {
                        string id = d["PNPDeviceID"]?.ToString() ?? string.Empty;
                        if (string.IsNullOrEmpty(id)) continue;

                        // null statusFilter means apply to ALL devices (used for re-enable)
                        bool matches = statusFilter == null ||
                                       d["Status"]?.ToString() == statusFilter;
                        if (matches)
                        {
                            _logger.LogInformation("{V} device: {Id}", logVerb, id);
                            Run($"pnputil /{verb} \"{id}\"");
                        }
                    }
                }
                catch (Exception ex) { _logger.LogError(ex, "ApplyToChildDevices ({V}) failed.", logVerb); }
            }
        }

        private static void Run(string args)
        {
            try
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe", Arguments = $"/c {args}",
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                    CreateNoWindow = true, UseShellExecute = false
                });
                p?.WaitForExit(3000);
            }
            catch { /* best-effort */ }
        }
    }
}
