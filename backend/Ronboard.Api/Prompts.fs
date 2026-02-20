namespace Ronboard.Api.Prompts

open Stubble.Core.Builders

module NamingPrompt =
    let [<Literal>] private MaxSnippetLength = 500

    let private template =
        "Given this conversation snippet, suggest a very short title\n\
         (2-4 words, no quotes, no punctuation, no explanation - ONLY the title):\n\
         \n\
         {{Snippet}}"

    let build (conversationText: string) =
        let snippet =
            if conversationText.Length > MaxSnippetLength then
                conversationText.[.. MaxSnippetLength - 1]
            else
                conversationText

        StubbleBuilder().Build().Render(template, {| Snippet = snippet |})
