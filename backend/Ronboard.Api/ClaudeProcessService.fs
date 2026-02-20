namespace Ronboard.Api.Services

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading.Channels
open Microsoft.Extensions.Logging
open Ronboard.Api.Models

type ClaudeProcessService(logger: ILogger<ClaudeProcessService>) =

    let startStderrReader (proc: Process) =
        task {
            try
                let mutable keepReading = true

                while keepReading do
                    let! line = proc.StandardError.ReadLineAsync()

                    if isNull line then
                        keepReading <- false
                    elif not (String.IsNullOrWhiteSpace line) then
                        logger.LogWarning("Claude stderr: {Line}", line)
            with
            | _ -> ()
        }
        |> ignore

    static member ExpandPath(path: string) =
        let p =
            if path.StartsWith('~') then
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    path.[1..].TrimStart('/')
                )
            else
                path

        Path.GetFullPath(p)

    member _.StartStreamProcess
        (workingDirectory: string, sessionId: Guid, isResume: bool, ?model: string)
        =
        let resolvedDir = ClaudeProcessService.ExpandPath(workingDirectory)

        if not (Directory.Exists resolvedDir) then
            raise (DirectoryNotFoundException $"Working directory not found: {resolvedDir}")

        let mutable args =
            "--print --input-format stream-json --output-format stream-json --include-partial-messages --verbose"

        args <-
            if isResume then
                args + $" --resume {sessionId}"
            else
                args + $" --session-id {sessionId}"

        match model with
        | Some m -> args <- args + $" --model {m}"
        | None -> ()

        let shell =
            Environment.GetEnvironmentVariable("SHELL")
            |> Option.ofObj
            |> Option.defaultValue "/bin/zsh"

        let psi =
            ProcessStartInfo(
                FileName = shell,
                Arguments = $"""-l -c "claude {args}" """,
                WorkingDirectory = resolvedDir,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            )

        let proc = new Process(StartInfo = psi)

        let channel =
            Channel.CreateUnbounded<ClaudeMessage>(
                UnboundedChannelOptions(SingleReader = false, SingleWriter = true)
            )

        proc.Start() |> ignore
        logger.LogInformation("Started stream Claude process (PID {Pid}) in {Dir}", proc.Id, resolvedDir)

        task {
            let mutable index = 0

            try
                let mutable keepReading = true

                while keepReading do
                    let! line = proc.StandardOutput.ReadLineAsync()

                    if isNull line then
                        keepReading <- false
                    elif not (String.IsNullOrWhiteSpace line) then
                        try
                            let json = JsonDocument.Parse(line)

                            let typ =
                                let mutable t = Unchecked.defaultof<JsonElement>

                                if json.RootElement.TryGetProperty("type", &t) then
                                    t.GetString() |> Option.ofObj |> Option.defaultValue "unknown"
                                else
                                    "unknown"

                            do!
                                channel.Writer.WriteAsync(
                                    { Index = index
                                      Timestamp = DateTime.UtcNow
                                      Type = typ
                                      RawJson = json.RootElement.Clone() }
                                )

                            index <- index + 1
                        with
                        | :? JsonException -> logger.LogDebug("Non-JSON output: {Line}", line)
            with
            | ex -> logger.LogError(ex, "Error reading stream stdout")

            channel.Writer.Complete()
        }
        |> ignore

        startStderrReader proc
        struct (proc, channel.Reader)

    member _.SendStreamMessageAsync(proc: Process, userMessage: string) =
        task {
            let input =
                {| ``type`` = "user"
                   message = {| role = "user"; content = userMessage |} |}

            let json = JsonSerializer.Serialize(input)
            logger.LogInformation("Sending to stream stdin (PID {Pid}): {Json}", proc.Id, json)
            do! proc.StandardInput.WriteLineAsync(json)
            do! proc.StandardInput.FlushAsync()
        }
