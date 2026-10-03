using System.Net;
using System.Net.Mail;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using USBGuardian.Shared;

namespace USBGuardianServer
{
    /// <summary>One audited security event (P1.1). Mirrors USB-Lock-RP's alert log.</summary>
    public class UsbEvent
    {
        public Guid EventId { get; set; }
        public DateTime Timestamp  { get; set; } = DateTime.UtcNow;
        public string   MachineId  { get; set; } = string.Empty;
        public string   EventType  { get; set; } = string.Empty; // Connected / Blocked / Allowed / Locked / Unlocked / LearnOn / LearnOff / LearnCaptured / Authorized / BlockedByAdmin / GroupSet / Remote:* / Group:*
        public string   DeviceId   { get; set; } = string.Empty;
        public string   DeviceName { get; set; } = string.Empty;
        public string   Detail     { get; set; } = string.Empty;
    }

    /// <summary>
    /// Append-only, per-machine event log (JSON-lines under
    /// C:\ProgramData\USBGuardian\events\{machineId}.jsonl), plus two optional sinks (P2.4):
    ///   • CEF/SIEM log file  (Cef:Enabled, Cef:Path)
    ///   • SMTP email alerts on blocked events (Smtp:Enabled, Smtp:Host, ...)
    /// Both sinks are best-effort and never block or break enforcement.
    /// </summary>
    public class EventStore
    {
        private readonly string _dir;
        private readonly object _lock = new();
        private readonly ILogger<EventStore> _logger;
        private static readonly JsonSerializerOptions J = new() { WriteIndented = false };
        private readonly Dictionary<string, EventIdWindow> _eventIds =
            new(StringComparer.OrdinalIgnoreCase);

        // CEF sink
        private readonly bool   _cefEnabled;
        private readonly string _cefPath;

        // Email sink
        private readonly bool   _smtpEnabled;
        private readonly IConfiguration _config;

        public EventStore(IConfiguration config, ILogger<EventStore> logger)
        {
            _config = config;
            _logger = logger;
            _dir = config["EventDir"] ?? @"C:\ProgramData\USBGuardian\events";
            Directory.CreateDirectory(_dir);

            _cefEnabled  = config.GetValue<bool>("Cef:Enabled");
            _cefPath     = config["Cef:Path"] ?? @"C:\ProgramData\USBGuardian\usbguardian_cef.log";
            _smtpEnabled = config.GetValue<bool>("Smtp:Enabled");

            if (_cefEnabled)
            {
                var cefParent = Path.GetDirectoryName(_cefPath);
                if (!string.IsNullOrWhiteSpace(cefParent))
                    Directory.CreateDirectory(cefParent);
            }
        }

        private string PathFor(string machineId)
        {
            string identity = (machineId ?? "unknown").ToUpperInvariant();
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
            return Path.Combine(_dir, "by-identity", hash + ".jsonl");
        }

        private string LegacyPathFor(string machineId)
        {
            var safe = new string((machineId ?? "unknown")
                .Take(128)
                .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'
                    ? c
                    : '_')
                .ToArray())
                .Trim('.');
            if (string.IsNullOrWhiteSpace(safe)) safe = "unknown";
            return Path.Combine(_dir, safe + ".jsonl");
        }

        public bool Append(UsbEvent ev)
        {
            if (ev.Timestamp == default)
                ev.Timestamp = DateTime.UtcNow;
            else if (ev.Timestamp.Kind == DateTimeKind.Local)
                ev.Timestamp = ev.Timestamp.ToUniversalTime();
            else if (ev.Timestamp.Kind == DateTimeKind.Unspecified)
                ev.Timestamp = DateTime.SpecifyKind(
                    ev.Timestamp,
                    DateTimeKind.Utc);
            var file = PathFor(ev.MachineId);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var line = JsonSerializer.Serialize(ev, J);
            bool appended = false;
            lock (_lock)
            {
                if (ev.EventId != Guid.Empty &&
                    EventIdsForUnsafe(file, ev.MachineId).Contains(ev.EventId))
                    return false;

                try
                {
                    WindowsFileSecurity.AppendAllTextProtected(
                        file,
                        line + Environment.NewLine);
                    if (ev.EventId != Guid.Empty)
                        EventIdsForUnsafe(file, ev.MachineId).Add(ev.EventId);
                    TrimIfLarge(file);
                    appended = true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not append event log {File}.", file);
                    // A failed durable append must fail the hub invocation. A duplicate
                    // remains a successful, idempotent receipt (Append returns false).
                    throw new IOException("Security event could not be persisted.", ex);
                }

                if (appended && _cefEnabled) WriteCef(ev);
            }

            // Email is fire-and-forget so a slow SMTP server never stalls the hub thread.
            if (appended && _smtpEnabled && IsBlockEvent(ev.EventType))
                _ = Task.Run(() => SendEmail(ev));
            return appended;
        }

