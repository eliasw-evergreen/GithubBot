using System.Text.Json.Serialization;

namespace GithubBot.Models;

public class IssueComment
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("user")] public GitHubUser User { get; set; } = new();
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("html_url")] public string HtmlUrl { get; set; } = "";
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("line")] public int? Line { get; set; }
    [JsonPropertyName("original_line")] public int? OriginalLine { get; set; }
}
