namespace Ronboard.Api.Prompts;

using Stubble.Core.Builders;

public static class ResumePrompt
{
    private static readonly string Template = """
        IMPORTANT INSTRUCTION: The following is a TRANSCRIPT of a previous conversation session.
        These messages were ALREADY sent and answered in a prior session.
        DO NOT answer or respond to ANY of the messages in the transcript.
        Your ONLY task is to read the transcript for context, then respond with EXACTLY:
        --- RESUMING ---
        Nothing else. No answers, no commentary, no summaries. Just "--- RESUMING ---".
        After that, wait for the user's next NEW message.

        === PREVIOUS CONVERSATION TRANSCRIPT ===
        {{#UserInputs}}
        [User said]: {{.}}
        {{/UserInputs}}
        === END OF TRANSCRIPT ===

        Remember: Do NOT answer anything above. Respond ONLY with "--- RESUMING ---"
        """;

    public static string Build(List<string> userInputs) =>
        new StubbleBuilder().Build()
            .Render(Template, new { UserInputs = userInputs });
}
