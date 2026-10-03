using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Management;
using System.Text.Json;
using USBGuardian.Shared;

namespace USBGuardianAgent
{
    public class UsbDeviceInfo
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

    public class UsbPolicyManager
    {
        private readonly string AlertDir;
        private readonly string PolicyCachePath;
        private readonly string ManagedStatePath;
        private readonly IUsbDeviceAccess _devices;
        private readonly UsbDeviceMonitor _monitor;
        private const string KeyboardQuery =
            "SELECT PNPDeviceID, Name, Status, ConfigManagerErrorCode " +
            "FROM Win32_PnPEntity WHERE PNPClass='Keyboard'";
        private const string OpticalQuery =
            "SELECT PNPDeviceID, Status, ConfigManagerErrorCode, PNPClass, Service " +
            "FROM Win32_PnPEntity WHERE PNPClass='CDROM'";
        private const string BluetoothQuery =
            "SELECT PNPDeviceID, Status, ConfigManagerErrorCode, PNPClass, Service " +
            "FROM Win32_PnPEntity WHERE PNPClass='Bluetooth'";
        private const string WifiQuery =
            "SELECT PNPDeviceID, Status, ConfigManagerErrorCode, PNPClass, Service " +
            "FROM Win32_PnPEntity WHERE PNPClass='Net' AND " +
            "(Name LIKE '%Wireless%' OR Name LIKE '%Wi-Fi%' OR Name LIKE '%802.11%')";

        private static readonly string[] RemovableDeviceQueries = UsbDeviceQueries.Removable;

        private readonly ILogger<UsbPolicyManager> _logger;
        private readonly SecurityEventOutbox _securityEventOutbox;
        private readonly object _lock = new();
        private readonly object _enforcementLock = new();
        private DateTime? _unlockExpiry;
        private bool _learnMode;
        private DateTime? _learnModeExpiry;
        private bool _controlConnected;
        private bool _opticalBlocked;
        private bool _bluetoothBlocked;
        private bool _wifiBlocked;
        private bool _badUsbBlock;
        private List<string> _allowedDevices = new();
        private List<string> _blockedDevices = new();

        private readonly HashSet<string> _trustedKeyboards =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _presentKeyboards =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _devicesDisabledByAgent =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _actionRetryAfterUtc =
            new(StringComparer.OrdinalIgnoreCase);
        private bool _keyboardBaselineDone;
        private long _revision;
        private long? _appliedRevision;
        private DateTime? _enforcementUtc;
        private string _policyError = string.Empty;
        private bool _scanFailed;
        private bool _managedStateReadable = true;
        public UsbPolicyManager(
            ILogger<UsbPolicyManager> logger,
            SecurityEventOutbox securityEventOutbox,
            IConfiguration configuration,
            IUsbDeviceAccess devices,
            UsbDeviceMonitor monitor)
        {
            _logger = logger;
            _securityEventOutbox = securityEventOutbox;
            _devices = devices;
            _monitor = monitor;
            AlertDir = configuration["AlertDirectory"] ?? @"C:\ProgramData\USBGuardian\alerts";
            PolicyCachePath = configuration["AgentPolicyCachePath"] ?? @"C:\ProgramData\USBGuardian\agent_policy_cache.json";
            ManagedStatePath = configuration["ManagedDeviceStatePath"] ?? @"C:\ProgramData\USBGuardian\agent_managed_state.json";
            WindowsFileSecurity.EnsureRuntimeParentDirectory(ManagedStatePath);
            WindowsFileSecurity.EnsureRuntimeParentDirectory(Path.Combine(AlertDir, ".initialization"));
            try
            {
                Directory.CreateDirectory(AlertDir);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not initialize endpoint alert directory {Directory}.",
                    AlertDir);
            }
            LoadCachedPolicy();
            LoadManagedState();
        }

        private bool IsUnlockedCore()
        {
            if (!_unlockExpiry.HasValue) return false;
            if (DateTime.UtcNow < _unlockExpiry.Value) return true;

            _logger.LogInformation("Timed unlock expired. Auto-relocking.");
            _unlockExpiry = null;
            return false;
        }

