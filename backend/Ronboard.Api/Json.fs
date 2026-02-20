namespace Ronboard.Api

open System
open System.Text.Json
open System.Text.Json.Serialization
open Microsoft.FSharp.Reflection

/// Serializes a simple F# discriminated union (cases with no fields)
/// as a camelCase JSON string, e.g. ToolUse -> "toolUse"
type DuStringConverter<'T>() =
    inherit JsonConverter<'T>()

    let cases = FSharpType.GetUnionCases(typeof<'T>)

    let nameToCase =
        cases
        |> Array.map (fun c ->
            let camel = JsonNamingPolicy.CamelCase.ConvertName(c.Name)
            camel, c)
        |> dict

    let tagToName =
        cases
        |> Array.map (fun c ->
            c.Tag, JsonNamingPolicy.CamelCase.ConvertName(c.Name))
        |> dict

    override _.Read(reader, _typeToConvert, _options) =
        let s = reader.GetString()

        match nameToCase.TryGetValue(s) with
        | true, c -> FSharpValue.MakeUnion(c, [||]) :?> 'T
        | _ -> failwithf "Unknown value '%s' for type %s" s typeof<'T>.Name

    override _.Write(writer, value, _options) =
        let case, _ = FSharpValue.GetUnionFields(value, typeof<'T>)
        writer.WriteStringValue(tagToName.[case.Tag])
