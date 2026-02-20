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

module ResumePrompt =
    let private template =
        "IMPORTANT INSTRUCTION: The following is a TRANSCRIPT of a previous conversation session.\n\
         These messages were ALREADY sent and answered in a prior session.\n\
         DO NOT answer or respond to ANY of the messages in the transcript.\n\
         Your ONLY task is to read the transcript for context, then respond with EXACTLY:\n\
         --- RESUMING ---\n\
         Nothing else. No answers, no commentary, no summaries. Just \"--- RESUMING ---\".\n\
         After that, wait for the user's next NEW message.\n\
         \n\
         === PREVIOUS CONVERSATION TRANSCRIPT ===\n\
         {{#UserInputs}}\n\
         [User said]: {{.}}\n\
         {{/UserInputs}}\n\
         === END OF TRANSCRIPT ===\n\
         \n\
         Remember: Do NOT answer anything above. Respond ONLY with \"--- RESUMING ---\""

    let build (userInputs: string list) =
        StubbleBuilder().Build().Render(template, {| UserInputs = userInputs |})
