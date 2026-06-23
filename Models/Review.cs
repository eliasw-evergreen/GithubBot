using System.Text.Json.Serialization;

namespace GithubBot.Models;

public class Review
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("user")] public GitHubUser User { get; set; } = new();
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("state")] public string State { get; set; } = "";
    [JsonPropertyName("html_url")] public string HtmlUrl { get; set; } = "";
    [JsonPropertyName("submitted_at")] public DateTime? SubmittedAt { get; set; }
}