        /// <summary>Most-recent-first list of up to <paramref name="limit"/> events.</summary>
        public List<UsbEvent> GetRecent(string machineId, int limit)
        {
            var file = PathFor(machineId);
            lock (_lock)
            {
                if (limit <= 0) return new();
                try
                {
                    var seen = new HashSet<Guid>();
                    return ReadEvents(LegacyPathFor(machineId), machineId, limit)
                        .Concat(ReadEvents(file, machineId, limit))
                        .OrderByDescending(ev => ev.Timestamp)
                        .Where(ev => ev.EventId == Guid.Empty || seen.Add(ev.EventId))
                        .Take(limit).ToList();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not read history for {MachineId}.", machineId);
                    return new();
                }
            }
        }

        private static IEnumerable<UsbEvent> ReadEvents(string file, string machineId, int limit)
        {
            if (!File.Exists(file)) return Array.Empty<UsbEvent>();
            WindowsFileSecurity.RestrictExistingFileToSystemAndAdministrators(file);
            return File.ReadLines(file).Select(line =>
            {
                try { return JsonSerializer.Deserialize<UsbEvent>(line); }
                catch (JsonException) { return null; }
            }).Where(ev => ev != null && string.Equals(ev.MachineId, machineId, StringComparison.OrdinalIgnoreCase))
                .Select(ev => ev!).TakeLast(limit).ToArray();
        }

        private static bool IsBlockEvent(string t) =>
            t is "Blocked" or "BlockedByAdmin" or "BadUSBBlocked" or
                "BlockFailed" or "BadUSBBlockFailed";

        private EventIdWindow EventIdsForUnsafe(string file, string machineId)
        {
            if (_eventIds.TryGetValue(file, out var existing)) return existing;

            var loaded = new EventIdWindow();
            try
            {
                foreach (var ev in ReadEvents(LegacyPathFor(machineId), machineId, 5_000)
                    .Concat(ReadEvents(file, machineId, 5_000)).TakeLast(5_000))
                    loaded.Add(ev.EventId);
            }
            catch (Exception ex)
            {
                throw new IOException("Could not read durable event receipts; append refused.", ex);
            }

            _eventIds[file] = loaded;
            return loaded;
        }

