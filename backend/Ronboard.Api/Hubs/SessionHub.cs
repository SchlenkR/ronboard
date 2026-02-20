namespace Ronboard.Api.Hubs;

using Microsoft.AspNetCore.SignalR;
using Ronboard.Api.Services;

public class SessionHub(SessionManager sessionManager) : Hub
{
    public async Task SendInput(Guid sessionId, string data)
    {
        await sessionManager.SendInputAsync(sessionId, data);
    }

    public async Task SendMessage(Guid sessionId, string message)
    {
        await sessionManager.SendStreamMessageAsync(sessionId, message);

        // Broadcast user message to other clients in the group (sender already has it locally)
        var msgs = sessionManager.GetStreamHistory(sessionId);
        var userMsg = msgs.LastOrDefault(m => m.Type == "user_message");
        if (userMsg is not null)
            await Clients.OthersInGroup(sessionId.ToString()).SendAsync("StreamMessage", sessionId, userMsg);
    }

    public async Task JoinSession(Guid sessionId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, sessionId.ToString());

        var session = sessionManager.Get(sessionId);
        if (session is null) return;

        if (session.Mode == Models.SessionMode.Terminal)
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
}
