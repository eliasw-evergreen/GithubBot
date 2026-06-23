using System.Text.Json.Serialization;

namespace GithubBot.Models;

public class Branch
{
    [JsonPropertyName("ref")] public string Ref { get; set; } = "";
}

public class PullRequest
{
    [JsonPropertyName("node_id")] public string NodeId { get; set; } = "";
    [JsonPropertyName("number")] public int Number { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("html_url")] public string HtmlUrl { get; set; } = "";
    [JsonPropertyName("user")] public GitHubUser User { get; set; } = new();
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("head")] public Branch Head { get; set; } = new();
    [JsonPropertyName("base")] public Branch Base { get; set; } = new();
    [JsonPropertyName("additions")] public int? Additions { get; set; }
    [JsonPropertyName("deletions")] public int? Deletions { get; set; }
    [JsonPropertyName("changed_files")] public int? ChangedFiles { get; set; }
    [JsonPropertyName("merged")] public bool? Merged { get; set; }
    [JsonPropertyName("merged_by")] public GitHubUser? MergedBy { get; set; }
    [JsonPropertyName("assignee")] public GitHubUser? Assignee { get; set; }
    [JsonPropertyName("requested_reviewers")] public List<GitHubUser>? RequestedReviewers { get; set; }
}
