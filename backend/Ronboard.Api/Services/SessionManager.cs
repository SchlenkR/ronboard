namespace Ronboard.Api.Services;

using System.Collections.Concurrent;
using System.Threading.Channels;
using Ronboard.Api.Models;

public class SessionManager(ClaudeProcessService processService, ILogger<SessionManager> logger)
{
    private readonly ConcurrentDictionary<Guid, AgentSession> _sessions = new();

    // Terminal mode
    private readonly ConcurrentDictionary<Guid, ChannelReader<string>> _terminalChannels = new();
    private readonly ConcurrentDictionary<Guid, List<string>> _terminalBuffers = new();

    // Stream mode
    private readonly ConcurrentDictionary<Guid, ChannelReader<ClaudeMessage>> _streamChannels = new();
    private readonly ConcurrentDictionary<Guid, List<ClaudeMessage>> _streamMessages = new();

    public IReadOnlyCollection<AgentSession> GetAll() => _sessions.Values.ToList();
    public AgentSession? Get(Guid id) => _sessions.GetValueOrDefault(id);

    // ── Terminal mode accessors ──

    public ChannelReader<string>? GetTerminalChannel(Guid id) =>
        _terminalChannels.GetValueOrDefault(id);

    public string GetTerminalHistory(Guid id)
    {
        if (!_terminalBuffers.TryGetValue(id, out var buffer)) return string.Empty;
        lock (buffer) { return string.Concat(buffer); }
    }

    public void AppendTerminalOutput(Guid id, string data)
    {
        if (_terminalBuffers.TryGetValue(id, out var buffer))
            lock (buffer) { buffer.Add(data); }
    }

    // ── Stream mode accessors ──

    public ChannelReader<ClaudeMessage>? GetStreamChannel(Guid id) =>
        _streamChannels.GetValueOrDefault(id);

    public List<ClaudeMessage> GetStreamHistory(Guid id)
    {
        if (!_streamMessages.TryGetValue(id, out var msgs)) return [];
        lock (msgs) { return [..msgs]; }
    }

    public void AppendStreamMessage(Guid id, ClaudeMessage msg)
    {
        if (_streamMessages.TryGetValue(id, out var msgs))
            lock (msgs) { msgs.Add(msg); }
    }

    // ── Session lifecycle ──

    public AgentSession CreateSession(string name, string workingDirectory,
        SessionMode mode = SessionMode.Terminal, string? model = null)
    {
        var session = new AgentSession
        {
            Name = name,
            WorkingDirectory = workingDirectory,
            Mode = mode,
        };

        try
        {
            if (mode == SessionMode.Terminal)
            {
                var (process, output) = processService.StartTerminalProcess(workingDirectory, model);
                session.Process = process;
                _terminalChannels[session.Id] = output;
                _terminalBuffers[session.Id] = [];
            }
            else
            {
                var (process, output) = processService.StartStreamProcess(workingDirectory, model);
                session.Process = process;
                _streamChannels[session.Id] = output;
                _streamMessages[session.Id] = [];
            }

            session.Status = SessionStatus.Running;
            _sessions[session.Id] = session;

            session.Process!.EnableRaisingEvents = true;
            session.Process.Exited += (_, _) =>
            {
                session.Status = session.Process.ExitCode == 0
                    ? SessionStatus.Stopped
                    : SessionStatus.Error;
                logger.LogInformation("Session {Id} exited with code {Code}", session.Id, session.Process.ExitCode);
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start Claude process for session {Id}", session.Id);
            session.Status = SessionStatus.Error;
            _sessions[session.Id] = session;
        }

        return session;
    }

    public async Task SendInputAsync(Guid sessionId, string data)
    {
        if (_sessions.TryGetValue(sessionId, out var session) && session.Process is not null)
            await processService.SendInputAsync(session.Process, data);
    }

    public async Task SendStreamMessageAsync(Guid sessionId, string message)
    {
        if (_sessions.TryGetValue(sessionId, out var session) && session.Process is not null)
            await processService.SendStreamMessageAsync(session.Process, message);
    }

    public void StopSession(Guid sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session) && session.Process is not null)
        {
            try { session.Process.Kill(entireProcessTree: true); }
            catch (Exception ex) { logger.LogWarning(ex, "Error killing session {Id}", sessionId); }
            session.Status = SessionStatus.Stopped;
        }
    }

    public bool RemoveSession(Guid sessionId)
    {
        StopSession(sessionId);
        _terminalChannels.TryRemove(sessionId, out _);
        _terminalBuffers.TryRemove(sessionId, out _);
        _streamChannels.TryRemove(sessionId, out _);
        _streamMessages.TryRemove(sessionId, out _);
        return _sessions.TryRemove(sessionId, out _);
    }
}
