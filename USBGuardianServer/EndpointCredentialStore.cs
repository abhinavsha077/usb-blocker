using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using USBGuardian.Shared;

namespace USBGuardianServer;

// Each credential authenticates one enrolled machine; fleet bootstrap secrets
// cannot replace a bound endpoint, even when compatibility mode is enabled.
public sealed class EndpointCredentialStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, string> _hashes = new(StringComparer.OrdinalIgnoreCase);

    public EndpointCredentialStore(IConfiguration configuration)
    {
        _path = configuration["EndpointCredentialFilePath"] ?? @"C:\ProgramData\USBGuardian\endpoint_credentials.json";
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        WindowsFileSecurity.RestrictExistingFileToSystemAndAdministrators(_path);
        if (File.Exists(_path))
        {
            // Invalid/unreadable enrollment state must not silently downgrade to
            // fleet authentication or overwrite existing credential bindings.
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path))
                ?? throw new InvalidDataException("Endpoint credential store is empty.");
            foreach (var pair in loaded)
            {
                ValidateId(pair.Key);
                if (pair.Value is not { Length: 64 } || pair.Value.Any(c => !Uri.IsHexDigit(c)))
                    throw new InvalidDataException("Endpoint credential store contains an invalid binding.");
            }
            _hashes = new(loaded, StringComparer.OrdinalIgnoreCase);
        }
    }

    public string Issue(string machineId)
    {
        machineId = ValidateId(machineId);
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        lock (_gate)
        {
            var candidate = new Dictionary<string, string>(_hashes, StringComparer.OrdinalIgnoreCase)
            {
                [machineId] = Hash(token)
            };
            WindowsFileSecurity.WriteAllTextAtomically(_path, JsonSerializer.Serialize(candidate), true);
            _hashes = candidate;
        }
        return token;
    }

    public bool IsEnrolled(string machineId) { lock (_gate) return _hashes.ContainsKey(machineId); }

    public bool ValidateBinding(string machineId, string binding)
    {
        lock (_gate) return _hashes.TryGetValue(machineId, out var expected) &&
            CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(binding), Encoding.ASCII.GetBytes(expected));
    }

    public static string Fingerprint(string? token) => Hash(token ?? string.Empty);

    public bool Validate(string machineId, string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 256) return false;
        lock (_gate)
        {
            if (!_hashes.TryGetValue(machineId, out var expected)) return false;
            return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(token)), Encoding.ASCII.GetBytes(expected));
        }
    }

    public static string ValidateId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id != id.Trim() || id.Length > 128 ||
            id.Any(c => char.IsControl(c) || c is '/' or '\\'))
            throw new ArgumentException("Machine ID must contain 1 to 128 characters without separators or control characters.");
        return id;
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
