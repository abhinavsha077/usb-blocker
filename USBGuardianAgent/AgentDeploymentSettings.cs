using Microsoft.Win32;

namespace USBGuardianAgent;

internal static class AgentDeploymentSettings
{
    private const string RegistryPath = @"SOFTWARE\USBGuardian\Agent";

    public static AgentConfig? TryRead(ILogger logger)
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            using var localMachine = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                RegistryView.Registry64);
            using var key = localMachine.OpenSubKey(RegistryPath);
            string serverUrl = key?.GetValue("ServerUrl")?.ToString()?.Trim()
                ?? string.Empty;
            string agentToken = key?.GetValue("AgentToken")?.ToString()?.Trim()
                ?? string.Empty;
            string machineId = key?.GetValue("MachineId")?.ToString()?.Trim()
                ?? string.Empty;

            if (string.IsNullOrWhiteSpace(serverUrl) ||
                string.IsNullOrWhiteSpace(agentToken))
                return null;

            return new AgentConfig
            {
                ServerUrl = serverUrl,
                AgentToken = agentToken,
                MachineId = string.IsNullOrWhiteSpace(machineId)
                    ? Environment.MachineName
                    : machineId
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Could not read installer configuration from HKLM\\{RegistryPath}.",
                RegistryPath);
            return null;
        }
    }
}
