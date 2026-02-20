namespace Ronboard.Api.Controllers

open System
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
           createdAt = s.CreatedAt
           lastUsedAt = s.LastUsedAt
           isRunning = AgentSession.isRunning s |}

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
        | Some _ ->
            let messages = sessions.GetStreamHistory(id)
            OkObjectResult({| messages = messages |}) :> IActionResult

    [<HttpPost>]
    member _.Create([<FromBody>] request: CreateRequest) =
        task {
            let model =
                if String.IsNullOrEmpty request.Model then
                    None
                else
                    Some request.Model

            let! session =
                match model with
                | Some m -> sessions.CreateSession(request.Name, request.WorkingDirectory, m)
                | None -> sessions.CreateSession(request.Name, request.WorkingDirectory)

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