        private bool IsLearnModeActiveCore()
        {
            if (!_learnMode) return false;
            if (_learnModeExpiry.HasValue && DateTime.UtcNow < _learnModeExpiry.Value)
                return _controlConnected;

            _logger.LogInformation("Learn mode expired locally. Protection is active.");
            _learnMode = false;
            _learnModeExpiry = null;
            return false;
        }

        public bool IsUnlocked
        {
            get { lock (_lock) return IsUnlockedCore(); }
        }

        public void Unlock(TimeSpan? duration = null)
        {
            lock (_lock)
            {
                _unlockExpiry = duration.HasValue
                    ? DateTime.UtcNow.Add(duration.Value)
                    : DateTime.MaxValue;
            }
            _logger.LogInformation("Unlocked for {Duration}.",
                duration?.ToString() ?? "indefinitely");
            _monitor.RequestScan();
        }

        public void Lock()
        {
            lock (_lock)
            {
                _unlockExpiry = null;
                _learnMode = false;
                _learnModeExpiry = null;
            }
            _logger.LogInformation("Locked.");
            _monitor.RequestScan();
        }

        public void SetControlConnected(bool connected)
        {
            bool changed;
            lock (_lock)
            {
                changed = _controlConnected != connected;
                _controlConnected = connected;
            }

            if (changed)
                _logger.LogInformation("Control connection state changed: {State}.",
                    connected ? "connected" : "offline; learn mode is fail-closed");
            if (changed) _monitor.RequestScan();
        }

        public void ApplyRemotePolicy(MachinePolicies policy)
        {
            ArgumentNullException.ThrowIfNull(policy);

            // Commit to disk before publishing in memory: an unpersisted policy
            // is never presented as applied and cannot revert silently at reboot.
            lock (_lock)
            {
                SaveCachedPolicy(policy);
                _revision = policy.Revision;
                _policyError = string.Empty;
                _allowedDevices = new List<string>(policy.AllowedDevices ?? new());
                _blockedDevices = new List<string>(policy.BlockedDevices ?? new());
                _learnMode = policy.LearnMode;
                _learnModeExpiry = policy.LearnModeExpiry;
                _opticalBlocked = policy.OpticalBlocked;
                _bluetoothBlocked = policy.BluetoothBlocked;
                _wifiBlocked = policy.WifiBlocked;
                _badUsbBlock = policy.BadUsbBlock;

                if (!policy.IsUnlocked)
                {
                    _unlockExpiry = null;
                }
                else if (policy.UnlockExpiry.HasValue)
                {
                    _unlockExpiry = policy.UnlockExpiry.Value > DateTime.UtcNow
                        ? policy.UnlockExpiry
                        : null;
                }
                else
                {
                    _unlockExpiry = DateTime.MaxValue;
                }

            }

            // Never execute WMI or pnputil on SignalR's receive thread.
            _monitor.RequestScan();
        }

        public bool IsExplicitlyAllowed(string id)
        {
            lock (_lock) return DeviceMatcher.Matches(id, _allowedDevices);
        }

        public bool IsExplicitlyBlocked(string id)
        {
            lock (_lock) return DeviceMatcher.Matches(id, _blockedDevices);
        }

        public int BlockedCount()
        {
            lock (_lock) return _blockedDevices.Count;
        }

        public void Enforce()
        {
            // SignalR policy pushes and the worker loop can call Enforce concurrently.
            // Serialize the hardware pass, while keeping the policy lock out of all
            // WMI and subprocess work.
            lock (_enforcementLock)
            {
                long scannedRevision;
                lock (_lock) scannedRevision = _revision;
                _scanFailed = !_managedStateReadable;
                EnforceCore();
                lock (_lock)
                {
                    _enforcementUtc = DateTime.UtcNow;
                    _appliedRevision = _scanFailed ? null : scannedRevision;
                }
            }
        }

        public void RecordPolicyError(string error) { lock (_lock) _policyError = error; }

