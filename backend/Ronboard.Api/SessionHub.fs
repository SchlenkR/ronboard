namespace Ronboard.Api.Hubs

open System
open Microsoft.AspNetCore.SignalR
open Microsoft.Extensions.Logging
open Ronboard.Api.Models
open Ronboard.Api.Services

type SessionHub(sessionOps: ISessionOperations, logger: ILogger<SessionHub>) =
    inherit Hub()

    member this.SendMessage(sessionId: Guid, message: string) =
        task {
            logger.LogInformation("User message for session {SessionId}: {Message}", sessionId, message)
            do! sessionOps.SendStreamMessageAsync(sessionId, message)

            let msgs = sessionOps.GetStreamHistory(sessionId)
            let userMsg = msgs |> List.tryFindBack (fun m -> m.Type = "user_message")

            match userMsg with
            | Some msg ->
                do!
                    this.Clients
                        .OthersInGroup(sessionId.ToString())
                        .SendCoreAsync("StreamMessage", [| sessionId; msg |])
            | None -> ()
        }

    member this.JoinSession(sessionId: Guid) =
        task {
            do! this.Groups.AddToGroupAsync(this.Context.ConnectionId, sessionId.ToString())

            match sessionOps.Get(sessionId) with
            | None -> ()
            | Some _ ->
                let messages = sessionOps.GetStreamHistory(sessionId)

                if messages.Length > 0 then
                    do! this.Clients.Caller.SendCoreAsync("StreamHistory", [| messages |])

                let activity = sessionOps.GetActivityState(sessionId)

                let stateName =
                    match activity with
                    | ActivityState.ToolUse -> "tool_use"
                    | ActivityState.Busy -> "busy"
                    | ActivityState.Idle -> "idle"

                do! this.Clients.Caller.SendCoreAsync("SessionActivity", [| sessionId; stateName |])
        }

    member this.LeaveSession(sessionId: Guid) =
        task { do! this.Groups.RemoveFromGroupAsync(this.Context.ConnectionId, sessionId.ToString()) }
