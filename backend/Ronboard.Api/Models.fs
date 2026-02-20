namespace Ronboard.Api.Models

open System
open System.Diagnostics
open System.Text.Json
open System.Text.Json.Serialization
open Ronboard.Api

[<JsonConverter(typeof<DuStringConverter<SessionStatus>>)>]
type SessionStatus =
    | Starting
    | Running
    | Idle
    | Stopped
    | Error

[<JsonConverter(typeof<DuStringConverter<SessionMode>>)>]
type SessionMode =
    | Terminal
    | Stream

[<RequireQualifiedAccess>]
[<JsonConverter(typeof<DuStringConverter<ActivityState>>)>]
type ActivityState =
    | Idle
    | Busy
    | ToolUse

[<CLIMutable>]
type AgentSession =
    { Id: Guid
      mutable Number: int
      mutable Name: string
      mutable WorkingDirectory: string
      mutable Status: SessionStatus
      mutable Mode: SessionMode
      CreatedAt: DateTime
      mutable LastUsedAt: DateTime
      mutable ClaudeSessionCreated: bool
      [<JsonIgnore>]
      mutable Process: Process option }

module AgentSession =
    let create () =
        { Id = Guid.NewGuid()
          Number = 0
          Name = ""
          WorkingDirectory = ""
          Status = Starting
          Mode = Terminal
          CreatedAt = DateTime.UtcNow
          LastUsedAt = DateTime.UtcNow
          ClaudeSessionCreated = false
          Process = None }

    let isRunning (s: AgentSession) =
        match s.Process with
        | Some p -> not p.HasExited
        | None -> false

[<CLIMutable>]
type ClaudeMessage =
    { Index: int
      Timestamp: DateTime
      Type: string
      RawJson: JsonElement }
