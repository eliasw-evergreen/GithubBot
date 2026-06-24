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
    /// <param name="body">Raw PR description text.</param>
    /// <param name="maxLines">Truncate output to this many lines. null = no limit.</param>
    /// <param name="maxChars">Truncate output to this many characters. null = no limit.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<string?> SummarizeAsync(string? body, int? maxLines = null, int? maxChars = null, CancellationToken ct = default)
    {
        // TODO: implement — rough shape:
        //   if (string.IsNullOrWhiteSpace(body) || body.Length < 300) return Task.FromResult<string?>(null);
        //   var constraint = (maxLines, maxChars) switch {
        //       ({ } l, { } c) => $"in at most {l} lines and {c} characters",
        //       ({ } l, null) => $"in at most {l} lines",
        //       (null, { } c) => $"in at most {c} characters",
        //       _             => "in 2-3 sentences",
        //   };
        //   var response = await _client.Messages.CreateAsync(new() {
        //       Model = "claude-haiku-4-5-20251001",
        //       MaxTokens = maxChars.HasValue ? Math.Min(maxChars.Value / 3, 500) : 150,
        //       Messages = [new() { Role = "user", Content = $"Summarize this PR description {constraint}:\n\n{body}" }]
        //   }, ct);
        //   var summary = response.Content.FirstOrDefault()?.Text;
        //   // Hard-truncate as a safety net in case the model overshoots
        //   if (summary != null && maxLines.HasValue)
        //   {
        //       var lines = summary.Split('\n');
        //       if (lines.Length > maxLines.Value) summary = string.Join('\n', lines.Take(maxLines.Value));
        //   }
        //   if (summary != null && maxChars.HasValue && summary.Length > maxChars.Value)
        //       summary = summary[..maxChars.Value].TrimEnd() + "…";
        //   return summary;
        return Task.FromResult<string?>(null);
    }
}
