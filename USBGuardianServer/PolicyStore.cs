using System.Text.Json;
using USBGuardian.Shared;

namespace USBGuardianServer
{
    public class AgentInfo
    {
        public string MachineId { get; set; } = string.Empty;
        public string MachineName { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public string ConnectionId { get; set; } = string.Empty;
        public bool IsLocked { get; set; } = true;
        public DateTime LastSeen { get; set; } = DateTime.UtcNow;
        public List<UsbDeviceDto> Devices { get; set; } = new();
        public long? AppliedRevision { get; set; }
        public DateTime? EnforcementUtc { get; set; }
        public string EnforcementError { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonIgnore]
        public string CredentialBinding { get; set; } = string.Empty;
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

    public class MachinePolicies
    {
        public long Revision { get; set; }
        public List<string> AllowedDevices { get; set; } = new();
        public List<string> BlockedDevices { get; set; } = new();
        public bool IsUnlocked { get; set; }
        public DateTime? UnlockExpiry { get; set; }
        public string Group { get; set; } = "Default";
        public bool OpticalBlocked { get; set; }
        public bool BluetoothBlocked { get; set; }
        public bool WifiBlocked { get; set; }
        public bool BadUsbBlock { get; set; }
        public bool LearnMode { get; set; }
        public DateTime? LearnModeExpiry { get; set; }

        public bool IsEffectivelyUnlocked(DateTime utcNow) =>
            IsUnlocked && (!UnlockExpiry.HasValue || utcNow < UnlockExpiry.Value);
    }

    public class PolicyStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
        private readonly string _filePath;
        private readonly Dictionary<string, MachinePolicies> _policies =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();
        private readonly ILogger<PolicyStore> _logger;

        public PolicyStore(IConfiguration config, ILogger<PolicyStore> logger)
        {
            _logger = logger;
            _filePath = config["PolicyFilePath"]
                ?? @"C:\ProgramData\USBGuardian\server_policies.json";
            EnsureParentDirectory(_filePath);
            WindowsFileSecurity.RestrictExistingFileToSystemAndAdministrators(
                _filePath);
            Load();
        }

        public MachinePolicies GetOrCreate(string machineId) =>
            GetOrCreate(machineId, out _);

        public MachinePolicies GetOrCreate(string machineId, out bool normalized)
        {
            machineId = ValidateMachineId(machineId);
            lock (_lock)
            {
                if (!_policies.TryGetValue(machineId, out var policy))
                {
                    policy = new MachinePolicies();
                    var candidate = new Dictionary<string, MachinePolicies>(_policies, StringComparer.OrdinalIgnoreCase)
                    {
                        [machineId] = policy
                    };
                    normalized = false;
                    PersistUnsafe(candidate);
                    _policies[machineId] = policy;
                    return Clone(policy);
                }

                policy = Clone(policy);
                normalized = Normalize(policy, DateTime.UtcNow);
                if (normalized)
                {
                    var candidate = new Dictionary<string, MachinePolicies>(_policies, StringComparer.OrdinalIgnoreCase)
                    {
                        [machineId] = policy
                    };
                    PersistUnsafe(candidate);
                    _policies[machineId] = policy;
                }
                return Clone(policy);
            }
        }

        public MachinePolicies Update(string machineId, Action<MachinePolicies> update)
        {
            machineId = ValidateMachineId(machineId);
            ArgumentNullException.ThrowIfNull(update);

            lock (_lock)
            {
                var policy = _policies.TryGetValue(machineId, out var existing)
                    ? Clone(existing)
                    : new MachinePolicies();

                Normalize(policy, DateTime.UtcNow);
                update(policy);
                Normalize(policy, DateTime.UtcNow);
                Sanitize(policy);
                policy.Revision = checked(policy.Revision + 1);

                var candidate = new Dictionary<string, MachinePolicies>(_policies, StringComparer.OrdinalIgnoreCase)
                {
                    [machineId] = policy
                };
                PersistUnsafe(candidate);
                _policies[machineId] = policy;
                return Clone(policy);
            }
        }

        public Dictionary<string, MachinePolicies> Snapshot()
        {
            lock (_lock)
            {
                bool changed = false;
                var now = DateTime.UtcNow;
                var candidate = _policies.ToDictionary(pair => pair.Key, pair => Clone(pair.Value), StringComparer.OrdinalIgnoreCase);
                foreach (var policy in candidate.Values)
                    changed |= Normalize(policy, now);
                if (changed)
                {
                    PersistUnsafe(candidate);
                    foreach (var pair in candidate) _policies[pair.Key] = pair.Value;
                }

                return _policies.ToDictionary(
                    pair => pair.Key,
                    pair => Clone(pair.Value),
                    StringComparer.OrdinalIgnoreCase);
            }
        }

        private void Load()
        {
            if (!File.Exists(_filePath)) return;

            try
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, MachinePolicies>>(
                    File.ReadAllText(_filePath));
                if (loaded == null) return;

                foreach (var (machineId, policy) in loaded)
                {
                    if (string.IsNullOrWhiteSpace(machineId) || policy == null) continue;
                    Sanitize(policy);
                    Normalize(policy, DateTime.UtcNow);
                    _policies[machineId.Trim()] = policy;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Could not load policy file {File}. Existing file was left untouched.", _filePath);
            }
        }

        private void PersistUnsafe(Dictionary<string, MachinePolicies> policies)
        {
            try
            {
                WindowsFileSecurity.WriteAllTextAtomically(
                    _filePath,
                    JsonSerializer.Serialize(policies, JsonOptions),
                    restrictToSystemAndAdministrators: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not persist policy file {File}.", _filePath);
                throw;
            }
        }

        private static bool Normalize(MachinePolicies policy, DateTime utcNow)
        {
            bool changed = false;

            if (policy.IsUnlocked && policy.UnlockExpiry.HasValue &&
                utcNow >= policy.UnlockExpiry.Value)
            {
                policy.IsUnlocked = false;
                policy.UnlockExpiry = null;
                changed = true;
            }
            else if (!policy.IsUnlocked && policy.UnlockExpiry.HasValue)
            {
                policy.UnlockExpiry = null;
                changed = true;
            }

            // Learn mode is intentionally fail-closed. A missing or elapsed expiry
            // disables it instead of leaving an endpoint permissive indefinitely.
            if (policy.LearnMode &&
                (!policy.LearnModeExpiry.HasValue || utcNow >= policy.LearnModeExpiry.Value))
            {
                policy.LearnMode = false;
                policy.LearnModeExpiry = null;
                changed = true;
            }
            else if (!policy.LearnMode && policy.LearnModeExpiry.HasValue)
            {
                policy.LearnModeExpiry = null;
                changed = true;
            }

            if (changed) policy.Revision = checked(policy.Revision + 1);
            return changed;
        }

        private static void Sanitize(MachinePolicies policy)
        {
            var group = string.IsNullOrWhiteSpace(policy.Group) ? "Default" : policy.Group.Trim();
            policy.Group = group[..Math.Min(group.Length, 100)];
            policy.AllowedDevices = SanitizeDeviceList(policy.AllowedDevices);
            policy.BlockedDevices = SanitizeDeviceList(policy.BlockedDevices);
        }

        private static List<string> SanitizeDeviceList(IEnumerable<string>? entries) =>
            (entries ?? Array.Empty<string>())
                .Where(entry => !string.IsNullOrWhiteSpace(entry))
                .Select(entry => entry.Trim())
                .Where(entry => entry.Length <= 512)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(4096)
                .ToList();

        private static MachinePolicies Clone(MachinePolicies policy) => new()
        {
            Revision = policy.Revision,
            AllowedDevices = new List<string>(policy.AllowedDevices ?? new()),
            BlockedDevices = new List<string>(policy.BlockedDevices ?? new()),
            IsUnlocked = policy.IsUnlocked,
            UnlockExpiry = policy.UnlockExpiry,
            Group = policy.Group,
            OpticalBlocked = policy.OpticalBlocked,
            BluetoothBlocked = policy.BluetoothBlocked,
            WifiBlocked = policy.WifiBlocked,
            BadUsbBlock = policy.BadUsbBlock,
            LearnMode = policy.LearnMode,
            LearnModeExpiry = policy.LearnModeExpiry
        };

        private static string ValidateMachineId(string machineId)
        {
            if (string.IsNullOrWhiteSpace(machineId))
                throw new ArgumentException("Machine ID is required.", nameof(machineId));

            machineId = machineId.Trim();
            if (machineId.Length > 128)
                throw new ArgumentException("Machine ID is too long.", nameof(machineId));
            return machineId;
        }

        private static void EnsureParentDirectory(string path)
        {
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
        }
    }
}
