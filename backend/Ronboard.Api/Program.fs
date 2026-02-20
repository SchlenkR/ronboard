open System.Text.Json
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Ronboard.Api.Hubs
open Ronboard.Api.Services

[<EntryPoint>]
let main args =
    let builder = WebApplication.CreateBuilder(args)

    builder.Services
        .AddControllers()
        .AddJsonOptions(fun options ->
            options.JsonSerializerOptions.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase)
    |> ignore

    builder.Services
        .AddSignalR()
        .AddJsonProtocol(fun options ->
            options.PayloadSerializerOptions.PropertyNamingPolicy <- JsonNamingPolicy.CamelCase)
    |> ignore

    builder.Services.AddSingleton<ClaudeProcessService>() |> ignore
    builder.Services.AddSingleton<PersistenceService>() |> ignore
    builder.Services.AddSingleton<SessionManager>() |> ignore
    builder.Services.AddSingleton<ISessionOperations>(fun (sp: System.IServiceProvider) -> sp.GetRequiredService<SessionManager>() :> ISessionOperations) |> ignore
    builder.Services.AddSingleton<AutoNamingService>() |> ignore

    builder.Services.AddCors(fun options ->
        options.AddPolicy(
            "Frontend",
            fun policy ->
                policy
                    .WithOrigins("http://localhost:4200")
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .AllowCredentials()
                |> ignore
        ))
    |> ignore

    let app = builder.Build()

    let sessionManager = app.Services.GetRequiredService<SessionManager>()
    sessionManager.LoadPersistedSessionsAsync().GetAwaiter().GetResult()

    let autoNaming = app.Services.GetRequiredService<AutoNamingService>()
    autoNaming.Initialize(sessionManager)

    app.UseWebSockets() |> ignore
    app.UseCors("Frontend") |> ignore

    app.MapControllers().RequireCors("Frontend") |> ignore
    app.MapHub<SessionHub>("/hubs/session").RequireCors("Frontend") |> ignore

    app.Lifetime.ApplicationStopping.Register(fun () ->
        for session in sessionManager.GetAll() do
            sessionManager.StopSession(session.Id))
    |> ignore

    app.Run()
    0
