namespace Ronboard.Api.Services;

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Ronboard.Api.Models;
using Ronboard.Api.Prompts;

public class SessionManager(
    ClaudeProcessService processService,
    PersistenceService persistence,
    ILogger<SessionManager> logger)
{
    private readonly ConcurrentDictionary<Guid, AgentSession> _sessions = new();

    // Terminal mode
    private readonly ConcurrentDictionary<Guid, ChannelReader<string>> _terminalChannels = new();
    private readonly ConcurrentDictionary<Guid, List<string>> _terminalBuffers = new();

    // Stream mode
    private readonly ConcurrentDictionary<Guid, ChannelReader<ClaudeMessage>> _streamChannels = new();
    private readonly ConcurrentDictionary<Guid, List<ClaudeMessage>> _streamMessages = new();

    // Auto-naming callback (set by AutoNamingService to avoid circular DI)
    public Action<Guid>? OnOutputReceived { get; set; }

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

        _ = persistence.AppendTerminalOutputAsync(id, data);
        UpdateLastUsedAt(id);
        OnOutputReceived?.Invoke(id);
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

        _ = persistence.AppendStreamMessageAsync(id, msg);
        UpdateLastUsedAt(id);
        OnOutputReceived?.Invoke(id);
    }

    // ── Session lifecycle ──

    public async Task<AgentSession> CreateSession(string name, string workingDirectory, SessionMode mode = SessionMode.Terminal, string? model = null)
    {
        var number = await persistence.GetNextNumberAsync();

        var session = new AgentSession
        {
            Number = number,
            Name = name,
            WorkingDirectory = workingDirectory,
            Mode = mode,
        };

        try
        {
            StartProcess(session, mode, workingDirectory, model);
            session.Status = SessionStatus.Running;
            _sessions[session.Id] = session;
            SetupProcessExitHandler(session);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start Claude process for session {Id}", session.Id);
            session.Status = SessionStatus.Error;
            _sessions[session.Id] = session;
        }

        await persistence.SaveMetadataAsync(session);
        return session;
    }

    public async Task SendInputAsync(Guid sessionId, string data)
    {
        if (_sessions.TryGetValue(sessionId, out var session) && session.Process is not null)
        {
            await processService.SendInputAsync(session.Process, data);
            _ = persistence.AppendTerminalInputAsync(sessionId, data);
            UpdateLastUsedAt(sessionId);
        }
    }

    public async Task SendStreamMessageAsync(Guid sessionId, string message)
    {
        if (_sessions.TryGetValue(sessionId, out var session) && session.Process is not null)
        {
            // Store user message in stream history (for persistence + replay)
            var userMsg = CreateUserMessage(sessionId, message);
            AppendStreamMessage(sessionId, userMsg);

            await processService.SendStreamMessageAsync(session.Process, message);
            _ = persistence.AppendStreamInputAsync(sessionId, message);
            UpdateLastUsedAt(sessionId);
        }
    }

    private ClaudeMessage CreateUserMessage(Guid sessionId, string text)
    {
        var nextIndex = _streamMessages.TryGetValue(sessionId, out var msgs) ? msgs.Count : 0;
        var rawJson = JsonSerializer.SerializeToElement(new { text });
        return new ClaudeMessage { Index = nextIndex, Type = "user_message", RawJson = rawJson };
    }

    public void StopSession(Guid sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session)) return;
        if (session.Process is not null && !session.Process.HasExited)
        {
            try { session.Process.Kill(entireProcessTree: true); }
            catch (Exception ex) { logger.LogWarning(ex, "Error killing session {Id}", sessionId); }
        }
        session.Status = SessionStatus.Stopped;
        _ = persistence.SaveMetadataAsync(session);
    }

    public async Task RenameSession(Guid sessionId, string name)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            throw new InvalidOperationException("Session not found");

        session.Name = name;
        await persistence.SaveMetadataAsync(session);
    }

    public bool RemoveSession(Guid sessionId)
    {
        StopSession(sessionId);
        _terminalChannels.TryRemove(sessionId, out _);
        _terminalBuffers.TryRemove(sessionId, out _);
        _streamChannels.TryRemove(sessionId, out _);
        _streamMessages.TryRemove(sessionId, out _);
        _ = persistence.DeleteSessionAsync(sessionId);
        return _sessions.TryRemove(sessionId, out _);
    }

    // ── Persistence: Load + Resume ──

    public async Task LoadPersistedSessionsAsync()
    {
        persistence.EnsureDirectories();
        var sessions = await persistence.LoadAllSessionsAsync();
        foreach (var session in sessions)
        {
            _sessions[session.Id] = session;

            if (session.Mode == SessionMode.Terminal)
            {
                var history = await persistence.LoadTerminalHistoryAsync(session.Id);
                _terminalBuffers[session.Id] = string.IsNullOrEmpty(history) ? [] : [history];
            }
            else
            {
                var msgs = await persistence.LoadStreamHistoryAsync(session.Id);
                _streamMessages[session.Id] = msgs;
            }
        }

        logger.LogInformation("Loaded {Count} persisted sessions", sessions.Count);
    }

    public async Task<AgentSession> ResumeSessionAsync(Guid sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            throw new InvalidOperationException("Session not found");

        if (session.Process is not null && !session.Process.HasExited)
            return session; // already running

        var inputs = await persistence.LoadInputHistoryAsync(sessionId);

        if (session.Mode == SessionMode.Terminal)
        {
            var (process, output) = processService.StartTerminalProcess(session.WorkingDirectory);
            session.Process = process;
            _terminalChannels[session.Id] = output;
            // Keep existing buffer (old output stays visible)

            // Feed old conversation context (strip ANSI codes)
            if (inputs.Count > 0)
            {
                var context = string.Join("\n", inputs);
                var stripped = StripAnsiCodes(context);
                if (!string.IsNullOrWhiteSpace(stripped))
                {
                    var resumePrompt = ResumePrompt.Build(inputs);
                    // Wait for Claude to initialize
                    await Task.Delay(2000);
                    await processService.SendInputAsync(process, resumePrompt);
                }
            }
        }
        else
        {
            var (process, output) = processService.StartStreamProcess(session.WorkingDirectory);
            session.Process = process;
            _streamChannels[session.Id] = output;
            // Keep existing stream messages

            if (inputs.Count > 0)
            {
                var resumePrompt = ResumePrompt.Build(inputs);
                await processService.SendStreamMessageAsync(process, resumePrompt);
            }
        }

        session.Status = SessionStatus.Running;
        session.LastUsedAt = DateTime.UtcNow;
        SetupProcessExitHandler(session);
        await persistence.SaveMetadataAsync(session);

        return session;
    }

    /// <summary>Get accumulated text for auto-naming (stream: text deltas, terminal: raw output).</summary>
    public string GetAccumulatedText(Guid sessionId)
    {
        var session = Get(sessionId);
        if (session is null) return string.Empty;

        if (session.Mode == SessionMode.Terminal)
        {
            var raw = GetTerminalHistory(sessionId);
            return StripAnsiCodes(raw);
        }

        // Stream: extract text_delta content
        var msgs = GetStreamHistory(sessionId);
        var sb = new StringBuilder();
        foreach (var msg in msgs)
        {
            if (msg.Type != "stream_event") continue;
            try
            {
                if (msg.RawJson.TryGetProperty("event", out var evt) &&
                    evt.TryGetProperty("type", out var t) &&
                    t.GetString() == "content_block_delta" &&
                    evt.TryGetProperty("delta", out var delta) &&
                    delta.TryGetProperty("type", out var dt) &&
                    dt.GetString() == "text_delta" &&
                    delta.TryGetProperty("text", out var text))
                {
                    sb.Append(text.GetString());
                }
            }
            catch { /* skip malformed */ }
        }

        return sb.ToString();
    }

    // ── Private helpers ──

    private void StartProcess(AgentSession session, SessionMode mode, string workingDirectory, string? model)
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
    }

    private void SetupProcessExitHandler(AgentSession session)
    {
        session.Process!.EnableRaisingEvents = true;
        session.Process.Exited += (_, _) =>
        {
            session.Status = session.Process.ExitCode == 0
                ? SessionStatus.Stopped
                : SessionStatus.Error;
            logger.LogInformation("Session {Id} exited with code {Code}", session.Id, session.Process.ExitCode);
            _ = persistence.SaveMetadataAsync(session);
        };
    }

    private void UpdateLastUsedAt(Guid sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.LastUsedAt = DateTime.UtcNow;
            // Metadata save is batched — not every single output chunk
        }
    }

    private static string StripAnsiCodes(string text) =>
        Regex.Replace(text, @"\x1B\[[0-9;]*[a-zA-Z]|\x1B\].*?\x07|\x1B[()][A-B]", "");
}