        public (long? Revision, DateTime? CheckedUtc, string Error) GetEnforcementStatus()
        {
            lock (_lock) return (_appliedRevision, _enforcementUtc,
                !string.IsNullOrEmpty(_policyError) ? _policyError :
                _appliedRevision == null ? "Enforcement incomplete; check Agent errors." : string.Empty);
        }

        public bool RestoreManagedDevices()
        {
            lock (_enforcementLock)
            {
                if (!_managedStateReadable) return false;
                List<string> managed;
                lock (_lock) managed = _devicesDisabledByAgent.ToList();

                foreach (string deviceId in managed)
                {
                    if (RunPnPUtil("enable-device", deviceId))
                    {
                        RecordManagedState(deviceId, disabled: false);
                    }
                }

                int failed = ManagedDeviceCount();
                if (managed.Count > 0)
                {
                    _logger.LogInformation(
                        "Managed-device recovery completed. Restored={Restored}, Failed={Failed}.",
                        managed.Count - failed,
                        failed);
                }
                return failed == 0;
            }
        }

        private int ManagedDeviceCount()
        {
            lock (_lock) return _devicesDisabledByAgent.Count;
        }

        private void EnforceCore()
        {
            List<string> allowed;
            List<string> blocked;
            bool unlocked;
            bool learn;
            bool optical;
            bool bluetooth;
            bool wifi;
            bool badUsb;
            HashSet<string> managedDevices;

            lock (_lock)
            {
                unlocked = IsUnlockedCore();
                learn = IsLearnModeActiveCore();
                optical = _opticalBlocked;
                bluetooth = _bluetoothBlocked;
                wifi = _wifiBlocked;
                badUsb = _badUsbBlock;
                allowed = new List<string>(_allowedDevices);
                blocked = new List<string>(_blockedDevices);
                managedDevices = new HashSet<string>(
                    _devicesDisabledByAgent,
                    StringComparer.OrdinalIgnoreCase);
            }

            var actions = new Dictionary<string, DeviceAction>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var wql in RemovableDeviceQueries)
            {
                try
                {
                    foreach (var device in _devices.Query(wql))
                    {
                        string id = device["PNPDeviceID"]?.ToString() ?? string.Empty;
                        if (string.IsNullOrEmpty(id) || IsHumanInterfaceDevice(id) ||
                            IsInputOrSharedParent(device) || !IsRemovableDevice(device)) continue;

                        bool shouldAllow = ShouldAllow(id, unlocked, learn, allowed, blocked);
                        bool isRunning = IsDeviceRunning(device);
                        string name = device["Name"]?.ToString() ?? "Unauthorized USB device";

                        ApplyPlannedAction(
                            actions,
                            ManagedDeviceStatePlanner.Decide(
                                shouldBeBlocked: !shouldAllow,
                                isRunning,
                                managedDevices.Contains(id)),
                            id,
                            name,
                            alertOnDisable: true);
                    }
                }
                catch (Exception ex)
                {
                    _scanFailed = true;
                    _logger.LogError(ex, "Enforcement WMI query failed: {Query}", wql);
                }
            }

            CollectSector(optical, OpticalQuery, actions, managedDevices);
            CollectSector(bluetooth, BluetoothQuery, actions, managedDevices);
            CollectSector(wifi, WifiQuery, actions, managedDevices);

            foreach (var action in actions.Values)
            {
                if (action.Verb == "disable-device" && action.TrackManagedState &&
                    !RecordManagedState(action.Id, disabled: true))
                {
                    _scanFailed = true;
                    QueueSecurityEvent(new SecurityEvent("BlockFailed", action.Id,
                        action.DeviceName, "Managed-device journal could not be persisted; action skipped"));
                    continue;
                }
                bool succeeded = RunPnPUtilWithBackoff(
                    action.Verb,
                    action.Id,
                    out bool attempted);
                if (!succeeded)
                {
                    _scanFailed = true;
                    if (attempted && action.AlertOnSuccess)
                    {
                        QueueSecurityEvent(new SecurityEvent(
                            "BlockFailed",
                            action.Id,
                            action.DeviceName,
                            "pnputil failed; retry delayed for one minute"));
                    }
                    continue;
                }

                if (action.TrackManagedState && action.Verb == "enable-device")
                    RecordManagedState(
                        action.Id,
                        action.Verb == "disable-device");
                if (action.AlertOnSuccess)
                {
                    WriteBlockAlert(action.DeviceName, action.Id);
                    QueueSecurityEvent(new SecurityEvent(
                        "Blocked",
                        action.Id,
                        action.DeviceName,
                        "device disabled by pnputil"));
                }
            }

