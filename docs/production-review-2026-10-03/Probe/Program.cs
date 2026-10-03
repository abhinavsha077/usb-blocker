using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Management;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using USBGuardianControl;
using Policy = USBGuardianServer.MachinePolicies;

// Isolated review probes: no real Agent process, WMI enforcement, services,
// registry changes, firewall changes, reboot, or hardware toggles.
string root = Path.GetFullPath(args[0]);
string evidence = Path.Combine(root, "docs", "production-review-2026-10-03", "evidence");
string state = Path.Combine(evidence, "state-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(state);
int port;
using (var listener = new TcpListener(IPAddress.Loopback, 0))
{
    listener.Start();
    port = ((IPEndPoint)listener.LocalEndpoint).Port;
}
string url = $"http://127.0.0.1:{port}";
string token = Guid.NewGuid().ToString("N");
string password = "Review-" + Guid.NewGuid().ToString("N");
var results = new List<object>();
void Record(string name, bool ok, string detail)
{
    results.Add(new { name, status = ok ? "PASS" : "FAIL", detail });
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}: {name}: {detail}");
}
var psi = new ProcessStartInfo("dotnet")
{
    WorkingDirectory = Path.Combine(root, "USBGuardianServer"),
    UseShellExecute = false, CreateNoWindow = true,
    RedirectStandardOutput = true, RedirectStandardError = true
};
psi.ArgumentList.Add(Path.Combine(root, "USBGuardianServer", "bin", "Release", "net8.0", "USBGuardianServer.dll"));
psi.Environment["Http__BindAddress"] = "127.0.0.1";
psi.Environment["Http__Port"] = port.ToString();
psi.Environment["Https__Enabled"] = "false";
psi.Environment["AgentSecret"] = token;
psi.Environment["ConfigFilePath"] = Path.Combine(state, "config.json");
psi.Environment["EndpointCredentialFilePath"] = Path.Combine(state, "credentials.json");
psi.Environment["PolicyFilePath"] = Path.Combine(state, "policies.json");
psi.Environment["InventoryFilePath"] = Path.Combine(state, "inventory.json");
psi.Environment["EventDir"] = Path.Combine(state, "events");
psi.Environment["Cef__Enabled"] = "false";
psi.Environment["Smtp__Enabled"] = "false";
using var process = Process.Start(psi)!;
var stdout = process.StandardOutput.ReadToEndAsync();
var stderr = process.StandardError.ReadToEndAsync();
using var http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(10) };
using var control = new ServerClient();
var clients = new List<HubConnection>();
async Task<(HubConnection hub, Channel<Policy> policies)> Fake(string id, string auth)
{
    var hub = new HubConnectionBuilder().WithUrl(url + "/agenthub").Build();
    clients.Add(hub);
    var policies = Channel.CreateUnbounded<Policy>();
    hub.On<Policy>("ApplyPolicy", policy => policies.Writer.TryWrite(policy));
    await hub.StartAsync();
    await hub.InvokeAsync("RegisterAgent", id, id, "127.0.0.1", auth);
    return (hub, policies);
}
async Task<Policy> Next(Channel<Policy> policies)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    return await policies.Reader.ReadAsync(timeout.Token);
}
async Task<HttpResponseMessage> Post(string path) => await http.PostAsync(path, null);
string id = "REVIEW-ENDPOINT-A";
string device = @"USBSTOR\DISK&VEN_REVIEW&PROD_TEST\UNIT-1";
try
{
    for (int attempt = 0; attempt < 100; attempt++)
    {
        try { if ((await http.GetAsync("/health")).IsSuccessStatusCode) break; }
        catch (HttpRequestException) { }
        if (process.HasExited) throw new Exception("Isolated server failed to start.");
        await Task.Delay(100);
    }
    Record("Health endpoint", (await http.GetAsync("/health")).IsSuccessStatusCode, "Isolated server responds.");
    Record("Anonymous admin denied", (await http.GetAsync("/api/agents")).StatusCode == HttpStatusCode.Unauthorized, "GET /api/agents without password.");
    control.Configure(url, "admin");
    await control.ChangePasswordAsync("admin", password);
    http.DefaultRequestHeaders.Add("X-Admin-Password", password);
    Record("Loopback password rotation", (await http.GetAsync("/api/agents")).IsSuccessStatusCode, "New password accepted.");
    var provision = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
    { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
        RedirectStandardOutput = true, RedirectStandardError = true };
    provision.ArgumentList.Add("-NoProfile");
    provision.ArgumentList.Add("-NonInteractive");
    provision.ArgumentList.Add("-Command");
    provision.ArgumentList.Add("& $env:USB_TEST_SCRIPT -ServerUrl $env:USB_TEST_URL -MachineId 'PROVISION-SCRIPT-ENDPOINT' -OutputPath $env:USB_TEST_OUTPUT -AllowHttp -AdminPassword (ConvertTo-SecureString $env:USB_TEST_PASSWORD -AsPlainText -Force)");
    provision.Environment["USB_TEST_SCRIPT"] = Path.Combine(root, "Provision-Endpoint.ps1");
    provision.Environment["USB_TEST_URL"] = url;
    provision.Environment["USB_TEST_OUTPUT"] = Path.Combine(state, "provisioned-agent.json");
    provision.Environment["USB_TEST_PASSWORD"] = password;
    provision.Environment["PSModulePath"] = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "Modules");
    using (var provisioningProcess = Process.Start(provision)!)
    {
        var output = provisioningProcess.StandardOutput.ReadToEndAsync();
        var error = provisioningProcess.StandardError.ReadToEndAsync();
        await provisioningProcess.WaitForExitAsync();
        await output;
        string diagnostic = await error;
        Record("Windows PowerShell endpoint provisioning", provisioningProcess.ExitCode == 0,
            provisioningProcess.ExitCode == 0 ? "Protected configuration created; token not printed." : diagnostic);
        if (provisioningProcess.ExitCode == 0)
        {
            var issuedConfig = JsonSerializer.Deserialize<USBGuardianAgent.AgentConfig>(File.ReadAllText(Path.Combine(state, "provisioned-agent.json")))!;
            var (scriptAgent, _) = await Fake(issuedConfig.MachineId, issuedConfig.AgentToken);
            await scriptAgent.DisposeAsync();
        }
    }
    using (var destinationLock = new FileStream(Path.Combine(state, "provisioned-agent.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    using (var failedProvision = Process.Start(provision)!)
    {
        var output = failedProvision.StandardOutput.ReadToEndAsync();
        var error = failedProvision.StandardError.ReadToEndAsync();
        await failedProvision.WaitForExitAsync();
        await output;
        await error;
        string? recoveryPath = Directory.GetFiles(state, "provisioned-agent.json.*.tmp").SingleOrDefault();
        Record("Provisioning replacement failure retains credential", failedProvision.ExitCode != 0 && recoveryPath != null,
            "Locked destination fails; protected recovery configuration is retained.");
        if (recoveryPath != null)
        {
            var recovered = JsonSerializer.Deserialize<USBGuardianAgent.AgentConfig>(File.ReadAllText(recoveryPath))!;
            var (recoveryAgent, _) = await Fake(recovered.MachineId, recovered.AgentToken);
            await recoveryAgent.DisposeAsync();
        }
    }
    if (args.Length > 1)
    {
        provision.FileName = args[1];
        provision.Environment["PSModulePath"] = Environment.GetEnvironmentVariable("PSModulePath") ?? string.Empty;
        using var coreProvision = Process.Start(provision)!;
        var output = coreProvision.StandardOutput.ReadToEndAsync();
        var error = coreProvision.StandardError.ReadToEndAsync();
        await coreProvision.WaitForExitAsync();
        await output;
        string diagnostic = await error;
        Record("PowerShell 7 endpoint provisioning", coreProvision.ExitCode == 0,
            coreProvision.ExitCode == 0 ? "Protected configuration replaced successfully." : diagnostic);
    }
    try
    {
        await Fake("REVIEW-INVALID-TOKEN", "invalid-review-token");
        Record("Invalid agent token denied", false, "Unexpected registration success.");
    }
    catch { Record("Invalid agent token denied", true, "Registration rejected."); }
    var enrollment = await Post($"/api/agents/{id}/credential");
    enrollment.EnsureSuccessStatusCode();
    token = (await enrollment.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("agentToken").GetString()!;
    var (agent, policies) = await Fake(id, token);
    var policy = await Next(policies);
    Record("Authenticated registration", !policy.IsUnlocked && !policy.LearnMode, "Default policy locked.");
    await agent.InvokeAsync("UpdateDeviceList", id, new[] { new USBGuardianServer.UsbDeviceDto { DeviceID = device, Name = "Review drive", Status = "OK", IsAllowed = false } });
    Record("Device snapshot", (await control.GetDevicesAsync(id)).Single().DeviceID == device, "Full PNP ID preserved.");
    await control.AllowAsync(id, device);
    policy = await Next(policies);
    Record("Allow encoded PNP ID", policy.AllowedDevices.Contains(device), "WPF HTTP client route roundtrip succeeds.");
    await control.BlockAsync(id, device);
    policy = await Next(policies);
    Record("Block encoded PNP ID", policy.BlockedDevices.Contains(device) && !policy.AllowedDevices.Contains(device), "Block removes exact allow rule.");
    await control.UnlockTimedAsync(id, 1);
    policy = await Next(policies);
    Record("Timed unlock push", policy.IsUnlocked && policy.UnlockExpiry > DateTime.UtcNow, "UTC expiry included in policy.");
    Record("Timed unlock persistence", File.ReadAllText(Path.Combine(state, "policies.json")).Contains("UnlockExpiry"), "Expiry written to isolated policy file.");
    await control.LockAsync(id);
    policy = await Next(policies);
    Record("Lock after timed unlock", !policy.IsUnlocked && policy.UnlockExpiry == null, "Unlock expiry cleared.");
    await control.LearnModeAsync(id, true, 1);
    policy = await Next(policies);
    await control.LockAsync(id);
    policy = await Next(policies);
    var shouldAllow = typeof(USBGuardianAgent.UsbPolicyManager).GetMethod("ShouldAllow", BindingFlags.Static | BindingFlags.NonPublic)!;
    bool allows = (bool)shouldAllow.Invoke(null, new object[] { device, policy.IsUnlocked, policy.LearnMode, policy.AllowedDevices, policy.BlockedDevices })!;
    Record("Lock cancels learn bypass", !allows, $"After Lock: IsUnlocked={policy.IsUnlocked}, LearnMode={policy.LearnMode}, agent ShouldAllow={allows}.");
    await control.SetGroupAsync(id, "ReviewGroup");
    await control.GroupLockAsync("ReviewGroup");
    policy = await Next(policies);
    Record("Group lock cancels learn", !policy.LearnMode, $"LearnMode remains {policy.LearnMode}.");
    await control.LockAllAsync();
    policy = await Next(policies);
    Record("Lock all cancels learn", !policy.LearnMode, $"LearnMode remains {policy.LearnMode}.");
    await control.LearnModeAsync(id, false);
    await Next(policies);
    await control.SetSectorAsync(id, "optical", true);
    policy = await Next(policies);
    Record("Sector policy push", policy.OpticalBlocked, "Optical sector flag delivered.");
    await control.SetBadUsbAsync(id, true);
    policy = await Next(policies);
    Record("BadUSB policy push", policy.BadUsbBlock, "BadUSB flag delivered.");
    await agent.InvokeAsync("ReportEnforcement", id, policy.Revision, DateTime.UtcNow, "");
    var verified = (await control.GetAgentsAsync()).Single(a => a.MachineId == id);
    Record("Matching enforcement receipt", verified.AppliedRevision == verified.PolicyRevision && verified.EnforcementUtc.HasValue,
        "Matching revision and actual scan timestamp returned separately from policy intent.");
    long previousRevision = policy.Revision;
    await control.LockAsync(id);
    policy = await Next(policies);
    await agent.InvokeAsync("ReportEnforcement", id, previousRevision, DateTime.UtcNow, "");
    await Next(policies); // server repairs missing delivery/revision mismatch
    var pending = (await control.GetAgentsAsync()).Single(a => a.MachineId == id);
    Record("Stale receipt cannot verify newer policy", pending.AppliedRevision == null,
        "Older receipt rejected and desired policy replayed.");
    Record("Invalid unlock duration denied", (await Post($"/api/agents/{id}/unlock?minutes=0")).StatusCode == HttpStatusCode.BadRequest, "Zero minutes returns 400.");
    Guid eventId = Guid.NewGuid();
    await agent.InvokeAsync("ReportEventV2", id, eventId, DateTime.UtcNow, "Blocked", device, "Review drive", "simulated event");
    await agent.InvokeAsync("ReportEventV2", id, eventId, DateTime.UtcNow, "Blocked", device, "Review drive", "simulated event");
    Record("Security event deduplication", (await http.GetFromJsonAsync<List<USBGuardianServer.UsbEvent>>($"/api/agents/{id}/history"))!.Count(e => e.EventId == eventId) == 1, "Duplicate GUID recorded once.");
    string eventFile = Directory.GetFiles(Path.Combine(state, "events"), "*.jsonl", SearchOption.AllDirectories)
        .Single(path => File.ReadLines(path).Any(line => JsonSerializer.Deserialize<USBGuardianServer.UsbEvent>(line)?.MachineId == id));
    Guid failedEventId = Guid.NewGuid();
    bool acknowledged;
    using (var fileLock = new FileStream(eventFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        try
        {
            await agent.InvokeAsync("ReportEventV2", id, failedEventId, DateTime.UtcNow, "Blocked", device, "Review drive", "write failure probe");
            acknowledged = true;
        }
        catch { acknowledged = false; }
    }
    bool persisted = (await http.GetFromJsonAsync<List<USBGuardianServer.UsbEvent>>($"/api/agents/{id}/history"))!.Any(e => e.EventId == failedEventId);
    Record("Failed event persistence is not acknowledged", !acknowledged, $"Hub invocation success={acknowledged}; event persisted={persisted}. Real Agent discards its outbox entry on success.");
    using (var auditLock = new FileStream(eventFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        var response = await Post($"/api/agents/{id}/credential");
        await agent.InvokeAsync("UpdateDeviceList", id, Array.Empty<USBGuardianServer.UsbDeviceDto>());
        Record("Failed credential audit preserves current session", response.StatusCode == HttpStatusCode.InternalServerError,
            "Issuance fails before rotation; existing endpoint credential and session remain valid.");
    }
    using (var policyLock = new FileStream(Path.Combine(state, "policies.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        var response = await Post($"/api/agents/{id}/unlock");
        var summary = (await control.GetAgentsAsync()).Single(a => a.MachineId == id);
        Record("Failed policy save rolls back memory", summary.IsLocked, $"Unlock HTTP={(int)response.StatusCode}; dashboard IsLocked={summary.IsLocked}; no unlock sent to simulated agent.");
    }
    await control.LockAsync(id);
    await Next(policies);
    bool impersonationDenied;
    var otherEnrollment = await Post("/api/agents/REVIEW-ENDPOINT-B/credential");
    string otherToken = (await otherEnrollment.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("agentToken").GetString()!;
    try { await Fake(id, otherToken); impersonationDenied = false; }
    catch { impersonationDenied = true; }
    bool originalAccepted;
    try { await agent.InvokeAsync("UpdateDeviceList", id, Array.Empty<USBGuardianServer.UsbDeviceDto>()); originalAccepted = true; }
    catch { originalAccepted = false; }
    Record("Endpoint credential is bound to identity", impersonationDenied && originalAccepted,
        $"Other endpoint token rejected={impersonationDenied}; original session remains valid={originalAccepted}.");
    await agent.DisposeAsync();
    await control.UnlockAsync(id);
    var offline = (await control.GetAgentsAsync()).Single(a => a.MachineId == id);
    Record("Offline policy persistence", !offline.Online && !offline.IsLocked, "Offline endpoint retained with saved unlock policy.");
    var (reconnected, reconnectPolicies) = await Fake(id, token);
    policy = await Next(reconnectPolicies);
    Record("Reconnect policy replay", policy.IsUnlocked && policy.BadUsbBlock && policy.OpticalBlocked, "Saved policy delivered after reconnect.");
    await reconnected.DisposeAsync();
    using var blackholeListener = new TcpListener(IPAddress.Loopback, 0);
    blackholeListener.Start();
    int blackholePort = ((IPEndPoint)blackholeListener.LocalEndpoint).Port;
    string agentConfigPath = Path.Combine(state, "agent-config.json");
    File.WriteAllText(agentConfigPath, JsonSerializer.Serialize(new USBGuardianAgent.AgentConfig
        { ServerUrl = $"http://127.0.0.1:{blackholePort}", MachineId = "ISOLATED-NETWORK-PROBE", AgentToken = "probe" }));
    var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["AgentConfigPath"] = agentConfigPath,
        ["AgentPolicyCachePath"] = Path.Combine(state, "cache.json"),
        ["ManagedDeviceStatePath"] = Path.Combine(state, "managed.json"),
        ["AlertDirectory"] = Path.Combine(state, "alerts"),
        ["SecurityEventQueuePath"] = Path.Combine(state, "outbox.json")
    }).Build();
    using var monitor = new USBGuardianAgent.UsbDeviceMonitor(Microsoft.Extensions.Logging.Abstractions.NullLogger<USBGuardianAgent.UsbDeviceMonitor>.Instance);
    var hardware = new ProbeDevices();
    var manager = new USBGuardianAgent.UsbPolicyManager(Microsoft.Extensions.Logging.Abstractions.NullLogger<USBGuardianAgent.UsbPolicyManager>.Instance,
        new USBGuardianAgent.SecurityEventOutbox(config, Microsoft.Extensions.Logging.Abstractions.NullLogger<USBGuardianAgent.SecurityEventOutbox>.Instance), config, hardware, monitor);
    await using var client = new USBGuardianAgent.AgentSignalRClient(new USBGuardianAgent.AgentConfigManager(config,
        Microsoft.Extensions.Logging.Abstractions.NullLogger<USBGuardianAgent.AgentConfigManager>.Instance), manager,
        Microsoft.Extensions.Logging.Abstractions.NullLogger<USBGuardianAgent.AgentSignalRClient>.Instance);
    using var worker = new USBGuardianAgent.Worker(Microsoft.Extensions.Logging.Abstractions.NullLogger<USBGuardianAgent.Worker>.Instance, manager, monitor);
    using var communication = new USBGuardianAgent.CommunicationWorker(Microsoft.Extensions.Logging.Abstractions.NullLogger<USBGuardianAgent.CommunicationWorker>.Instance, manager, client);
    using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    await worker.StartAsync(cancel.Token);
    await communication.StartAsync(cancel.Token);
    try
    {
        using var socket = await blackholeListener.AcceptTcpClientAsync(cancel.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(6500), cancel.Token);
        Record("Enforcement continues during stalled network", hardware.Scans >= 2 && !communication.ExecuteTask!.IsCompleted,
            $"Independent hardware scans={hardware.Scans} while communication is pending. No real hardware toggled.");
    }
    finally
    {
        cancel.Cancel();
        await communication.StopAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);
    }
    var queries = (string[])typeof(USBGuardianAgent.UsbPolicyManager)
        .GetField("RemovableDeviceQueries", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    for (int index = 0; index < queries.Length; index++)
    {
        try
        {
            using var query = new ManagementObjectSearcher(queries[index]);
            using var devices = query.Get();
            Record($"Compiled removable WQL query {index + 1}", true, $"Query accepted; matching count={devices.Count}. Device IDs not printed.");
        }
        catch (ManagementException ex)
        {
            Record($"Compiled removable WQL query {index + 1}", false, $"Windows WMI rejects production query: {ex.ErrorCode}.");
        }
    }
    // Secure-profile gate exercised on a separate isolated HTTP listener. TLS
    // certificate trust and actual HTTPS deployment remain a manual release gate.
    if (!process.HasExited) process.Kill(entireProcessTree: true);
    await process.WaitForExitAsync();
    psi.Environment["Security__RequireHttps"] = "true";
    using var secureGuard = Process.Start(psi)!;
    var guardOutput = secureGuard.StandardOutput.ReadToEndAsync();
    var guardError = secureGuard.StandardError.ReadToEndAsync();
    try
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            try { if ((await http.GetAsync("/health")).IsSuccessStatusCode) break; }
            catch (HttpRequestException) { }
            if (secureGuard.HasExited) throw new Exception("Secure-profile guard failed to start.");
            await Task.Delay(100);
        }
        Record("Secure profile rejects plaintext admin API", (await http.GetAsync("/api/agents")).StatusCode == HttpStatusCode.Forbidden,
            "Authenticated HTTP request rejected by central transport boundary.");
        Record("Secure profile rejects plaintext SignalR negotiation", (await Post("/agenthub/negotiate?negotiateVersion=1")).StatusCode == HttpStatusCode.Forbidden,
            "HTTP negotiation rejected before registration.");
    }
    finally
    {
        if (!secureGuard.HasExited) secureGuard.Kill(entireProcessTree: true);
        await secureGuard.WaitForExitAsync();
        await guardOutput;
        await guardError;
    }

}
finally
{
    foreach (var client in clients) try { await client.DisposeAsync(); } catch { }
    if (!process.HasExited) process.Kill(entireProcessTree: true);
    await process.WaitForExitAsync();
    await File.WriteAllTextAsync(Path.Combine(evidence, "server-fixed.log"), await stdout + await stderr);
    // The state directory deliberately contains generated test credentials only.
    // Do not print them or inspect production ProgramData/registry settings.
    await File.WriteAllTextAsync(Path.Combine(evidence, "integration-fixed-results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
}

Environment.ExitCode = results.Any(r => JsonSerializer.Serialize(r).Contains("\"FAIL\"")) ? 1 : 0;

sealed class ProbeDevices : USBGuardianAgent.IUsbDeviceAccess
{
    private int _scans;
    public int Scans => Volatile.Read(ref _scans);
    public IReadOnlyList<USBGuardianAgent.DeviceSnapshot> Query(string wql)
    {
        if (wql == USBGuardianAgent.UsbDeviceQueries.Storage) Interlocked.Increment(ref _scans);
        return Array.Empty<USBGuardianAgent.DeviceSnapshot>();
    }
    public bool SetState(string verb, string id) => throw new InvalidOperationException("This probe must not toggle hardware.");
}
