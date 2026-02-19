namespace Ronboard.Api.Models;

using System.Text.Json;

public class ClaudeMessage
{
    public int Index { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string Type { get; init; } = string.Empty;
    public JsonElement RawJson { get; init; }
}
