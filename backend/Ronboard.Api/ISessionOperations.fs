namespace Ronboard.Api.Services

open System
open System.Threading.Tasks
open Ronboard.Api.Models

type ISessionOperations =
    abstract SendStreamMessageAsync: sessionId: Guid * message: string -> Task
    abstract Get: id: Guid -> AgentSession option
    abstract GetStreamHistory: id: Guid -> ClaudeMessage list
    abstract GetActivityState: id: Guid -> ActivityState
