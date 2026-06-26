using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace GithubBot.Services;

public class PrSummaryService
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly ILogger<PrSummaryService> _logger;

    private const int MinBodyLength = 300;
    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    public PrSummaryService(string apiKey, string model, ILogger<PrSummaryService> logger)
    {
        _model = model;
        _logger = logger;
        _http = new HttpClient { BaseAddress = new Uri("https://openrouter.ai/api/v1/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _http.DefaultRequestHeaders.Add("X-Title", "GithubBot");
    }

    public enum SummarizeResult { Ok, TooShort, ApiError }

    /// <summary>
    /// Summarizes a PR description. Returns (Ok, text), (TooShort, null), or (ApiError, null).
    /// </summary>
    public async Task<(SummarizeResult Status, string? Text)> SummarizeAsync(string? body, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(body) || body.Length < MinBodyLength)
            return (SummarizeResult.TooShort, null);

        try
        {
            var payload = new
            {
                model = _model,
                max_tokens = 120,
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = $"Summarize this pull request description in 2-3 concise sentences. Return only the summary, no preamble:\n\n{body}"
                    }
                }
            };

            var json = JsonSerializer.Serialize(payload);
            var response = await _http.PostAsync("chat/completions",
                new StringContent(json, Encoding.UTF8, "application/json"), ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[PrSummary] OpenRouter request failed: {Status}", response.StatusCode);
                return (SummarizeResult.ApiError, null);
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var text = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            return string.IsNullOrWhiteSpace(text)
                ? (SummarizeResult.ApiError, null)
                : (SummarizeResult.Ok, text.Trim());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PrSummary] Summarization failed");
            return (SummarizeResult.ApiError, null);
        }
    }
}
