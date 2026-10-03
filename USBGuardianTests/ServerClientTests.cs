using System.Net;
using System.Net.Sockets;
using USBGuardianControl;

namespace USBGuardianTests;

public class ServerClientTests
{
    [Fact]
    public async Task UnreachableServerReportsTheEndpointAndServiceCheck()
    {
        int unusedPort;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            unusedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        using var client = new ServerClient();
        client.Configure($"http://127.0.0.1:{unusedPort}", "admin");

        var error = await Assert.ThrowsAsync<ServerConnectionException>(
            () => client.GetAgentsAsync());

        Assert.Contains($"127.0.0.1:{unusedPort}", error.Message,
            StringComparison.Ordinal);
        Assert.Contains("USB Guardian Server", error.Message,
            StringComparison.Ordinal);
        Assert.Contains("TCP port", error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.IsType<HttpRequestException>(error.InnerException);
    }

    [Theory]
    [InlineData("")]
    [InlineData("localhost:5050")]
    [InlineData("ftp://localhost:5050")]
    [InlineData("https://user:password@localhost:5443")]
    [InlineData("https://localhost:5443/path")]
    [InlineData("https://localhost:5443/?query=value")]
    [InlineData("https://localhost:5443/#fragment")]
    public void ConfigureRejectsInvalidServerUrls(string url)
    {
        using var client = new ServerClient();

        var error = Assert.Throws<ArgumentException>(
            () => client.Configure(url, "admin"));

        Assert.Contains("HTTP or HTTPS", error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RedirectDoesNotForwardTheAdministratorPasswordToAnotherOrigin()
    {
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        using var destination = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        destination.Start();
        int originPort = ((IPEndPoint)origin.LocalEndpoint).Port;
        int destinationPort = ((IPEndPoint)destination.LocalEndpoint).Port;
        using var client = new ServerClient();
        client.Configure($"http://127.0.0.1:{originPort}", "isolated-password");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var request = client.GetAgentsAsync();
        using var connection = await origin.AcceptTcpClientAsync(timeout.Token);
        using var reader = new StreamReader(connection.GetStream(), leaveOpen: true);
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(timeout.Token))) { }
        string response = $"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{destinationPort}/api/agents\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        await connection.GetStream().WriteAsync(System.Text.Encoding.ASCII.GetBytes(response), timeout.Token);
        connection.Close();
        var error = await Assert.ThrowsAsync<ServerApiException>(() => request);
        Assert.Equal(HttpStatusCode.Redirect, error.StatusCode);
        Assert.False(destination.Pending());
    }
}
