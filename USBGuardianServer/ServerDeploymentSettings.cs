using Microsoft.Win32;

namespace USBGuardianServer;

internal static class ServerDeploymentSettings
{
    private const string RegistryPath = @"SOFTWARE\USBGuardian\Server";

    public static string ResolveAgentSecret(
        IConfiguration configuration,
        ILogger logger)
    {
        string configured = configuration["AgentSecret"]?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(configured) || !OperatingSystem.IsWindows())
            return configured;

        try
        {
            using var localMachine = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                RegistryView.Registry64);
            using var key = localMachine.OpenSubKey(RegistryPath);
            return key?.GetValue("AgentSecret")?.ToString()?.Trim()
                ?? string.Empty;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Could not read installer configuration from HKLM\\{RegistryPath}.",
                RegistryPath);
            return string.Empty;
        }
    }
}
