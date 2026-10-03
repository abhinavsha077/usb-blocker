using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using USBGuardianServer;

var builder = WebApplication.CreateBuilder(args);

// Run as Windows Service
builder.Host.UseWindowsService();

// Services
builder.Services.AddSingleton<PolicyStore>();
builder.Services.AddSingleton<MachineInventoryStore>();
builder.Services.AddSingleton<ServerConfigManager>();
builder.Services.AddSingleton<EventStore>();
builder.Services.AddSingleton<EndpointCredentialStore>();
builder.Services.AddSignalR(options =>
{
    // Device snapshots are small. Bound inbound payloads so a registered or
    // unauthenticated network client cannot force unbounded allocations.
    options.MaximumReceiveMessageSize = 512 * 1024;
});
builder.Services.AddControllers();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("admin-api", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

// CORS is only needed by an explicitly configured browser client. The WPF
// dashboard is not subject to CORS, so an empty allow-list exposes no browser
// origin by default.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();
if (allowedOrigins is { Length: > 0 })
{
    builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
        policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod()
    ));
}

// Preserve the LAN default while allowing managed deployments and tests to
// select a different bind address or port.
string bindAddress = builder.Configuration["Http:BindAddress"] ?? "0.0.0.0";
int httpPort = builder.Configuration.GetValue("Http:Port", 5050);
if (httpPort is < 1 or > 65535)
    throw new InvalidOperationException("Http:Port must be between 1 and 65535.");
bool httpEnabled = builder.Configuration.GetValue("Http:Enabled", true);
var urls = new List<string>();
if (httpEnabled)
    urls.Add($"http://{bindAddress}:{httpPort}");
if (builder.Configuration.GetValue<bool>("Https:Enabled"))
{
    var httpsPort = builder.Configuration.GetValue("Https:Port", 5443);
    if (httpsPort is < 1 or > 65535)
        throw new InvalidOperationException(
            "Https:Port must be between 1 and 65535.");
    urls.Add($"https://{bindAddress}:{httpsPort}");
}
if (urls.Count == 0)
    throw new InvalidOperationException(
        "Enable at least one HTTP or HTTPS listener.");
builder.WebHost.UseUrls(urls.ToArray());

var app = builder.Build();

// Touch the config manager at startup so its security warnings are logged immediately.
app.Services.GetRequiredService<ServerConfigManager>();
app.Services.GetRequiredService<EndpointCredentialStore>();

// Secure deployments reject every plaintext credential-bearing path before
// routing, including SignalR negotiation and transport upgrades.
if (builder.Configuration.GetValue<bool>("Security:RequireHttps"))
{
    app.Use(async (context, next) =>
    {
        if (!context.Request.IsHttps &&
            (context.Request.Path.StartsWithSegments("/api") ||
             context.Request.Path.StartsWithSegments("/agenthub")))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        await next(context);
    });
}

if (allowedOrigins is { Length: > 0 })
    app.UseCors();
app.UseRateLimiter();
app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    utc = DateTime.UtcNow
})).DisableRateLimiting();
app.MapControllers().RequireRateLimiting("admin-api");
app.MapHub<AgentHub>("/agenthub");

app.Run();
