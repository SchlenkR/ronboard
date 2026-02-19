namespace Ronboard.Api.Services;

using System.Text.Json;
using System.Text.Json.Serialization;
using Ronboard.Api.Models;

public class PersistenceService(ILogger<PersistenceService> logger)
{
    private readonly string _baseDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ronboard");

    private readonly string _sessionsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ronboard", "sessions");

    private readonly string _counterFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ronboard", "counter.json");

    private readonly SemaphoreSlim _counterLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly JsonSerializerOptions NdjsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(_sessionsDir);
    }

    // ── Counter ──

    public async Task<int> GetNextNumberAsync()
    {
        await _counterLock.WaitAsync();
        try
        {
            var current = 1;
            if (File.Exists(_counterFile))
            {
                var json = await File.ReadAllTextAsync(_counterFile);
                var counter = JsonSerializer.Deserialize<CounterData>(json, JsonOptions);
                current = counter?.NextNumber ?? 1;
            }

            await File.WriteAllTextAsync(_counterFile,
                JsonSerializer.Serialize(new CounterData { NextNumber = current + 1 }, JsonOptions));
            return current;
        }
        finally
        {
            _counterLock.Release();
        }
    }

    // ── Session Metadata ──

    public async Task SaveMetadataAsync(AgentSession session)
    {
        var dir = GetSessionDir(session.Id);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "metadata.json");
        var json = JsonSerializer.Serialize(session, JsonOptions);
        await File.WriteAllTextAsync(path, json);
    }

    public async Task<List<AgentSession>> LoadAllSessionsAsync()
    {
        var sessions = new List<AgentSession>();
        if (!Directory.Exists(_sessionsDir)) return sessions;

        foreach (var dir in Directory.GetDirectories(_sessionsDir))
        {
            var metadataPath = Path.Combine(dir, "metadata.json");
            if (!File.Exists(metadataPath)) continue;

            try
            {
                var json = await File.ReadAllTextAsync(metadataPath);
                var session = JsonSerializer.Deserialize<AgentSession>(json, JsonOptions);
                if (session is not null)
                {
                    session.Status = SessionStatus.Stopped;
                    sessions.Add(session);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load session from {Dir}", dir);
            }
        }

        return sessions;
    }

    public Task DeleteSessionAsync(Guid sessionId)
    {
        var dir = GetSessionDir(sessionId);
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
        return Task.CompletedTask;
    }

    // ── Terminal I/O Logging ──

    public async Task AppendTerminalOutputAsync(Guid sessionId, string data)
    {
        var path = Path.Combine(GetSessionDir(sessionId), "terminal.log");
        await File.AppendAllTextAsync(path, data);
    }

    public async Task AppendTerminalInputAsync(Guid sessionId, string data)
    {
        var path = Path.Combine(GetSessionDir(sessionId), "stdin.log");
        var entry = JsonSerializer.Serialize(new { timestamp = DateTime.UtcNow, input = data }, NdjsonOptions);
        await File.AppendAllTextAsync(path, entry + "\n");
    }

    public async Task<string> LoadTerminalHistoryAsync(Guid sessionId)
    {
        var path = Path.Combine(GetSessionDir(sessionId), "terminal.log");
        return File.Exists(path) ? await File.ReadAllTextAsync(path) : string.Empty;
    }

    // ── Stream I/O Logging ──

    public async Task AppendStreamMessageAsync(Guid sessionId, ClaudeMessage message)
    {
        var path = Path.Combine(GetSessionDir(sessionId), "stream.ndjson");
        var json = JsonSerializer.Serialize(message, NdjsonOptions);
        await File.AppendAllTextAsync(path, json + "\n");
    }

    public async Task AppendStreamInputAsync(Guid sessionId, string userMessage)
    {
        var path = Path.Combine(GetSessionDir(sessionId), "stdin.log");
        var entry = JsonSerializer.Serialize(new { timestamp = DateTime.UtcNow, message = userMessage }, NdjsonOptions);
        await File.AppendAllTextAsync(path, entry + "\n");
    }

    public async Task<List<ClaudeMessage>> LoadStreamHistoryAsync(Guid sessionId)
    {
        var path = Path.Combine(GetSessionDir(sessionId), "stream.ndjson");
        var messages = new List<ClaudeMessage>();
        if (!File.Exists(path)) return messages;

        foreach (var line in await File.ReadAllLinesAsync(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var msg = JsonSerializer.Deserialize<ClaudeMessage>(line, NdjsonOptions);
                if (msg is not null) messages.Add(msg);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to parse NDJSON line");
            }
        }

        return messages;
    }

    public async Task<List<string>> LoadInputHistoryAsync(Guid sessionId)
    {
        var path = Path.Combine(GetSessionDir(sessionId), "stdin.log");
        var inputs = new List<string>();
        if (!File.Exists(path)) return inputs;

        foreach (var line in await File.ReadAllLinesAsync(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("message", out var msg))
                    inputs.Add(msg.GetString() ?? "");
                else if (root.TryGetProperty("input", out var inp))
                    inputs.Add(inp.GetString() ?? "");
            }
            catch { /* skip malformed lines */ }
        }

        return inputs;
    }

    // ── Helpers ──

    private string GetSessionDir(Guid sessionId) =>
        Path.Combine(_sessionsDir, sessionId.ToString());

    private class CounterData
    {
        public int NextNumber { get; set; } = 1;
    }
}
