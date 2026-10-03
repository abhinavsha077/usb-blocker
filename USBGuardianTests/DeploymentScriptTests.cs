using System.Runtime.CompilerServices;

namespace USBGuardianTests;

public class DeploymentScriptTests
{
    [Theory]
    [InlineData("Install-Agent.bat")]
    [InlineData("Install-Server.bat")]
    [InlineData("Update-All.bat")]
    public void SourcePublishScriptsRequireTheSdk(string scriptName)
    {
        string script = ReadScript(scriptName);

        Assert.Contains("dotnet --list-sdks", script, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "dotnet --list-runtimes",
            script,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Install-Agent.bat", "USB Guardian Agent")]
    [InlineData("Install-Server.bat", "USB Guardian Server")]
    public void ServiceInstallScriptsConfigureCrashRecovery(
        string scriptName,
        string serviceName)
    {
        string script = ReadScript(scriptName);

        Assert.Contains(
            $"sc failure \"%SERVICE_NAME%\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            $"sc failureflag \"%SERVICE_NAME%\" 1",
            script,
            StringComparison.Ordinal);
        Assert.Contains(serviceName, script, StringComparison.Ordinal);
    }

    [Fact]
    public void ServerInstallVerifiesTheListenerAfterStartingTheService()
    {
        string script = ReadScript("Install-Server.bat");

        Assert.Contains("http://127.0.0.1:5050/health", script,
            StringComparison.Ordinal);
        Assert.Contains("service started but did not open", script,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AgentInstallRejectsAnUnreachableServerBeforeWritingConfiguration()
    {
        string script = ReadScript("Install-Agent.bat");
        int healthCheck = script.IndexOf("'/health'", StringComparison.Ordinal);
        int writeConfiguration = script.IndexOf(
            "[6/7] Writing agent configuration",
            StringComparison.Ordinal);

        Assert.True(healthCheck >= 0 && healthCheck < writeConfiguration);
        Assert.Contains("Server is not reachable", script,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AgentInstallValidatesThePersistedConfiguration()
    {
        string script = ReadScript("Install-Agent.bat");

        Assert.Contains("Get-Content -Raw $env:CONFIG_PATH | ConvertFrom-Json",
            script, StringComparison.Ordinal);
        Assert.Contains("is empty, malformed, or incomplete", script,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AcceptanceBundleUsesPerMachineConfigurationAndDoesNotPrintBootstrapSecret()
    {
        string tools = Path.Combine(RepositoryRoot(), "USBGuardianInstaller", "ReleaseBundle", "Tools");
        string endpoint = File.ReadAllText(Path.Combine(tools, "Install-Endpoint-Test.ps1"));
        string server = File.ReadAllText(Path.Combine(tools, "Install-Server-Test.ps1"));
        Assert.Contains("EndpointConfiguration", endpoint, StringComparison.Ordinal);
        Assert.Contains("MACHINEID=$MachineId", endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("Write-Host \"Agent token: $AgentToken\"", server, StringComparison.Ordinal);
        Assert.DoesNotContain("Set-Content -LiteralPath $tokenPath", server, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionServerHelperUsesIpv4HealthAndCapturesDiagnostics()
    {
        string script = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "USBGuardianInstaller", "ReleaseBundle",
            "Tools", "Install-Server-Test.ps1"));

        Assert.Contains("http://127.0.0.1:5050/health", script,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-RestMethod -Uri 'http://localhost:5050",
            script, StringComparison.Ordinal);
        Assert.Contains("AddSeconds(90)", script, StringComparison.Ordinal);
        Assert.Contains("Get-NetTCPConnection", script, StringComparison.Ordinal);
        Assert.Contains("Get-WinEvent", script, StringComparison.Ordinal);
        Assert.Contains("usbguardian-server-diagnostics.log", script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateStopsInteractiveProcessesBeforePublishingTheirFiles()
    {
        string script = ReadScript("Update-All.bat");

        int stopNotifier = script.IndexOf(
            "taskkill /IM USBGuardianNotifier.exe",
            StringComparison.Ordinal);
        int publishNotifier = script.IndexOf(
            "USBGuardianNotifier.csproj",
            StringComparison.Ordinal);
        int stopControl = script.IndexOf(
            "taskkill /IM USBGuardianControl.exe",
            StringComparison.Ordinal);
        int publishControl = script.IndexOf(
            "USBGuardianControl.csproj",
            StringComparison.Ordinal);

        Assert.True(stopNotifier >= 0 && stopNotifier < publishNotifier);
        Assert.True(stopControl >= 0 && stopControl < publishControl);
    }

    [Fact]
    public void UninstallStopsAgentBeforeManagedDeviceRecovery()
    {
        string script = ReadScript("Uninstall-All.bat");
        int stopAgent = script.IndexOf(
            "net stop \"%AGENT_SERVICE%\"",
            StringComparison.Ordinal);
        int restore = script.IndexOf(
            "--restore-managed-devices",
            StringComparison.Ordinal);

        Assert.True(stopAgent >= 0 && stopAgent < restore);
        Assert.Contains("if errorlevel 1", script, StringComparison.Ordinal);
        Assert.DoesNotContain("PNPClass='DiskDrive'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("PNPClass='WPD'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerBuildRemovesStaleOutputsAndWritesPortableChecksums()
    {
        string script = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "USBGuardianInstaller",
            "Build-Installers.ps1"));

        Assert.Contains(
            "Remove-Item -LiteralPath $generatedDirectory -Recurse -Force",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "-Encoding ascii",
            script,
            StringComparison.Ordinal);
    }

    private static string ReadScript(string scriptName) =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), scriptName));

    private static string RepositoryRoot(
        [CallerFilePath] string sourceFile = "")
    {
        string? testProjectDirectory = Path.GetDirectoryName(sourceFile);
        return Directory.GetParent(testProjectDirectory
            ?? throw new DirectoryNotFoundException(
                "Could not locate the USB Guardian test project."))?.FullName
            ?? throw new DirectoryNotFoundException(
                "Could not locate the USB Guardian repository root.");
    }
}
