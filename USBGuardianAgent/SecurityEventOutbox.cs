using System.Text.Json;
using USBGuardian.Shared;

namespace USBGuardianAgent;

public sealed record SecurityEvent(
    string Type,
    string DeviceId,
    string DeviceName,
    string Detail)
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
}

public sealed class SecurityEventOutbox
{
    private const int MaximumEvents = 5_000;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly List<SecurityEvent> _events = new();
    private readonly object _lock = new();
    private readonly string _filePath;
    private readonly ILogger<SecurityEventOutbox> _logger;

    public SecurityEventOutbox(
        IConfiguration configuration,
        ILogger<SecurityEventOutbox> logger)
    {
        _logger = logger;
        _filePath = configuration["SecurityEventQueuePath"]
            ?? @"C:\ProgramData\USBGuardian\agent_event_queue.json";

        string? parent = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
        WindowsFileSecurity.RestrictExistingFileToSystemAndAdministrators(_filePath);
        Load();
    }

    public void Enqueue(SecurityEvent securityEvent)
    {
        ArgumentNullException.ThrowIfNull(securityEvent);
        securityEvent = Sanitize(securityEvent);

        lock (_lock)
        {
            var previous = new List<SecurityEvent>(_events);
            try
            {
                if (_events.Count >= MaximumEvents)
                {
                    int removeCount = _events.Count - MaximumEvents + 1;
                    _events.RemoveRange(0, removeCount);
                    _logger.LogWarning(
                        "Security-event outbox reached {Maximum}; dropped {Count} oldest event(s).",
                        MaximumEvents,
                        removeCount);
                }

                _events.Add(securityEvent);
                PersistUnsafe();
            }
            catch
            {
                _events.Clear();
                _events.AddRange(previous);
                throw;
            }
        }
    }

    public List<SecurityEvent> Peek(int limit = 100)
    {
        limit = Math.Clamp(limit, 1, 1_000);
        lock (_lock)
            return _events.Take(limit).Select(Clone).ToList();
    }

    public bool Acknowledge(Guid eventId)
    {
        if (eventId == Guid.Empty) return false;

        lock (_lock)
        {
            int index = _events.FindIndex(item => item.EventId == eventId);
            if (index < 0) return false;
            var removed = _events[index];
            _events.RemoveAt(index);
            try
            {
                PersistUnsafe();
                return true;
            }
            catch
            {
                _events.Insert(index, removed);
                throw;
            }
        }
    }

    private void Load()
    {
        if (!File.Exists(_filePath)) return;

        try
        {
            var loaded = JsonSerializer.Deserialize<List<SecurityEvent>>(
                File.ReadAllText(_filePath));
            if (loaded == null) return;

            foreach (var securityEvent in loaded
                .Where(item => item != null)
                .TakeLast(MaximumEvents))
            {
                _events.Add(Sanitize(securityEvent));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Could not load security-event outbox {File}. Existing file was left untouched.",
                _filePath);
        }
    }

    private void PersistUnsafe()
    {
        try
        {
            WindowsFileSecurity.WriteAllTextAtomically(
                _filePath,
                JsonSerializer.Serialize(_events, JsonOptions),
                restrictToSystemAndAdministrators: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Could not persist security-event outbox {File}.",
                _filePath);
            throw;
        }
    }

    private static SecurityEvent Sanitize(SecurityEvent securityEvent)
    {
        DateTime created = securityEvent.CreatedUtc == default
            ? DateTime.UtcNow
            : securityEvent.CreatedUtc.ToUniversalTime();
        if (created > DateTime.UtcNow.AddMinutes(5))
            created = DateTime.UtcNow;

        return new SecurityEvent(
            Clean(securityEvent.Type, 64, "AgentEvent"),
            Clean(securityEvent.DeviceId, 512, string.Empty),
            Clean(securityEvent.DeviceName, 256, string.Empty),
            Clean(securityEvent.Detail, 1024, string.Empty))
        {
            EventId = securityEvent.EventId == Guid.Empty
                ? Guid.NewGuid()
                : securityEvent.EventId,
            CreatedUtc = created
        };
    }

    private static SecurityEvent Clone(SecurityEvent securityEvent) =>
        securityEvent with { };

    private static string Clean(
        string? value,
        int maximumLength,
        string fallback)
    {
        value = (value ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        if (value.Length == 0) return fallback;
        return value.Length <= maximumLength
            ? value
            : value[..maximumLength];
    }
}
