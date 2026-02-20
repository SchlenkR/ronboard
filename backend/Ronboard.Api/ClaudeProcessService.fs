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

    member _.StartTerminalProcess(workingDirectory: string, ?model: string) =
        let resolvedDir = ClaudeProcessService.ExpandPath(workingDirectory)

        if not (Directory.Exists resolvedDir) then
            raise (DirectoryNotFoundException $"Working directory not found: {resolvedDir}")

        let shell =
            Environment.GetEnvironmentVariable("SHELL")
            |> Option.ofObj
            |> Option.defaultValue "/bin/zsh"

        let claudeCmd =
            match model with
            | Some m -> $"claude --model {m}"
            | None -> "claude"

        let psi =
            ProcessStartInfo(
                FileName = "/usr/bin/script",
                Arguments = $"""-q /dev/null {shell} -l -c "{claudeCmd}" """,
                WorkingDirectory = resolvedDir,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            )

        psi.Environment.["TERM"] <- "xterm-256color"
        psi.Environment.["COLUMNS"] <- "120"
        psi.Environment.["LINES"] <- "40"

        let proc = new Process(StartInfo = psi)

        let channel =
            Channel.CreateUnbounded<string>(
                UnboundedChannelOptions(SingleReader = false, SingleWriter = true)
            )

        proc.Start() |> ignore
        logger.LogInformation("Started terminal Claude process (PID {Pid}) in {Dir}", proc.Id, resolvedDir)

        task {
            try
                let buffer = Array.zeroCreate<char> 4096
                let mutable keepReading = true

                while keepReading do
                    let! count = proc.StandardOutput.ReadAsync(buffer, 0, buffer.Length)

                    if count = 0 then
                        keepReading <- false
                    else
                        do! channel.Writer.WriteAsync(new string (buffer, 0, count))
            with
            | ex -> logger.LogError(ex, "Error reading terminal stdout")

            channel.Writer.Complete()
        }
        |> ignore

        startStderrReader proc
        struct (proc, channel.Reader)

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

    member _.SendInputAsync(proc: Process, data: string) =
        task {
            do! proc.StandardInput.WriteAsync(data)
            do! proc.StandardInput.FlushAsync()
        }

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
