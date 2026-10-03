using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace USBGuardianControl
{
    public class AgentSummary
    {
        [JsonPropertyName("machineId")] public string MachineId { get; set; } = string.Empty;
        [JsonPropertyName("machineName")] public string MachineName { get; set; } = string.Empty;
        [JsonPropertyName("ipAddress")] public string IpAddress { get; set; } = string.Empty;
        [JsonPropertyName("isLocked")] public bool IsLocked { get; set; }
        [JsonPropertyName("online")] public bool Online { get; set; }
        [JsonPropertyName("lastSeen")] public DateTime? LastSeen { get; set; }
        [JsonPropertyName("deviceCount")] public int DeviceCount { get; set; }
        [JsonPropertyName("learnMode")] public bool LearnMode { get; set; }
        [JsonPropertyName("learnModeExpiry")] public DateTime? LearnModeExpiry { get; set; }
        [JsonPropertyName("unlockExpiry")] public DateTime? UnlockExpiry { get; set; }
        [JsonPropertyName("group")] public string Group { get; set; } = "Default";
        [JsonPropertyName("opticalBlocked")] public bool OpticalBlocked { get; set; }
        [JsonPropertyName("bluetoothBlocked")] public bool BluetoothBlocked { get; set; }
        [JsonPropertyName("wifiBlocked")] public bool WifiBlocked { get; set; }
        [JsonPropertyName("badUsbBlock")] public bool BadUsbBlock { get; set; }
        [JsonPropertyName("policyRevision")] public long PolicyRevision { get; set; }
        [JsonPropertyName("appliedRevision")] public long? AppliedRevision { get; set; }
        [JsonPropertyName("enforcementUtc")] public DateTime? EnforcementUtc { get; set; }
        [JsonPropertyName("enforcementError")] public string EnforcementError { get; set; } = string.Empty;
    }

    public class GroupSummary
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("total")] public int Total { get; set; }
        [JsonPropertyName("online")] public int Online { get; set; }
        [JsonPropertyName("locked")] public int Locked { get; set; }
    }

    public class UsbDeviceDto
    {
        [JsonPropertyName("deviceID")] public string DeviceID { get; set; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
        [JsonPropertyName("isAllowed")] public bool IsAllowed { get; set; }
        [JsonPropertyName("isHub")] public bool IsHub { get; set; }
        [JsonPropertyName("connectedDeviceName")] public string ConnectedDeviceName { get; set; } = string.Empty;
        [JsonPropertyName("isRunning")] public bool? IsRunning { get; set; }
        [JsonPropertyName("enforcementError")] public string EnforcementError { get; set; } = string.Empty;
    }

    public class UsbEventDto
    {
        [JsonPropertyName("timestamp")] public DateTime Timestamp { get; set; }
        [JsonPropertyName("machineId")] public string MachineId { get; set; } = string.Empty;
        [JsonPropertyName("eventType")] public string EventType { get; set; } = string.Empty;
        [JsonPropertyName("deviceId")] public string DeviceId { get; set; } = string.Empty;
        [JsonPropertyName("deviceName")] public string DeviceName { get; set; } = string.Empty;
        [JsonPropertyName("detail")] public string Detail { get; set; } = string.Empty;
    }

    public sealed class ServerApiException : Exception
    {
        public HttpStatusCode StatusCode { get; }

        public ServerApiException(HttpStatusCode statusCode, string message)
            : base(message)
        {
            StatusCode = statusCode;
        }
    }

    public sealed class ServerConnectionException : Exception
    {
        public ServerConnectionException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    public class ServerClient : IDisposable
    {
        private HttpClient _http = NewHttpClient();
        private string _password = string.Empty;
        public bool IsConfigured { get; private set; }

        public void Configure(string serverUrl, string password)
        {
            if (!Uri.TryCreate(serverUrl?.Trim(), UriKind.Absolute, out var baseUri) ||
                (baseUri.Scheme != Uri.UriSchemeHttp &&
                 baseUri.Scheme != Uri.UriSchemeHttps) ||
                !string.IsNullOrEmpty(baseUri.UserInfo) || !string.IsNullOrEmpty(baseUri.Query) ||
                !string.IsNullOrEmpty(baseUri.Fragment) || baseUri.AbsolutePath != "/")
                throw new ArgumentException("Enter an HTTP or HTTPS server origin without credentials, path, query or fragment.");
            if (string.IsNullOrEmpty(password))
                throw new ArgumentException("Enter the admin password.");

            var replacement = NewHttpClient();
            replacement.BaseAddress = new Uri(baseUri.ToString().TrimEnd('/') + "/");
            var previous = _http;
            _http = replacement;
            previous.Dispose();

            _password = password;
            IsConfigured = true;
        }

        public Task<List<AgentSummary>> GetAgentsAsync() =>
            GetAsync<List<AgentSummary>>("api/agents");

        public Task<List<UsbDeviceDto>> GetDevicesAsync(string id) =>
            GetAsync<List<UsbDeviceDto>>($"api/agents/{Route(id)}/devices");

        public Task<List<UsbEventDto>> GetHistoryAsync(string id, int limit = 200) =>
            GetAsync<List<UsbEventDto>>(
                $"api/agents/{Route(id)}/history?limit={Math.Clamp(limit, 1, 2000)}");

        public Task<string> LockAsync(string id) =>
            PostAsync($"api/agents/{Route(id)}/lock");

        public Task<string> UnlockAsync(string id) =>
            PostAsync($"api/agents/{Route(id)}/unlock");

        public Task<string> UnlockTimedAsync(string id, int minutes) =>
            PostAsync($"api/agents/{Route(id)}/unlock?minutes={minutes}");

        public Task<string> AllowAsync(string id, string deviceId) =>
            PostAsync($"api/agents/{Route(id)}/allow/{Route(deviceId)}");

        public Task<string> BlockAsync(string id, string deviceId) =>
            PostAsync($"api/agents/{Route(id)}/block/{Route(deviceId)}");

        public Task<string> LockAllAsync() => PostAsync("api/agents/lockall");
        public Task<string> UnlockAllAsync() => PostAsync("api/agents/unlockall");

        public Task<string> LearnModeAsync(string id, bool on, int minutes = 480) =>
            PostAsync(
                $"api/agents/{Route(id)}/learnmode?on={on.ToString().ToLowerInvariant()}&minutes={minutes}");

        public Task<List<GroupSummary>> GetGroupsAsync() =>
            GetAsync<List<GroupSummary>>("api/groups");

        public Task<string> SetGroupAsync(string id, string name) =>
            PostAsync($"api/agents/{Route(id)}/group?name={Route(name)}");

        public Task<string> GroupLockAsync(string name) =>
            PostAsync($"api/groups/{Route(name)}/lock");

        public Task<string> GroupUnlockAsync(string name) =>
            PostAsync($"api/groups/{Route(name)}/unlock");

        public Task<string> SetSectorAsync(string id, string name, bool block) =>
            PostAsync(
                $"api/agents/{Route(id)}/sector?name={Route(name)}&block={block.ToString().ToLowerInvariant()}");

        public Task<string> SetBadUsbAsync(string id, bool block) =>
            PostAsync(
                $"api/agents/{Route(id)}/badusb?block={block.ToString().ToLowerInvariant()}");

        public Task<string> RebootAsync(string id) =>
            PostAsync($"api/agents/{Route(id)}/reboot");

        public Task<string> ShutdownAsync(string id) =>
            PostAsync($"api/agents/{Route(id)}/shutdown");

        public Task<string> ReloadAsync(string id) =>
            PostAsync($"api/agents/{Route(id)}/reload");

        public async Task<string> ChangePasswordAsync(string current, string newPassword)
        {
            string response = await PostAsync(
                "api/auth/changepassword",
                new { currentPassword = current, newPassword });
            _password = newPassword;
            return response;
        }

        // X-Admin-Password is a custom header; automatic redirects could forward
        // it to another origin. Require the operator to configure that origin.
        private static HttpClient NewHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

        private void AddAuth(HttpRequestMessage request)
        {
            if (!IsConfigured)
                throw new InvalidOperationException("The server client is not configured.");
            request.Headers.Add("X-Admin-Password", _password);
        }

        private async Task<T> GetAsync<T>(string url)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddAuth(request);
            using var response = await SendAsync(request);
            await EnsureSuccessAsync(response);
            return await response.Content.ReadFromJsonAsync<T>()
                ?? throw new InvalidDataException("The server returned an empty response.");
        }

        private async Task<string> PostAsync(string url, object? body = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            AddAuth(request);
            if (body != null) request.Content = JsonContent.Create(body);

            using var response = await SendAsync(request);
            await EnsureSuccessAsync(response);
            return NormalizeBody(await response.Content.ReadAsStringAsync());
        }

        private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
        {
            string endpoint = _http.BaseAddress?.ToString().TrimEnd('/')
                ?? "the configured server";
            try
            {
                return await _http.SendAsync(request);
            }
            catch (TaskCanceledException ex) when (!ex.CancellationToken.IsCancellationRequested)
            {
                throw new ServerConnectionException(
                    $"The USB Guardian Server at {endpoint} did not respond within " +
                    $"{_http.Timeout.TotalSeconds:0} seconds. Confirm the server service is " +
                    "running and the configured port is allowed through the firewall.",
                    ex);
            }
            catch (HttpRequestException ex)
            {
                throw new ServerConnectionException(
                    $"Cannot reach the USB Guardian Server at {endpoint}. Confirm the " +
                    "'USB Guardian Server' service is running and the configured TCP port " +
                    $"is listening. Details: {InnermostMessage(ex)}",
                    ex);
            }
        }

        private static string InnermostMessage(Exception exception)
        {
            while (exception.InnerException != null)
                exception = exception.InnerException;
            return exception.Message;
        }

        private static async Task EnsureSuccessAsync(HttpResponseMessage response)
        {
            if (response.IsSuccessStatusCode) return;

            string body = NormalizeBody(await response.Content.ReadAsStringAsync());
            string message = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized =>
                    "Authentication failed, or remote changes are disabled until the default password is changed.",
                HttpStatusCode.Forbidden =>
                    "The server refused this operation.",
                HttpStatusCode.NotFound =>
                    string.IsNullOrWhiteSpace(body) ? "The selected agent was not found." : body,
                _ => string.IsNullOrWhiteSpace(body)
                    ? $"Server request failed ({(int)response.StatusCode} {response.ReasonPhrase})."
                    : body
            };
            throw new ServerApiException(response.StatusCode, message);
        }

        private static string NormalizeBody(string body)
        {
            body = body?.Trim() ?? string.Empty;
            if (body.Length >= 2 && body[0] == '"' && body[^1] == '"')
            {
                try { return JsonSerializer.Deserialize<string>(body) ?? string.Empty; }
                catch (JsonException) { }
            }
            return body;
        }

        private static string Route(string value) =>
            Uri.EscapeDataString(value ?? string.Empty);

        public void Dispose() => _http.Dispose();
    }
}
