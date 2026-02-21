using USBGuardianServer;

var builder = WebApplication.CreateBuilder(args);

// Run as Windows Service
builder.Host.UseWindowsService();

// Services
builder.Services.AddSingleton<PolicyStore>();
builder.Services.AddSingleton<ServerConfigManager>();
builder.Services.AddSignalR();
builder.Services.AddControllers();
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

// Listen on all interfaces so agents on LAN can reach the server
builder.WebHost.UseUrls("http://0.0.0.0:5050");

var app = builder.Build();

app.UseCors();
app.MapControllers();
app.MapHub<AgentHub>("/agenthub");

app.Run();
