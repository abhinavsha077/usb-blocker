using System.Text.Json;

namespace USBGuardianServer
{
    public class AgentInfo
    {
        public string MachineId   { get; set; } = string.Empty;
        public string MachineName { get; set; } = string.Empty;
        public string IpAddress   { get; set; } = string.Empty;
        public string ConnectionId { get; set; } = string.Empty;
        public bool   IsLocked    { get; set; } = true;
        public DateTime LastSeen  { get; set; } = DateTime.UtcNow;
        public List<UsbDeviceDto> Devices { get; set; } = new();
    }

    public class UsbDeviceDto
    {
        public string DeviceID            { get; set; } = string.Empty;
        public string Name                { get; set; } = string.Empty;
        public string Status              { get; set; } = string.Empty;
        public bool   IsAllowed           { get; set; }
        public bool   IsHub               { get; set; }
        public string ConnectedDeviceName { get; set; } = string.Empty;
    }

    public class MachinePolicies
    {
        public List<string> AllowedDevices { get; set; } = new();
        public List<string> BlockedDevices { get; set; } = new();
        public bool IsUnlocked { get; set; } = false;
    }

    public class PolicyStore
    {
        private readonly string _filePath;
        private readonly Dictionary<string, MachinePolicies> _policies = new();
        private readonly object _lock = new();

        public PolicyStore(IConfiguration config)
        {
            _filePath = config["PolicyFilePath"] ?? @"C:\ProgramData\USBGuardian\server_policies.json";
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            Load();
        }

        public MachinePolicies GetOrCreate(string machineId)
        {
            lock (_lock)
            {
                if (!_policies.ContainsKey(machineId))
                    _policies[machineId] = new MachinePolicies();
                return _policies[machineId];
            }
        }

        public void Save(string machineId, MachinePolicies policies)
        {
            lock (_lock)
            {
                _policies[machineId] = policies;
                File.WriteAllText(_filePath, JsonSerializer.Serialize(_policies, new JsonSerializerOptions { WriteIndented = true }));
            }
        }

        private void Load()
        {
            if (!File.Exists(_filePath)) return;
            try
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, MachinePolicies>>(File.ReadAllText(_filePath));
                if (loaded != null)
                    foreach (var kv in loaded)
                        _policies[kv.Key] = kv.Value;
            }
            catch { /* start fresh if corrupt */ }
        }
    }
}
