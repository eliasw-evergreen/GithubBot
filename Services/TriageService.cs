using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GithubBot.Services;

public record TriageResult(
    [property: JsonPropertyName("priority")] int Priority,
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("estimatedSize")] string EstimatedSize);

public class TriageService
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly ILogger<TriageService> _logger;

    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    private static readonly HashSet<string> _validSeverities = new(StringComparer.OrdinalIgnoreCase)
        { "1 - Critical", "2 - High", "3 - Medium", "4 - Low" };
    private static readonly HashSet<string> _validSizes = new(StringComparer.OrdinalIgnoreCase)
        { "Small", "Medium", "Large", "XL" };

    public enum TriageStatus { Ok, Error }

    public TriageService(string apiKey, string model, ILogger<TriageService> logger)
    {
        _model = model;
        _logger = logger;
        _http = new HttpClient { BaseAddress = new Uri("https://openrouter.ai/api/v1/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _http.DefaultRequestHeaders.Add("X-Title", "GithubBot");
    }

    public async Task<(TriageStatus Status, TriageResult? Result)> TriageAsync(
        string? title, string? workItemType,
        string? description, string? reproSteps,
        string? expected, string? actual,
        CancellationToken ct = default)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(workItemType)) parts.Add($"Type: {workItemType}");
        if (!string.IsNullOrWhiteSpace(title))        parts.Add($"Title: {title}");
        if (!string.IsNullOrWhiteSpace(description))  parts.Add($"Description: {description}");
        if (!string.IsNullOrWhiteSpace(reproSteps))   parts.Add($"Steps to Reproduce: {reproSteps}");
        if (!string.IsNullOrWhiteSpace(expected))     parts.Add($"Expected: {expected}");
        if (!string.IsNullOrWhiteSpace(actual))       parts.Add($"Actual: {actual}");

        if (parts.Count == 0) return (TriageStatus.Error, null);

        try
        {
            var payload = new
            {
                model = _model,
                max_tokens = 80,
                messages = new[]
                {
                    new
                    {
                        role = "system",
                        content = "You are a software bug triage assistant. Analyze the work item and return ONLY valid JSON with no markdown, no explanation. " +
                                  "Fields: priority (int 1-4, where 1=must fix immediately, 4=low), " +
                                  "severity (exactly one of: \"1 - Critical\", \"2 - High\", \"3 - Medium\", \"4 - Low\"), " +
                                  "estimatedSize (exactly one of: \"Small\", \"Medium\", \"Large\", \"XL\")."
                    },
                    new
                    {
                        role = "user",
                        content = string.Join("\n", parts)
                    }
                }
            };

            var json = JsonSerializer.Serialize(payload);
            var response = await _http.PostAsync("chat/completions",
                new StringContent(json, Encoding.UTF8, "application/json"), ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[Triage] OpenRouter request failed: {Status}", response.StatusCode);
                return (TriageStatus.Error, null);
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var text = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            if (string.IsNullOrWhiteSpace(text)) return (TriageStatus.Error, null);

            // Strip markdown fences if present
            text = text.Trim();
            if (text.StartsWith("```")) text = text[(text.IndexOf('\n') + 1)..];
            if (text.EndsWith("```")) text = text[..text.LastIndexOf("```")].Trim();

            var result = JsonSerializer.Deserialize<TriageResult>(text, _json);
            if (result == null) return (TriageStatus.Error, null);

            if (result.Priority is < 1 or > 4 ||
                !_validSeverities.Contains(result.Severity) ||
                !_validSizes.Contains(result.EstimatedSize))
            {
                _logger.LogWarning("[Triage] AI returned out-of-range values: {Text}", text);
                return (TriageStatus.Error, null);
            }

            return (TriageStatus.Ok, result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Triage] Triage failed");
            return (TriageStatus.Error, null);
        }
    }

    public static string SeverityShort(string severity)
    {
        var dash = severity.IndexOf(" - ");
        return dash >= 0 ? severity[(dash + 3)..] : severity;
    }
}
