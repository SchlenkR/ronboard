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
builder.Services.AddSingleton<SessionManager>();

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

app.UseWebSockets();
app.UseCors("Frontend");

app.MapHub<SessionHub>("/hubs/session").RequireCors("Frontend");

app.Lifetime.ApplicationStopping.Register(() =>
{
    var manager = app.Services.GetRequiredService<SessionManager>();
    foreach (var session in manager.GetAll())
    {
        manager.StopSession(session.Id);
    }
});

app.Run();
