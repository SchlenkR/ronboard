namespace Ronboard.Api.Controllers

open System
open System.Timers
open System.Threading.Tasks
open Microsoft.AspNetCore.Mvc
open Microsoft.AspNetCore.SignalR
open Microsoft.Extensions.Logging
open Ronboard.Api.Hubs
open Ronboard.Api.Models
open Ronboard.Api.Services

[<CLIMutable>]
type CreateRequest =
    { Name: string
      WorkingDirectory: string
      Mode: string
      Model: string }

[<CLIMutable>]
type RenameRequest = { Name: string }

[<ApiController>]
[<Route("api/sessions")>]
type SessionController
    (
        sessions: SessionManager,
        hub: IHubContext<SessionHub>,
        logger: ILogger<SessionManager>
    ) =
    inherit ControllerBase()

    let toSessionDto (s: AgentSession) =
        {| id = s.Id
           number = s.Number
           name = s.Name
           workingDirectory = s.WorkingDirectory
           status = s.Status.ToString().ToLowerInvariant()
           mode = s.Mode.ToString().ToLowerInvariant()
           createdAt = s.CreatedAt
           lastUsedAt = s.LastUsedAt
           isRunning = AgentSession.isRunning s |}

    static member private StartBroadcastLoop
        (
            session: AgentSession,
            sessions: SessionManager,
            hub: IHubContext<SessionHub>,
            logger: ILogger<SessionManager>
        ) =
        match session.Mode with
        | Terminal ->
            Task.Run(fun () -> SessionController.BroadcastTerminalOutput(session.Id, sessions, hub, logger))
            |> ignore
        | Stream ->
            Task.Run(fun () -> SessionController.BroadcastStreamOutput(session.Id, sessions))
            |> ignore

    static member private BroadcastTerminalOutput
        (
            sessionId: Guid,
            sessions: SessionManager,
            hub: IHubContext<SessionHub>,
            logger: ILogger<SessionManager>
        ) =
        task {
            match sessions.GetTerminalChannel(sessionId) with
            | None -> ()
            | Some channel ->
                use idleTimer = new Timer(2000.0, AutoReset = false)

                let send (proxy: IClientProxy) meth (args: obj array) =
                    proxy.SendCoreAsync(meth, args)

                idleTimer.Elapsed.Add(fun _ ->
                    if sessions.SetActivityState(sessionId, ActivityState.Idle) then
                        send hub.Clients.All "SessionActivity" [| sessionId; "idle" |]
                        |> ignore)

                try
                    let mutable keepReading = true

                    while keepReading do
                        let! hasMore = channel.WaitToReadAsync()

                        if not hasMore then
                            keepReading <- false
                        else
                            let mutable data = ""

                            while channel.TryRead(&data) do
                                sessions.AppendTerminalOutput(sessionId, data)

                                do!
                                    send
                                        (hub.Clients.Group(sessionId.ToString()))
                                        "TerminalOutput"
                                        [| sessionId; data |]

                                if sessions.SetActivityState(sessionId, ActivityState.Busy) then
                                    do! send hub.Clients.All "SessionActivity" [| sessionId; "busy" |]

                                idleTimer.Stop()
                                idleTimer.Start()
                with
                | ex -> logger.LogWarning(ex, "Error broadcasting terminal {Id}", sessionId)

                idleTimer.Stop()
                sessions.SetActivityState(sessionId, ActivityState.Idle) |> ignore
                do! send hub.Clients.All "SessionActivity" [| sessionId; "idle" |]
                do! send (hub.Clients.Group(sessionId.ToString())) "SessionEnded" [| sessionId |]
        }

    static member private BroadcastStreamOutput(sessionId: Guid, sessions: SessionManager) =
        sessions.BroadcastStreamOutput(sessionId)

    // -- Endpoints --

    [<HttpGet>]
    member _.List() =
        let all = sessions.GetAll() |> Seq.map toSessionDto
        OkObjectResult(all) :> IActionResult

    [<HttpGet("{id:guid}")>]
    member _.Get(id: Guid) =
        match sessions.Get(id) with
        | None -> NotFoundResult() :> IActionResult
        | Some s -> OkObjectResult(toSessionDto s) :> IActionResult

    [<HttpGet("{id:guid}/content")>]
    member _.GetContent(id: Guid) =
        match sessions.Get(id) with
        | None -> NotFoundResult() :> IActionResult
        | Some s ->
            let modeStr = s.Mode.ToString().ToLowerInvariant()

            match s.Mode with
            | Terminal ->
                let history = sessions.GetTerminalHistory(id)
                OkObjectResult({| mode = modeStr; terminal = history |}) :> IActionResult
            | Stream ->
                let messages = sessions.GetStreamHistory(id)
                OkObjectResult({| mode = modeStr; messages = messages |}) :> IActionResult

    [<HttpPost>]
    member _.Create([<FromBody>] request: CreateRequest) =
        task {
            let mode =
                if String.Equals(request.Mode, "stream", StringComparison.OrdinalIgnoreCase) then
                    Stream
                else
                    Terminal

            let model =
                if String.IsNullOrEmpty request.Model then
                    None
                else
                    Some request.Model

            let! session =
                match model with
                | Some m -> sessions.CreateSession(request.Name, request.WorkingDirectory, mode, m)
                | None -> sessions.CreateSession(request.Name, request.WorkingDirectory, mode)

            if session.Status <> Error && session.Mode = Terminal then
                sessions.SetActivityState(session.Id, ActivityState.Busy) |> ignore
                SessionController.StartBroadcastLoop(session, sessions, hub, logger)

            do! hub.Clients.All.SendCoreAsync("SessionCreated", [| session |])
            return OkObjectResult(session) :> IActionResult
        }

    [<HttpPost("{id:guid}/resume")>]
    member _.Resume(id: Guid) =
        task {
            match sessions.Get(id) with
            | None -> return NotFoundResult() :> IActionResult
            | Some _ ->
                let! resumed = sessions.ResumeSessionAsync(id)

                if resumed.Mode = Terminal then
                    sessions.SetActivityState(id, ActivityState.Busy) |> ignore
                    SessionController.StartBroadcastLoop(resumed, sessions, hub, logger)

                do! hub.Clients.All.SendCoreAsync("SessionResumed", [| resumed |])
                return OkObjectResult(resumed) :> IActionResult
        }

    [<HttpPatch("{id:guid}/name")>]
    member _.Rename(id: Guid, [<FromBody>] request: RenameRequest) =
        task {
            match sessions.Get(id) with
            | None -> return NotFoundResult() :> IActionResult
            | Some _ ->
                do! sessions.RenameSession(id, request.Name)
                do! hub.Clients.All.SendCoreAsync("SessionRenamed", [| id; request.Name |])
                return OkObjectResult({| id = id; name = request.Name |}) :> IActionResult
        }

    [<HttpPost("{id:guid}/stop")>]
    member _.Stop(id: Guid) =
        task {
            match sessions.Get(id) with
            | None -> return NotFoundResult() :> IActionResult
            | Some _ ->
                sessions.StopSession(id)
                do! hub.Clients.All.SendCoreAsync("SessionStopped", [| id |])
                return OkObjectResult({| id = id; status = "stopped" |}) :> IActionResult
        }

    [<HttpDelete("{id:guid}")>]
    member _.Delete(id: Guid) =
        task {
            if not (sessions.RemoveSession(id)) then
                return NotFoundResult() :> IActionResult
            else
                do! hub.Clients.All.SendCoreAsync("SessionRemoved", [| id |])
                return OkObjectResult({| id = id; deleted = true |}) :> IActionResult
        }
