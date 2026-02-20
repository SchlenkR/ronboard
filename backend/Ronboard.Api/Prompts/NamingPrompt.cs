namespace Ronboard.Api.Prompts;

using Stubble.Core.Builders;

public static class NamingPrompt
{
    private const int MaxSnippetLength = 500;

    private static readonly string Template = """
        Given this conversation snippet, suggest a very short title
        (2-4 words, no quotes, no punctuation, no explanation - ONLY the title):

        {{Snippet}}
        """;

    public static string Build(string conversationText)
    {
        var snippet = conversationText.Length > MaxSnippetLength
            ? conversationText[..MaxSnippetLength]
            : conversationText;

        return new StubbleBuilder().Build()
            .Render(Template, new { Snippet = snippet });
    }
}