        // ── CEF (ArcSight Common Event Format) ────────────────────────────────
        private void WriteCef(UsbEvent ev)
        {
            try
            {
                var (sig, name, sev) = CefMap(ev.EventType);
                // CEF:Version|Vendor|Product|Version|SignatureID|Name|Severity|Extension
                string ts = ev.Timestamp.ToLocalTime().ToString("MMM dd HH:mm:ss");
                string machine = CefExtension(ev.MachineId);
                string ext = $"msg={machine} {CefExtension(ev.DeviceName)} " +
                    $"{CefExtension(ev.DeviceId)} - {CefExtension(ev.EventType)} " +
                    $"{CefExtension(ev.Detail)}".Trim();
                string cef = $"{ts} {machine} CEF:0|USBGuardian|USBGuardian|2.0|" +
                    $"{sig}|{CefHeader(name)}|{sev}|{ext}";
                WindowsFileSecurity.AppendAllTextProtected(
                    _cefPath,
                    cef + Environment.NewLine);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "CEF write failed."); }
        }

        private static (int sig, string name, int sev) CefMap(string t) => t switch
        {
            "Blocked" or "BlockedByAdmin" => (103, "unauthorized device connection blocked", 9),
            "Connected"                   => (100, "device connected", 3),
            "Authorized" or "LearnCaptured" => (104, "authorized device connection", 5),
            "Locked"                      => (200, "machine locked", 3),
            "Unlocked"                    => (201, "machine unlocked", 5),
            "LearnOn"                     => (210, "learn mode enabled", 4),
            "LearnOff"                    => (211, "learn mode disabled", 3),
            "BadUSBBlocked"               => (300, "badUSB keystroke-injection device blocked", 10),
            "BlockFailed"                 => (105, "device block failed", 9),
            "BadUSBBlockFailed"           => (302, "badUSB device block failed", 10),
            "BadUSBRestoreFailed"         => (303, "badUSB device restore failed", 8),
            "BadUSBRestored"              => (304, "badUSB device restored", 4),
            "KeyboardAdded"               => (301, "new keyboard enumerated", 6),
            _                              => (299, t, 3)
        };

        private static string CefHeader(string value) => (value ?? string.Empty)
            .Replace("\\", "\\\\")
            .Replace("|", "\\|")
            .Replace("\r", " ")
            .Replace("\n", " ");

        private static string CefExtension(string value) => (value ?? string.Empty)
            .Replace("\\", "\\\\")
            .Replace("=", "\\=")
            .Replace("\r", " ")
            .Replace("\n", " ");

        // ── SMTP email alerts ─────────────────────────────────────────────────
        private void SendEmail(UsbEvent ev)
        {
            try
            {
                string host = _config["Smtp:Host"] ?? "";
                string from = _config["Smtp:From"] ?? "";
                string to   = _config["Smtp:To"] ?? "";
                if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
                    return;

                int port      = _config.GetValue("Smtp:Port", 587);
                bool ssl      = _config.GetValue("Smtp:EnableSsl", true);
                string user   = _config["Smtp:User"] ?? "";
                string pass   = _config["Smtp:Password"] ?? "";

                using var client = new SmtpClient(host, port)
                {
                    EnableSsl = ssl,
                    Timeout = 10_000
                };
                if (!string.IsNullOrEmpty(user))
                    client.Credentials = new NetworkCredential(user, pass);

                using var msg = new MailMessage(from, to)
                {
                    Subject = $"[USB Guardian] Blocked device on {ev.MachineId}",
                    Body    = $"A device was blocked.\n\nMachine: {ev.MachineId}\nDevice:  {ev.DeviceName}\nID:      {ev.DeviceId}\nTime:    {ev.Timestamp.ToLocalTime()}\nDetail:  {ev.Detail}"
                };
                client.Send(msg);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Email alert failed for {Machine}.", ev.MachineId); }
        }

        private void TrimIfLarge(string file)
        {
            try
            {
                var fi = new FileInfo(file);
                if (fi.Length < 2_000_000) return;
                var lines = new Queue<string>();
                long retainedBytes = 0;
                foreach (string line in File.ReadLines(file))
                {
                    lines.Enqueue(line);
                    retainedBytes += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
                    while (lines.Count > 1 && (retainedBytes > 1_000_000 || lines.Count > 5_000))
                        retainedBytes -= Encoding.UTF8.GetByteCount(lines.Dequeue()) + Environment.NewLine.Length;
                }
                WindowsFileSecurity.WriteAllTextAtomically(file,
                    string.Join(Environment.NewLine, lines) + Environment.NewLine,
                    restrictToSystemAndAdministrators: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Event retention failed; preserving the original log {File}.", file);
            }
        }

        private sealed class EventIdWindow
        {
            private const int Capacity = 5_000;
            private readonly HashSet<Guid> _ids = new();
            private readonly Queue<Guid> _order = new();

            public bool Contains(Guid eventId) => _ids.Contains(eventId);

            public void Add(Guid eventId)
            {
                if (eventId == Guid.Empty || !_ids.Add(eventId)) return;
                _order.Enqueue(eventId);
                while (_order.Count > Capacity)
                    _ids.Remove(_order.Dequeue());
            }
        }
    }
}
