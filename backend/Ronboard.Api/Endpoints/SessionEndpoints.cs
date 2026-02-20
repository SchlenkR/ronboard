namespace Ronboard.Api.Endpoints;

using Microsoft.AspNetCore.SignalR;
using Ronboard.Api.Hubs;
using Ronboard.Api.Models;
using Ronboard.Api.Services;

public static class SessionEndpoints
{
    public static void MapSessionApi(this WebApplication app)
    {
        var group = app.MapGroup("/api/sessions").RequireCors("Frontend");

        group.MapGet("/", ListSessions);
        group.MapGet("/{id:guid}", GetSession);
        group.MapGet("/{id:guid}/content", GetContent);
        group.MapPost("/", CreateSession);
        group.MapPost("/{id:guid}/resume", ResumeSession);
        group.MapPatch("/{id:guid}/name", RenameSession);
        group.MapPost("/{id:guid}/stop", StopSession);
        group.MapDelete("/{id:guid}", DeleteSession);
    }

    // ── Queries ──

    private static IResult ListSessions(SessionManager sessions)
    {
        var all = sessions.GetAll().Select(s => new
        {
            s.Id,
            s.Number,
            s.Name,
            s.WorkingDirectory,
            Status = s.Status.ToString().ToLowerInvariant(),
            Mode = s.Mode.ToString().ToLowerInvariant(),
            s.CreatedAt,
            s.LastUsedAt,
            IsRunning = s.Process is not null && !s.Process.HasExited,
        });

        return Results.Ok(all);
    }

    private static IResult GetSession(Guid id, SessionManager sessions)
    {
        var session = sessions.Get(id);
        if (session is null) return Results.NotFound();

        return Results.Ok(new
        {
            session.Id,
            session.Number,
            session.Name,
            session.WorkingDirectory,
            Status = session.Status.ToString().ToLowerInvariant(),
            Mode = session.Mode.ToString().ToLowerInvariant(),
            session.CreatedAt,
            session.LastUsedAt,
            IsRunning = session.Process is not null && !session.Process.HasExited,
        });
    }

    private static IResult GetContent(Guid id, SessionManager sessions)
    {
        var session = sessions.Get(id);
        if (session is null) return Results.NotFound();

        if (session.Mode == SessionMode.Terminal)
        {
            var history = sessions.GetTerminalHistory(id);
            return Results.Ok(new { session.Mode, Terminal = history });
        }

        var messages = sessions.GetStreamHistory(id);
        return Results.Ok(new { session.Mode, Messages = messages });
    }

    // ── Actions ──

    private record CreateRequest(string Name, string WorkingDirectory, string Mode = "terminal", string? Model = null);

    private static async Task<IResult> CreateSession(CreateRequest request, SessionManager sessions, IHubContext<SessionHub> hub, ILogger<SessionManager> logger)
    {
        var mode = request.Mode.Equals("stream", StringComparison.OrdinalIgnoreCase)
            ? SessionMode.Stream
            : SessionMode.Terminal;

        var session = await sessions.CreateSession(request.Name, request.WorkingDirectory, mode, request.Model);

        if (session.Status != SessionStatus.Error)
            StartBroadcastLoop(session, sessions, hub, logger);

        await hub.Clients.All.SendAsync("SessionCreated", session);
        return Results.Ok(session);
    }

    private static async Task<IResult> ResumeSession(Guid id, SessionManager sessions, IHubContext<SessionHub> hub, ILogger<SessionManager> logger)
    {
        var session = sessions.Get(id);
        if (session is null) return Results.NotFound();

        var resumed = await sessions.ResumeSessionAsync(id);
        StartBroadcastLoop(resumed, sessions, hub, logger);

        await hub.Clients.All.SendAsync("SessionResumed", resumed);
        return Results.Ok(resumed);
    }

    private record RenameRequest(string Name);

    private static async Task<IResult> RenameSession(Guid id, RenameRequest request, SessionManager sessions, IHubContext<SessionHub> hub)
    {
        var session = sessions.Get(id);
        if (session is null) return Results.NotFound();

        await sessions.RenameSession(id, request.Name);
        await hub.Clients.All.SendAsync("SessionRenamed", id, request.Name);

        return Results.Ok(new { id, request.Name });
    }

    private static async Task<IResult> StopSession(Guid id, SessionManager sessions, IHubContext<SessionHub> hub)
    {
        var session = sessions.Get(id);
        if (session is null) return Results.NotFound();

        sessions.StopSession(id);
        await hub.Clients.All.SendAsync("SessionStopped", id);

        return Results.Ok(new { id, Status = "stopped" });
    }

    private static async Task<IResult> DeleteSession(Guid id, SessionManager sessions, IHubContext<SessionHub> hub)
    {
        if (!sessions.RemoveSession(id)) return Results.NotFound();

        await hub.Clients.All.SendAsync("SessionRemoved", id);

        return Results.Ok(new { id, Deleted = true });
    }

    // ── Broadcast loops ──

    private static void StartBroadcastLoop(AgentSession session, SessionManager sessions, IHubContext<SessionHub> hub, ILogger logger)
    {
        if (session.Mode == SessionMode.Terminal)
            _ = Task.Run(() => BroadcastTerminalOutput(session.Id, sessions, hub, logger));
        else
            _ = Task.Run(() => BroadcastStreamOutput(session.Id, sessions, hub, logger));
    }

    private static async Task BroadcastTerminalOutput(Guid sessionId, SessionManager sessions, IHubContext<SessionHub> hub, ILogger logger)
    {
        var channel = sessions.GetTerminalChannel(sessionId);
        if (channel is null) return;

        try
        {
            await foreach (var data in channel.ReadAllAsync())
            {
                sessions.AppendTerminalOutput(sessionId, data);
                await hub.Clients.Group(sessionId.ToString()).SendAsync("TerminalOutput", sessionId, data);
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "Error broadcasting terminal {Id}", sessionId); }

        await hub.Clients.Group(sessionId.ToString()).SendAsync("SessionEnded", sessionId);
    }

    private static async Task BroadcastStreamOutput(Guid sessionId, SessionManager sessions, IHubContext<SessionHub> hub, ILogger logger)
    {
        var channel = sessions.GetStreamChannel(sessionId);
        if (channel is null) return;

        try
        {
            await foreach (var msg in channel.ReadAllAsync())
            {
                sessions.AppendStreamMessage(sessionId, msg);
                await hub.Clients.Group(sessionId.ToString()).SendAsync("StreamMessage", sessionId, msg);
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "Error broadcasting stream {Id}", sessionId); }

        await hub.Clients.Group(sessionId.ToString()).SendAsync("SessionEnded", sessionId);
    }
}
