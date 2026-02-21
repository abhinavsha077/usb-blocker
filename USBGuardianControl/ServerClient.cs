using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace USBGuardianControl
{
    public class AgentSummary
    {
        [JsonPropertyName("machineId")]    public string MachineId    { get; set; } = string.Empty;
        [JsonPropertyName("machineName")]  public string MachineName  { get; set; } = string.Empty;
        [JsonPropertyName("ipAddress")]    public string IpAddress    { get; set; } = string.Empty;
        [JsonPropertyName("isLocked")]     public bool   IsLocked     { get; set; }
        [JsonPropertyName("online")]       public bool   Online       { get; set; }
        [JsonPropertyName("deviceCount")]  public int    DeviceCount  { get; set; }
    }

    public class UsbDeviceDto
    {
        [JsonPropertyName("deviceID")]            public string DeviceID            { get; set; } = string.Empty;
        [JsonPropertyName("name")]                public string Name                { get; set; } = string.Empty;
        [JsonPropertyName("status")]              public string Status              { get; set; } = string.Empty;
        [JsonPropertyName("isAllowed")]           public bool   IsAllowed           { get; set; }
        [JsonPropertyName("isHub")]               public bool   IsHub               { get; set; }
        [JsonPropertyName("connectedDeviceName")] public string ConnectedDeviceName { get; set; } = string.Empty;
    }

    public class ServerClient
    {
        private HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(6) };
        private string _password = string.Empty;
        public bool IsConfigured { get; private set; }

        public void Configure(string serverUrl, string password)
        {
            // Always create fresh HttpClient — BaseAddress cannot be changed after first request
            _http = new HttpClient
            {
                BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/"),
                Timeout     = TimeSpan.FromSeconds(6)
            };
            _password    = password;
            IsConfigured = true;
        }

        private void AddAuth(HttpRequestMessage req) =>
            req.Headers.Add("X-Admin-Password", _password);

        private async Task<T?> GetAsync<T>(string url)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            AddAuth(req);
            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return default;
            return await resp.Content.ReadFromJsonAsync<T>();
        }

        private async Task<string> PostAsync(string url, object? body = null)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url);
            AddAuth(req);
            if (body != null)
                req.Content = JsonContent.Create(body);
            var resp = await _http.SendAsync(req);
            return await resp.Content.ReadAsStringAsync();
        }

        public Task<List<AgentSummary>?> GetAgentsAsync()           => GetAsync<List<AgentSummary>>("api/agents");
        public Task<List<UsbDeviceDto>?> GetDevicesAsync(string id) => GetAsync<List<UsbDeviceDto>>($"api/agents/{id}/devices");

        public Task<string> LockAsync(string id)            => PostAsync($"api/agents/{id}/lock");
        public Task<string> UnlockAsync(string id)          => PostAsync($"api/agents/{id}/unlock");
        public Task<string> UnlockTimedAsync(string id, int mins) => PostAsync($"api/agents/{id}/unlock?minutes={mins}");
        public Task<string> AllowAsync(string id, string dev)     => PostAsync($"api/agents/{id}/allow/{Uri.EscapeDataString(dev)}");
        public Task<string> BlockAsync(string id, string dev)     => PostAsync($"api/agents/{id}/block/{Uri.EscapeDataString(dev)}");
        public Task<string> LockAllAsync()                  => PostAsync("api/agents/lockall");
        public Task<string> UnlockAllAsync()                => PostAsync("api/agents/unlockall");

        public Task<string> ChangePasswordAsync(string current, string newPwd) =>
            PostAsync("api/auth/changepassword", new { currentPassword = current, newPassword = newPwd });
    }
}