            CheckBadUsb(badUsb, allowed);
        }

        private static bool ShouldAllow(
            string id,
            bool unlocked,
            bool learn,
            IEnumerable<string> allowed,
            IEnumerable<string> blocked)
        {
            // An unlocked sector means unrestricted removable-device access without
            // destroying its saved allow/block rules. Those rules take effect again
            // when the sector is locked.
            if (learn || unlocked) return true;
            if (DeviceMatcher.Matches(id, blocked)) return false;
            return DeviceMatcher.Matches(id, allowed);
        }

        private void WriteBlockAlert(string deviceName, string deviceId)
        {
            try
            {
                Directory.CreateDirectory(AlertDir);
                if (OperatingSystem.IsWindows()) WindowsFileSecurity.RestrictRuntimeDirectory(AlertDir);
                foreach (var file in Directory.GetFiles(AlertDir, "*.json"))
                {
                    try
                    {
                        if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(file)).TotalMinutes > 2)
                            File.Delete(file);
                    }
                    catch { }
                }

                var payload = JsonSerializer.Serialize(new
                {
                    DeviceName = deviceName,
                    DeviceId = deviceId,
                    Timestamp = DateTime.UtcNow
                });
                var finalPath = Path.Combine(AlertDir, $"alert-{Guid.NewGuid():N}.json");
                var tempPath = finalPath + ".tmp";
                File.WriteAllText(tempPath, payload);
                File.Move(tempPath, finalPath);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to write endpoint block alert.");
            }
        }

        private void CheckBadUsb(bool block, List<string> allowed)
        {
            var current = new List<(string id, string name, bool isRunning)>();
            try
            {
                foreach (var device in _devices.Query(KeyboardQuery))
                {
                    string id = device["PNPDeviceID"]?.ToString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(id))
                        current.Add((
                            id,
                            device["Name"]?.ToString() ?? "Keyboard",
                            IsDeviceRunning(device)));
                }
            }
            catch (Exception ex)
            {
                _scanFailed = true;
                _logger.LogError(ex, "Keyboard enumeration failed.");
                return;
            }

            var keyboards =
                new List<(string id, string name, bool isRunning, bool arrival,
                    bool trusted, bool allowed, bool managed)>();
            lock (_lock)
            {
                bool initializingBaseline = !_keyboardBaselineDone;
                if (initializingBaseline)
                {
                    foreach (var (id, _, _) in current)
                    {
                        if (!_devicesDisabledByAgent.Contains(id))
                            _trustedKeyboards.Add(id);
                    }
                    _keyboardBaselineDone = true;
                }

                foreach (var (id, name, isRunning) in current)
                {
                    keyboards.Add((
                        id,
                        name,
                        isRunning,
                        !initializingBaseline && !_presentKeyboards.Contains(id),
                        _trustedKeyboards.Contains(id),
                        DeviceMatcher.Matches(id, allowed),
                        _devicesDisabledByAgent.Contains(id)));
                }

                _presentKeyboards.Clear();
                foreach (var (id, _, _) in current) _presentKeyboards.Add(id);
            }

            foreach (var keyboard in keyboards)
            {
                bool shouldBlock =
                    block && !keyboard.trusted && !keyboard.allowed;
                var action = ManagedDeviceStatePlanner.Decide(
                    shouldBlock,
                    keyboard.isRunning,
                    keyboard.managed);

                if (action == ManagedDeviceAction.Disable)
                {
                    if (!RecordManagedState(keyboard.id, disabled: true))
                    {
                        _scanFailed = true;
                        QueueSecurityEvent(new SecurityEvent("BadUSBBlockFailed", keyboard.id,
                            keyboard.name, "Managed-device journal could not be persisted"));
                        continue;
                    }
                    bool disabled = RunPnPUtilWithBackoff(
                        "disable-device",
                        keyboard.id,
                        out bool attempted);
                    if (!disabled) _scanFailed = true;
                    if (disabled || attempted)
                    {
                        QueueSecurityEvent(new SecurityEvent(
                            disabled ? "BadUSBBlocked" : "BadUSBBlockFailed",
                            keyboard.id,
                            keyboard.name,
                            disabled
                                ? "unapproved keyboard disabled"
                                : "pnputil failed; retry delayed for one minute"));
                    }
                }
                else if (action == ManagedDeviceAction.Enable)
                {
                    bool enabled = RunPnPUtilWithBackoff(
                        "enable-device",
                        keyboard.id,
                        out bool attempted);
                    if (!enabled) _scanFailed = true;
                    if (enabled)
                    {
                        RecordManagedState(keyboard.id, disabled: false);
                        QueueSecurityEvent(new SecurityEvent(
                            "BadUSBRestored",
                            keyboard.id,
                            keyboard.name,
                            "keyboard restored by policy"));
                    }
                    else if (attempted)
                    {
                        QueueSecurityEvent(new SecurityEvent(
                            "BadUSBRestoreFailed",
                            keyboard.id,
                            keyboard.name,
                            "pnputil failed; retry delayed for one minute"));
                    }
                }
                else if (action == ManagedDeviceAction.ClearOwnership)
                {
                    RecordManagedState(keyboard.id, disabled: false);
                }
                else if (keyboard.arrival)
                {
                    QueueSecurityEvent(new SecurityEvent(
                        "KeyboardAdded",
                        keyboard.id,
                        keyboard.name,
                        keyboard.trusted || keyboard.allowed ? "authorized" : "review"));
                }
            }
        }

        public List<SecurityEvent> PeekSecurityEvents(int limit = 100) =>
            _securityEventOutbox.Peek(limit);

        public bool AcknowledgeSecurityEvent(Guid eventId) =>
            _securityEventOutbox.Acknowledge(eventId);

        private void QueueSecurityEvent(SecurityEvent securityEvent)
        {
            try
            {
                _securityEventOutbox.Enqueue(securityEvent);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Could not queue security event {Type}; enforcement continues.",
                    securityEvent.Type);
            }
        }

        private void CollectSector(
            bool block,
            string wql,
            Dictionary<string, DeviceAction> actions,
            HashSet<string> managedDevices)
        {
            try
            {
                foreach (var device in _devices.Query(wql))
                {
                    string id = device["PNPDeviceID"]?.ToString() ?? string.Empty;
                    if (string.IsNullOrEmpty(id) || IsHumanInterfaceDevice(id) || IsInputOrSharedParent(device)) continue;

                    bool isRunning = IsDeviceRunning(device);
                    ApplyPlannedAction(
                        actions,
                        ManagedDeviceStatePlanner.Decide(
                            shouldBeBlocked: block,
                            isRunning,
                            managedDevices.Contains(id)),
                        id,
                        string.Empty,
                        alertOnDisable: false);
                }
            }
            catch (Exception ex)
            {
                _scanFailed = true;
                _logger.LogError(ex, "Sector WMI query failed: {Query}", wql);
            }
        }

        private void ApplyPlannedAction(
            Dictionary<string, DeviceAction> actions,
            ManagedDeviceAction action,
            string id,
            string deviceName,
            bool alertOnDisable)
        {
            switch (action)
            {
                case ManagedDeviceAction.Disable:
                    actions[id] = new DeviceAction(
                        id,
                        "disable-device",
                        deviceName,
                        alertOnDisable,
                        TrackManagedState: true);
                    break;
                case ManagedDeviceAction.Enable:
                    actions[id] = new DeviceAction(
                        id,
                        "enable-device",
                        deviceName,
                        AlertOnSuccess: false,
                        TrackManagedState: true);
                    break;
                case ManagedDeviceAction.ClearOwnership:
                    RecordManagedState(id, disabled: false);
                    break;
            }
        }

        private static bool IsDeviceRunning(DeviceSnapshot device)
        {
            try
            {
                if (device["ConfigManagerErrorCode"] != null)
                    return Convert.ToUInt32(device["ConfigManagerErrorCode"]) == 0;
            }
            catch { }

            return string.Equals(
                device["Status"]?.ToString(),
                "OK",
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsHumanInterfaceDevice(string pnpDeviceId)
        {
            var id = pnpDeviceId.ToUpperInvariant();
            return id.StartsWith("HID\\") || id.Contains("\\HID") || id.Contains("&HID");
        }

        private static bool IsInputOrSharedParent(DeviceSnapshot device)
        {
            string pnpClass = device["PNPClass"]?.ToString() ?? string.Empty;
            string service = device["Service"]?.ToString() ?? string.Empty;
            return pnpClass.Equals("Keyboard", StringComparison.OrdinalIgnoreCase) ||
                pnpClass.Equals("Mouse", StringComparison.OrdinalIgnoreCase) ||
                pnpClass.Equals("HIDClass", StringComparison.OrdinalIgnoreCase) ||
                service.Equals("usbccgp", StringComparison.OrdinalIgnoreCase) ||
                service.Equals("USBHUB", StringComparison.OrdinalIgnoreCase) ||
                service.Equals("USBHUB3", StringComparison.OrdinalIgnoreCase) ||
                service.Equals("USBXHCI", StringComparison.OrdinalIgnoreCase) ||
                service.Equals("USBEHCI", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsRemovableDevice(DeviceSnapshot device)
        {
            string pnpClass = device["PNPClass"]?.ToString() ?? string.Empty;
            string service = device["Service"]?.ToString() ?? string.Empty;
            string id = device["PNPDeviceID"]?.ToString() ?? string.Empty;
            if (pnpClass.Equals("Camera", StringComparison.OrdinalIgnoreCase) ||
                pnpClass.Equals("Media", StringComparison.OrdinalIgnoreCase) ||
                service.Equals("usbvideo", StringComparison.OrdinalIgnoreCase) ||
                service.StartsWith("usbaudio", StringComparison.OrdinalIgnoreCase)) return false;
            return pnpClass.Equals("WPD", StringComparison.OrdinalIgnoreCase) ||
                pnpClass.Equals("PortableDevice", StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith("USBSTOR\\", StringComparison.OrdinalIgnoreCase) ||
                service.Equals("USBSTOR", StringComparison.OrdinalIgnoreCase) ||
                service.Equals("UASPStor", StringComparison.OrdinalIgnoreCase);
        }

        public List<UsbDeviceInfo> GetConnectedDevices()
        {
            List<string> allowed;
            List<string> blocked;
            bool unlocked;
            bool learn;
            lock (_lock)
            {
                unlocked = IsUnlockedCore();
                learn = IsLearnModeActiveCore();
                allowed = new List<string>(_allowedDevices);
                blocked = new List<string>(_blockedDevices);
            }

            var list = new List<UsbDeviceInfo>();

            QueryAndAppend(list, "SELECT * FROM Win32_USBHub", device =>
            {
                string id = device["DeviceID"]?.ToString() ?? "Unknown";
                return new UsbDeviceInfo
                {
                    DeviceID = id,
                    Name = device["Name"]?.ToString() ?? "USB Hub",
                    Status = device["Status"]?.ToString() ?? "Unknown",
                    IsAllowed = IsDeviceRunning(device),
                    IsHub = true
                };
            });

            QueryAndAppend(list, "SELECT * FROM Win32_USBController", device =>
            {
                string id = device["DeviceID"]?.ToString() ?? "Unknown";
                if (list.Any(item => item.IsHub &&
                    string.Equals(item.DeviceID, id, StringComparison.OrdinalIgnoreCase)))
                    return null;

                return new UsbDeviceInfo
                {
                    DeviceID = id,
                    Name = device["Name"]?.ToString() ?? "USB Controller",
                    Status = device["Status"]?.ToString() ?? "Unknown",
                    IsAllowed = IsDeviceRunning(device),
                    IsHub = true
                };
            });

            QueryAndAppend(list, UsbDeviceQueries.Storage,
                device => MapEndpointDevice(device, list, unlocked, learn, allowed, blocked, "USB Storage Device"));
            QueryAndAppend(list, UsbDeviceQueries.Portable,
                device => MapEndpointDevice(device, list, unlocked, learn, allowed, blocked, "Portable Device"));
            return list;
        }

        private static UsbDeviceInfo? MapEndpointDevice(
            DeviceSnapshot device,
            List<UsbDeviceInfo> existing,
            bool unlocked,
            bool learn,
            List<string> allowed,
            List<string> blocked,
            string fallbackName)
        {
            string id = device["PNPDeviceID"]?.ToString() ?? "Unknown";
            if (IsHumanInterfaceDevice(id) || IsInputOrSharedParent(device) || !IsRemovableDevice(device)) return null;
            if (existing.Any(item => !item.IsHub &&
                string.Equals(item.DeviceID, id, StringComparison.OrdinalIgnoreCase)))
                return null;

            string name = device["Name"]?.ToString() ?? fallbackName;
            bool running = IsDeviceRunning(device);
            bool permitted = ShouldAllow(id, unlocked, learn, allowed, blocked);
            return new UsbDeviceInfo
            {
                DeviceID = id,
                Name = name,
                Status = device["Status"]?.ToString() ?? "Unknown",
                IsAllowed = permitted,
                IsRunning = running,
                EnforcementError = permitted == running ? string.Empty :
                    permitted ? "Allowed by policy, but device is disabled or faulted" : "Blocking requested, but device is still enabled",
                IsHub = false,
                ConnectedDeviceName = name
            };
        }

        private void QueryAndAppend(
            List<UsbDeviceInfo> list,
            string wql,
            Func<DeviceSnapshot, UsbDeviceInfo?> selector)
        {
            try
            {
                foreach (var device in _devices.Query(wql))
                {
                    var info = selector(device);
                    if (info != null) list.Add(info);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Device reporting query failed: {Query}", wql);
            }
        }

        private bool RunPnPUtil(string verb, string deviceId) => _devices.SetState(verb, deviceId);

        private bool RunPnPUtilWithBackoff(
            string verb,
            string deviceId,
            out bool attempted)
        {
            string key = $"{verb}\0{deviceId}";
            DateTime now = DateTime.UtcNow;
            if (_actionRetryAfterUtc.TryGetValue(key, out var retryAfter) &&
                now < retryAfter)
            {
                attempted = false;
                return false;
            }

            attempted = true;
            bool succeeded = RunPnPUtil(verb, deviceId);
            if (succeeded)
            {
                _actionRetryAfterUtc.Remove(key);
            }
            else
            {
                _actionRetryAfterUtc[key] = now.AddMinutes(1);
                if (_actionRetryAfterUtc.Count > 4_096)
                {
                    foreach (string expired in _actionRetryAfterUtc
                        .Where(pair => pair.Value <= now)
                        .Select(pair => pair.Key)
                        .ToList())
                    {
                        _actionRetryAfterUtc.Remove(expired);
                    }

                    int excess = _actionRetryAfterUtc.Count - 4_096;
                    if (excess > 0)
                    {
                        foreach (string oldest in _actionRetryAfterUtc
                            .OrderBy(pair => pair.Value)
                            .Take(excess)
                            .Select(pair => pair.Key)
                            .ToList())
                        {
                            _actionRetryAfterUtc.Remove(oldest);
                        }
                    }
                }
            }

            return succeeded;
        }

        private void LoadCachedPolicy()
        {
            if (!File.Exists(PolicyCachePath)) return;

            try
            {
                WindowsFileSecurity.RestrictExistingFileToSystemAndAdministrators(
                    PolicyCachePath);
                var policy = JsonSerializer.Deserialize<MachinePolicies>(
                    File.ReadAllText(PolicyCachePath));
                if (policy == null) return;

                lock (_lock)
                {
                    _revision = policy.Revision;
                    _allowedDevices = new List<string>(policy.AllowedDevices ?? new());
                    _blockedDevices = new List<string>(policy.BlockedDevices ?? new());
                    _learnMode = policy.LearnMode;
                    _learnModeExpiry = policy.LearnModeExpiry;
                    _opticalBlocked = policy.OpticalBlocked;
                    _bluetoothBlocked = policy.BluetoothBlocked;
                    _wifiBlocked = policy.WifiBlocked;
                    _badUsbBlock = policy.BadUsbBlock;
                    _unlockExpiry = !policy.IsUnlocked
                        ? null
                        : policy.UnlockExpiry ?? DateTime.MaxValue;
                }

                _logger.LogInformation("Loaded cached endpoint policy for offline enforcement.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ignoring unreadable cached endpoint policy.");
            }
        }

        private void SaveCachedPolicy(MachinePolicies policy)
        {
            try
            {
                var parent = Path.GetDirectoryName(PolicyCachePath);
                if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);

                WindowsFileSecurity.WriteAllTextAtomically(
                    PolicyCachePath,
                    JsonSerializer.Serialize(
                        policy,
                        new JsonSerializerOptions { WriteIndented = true }),
                    restrictToSystemAndAdministrators: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not persist endpoint policy cache.");
                throw;
            }
        }

        private void LoadManagedState()
        {
            if (!File.Exists(ManagedStatePath)) return;

            try
            {
                WindowsFileSecurity.RestrictExistingFileToSystemAndAdministrators(
                    ManagedStatePath);
                var deviceIds = JsonSerializer.Deserialize<List<string>>(
                    File.ReadAllText(ManagedStatePath)) ?? throw new JsonException("Managed state is empty.");
                if (deviceIds.Count > 4096 || deviceIds.Any(string.IsNullOrWhiteSpace))
                    throw new JsonException("Managed state contains invalid device identities.");
                lock (_lock)
                {
                    foreach (var id in deviceIds
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Take(4096))
                    {
                        _devicesDisabledByAgent.Add(id);
                    }
                }
            }
            catch (Exception ex)
            {
                _managedStateReadable = false;
                _logger.LogError(ex,
                    "Managed-device state is unreadable; preserving it and refusing new hardware disables until repaired.");
            }
        }

        private bool RecordManagedState(string deviceId, bool disabled)
        {
            lock (_lock)
            {
                if (!_managedStateReadable) { _scanFailed = true; return false; }
                if (_devicesDisabledByAgent.Contains(deviceId) == disabled) return true;
                if (disabled && _devicesDisabledByAgent.Count >= 4096) { _scanFailed = true; return false; }
                var candidate = new HashSet<string>(_devicesDisabledByAgent, StringComparer.OrdinalIgnoreCase);
                if (disabled) candidate.Add(deviceId); else candidate.Remove(deviceId);
                try
                {
                    var parent = Path.GetDirectoryName(ManagedStatePath);
                    if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
                    WindowsFileSecurity.WriteAllTextAtomically(ManagedStatePath,
                        JsonSerializer.Serialize(candidate), restrictToSystemAndAdministrators: true);
                    if (disabled) _devicesDisabledByAgent.Add(deviceId); else _devicesDisabledByAgent.Remove(deviceId);
                    return true;
                }
                catch (Exception ex)
                {
                    _scanFailed = true;
                    _logger.LogError(ex, "Could not persist managed-device state; ownership remains recoverable.");
                    return false;
                }
            }
        }

        private MachinePolicies SnapshotPolicyCore() => new()
        {
            Revision = _revision,
            AllowedDevices = new List<string>(_allowedDevices),
            BlockedDevices = new List<string>(_blockedDevices),
            IsUnlocked = _unlockExpiry.HasValue,
            UnlockExpiry = _unlockExpiry == DateTime.MaxValue ? null : _unlockExpiry,
            LearnMode = _learnMode,
            LearnModeExpiry = _learnModeExpiry,
            OpticalBlocked = _opticalBlocked,
            BluetoothBlocked = _bluetoothBlocked,
            WifiBlocked = _wifiBlocked,
            BadUsbBlock = _badUsbBlock
        };

        private sealed record DeviceAction(
            string Id,
            string Verb,
            string DeviceName,
            bool AlertOnSuccess,
            bool TrackManagedState = false);
    }
}
