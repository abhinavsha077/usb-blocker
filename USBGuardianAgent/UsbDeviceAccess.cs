using System.Diagnostics;
using System.Management;

namespace USBGuardianAgent;

public sealed class DeviceSnapshot
{
    private readonly Dictionary<string, object?> _properties;
    public DeviceSnapshot(Dictionary<string, object?> properties) => _properties = properties;
    public object? this[string name] => _properties.GetValueOrDefault(name);
}

public interface IUsbDeviceAccess
{
    IReadOnlyList<DeviceSnapshot> Query(string wql);
    bool SetState(string verb, string deviceId);
}

public sealed class WindowsUsbDeviceAccess : IUsbDeviceAccess
{
    private readonly ILogger<WindowsUsbDeviceAccess> logger;

    public WindowsUsbDeviceAccess(ILogger<WindowsUsbDeviceAccess> logger)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            throw new PlatformNotSupportedException(
                "USB Guardian device enforcement requires Windows build 19041 or newer.");
        this.logger = logger;
    }

    public IReadOnlyList<DeviceSnapshot> Query(string wql)
    {
        using var searcher = new ManagementObjectSearcher(wql);
        using var results = searcher.Get();
        var devices = new List<DeviceSnapshot>();
        foreach (ManagementObject device in results)
        {
            using (device)
            {
                var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (PropertyData property in device.Properties) properties[property.Name] = property.Value;
                devices.Add(new DeviceSnapshot(properties));
            }
        }
        return devices;
    }

    public static ProcessStartInfo CreateStartInfo(string verb, string deviceId)
    {
        if (verb is not ("enable-device" or "disable-device"))
            throw new ArgumentException("Unsupported device action.", nameof(verb));
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "pnputil.exe"))
        {
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("/" + verb);
        start.ArgumentList.Add(deviceId);
        return start;
    }

    public bool SetState(string verb, string deviceId)
    {
        try
        {
            using var process = Process.Start(CreateStartInfo(verb, deviceId));
            if (process == null) return false;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                try { process.WaitForExit(1000); } catch { }
                logger.LogError("pnputil timed out for {Verb}: {DeviceId}.", verb, deviceId);
                return false;
            }
            string detail = output.GetAwaiter().GetResult().Trim();
            string failure = error.GetAwaiter().GetResult().Trim();
            if (process.ExitCode != 0)
            {
                logger.LogError("pnputil /{Verb} failed for {DeviceId} with exit code {ExitCode}. {Detail}",
                    verb, deviceId, process.ExitCode, string.IsNullOrWhiteSpace(failure) ? detail : failure);
                return false;
            }
            logger.LogInformation("pnputil /{Verb} succeeded for {DeviceId}.", verb, deviceId);
            string escaped = deviceId.Replace("\\", "\\\\").Replace("'", "\\'");
            var observed = Query($"SELECT * FROM Win32_PnPEntity WHERE PNPDeviceID='{escaped}'");
            bool verified = observed.Count > 0 && observed.All(d =>
                d["ConfigManagerErrorCode"] != null &&
                Convert.ToUInt32(d["ConfigManagerErrorCode"]) == (verb == "disable-device" ? 22u : 0u));
            if (!verified) logger.LogError("Device state did not confirm /{Verb} for {DeviceId}.", verb, deviceId);
            return verified;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "pnputil /{Verb} failed for {DeviceId}.", verb, deviceId);
            return false;
        }
    }
}
