namespace Ronboard.Api.Services

open System
open System.Collections.Concurrent
open System.Diagnostics
open Microsoft.AspNetCore.SignalR
open Microsoft.Extensions.Logging
open Ronboard.Api.Hubs
open Ronboard.Api.Prompts

type AutoNamingService
    (
        logger: ILogger<AutoNamingService>,
        persistence: PersistenceService,
        hubContext: IHubContext<SessionHub>
    ) =

    let [<Literal>] MinTextLengthForNaming = 200
    let pendingNaming = ConcurrentDictionary<Guid, bool>()

    let generateTitleAsync (text: string) =
        task {
            let shell =
                Environment.GetEnvironmentVariable("SHELL")
                |> Option.ofObj
                |> Option.defaultValue "/bin/zsh"

            let psi =
                ProcessStartInfo(
                    FileName = shell,
                    Arguments = "-l -c \"claude --print\"",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                )

            use proc = new Process(StartInfo = psi)
            proc.Start() |> ignore

            do! proc.StandardInput.WriteAsync(NamingPrompt.build text)
            proc.StandardInput.Close()

            let! output = proc.StandardOutput.ReadToEndAsync()
            do! proc.WaitForExitAsync()

            return output.Trim()
        }

    member _.Initialize(sessionManager: SessionManager) =
        sessionManager.OnOutputReceived <-
            Some(fun sessionId ->
                let session = sessionManager.Get(sessionId)

                match session with
                | None -> ()
                | Some s when s.Name <> "Untitled" -> ()
                | Some s ->
                    let text = sessionManager.GetAccumulatedText(sessionId)

                    if text.Length >= MinTextLengthForNaming then
                        if pendingNaming.TryAdd(sessionId, true) then
                            task {
                                try
                                    let! title = generateTitleAsync text

                                    if not (String.IsNullOrWhiteSpace title) then
                                        let mutable title = title.Trim().Trim('"').Trim()

                                        if title.Length > 60 then
                                            title <- title.[.. 59]

                                        s.Name <- title
                                        do! persistence.SaveMetadataAsync(s)
                                        do! hubContext.Clients.All.SendCoreAsync("SessionRenamed", [| sessionId; title |])

                                        logger.LogInformation(
                                            "Auto-named session {Id} to '{Title}'",
                                            sessionId,
                                            title
                                        )
                                with
                                | ex ->
                                    logger.LogWarning(ex, "Auto-naming failed for session {Id}", sessionId)

                                pendingNaming.TryRemove(sessionId) |> ignore
                            }
                            |> ignore)
