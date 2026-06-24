namespace GithubBot.Services;

/// <summary>
/// Scaffold for AI-powered PR description summarization.
/// Not wired up yet — plug into EmbedBuilders.PrEmbed when ready.
/// </summary>
public class PrSummaryService
{
    // TODO: inject IConfiguration or options to read API key / model / endpoint
    // TODO: add Anthropic.SDK (or whatever client) as a PackageReference

    /// <summary>
    /// Summarizes a PR description to a concise few sentences.
    /// Returns null if summarization is unavailable or the input is too short to bother.
    /// </summary>
    public Task<string?> SummarizeAsync(string? body, CancellationToken ct = default)
    {
        // TODO: implement — rough shape:
        //   if (string.IsNullOrWhiteSpace(body) || body.Length < 300) return Task.FromResult<string?>(null);
        //   var response = await _client.Messages.CreateAsync(new() {
        //       Model = "claude-haiku-4-5-20251001",
        //       MaxTokens = 150,
        //       Messages = [new() { Role = "user", Content = $"Summarize this PR description in 2-3 sentences:\n\n{body}" }]
        //   }, ct);
        //   return response.Content.FirstOrDefault()?.Text;
        return Task.FromResult<string?>(null);
    }
}
