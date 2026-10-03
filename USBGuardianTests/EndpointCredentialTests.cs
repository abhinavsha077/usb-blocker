using Microsoft.Extensions.Configuration;
using USBGuardianServer;

namespace USBGuardianTests;

public sealed class EndpointCredentialTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "usb-guardian-tests", Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "credentials.json");
    private EndpointCredentialStore Store() => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["EndpointCredentialFilePath"] = FilePath }).Build());

    [Fact]
    public void CredentialsAreBoundToIdentityAndPersistWithoutPlaintextTokens()
    {
        var store = Store();
        string a = store.Issue("endpoint-A"), b = store.Issue("endpoint-B");
        Assert.True(store.Validate("ENDPOINT-a", a));
        Assert.False(store.Validate("endpoint-A", b));
        Assert.False(store.Validate("endpoint-B", a));
        Assert.False(store.Validate("unknown", a));
        Assert.DoesNotContain(a, File.ReadAllText(FilePath));
        Assert.True(Store().Validate("endpoint-A", a));
    }

    [Fact]
    public void RotationRevokesExistingSessionBindingAndFailedSaveDoesNotRotate()
    {
        var store = Store();
        string old = store.Issue("endpoint-A");
        using (var fileLock = new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var error = Record.Exception(() => store.Issue("endpoint-A"));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        Assert.True(store.Validate("endpoint-A", old));
        string current = store.Issue("endpoint-A");
        Assert.False(store.ValidateBinding("endpoint-A", EndpointCredentialStore.Fingerprint(old)));
        Assert.True(store.Validate("endpoint-A", current));
    }

    [Theory]
    [InlineData(" endpoint-A")]
    [InlineData("endpoint-A\n")]
    [InlineData("endpoint/A")]
    [InlineData("endpoint\\A")]
    public void IdentityAliasesAreRejected(string id) => Assert.Throws<ArgumentException>(() => Store().Issue(id));

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
}
