using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using USBGuardianAgent;
using USBGuardianServer;

namespace USBGuardianTests;

public class ConfigurationTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "usb-guardian-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ServerConfiguredSecretEnablesAgentAuthentication()
    {
        string path = Path.Combine(_directory, "server.json");
        var manager = CreateServerConfig(path, "deployment-secret");

        Assert.True(manager.AgentAuthEnabled);
        Assert.True(manager.ValidateAgentToken("deployment-secret"));
        Assert.False(manager.ValidateAgentToken("wrong-secret"));
    }

    [Fact]
    public void ServerConfiguredSecretRotatesPersistedSecret()
    {
        string path = Path.Combine(_directory, "server.json");
        _ = CreateServerConfig(path, "first-secret");

        var rotated = CreateServerConfig(path, "second-secret");

        Assert.True(rotated.ValidateAgentToken("second-secret"));
        Assert.False(rotated.ValidateAgentToken("first-secret"));
    }

    [Fact]
    public void PasswordChangeRequiresCurrentPasswordAndPersists()
    {
        string path = Path.Combine(_directory, "server-password.json");
        var manager = CreateServerConfig(path, "deployment-secret");

        Assert.False(manager.TryChangePassword("wrong", "new-password"));
        Assert.True(manager.VerifyPassword("admin"));
        Assert.True(manager.TryChangePassword("admin", "new-password"));
        Assert.False(manager.VerifyPassword("admin"));
        Assert.True(
            CreateServerConfig(path, "deployment-secret")
                .VerifyPassword("new-password"));
    }

    [Fact]
    public void LegacyBcryptPasswordMigratesOnRotationWithoutBreakingExistingLogin()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "legacy-password.json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new
        {
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("legacy-password"),
            PasswordIsDefault = false,
            AgentSecret = "deployment-secret"
        }));
        var manager = CreateServerConfig(path, "deployment-secret");
        Assert.True(manager.VerifyPassword("legacy-password"));
        Assert.True(manager.TryChangePassword("legacy-password", new string('x', 72) + "new-suffix"));
        var restarted = CreateServerConfig(path, "deployment-secret");
        Assert.True(restarted.VerifyPassword(new string('x', 72) + "new-suffix"));
        Assert.False(restarted.VerifyPassword(new string('x', 72) + "wrong-suffix"));
    }

    [Fact]
    public void LongPasswordsAndUnicodeSuffixesRemainDistinctAfterRestart()
    {
        string path = Path.Combine(_directory, "long-password.json");
        var manager = CreateServerConfig(path, "deployment-secret");
        string prefix = new string('x', 72);
        Assert.True(manager.TryChangePassword("admin", prefix + "original"));
        Assert.True(manager.VerifyPassword(prefix + "original"));
        Assert.False(manager.VerifyPassword(prefix + "different"));
        var restarted = CreateServerConfig(path, "deployment-secret");
        Assert.True(restarted.VerifyPassword(prefix + "original"));
        Assert.False(restarted.VerifyPassword(prefix + "different"));
        Assert.True(restarted.TryChangePassword(prefix + "original", new string('é', 36) + "suffix"));
        Assert.False(restarted.VerifyPassword(new string('é', 36) + "changed"));
    }

    [Fact]
    public void HttpsRequiredAgentConfigNeverUsesItsCredentialOverHttp()
    {
        string path = Path.Combine(_directory, "agent-secure.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new AgentConfig
        { ServerUrl = "http://server:5050", MachineId = "endpoint", AgentToken = "credential", RequireHttps = true }));
        var manager = CreateAgentConfig(path);
        Assert.StartsWith("https://", manager.Config.ServerUrl);
        Assert.Empty(manager.Config.AgentToken);
        Assert.True(manager.Config.RequireHttps);
    }

    [Fact]
    public void MissingAgentConfigCreatesSafeDefaultFile()
    {
        string path = Path.Combine(_directory, "agent.json");
        var manager = CreateAgentConfig(path);

        Assert.Equal("http://localhost:5050", manager.Config.ServerUrl);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void MissingServerSecretRejectsAgentRegistrationByDefault()
    {
        var manager = CreateServerConfig(
            Path.Combine(_directory, "server-no-secret.json"),
            string.Empty);

        Assert.False(manager.AgentAuthEnabled);
        Assert.False(manager.ValidateAgentToken(null));
        Assert.False(manager.ValidateAgentToken("default-token"));
    }

    [Fact]
    public void DevelopmentCanExplicitlyAllowUnauthenticatedAgents()
    {
        string path = Path.Combine(_directory, "server-dev.json");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConfigFilePath"] = path,
                ["Security:AllowUnauthenticatedAgents"] = "true"
            })
            .Build();
        var manager = new ServerConfigManager(
            configuration,
            NullLogger<ServerConfigManager>.Instance);

        Assert.True(manager.ValidateAgentToken(null));
    }

    [Fact]
    public void MalformedAgentConfigIsNotOverwritten()
    {
        string path = Path.Combine(_directory, "agent.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, "{ invalid json");

        var manager = CreateAgentConfig(path);

        Assert.Equal("http://localhost:5050", manager.Config.ServerUrl);
        Assert.Equal("{ invalid json", File.ReadAllText(path));
    }

    private static ServerConfigManager CreateServerConfig(
        string path,
        string secret)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConfigFilePath"] = path,
                ["AgentSecret"] = secret
            })
            .Build();
        return new ServerConfigManager(
            configuration,
            NullLogger<ServerConfigManager>.Instance);
    }

    private static AgentConfigManager CreateAgentConfig(string path)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentConfigPath"] = path
            })
            .Build();
        return new AgentConfigManager(
            configuration,
            NullLogger<AgentConfigManager>.Instance);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch { }
    }
}
