using System.Text.Json.Serialization;
using Ronboard.Api.Hubs;
using Ronboard.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR()
    .AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.PropertyNamingPolicy =
            System.Text.Json.JsonNamingPolicy.CamelCase;
        options.PayloadSerializerOptions.Converters.Add(
            new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
    });

builder.Services.AddSingleton<ClaudeProcessService>();
builder.Services.AddSingleton<PersistenceService>();
builder.Services.AddSingleton<SessionManager>();
builder.Services.AddSingleton<AutoNamingService>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
    {
        policy.WithOrigins("http://localhost:4200")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

var app = builder.Build();

// Load persisted sessions before accepting connections
var sessionManager = app.Services.GetRequiredService<SessionManager>();
await sessionManager.LoadPersistedSessionsAsync();

// Wire up auto-naming
var autoNaming = app.Services.GetRequiredService<AutoNamingService>();
autoNaming.Initialize(sessionManager);

app.UseWebSockets();
app.UseCors("Frontend");

app.MapHub<SessionHub>("/hubs/session").RequireCors("Frontend");

app.Lifetime.ApplicationStopping.Register(() =>
{
    foreach (var session in sessionManager.GetAll())
    {
        sessionManager.StopSession(session.Id);
    }
});

app.Run();
