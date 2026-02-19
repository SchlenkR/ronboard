namespace Ronboard.Api.Services;

using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Ronboard.Api.Models;

public class ClaudeProcessService(ILogger<ClaudeProcessService> logger)
{
    // ── Terminal mode: PTY via `script`, raw byte relay ──

    public (Process Process, ChannelReader<string> Output) StartTerminalProcess(
        string workingDirectory, string? model = null)
    {
        var resolvedDir = ExpandPath(workingDirectory);
        if (!Directory.Exists(resolvedDir))
            throw new DirectoryNotFoundException($"Working directory not found: {resolvedDir}");

        var shell = Environment.GetEnvironmentVariable("SHELL") ?? "/bin/zsh";
        var claudeCmd = "claude";
        if (!string.IsNullOrEmpty(model))
            claudeCmd += $" --model {model}";

        var psi = new ProcessStartInfo
        {
            FileName = "/usr/bin/script",
            Arguments = $"-q /dev/null {shell} -l -c \"{claudeCmd}\"",
            WorkingDirectory = resolvedDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.Environment["TERM"] = "xterm-256color";
        psi.Environment["COLUMNS"] = "120";
        psi.Environment["LINES"] = "40";

        var process = new Process { StartInfo = psi };
        var channel = Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });

        process.Start();
        logger.LogInformation("Started terminal Claude process (PID {Pid}) in {Dir}", process.Id, resolvedDir);

        _ = Task.Run(async () =>
        {
            try
            {
                var buffer = new char[4096];
                while (true)
                {
                    var count = await process.StandardOutput.ReadAsync(buffer, 0, buffer.Length);
                    if (count == 0) break;
                    await channel.Writer.WriteAsync(new string(buffer, 0, count));
                }
            }
            catch (Exception ex) { logger.LogError(ex, "Error reading terminal stdout"); }
            finally { channel.Writer.Complete(); }
        });

        StartStderrReader(process);
        return (process, channel.Reader);
    }

    public async Task SendInputAsync(Process process, string data)
    {
        await process.StandardInput.WriteAsync(data);
        await process.StandardInput.FlushAsync();
    }

    // ── Stream mode: structured JSON via --print --stream-json ──

    public (Process Process, ChannelReader<ClaudeMessage> Output) StartStreamProcess(
        string workingDirectory, string? model = null)
    {
        var resolvedDir = ExpandPath(workingDirectory);
        if (!Directory.Exists(resolvedDir))
            throw new DirectoryNotFoundException($"Working directory not found: {resolvedDir}");

        var args = "--print --input-format stream-json --output-format stream-json --include-partial-messages --verbose";
        if (!string.IsNullOrEmpty(model))
            args += $" --model {model}";

        var shell = Environment.GetEnvironmentVariable("SHELL") ?? "/bin/zsh";

        var psi = new ProcessStartInfo
        {
            FileName = shell,
            Arguments = $"-l -c \"claude {args}\"",
            WorkingDirectory = resolvedDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var process = new Process { StartInfo = psi };
        var channel = Channel.CreateUnbounded<ClaudeMessage>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });

        process.Start();
        logger.LogInformation("Started stream Claude process (PID {Pid}) in {Dir}", process.Id, resolvedDir);

        _ = Task.Run(async () =>
        {
            var index = 0;
            try
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var json = JsonDocument.Parse(line);
                        var type = json.RootElement.TryGetProperty("type", out var t)
                            ? t.GetString() ?? "unknown"
                            : "unknown";

                        await channel.Writer.WriteAsync(new ClaudeMessage
                        {
                            Index = index++,
                            Type = type,
                            RawJson = json.RootElement.Clone(),
                        });
                    }
                    catch (JsonException)
                    {
                        logger.LogDebug("Non-JSON output: {Line}", line);
                    }
                }
            }
            catch (Exception ex) { logger.LogError(ex, "Error reading stream stdout"); }
            finally { channel.Writer.Complete(); }
        });

        StartStderrReader(process);
        return (process, channel.Reader);
    }

    public async Task SendStreamMessageAsync(Process process, string userMessage)
    {
        var input = new
        {
            type = "user",
            message = new { role = "user", content = userMessage }
        };
        var json = JsonSerializer.Serialize(input);
        await process.StandardInput.WriteLineAsync(json);
        await process.StandardInput.FlushAsync();
    }

    // ── Shared ──

    private void StartStderrReader(Process process)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardError.ReadLineAsync() is { } line)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        logger.LogWarning("Claude stderr: {Line}", line);
                }
            }
            catch { }
        });
    }

    public static string ExpandPath(string path)
    {
        if (path.StartsWith('~'))
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[1..].TrimStart('/'));
        return Path.GetFullPath(path);
    }
}
