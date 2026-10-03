using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace USBGuardianNotifier;

// Every interactive session consumes independently. Only the Agent prunes files;
// removing them here would make delivery depend on which user reads first.
public sealed class BlockAlertInbox(string directory)
{
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> ReadPending(DateTime utcNow)
    {
        var files = Directory.GetFiles(directory, "*.json");
        _seen.IntersectWith(files);
        var pending = new List<string>();
        foreach (string path in files.Where(path => !_seen.Contains(path)).Take(100))
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var bytes = new byte[64 * 1024 + 1];
                int length = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
                if (length > 64 * 1024) { _seen.Add(path); continue; }
                var alert = JsonSerializer.Deserialize<BlockAlert>(bytes.AsSpan(0, length));
                _seen.Add(path);
                if (alert == null || alert.Timestamp < utcNow.AddMinutes(-2) ||
                    alert.Timestamp > utcNow.AddSeconds(30)) continue;
                string name = alert.DeviceName?.Trim() ?? string.Empty;
                pending.Add(name.Length == 0 ? "Unauthorized USB device" :
                    name.Length <= 256 ? name : name[..256]);
            }
            catch (IOException) { /* Retry on the next wakeup or periodic poll. */ }
            catch (UnauthorizedAccessException) { /* ACL may be repaired by the Agent. */ }
            catch (JsonException) { _seen.Add(path); }
        }
        return pending;
    }

    private sealed class BlockAlert
    {
        public string? DeviceName { get; set; }
        public DateTime Timestamp { get; set; }
    }
}
