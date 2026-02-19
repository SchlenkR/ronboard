namespace Ronboard.Api.Services;

using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using Ronboard.Api.Hubs;

public class AutoNamingService(
    ILogger<AutoNamingService> logger,
    PersistenceService persistence,
    IHubContext<SessionHub> hubContext)
{
    private const int MinTextLengthForNaming = 200;
    private readonly ConcurrentDictionary<Guid, bool> _pendingNaming = new();

    /// <summary>
    /// Wire up to SessionManager. Called once at startup since SessionManager
    /// can't directly depend on AutoNamingService (circular DI).
    /// </summary>
    public void Initialize(SessionManager sessionManager)
    {
        sessionManager.OnOutputReceived = sessionId =>
        {
            var session = sessionManager.Get(sessionId);
            if (session is null || session.Name != "Untitled") return;

            var text = sessionManager.GetAccumulatedText(sessionId);
            if (text.Length < MinTextLengthForNaming) return;
            if (!_pendingNaming.TryAdd(sessionId, true)) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    var title = await GenerateTitleAsync(text);
                    if (string.IsNullOrWhiteSpace(title)) return;

                    title = title.Trim().Trim('"').Trim();
                    if (title.Length > 60) title = title[..60];

                    session.Name = title;
                    await persistence.SaveMetadataAsync(session);
                    await hubContext.Clients.All.SendAsync("SessionRenamed", sessionId, title);

                    logger.LogInformation("Auto-named session {Id} to '{Title}'", sessionId, title);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Auto-naming failed for session {Id}", sessionId);
                }
                finally
                {
                    _pendingNaming.TryRemove(sessionId, out _);
                }
            });
        };
    }

    private async Task<string> GenerateTitleAsync(string text)
    {
        // Truncate to keep the prompt short
        var snippet = text.Length > 500 ? text[..500] : text;

        var shell = Environment.GetEnvironmentVariable("SHELL") ?? "/bin/zsh";

        var psi = new ProcessStartInfo
        {
            FileName = shell,
            Arguments = "-l -c \"claude --print\"",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var prompt = $"Given this conversation snippet, suggest a very short title (2-4 words, no quotes, no punctuation, no explanation - ONLY the title):\n\n{snippet}";
        await process.StandardInput.WriteAsync(prompt);
        process.StandardInput.Close();

        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        return output.Trim();
    }
}
