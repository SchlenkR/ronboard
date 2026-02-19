namespace Ronboard.Api.Hubs;

using Microsoft.AspNetCore.SignalR;
using Ronboard.Api.Models;
using Ronboard.Api.Services;

public class SessionHub(SessionManager sessionManager, ILogger<SessionHub> logger) : Hub
{
    public IReadOnlyCollection<AgentSession> GetSessions() => sessionManager.GetAll();

    public async Task<AgentSession> CreateSession(
        string name, string workingDirectory,
        string mode = "terminal", string? model = null)
    {
        var sessionMode = mode.Equals("stream", StringComparison.OrdinalIgnoreCase)
            ? SessionMode.Stream
            : SessionMode.Terminal;

        var session = sessionManager.CreateSession(name, workingDirectory, sessionMode, model);

        if (session.Status != SessionStatus.Error)
        {
            // Resolve IHubContext NOW while Context is still valid.
            // Task.Run runs after CreateSession returns, when Context is already disposed.
            var hubContext = Context.GetHttpContext()!.RequestServices
                .GetRequiredService<IHubContext<SessionHub>>();

            if (session.Mode == SessionMode.Terminal)
                _ = Task.Run(() => BroadcastTerminalOutput(session.Id, hubContext));
            else
                _ = Task.Run(() => BroadcastStreamOutput(session.Id, hubContext));
        }

        await Clients.All.SendAsync("SessionCreated", session);
        return session;
    }

    // Terminal mode: raw keystrokes
    public async Task SendInput(Guid sessionId, string data)
    {
        await sessionManager.SendInputAsync(sessionId, data);
    }

    // Stream mode: user message text
    public async Task SendMessage(Guid sessionId, string message)
    {
        await sessionManager.SendStreamMessageAsync(sessionId, message);
    }

    public async Task JoinSession(Guid sessionId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, sessionId.ToString());

        var session = sessionManager.Get(sessionId);
        if (session is null) return;

        if (session.Mode == SessionMode.Terminal)
        {
            var history = sessionManager.GetTerminalHistory(sessionId);
            if (!string.IsNullOrEmpty(history))
                await Clients.Caller.SendAsync("TerminalHistory", history);
        }
        else
        {
            var messages = sessionManager.GetStreamHistory(sessionId);
            if (messages.Count > 0)
                await Clients.Caller.SendAsync("StreamHistory", messages);
        }
    }

    public async Task LeaveSession(Guid sessionId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, sessionId.ToString());
    }

    public async Task StopSession(Guid sessionId)
    {
        sessionManager.StopSession(sessionId);
        await Clients.All.SendAsync("SessionStopped", sessionId);
    }

    public async Task RemoveSession(Guid sessionId)
    {
        sessionManager.RemoveSession(sessionId);
        await Clients.All.SendAsync("SessionRemoved", sessionId);
    }

    private async Task BroadcastTerminalOutput(Guid sessionId, IHubContext<SessionHub> hubContext)
    {
        var channel = sessionManager.GetTerminalChannel(sessionId);
        if (channel is null) return;

        try
        {
            await foreach (var data in channel.ReadAllAsync())
            {
                sessionManager.AppendTerminalOutput(sessionId, data);
                await hubContext.Clients.Group(sessionId.ToString())
                    .SendAsync("TerminalOutput", sessionId, data);
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "Error broadcasting terminal {Id}", sessionId); }

        await hubContext.Clients.Group(sessionId.ToString())
            .SendAsync("SessionEnded", sessionId);
    }

    private async Task BroadcastStreamOutput(Guid sessionId, IHubContext<SessionHub> hubContext)
    {
        var channel = sessionManager.GetStreamChannel(sessionId);
        if (channel is null) return;

        try
        {
            await foreach (var msg in channel.ReadAllAsync())
            {
                sessionManager.AppendStreamMessage(sessionId, msg);
                await hubContext.Clients.Group(sessionId.ToString())
                    .SendAsync("StreamMessage", sessionId, msg);
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "Error broadcasting stream {Id}", sessionId); }

        await hubContext.Clients.Group(sessionId.ToString())
            .SendAsync("SessionEnded", sessionId);
    }
}
