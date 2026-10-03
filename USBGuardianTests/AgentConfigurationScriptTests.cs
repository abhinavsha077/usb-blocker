using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace USBGuardianTests;

public sealed class AgentConfigurationScriptTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "usb-config-script", Guid.NewGuid().ToString("N"));
    private string Destination => Path.Combine(_directory, "agent.json");

    public AgentConfigurationScriptTests() => Directory.CreateDirectory(_directory);

    private async Task<int> WriteConfiguration(string server = "https://server.example:5443",
        [CallerFilePath] string source = "")
    {
        string root = Directory.GetParent(Path.GetDirectoryName(source)!)!.FullName;
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(root, "Write-AgentConfig.ps1") })
            start.ArgumentList.Add(argument);
        start.Environment["SERVER_URL"] = server;
        start.Environment["MACHINE_ID"] = "script-test-endpoint";
        start.Environment["AGENT_TOKEN"] = "isolated-test-token";
        start.Environment["CONFIG_PATH"] = Destination;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        await Task.WhenAll(output, error);
        return process.ExitCode;
    }

    [Fact]
    public async Task ConfigurationIsPersistedWithProtectedAclAndHttpsRequirement()
    {
        Assert.Equal(0, await WriteConfiguration());
        using var config = JsonDocument.Parse(File.ReadAllText(Destination));
        Assert.Equal("isolated-test-token", config.RootElement.GetProperty("AgentToken").GetString());
        Assert.True(config.RootElement.GetProperty("RequireHttps").GetBoolean());
        var security = new FileInfo(Destination).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();
        Assert.DoesNotContain(rules, rule => rule.IdentityReference.Value is "S-1-1-0" or "S-1-5-11" or "S-1-5-32-545");
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task FailedReplacementPreservesPriorConfigurationAndCleansTemporaryFile()
    {
        Assert.Equal(0, await WriteConfiguration());
        string original = File.ReadAllText(Destination);
        using (var hold = new FileStream(Destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.NotEqual(0, await WriteConfiguration("https://replacement.example:5443"));
        Assert.Equal(original, File.ReadAllText(Destination));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Theory]
    [InlineData("https://user:password@server.example:5443")]
    [InlineData("https://server.example:5443/?query=value")]
    [InlineData("https://server.example:5443/path")]
    public async Task InvalidOriginNeverWritesTheCredential(string server)
    {
        Assert.NotEqual(0, await WriteConfiguration(server));
        Assert.False(File.Exists(Destination));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
