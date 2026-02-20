namespace Ronboard.Api.Services

open System
open System.Collections.Concurrent
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Channels
open Microsoft.AspNetCore.SignalR
open Microsoft.Extensions.Logging
open Ronboard.Api.Hubs
open Ronboard.Api.Models
open Ronboard.Api.Prompts

[<AutoOpen>]
module private HubHelpers =
    let sendAsync (proxy: IClientProxy) (method: string) (args: obj array) =
        proxy.SendCoreAsync(method, args)

type SessionManager
    (
        processService: ClaudeProcessService,
        persistence: PersistenceService,
        hub: IHubContext<SessionHub>,
        logger: ILogger<SessionManager>
    ) =

    let sessions = ConcurrentDictionary<Guid, AgentSession>()

    // Terminal mode
    let terminalChannels = ConcurrentDictionary<Guid, ChannelReader<string>>()
    let terminalBuffers = ConcurrentDictionary<Guid, ResizeArray<string>>()

    // Stream mode
    let streamChannels = ConcurrentDictionary<Guid, ChannelReader<ClaudeMessage>>()
    let streamMessages = ConcurrentDictionary<Guid, ResizeArray<ClaudeMessage>>()

    // Activity tracking (transient, not persisted)
    let activityStates = ConcurrentDictionary<Guid, ActivityState>()

    // Per-session lock for process lifecycle
    let processLocks = ConcurrentDictionary<Guid, SemaphoreSlim>()

    let mutable onOutputReceived: (Guid -> unit) option = None

    let updateLastUsedAt (sessionId: Guid) =
        match sessions.TryGetValue(sessionId) with
        | true, session -> session.LastUsedAt <- DateTime.UtcNow
        | _ -> ()

    let notifyOutputReceived (sessionId: Guid) =
        match onOutputReceived with
        | Some f -> f sessionId
        | None -> ()

    let setupProcessExitHandler (session: AgentSession) =
        match session.Process with
        | Some proc ->
            proc.EnableRaisingEvents <- true

            proc.Exited.Add(fun _ ->
                let exitCode = proc.ExitCode

                logger.LogInformation(
                    "Session {Id} (mode={Mode}) exited with code {Code}",
                    session.Id,
                    session.Mode,
                    exitCode
                )

                if exitCode <> 0 then
                    session.Status <- Error
                    persistence.SaveMetadataAsync(session) |> ignore
                elif session.Mode = Terminal then
                    session.Status <- Stopped
                    persistence.SaveMetadataAsync(session) |> ignore)
        | None -> ()

    let startProcess (session: AgentSession) (mode: SessionMode) (workingDirectory: string) (model: string option) =
        match mode with
        | Terminal ->
            let struct (proc, output) =
                match model with
                | Some m -> processService.StartTerminalProcess(workingDirectory, m)
                | None -> processService.StartTerminalProcess(workingDirectory)

            session.Process <- Some proc
            terminalChannels.[session.Id] <- output
            terminalBuffers.[session.Id] <- ResizeArray<string>()
        | Stream ->
            let struct (proc, output) =
                match model with
                | Some m -> processService.StartStreamProcess(workingDirectory, session.Id, false, m)
                | None -> processService.StartStreamProcess(workingDirectory, session.Id, false)

            session.Process <- Some proc
            streamChannels.[session.Id] <- output
            streamMessages.[session.Id] <- ResizeArray<ClaudeMessage>()

    let createUserMessage (sessionId: Guid) (text: string) =
        let nextIndex =
            match streamMessages.TryGetValue(sessionId) with
            | true, msgs -> msgs.Count
            | _ -> 0

        let rawJson = JsonSerializer.SerializeToElement({| text = text |})

        { Index = nextIndex
          Timestamp = DateTime.UtcNow
          Type = "user_message"
          RawJson = rawJson }

    let appendTerminalOutputInternal (id: Guid) (data: string) =
        match terminalBuffers.TryGetValue(id) with
        | true, buffer -> lock buffer (fun () -> buffer.Add(data))
        | _ -> ()

        persistence.AppendTerminalOutputAsync(id, data) |> ignore
        updateLastUsedAt id
        notifyOutputReceived id

    let appendStreamMessageInternal (id: Guid) (msg: ClaudeMessage) =
        match streamMessages.TryGetValue(id) with
        | true, msgs -> lock msgs (fun () -> msgs.Add(msg))
        | _ -> ()

        persistence.AppendStreamMessageAsync(id, msg) |> ignore
        updateLastUsedAt id
        notifyOutputReceived id

    let getTerminalHistoryInternal (id: Guid) =
        match terminalBuffers.TryGetValue(id) with
        | true, buffer -> lock buffer (fun () -> String.Concat(buffer))
        | _ -> ""

    let getStreamHistoryInternal (id: Guid) =
        match streamMessages.TryGetValue(id) with
        | true, msgs -> lock msgs (fun () -> msgs |> Seq.toList)
        | _ -> []

    let getActivityStateInternal (id: Guid) =
        match activityStates.TryGetValue(id) with
        | true, s -> s
        | _ -> ActivityState.Idle

    let setActivityStateInternal (id: Guid) (state: ActivityState) =
        let previous = getActivityStateInternal id
        activityStates.[id] <- state
        previous <> state

    let broadcastStreamOutputInternal (sessionId: Guid) =
        task {
            let channel =
                match streamChannels.TryGetValue(sessionId) with
                | true, ch -> Some ch
                | _ -> None

            match channel with
            | None -> ()
            | Some ch ->
                try
                    let mutable keepReading = true

                    while keepReading do
                        let! hasMore = ch.WaitToReadAsync()

                        if not hasMore then
                            keepReading <- false
                        else
                            let mutable msg = Unchecked.defaultof<ClaudeMessage>

                            while ch.TryRead(&msg) do
                                appendStreamMessageInternal sessionId msg

                                do!
                                    sendAsync
                                        (hub.Clients.Group(sessionId.ToString()))
                                        "StreamMessage"
                                        [| sessionId; msg |]

                                let newState = SessionManager.DetectStreamActivity(msg)

                                match newState with
                                | Some state ->
                                    if setActivityStateInternal sessionId state then
                                        do!
                                            sendAsync
                                                hub.Clients.All
                                                "SessionActivity"
                                                [| sessionId; SessionManager.ActivityStateName(state) |]
                                | None -> ()
                with
                | ex -> logger.LogWarning(ex, "Error broadcasting stream {Id}", sessionId)

                activityStates.[sessionId] <- ActivityState.Idle
                do! sendAsync hub.Clients.All "SessionActivity" [| sessionId; "idle" |]
        }

    let sendStreamMessageCore (session: AgentSession) (sessionId: Guid) (message: string) =
        task {
            let needsStart =
                match session.Process with
                | None -> true
                | Some p -> p.HasExited

            if needsStart then
                let isResume = session.ClaudeSessionCreated

                logger.LogInformation(
                    "SendStreamMessage: starting process for session {Id} ({Flag})",
                    sessionId,
                    (if isResume then "--resume" else "--session-id")
                )

                let struct (proc, output) =
                    processService.StartStreamProcess(session.WorkingDirectory, sessionId, isResume)

                session.Process <- Some proc
                streamChannels.[sessionId] <- output
                setupProcessExitHandler session

                if session.Status <> Running then
                    session.Status <- Running
                    do! sendAsync hub.Clients.All "SessionStatusChanged" [| sessionId; "running" |]

                activityStates.[sessionId] <- ActivityState.Busy
                do! sendAsync hub.Clients.All "SessionActivity" [| sessionId; "busy" |]

                Threading.Tasks.Task.Run(fun () ->
                    broadcastStreamOutputInternal sessionId :> Threading.Tasks.Task)
                |> ignore

                if not session.ClaudeSessionCreated then
                    session.ClaudeSessionCreated <- true
                    persistence.SaveMetadataAsync(session) |> ignore

            // Store user message
            let userMsg = createUserMessage sessionId message
            appendStreamMessageInternal sessionId userMsg

            let pid =
                match session.Process with
                | Some p -> p.Id
                | None -> -1

            logger.LogInformation(
                "SendStreamMessage: sending to PID {Pid} for session {Id}: {Message}",
                pid,
                sessionId,
                message
            )

            try
                match session.Process with
                | Some proc -> do! processService.SendStreamMessageAsync(proc, message)
                | None -> ()
            with
            | ex ->
                logger.LogError(
                    ex,
                    "SendStreamMessage: failed to write to process stdin for session {Id}",
                    sessionId
                )

                return ()

            persistence.AppendStreamInputAsync(sessionId, message) |> ignore
            updateLastUsedAt sessionId
        }

    // -- Public API --

    member _.OnOutputReceived
        with get () = onOutputReceived
        and set v = onOutputReceived <- v

    member _.GetAll() : AgentSession seq = sessions.Values

    member _.Get(id: Guid) =
        match sessions.TryGetValue(id) with
        | true, s -> Some s
        | _ -> None

    member _.GetActivityState(id: Guid) = getActivityStateInternal id

    member _.SetActivityState(id: Guid, state: ActivityState) = setActivityStateInternal id state

    member _.GetTerminalChannel(id: Guid) =
        match terminalChannels.TryGetValue(id) with
        | true, ch -> Some ch
        | _ -> None

    member _.GetTerminalHistory(id: Guid) = getTerminalHistoryInternal id
    member _.AppendTerminalOutput(id: Guid, data: string) = appendTerminalOutputInternal id data
    member _.GetStreamHistory(id: Guid) = getStreamHistoryInternal id
    member _.AppendStreamMessage(id: Guid, msg: ClaudeMessage) = appendStreamMessageInternal id msg

    member _.CreateSession(name: string, workingDirectory: string, ?mode: SessionMode, ?model: string) =
        task {
            let mode = defaultArg mode Terminal
            let! number = persistence.GetNextNumberAsync()

            let session =
                { AgentSession.create () with
                    Number = number
                    Name = name
                    WorkingDirectory = workingDirectory
                    Mode = mode }

            match mode with
            | Stream ->
                session.Status <- Running
                sessions.[session.Id] <- session
                streamMessages.[session.Id] <- ResizeArray<ClaudeMessage>()
            | Terminal ->
                try
                    startProcess session mode workingDirectory model
                    session.Status <- Running
                    sessions.[session.Id] <- session
                    setupProcessExitHandler session
                with
                | ex ->
                    logger.LogError(ex, "Failed to start Claude process for session {Id}", session.Id)
                    session.Status <- Error
                    sessions.[session.Id] <- session

            do! persistence.SaveMetadataAsync(session)
            return session
        }

    member _.SendInputAsync(sessionId: Guid, data: string) =
        task {
            match sessions.TryGetValue(sessionId) with
            | true, session ->
                match session.Process with
                | Some proc ->
                    do! processService.SendInputAsync(proc, data)
                    persistence.AppendTerminalInputAsync(sessionId, data) |> ignore
                    updateLastUsedAt sessionId
                | None -> ()
            | _ -> ()
        }

    member _.SendStreamMessageAsync(sessionId: Guid, message: string) =
        task {
            match sessions.TryGetValue(sessionId) with
            | true, session ->
                let semaphore = processLocks.GetOrAdd(sessionId, fun _ -> new SemaphoreSlim(1, 1))
                do! semaphore.WaitAsync()

                try
                    do! sendStreamMessageCore session sessionId message
                finally
                    semaphore.Release() |> ignore
            | _ -> logger.LogWarning("SendStreamMessage: session {Id} not found", sessionId)
        }

    member _.StopSession(sessionId: Guid) =
        match sessions.TryGetValue(sessionId) with
        | true, session ->
            match session.Process with
            | Some proc when not proc.HasExited ->
                try
                    proc.Kill(true)
                with
                | ex -> logger.LogWarning(ex, "Error killing session {Id}", sessionId)
            | _ -> ()

            session.Status <- Stopped
            activityStates.TryRemove(sessionId) |> ignore
            persistence.SaveMetadataAsync(session) |> ignore
        | _ -> ()

    member _.RenameSession(sessionId: Guid, name: string) =
        task {
            match sessions.TryGetValue(sessionId) with
            | true, session ->
                session.Name <- name
                do! persistence.SaveMetadataAsync(session)
            | _ -> raise (InvalidOperationException("Session not found"))
        }

    member this.RemoveSession(sessionId: Guid) =
        this.StopSession(sessionId)
        terminalChannels.TryRemove(sessionId) |> ignore
        terminalBuffers.TryRemove(sessionId) |> ignore
        streamChannels.TryRemove(sessionId) |> ignore
        streamMessages.TryRemove(sessionId) |> ignore
        activityStates.TryRemove(sessionId) |> ignore
        persistence.DeleteSessionAsync(sessionId) |> ignore

        match sessions.TryRemove(sessionId) with
        | true, _ -> true
        | _ -> false

    member _.LoadPersistedSessionsAsync() =
        task {
            persistence.EnsureDirectories()
            let! loaded = persistence.LoadAllSessionsAsync()

            for session in loaded do
                sessions.[session.Id] <- session

                match session.Mode with
                | Terminal ->
                    let! history = persistence.LoadTerminalHistoryAsync(session.Id)

                    terminalBuffers.[session.Id] <-
                        if String.IsNullOrEmpty history then
                            ResizeArray<string>()
                        else
                            ResizeArray<string>([ history ])
                | Stream ->
                    let! msgs = persistence.LoadStreamHistoryAsync(session.Id)
                    streamMessages.[session.Id] <- ResizeArray<ClaudeMessage>(msgs)

            logger.LogInformation("Loaded {Count} persisted sessions", loaded.Length)
        }

    member _.ResumeSessionAsync(sessionId: Guid) =
        task {
            match sessions.TryGetValue(sessionId) with
            | false, _ -> return raise (InvalidOperationException("Session not found"))
            | true, session ->

            let alreadyRunning =
                match session.Process with
                | Some p -> not p.HasExited
                | None -> false

            if alreadyRunning then
                return session
            else

            match session.Mode with
            | Terminal ->
                let! inputs = persistence.LoadInputHistoryAsync(sessionId)

                let struct (proc, output) =
                    processService.StartTerminalProcess(session.WorkingDirectory)

                session.Process <- Some proc
                terminalChannels.[session.Id] <- output

                if inputs.Length > 0 then
                    let context = String.Join("\n", inputs)
                    let stripped = SessionManager.StripAnsiCodes(context)

                    if not (String.IsNullOrWhiteSpace stripped) then
                        let resumePrompt = ResumePrompt.build inputs
                        do! Threading.Tasks.Task.Delay(2000)
                        do! processService.SendInputAsync(proc, resumePrompt)

                setupProcessExitHandler session
            | Stream ->
                logger.LogInformation(
                    "ResumeSession (stream): session {Id} set to Running, process starts on next message",
                    sessionId
                )

            session.Status <- Running
            session.LastUsedAt <- DateTime.UtcNow
            do! persistence.SaveMetadataAsync(session)

            return session
        }

    member this.GetAccumulatedText(sessionId: Guid) =
        match this.Get(sessionId) with
        | None -> ""
        | Some session ->
            match session.Mode with
            | Terminal ->
                let raw = getTerminalHistoryInternal sessionId
                SessionManager.StripAnsiCodes(raw)
            | Stream ->
                let msgs = getStreamHistoryInternal sessionId
                let sb = StringBuilder()

                for msg in msgs do
                    if msg.Type = "stream_event" then
                        try
                            let mutable evt = Unchecked.defaultof<JsonElement>
                            let mutable t = Unchecked.defaultof<JsonElement>
                            let mutable delta = Unchecked.defaultof<JsonElement>
                            let mutable dt = Unchecked.defaultof<JsonElement>
                            let mutable text = Unchecked.defaultof<JsonElement>

                            if
                                msg.RawJson.TryGetProperty("event", &evt)
                                && evt.TryGetProperty("type", &t)
                                && t.GetString() = "content_block_delta"
                                && evt.TryGetProperty("delta", &delta)
                                && delta.TryGetProperty("type", &dt)
                                && dt.GetString() = "text_delta"
                                && delta.TryGetProperty("text", &text)
                            then
                                sb.Append(text.GetString()) |> ignore
                        with
                        | _ -> ()

                sb.ToString()

    member _.BroadcastStreamOutput(sessionId: Guid) = broadcastStreamOutputInternal sessionId

    static member DetectStreamActivity(msg: ClaudeMessage) =
        match msg.Type with
        | "result" -> Some ActivityState.Idle
        | "stream_event" ->
            try
                let mutable evt = Unchecked.defaultof<JsonElement>
                let mutable t = Unchecked.defaultof<JsonElement>

                if
                    msg.RawJson.TryGetProperty("event", &evt)
                    && evt.TryGetProperty("type", &t)
                then
                    let eventType = t.GetString()

                    match eventType with
                    | "content_block_start" ->
                        let mutable cb = Unchecked.defaultof<JsonElement>
                        let mutable cbType = Unchecked.defaultof<JsonElement>

                        if
                            evt.TryGetProperty("content_block", &cb)
                            && cb.TryGetProperty("type", &cbType)
                        then
                            if cbType.GetString() = "tool_use" then
                                Some ActivityState.ToolUse
                            else
                                Some ActivityState.Busy
                        else
                            None
                    | "message_start" -> Some ActivityState.Busy
                    | "message_stop" -> Some ActivityState.Idle
                    | _ -> None
                else
                    None
            with
            | _ -> None
        | _ -> None

    static member ActivityStateName(state: ActivityState) =
        match state with
        | ActivityState.Idle -> "idle"
        | ActivityState.Busy -> "busy"
        | ActivityState.ToolUse -> "tool_use"

    static member StripAnsiCodes(text: string) =
        Regex.Replace(text, @"\x1B\[[0-9;]*[a-zA-Z]|\x1B\].*?\x07|\x1B[()][A-B]", "")

    interface ISessionOperations with
        member this.SendInputAsync(sessionId, data) = this.SendInputAsync(sessionId, data)
        member this.SendStreamMessageAsync(sessionId, message) = this.SendStreamMessageAsync(sessionId, message)
        member this.Get(id) = this.Get(id)
        member this.GetTerminalHistory(id) = this.GetTerminalHistory(id)
        member this.GetStreamHistory(id) = this.GetStreamHistory(id)
        member this.GetActivityState(id) = this.GetActivityState(id)
