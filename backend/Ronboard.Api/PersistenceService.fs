namespace Ronboard.Api.Services

open System
open System.Collections.Concurrent
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open Microsoft.Extensions.Logging
open Ronboard.Api.Models

type PersistenceService(logger: ILogger<PersistenceService>) =

    let baseDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ronboard")

    let sessionsDir = Path.Combine(baseDir, "sessions")
    let counterFile = Path.Combine(baseDir, "counter.json")

    let counterLock = new SemaphoreSlim(1, 1)
    let metadataLocks = ConcurrentDictionary<Guid, SemaphoreSlim>()

    let jsonOptions =
        JsonSerializerOptions(
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        )

    let ndjsonOptions =
        JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

    let getSessionDir (sessionId: Guid) =
        Path.Combine(sessionsDir, sessionId.ToString())

    member _.EnsureDirectories() = Directory.CreateDirectory(sessionsDir) |> ignore

    member _.GetNextNumberAsync() =
        task {
            do! counterLock.WaitAsync()

            try
                let mutable current = 1

                if File.Exists counterFile then
                    let! json = File.ReadAllTextAsync(counterFile)

                    if not (String.IsNullOrWhiteSpace json) then
                        use doc = JsonDocument.Parse(json)
                        let mutable v = Unchecked.defaultof<JsonElement>

                        if doc.RootElement.TryGetProperty("nextNumber", &v) then
                            current <- v.GetInt32()

                let nextJson = JsonSerializer.Serialize({| nextNumber = current + 1 |}, jsonOptions)
                do! File.WriteAllTextAsync(counterFile, nextJson)
                return current
            finally
                counterLock.Release() |> ignore
        }

    member _.SaveMetadataAsync(session: AgentSession) =
        task {
            let semaphore = metadataLocks.GetOrAdd(session.Id, fun _ -> new SemaphoreSlim(1, 1))
            do! semaphore.WaitAsync()

            try
                let dir = getSessionDir session.Id
                Directory.CreateDirectory(dir) |> ignore
                let path = Path.Combine(dir, "metadata.json")
                let tmpPath = path + ".tmp"
                let json = JsonSerializer.Serialize(session, jsonOptions)
                do! File.WriteAllTextAsync(tmpPath, json)
                File.Move(tmpPath, path, true)
            finally
                semaphore.Release() |> ignore
        }

    member _.LoadAllSessionsAsync() =
        task {
            let sessions = ResizeArray<AgentSession>()

            if Directory.Exists sessionsDir then
                for dir in Directory.GetDirectories(sessionsDir) do
                    let metadataPath = Path.Combine(dir, "metadata.json")

                    if File.Exists metadataPath then
                        try
                            let! json = File.ReadAllTextAsync(metadataPath)
                            let session = JsonSerializer.Deserialize<AgentSession>(json, jsonOptions)

                            if not (isNull (box session)) then
                                session.Status <- Stopped
                                session.Process <- None
                                sessions.Add(session)
                        with
                        | ex -> logger.LogWarning(ex, "Failed to load session from {Dir}", dir)

            return sessions |> Seq.toList
        }

    member _.DeleteSessionAsync(sessionId: Guid) =
        task {
            let dir = getSessionDir sessionId

            if Directory.Exists dir then
                Directory.Delete(dir, true)
        }

    member _.AppendStreamMessageAsync(sessionId: Guid, message: ClaudeMessage) =
        task {
            let path = Path.Combine(getSessionDir sessionId, "stream.ndjson")
            let json = JsonSerializer.Serialize(message, ndjsonOptions)
            do! File.AppendAllTextAsync(path, json + "\n")
        }

    member _.AppendStreamInputAsync(sessionId: Guid, userMessage: string) =
        task {
            let path = Path.Combine(getSessionDir sessionId, "stdin.log")

            let entry =
                JsonSerializer.Serialize(
                    {| timestamp = DateTime.UtcNow
                       message = userMessage |},
                    ndjsonOptions
                )

            do! File.AppendAllTextAsync(path, entry + "\n")
        }

    member _.LoadStreamHistoryAsync(sessionId: Guid) =
        task {
            let path = Path.Combine(getSessionDir sessionId, "stream.ndjson")
            let messages = ResizeArray<ClaudeMessage>()

            if File.Exists path then
                let! lines = File.ReadAllLinesAsync(path)

                for line in lines do
                    if not (String.IsNullOrWhiteSpace line) then
                        try
                            let msg = JsonSerializer.Deserialize<ClaudeMessage>(line, ndjsonOptions)

                            if not (isNull (box msg)) then
                                messages.Add(msg)
                        with
                        | ex -> logger.LogDebug(ex, "Failed to parse NDJSON line")

            return messages |> Seq.toList
        }

