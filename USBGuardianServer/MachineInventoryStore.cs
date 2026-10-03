using System.Text.Json;
using USBGuardian.Shared;

namespace USBGuardianServer;

public sealed class MachineInventoryRecord
{
    public string MachineId { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public DateTime LastSeenUtc { get; set; }
    public int LastDeviceCount { get; set; }
}

public sealed class MachineInventoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly Dictionary<string, MachineInventoryRecord> _machines =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastCheckpointUtc =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly string _filePath;
    private readonly ILogger<MachineInventoryStore> _logger;

    public MachineInventoryStore(
        IConfiguration configuration,
        ILogger<MachineInventoryStore> logger)
    {
        _logger = logger;
        _filePath = configuration["InventoryFilePath"]
            ?? @"C:\ProgramData\USBGuardian\server_inventory.json";

        string? parent = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
        WindowsFileSecurity.RestrictExistingFileToSystemAndAdministrators(_filePath);
        Load();
    }

    public MachineInventoryRecord Register(
        string machineId,
        string machineName,
        string ipAddress)
    {
        machineId = ValidateMachineId(machineId);
        machineName = Clean(machineName, 128, machineId);
        ipAddress = Clean(ipAddress, 64, string.Empty);

        lock (_lock)
        {
            _machines.TryGetValue(machineId, out var current);
            current ??= new MachineInventoryRecord { MachineId = machineId };

            current.MachineName = machineName;
            current.IpAddress = ipAddress;
            current.LastSeenUtc = DateTime.UtcNow;
            _machines[machineId] = current;

            // Registration is infrequent and is the most important last-seen
            // boundary if the server stops before the first device snapshot.
            PersistUnsafe();
            return Clone(current);
        }
    }

    public void Touch(
        string machineId,
        int? deviceCount = null,
        bool forcePersist = false)
    {
        machineId = ValidateMachineId(machineId);
        lock (_lock)
        {
            if (!_machines.TryGetValue(machineId, out var current))
            {
                current = new MachineInventoryRecord
                {
                    MachineId = machineId,
                    MachineName = machineId
                };
                _machines[machineId] = current;
                forcePersist = true;
            }

            DateTime now = DateTime.UtcNow;
            bool deviceCountChanged =
                deviceCount.HasValue && current.LastDeviceCount != deviceCount.Value;
            bool checkpointDue =
                !_lastCheckpointUtc.TryGetValue(machineId, out var checkpoint) ||
                now - checkpoint >= TimeSpan.FromMinutes(1);

            current.LastSeenUtc = now;
            if (deviceCount.HasValue)
                current.LastDeviceCount = Math.Clamp(deviceCount.Value, 0, 512);

            if (forcePersist || deviceCountChanged || checkpointDue)
                PersistUnsafe();
        }
    }

    public Dictionary<string, MachineInventoryRecord> Snapshot()
    {
        lock (_lock)
        {
            return _machines.ToDictionary(
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
            var loaded =
                JsonSerializer.Deserialize<Dictionary<string, MachineInventoryRecord>>(
                    File.ReadAllText(_filePath));
            if (loaded == null) return;

            DateTime now = DateTime.UtcNow;
            foreach (var pair in loaded.Take(10_000))
            {
                string machineId;
                try
                {
                    machineId = ValidateMachineId(pair.Key);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                var record = pair.Value;
                if (record == null) continue;

                record.MachineId = machineId;
                record.MachineName = Clean(record.MachineName, 128, machineId);
                record.IpAddress = Clean(record.IpAddress, 64, string.Empty);
                record.LastDeviceCount = Math.Clamp(record.LastDeviceCount, 0, 512);
                record.LastSeenUtc = NormalizeTimestamp(record.LastSeenUtc, now);
                _machines[machineId] = record;
                _lastCheckpointUtc[machineId] = record.LastSeenUtc;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Could not load machine inventory {File}. Existing file was left untouched.",
                _filePath);
        }
    }

    private void PersistUnsafe()
    {
        try
        {
            WindowsFileSecurity.WriteAllTextAtomically(
                _filePath,
                JsonSerializer.Serialize(_machines, JsonOptions),
                restrictToSystemAndAdministrators: true);
            foreach (var pair in _machines)
                _lastCheckpointUtc[pair.Key] = pair.Value.LastSeenUtc;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not persist machine inventory {File}.", _filePath);
            throw;
        }
    }

    private static MachineInventoryRecord Clone(MachineInventoryRecord record) => new()
    {
        MachineId = record.MachineId,
        MachineName = record.MachineName,
        IpAddress = record.IpAddress,
        LastSeenUtc = record.LastSeenUtc,
        LastDeviceCount = record.LastDeviceCount
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

    private static string Clean(string? value, int maxLength, string fallback)
    {
        value = (value ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        if (value.Length == 0) return fallback;
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static DateTime NormalizeTimestamp(DateTime timestamp, DateTime now)
    {
        if (timestamp == default) return timestamp;

        timestamp = timestamp.Kind switch
        {
            DateTimeKind.Utc => timestamp,
            DateTimeKind.Local => timestamp.ToUniversalTime(),
            _ => DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)
        };
        return timestamp > now.AddMinutes(5) ? now : timestamp;
    }
}
