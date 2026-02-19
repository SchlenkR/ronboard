namespace Ronboard.Api.Models;

public enum SessionStatus
{
    Starting,
    Running,
    Idle,
    Stopped,
    Error
}

public enum SessionMode
{
    Terminal,
    Stream
}

public class AgentSession
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public SessionStatus Status { get; set; } = SessionStatus.Starting;
    public SessionMode Mode { get; set; } = SessionMode.Terminal;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    [System.Text.Json.Serialization.JsonIgnore]
    public System.Diagnostics.Process? Process { get; set; }
}
